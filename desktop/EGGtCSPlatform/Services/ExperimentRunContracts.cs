using System;
using System.Collections.Generic;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public enum ExperimentRunStage
{
    Standby,
    Acquisition,
    Blanking,
    Stimulation,
    Recovery,
    Completed,
    Stopped,
}

public sealed record WaveformChannelBatch(
    string ChannelId,
    double StartTimeSeconds,
    double SampleIntervalSeconds,
    IReadOnlyList<double> Samples
);

public sealed record ExperimentRunTelemetry(
    ExperimentRunStage Stage,
    TimeSpan TotalElapsed,
    TimeSpan StageElapsed,
    double StageProgress,
    int CurrentCycle,
    int TotalCycles,
    double ActualCurrentMilliAmps,
    double AverageImpedanceKiloOhms,
    bool CommunicationHealthy,
    IReadOnlyList<string> Exceptions,
    IReadOnlyList<WaveformChannelBatch> WaveformBatches,
    bool IsWaveformOnly = false,
    DateTimeOffset? LatestPacketReceivedAtUtc = null,
    long LatestPacketReceivedTimestamp = 0,
    TimeSpan? LocalProcessingLatency = null,
    TimeSpan? DecodeLatency = null,
    EegPacketStatistics? PacketStatistics = null
);

public enum EegAcquisitionFailureKind
{
    StartResponseTimeout,
    StartRejected,
    StartFailed,
    DataPacketTimeout,
    RawRecordingFailed,
}

public sealed class EegAcquisitionException : Exception
{
    public EegAcquisitionException(
        EegAcquisitionFailureKind kind,
        string message,
        TimeSpan? timeout = null,
        bool stopCommandRequested = false,
        string? stopFailure = null,
        Exception? innerException = null
    )
        : base(AppendStopResult(message, stopCommandRequested, stopFailure), innerException)
    {
        Kind = kind;
        BaseMessage = message;
        Timeout = timeout;
        StopCommandRequested = stopCommandRequested;
        StopFailure = stopFailure;
    }

    public EegAcquisitionFailureKind Kind { get; }

    public string BaseMessage { get; }

    public TimeSpan? Timeout { get; }

    public bool StopCommandRequested { get; }

    public string? StopFailure { get; }

    internal EegAcquisitionException WithStopResult(bool requested, string? failure) =>
        new(Kind, BaseMessage, Timeout, requested, failure, InnerException);

    private static string AppendStopResult(
        string message,
        bool stopCommandRequested,
        string? stopFailure
    )
    {
        if (!stopCommandRequested)
            return message;
        return string.IsNullOrWhiteSpace(stopFailure)
            ? $"{message}；已发送停止采集指令。"
            : $"{message}；停止采集指令失败：{stopFailure}";
    }
}

public enum StimulationFailureKind
{
    ConfigurationFailed,
    StartResponseTimeout,
    StartRejected,
    StartFailed,
    ProgressPacketTimeout,
    CompletionEventTimeout,
}

public sealed class StimulationException : Exception
{
    public StimulationException(
        StimulationFailureKind kind,
        string message,
        TimeSpan? timeout = null,
        bool stopCommandRequested = false,
        string? stopFailure = null,
        Exception? innerException = null
    )
        : base(AppendStopResult(message, stopCommandRequested, stopFailure), innerException)
    {
        Kind = kind;
        BaseMessage = message;
        Timeout = timeout;
        StopCommandRequested = stopCommandRequested;
        StopFailure = stopFailure;
    }

    public StimulationFailureKind Kind { get; }

    public string BaseMessage { get; }

    public TimeSpan? Timeout { get; }

    public bool StopCommandRequested { get; }

    public string? StopFailure { get; }

    internal StimulationException WithStopResult(bool requested, string? failure) =>
        new(Kind, BaseMessage, Timeout, requested, failure, InnerException);

    private static string AppendStopResult(
        string message,
        bool stopCommandRequested,
        string? stopFailure
    )
    {
        if (!stopCommandRequested)
            return message;
        return string.IsNullOrWhiteSpace(stopFailure)
            ? $"{message}；已发送停止刺激指令。"
            : $"{message}；停止刺激指令失败：{stopFailure}";
    }
}

public sealed class ExperimentRunTelemetryEventArgs(ExperimentRunTelemetry telemetry) : EventArgs
{
    public ExperimentRunTelemetry Telemetry { get; } = telemetry;

    public string DeviceId { get; init; } = "simulator-default";
}

public sealed record AcquisitionRunRequest(
    TimeSpan Duration,
    IReadOnlyList<string> ChannelIds,
    int SampleRateHz,
    string DeviceId = "simulator-default",
    TimeSpan TimelineOffset = default,
    int CurrentCycle = 1,
    int TotalCycles = 1,
    Guid RecordingId = default
);

public sealed record StimulationRunRequest(
    TimeSpan Duration,
    double TargetCurrentMilliAmps,
    TimeSpan TimelineOffset,
    IReadOnlyList<string> ChannelIds,
    int SampleRateHz,
    ExperimentStimulusConfigurationSnapshot? StimulusConfiguration = null,
    IReadOnlyList<StimulusElectrodeAssignment>? StimulusElectrodes = null,
    string DeviceId = "simulator-default",
    int CurrentCycle = 1,
    int TotalCycles = 1,
    Guid RecordingId = default
);

public sealed record AutomaticExperimentRunRequest(
    TimeSpan AcquisitionDuration,
    TimeSpan BlankingDuration,
    TimeSpan StimulationDuration,
    TimeSpan RecoveryDuration,
    int CycleCount,
    double TargetCurrentMilliAmps,
    IReadOnlyList<string> ChannelIds,
    int SampleRateHz,
    ExperimentStimulusConfigurationSnapshot? StimulusConfiguration = null,
    IReadOnlyList<StimulusElectrodeAssignment>? StimulusElectrodes = null,
    string DeviceId = "simulator-default",
    Guid RecordingId = default,
    ExperimentCreationMode CreationMode = ExperimentCreationMode.AcquisitionAndStimulation
);
