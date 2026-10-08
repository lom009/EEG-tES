using System.Runtime.CompilerServices;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk.Simulation;

public sealed class SimulatedDeviceDiscovery(
    DeviceIdentity? identity = null,
    TransportEndpoint? endpoint = null
) : IDeviceDiscovery
{
    private readonly DeviceIdentity _identity =
        identity
        ?? new DeviceIdentity(
            DeviceId.Simulator,
            "EGG/tCS Simulator",
            "SIM-0001",
            "00:00:00:00:00:01"
        );

    public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new DeviceCandidate(
            _identity,
            endpoint
                ?? new TransportEndpoint(
                    "simulator",
                    SimulatedLanAddressResolver.Resolve().ToString(),
                    30307
                ),
            "simulator"
        );
    }
}

public sealed class SimulatedDeviceConnector : IDeviceConnector
{
    public bool CanConnect(DeviceCandidate candidate) =>
        string.Equals(candidate.Endpoint.Scheme, "simulator", StringComparison.OrdinalIgnoreCase);

    public Task<IEggtCsDevice> ConnectAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IEggtCsDevice>(new SimulatedEggtCsDevice(candidate.Identity));
    }
}
