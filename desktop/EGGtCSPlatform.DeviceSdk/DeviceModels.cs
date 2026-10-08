using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk;

public readonly record struct DeviceId(string Value)
{
    public static DeviceId Simulator { get; } = new("simulator-default");

    public override string ToString() => Value;
}

public sealed record DeviceIdentity(
    DeviceId DeviceId,
    string Model,
    string? SerialNumber,
    string? MacAddress
);

public sealed record DeviceCandidate(
    DeviceIdentity Identity,
    TransportEndpoint Endpoint,
    string ProtocolVersion,
    bool IdentityIsProvisional = false
)
{
    public ITransportEndpoint? ConnectionEndpoint { get; init; }
    public string? ProtocolId { get; init; }
    public ProtocolRevisionSource RevisionSource { get; init; } =
        ProtocolRevisionSource.Unspecified;
}

public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted,
}

public enum DeviceOperationState
{
    Unknown,
    Ready,
    ImpedanceChecking,
    Acquiring,
    Stimulating,
    Stopping,
    Faulted,
}

public sealed record DeviceStateSnapshot(
    DeviceConnectionState Connection,
    DeviceOperationState Operation,
    int? BatteryPercent,
    DateTimeOffset UpdatedAt,
    string? Fault = null
);

public sealed record DeviceCapabilities(
    bool CanReadStatus,
    bool CanAcquireEeg,
    bool CanStimulate,
    bool CanCheckEegImpedance,
    bool CanCheckStimulationImpedance,
    bool CanRunToleranceTest,
    int MaximumEegChannels,
    IReadOnlySet<int> SupportedSampleRatesHz,
    int StimulationPhysicalChannelCount
)
{
    public static DeviceCapabilities Simulator { get; } =
        new(true, true, true, true, true, true, 32, new HashSet<int> { 250, 500 }, 8);
}

public enum DeviceCommandStatus
{
    Success,
    Failed,
    InvalidParameter,
    DeviceBusy,
    NotConfigured,
    AlreadyRunning,
    AlreadyStopped,
    CurrentRampingDown,
    MaximumReached,
    MinimumReached,
}

public sealed record DeviceCommandResult(DeviceCommandStatus Status, string? Message = null)
{
    public byte? RawResultCode { get; init; }
    public bool IsSuccess => Status == DeviceCommandStatus.Success;

    public static DeviceCommandResult Success { get; } = new(DeviceCommandStatus.Success);
}

public sealed record DeviceStatusResponse(
    DeviceCommandStatus Status,
    DeviceOperationState Operation,
    int BatteryPercent
);

public sealed record CurrentAdjustmentResponse(
    DeviceCommandStatus Status,
    decimal CurrentMilliAmps
);
