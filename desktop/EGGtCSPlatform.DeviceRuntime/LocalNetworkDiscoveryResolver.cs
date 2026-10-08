using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EGGtCSPlatform.DeviceRuntime;

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
    )
    {
        var candidates = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(item =>
                item.OperationalStatus == OperationalStatus.Up
                && item.NetworkInterfaceType != NetworkInterfaceType.Loopback
            )
            .SelectMany(item =>
                item.GetIPProperties()
                    .UnicastAddresses.Where(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork
                    )
                    .Select(address => new
                    {
                        Interface = item,
                        Address = address.Address,
                        PrefixLength = address.PrefixLength,
                        HasGateway = item.GetIPProperties()
                            .GatewayAddresses.Any(gateway =>
                                gateway.Address.AddressFamily == AddressFamily.InterNetwork
                                && !gateway.Address.Equals(IPAddress.Any)
                            ),
                    })
            )
            .ToArray();
        var selected =
            !string.IsNullOrWhiteSpace(configuredLocalAddress)
            && configuredLocalAddress != IPAddress.Any.ToString()
                ? candidates.FirstOrDefault(item =>
                    item.Address.ToString() == configuredLocalAddress
                )
                : candidates
                    .OrderByDescending(item => item.HasGateway)
                    .ThenByDescending(item => IsPrivate(item.Address))
                    .ThenBy(item =>
                        item.Interface.GetIPProperties().GetIPv4Properties()?.Index ?? int.MaxValue
                    )
                    .FirstOrDefault();
        if (selected is null)
            throw new InvalidOperationException("未找到可用于设备发现的本地 IPv4 网卡。");

        return CreateSnapshot(selected.Address, selected.PrefixLength, maximumHostCount);
    }

    public static LocalNetworkDiscoverySnapshot CreateSnapshot(
        IPAddress localAddress,
        int prefixLength,
        int maximumHostCount
    )
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        if (localAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("必须使用 IPv4 地址。", nameof(localAddress));
        if (prefixLength is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        if (maximumHostCount is < 1 or > 254)
            throw new ArgumentOutOfRangeException(nameof(maximumHostCount));
        var local = ToUInt32(localAddress);
        var prefix = prefixLength;
        var mask = uint.MaxValue << (32 - prefix);
        var broadcast = local & mask | ~mask;

        // 单播扫描固定限制在本机 /24，避免 /16 等网段产生数万次请求。
        var subnet24 = local & 0xFFFFFF00u;
        var hosts = new List<IPAddress>(Math.Min(maximumHostCount, 254));
        for (uint suffix = 1; suffix <= 254 && hosts.Count < maximumHostCount; suffix++)
        {
            var host = subnet24 | suffix;
            if (host != local)
                hosts.Add(FromUInt32(host));
        }
        return new LocalNetworkDiscoverySnapshot(localAddress, FromUInt32(broadcast), hosts);
    }

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
        return (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
}
