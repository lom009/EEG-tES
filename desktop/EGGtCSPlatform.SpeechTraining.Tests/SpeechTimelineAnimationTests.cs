using System.Diagnostics;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechTimelineAnimationTests
{
    [Fact]
    public void FramePositionsUseElapsedTimeWithoutWaitingForSessionTicks()
    {
        var animation = new SpeechTimelineAnimation(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.3), 0
        );
        foreach (var milliseconds in new[] { 0, 7, 16, 23, 100, 217, 299, 300, 500 })
        {
            var timestamp = (long)(milliseconds / 1000d * Stopwatch.Frequency);
            var expected = 1 + Math.Min(milliseconds, 300) / 1000d;
            Assert.Equal(expected, animation.GetPosition(timestamp).TotalSeconds, 6);
        }
    }
}
