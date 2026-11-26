using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.Noise;

public sealed record NoiseMixOptions(int OffsetFrames, double SnrDb, double CalibrationRms)
{
    public void Validate()
    {
        NoiseInput.ValidateOptions(OffsetFrames, SnrDb);
        if (!double.IsFinite(CalibrationRms) || CalibrationRms <= 0)
            throw new ArgumentException("噪声参数无效。");
    }
}

public sealed record NoiseMixMetadata(
    int OffsetFrames,
    double NormalizationGain,
    double PeakGain,
    double OutputRms,
    int Frames,
    int SampleRate,
    NoiseInputMetadata NoiseInput
);

public sealed class NoiseMixProcessor : IAudioProcessor<NoiseMixOptions>
{
    public string Identity => "calibrated-noise-mix-2";

    public IReadOnlyDictionary<string, AudioSignalFormat> Describe(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs,
        NoiseMixOptions options,
        string nodeId
    )
    {
        options.Validate();
        var audio = NoiseInput.ValidatePorts(inputs, true);
        return new Dictionary<string, AudioSignalFormat> { ["audio"] = audio };
    }

    public ValueTask<AudioProcessResult> ProcessAsync(
        IReadOnlyDictionary<string, AudioSignal> inputs,
        NoiseMixOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        // Also guard direct processor calls, not only execution through a validated graph.
        Describe(
            inputs.ToDictionary(
                p => p.Key,
                p => new AudioSignalFormat(
                    p.Value.Buffer.SampleRate,
                    p.Value.Buffer.Channels,
                    p.Value.Timeline.TimeDomain,
                    p.Value.Buffer.SampleType
                )
            ),
            options,
            context.NodeId
        );
        var input = inputs["audio"];
        var noise = NoiseInput.Match(
            inputs["noise"],
            input.Audio.Frames,
            input.Audio.SampleRate,
            input.Audio.Channels,
            options.OffsetFrames,
            token
        );
        var result = NoiseMixer.Mix(
            input.Audio,
            noise,
            options.SnrDb,
            options.CalibrationRms,
            token
        );
        return ValueTask.FromResult(
            AudioProcessResult.Single(
                input with
                {
                    Audio = result.Audio,
                },
                new NoiseMixMetadata(
                    options.OffsetFrames,
                    result.NormalizationGain,
                    result.PeakGain,
                    result.OutputRms,
                    input.Audio.Frames,
                    input.Audio.SampleRate,
                    NoiseInputMetadata.FromBuffer(inputs["noise"].Buffer, token)
                )
            )
        );
    }
}
