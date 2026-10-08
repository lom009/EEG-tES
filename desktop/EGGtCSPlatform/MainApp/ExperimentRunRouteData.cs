using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.MainApp;

public sealed record HistoricalExperimentStageInterval(
    ExperimentRunStage Stage,
    int Cycle,
    double StartSeconds,
    double EndSeconds
);

public sealed record HistoricalExperimentRunContext(
    Guid RunId,
    string RawFilePath,
    ExperimentRunStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    IReadOnlyDictionary<int, string> PhysicalChannelNames,
    IReadOnlyList<HistoricalExperimentStageInterval> StageIntervals,
    EegPacketStatistics? PacketStatistics = null,
    double? LogicalTimelineEndSeconds = null,
    [property:
        System.Text.Json.Serialization.JsonPropertyName("generationParameters"),
        System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        )
    ]
        SimulationProvenance? Simulation = null
);

public sealed record ExperimentRerunRouteData(ExperimentRunRouteData RouteData);

public sealed record ExperimentStimulationChannelSnapshot(
    int PhysicalChannelId,
    StimulationChannelRole Role,
    double Current
);

public sealed record ExperimentStimulusTargetSnapshot(
    Guid TargetId,
    int DisplayOrder,
    double PeakCurrent,
    int FixedActivePhysicalChannelId,
    IReadOnlyList<ExperimentStimulationChannelSnapshot> Channels
);

public sealed record ExperimentStimulusConfigurationSnapshot(
    StimulusKind Kind,
    StimulusArrayMode ArrayMode,
    StimulusDirection Direction,
    ShamWaveformMode ShamMode,
    double RampSeconds,
    double Frequency,
    double DutyPercent,
    IReadOnlyList<ExperimentStimulusTargetSnapshot> Targets,
    EnvelopeParameters? Envelope = null
)
{
    public static ExperimentStimulusConfigurationSnapshot From(
        StimulusConfigurationSnapshot source,
        IReadOnlyList<StimulusElectrodeAssignment> assignments
    )
    {
        var targets = source
            .Targets.Select(target =>
            {
                var targetAssignments = assignments
                    .Where(assignment => assignment.TargetId == target.TargetId)
                    .ToArray();
                var channels =
                    target.Channels.Count > 0
                        ? target
                            .Channels.Select(channel => new ExperimentStimulationChannelSnapshot(
                                channel.PhysicalChannelId,
                                channel.Role,
                                channel.Current
                            ))
                            .ToArray()
                        : targetAssignments
                            .Select(assignment => new ExperimentStimulationChannelSnapshot(
                                assignment.PhysicalChannelId,
                                assignment.Role,
                                target.PeakCurrent
                            ))
                            .ToArray();
                var fixedChannel = channels.Single(channel =>
                    channel.Role == StimulationChannelRole.FixedActive
                );
                return new ExperimentStimulusTargetSnapshot(
                    target.TargetId,
                    target.DisplayOrder,
                    target.PeakCurrent,
                    fixedChannel.PhysicalChannelId,
                    Array.AsReadOnly(channels)
                );
            })
            .ToArray();
        return new ExperimentStimulusConfigurationSnapshot(
            source.Kind,
            source.ArrayMode,
            source.Direction,
            source.ShamMode,
            source.RampSeconds,
            source.Frequency,
            source.DutyPercent,
            Array.AsReadOnly(targets),
            source.Envelope
        );
    }

    public static ExperimentStimulusConfigurationSnapshot Freeze(
        ExperimentStimulusConfigurationSnapshot source
    ) =>
        new(
            source.Kind,
            source.ArrayMode,
            source.Direction,
            source.ShamMode,
            source.RampSeconds,
            source.Frequency,
            source.DutyPercent,
            Array.AsReadOnly(
                source
                    .Targets.Select(target => new ExperimentStimulusTargetSnapshot(
                        target.TargetId,
                        target.DisplayOrder,
                        target.PeakCurrent,
                        target.FixedActivePhysicalChannelId,
                        Array.AsReadOnly(
                            target.Channels.Select(channel => channel with { }).ToArray()
                        )
                    ))
                    .ToArray()
            ),
            source.Envelope
        );
}

