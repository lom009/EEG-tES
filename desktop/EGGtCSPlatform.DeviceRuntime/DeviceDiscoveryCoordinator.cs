using System.Net;
using System.Runtime.CompilerServices;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.DeviceRuntime;

public enum DiscoveryModeKind
{
    GlobalBroadcast,
    LocalBroadcast,
    SubnetUnicast,
}

public sealed record DiscoveryModeRequest(
    DiscoveryModeKind Kind,
    TimeSpan ResponseTimeout,
    string? Address = null,
    int MaximumHostCount = 254
);

public sealed class DeviceDiscoveryCoordinator(
    Func<UdpNetworkSettings> networkSettings,
    IReadOnlyList<IDeviceProtocolModule> protocols,
    IByteTrafficLogger? logger = null,
    SharedUdpHubRegistry? hubs = null,
    Func<string, int, LocalNetworkDiscoverySnapshot>? resolveNetwork = null
)
{
    public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        DiscoveryModeRequest mode,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (protocols.Count == 0)
            yield break;
        var network = networkSettings();
        var snapshot = (resolveNetwork ?? new LocalNetworkDiscoveryResolver().Resolve)(
            network.LocalAddress,
            mode.MaximumHostCount
        );
        IReadOnlyList<IPAddress> targets = mode.Kind switch
        {
            DiscoveryModeKind.GlobalBroadcast =>
            [
                IPAddress.Parse(mode.Address ?? network.BroadcastAddress),
            ],
            DiscoveryModeKind.LocalBroadcast =>
            [
                string.IsNullOrWhiteSpace(mode.Address)
                    ? snapshot.DirectedBroadcastAddress
                    : IPAddress.Parse(mode.Address),
            ],
            DiscoveryModeKind.SubnetUnicast => snapshot.SubnetHosts,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        foreach (var module in protocols)
        {
            var transport = new UdpTargetedDiscoveryTransport(
                snapshot.LocalAddress.ToString(),
                network.LocalPort,
                targets.Select(address => new IPEndPoint(address, network.DevicePort)).ToArray(),
                hubs,
                logger
            );
            await foreach (
                var candidate in module
                    .CreateDiscovery(transport, network.LocalPort)
                    .DiscoverAsync(mode.ResponseTimeout, cancellationToken)
                    .ConfigureAwait(false)
            )
                yield return DeviceRuntime.IdentifyDiscovery(module, candidate);
        }
    }
}
