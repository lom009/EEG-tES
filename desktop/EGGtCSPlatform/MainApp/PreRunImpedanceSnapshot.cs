using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.MainApp;

public sealed record PreRunImpedanceChannel(
    string SiteId,
    int? PhysicalChannelId,
    ImpedanceBand? Band
);

/// <summary>Detection-page observations, never live run telemetry.</summary>
public sealed class PreRunImpedanceSnapshot
{
    public PreRunImpedanceSnapshot(
        IEnumerable<PreRunImpedanceChannel> acquisition,
        IEnumerable<PreRunImpedanceChannel> stimulation,
        DateTimeOffset? acquisitionMeasuredAt,
        DateTimeOffset? stimulationMeasuredAt
    )
    {
        Acquisition = Array.AsReadOnly(acquisition.ToArray());
        Stimulation = Array.AsReadOnly(stimulation.ToArray());
        AcquisitionMeasuredAt = acquisitionMeasuredAt;
        StimulationMeasuredAt = stimulationMeasuredAt;
    }

    public IReadOnlyList<PreRunImpedanceChannel> Acquisition { get; }
    public IReadOnlyList<PreRunImpedanceChannel> Stimulation { get; }
    public DateTimeOffset? AcquisitionMeasuredAt { get; }
    public DateTimeOffset? StimulationMeasuredAt { get; }
}
