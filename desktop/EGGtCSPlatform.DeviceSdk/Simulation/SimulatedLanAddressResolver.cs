using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EGGtCSPlatform.DeviceSdk.Simulation;

internal sealed record SimulatedIpv4Interface(
    IPAddress Address,
    IPAddress SubnetMask,
    IReadOnlyList<IPAddress> Gateways,
    int AdapterPriority
);

internal static class SimulatedLanAddressResolver
{
    private static readonly IPAddress DefaultFallback = IPAddress.Parse("192.168.1.101");

    public static IPAddress Resolve()
    {
        var interfaces = new List<SimulatedIpv4Interface>();
        var localAddresses = new HashSet<IPAddress>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (
                    networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType
                        is NetworkInterfaceType.Loopback
                            or NetworkInterfaceType.Tunnel
                )
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = networkInterface.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                var gateways = properties
                    .GatewayAddresses.Select(item => item.Address)
                    .Where(IsIpv4)
                    .ToArray();
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (!IsIpv4(unicast.Address) || unicast.IPv4Mask is null)
                        continue;
                    localAddresses.Add(unicast.Address);
                    interfaces.Add(
                        new SimulatedIpv4Interface(
                            unicast.Address,
                            unicast.IPv4Mask,
                            gateways,
                            GetAdapterPriority(networkInterface.NetworkInterfaceType)
                        )
                    );
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Fall through to the deterministic private-network fallback.
        }

        return Resolve(interfaces, localAddresses);
    }

    internal static IPAddress Resolve(
        IReadOnlyList<SimulatedIpv4Interface> interfaces,
        IReadOnlyCollection<IPAddress> localAddresses
    )
    {
        var excluded = localAddresses.Where(IsIpv4).Select(ToUInt32).ToHashSet();
        foreach (
            var networkInterface in interfaces
                .Where(item => IsIpv4(item.Address) && IsIpv4(item.SubnetMask))
                .OrderByDescending(item => IsPrivate(item.Address) && item.Gateways.Any(IsIpv4))
                .ThenByDescending(item => IsPrivate(item.Address))
                .ThenByDescending(item => item.Gateways.Any(IsIpv4))
                .ThenByDescending(item => item.AdapterPriority)
        )
        {
            foreach (var gateway in networkInterface.Gateways.Where(IsIpv4))
                excluded.Add(ToUInt32(gateway));
            if (
                TrySelectSibling(
                    networkInterface.Address,
                    networkInterface.SubnetMask,
                    excluded,
                    out var address
                )
            )
                return address;
        }

        return SelectFallback(excluded);
    }

    private static bool TrySelectSibling(
        IPAddress localAddress,
        IPAddress subnetMask,
        IReadOnlySet<uint> excluded,
        out IPAddress address
    )
    {
        var local = ToUInt32(localAddress);
        var mask = ToUInt32(subnetMask);
        var network = local & mask;
        var broadcast = network | ~mask;
        var hostRange = (ulong)broadcast - network;
        if (hostRange <= 2)
        {
            address = IPAddress.None;
            return false;
        }

        var maximumOffset = (uint)Math.Min(hostRange, 1024UL);
        for (uint offset = 1; offset <= maximumOffset; offset++)
        {
            var higher = (ulong)local + offset;
            if (higher < broadcast && !excluded.Contains((uint)higher))
            {
                address = FromUInt32((uint)higher);
                return true;
            }

            if (local >= offset)
            {
                var lower = local - offset;
                if (lower > network && !excluded.Contains(lower))
                {
                    address = FromUInt32(lower);
                    return true;
                }
            }
        }

        address = IPAddress.None;
        return false;
    }

    private static IPAddress SelectFallback(IReadOnlySet<uint> excluded)
    {
        var first = ToUInt32(DefaultFallback);
        var last = ToUInt32(IPAddress.Parse("192.168.1.254"));
        for (var candidate = first; candidate <= last; candidate++)
        {
            if (!excluded.Contains(candidate))
                return FromUInt32(candidate);
        }
        return IPAddress.Parse("192.168.2.101");
    }

    private static int GetAdapterPriority(NetworkInterfaceType type) =>
        type switch
        {
            NetworkInterfaceType.Ethernet => 2,
            NetworkInterfaceType.Wireless80211 => 1,
            _ => 0,
        };

    private static bool IsIpv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork;

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
