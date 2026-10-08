using System;
using System.Collections.Generic;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Persistence;

public enum ExperimentStatus
{
    Draft,
    Running,
    Completed,
    Interrupted,
    Failed,
}

public enum ExperimentRunStatus
{
    Preparing,
    Running,
    Completed,
    InterruptedByUser,
    InterruptedByExit,
    Failed,
    RecoveredAfterCrash,
}

public enum ExperimentEventStatus
{
    Starting,
    Running,
    Completed,
    Interrupted,
    StartFailed,
}

public enum EventEndTimeAccuracy
{
    Exact,
    LastDurableHeartbeat,
}

public enum ExperimentIncidentKind
{
    UserEmergencyStop,
    ApplicationExit,
    DeviceFailure,
    DataTimeout,
    FileFailure,
    UnhandledException,
    CrashRecovery,
    PersistenceFailure,
}

public enum ExperimentEventKind
{
    Acquisition,
    Blanking,
    Stimulation,
    Recovery,
}

public enum ElectrodeUsage
{
    None,
    Acquisition,
    Reference,
    Ground,
}

public enum ImpedanceMeasurementKind
{
    Stimulation,
    Acquisition,
}

public enum EegFileFormat
{
    Staging,
    EdfPlus,
    Csv,
    ExperimentPackage,
    RecordingMetadata,
}

public enum EegFileLocation
{
    Temporary,
    Exported,
    Managed,
}

public enum EegFileState
{
    Writing,
    Available,
    Cleaned,
    Failed,
}

