using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.MainApp;

public sealed record ExperimentTimingTemplate(
    ExperimentRunMode Mode,
    double AcquisitionMilliseconds,
    double BlankingMilliseconds,
    double StimulationMilliseconds,
    double RecoveryMilliseconds,
    int CycleCount
);

public sealed record ExperimentConfigurationTemplate(
    StimulusConfigurationSnapshot StimulusConfiguration,
    IReadOnlyList<StimulusElectrodeAssignment> StimulusElectrodes,
    IReadOnlyList<string> AcquisitionChannels,
    string ReferenceChannel,
    string GroundChannel,
    int SampleRateHz,
    ExperimentTimingTemplate Timing,
    string SourceDisplayName,
    IReadOnlyList<EegPhysicalChannelMapping>? PhysicalChannels = null,
    [property:
        System.Text.Json.Serialization.JsonPropertyName("generationParameters"),
        System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        )
    ]
        SimulationProvenance? Simulation = null,
    ExperimentCreationMode CreationMode = ExperimentCreationMode.AcquisitionAndStimulation
);

public interface IExperimentConfigurationTransferContext
{
    void Set(ExperimentConfigurationTemplate template);
    ExperimentConfigurationTemplate? Take();
}

public sealed class ExperimentConfigurationTransferContext : IExperimentConfigurationTransferContext
{
    private readonly object _sync = new();
    private ExperimentConfigurationTemplate? _template;

    public void Set(ExperimentConfigurationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        lock (_sync)
            _template = template;
    }

    public ExperimentConfigurationTemplate? Take()
    {
        lock (_sync)
        {
            var result = _template;
            _template = null;
            return result;
        }
    }
}

public static class SingleStimulusConfiguration
{
    public static ExperimentConfigurationTemplate Snapshot(
        ExperimentRunRouteData route,
        TimeSpan duration
    )
    {
        var s = route.StimulusConfiguration;
        var stimulus = new StimulusConfigurationSnapshot(
            s.Kind,
            s.ArrayMode,
            s.Direction,
            s.ShamMode,
            s.RampSeconds,
            s.Frequency,
            s.DutyPercent,
            s.Targets.Select(t => new StimulusTargetSnapshot(
                    t.TargetId,
                    t.DisplayOrder,
                    t.PeakCurrent,
                    t.FixedActivePhysicalChannelId,
                    t.Channels.Select(c => new StimulationPhysicalChannelSnapshot(
                            c.PhysicalChannelId,
                            c.Role,
                            c.Current
                        ))
                        .ToArray()
                ))
                .ToArray(),
            s.Envelope
        );
        return new ExperimentConfigurationTemplate(
            stimulus,
            route.StimulusElectrodes,
            [],
            "",
            "",
            route.SampleRateHz,
            new ExperimentTimingTemplate(
                ExperimentRunMode.Manual,
                0,
                0,
                duration.TotalMilliseconds,
                0,
                1
            ),
            route.ExperimentId,
            CreationMode: ExperimentCreationMode.StimulusOnly
        );
    }
}
