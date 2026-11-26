using System.Diagnostics;

namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Immutable timing shared by the session and compositor animation.</summary>
public sealed class SpeechTimelineAnimation
{
    internal SpeechTimelineAnimation(TimeSpan start, TimeSpan duration, long timestamp)
    {
        Start = start;
        Duration = duration;
        Timestamp = timestamp;
    }

    public TimeSpan Start { get; }
    public TimeSpan Duration { get; }
    internal long Timestamp { get; }
    public TimeSpan Position => GetPosition(Stopwatch.GetTimestamp());

    internal TimeSpan GetPosition(long timestamp) =>
        TimeSpan.FromTicks(Math.Clamp(
            (Start + Stopwatch.GetElapsedTime(Timestamp, timestamp)).Ticks,
            Start.Ticks,
            Duration.Ticks
        ));
}
