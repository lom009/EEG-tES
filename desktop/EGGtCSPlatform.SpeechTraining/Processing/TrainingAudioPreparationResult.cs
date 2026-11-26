using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Dsp.Noise;
using EGGtCSPlatform.Audio.Dsp.Reverb;

namespace EGGtCSPlatform.SpeechTraining;

public sealed record TrainingAudioPreparationResult(
    TrainingAudioRequest Request,
    AudioPipelineDefinition Pipeline,
    AudioPipelineResult Graph,
    TrainingAudioSnapshot Snapshot,
    RirResponse? RirResponse = null
)
{
    public AudioBuffer Playback => Graph.Outputs["playback"].Audio;
    public FloatBuffer Envelope => Graph.GetFloat("envelope");
    public AudioSignal EnvelopeSource => Graph.Outputs["envelopeSource"];
    public SpeechTimeStretchMetadata? TimeStretch => Snapshot.TimeStretch;
    public SpeechNoiseMetadata? Noise => Snapshot.Noise;
    public SpeechRirMetadata? Rir => Snapshot.Rir;

    public void ValidateRequest()
    {
        Validate();
        Request.Validate();
        var expected = TrainingAudioPreset.Build(Request, RirResponse);
        if (
            !Pipeline.EquivalentTo(expected.Graph)
            || Snapshot.Settings != Request.Settings
            || Snapshot.SentenceId != Request.Sentence.Id
            || Snapshot.Seed != Request.Seed
            || Snapshot.TimeStretch != expected.Timing
        )
            throw new InvalidOperationException("音频元数据与本次请求不一致。");
        var formats = new AudioPipelineExecutor().Validate(Pipeline, Request.Source);
        if (
            Snapshot.OutputFormats is null
            || Snapshot.OutputFormats.Count != Pipeline.Outputs.Count
            || Snapshot.Nodes.Length != expected.Nodes.Length
        )
            throw new InvalidOperationException("训练快照缺少信号类型或节点记录。");
        foreach (var (actual, declared) in Snapshot.Nodes.Zip(expected.Nodes))
        {
            if (
                actual.Id != declared.Id
                || actual.ProcessorIdentity != declared.ProcessorIdentity
                || !System.Text.Json.JsonElement.DeepEquals(actual.Parameters, declared.Parameters)
                || actual.Inputs.Count != declared.Inputs.Count
                || actual.Inputs.Any(p =>
                    !declared.Inputs.TryGetValue(p.Key, out var value) || value != p.Value
                )
                || actual.OutputFormats is null
                || actual.OutputFormats.Count != formats.Count(p => p.Key.NodeId == actual.Id)
                || actual.OutputFormats.Any(p =>
                    !formats.TryGetValue(new(actual.Id, p.Key), out var value) || value != p.Value
                )
            )
                throw new InvalidOperationException($"训练快照节点 {actual.Id} 与预设不一致。");
        }
        if (
            Graph.Outputs.Count != Pipeline.Outputs.Count
            || Graph.Records.Length != Pipeline.Nodes.Length
            || Graph
                .Records.Zip(Pipeline.Nodes)
                .Any(pair =>
                    pair.First.NodeId != pair.Second.Id
                    || pair.First.ProcessorIdentity != pair.Second.ProcessorIdentity
                )
        )
            throw new InvalidOperationException("图执行记录与预设不一致。");
        foreach (var (name, route) in Pipeline.Outputs)
        {
            var format = formats[route.Source];
            if (
                !Snapshot.OutputFormats.TryGetValue(name, out var recordedFormat)
                || recordedFormat != format
                || !Graph.Outputs.TryGetValue(name, out var signal)
                || signal.Buffer.SampleRate != format.SampleRate
                || signal.Buffer.Channels != format.Channels
                || signal.Buffer.SampleType != format.SampleType
                || signal.Timeline.TimeDomain != format.TimeDomain
            )
                throw new InvalidOperationException($"训练出口 {name} 格式与预设不一致。");
        }
        T Metadata<T>(string id) =>
            Graph.Records.Single(r => r.NodeId == id).Metadata is T metadata
                ? metadata
                : throw new InvalidOperationException($"节点 {id} 缺少数值元数据。");
        void ValidateNoiseInput(NoiseInputMetadata? input)
        {
            if (
                Request.NoiseResource is not { } resource
                || input != NoiseInputMetadata.FromBuffer(resource.Audio)
            )
                throw new InvalidOperationException("噪声节点输入与本次资源不一致。");
            var source = Metadata<AudioBufferSourceMetadata>("noise-source");
            if (
                source.ContentHash != resource.Audio.ContentHash
                || source.Frames != resource.Audio.Frames
                || source.SampleRate != resource.Audio.SampleRate
                || source.Channels != resource.Audio.Channels
            )
                throw new InvalidOperationException("噪声源节点记录不一致。");
        }
        if (expected.TimingNode is { } timingId)
        {
            var timing = Metadata<EGGtCSPlatform.Audio.Dsp.TimeStretch.TimeStretchMetadata>(
                timingId
            );
            if (
                timing.InputFrames != Request.Source.Frames
                || timing.OutputFrames != expected.Timing!.OutputFrames
                || timing.SampleRate != Request.Source.SampleRate
            )
                throw new InvalidOperationException("变速元数据与请求不一致。");
        }
        if (expected.NoiseNode is { } noiseId)
        {
            var mixed = Metadata<EGGtCSPlatform.Audio.Dsp.Noise.NoiseMixMetadata>(noiseId);
            ValidateNoiseInput(mixed.NoiseInput);
            if (
                Noise is not { } n
                || n.Frames != Request.Source.Frames
                || n.SampleRate != Request.Source.SampleRate
                || n.Frames != mixed.Frames
                || n.SampleRate != mixed.SampleRate
                || n.OffsetFrames != mixed.OffsetFrames
                || n.NormalizationGain != mixed.NormalizationGain
                || n.PeakGain != mixed.PeakGain
                || n.OutputRms != mixed.OutputRms
            )
                throw new InvalidOperationException("混音元数据与请求不一致。");
        }
        if (Request.Noise is { } noise)
        {
            if (
                Noise is not { } actual
                || actual.Settings != noise
                || actual.OffsetFrames
                    != TrainingNoiseOffset.Calculate(noise.Seed, Request.Sentence.Id, actual.Frames)
            )
                throw new InvalidOperationException("SNR 音频元数据与本次请求不一致。");
        }
        else if (Noise is not null)
            throw new InvalidOperationException("意外的噪声元数据。");
        if (Request.Settings.Mode == SpeechTrainingMode.FixedRir)
        {
            var response =
                RirResponse ?? throw new InvalidOperationException("缺少 RIR 响应元数据。");
            var mixed = Metadata<ReverbMetadata>(expected.RirNode!);
            if (Request.Noise is not null)
                ValidateNoiseInput(mixed.NoiseInput);
            else if (mixed.NoiseInput is not null)
                throw new InvalidOperationException("意外的 RIR 噪声输入。");
            var sourceFrames =
                Request.Source.Frames == 0
                    ? 0
                    : Math.Max(
                        1,
                        checked(
                            (int)
                                Math.Round(
                                    Request.Source.Frames * 16000d / Request.Source.SampleRate,
                                    MidpointRounding.AwayFromZero
                                )
                        )
                    );
            if (
                Rir is not { } room
                || room.Configuration.Settings
                    != (Request.Settings.Room ?? TrainingRoomSettings.Default)
                || room.Configuration.CalibrationRms != Request.CalibrationRms
                || room.Configuration.Noise != Request.Noise
                || room.FullHash != response.FullHash
                || room.EffectiveHash != response.EffectiveHash
                || room.DirectFrame != response.DirectFrame
                || room.DivisionFrame != response.DivisionFrame
                || room.RirFrames != response.Effective.Length
                || room.EarlyCoefficientRms != response.EarlyCoefficientRms
                || room.CalibrationGain != Request.CalibrationRms / response.EarlyCoefficientRms
                || !double.IsFinite(room.PeakGain)
                || room.PeakGain <= 0
                || room.PeakGain > 1
                || room.OutputFrames != sourceFrames + response.Effective.Length - 1
                || room.OutputFrames != mixed.OutputFrames
                || room.CalibrationGain != mixed.CalibrationGain
                || room.PeakGain != mixed.PeakGain
                || room.NoiseGain != mixed.NoiseGain
                || room.EarlySpeechRms != mixed.EarlySpeechRms
                || room.Noise != Noise
                || (
                    Noise is { } rn
                    && (
                        rn.Frames != room.OutputFrames
                        || rn.SampleRate != 16000
                        || rn.NormalizationGain != 1
                        || rn.PeakGain != mixed.PeakGain
                        || rn.OutputRms != mixed.OutputRms
                    )
                )
            )
                throw new InvalidOperationException("RIR 音频元数据与本次请求不一致。");
        }
    }

    public void Validate()
    {
        if (
            !Graph.Outputs.ContainsKey("playback")
            || !Graph.Outputs.ContainsKey("envelopeSource")
            || !Graph.Outputs.ContainsKey("envelope")
        )
            throw new InvalidOperationException("训练图缺少播放或包络出口。");
        foreach (var signal in Graph.Outputs.Values)
            signal.Timeline.Validate();
        _ = Playback;
        var source = EnvelopeSource.GetAudio();
        if (
            Envelope.Channels != 1
            || Envelope.Frames != source.Frames
            || Envelope.SampleRate != source.SampleRate
            || Graph.Outputs["envelope"].Timeline.TimeDomain != EnvelopeSource.Timeline.TimeDomain
            || Graph.Outputs["envelope"].Timeline.StartSeconds
                != EnvelopeSource.Timeline.StartSeconds
            || !Graph
                .Outputs["envelope"]
                .Timeline.Regions.SequenceEqual(EnvelopeSource.Timeline.Regions)
        )
            throw new InvalidOperationException("包络与其来源不一致。");
        if (Graph.Outputs["playback"].Timeline.TimeDomain != EnvelopeSource.Timeline.TimeDomain)
            throw new InvalidOperationException("训练出口必须同步。");
    }
}
