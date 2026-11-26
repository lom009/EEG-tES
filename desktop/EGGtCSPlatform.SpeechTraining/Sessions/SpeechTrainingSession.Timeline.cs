using System.Diagnostics;

namespace EGGtCSPlatform.SpeechTraining;

public sealed partial class SpeechTrainingSession
{
    /// <summary>Display-only changes; does not invalidate training state or waveform data.</summary>
    public event EventHandler? TimelineChanged;
    public SpeechTimelineAnimation? TimelineAnimation { get; private set; }

    private void StopTimeline()
    {
        if (TimelineAnimation is { } animation)
            TimelinePosition = animation.Position;
        TimelineAnimation = null;
    }

    private void StartTimeline()
    {
        var end = TimeSpan.FromSeconds(StimulusPlan?.DisplayDurationSeconds ?? Duration.TotalSeconds);
        if (TimelineAnimation is not null || TimelinePosition >= end)
            return;
        // The compositor owns playback of this trajectory. No background update
        // task is needed; explicit stop/replay replaces it and freezes/resets position.
        TimelineAnimation = new(TimeSpan.Zero, end, Stopwatch.GetTimestamp());
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }
}
