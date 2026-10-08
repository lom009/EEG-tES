namespace EGGtCSPlatform.Services;

public sealed class ExperimentRunTimingOptions
{
    public const string SectionName = "ExperimentRunTiming";

    public int DurationStepMilliseconds { get; set; } = 1000;

    public int DurationMinimumMilliseconds { get; set; } = 1000;

    public int DurationMaximumMilliseconds { get; set; } = 65535000;

    public int BlankingDurationMilliseconds { get; set; } = 1000;

    public int RecoveryDurationMilliseconds { get; set; } = 1000;

    public int WaveformRefreshRateFps { get; set; } = 30;

    public bool IsValid() =>
        DurationStepMilliseconds > 0
        && DurationMinimumMilliseconds > 0
        && DurationMinimumMilliseconds % DurationStepMilliseconds == 0
        && DurationMaximumMilliseconds >= DurationStepMilliseconds
        && DurationMinimumMilliseconds <= DurationMaximumMilliseconds
        && DurationMaximumMilliseconds % DurationStepMilliseconds == 0
        && BlankingDurationMilliseconds > 0
        && RecoveryDurationMilliseconds > 0
        && BlankingDurationMilliseconds >= DurationMinimumMilliseconds
        && RecoveryDurationMilliseconds >= DurationMinimumMilliseconds
        && BlankingDurationMilliseconds <= DurationMaximumMilliseconds
        && RecoveryDurationMilliseconds <= DurationMaximumMilliseconds
        && BlankingDurationMilliseconds % DurationStepMilliseconds == 0
        && RecoveryDurationMilliseconds % DurationStepMilliseconds == 0
        && WaveformRefreshRateFps is >= 1 and <= 120;
}
