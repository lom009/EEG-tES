using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EGGtCSPlatform.DeviceRuntime;

public static class UdpDiscoveryTargets
{
    public static IReadOnlyList<IPEndPoint> Legacy(UdpNetworkSettings settings)
    {
        var targets = new HashSet<IPAddress> { ParseAddress(settings.BroadcastAddress) };
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (
                networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
            )
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (
                    unicast.Address.AddressFamily != AddressFamily.InterNetwork
                    || !ShouldUseAddress(unicast.Address, settings)
                )
                {
                    continue;
                }

                AddSmallSubnetTargets(targets, unicast.Address, unicast.PrefixLength);
            }
        }
        return targets.Select(address => new IPEndPoint(address, settings.DevicePort)).ToArray();
    }

    private static bool ShouldUseAddress(IPAddress address, UdpNetworkSettings settings) =>
        string.Equals(
            settings.LocalAddress,
            IPAddress.Any.ToString(),
            StringComparison.OrdinalIgnoreCase
        )
        || string.Equals(
            settings.LocalAddress,
            address.ToString(),
            StringComparison.OrdinalIgnoreCase
        );

    private static void AddSmallSubnetTargets(
        HashSet<IPAddress> targets,
        IPAddress localAddress,
        int prefixLength
    )
    {
        // Bound the fallback to at most 254 unicast discovery packets.
        if (prefixLength is < 24 or > 30)
            return;

        var local = ToUInt32(localAddress);
        var mask = uint.MaxValue << (32 - prefixLength);
        var network = local & mask;
        var broadcast = network | ~mask;
        targets.Add(FromUInt32(broadcast));
        for (var host = network + 1; host < broadcast; host++)
        {
            if (host != local)
                targets.Add(FromUInt32(host));
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
    }

    private static IPAddress FromUInt32(uint address) =>
        new([(byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address]);

    private static IPAddress ParseAddress(string value) =>
        IPAddress.TryParse(value, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork
            ? address
            : throw new ArgumentException($"'{value}' is not a valid IPv4 address.", nameof(value));
}
