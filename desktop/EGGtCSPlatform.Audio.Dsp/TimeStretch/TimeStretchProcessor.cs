using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.TimeStretch;

public sealed record TimeStretchOptions(int TargetFrames);

public sealed record TimeStretchMetadata(int InputFrames, int OutputFrames, int SampleRate);

public sealed class TimeStretchProcessor : SingleInputAudioProcessor<TimeStretchOptions>
{
    public override string Identity => "solafs-1";

    protected override AudioSignalFormat Describe(
        AudioSignalFormat input,
        TimeStretchOptions options,
        string nodeId
    )
    {
        if (options.TargetFrames < 1 || options.TargetFrames > int.MaxValue / (input.Channels * 2))
            throw new ArgumentOutOfRangeException(nameof(options));
        return input with { TimeDomain = $"{input.TimeDomain}/{nodeId}" };
    }

    protected override AudioProcessResult Process(
        AudioSignal input,
        TimeStretchOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        if (input.Timeline.StartSeconds != 0 || !input.Timeline.Regions.IsEmpty)
            throw new ArgumentException("不能在没有精确时间映射时变速带有偏移或零区间的音频。");
        return AudioProcessResult.Single(
            new(
                Solafs.Stretch(input.Audio, options.TargetFrames, token),
                input.Timeline with
                {
                    TimeDomain = $"{input.Timeline.TimeDomain}/{context.NodeId}",
                }
            ),
            new TimeStretchMetadata(
                input.Audio.Frames,
                options.TargetFrames,
                input.Audio.SampleRate
            )
        );
    }
}
