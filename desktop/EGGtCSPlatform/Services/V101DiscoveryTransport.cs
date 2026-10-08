using System;
using System.Collections.Generic;
using System.Threading;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.Interfaces;

namespace EGGtCSPlatform.Services;

public sealed class V101DiscoveryTransport(
    DeviceConnectionNetworkSettings settings,
    IByteTrafficLogger? byteTrafficLogger = null
) : IDiscoveryTransport
{
    public IAsyncEnumerable<DiscoveryPacket> DiscoverAsync(
        ReadOnlyMemory<byte> probe,
        TimeSpan window,
        CancellationToken cancellationToken = default
    ) =>
        new UdpTargetedDiscoveryTransport(
            settings.LocalAddress,
            settings.LocalPort,
            UdpDiscoveryTargets.Legacy(
                new(
                    settings.BroadcastAddress,
                    settings.DevicePort,
                    settings.LocalAddress,
                    settings.LocalPort
                )
            ),
            logger: byteTrafficLogger
        ).DiscoverAsync(probe, window, cancellationToken);
}