public sealed record ExperimentRunRouteData
{
    public ExperimentRunRouteData(
        string experimentId,
        string subjectId,
        ExperimentStimulusConfigurationSnapshot stimulusConfiguration,
        IEnumerable<StimulusElectrodeAssignment> stimulusElectrodes,
        IEnumerable<string> acquisitionChannels,
        string referenceChannel,
        string groundChannel,
        int sampleRateHz,
        string deviceId = "simulator-default",
        long experimentDatabaseId = 0,
        DateTimeOffset? scheduledAt = null,
        string remarks = "",
        ExperimentTimingTemplate? importedTiming = null,
        HistoricalExperimentRunContext? historicalResult = null,
        IReadOnlyDictionary<int, string>? physicalChannelNames = null,
        PreRunImpedanceSnapshot? preRunImpedance = null,
        ExperimentCreationMode creationMode = ExperimentCreationMode.AcquisitionAndStimulation
    )
    {
        ExperimentId = experimentId;
        CreationMode = creationMode;
        if (creationMode == ExperimentCreationMode.AcquisitionOnly && historicalResult is null)
        {
            stimulusConfiguration = ExperimentStimulusConfigurationSnapshot.From(
                AcquisitionOnlyConfiguration.StimulusPlaceholder,
                []
            );
            stimulusElectrodes = [];
            if (importedTiming is not null)
                importedTiming = AcquisitionOnlyConfiguration.NormalizeTiming(importedTiming);
        }
        SubjectId = subjectId;
        StimulusConfiguration = ExperimentStimulusConfigurationSnapshot.Freeze(
            stimulusConfiguration
        );
        StimulusElectrodes = Array.AsReadOnly(stimulusElectrodes.ToArray());
        AcquisitionChannels = Array.AsReadOnly(acquisitionChannels.ToArray());
        ReferenceChannel = referenceChannel;
        GroundChannel = groundChannel;
        SampleRateHz = sampleRateHz;
        DeviceId = deviceId;
        ExperimentDatabaseId = experimentDatabaseId;
        ScheduledAt = scheduledAt;
        Remarks = remarks;
        ImportedTiming = importedTiming;
        HistoricalResult = historicalResult;
        PreRunImpedance = preRunImpedance;
        PhysicalChannelNames = physicalChannelNames is null
            ? null
            : new ReadOnlyDictionary<int, string>(
                new Dictionary<int, string>(physicalChannelNames)
            );
    }

    public string ExperimentId { get; }
    public ExperimentCreationMode CreationMode { get; }

    public string SubjectId { get; }

    public ExperimentStimulusConfigurationSnapshot StimulusConfiguration { get; }

    public IReadOnlyList<StimulusElectrodeAssignment> StimulusElectrodes { get; }

    public IReadOnlyList<string> AcquisitionChannels { get; }

    public string ReferenceChannel { get; }

    public string GroundChannel { get; }

    public int SampleRateHz { get; }

    public string DeviceId { get; }

    public long ExperimentDatabaseId { get; }

    public DateTimeOffset? ScheduledAt { get; }

    public string Remarks { get; }

    public ExperimentTimingTemplate? ImportedTiming { get; }

    public HistoricalExperimentRunContext? HistoricalResult { get; }

    public PreRunImpedanceSnapshot? PreRunImpedance { get; }

    public IReadOnlyDictionary<int, string>? PhysicalChannelNames { get; }
}

public static class ExperimentRunRouteDataDefaults
{
    public static ExperimentRunRouteData Create()
    {
        var targetId = Guid.NewGuid();
        var assignments = new[]
        {
            new StimulusElectrodeAssignment("Fz", targetId, 1, StimulationChannelRole.FixedActive),
            new StimulusElectrodeAssignment("CP4", targetId, 2, StimulationChannelRole.Selectable),
        };
        var source = new StimulusConfigurationSnapshot(
            StimulusKind.TDcs,
            StimulusArrayMode.DualChannel,
            StimulusDirection.Positive,
            ShamWaveformMode.Direct,
            7,
            40,
            79,
            [new StimulusTargetSnapshot(targetId, 1, 2d, null, [])]
        );
        return new ExperimentRunRouteData(
            "EXP-20260707-001",
            "PAT-00023",
            ExperimentStimulusConfigurationSnapshot.From(source, assignments),
            assignments,
            ["Fz", "CP4", "TP8", "FP2", "P3", "Cz"],
            "PCz",
            "AFz",
            500
        );
    }

    public static string GetPrimaryStimulusMode(
        ExperimentStimulusConfigurationSnapshot configuration
    ) =>
        configuration.Kind switch
        {
            StimulusKind.TDcs => "tDCS",
            StimulusKind.TAcs => "tACS",
            StimulusKind.TRns => "tRNS",
            StimulusKind.TPcs => "tPCS",
            StimulusKind.Sham => "Sham",
            StimulusKind.EnvelopeTAcs => "包络-tACS",
            _ => configuration.Kind.ToString(),
        };

    public static double GetTotalCurrent(ExperimentStimulusConfigurationSnapshot configuration) =>
        configuration.Targets.Sum(target => target.PeakCurrent);
}
