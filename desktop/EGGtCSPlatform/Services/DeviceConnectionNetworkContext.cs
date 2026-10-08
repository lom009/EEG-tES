using System;
using EGGtCSPlatform.Interfaces;

namespace EGGtCSPlatform.Services;

public sealed class DeviceConnectionNetworkContext(
    string broadcastAddress = "255.255.255.255",
    string localAddress = "0.0.0.0",
    int devicePort = 30307,
    int localPort = 30302
) : IDeviceConnectionNetworkContext
{
    private readonly object _sync = new();
    private DeviceConnectionNetworkSettings _snapshot = new(
        broadcastAddress,
        devicePort,
        localAddress,
        localPort
    );

    public DeviceConnectionNetworkSettings Snapshot
    {
        get
        {
            lock (_sync)
                return _snapshot;
        }
    }

    public void Configure(int devicePort, int localPort)
    {
        if (devicePort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(devicePort));
        if (localPort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(localPort));

        lock (_sync)
        {
            _snapshot = _snapshot with { DevicePort = devicePort, LocalPort = localPort };
        }
    }
}
