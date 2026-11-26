using System.Collections.Immutable;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Dsp.Noise;
using EGGtCSPlatform.Audio.Dsp.Reverb;
using EGGtCSPlatform.Audio.Dsp.TimeStretch;

namespace EGGtCSPlatform.SpeechTraining;

public sealed class TrainingAudioPreparationService(
    ISpeechCorpusProvider corpus,
    ISpeechNoiseProvider? noiseProvider = null,
    IRirGenerator? rirGenerator = null
) : ITrainingAudioPreparationService
{
    private readonly ISpeechNoiseProvider _noise = noiseProvider ?? new FileSpeechNoiseProvider();
    private readonly IRirGenerator _rir = rirGenerator ?? new RirGenerator();
    private readonly AudioPipelineExecutor _executor = new();

    public TrainingAudioRequest CreateRequest(
        SpeechSentence sentence,
        TrainingAudioSettings settings,
        double speedMultiplier,
        int seed,
        SpeechNoiseMapping? noiseMapping,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        settings.Validate();
        var source = corpus.ReadAudioResource(sentence);
        var room = settings.Room ?? TrainingRoomSettings.Default;
        var needsNoise =
            settings.Mode is SpeechTrainingMode.FixedSnr or SpeechTrainingMode.AdaptiveSnr
            || settings.Mode == SpeechTrainingMode.FixedRir && room.AddNoise;
        var calibration =
            needsNoise || settings.Mode == SpeechTrainingMode.FixedRir ? _noise.CalibrationRms : 0;
        SpeechNoiseResource? resource = null;
        SpeechNoiseSettings? noise = null;
        if (needsNoise)
        {
            var kind =
                settings.Mode == SpeechTrainingMode.FixedRir ? room.NoiseKind : settings.NoiseKind;
            var name =
                noiseMapping?.File(kind) ?? throw new InvalidOperationException("缺少噪声配置。");
            resource = _noise.Read(name);
            noise = new(
                kind,
                name,
                resource.Sha256,
                settings.Mode == SpeechTrainingMode.FixedRir ? room.SnrDb : settings.SnrDb,
                seed,
                calibration
            );
        }
        token.ThrowIfCancellationRequested();
        return new(
            sentence,
            settings,
            source.Audio,
            speedMultiplier,
            seed,
            noise,
            resource,
            calibration,
            source.FileSha256,
            needsNoise || settings.Mode == SpeechTrainingMode.FixedRir
                ? _noise.CalibrationResource
                : null
        );
    }

    public TrainingAudioPreparationResult Prepare(
        TrainingAudioRequest request,
        TrainingAudioPreparationResult? cached = null,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        request.Validate();
        var acoustics = (request.Settings.Room ?? TrainingRoomSettings.Default).Acoustics;
        var response =
            request.Settings.Mode == SpeechTrainingMode.FixedRir
                ? cached?.RirResponse is { } previous && previous.Settings == acoustics
                    ? previous
                    : _rir.Generate(acoustics, token)
                : null;
        var preset = TrainingAudioPreset.Build(request, response);
        var formats = _executor.Validate(preset.Graph, request.Source);
        if (
            cached is not null
            && cached.Request == request
            && cached.Pipeline.EquivalentTo(preset.Graph)
        )
        {
            cached.ValidateRequest();
            return cached;
        }
        var graph = _executor
            .ExecuteAsync(preset.Graph, request.Source, token)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        T Metadata<T>(string id) =>
            graph.Records.Single(r => r.NodeId == id).Metadata is T m
                ? m
                : throw new InvalidOperationException($"处理节点 {id} 缺少必要记录。");
        if (preset.TimingNode is { } timingId)
        {
            var m = Metadata<TimeStretchMetadata>(timingId);
            if (
                m.OutputFrames != preset.Timing!.OutputFrames
                || m.InputFrames != request.Source.Frames
            )
                throw new InvalidOperationException("变速记录与请求不一致。");
        }
        SpeechNoiseMetadata? noise = null;
        SpeechRirMetadata? rir = null;
        if (preset.NoiseNode is { } noiseId)
        {
            var m = Metadata<NoiseMixMetadata>(noiseId);
            noise = new(
                request.Noise!,
                m.OffsetFrames,
                m.SampleRate,
                m.Frames,
                m.NormalizationGain,
                m.PeakGain,
                m.OutputRms
            );
        }
        if (preset.RirNode is { } rirId)
        {
            var m = Metadata<ReverbMetadata>(rirId);
            if (request.Noise is { } n)
                noise = new(
                    n,
                    preset.NoiseOffset,
                    16000,
                    m.OutputFrames,
                    1,
                    m.PeakGain,
                    m.OutputRms
                );
            rir = new(
                new(
                    request.Settings.Room ?? TrainingRoomSettings.Default,
                    request.CalibrationRms,
                    request.Noise
                ),
                response!.FullHash,
                response.EffectiveHash,
                response.DirectFrame,
                response.DivisionFrame,
                response.Effective.Length,
                m.OutputFrames,
                response.EarlyCoefficientRms,
                m.CalibrationGain,
                m.PeakGain,
                m.EarlySpeechRms,
                m.NoiseGain,
                noise
            );
        }
        var resources = ImmutableArray.CreateBuilder<TrainingResourceSnapshot>();
        resources.Add(
            new(
                "source",
                request.Sentence.AudioRelativePath,
                request.SourceFileSha256,
                request.Source.ContentHash
            )
        );
        if (request.NoiseResource is { } nr)
            resources.Add(new("noise", request.Noise!.FileName, nr.Sha256, nr.Audio.ContentHash));
        if (request.CalibrationResource is { } calibrationResource)
            resources.Add(calibrationResource);
        var snapshot = new TrainingAudioSnapshot(
            request.Settings,
            request.Sentence.Id,
            request.Seed,
            preset.Timing?.Multiplier ?? 1,
            [
                .. preset.Nodes.Select(node =>
                    node with
                    {
                        OutputFormats = formats
                            .Where(p => p.Key.NodeId == node.Id)
                            .ToImmutableDictionary(p => p.Key.Port, p => p.Value),
                    }
                ),
            ],
            preset.Graph.Outputs,
            resources.ToImmutable(),
            graph.Outputs.ToImmutableDictionary(p => p.Key, p => p.Value.Timeline),
            preset.Timing,
            noise,
            rir,
            [
                .. graph.Records.Select(record => new TrainingExecutionSnapshot(
                    record.NodeId,
                    record.ProcessorIdentity,
                    System.Text.Json.JsonSerializer.SerializeToElement(
                        record.Metadata is ReverbMetadata reverb
                            ? (object)new { Rir = rir, reverb.NoiseInput }
                            : record.Metadata
                    )
                )),
            ],
            graph.Outputs.ToImmutableDictionary(
                p => p.Key,
                p => new EGGtCSPlatform.Audio.Core.Pipeline.AudioSignalFormat(
                    p.Value.Buffer.SampleRate,
                    p.Value.Buffer.Channels,
                    p.Value.Timeline.TimeDomain,
                    p.Value.Buffer.SampleType
                )
            )
        );
        var result = new TrainingAudioPreparationResult(
            request,
            preset.Graph,
            graph,
            snapshot,
            response
        );
        result.ValidateRequest();
        return result;
    }
}
