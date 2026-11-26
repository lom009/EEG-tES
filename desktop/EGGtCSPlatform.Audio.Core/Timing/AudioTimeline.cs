using System.Collections.Immutable;

namespace EGGtCSPlatform.Audio.Core.Timing;

public readonly record struct AudioZeroRegion(double StartSeconds, double EndSeconds);

/// <summary>Placement relative to the selected playback, not an inferred time-warp.</summary>
public sealed record AudioTimeline(
    string TimeDomain = "source",
    double StartSeconds = 0,
    ImmutableArray<AudioZeroRegion> ZeroRegions = default
)
{
    public ImmutableArray<AudioZeroRegion> ZeroRegions { get; init; } =
        ZeroRegions.IsDefault ? [] : ZeroRegions;

    public ImmutableArray<AudioZeroRegion> Regions => ZeroRegions.IsDefault ? [] : ZeroRegions;

    public bool IsZero(double seconds) =>
        Regions.Any(r => seconds >= r.StartSeconds && seconds < r.EndSeconds);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TimeDomain) || !double.IsFinite(StartSeconds))
            throw new ArgumentException("无效的音频时间关系。");
        foreach (var region in Regions)
            if (
                !double.IsFinite(region.StartSeconds)
                || !double.IsFinite(region.EndSeconds)
                || region.StartSeconds < 0
                || region.EndSeconds <= region.StartSeconds
            )
                throw new ArgumentException("零输出区间无效。");
    }
}
