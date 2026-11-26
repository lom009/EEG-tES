using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.Resampling;

public sealed record ResampleOptions(int SampleRate);

public sealed class ResampleProcessor : SingleInputAudioProcessor<ResampleOptions>
{
    public override string Identity => "sinc-resample-1";

    protected override AudioSignalFormat Describe(
        AudioSignalFormat input,
        ResampleOptions options,
        string nodeId
    )
    {
        if (options.SampleRate is < 8000 or > 192000)
            throw new ArgumentException("无效的重采样采样率。");
        return input with { SampleRate = options.SampleRate };
    }

    protected override AudioProcessResult Process(
        AudioSignal input,
        ResampleOptions options,
        AudioProcessingContext context,
        CancellationToken token
    ) =>
        AudioProcessResult.Single(
            input with
            {
                Audio = SincResampler.Resample(input.Audio, options.SampleRate, token),
            }
        );
}
