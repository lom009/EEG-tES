using System.Collections.Immutable;

namespace EGGtCSPlatform.SpeechTraining;

public sealed record SpeechTimeStretchMetadata(
    int OriginalFrames,
    int SampleRate,
    double OriginalDurationSeconds,
    double OriginalSpeechRate,
    double Multiplier,
    double TargetSpeechRate,
    int OutputFrames,
    double ActualSpeechRate
);
