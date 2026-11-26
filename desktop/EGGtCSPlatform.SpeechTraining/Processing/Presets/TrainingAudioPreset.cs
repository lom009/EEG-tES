using System.Collections.Immutable;
using System.Text.Json;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Noise;
using EGGtCSPlatform.Audio.Dsp.Resampling;
using EGGtCSPlatform.Audio.Dsp.Reverb;
using EGGtCSPlatform.Audio.Dsp.TimeStretch;
using EGGtCSPlatform.Audio.Dsp.Vocoder;

namespace EGGtCSPlatform.SpeechTraining;

// Bindings are explicit; metadata consumers never infer a node's role from its name.
internal sealed record TrainingPreset(
    AudioPipelineDefinition Graph,
    ImmutableArray<TrainingNodeSnapshot> Nodes,
    string? TimingNode,
    string? NoiseNode,
    string? RirNode,
    SpeechTimeStretchMetadata? Timing,
    int NoiseOffset
);

internal static class TrainingAudioPreset
{
    public static TrainingPreset Build(TrainingAudioRequest request, RirResponse? response)
    {
        var settings = request.Settings;
        settings.Validate();
        var graph = new AudioPipelineBuilder();
        var descriptions = ImmutableArray.CreateBuilder<TrainingNodeSnapshot>();
        string? timingNode = null,
            noiseNode = null,
            rirNode = null;
        SpeechTimeStretchMetadata? timing = null;
        int offset = 0;
        AudioOutputRef Add<T>(
            AudioOutputRef input,
            string id,
            IAudioProcessor<T> processor,
            T options,
            object? snapshot = null,
            AudioOutputRef? noiseInput = null
        )
        {
            var inputs = new Dictionary<string, AudioOutputRef> { ["audio"] = input };
            if (noiseInput is not null)
                inputs.Add("noise", noiseInput);
            var branch = graph.Add(new AudioNodeDefinition<T>(id, processor, options, inputs));
            var node = branch.Build().Nodes[^1];
            descriptions.Add(
                new(
                    id,
                    processor.Identity,
                    JsonSerializer.SerializeToElement(snapshot ?? options),
                    node.Inputs
                )
            );
            return branch.Tap();
        }
        var playback = AudioOutputRef.Source;
        int frames = request.Source.Frames,
            rate = request.Source.SampleRate;
        if (
            settings.Mode == SpeechTrainingMode.AdaptiveRate
            || settings.Mode == SpeechTrainingMode.FixedRate && settings.SpeechRate != 0
        )
        {
            var count = request.Sentence.ScoringCharacters.Count;
            if (count == 0 || frames == 0)
                throw new ArgumentException("变速需要非空音频和有效字数。");
            double duration = (double)frames / rate,
                originalRate = count / duration;
            double multiplier =
                settings.Mode == SpeechTrainingMode.AdaptiveRate
                    ? request.SpeedMultiplier
                    : (double)settings.SpeechRate / originalRate;
            if (!double.IsFinite(multiplier) || multiplier <= 0)
                throw new ArgumentException("变速倍率无效。");
            var target = Math.Round(
                settings.Mode == SpeechTrainingMode.AdaptiveRate
                    ? frames / multiplier
                    : (double)rate * count / (double)settings.SpeechRate,
                MidpointRounding.AwayFromZero
            );
            if (
                !double.IsFinite(target)
                || target < 1
                || target > int.MaxValue / (2 * request.Source.Channels)
            )
                throw new ArgumentOutOfRangeException(nameof(request));
            timing = new(
                frames,
                rate,
                duration,
                originalRate,
                multiplier,
                originalRate * multiplier,
                (int)target,
                (double)count * rate / target
            );
            frames = (int)target;
            timingNode = "speed";
            playback = Add(
                playback,
                timingNode,
                new TimeStretchProcessor(),
                new TimeStretchOptions(frames)
            );
        }
        var reference = playback;
        AudioOutputRef? noiseSource = null;
        NoiseMixOptions Noise(int count)
        {
            if (count == 0)
                throw new InvalidDataException("目标语音没有有效能量。");
            var n = request.Noise ?? throw new ArgumentException("缺少噪声快照。");
            n.Validate();
            var resource = request.NoiseResource ?? throw new ArgumentException("缺少噪声资源。");
            if (n.Sha256 != resource.Sha256)
                throw new InvalidDataException("噪声资源与请求不一致。");
            offset = TrainingNoiseOffset.Calculate(n.Seed, request.Sentence.Id, count);
            if (noiseSource is null)
            {
                graph.Source("noise-source", resource.Audio);
                noiseSource = graph.Tap();
                var node = graph.Build().Nodes[^1];
                descriptions.Add(
                    new(
                        node.Id,
                        node.ProcessorIdentity,
                        JsonSerializer.SerializeToElement(
                            new AudioBufferSourceMetadata(
                                resource.Audio.SampleRate,
                                resource.Audio.Channels,
                                resource.Audio.Frames,
                                resource.Audio.ContentHash
                            )
                        ),
                        node.Inputs
                    )
                );
            }
            return new(offset, n.SnrDb, n.CalibrationRms);
        }
        if (settings.Mode is SpeechTrainingMode.FixedSnr or SpeechTrainingMode.AdaptiveSnr)
        {
            var n = Noise(frames);
            noiseNode = "noise";
            playback = Add(
                playback,
                noiseNode,
                new NoiseMixProcessor(),
                n,
                noiseInput: noiseSource
            );
        }
        if (settings.Mode == SpeechTrainingMode.FixedRir)
        {
            if (response is null)
                throw new ArgumentException("缺少预生成 RIR。");
            playback = Add(
                playback,
                "room-rate",
                new ResampleProcessor(),
                new ResampleOptions(16000)
            );
            frames =
                frames == 0
                    ? 0
                    : Math.Max(
                        1,
                        checked(
                            (int)Math.Round(frames * 16000d / rate, MidpointRounding.AwayFromZero)
                        )
                    );
            var n = request.Noise is null
                ? null
                : Noise(checked(frames + response.Effective.Length - 1));
            rirNode = "room";
            playback = Add(
                playback,
                rirNode,
                new ReverbProcessor(),
                new ReverbOptions(
                    response.Settings,
                    request.CalibrationRms,
                    n is null ? null : new ReverbNoiseOptions(n.OffsetFrames, n.SnrDb),
                    response
                ),
                new
                {
                    response.Settings,
                    request.CalibrationRms,
                    response.FullHash,
                    response.EffectiveHash,
                    Noise = n is null ? null : new ReverbNoiseOptions(n.OffsetFrames, n.SnrDb),
                },
                noiseInput: noiseSource
            );
            reference = new(rirNode, "dryReference");
        }
        if (settings.Vocoder is { } vocoder)
        {
            // Preserve the original PCM encoding boundaries, including the independent clean branch.
            playback = Add(
                playback,
                "playback-rate",
                new ResampleProcessor(),
                new ResampleOptions(16000)
            );
            reference = Add(
                reference,
                "reference-rate",
                new ResampleProcessor(),
                new ResampleOptions(16000)
            );
            playback = Add(playback, "vocoder", new VocoderProcessor(), vocoder);
        }
        graph
            .Output("playback", playback)
            .Output("envelopeSource", reference, alignWith: "playback");
        var envelopeStart = graph.Build().Nodes.Length;
        graph
            .Branch(reference)
            .Then("envelope", new EnvelopeProcessor(), settings.Envelope)
            .Output("envelope", alignWith: "playback");
        foreach (var node in graph.Build().Nodes.Skip(envelopeStart))
            descriptions.Add(
                new(
                    node.Id,
                    node.ProcessorIdentity,
                    JsonSerializer.SerializeToElement(node.Parameters, node.Parameters!.GetType()),
                    node.Inputs
                )
            );
        return new(
            graph.Build(),
            descriptions.ToImmutable(),
            timingNode,
            noiseNode,
            rirNode,
            timing,
            offset
        );
    }
}
