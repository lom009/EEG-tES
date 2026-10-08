using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed record PreRunImpedanceSummary(
    double? AcquisitionAverage,
    int AcquisitionValidCount,
    int AcquisitionTotalCount,
    int StimulationNormalCount,
    int StimulationAbnormalCount,
    int StimulationMissingCount,
    DateTimeOffset? AcquisitionMeasuredAt,
    DateTimeOffset? StimulationMeasuredAt
)
{
    public string AcquisitionText =>
        AcquisitionAverage is { } average
            ? $"采集：{average:0.0} kΩ（配置检测·估算平均，{AcquisitionValidCount}/{AcquisitionTotalCount} 通道）"
            : "采集：未检测";
    public string StimulationText =>
        $"刺激：正常 {StimulationNormalCount} / 异常 {StimulationAbnormalCount} / 未检测 {StimulationMissingCount}";
    public string DetectionTimeText =>
        $"配置检测时间：采集 {FormatTime(AcquisitionMeasuredAt)}；刺激 {FormatTime(StimulationMeasuredAt)}";

    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("MM/dd HH:mm:ss") ?? "未检测";
}

public static class PreRunImpedanceSummaryCalculator
{
    public static ImpedanceBand? AcquisitionBand(double? value) =>
        value switch
        {
            null => null,
            double number when !double.IsFinite(number) || number < 0 => null,
            <= 10 => ImpedanceBand.UpTo10KOhms,
            <= 20 => ImpedanceBand.UpTo20KOhms,
            <= 30 => ImpedanceBand.UpTo30KOhms,
            <= 40 => ImpedanceBand.UpTo40KOhms,
            _ => ImpedanceBand.Above40KOhms,
        };

    public static ImpedanceBand? StimulationBand(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0
            ? number <= 10
                ? ImpedanceBand.Normal
                : ImpedanceBand.Abnormal
            : null;

    // These values represent detection bands, not precise impedance measurements.
    private static double? Representative(ImpedanceBand? band) =>
        band switch
        {
            ImpedanceBand.UpTo10KOhms => 8,
            ImpedanceBand.UpTo20KOhms => 15,
            ImpedanceBand.UpTo30KOhms => 25,
            ImpedanceBand.UpTo40KOhms => 35,
            ImpedanceBand.Above40KOhms => 45,
            _ => null,
        };

    public static PreRunImpedanceSummary Calculate(ExperimentRunRouteData route)
    {
        var snapshot = route.PreRunImpedance;
        var acquisition = route
            .AcquisitionChannels.Where(x =>
                !string.Equals(x, route.ReferenceChannel, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(x, route.GroundChannel, StringComparison.OrdinalIgnoreCase)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(site =>
                snapshot?.Acquisition.LastOrDefault(x =>
                    string.Equals(x.SiteId, site, StringComparison.OrdinalIgnoreCase)
                )
                ?? new PreRunImpedanceChannel(site, null, null)
            );
        var channels = UniqueChannels(acquisition).ToArray();
        var values = channels.Select(x => Representative(x.Band)).OfType<double>().ToArray();
        var stimulation = route
            .StimulusElectrodes.Where(x => x.Role == StimulationChannelRole.Selectable)
            .GroupBy(x => x.PhysicalChannelId)
            .Select(group =>
            {
                var assignment = group.First();
                return snapshot
                    ?.Stimulation.LastOrDefault(x =>
                        x.PhysicalChannelId == assignment.PhysicalChannelId
                        && string.Equals(
                            x.SiteId,
                            assignment.SiteId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    ?.Band;
            })
            .ToArray();
        return new(
            values.Length == 0 ? null : values.Average(),
            values.Length,
            channels.Length,
            stimulation.Count(x => x == ImpedanceBand.Normal),
            stimulation.Count(x => x == ImpedanceBand.Abnormal),
            stimulation.Count(x => x is not (ImpedanceBand.Normal or ImpedanceBand.Abnormal)),
            snapshot?.AcquisitionMeasuredAt,
            snapshot?.StimulationMeasuredAt
        );
    }

    private static IEnumerable<PreRunImpedanceChannel> UniqueChannels(
        IEnumerable<PreRunImpedanceChannel> channels
    ) =>
        channels
            .GroupBy(x =>
                x.PhysicalChannelId is > 0
                    ? $"CH{x.PhysicalChannelId}"
                    : $"SITE:{x.SiteId.ToUpperInvariant()}"
            )
            .Select(x => x.Last());
}
