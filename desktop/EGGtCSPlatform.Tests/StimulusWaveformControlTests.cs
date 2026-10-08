using System;
using System.Linq;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class StimulusWaveformControlTests
{
    [Fact]
    public void TdcsUsesSixtySecondTimelineWithTenSecondTicks()
    {
        var descriptor = CreateTdcs(StimulusDirection.Positive, rampSeconds: 7d);

        var scale = StimulusWaveformControl.GetAxisScale(descriptor);
        var timeWindowMilliseconds = StimulusWaveformControl.GetTimeWindowMilliseconds(descriptor);
        var tickValues = Enumerable
            .Range(0, scale.TimeTickCount + 1)
            .Select(index => timeWindowMilliseconds / 1000d * index / scale.TimeTickCount)
            .ToArray();

        Assert.Equal(60_000d, timeWindowMilliseconds);
        Assert.Equal([0d, 10d, 20d, 30d, 40d, 50d, 60d], tickValues);
    }

    [Fact]
    public void TdcsSevenSecondRampUsesActualElapsedSeconds()
    {
        var descriptor = CreateTdcs(StimulusDirection.Positive, rampSeconds: 7d);

        Assert.Equal(0d, ValueAt(descriptor, 0d), 6);
        Assert.Equal(1d, ValueAt(descriptor, 3.5d), 6);
        Assert.Equal(2d, ValueAt(descriptor, 7d), 6);
        Assert.Equal(2d, ValueAt(descriptor, 53d), 6);
        Assert.Equal(1d, ValueAt(descriptor, 56.5d), 6);
        Assert.Equal(0d, ValueAt(descriptor, 60d), 6);
    }

    [Fact]
    public void NegativeTdcsMirrorsPositiveRampWithoutPositiveAxis()
    {
        var positive = CreateTdcs(StimulusDirection.Positive, rampSeconds: 7d);
        var negative = CreateTdcs(StimulusDirection.Negative, rampSeconds: 7d);
        var positiveScale = StimulusWaveformControl.GetAxisScale(positive);
        var negativeScale = StimulusWaveformControl.GetAxisScale(negative);

        Assert.Equal(new StimulusWaveformAxisScale(0d, 2.5d, 0.5d, 6), positiveScale);
        Assert.Equal(new StimulusWaveformAxisScale(-2.5d, 0d, 0.5d, 6), negativeScale);
        foreach (var elapsed in new[] { 0d, 3.5d, 7d, 30d, 53d, 56.5d, 60d })
            Assert.Equal(-ValueAt(positive, elapsed), ValueAt(negative, elapsed), 6);
    }

    [Fact]
    public void ZeroSecondTdcsRampIsInstantaneousAtTimelineBoundaries()
    {
        var descriptor = CreateTdcs(StimulusDirection.Positive, rampSeconds: 0d);

        Assert.Equal(0d, ValueAt(descriptor, 0d), 6);
        Assert.Equal(2d, ValueAt(descriptor, 0.001d), 6);
        Assert.Equal(2d, ValueAt(descriptor, 59.999d), 6);
        Assert.Equal(0d, ValueAt(descriptor, 60d), 6);
    }

    [Fact]
    public void ThirtySecondTdcsRampFormsTriangleAcrossEntireWindow()
    {
        var descriptor = CreateTdcs(StimulusDirection.Positive, rampSeconds: 30d);

        Assert.Equal(0d, ValueAt(descriptor, 0d), 6);
        Assert.Equal(1d, ValueAt(descriptor, 15d), 6);
        Assert.Equal(2d, ValueAt(descriptor, 30d), 6);
        Assert.Equal(1d, ValueAt(descriptor, 45d), 6);
        Assert.Equal(0d, ValueAt(descriptor, 60d), 6);
    }

    [Fact]
    public void ShamDirectUsesOneHundredFiftySecondTimeline()
    {
        var descriptor = CreateShamDirect(StimulusDirection.Positive, rampSeconds: 13d);
        var scale = StimulusWaveformControl.GetAxisScale(descriptor);
        var timeWindowMilliseconds = StimulusWaveformControl.GetTimeWindowMilliseconds(descriptor);
        var tickValues = Enumerable
            .Range(0, scale.TimeTickCount + 1)
            .Select(index => timeWindowMilliseconds / 1000d * index / scale.TimeTickCount)
            .ToArray();

        Assert.Equal(150_000d, timeWindowMilliseconds);
        Assert.Equal([0d, 15d, 30d, 45d, 60d, 75d, 90d, 105d, 120d, 135d, 150d], tickValues);
    }

    [Fact]
    public void ShamDirectThirteenSecondRampUsesActualElapsedSecondsAtBothEnds()
    {
        var descriptor = CreateShamDirect(StimulusDirection.Positive, rampSeconds: 13d);

        Assert.Equal(0d, ShamValueAt(descriptor, 0d), 6);
        Assert.Equal(1d, ShamValueAt(descriptor, 6.5d), 6);
        Assert.Equal(2d, ShamValueAt(descriptor, 13d), 6);
        Assert.Equal(1d, ShamValueAt(descriptor, 19.5d), 6);
        Assert.Equal(0d, ShamValueAt(descriptor, 26d), 6);
        Assert.Equal(0d, ShamValueAt(descriptor, 124d), 6);
        Assert.Equal(1d, ShamValueAt(descriptor, 130.5d), 6);
        Assert.Equal(2d, ShamValueAt(descriptor, 137d), 6);
        Assert.Equal(1d, ShamValueAt(descriptor, 143.5d), 6);
        Assert.Equal(0d, ShamValueAt(descriptor, 150d), 6);
    }

    [Fact]
    public void NegativeShamDirectMirrorsPositiveWithoutPositiveAxis()
    {
        var positive = CreateShamDirect(StimulusDirection.Positive, rampSeconds: 13d);
        var negative = CreateShamDirect(StimulusDirection.Negative, rampSeconds: 13d);

        Assert.Equal(
            new StimulusWaveformAxisScale(0d, 2.5d, 0.5d, 10),
            StimulusWaveformControl.GetAxisScale(positive)
        );
        Assert.Equal(
            new StimulusWaveformAxisScale(-2.5d, 0d, 0.5d, 10),
            StimulusWaveformControl.GetAxisScale(negative)
        );
        foreach (
            var elapsed in new[]
            {
                0d,
                6.5d,
                13d,
                19.5d,
                26d,
                75d,
                124d,
                130.5d,
                137d,
                143.5d,
                150d,
            }
        )
            Assert.Equal(-ShamValueAt(positive, elapsed), ShamValueAt(negative, elapsed), 6);
    }

    [Fact]
    public void ShamAlternatingUsesOneHundredSecondTimelineInSeconds()
    {
        var descriptor = CreateShamAlternating(frequency: 0.1d);
        var scale = StimulusWaveformControl.GetAxisScale(descriptor);
        var timeWindowMilliseconds = StimulusWaveformControl.GetTimeWindowMilliseconds(descriptor);
        var tickValues = Enumerable
            .Range(0, scale.TimeTickCount + 1)
            .Select(index => timeWindowMilliseconds / 1000d * index / scale.TimeTickCount)
            .ToArray();

        Assert.Equal(100_000d, timeWindowMilliseconds);
        Assert.False(StimulusWaveformControl.UsesMillisecondTimeAxis(descriptor));
        Assert.Equal([0d, 10d, 20d, 30d, 40d, 50d, 60d, 70d, 80d, 90d, 100d], tickValues);
    }

    [Fact]
    public void ShamAlternatingUsesReferenceShapeAroundSilentInterval()
    {
        var descriptor = CreateShamAlternating(frequency: 0.1d);

        Assert.Equal(2d, ShamAlternatingValueAt(descriptor, 2.5d), 6);
        Assert.Equal(-2d, ShamAlternatingValueAt(descriptor, 7.5d), 6);
        Assert.Equal(0d, ShamAlternatingValueAt(descriptor, 10d), 6);
        Assert.Equal(0d, ShamAlternatingValueAt(descriptor, 50d), 6);
        Assert.Equal(0d, ShamAlternatingValueAt(descriptor, 89.999d), 6);
        Assert.Equal(0d, ShamAlternatingValueAt(descriptor, 90d), 6);
        Assert.Equal(2d, ShamAlternatingValueAt(descriptor, 92.5d), 6);
        Assert.Equal(-2d, ShamAlternatingValueAt(descriptor, 97.5d), 6);
        Assert.Equal(0d, ShamAlternatingValueAt(descriptor, 100d), 6);
    }

    [Theory]
    [InlineData(0.1d, 100_000d, false)]
    [InlineData(40d, 250d, true)]
    [InlineData(100d, 100d, true)]
    public void ShamAlternatingFrequencyScalesTimelineAndUnit(
        double frequency,
        double expectedMilliseconds,
        bool expectedMillisecondsAxis
    )
    {
        var descriptor = CreateShamAlternating(frequency);

        Assert.Equal(
            expectedMilliseconds,
            StimulusWaveformControl.GetTimeWindowMilliseconds(descriptor),
            6
        );
        Assert.Equal(
            expectedMillisecondsAxis,
            StimulusWaveformControl.UsesMillisecondTimeAxis(descriptor)
        );
    }

    [Fact]
    public void ShamAlternatingNormalizedShapeDoesNotChangeWithFrequency()
    {
        var reference = CreateShamAlternating(frequency: 0.1d);
        var fortyHertz = CreateShamAlternating(frequency: 40d);
        var oneHundredHertz = CreateShamAlternating(frequency: 100d);

        foreach (
            var normalizedTime in new[] { 0d, 0.025d, 0.099d, 0.1d, 0.5d, 0.899d, 0.9d, 0.925d, 1d }
        )
        {
            var expected = ShamAlternatingValueAtNormalizedTime(reference, normalizedTime);
            Assert.Equal(
                expected,
                ShamAlternatingValueAtNormalizedTime(fortyHertz, normalizedTime),
                6
            );
            Assert.Equal(
                expected,
                ShamAlternatingValueAtNormalizedTime(oneHundredHertz, normalizedTime),
                6
            );
        }
    }

    [Fact]
    public void ShamAlternatingUsesBipolarHalfMilliampAxis()
    {
        var descriptor = CreateShamAlternating(frequency: 0.1d);

        Assert.Equal(
            new StimulusWaveformAxisScale(-2.5d, 2.5d, 0.5d, 10),
            StimulusWaveformControl.GetAxisScale(descriptor)
        );
    }

    [Fact]
    public void ShamAlternatingSamplingRemainsFixedAsFrequencyChanges()
    {
        Assert.Equal(
            28,
            StimulusWaveformControl.GetShamAlternatingSamplesPerActiveSegment(
                CreateShamAlternating(frequency: 0.1d)
            )
        );
        Assert.Equal(
            28,
            StimulusWaveformControl.GetShamAlternatingSamplesPerActiveSegment(
                CreateShamAlternating(frequency: 40d)
            )
        );
        Assert.Equal(
            28,
            StimulusWaveformControl.GetShamAlternatingSamplesPerActiveSegment(
                CreateShamAlternating(frequency: 100d)
            )
        );
    }

    [Fact]
    public void OtherModePreviewWindowsAndScalesRemainUnchanged()
    {
        var tacs = CreateDescriptor(StimulusKind.TAcs, StimulusDirection.Positive, frequency: 40d);
        var trns = CreateDescriptor(
            StimulusKind.TRns,
            StimulusDirection.Bidirectional,
            frequency: 40d
        );
        var tpcs = CreateDescriptor(StimulusKind.TPcs, StimulusDirection.Positive, frequency: 40d);
        var shamAlternating = CreateDescriptor(
            StimulusKind.Sham,
            StimulusDirection.Bidirectional,
            frequency: 40d,
            shamMode: ShamWaveformMode.Alternating
        );

        Assert.Equal(1_000d, StimulusWaveformControl.GetTimeWindowMilliseconds(tacs));
        Assert.Equal(10_000d, StimulusWaveformControl.GetTimeWindowMilliseconds(trns));
        Assert.Equal(100d, StimulusWaveformControl.GetTimeWindowMilliseconds(tpcs));
        Assert.Equal(250d, StimulusWaveformControl.GetTimeWindowMilliseconds(shamAlternating));
        Assert.Equal(
            new StimulusWaveformAxisScale(0d, 2.2d, 0.55d, 10),
            StimulusWaveformControl.GetAxisScale(tacs)
        );
        Assert.Equal(
            new StimulusWaveformAxisScale(-2.2d, 2.2d, 1.1d, 10),
            StimulusWaveformControl.GetAxisScale(trns)
        );
    }

    private static double ValueAt(WaveformDescriptor descriptor, double elapsedSeconds) =>
        StimulusWaveformControl.GetDirectCurrent(
            descriptor,
            elapsedSeconds,
            durationSeconds: 60d,
            amplitude: descriptor.Current
        );

    private static double ShamValueAt(WaveformDescriptor descriptor, double elapsedSeconds) =>
        StimulusWaveformControl.GetShamDirectCurrent(
            descriptor,
            elapsedSeconds,
            durationSeconds: 150d,
            amplitude: descriptor.Current
        );

    private static double ShamAlternatingValueAt(
        WaveformDescriptor descriptor,
        double elapsedSeconds
    ) =>
        StimulusWaveformControl.GetShamAlternatingCurrent(
            descriptor,
            elapsedSeconds,
            amplitude: descriptor.Current
        );

    private static double ShamAlternatingValueAtNormalizedTime(
        WaveformDescriptor descriptor,
        double normalizedTime
    ) =>
        ShamAlternatingValueAt(
            descriptor,
            StimulusWaveformControl.GetTimeWindowMilliseconds(descriptor) / 1000d * normalizedTime
        );

    private static WaveformDescriptor CreateTdcs(StimulusDirection direction, double rampSeconds) =>
        CreateDescriptor(StimulusKind.TDcs, direction, rampSeconds: rampSeconds);

    private static WaveformDescriptor CreateShamDirect(
        StimulusDirection direction,
        double rampSeconds
    ) => CreateDescriptor(StimulusKind.Sham, direction, rampSeconds: rampSeconds);

    private static WaveformDescriptor CreateShamAlternating(double frequency) =>
        CreateDescriptor(
            StimulusKind.Sham,
            StimulusDirection.Bidirectional,
            frequency: frequency,
            shamMode: ShamWaveformMode.Alternating
        );

    private static WaveformDescriptor CreateDescriptor(
        StimulusKind kind,
        StimulusDirection direction,
        double rampSeconds = 7d,
        double frequency = 40d,
        ShamWaveformMode shamMode = ShamWaveformMode.Direct
    ) =>
        new(
            kind,
            StimulusArrayMode.DualChannel,
            direction,
            shamMode,
            2d,
            rampSeconds,
            frequency,
            79d
        );
}
