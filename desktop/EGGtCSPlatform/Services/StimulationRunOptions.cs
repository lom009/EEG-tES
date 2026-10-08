using System;

namespace EGGtCSPlatform.Services;

public sealed class StimulationRunOptions
{
    public const string SectionName = "StimulationRun";

    public TimeSpan ProgressPacketTimeout { get; set; } = TimeSpan.FromMilliseconds(2500);

    public TimeSpan CompletionEventGracePeriod { get; set; } = TimeSpan.FromSeconds(2);

    public bool IsValid() =>
        ProgressPacketTimeout > TimeSpan.Zero && CompletionEventGracePeriod > TimeSpan.Zero;
}
