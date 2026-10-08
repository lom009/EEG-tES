using System.Linq;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk;

public enum MeasurementControl
{
    Start,
    Stop,
}

public enum RunControl
{
    Start,
    Stop,
}

public enum CurrentAdjustment
{
    Increase,
    Decrease,
}

public sealed record ReadDeviceStatusRequest : IDeviceRequest<DeviceStatusResponse>;

public sealed record ConfigureEegImpedanceRequest(
    MeasurementControl Control,
    IReadOnlySet<int> EnabledPhysicalChannels
) : IDeviceRequest<DeviceCommandResult>;

public sealed record ControlAcquisitionRequest(RunControl Control, TimeSpan Duration)
    : IDeviceRequest<DeviceCommandResult>;

public sealed record ControlStimulationRequest(RunControl Control, TimeSpan Duration)
    : IDeviceRequest<DeviceCommandResult>;

public sealed record AdjustCurrentRequest(CurrentAdjustment Adjustment)
    : IDeviceRequest<CurrentAdjustmentResponse>;

public sealed record ConfigureStimulationImpedanceRequest(
    MeasurementControl Control,
    StimulationConfiguration Configuration
) : IDeviceRequest<DeviceCommandResult>;

public enum DeviceStimulationChannelRole
{
    FixedActive,
    Selectable,
}

public sealed record StimulationChannel(
    int PhysicalChannel,
    decimal CurrentMilliAmps,
    DeviceStimulationChannelRole Role = DeviceStimulationChannelRole.Selectable
);

public sealed record StimulationTargetGroup(
    int GroupNumber,
    IReadOnlyList<StimulationChannel> Channels
)
{
    public StimulationChannel FixedActiveChannel =>
        Channels.Single(channel => channel.Role == DeviceStimulationChannelRole.FixedActive);
}

public enum StimulationWaveform
{
    TDcs,
    TAcs,
    ShamDirect,
    TRns,
    TPcs,
    ShamAlternating,
}

public enum StimulationDirection
{
    Positive,
    Negative,
    Bidirectional,
}

public sealed record StimulationConfiguration(
    bool HighDefinition,
    IReadOnlyList<StimulationTargetGroup> TargetGroups,
    StimulationWaveform Waveform,
    StimulationDirection Direction,
    decimal FrequencyHz,
    int DutyPercent,
    TimeSpan RampDuration
)
{
    public StimulationConfiguration(
        bool highDefinition,
        IReadOnlyList<StimulationChannel> channels,
        StimulationWaveform waveform,
        StimulationDirection direction,
        decimal frequencyHz,
        int dutyPercent,
        TimeSpan rampDuration
    )
        : this(
            highDefinition,
            [new StimulationTargetGroup(1, channels)],
            waveform,
            direction,
            frequencyHz,
            dutyPercent,
            rampDuration
        ) { }

    public IReadOnlyList<StimulationChannel> Channels =>
        TargetGroups.SelectMany(group => group.Channels).ToArray();
}
