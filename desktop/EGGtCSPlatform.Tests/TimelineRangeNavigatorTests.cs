using EGGtCSPlatform.Controls;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class TimelineRangeNavigatorTests
{
    [Theory]
    [InlineData(5, "5s")]
    [InlineData(60, "1mins")]
    [InlineData(90, "1mins 30s")]
    [InlineData(3665.5, "1h 1mins 5.5s")]
    public void TimelineLabelsAlwaysIncludeTheirTimeUnits(double seconds, string expected)
    {
        Assert.Equal(expected, ExperimentTimelineFormatter.Format(seconds));
    }

    [Fact]
    public void RangeValuesAreClampedAndRespectMinimumSpan()
    {
        var navigator = new TimelineRangeNavigator
        {
            Minimum = 0,
            Maximum = 100,
            MinimumSpan = 10,
            ViewStart = 20,
            ViewEnd = 80,
            LiveValue = 50,
        };

        navigator.ViewStart = 98;
        Assert.Equal(90, navigator.ViewStart);
        Assert.Equal(100, navigator.ViewEnd);

        navigator.ViewEnd = -10;
        Assert.Equal(100, navigator.ViewEnd);
        Assert.True(navigator.ViewEnd - navigator.ViewStart >= navigator.MinimumSpan);
    }

    [Fact]
    public void InvalidMaximumIsRaisedToFitMinimumSpan()
    {
        var navigator = new TimelineRangeNavigator
        {
            Minimum = 5,
            Maximum = 5,
            MinimumSpan = 2,
        };

        Assert.Equal(7, navigator.Maximum);
        Assert.Equal(5, navigator.ViewStart);
        Assert.Equal(7, navigator.ViewEnd);
    }

    [Fact]
    public void NavigatorExposesLiveValueWithoutDrawingOrClampingItIntoSelection()
    {
        var navigator = new TimelineRangeNavigator
        {
            Minimum = 0,
            Maximum = 30,
            ViewStart = 0,
            ViewEnd = 5,
            LiveValue = 18,
            IsFollowingLatest = false,
        };

        Assert.Equal(18, navigator.LiveValue);
        Assert.False(navigator.IsFollowingLatest);
        Assert.Equal(0, navigator.ViewStart);
        Assert.Equal(5, navigator.ViewEnd);
    }

    [Theory]
    [InlineData(20, 50, 15, 35, 65)]
    [InlineData(20, 50, -40, 0, 30)]
    [InlineData(70, 100, 40, 70, 100)]
    public void PanningWindowPreservesSpanAndClampsToTimeline(
        double viewStart,
        double viewEnd,
        double delta,
        double expectedStart,
        double expectedEnd
    )
    {
        var range = TimelineRangeMath.PanWindow(0, 100, viewStart, viewEnd, delta);

        Assert.Equal(expectedStart, range.Start);
        Assert.Equal(expectedEnd, range.End);
        Assert.Equal(viewEnd - viewStart, range.End - range.Start);
    }

    [Theory]
    [InlineData(5, 0.5)]
    [InlineData(10, 1)]
    [InlineData(30, 3)]
    [InlineData(60, 6)]
    public void PointerPanningScalesWithCurrentVisibleTimeRange(
        double visibleSeconds,
        double expectedDelta
    )
    {
        var delta = TimelineRangeMath.ScalePointerDelta(
            pointerDelta: 10,
            viewportWidth: 100,
            viewStart: 20,
            viewEnd: 20 + visibleSeconds
        );

        Assert.Equal(expectedDelta, delta, 6);
    }

    [Fact]
    public void BottomNavigatorPanningScalesToFullTimeline()
    {
        const double timelineSeconds = 100;
        var delta = TimelineRangeMath.ScalePointerDelta(
            pointerDelta: 10,
            viewportWidth: 100,
            viewStart: 0,
            viewEnd: timelineSeconds
        );

        Assert.Equal(10, delta, 6);
    }

    [Fact]
    public void StimulationSpanIsMappedIntoTheCurrentNavigatorWindow()
    {
        var visible = TimelineViewportMath.TryGetVisibleSpan(
            intervalStart: 70,
            intervalEnd: 130,
            viewStart: 68,
            viewEnd: 73,
            out var span,
            revealThrough: 72
        );

        Assert.True(visible);
        Assert.Equal(0.4, span.StartFraction, 6);
        Assert.Equal(0.8, span.EndFraction, 6);
    }

    [Fact]
    public void FutureStimulationIsNotRevealedBeforeItsStageStarts()
    {
        var visible = TimelineViewportMath.TryGetVisibleSpan(
            intervalStart: 70,
            intervalEnd: 130,
            viewStart: 68,
            viewEnd: 73,
            out _,
            revealThrough: 69.9
        );

        Assert.False(visible);
    }

    [Fact]
    public void CompletedStimulationFillsOnlyItsVisibleViewportPortion()
    {
        var visible = TimelineViewportMath.TryGetVisibleSpan(
            intervalStart: 70,
            intervalEnd: 130,
            viewStart: 68,
            viewEnd: 73,
            out var span,
            revealThrough: 140
        );

        Assert.True(visible);
        Assert.Equal(0.4, span.StartFraction, 6);
        Assert.Equal(1, span.EndFraction, 6);
    }
}
