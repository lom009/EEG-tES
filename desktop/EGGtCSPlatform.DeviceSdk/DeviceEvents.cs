using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk;

public abstract record DeviceEvent;

public sealed record DeviceStateChangedEvent(DeviceStateSnapshot State) : DeviceEvent;

public sealed record DeviceProgressEvent(
    DeviceOperationState Operation,
    TimeSpan Elapsed,
    TimeSpan Remaining,
    double Progress,
    int CurrentCycle = 1,
    int TotalCycles = 1,
    decimal CurrentMilliAmps = 0,
    int? BatteryPercent = null,
    double AverageImpedanceKiloOhms = 0d
) : DeviceEvent;

public sealed record EegChannelSamples(
    int PhysicalChannel,
    double StartTimeSeconds,
    double SampleIntervalSeconds,
    IReadOnlyList<double> Samples
);

public sealed record EegSamplesReceivedEvent(IReadOnlyList<EegChannelSamples> Channels)
    : DeviceEvent;

public sealed record EegPacketChannelSamples(
    int PhysicalChannel,
    IReadOnlyList<double> SamplesMicrovolts
);

public sealed record EegDataPacketReceivedEvent(
    TimeSpan ReportedRemaining,
    int BatteryPercent,
    int SampleCount,
    IReadOnlyList<EegPacketChannelSamples> Channels,
    byte PacketIndex
) : DeviceEvent;

public enum ImpedanceBand
{
    Disabled,
    UpTo10KOhms,
    UpTo20KOhms,
    UpTo30KOhms,
    UpTo40KOhms,
    Above40KOhms,
    Normal,
    Abnormal,
}

public sealed record ImpedanceReading(int PhysicalChannel, ImpedanceBand Band);

public sealed record EegImpedanceReceivedEvent(IReadOnlyList<ImpedanceReading> Readings)
    : DeviceEvent;

public sealed record StimulationImpedanceReceivedEvent(IReadOnlyList<ImpedanceReading> Readings)
    : DeviceEvent;

public sealed record AcquisitionCompletedEvent : DeviceEvent;

public sealed record StimulationCompletedEvent : DeviceEvent;

public sealed record UninterpretedEegPacketEvent(int PayloadLength) : DeviceEvent;

public sealed record DeviceEventEnvelope(
    DeviceId DeviceId,
    Guid SessionId,
    DateTimeOffset Timestamp,
    DeviceEvent Event,
    EventDeliveryClass DeliveryClass,
    long ReceivedTimestamp = 0,
    TimeSpan? DecodeLatency = null,
    ReadOnlyMemory<byte> RawPacketData = default
)
{
    public ReadOnlyMemory<byte> RawFrameData { get; init; }
    public long TransportReceiveId { get; init; }
}
