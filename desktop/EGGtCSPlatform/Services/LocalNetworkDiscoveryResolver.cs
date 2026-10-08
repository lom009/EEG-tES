using System.Collections.Generic;
using System.Net;

namespace EGGtCSPlatform.Services;

public sealed record LocalNetworkDiscoverySnapshot(
    IPAddress LocalAddress,
    IPAddress DirectedBroadcastAddress,
    IReadOnlyList<IPAddress> SubnetHosts
);

public interface ILocalNetworkDiscoveryResolver
{
    LocalNetworkDiscoverySnapshot Resolve(string configuredLocalAddress, int maximumHostCount);
}

public sealed class LocalNetworkDiscoveryResolver : ILocalNetworkDiscoveryResolver
{
    public LocalNetworkDiscoverySnapshot Resolve(
        string configuredLocalAddress,
        int maximumHostCount
    ) =>
        Convert(
            new DeviceRuntime.LocalNetworkDiscoveryResolver().Resolve(
                configuredLocalAddress,
                maximumHostCount
            )
        );

    public static LocalNetworkDiscoverySnapshot CreateSnapshot(
        IPAddress localAddress,
        int prefixLength,
        int maximumHostCount
    ) =>
        Convert(
            DeviceRuntime.LocalNetworkDiscoveryResolver.CreateSnapshot(
                localAddress,
                prefixLength,
                maximumHostCount
            )
        );

    private static LocalNetworkDiscoverySnapshot Convert(
        DeviceRuntime.LocalNetworkDiscoverySnapshot value
    ) => new(value.LocalAddress, value.DirectedBroadcastAddress, value.SubnetHosts);
}
