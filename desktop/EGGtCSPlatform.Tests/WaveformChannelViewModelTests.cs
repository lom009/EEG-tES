using System;
using System.Linq;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class WaveformChannelViewModelTests
{
    [Theory]
    [InlineData(20d)]
    [InlineData(50d)]
    [InlineData(100d)]
    [InlineData(200d)]
    [InlineData(500d)]
    [InlineData(1000d)]
    [InlineData(2000d)]
    public void AmplitudeTicksCoverDisplayRangeAndAlignWithSevenGridLines(double range)
    {
        var ticks = Enumerable
            .Range(0, WaveformAmplitudeScale.IntervalCount + 1)
            .Select(index => WaveformAmplitudeScale.GetTickValue(range, index))
            .ToArray();

        Assert.Equal(7, ticks.Length);
        Assert.Equal(range, ticks[0], 8);
        Assert.Equal(0d, ticks[3]);
        Assert.Equal(-range, ticks[6], 8);
        for (var index = 0; index < ticks.Length; index++)
        {
            Assert.Equal(index * 16d, WaveformAmplitudeScale.ToY(ticks[index], range, 96d), 8);
            if (index > 0)
                Assert.Equal(2d * range / 6d, ticks[index - 1] - ticks[index], 8);
        }
    }

    [Theory]
    [InlineData(200d, "200")]
    [InlineData(133.333333d, "133")]
    [InlineData(66.666667d, "67")]
    [InlineData(-66.666667d, "-67")]
    [InlineData(-133.333333d, "-133")]
    [InlineData(-200d, "-200")]
    [InlineData(0d, "0")]
    [InlineData(-0.001d, "0")]
    [InlineData(12.5d, "13")]
    public void AmplitudeTickLabelsUseIntegersWithoutNegativeZero(double value, string expected)
    {
        expected = expected.Replace(
            ".",
            System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator
        );
        Assert.Equal(expected, WaveformAmplitudeScale.FormatTick(value));
    }

    [Theory]
    [InlineData(200d, 0d)]
    [InlineData(0d, 50d)]
    [InlineData(-200d, 100d)]
    [InlineData(300d, 0d)]
    [InlineData(-300d, 100d)]
    public void AmplitudeScaleMapsRangeToPlotBoundsAndClampsOverflow(double value, double expectedY)
    {
        Assert.Equal(expectedY, WaveformAmplitudeScale.ToY(value, 200d, 100d), 6);
    }

    [Theory]
    [InlineData(200d, true, "+200 μV")]
    [InlineData(200d, false, "-200 μV")]
    [InlineData(50d, true, "+50 μV")]
    public void AmplitudeScaleFormatsAxisBoundaries(double range, bool positive, string expected)
    {
        Assert.Equal(expected, WaveformAmplitudeScale.FormatBoundary(range, positive));
    }

    [Fact]
    public void InterpolationReturnsPointOnRenderedSegment()
    {
        TimedWaveformPoint[] samples = [new(1d, -20d, true), new(2d, 40d, false)];

        var found = WaveformSampleInterpolator.TryInterpolate(samples, 1.25d, out var coordinate);

        Assert.True(found);
        Assert.Equal(1.25d, coordinate.TimeSeconds, 6);
        Assert.Equal(-5d, coordinate.Value, 6);
    }

    [Fact]
    public void InterpolationDoesNotCrossSegmentGapOrFutureRange()
    {
        TimedWaveformPoint[] samples =
        [
            new(0d, 1d, true),
            new(1d, 2d, false),
            new(3d, 4d, true),
            new(4d, 5d, false),
        ];

        Assert.False(WaveformSampleInterpolator.TryInterpolate(samples, 2d, out _));
        Assert.False(WaveformSampleInterpolator.TryInterpolate(samples, 5d, out _));
        Assert.True(WaveformSampleInterpolator.TryInterpolate(samples, 3d, out var exact));
        Assert.Equal(4d, exact.Value, 6);
    }

    [Fact]
    public void VisibleSamplesConnectAcrossMissingBatchesInSameAcquisitionSegment()
    {
        var channel = CreateChannel(sampleRateHz: 10);
        channel.Append(
            new WaveformChannelBatch("Fz", 0d, 0.1d, [1d, 2d, 3d]),
            acquisitionSegmentId: 1
        );
        channel.Append(new WaveformChannelBatch("Fz", 2d, 0.1d, [4d, 5d]), acquisitionSegmentId: 1);

        channel.RefreshVisible(0d, 3d);

        Assert.Equal(5, channel.Samples.Length);
        Assert.Equal(0d, channel.Samples[0].TimeSeconds, 6);
        Assert.Equal(2d, channel.Samples[3].TimeSeconds, 6);
        Assert.True(channel.Samples[0].StartsNewSegment);
        Assert.False(channel.Samples[3].StartsNewSegment);
        Assert.Equal(2.2d, channel.HistoryDurationSeconds, 6);
    }

    [Fact]
    public void DifferentAcquisitionSegmentsRemainDisconnected()
    {
        var channel = CreateChannel(sampleRateHz: 10);
        channel.Append(new WaveformChannelBatch("Fz", 0d, 0.1d, [1d, 2d]), acquisitionSegmentId: 1);
        channel.Append(new WaveformChannelBatch("Fz", 4d, 0.1d, [3d, 4d]), acquisitionSegmentId: 2);

        channel.RefreshVisible(0d, 5d);

        Assert.True(channel.Samples[0].StartsNewSegment);
        Assert.True(channel.Samples[2].StartsNewSegment);
    }

    [Fact]
    public void HoverLookupInterpolatesRegularSamples()
    {
        var channel = CreateChannel(sampleRateHz: 10);
        channel.Append(
            new WaveformChannelBatch("Fz", 0d, 0.1d, [0d, 10d]),
            acquisitionSegmentId: 1
        );

        var found = channel.TryResolveHoverSample(0.05d, 0d, 1d, out var result);

        Assert.True(found);
        Assert.False(result.UsesNearestPoint);
        Assert.Equal(0.05d, result.SampleTimeSeconds, 6);
        Assert.Equal(5d, result.Value, 6);
    }

    [Fact]
    public void HoverLookupUsesEarlierNearestPointAcrossMissingSamples()
    {
        var channel = CreateChannel(sampleRateHz: 10);
        channel.Append(new WaveformChannelBatch("Fz", 0d, 0.1d, [1d, 2d]), acquisitionSegmentId: 1);
        channel.Append(
            new WaveformChannelBatch("Fz", 0.5d, 0.1d, [5d, 6d]),
            acquisitionSegmentId: 1
        );

        var found = channel.TryResolveHoverSample(0.3d, 0d, 1d, out var result);

        Assert.True(found);
        Assert.True(result.UsesNearestPoint);
        Assert.Equal(0.1d, result.SampleTimeSeconds, 6);
        Assert.Equal(2d, result.Value, 6);
    }

    [Fact]
    public void HoverLookupDoesNotUseSamplesOutsideAcquisitionSegment()
    {
        var channel = CreateChannel(sampleRateHz: 10);
        channel.Append(new WaveformChannelBatch("Fz", 0d, 0.1d, [1d, 2d]), acquisitionSegmentId: 1);
        channel.Append(new WaveformChannelBatch("Fz", 4d, 0.1d, [3d, 4d]), acquisitionSegmentId: 2);

        Assert.False(channel.TryResolveHoverSample(2d, 1d, 3d, out _));
        Assert.True(channel.TryResolveHoverSample(4.05d, 4d, 5d, out var result));
        Assert.False(result.UsesNearestPoint);
        Assert.Equal(3.5d, result.Value, 6);
    }

    [Fact]
    public void MissingBatchRangeKeepsConnectedBoundaryPointsAndFutureStaysBlank()
    {
        var channel = CreateChannel(sampleRateHz: 10);
        channel.Append(new WaveformChannelBatch("Fz", 0d, 0.1d, [1d, 2d, 3d]));
        channel.Append(new WaveformChannelBatch("Fz", 2d, 0.1d, [4d, 5d]));

        channel.RefreshVisible(0.5d, 1.5d);
        Assert.Equal(2, channel.Samples.Length);
        Assert.Equal(0.2d, channel.Samples[0].TimeSeconds, 6);
        Assert.Equal(2d, channel.Samples[1].TimeSeconds, 6);
        Assert.False(channel.Samples[1].StartsNewSegment);

        channel.RefreshVisible(3d, 5d);
        Assert.Empty(channel.Samples);
    }

    [Fact]
    public void OverviewKeepsDataOlderThanRecentHighResolutionBuffer()
    {
        var channel = CreateChannel(sampleRateHz: 1);
        for (var second = 0; second < 700; second++)
            channel.Append(new WaveformChannelBatch("Fz", second, 1d, [(double)second]));

        channel.RefreshVisible(0d, 5d);

        Assert.NotEmpty(channel.Samples);
        Assert.Equal(0d, channel.Samples[0].TimeSeconds, 6);
        Assert.All(channel.Samples, point => Assert.InRange(point.TimeSeconds, 0d, 5d));
    }

    [Fact]
    public void GrowingLiveDataDoesNotSuddenlyBecomeSparserAtPointLimit()
    {
        const int sampleRateHz = 500;
        var channel = CreateChannel(sampleRateHz);
        var firstTwoSeconds = Enumerable
            .Range(0, sampleRateHz * 2)
            .Select(index => Math.Sin(index * 0.05d))
            .ToArray();
        channel.Append(new WaveformChannelBatch("Fz", 0d, 1d / sampleRateHz, firstTwoSeconds));
        channel.RefreshVisible(0d, 30d);
        var countAtTwoSeconds = channel.Samples.Length;

        var nextSecond = Enumerable
            .Range(0, sampleRateHz)
            .Select(index => Math.Sin(index * 0.05d))
            .ToArray();
        channel.Append(new WaveformChannelBatch("Fz", 2d, 1d / sampleRateHz, nextSecond));
        channel.RefreshVisible(0d, 30d);

        Assert.True(countAtTwoSeconds > 0);
        Assert.True(channel.Samples.Length > countAtTwoSeconds);
    }

    [Fact]
    public void LargeWindowPanningKeepsOverlappingEnvelopePointsStable()
    {
        const int sampleRateHz = 500;
        var channel = CreateChannel(sampleRateHz);
        var samples = Enumerable
            .Range(0, sampleRateHz * 80)
            .Select(index =>
            {
                var time = index / (double)sampleRateHz;
                return 30d * Math.Sin(2d * Math.PI * 8d * time)
                    + 8d * Math.Sin(2d * Math.PI * 0.7d * time);
            })
            .ToArray();
        channel.Append(new WaveformChannelBatch("Fz", 0d, 1d / sampleRateHz, samples));

        channel.RefreshVisible(5d, 65d, maximumPoints: 120);
        var firstWindowOverlap = channel
            .Samples.Where(point => point.TimeSeconds is >= 10d and <= 60d)
            .Select(point => (point.TimeSeconds, point.Value))
            .ToArray();

        channel.RefreshVisible(5.5d, 65.5d, maximumPoints: 120);
        var shiftedWindowOverlap = channel
            .Samples.Where(point => point.TimeSeconds is >= 10d and <= 60d)
            .Select(point => (point.TimeSeconds, point.Value))
            .ToArray();

        Assert.NotEmpty(firstWindowOverlap);
        Assert.Equal(firstWindowOverlap, shiftedWindowOverlap);
    }

    [Fact]
    public void VisibleWindowOverrideDoesNotStopLiveHistoryAndIsAtomicallyReplaceable()
    {
        var channel = CreateChannel(sampleRateHz: 10);
        channel.Append(new WaveformChannelBatch("Fz", 0d, 0.1d, [1d, 2d, 3d]));
        channel.ReplaceVisibleWindow([
            new TimedWaveformPoint(20d, 100d, true),
            new TimedWaveformPoint(20.1d, 101d, false),
        ]);

        channel.ClearLiveHistory();
        Assert.Equal(new[] { 100d, 101d }, channel.Samples.Select(point => point.Value));
        channel.Append(new WaveformChannelBatch("Fz", 1d, 0.1d, [4d, 5d]));
        channel.RefreshVisible(0d, 2d);
        Assert.Equal(new[] { 100d, 101d }, channel.Samples.Select(point => point.Value));

        channel.ClearVisibleWindowOverride();
        channel.RefreshVisible(0d, 2d);
        Assert.Contains(channel.Samples, point => point.Value == 5d);
        Assert.DoesNotContain(channel.Samples, point => point.Value == 100d);
    }

    private static WaveformChannelViewModel CreateChannel(int sampleRateHz) =>
        new("Fz", "#398BFF", "#F7FBFF", sampleRateHz, false);
}
