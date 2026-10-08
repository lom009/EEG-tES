namespace EGGtCSPlatform.DeviceSdk;

public enum DeviceConnectionSource
{
    Real,
    Simulated,
}

public enum DeviceCapabilityKind
{
    Status,
    EegAcquisition,
    Stimulation,
    EegImpedance,
    StimulationImpedance,
    Tolerance,
}

public enum DeviceCapabilitySource
{
    Real,
    Simulated,
    Disabled,
}

public interface IDeviceCapabilitySourceProfile
{
    DeviceCapabilitySource GetCapabilitySource(DeviceCapabilityKind capability);
}

public static class DeviceCapabilitySourceExtensions
{
    public static DeviceCapabilitySource CapabilitySource(
        this IEggtCsDevice device,
        DeviceCapabilityKind capability
    )
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device is IDeviceCapabilitySourceProfile profile)
            return profile.GetCapabilitySource(capability);
        return IsEnabled(device.Capabilities, capability)
            ? DeviceCapabilitySource.Real
            : DeviceCapabilitySource.Disabled;
    }

    public static bool UsesCapabilitySource(
        this IEggtCsDevice device,
        DeviceCapabilityKind capability,
        DeviceCapabilitySource source
    ) => device.CapabilitySource(capability) == source;

    private static bool IsEnabled(
        DeviceCapabilities capabilities,
        DeviceCapabilityKind capability
    ) =>
        capability switch
        {
            DeviceCapabilityKind.Status => capabilities.CanReadStatus,
            DeviceCapabilityKind.EegAcquisition => capabilities.CanAcquireEeg,
            DeviceCapabilityKind.Stimulation => capabilities.CanStimulate,
            DeviceCapabilityKind.EegImpedance => capabilities.CanCheckEegImpedance,
            DeviceCapabilityKind.StimulationImpedance => capabilities.CanCheckStimulationImpedance,
            DeviceCapabilityKind.Tolerance => capabilities.CanRunToleranceTest,
            _ => false,
        };
}

public interface IEggtCsDevice : IAsyncDisposable
{
    DeviceIdentity Identity { get; }

    DeviceCapabilities Capabilities { get; }

    DeviceStateSnapshot State { get; }

    IEegAcquisitionCapability? EegAcquisition { get; }

    IStimulationCapability? Stimulation { get; }

    IEnvelopeStimulationCapability? EnvelopeStimulation => null;

    IImpedanceCapability? Impedance { get; }

    IToleranceCapability? Tolerance { get; }

    Task<DeviceStatusResponse> ReadStatusAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
        CancellationToken cancellationToken = default
    );

    DeviceEventSubscription SubscribeEvents(DeviceEventSubscriptionOptions? options = null) =>
        DeviceEventSubscription.FromStream(ReadEventsAsync(), options);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IEegAcquisitionCapability
{
    Task<DeviceCommandResult> StartAsync(
        TimeSpan duration,
        IReadOnlySet<int> physicalChannels,
        int sampleRateHz,
        CancellationToken cancellationToken = default
    );

    Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default);
}

public interface IStimulationCapability
{
    Task<DeviceCommandResult> ConfigureAsync(
        StimulationConfiguration configuration,
        CancellationToken cancellationToken = default
    );

    Task<DeviceCommandResult> StartAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default
    );

    Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default);
}

public interface IImpedanceCapability
{
    Task<DeviceCommandResult> StartEegAsync(
        IReadOnlySet<int> physicalChannels,
        CancellationToken cancellationToken = default
    );

    Task<DeviceCommandResult> StopEegAsync(
        IReadOnlySet<int> physicalChannels,
        CancellationToken cancellationToken = default
    );

    Task<DeviceCommandResult> StartStimulationAsync(
        StimulationConfiguration configuration,
        CancellationToken cancellationToken = default
    );

    Task<DeviceCommandResult> StopStimulationAsync(
        StimulationConfiguration configuration,
        CancellationToken cancellationToken = default
    );
}

public interface IToleranceCapability
{
    Task<CurrentAdjustmentResponse> AdjustCurrentAsync(
        CurrentAdjustment adjustment,
        CancellationToken cancellationToken = default
    );
}
