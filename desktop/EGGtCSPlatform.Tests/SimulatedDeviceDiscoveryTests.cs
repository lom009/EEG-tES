using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk.Simulation;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SimulatedDeviceDiscoveryTests
{
    [Fact]
    public void ResolverChoosesDifferentAddressOnSameSubnet()
    {
        var local = IPAddress.Parse("192.168.1.100");
        var resolved = SimulatedLanAddressResolver.Resolve(
            [
                new SimulatedIpv4Interface(
                    local,
                    IPAddress.Parse("255.255.255.0"),
                    [IPAddress.Parse("192.168.1.1")],
                    2
                ),
            ],
            [local]
        );

        Assert.Equal(IPAddress.Parse("192.168.1.101"), resolved);
        Assert.NotEqual(local, resolved);
    }

    [Fact]
    public void ResolverUsesPreviousHostAtUpperSubnetBoundary()
    {
        var local = IPAddress.Parse("192.168.1.254");
        var resolved = SimulatedLanAddressResolver.Resolve(
            [
                new SimulatedIpv4Interface(
                    local,
                    IPAddress.Parse("255.255.255.0"),
                    [IPAddress.Parse("192.168.1.1")],
                    2
                ),
            ],
            [local]
        );

        Assert.Equal(IPAddress.Parse("192.168.1.253"), resolved);
    }

    [Fact]
    public void ResolverSkipsGatewayAndOtherLocalAddresses()
    {
        var local = IPAddress.Parse("10.0.0.20");
        var resolved = SimulatedLanAddressResolver.Resolve(
            [
                new SimulatedIpv4Interface(
                    local,
                    IPAddress.Parse("255.255.255.0"),
                    [IPAddress.Parse("10.0.0.21")],
                    2
                ),
            ],
            [local, IPAddress.Parse("10.0.0.19")]
        );

        Assert.Equal(IPAddress.Parse("10.0.0.22"), resolved);
    }

    [Fact]
    public void ResolverFallsBackAndAvoidsMatchingLocalAddress()
    {
        var resolved = SimulatedLanAddressResolver.Resolve([], [IPAddress.Parse("192.168.1.101")]);

        Assert.Equal(IPAddress.Parse("192.168.1.102"), resolved);
    }

    [Fact]
    public async Task DiscoveryUsesInjectedSimulatedEndpoint()
    {
        var endpoint = new TransportEndpoint("simulator", "192.168.50.101", 31007);
        var discovery = new SimulatedDeviceDiscovery(endpoint: endpoint);

        var candidates = new List<EGGtCSPlatform.DeviceSdk.DeviceCandidate>();
        await foreach (var candidate in discovery.DiscoverAsync(TimeSpan.FromMilliseconds(1)))
            candidates.Add(candidate);

        var discovered = Assert.Single(candidates);
        Assert.Equal(endpoint, discovered.Endpoint);
    }
}
