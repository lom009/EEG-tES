using System;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.Controls;

internal readonly record struct SpeechTimelineMotion(double Progress, TimeSpan Remaining)
{
    public static SpeechTimelineMotion From(SpeechTimelineAnimation? timeline, double progress)
    {
        if (timeline is null || timeline.Duration <= TimeSpan.Zero)
            return new(double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0, TimeSpan.Zero);
        var position = timeline.Position;
        return new(position.Ticks / (double)timeline.Duration.Ticks, timeline.Duration - position);
    }
    public TimeSpan AnimationDuration => Remaining < TimeSpan.FromMilliseconds(1)
        ? TimeSpan.FromMilliseconds(1) : Remaining;
}
