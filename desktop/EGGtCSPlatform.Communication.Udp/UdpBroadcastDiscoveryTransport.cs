using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.Communication.Udp;

public sealed record UdpDiscoveryOptions(
    string BroadcastAddress = "255.255.255.255",
    int DeviceListenPort = 30307,
    string LocalAddress = "0.0.0.0",
    int LocalListenPort = 30302
);

public sealed class UdpBroadcastDiscoveryTransport(
    UdpDiscoveryOptions? options = null,
    IByteTrafficLogger? byteTrafficLogger = null
) : IBoundDiscoveryTransport
{
    private readonly UdpDiscoveryOptions _options = options ?? new();

    private UdpTargetedDiscoveryTransport Create() =>
        new(
            _options.LocalAddress,
            _options.LocalListenPort,
            [new IPEndPoint(IPAddress.Parse(_options.BroadcastAddress), _options.DeviceListenPort)],
            logger: byteTrafficLogger
        );

    public IAsyncEnumerable<DiscoveryPacket> DiscoverAsync(
        ReadOnlyMemory<byte> probe,
        TimeSpan window,
        CancellationToken cancellationToken = default
    ) => Create().DiscoverAsync(probe, window, cancellationToken);

    public IAsyncEnumerable<DiscoveryPacket> DiscoverAsync(
        Func<int, ReadOnlyMemory<byte>> probeFactory,
        TimeSpan window,
        CancellationToken cancellationToken = default
    ) => Create().DiscoverAsync(probeFactory, window, cancellationToken);
}
