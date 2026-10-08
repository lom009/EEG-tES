using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;

namespace EGGtCSPlatform.Services;

public interface IConfiguredDeviceDiscovery
{
    IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        DeviceDiscoveryModeDefinition mode,
        CancellationToken cancellationToken = default
    );
}

public sealed class ConfiguredDeviceDiscovery(
    DeviceAutoConnectionOptions options,
    IDeviceConnectionNetworkContext networkContext,
    ILocalNetworkDiscoveryResolver networkResolver,
    IByteTrafficLogger byteTrafficLogger,
    Func<IDeviceManager> deviceManager,
    DeviceBackendOptions backendOptions,
    IDeviceProtocolModule protocolModule
) : IConfiguredDeviceDiscovery
{
    public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        DeviceDiscoveryModeDefinition mode,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (backendOptions.ConnectionSource == DeviceConnectionSource.Simulated)
        {
            await foreach (
                var candidate in deviceManager()
                    .DiscoverAsync(mode.ResponseTimeout, cancellationToken)
            )
                yield return candidate;
            yield break;
        }

        var kind = mode.Kind switch
        {
            DeviceDiscoveryModeKind.GlobalBroadcast => DeviceRuntime
                .DiscoveryModeKind
                .GlobalBroadcast,
            DeviceDiscoveryModeKind.LocalBroadcast => DeviceRuntime
                .DiscoveryModeKind
                .LocalBroadcast,
            DeviceDiscoveryModeKind.SubnetUnicast => DeviceRuntime.DiscoveryModeKind.SubnetUnicast,
            _ => throw new InvalidOperationException(
                "Simulator discovery mode cannot run with a real connection source."
            ),
        };
        var address =
            mode.Kind == DeviceDiscoveryModeKind.GlobalBroadcast ? options.GlobalBroadcast.Address
            : mode.Kind == DeviceDiscoveryModeKind.LocalBroadcast ? options.LocalBroadcast.Address
            : null;
        var coordinator = new DeviceRuntime.DeviceDiscoveryCoordinator(
            () =>
            {
                var network = networkContext.Snapshot;
                return new(
                    network.BroadcastAddress,
                    network.DevicePort,
                    network.LocalAddress,
                    network.LocalPort
                );
            },
            [protocolModule],
            byteTrafficLogger,
            resolveNetwork: (local, maximum) =>
            {
                var value = networkResolver.Resolve(local, maximum);
                return new(value.LocalAddress, value.DirectedBroadcastAddress, value.SubnetHosts);
            }
        );
        await foreach (
            var candidate in coordinator.DiscoverAsync(
                new(kind, mode.ResponseTimeout, address, options.SubnetUnicast.MaximumHostCount),
                cancellationToken
            )
        )
            yield return candidate;
    }
}
