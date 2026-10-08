namespace EGGtCSPlatform.Interfaces;

public sealed record DeviceConnectionNetworkSettings(
    string BroadcastAddress,
    int DevicePort,
    string LocalAddress,
    int LocalPort
);

public interface IDeviceConnectionNetworkContext
{
    DeviceConnectionNetworkSettings Snapshot { get; }

    void Configure(int devicePort, int localPort);
}
