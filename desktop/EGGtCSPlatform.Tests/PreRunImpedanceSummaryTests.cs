using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class PreRunImpedanceSummaryTests
{
    [Fact]
    public void AveragesBandEstimatesAndKeepsStimulationSeparate()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PreRunImpedanceSnapshot(
            [
                new("F3", 1, ImpedanceBand.UpTo10KOhms),
                new("C3", 2, ImpedanceBand.UpTo20KOhms),
                new("P3", 3, ImpedanceBand.UpTo30KOhms),
                new("FCz", 4, ImpedanceBand.Above40KOhms),
                new("AFz", 5, ImpedanceBand.Above40KOhms),
            ],
            [new("Fz", 1, ImpedanceBand.Abnormal), new("CP4", 2, ImpedanceBand.Normal)],
            now,
            now
        );
        var summary = PreRunImpedanceSummaryCalculator.Calculate(
            Route(snapshot, ["F3", "C3", "P3", "FCz", "AFz"])
        );
        Assert.Equal(16d, summary.AcquisitionAverage);
        Assert.Equal(3, summary.AcquisitionValidCount);
        Assert.Equal(3, summary.AcquisitionTotalCount);
        Assert.Equal(1, summary.StimulationNormalCount);
        Assert.Equal(0, summary.StimulationAbnormalCount);
        Assert.Equal(0, summary.StimulationMissingCount);
        Assert.Contains("16.0 kΩ", summary.AcquisitionText);
        Assert.Contains("配置检测·估算平均", summary.AcquisitionText);
        Assert.Equal(now, summary.AcquisitionMeasuredAt);
    }

    [Fact]
    public void ExcludesMissingDisabledDuplicatesAndForeignSitesButIncludesHighImpedance()
    {
        var snapshot = new PreRunImpedanceSnapshot(
            [
                new("F3", 1, ImpedanceBand.UpTo10KOhms),
                new("C3", 1, ImpedanceBand.UpTo10KOhms),
                new("P3", 3, ImpedanceBand.Above40KOhms),
                new("F4", 4, ImpedanceBand.Disabled),
                new("C4", 5, null),
                new("O1", 6, ImpedanceBand.UpTo40KOhms),
            ],
            [new("CP4", 2, ImpedanceBand.Abnormal)],
            null,
            null
        );
        var summary = PreRunImpedanceSummaryCalculator.Calculate(
            Route(snapshot, ["F3", "C3", "P3", "F4", "C4"])
        );
        Assert.Equal(26.5, summary.AcquisitionAverage);
        Assert.Equal(2, summary.AcquisitionValidCount);
        Assert.Equal(4, summary.AcquisitionTotalCount);
        Assert.Equal(1, summary.StimulationAbnormalCount);
    }

    [Fact]
    public void NoSnapshotMeansUndetectedInsteadOfZero()
    {
        var summary = PreRunImpedanceSummaryCalculator.Calculate(Route(null, ["F3"]));
        Assert.Null(summary.AcquisitionAverage);
        Assert.Equal("采集：未检测", summary.AcquisitionText);
        Assert.Equal(1, summary.StimulationMissingCount);
        Assert.DoesNotContain("0.0", summary.AcquisitionText);
    }

    [Fact]
    public void AllInvalidAcquisitionBandsDoNotProduceAnAverage()
    {
        var snapshot = new PreRunImpedanceSnapshot(
            [
                new("F3", 1, ImpedanceBand.Disabled),
                new("C3", 2, null),
                new("P3", 3, ImpedanceBand.Abnormal),
            ],
            [],
            DateTimeOffset.UnixEpoch,
            null
        );
        var summary = PreRunImpedanceSummaryCalculator.Calculate(
            Route(snapshot, ["F3", "C3", "P3"])
        );
        Assert.Null(summary.AcquisitionAverage);
        Assert.Equal(0, summary.AcquisitionValidCount);
        Assert.Equal("采集：未检测", summary.AcquisitionText);
    }

    [Theory]
    [InlineData(8, ImpedanceBand.UpTo10KOhms, ImpedanceQuality.Excellent)]
    [InlineData(10, ImpedanceBand.UpTo10KOhms, ImpedanceQuality.Excellent)]
    [InlineData(20, ImpedanceBand.UpTo20KOhms, ImpedanceQuality.Good)]
    [InlineData(30, ImpedanceBand.UpTo30KOhms, ImpedanceQuality.Medium)]
    [InlineData(40, ImpedanceBand.UpTo40KOhms, ImpedanceQuality.Poor)]
    [InlineData(41, ImpedanceBand.Above40KOhms, ImpedanceQuality.Bad)]
    public void SnapshotBandsMatchDetectionPageLabels(
        double value,
        ImpedanceBand band,
        ImpedanceQuality quality
    )
    {
        Assert.Equal(band, PreRunImpedanceSummaryCalculator.AcquisitionBand(value));
        Assert.Equal(quality, ElectrodeSiteViewModel.FromImpedance(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidValuesDoNotBecomeBands(double? value)
    {
        Assert.Null(PreRunImpedanceSummaryCalculator.AcquisitionBand(value));
        Assert.Null(PreRunImpedanceSummaryCalculator.StimulationBand(value));
    }

    [Fact]
    public void SnapshotFreezesCollections()
    {
        var channels = new List<PreRunImpedanceChannel> { new("F3", 1, ImpedanceBand.UpTo10KOhms) };
        var snapshot = new PreRunImpedanceSnapshot(channels, [], DateTimeOffset.UnixEpoch, null);
        channels[0] = channels[0] with { Band = ImpedanceBand.Above40KOhms };
        channels.Clear();
        Assert.Equal(ImpedanceBand.UpTo10KOhms, Assert.Single(snapshot.Acquisition).Band);
    }

    private static ExperimentRunRouteData Route(
        PreRunImpedanceSnapshot? snapshot,
        string[] acquisition
    )
    {
        var source = ExperimentRunRouteDataDefaults.Create();
        return new(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            acquisition,
            "FCz",
            "AFz",
            500,
            preRunImpedance: snapshot
        );
    }
}
