using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.DeviceRuntime;

public sealed record UdpNetworkSettings(
    string BroadcastAddress = "255.255.255.255",
    int DevicePort = 30307,
    string LocalAddress = "0.0.0.0",
    int LocalPort = 30302
);

public sealed record DeviceRuntimeOptions
{
    public DeviceBackendProfile Backend { get; init; } = new();
    public Func<UdpNetworkSettings> NetworkSettings { get; init; } = () => new();
    public UdpConnectionMode UdpMode { get; init; } = UdpConnectionMode.Dedicated;
    public bool ValidateConnection { get; init; } = true;
    public bool HeartbeatEnabled { get; init; } = true;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan HeartbeatFailureTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public bool PauseHeartbeatDuringOperations { get; init; } = true;
    public IHeartbeatPauseService? HeartbeatPause { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public CommunicationFailureHandler? FailureHandler { get; init; }
    public TimeSpan FailureHandlerTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public ICommunicationDiagnostics? Diagnostics { get; init; }
    public IByteTrafficLogger? ByteTrafficLogger { get; init; }
    public Action<DeviceIdentity, Exception>? SessionFaulted { get; init; }
    public Action<IEggtCsDevice, DeviceStatusResponse>? HeartbeatReceived { get; init; }
    public DeviceSessionOptions SessionOptions { get; init; } = new();
    public IReadOnlyList<DiscoveryModeRequest>? DiscoveryModes { get; init; }
    public Func<string, int, LocalNetworkDiscoverySnapshot>? DiscoveryNetworkResolver { get; init; }
}

public sealed class DeviceBackendProfile
{
    public DeviceConnectionSource ConnectionSource { get; set; } = DeviceConnectionSource.Real;
    public Dictionary<DeviceCapabilityKind, DeviceCapabilitySource> Sources { get; init; } =
        Enum.GetValues<DeviceCapabilityKind>()
            .ToDictionary(
                kind => kind,
                kind =>
                    kind == DeviceCapabilityKind.Tolerance
                        ? DeviceCapabilitySource.Simulated
                        : DeviceCapabilitySource.Real
            );

    public DeviceCapabilitySource GetSource(DeviceCapabilityKind kind) =>
        Sources.GetValueOrDefault(kind, DeviceCapabilitySource.Disabled);

    public static DeviceBackendProfile Simulation() =>
        new()
        {
            ConnectionSource = DeviceConnectionSource.Simulated,
            Sources = Enum.GetValues<DeviceCapabilityKind>()
                .ToDictionary(kind => kind, _ => DeviceCapabilitySource.Simulated),
        };

    internal void Validate()
    {
        if (!Enum.IsDefined(ConnectionSource))
            throw new ArgumentException("Unknown connection source.");
        if (GetSource(DeviceCapabilityKind.Status) == DeviceCapabilitySource.Disabled)
            throw new ArgumentException("Status cannot be disabled.");
        foreach (var kind in Enum.GetValues<DeviceCapabilityKind>())
        {
            var source = GetSource(kind);
            if (!Enum.IsDefined(source))
                throw new ArgumentException("Unknown capability source.");
            if (
                ConnectionSource == DeviceConnectionSource.Simulated
                && source == DeviceCapabilitySource.Real
            )
                throw new ArgumentException("Real capabilities require a real connection.");
        }
    }
}