public sealed class OperatorEntity
{
    public long Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public required byte[] PasswordSalt { get; set; }
    public required byte[] PasswordHash { get; set; }
    public int PasswordIterations { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public List<ExperimentEntity> Experiments { get; set; } = [];
}

public sealed class SubjectEntity
{
    public long Id { get; set; }
    public required string SubjectCode { get; set; }
    public required string NormalizedSubjectCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public List<ExperimentEntity> Experiments { get; set; } = [];
}

public sealed class ExperimentEntity
{
    public ExperimentCreationMode CreationMode { get; set; } =
        ExperimentCreationMode.AcquisitionAndStimulation;
    public string? GenerationJson { get; set; }
    public long Id { get; set; }
    public required string ExperimentCode { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public required string Remarks { get; set; }
    public ExperimentStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long OperatorId { get; set; }
    public OperatorEntity Operator { get; set; } = null!;
    public long SubjectId { get; set; }
    public SubjectEntity Subject { get; set; } = null!;
    public StimulusParadigmEntity? StimulusParadigm { get; set; }
    public List<ExperimentElectrodeEntity> Electrodes { get; set; } = [];
    public List<ImpedanceSnapshotEntity> ImpedanceSnapshots { get; set; } = [];
    public List<ExperimentRunEntity> Runs { get; set; } = [];
}

public sealed class StimulusParadigmEntity
{
    public string? EnvelopeJson { get; set; }
    public long Id { get; set; }
    public long ExperimentId { get; set; }
    public ExperimentEntity Experiment { get; set; } = null!;
    public required string Kind { get; set; }
    public required string ArrayMode { get; set; }
    public required string Direction { get; set; }
    public required string ShamMode { get; set; }
    public double RampSeconds { get; set; }
    public double FrequencyHz { get; set; }
    public double DutyPercent { get; set; }
    public List<StimulusTargetEntity> Targets { get; set; } = [];
}

public sealed class StimulusTargetEntity
{
    public long Id { get; set; }
    public long StimulusParadigmId { get; set; }
    public StimulusParadigmEntity StimulusParadigm { get; set; } = null!;
    public int DisplayOrder { get; set; }
    public double PeakCurrentMilliAmps { get; set; }
    public List<StimulusChannelEntity> Channels { get; set; } = [];
}

public sealed class StimulusChannelEntity
{
    public long Id { get; set; }
    public long StimulusTargetId { get; set; }
    public StimulusTargetEntity StimulusTarget { get; set; } = null!;
    public required string SiteId { get; set; }
    public int PhysicalChannelId { get; set; }
    public required string Role { get; set; }
    public double CurrentMilliAmps { get; set; }
}

public sealed class ExperimentElectrodeEntity
{
    public long Id { get; set; }
    public long ExperimentId { get; set; }
    public ExperimentEntity Experiment { get; set; } = null!;
    public required string SiteId { get; set; }
    public ElectrodeUsage AcquisitionUsage { get; set; }
    public bool IsStimulus { get; set; }
    public int? AcquisitionPhysicalChannelId { get; set; }
    public int? StimulationPhysicalChannelId { get; set; }
    public string? StimulationRole { get; set; }
    public int? TargetDisplayOrder { get; set; }
}

public sealed class ImpedanceSnapshotEntity
{
    public long Id { get; set; }
    public long ExperimentId { get; set; }
    public ExperimentEntity Experiment { get; set; } = null!;
    public required string SiteId { get; set; }
    public ImpedanceMeasurementKind Kind { get; set; }
    public double? KiloOhms { get; set; }
    public bool Passed { get; set; }
    public DateTimeOffset MeasuredAtUtc { get; set; }
}

public sealed class ApplicationSessionEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset LastHeartbeatAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public List<ExperimentRunEntity> Runs { get; set; } = [];
}

public sealed class ExperimentRunEntity
{
    public string? ConfigurationJson { get; set; }
    public Guid Id { get; set; }
    public long ExperimentId { get; set; }
    public ExperimentEntity Experiment { get; set; } = null!;
    public Guid ApplicationSessionId { get; set; }
    public ApplicationSessionEntity ApplicationSession { get; set; } = null!;
    public ExperimentRunStatus Status { get; set; }
    public required string RunMode { get; set; }
    public int CycleCount { get; set; }
    public int SampleRateHz { get; set; }
    public double AcquisitionDurationMilliseconds { get; set; }
    public double BlankingDurationMilliseconds { get; set; }
    public double StimulationDurationMilliseconds { get; set; }
    public double RecoveryDurationMilliseconds { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset? InterruptRequestedAtUtc { get; set; }
    public string? InterruptRequestSource { get; set; }
    public DateTimeOffset LastHeartbeatAtUtc { get; set; }
    public string? LastKnownStage { get; set; }
    public int LastKnownCycle { get; set; }
    public double LastKnownElapsedMilliseconds { get; set; }
    public int Revision { get; set; }
    public bool? EegPacketReorderingEnabled { get; set; }
    public long? EegReceivedPacketCount { get; set; }
    public long? EegLostPacketCount { get; set; }
    public long? EegOutOfOrderPacketCount { get; set; }
    public long? EegDuplicatePacketCount { get; set; }
    public long? EegLateDiscardedPacketCount { get; set; }
    public List<ExperimentEventEntity> Events { get; set; } = [];
    public List<ExperimentIncidentEntity> Incidents { get; set; } = [];
    public List<EegFileEntity> Files { get; set; } = [];
}

public sealed class ExperimentEventEntity
{
    public long Id { get; set; }
    public Guid ExperimentRunId { get; set; }
    public ExperimentRunEntity ExperimentRun { get; set; } = null!;
    public ExperimentEventKind Kind { get; set; }
    public int Cycle { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public double? TimelineStartMilliseconds { get; set; }
    public double? TimelineEndMilliseconds { get; set; }
    public ExperimentEventStatus Status { get; set; }
    public EventEndTimeAccuracy EndTimeAccuracy { get; set; }
}

public sealed class ExperimentIncidentEntity
{
    public long Id { get; set; }
    public Guid ExperimentRunId { get; set; }
    public ExperimentRunEntity ExperimentRun { get; set; } = null!;
    public ExperimentIncidentKind Kind { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string Source { get; set; }
    public required string Message { get; set; }
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }
    public bool? DeviceStopSucceeded { get; set; }
    public string? DeviceStopError { get; set; }
}

public sealed class EegFileEntity
{
    public long Id { get; set; }
    public Guid ExperimentRunId { get; set; }
    public ExperimentRunEntity ExperimentRun { get; set; } = null!;
    public EegFileFormat Format { get; set; }
    public EegFileLocation Location { get; set; }
    public EegFileState State { get; set; }
    public required string Path { get; set; }
    public long? SourceFileId { get; set; }
    public EegFileEntity? SourceFile { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CleanedAtUtc { get; set; }
}
