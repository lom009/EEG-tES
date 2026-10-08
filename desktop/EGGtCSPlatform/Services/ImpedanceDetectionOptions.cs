using System;

namespace EGGtCSPlatform.Services;

public sealed class ImpedanceDetectionOptions
{
    public const string SectionName = "ImpedanceDetection";

    public bool AutoStopStimulationWhenPassed { get; set; } = true;

    public bool AutoStopEegWhenPassed { get; set; } = true;

    public TimeSpan AutoStopWhenNotPassedTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool AllowConfirmationWhenFailed { get; set; } = true;

    public bool IsValid() => AutoStopWhenNotPassedTimeout > TimeSpan.Zero;
}
