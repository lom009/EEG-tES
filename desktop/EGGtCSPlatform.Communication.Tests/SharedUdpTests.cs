using System.Net;
using System.Net.Sockets;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class SharedUdpTests
{
    [Fact]
    public async Task TwoDevicesShareOnePortAndDiscoveryDoesNotStealSessionDatagrams()
    {
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var firstEndpoint = (IPEndPoint)first.Client.LocalEndPoint!;
        var secondEndpoint = (IPEndPoint)second.Client.LocalEndPoint!;
        var port = FreePort();
        await using var registry = new SharedUdpHubRegistry();
        await using var a = registry.CreateTransport(
            new("127.0.0.1", firstEndpoint.Port, "0.0.0.0", port)
        );
        await using var b = registry.CreateTransport(
            new("127.0.0.1", secondEndpoint.Port, "127.0.0.1", port)
        );
        await a.ConnectAsync();
        await b.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await a.SendAsync(new byte[] { 1 }, timeout.Token);
        await b.SendAsync(new byte[] { 2 }, timeout.Token);
        var sentA = await first.ReceiveAsync(timeout.Token);
        var sentB = await second.ReceiveAsync(timeout.Token);
        Assert.Equal(port, sentA.RemoteEndPoint.Port);
        Assert.Equal(port, sentB.RemoteEndPoint.Port);
        var discovery = new UdpTargetedDiscoveryTransport(
            "127.0.0.1",
            port,
            [firstEndpoint],
            registry
        );
        await using var discoveryReader = discovery
            .DiscoverAsync(new byte[] { 9 }, TimeSpan.FromSeconds(1), timeout.Token)
            .GetAsyncEnumerator();
        var discoveryNext = discoveryReader.MoveNextAsync().AsTask();
        var probe = await first.ReceiveAsync(timeout.Token);
        await using var readerA = a.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await using var readerB = b.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await first.SendAsync(new byte[] { 10 }, probe.RemoteEndPoint, timeout.Token);
        await second.SendAsync(new byte[] { 20 }, sentB.RemoteEndPoint, timeout.Token);
        Assert.True(await discoveryNext);
        Assert.True(await readerA.MoveNextAsync());
        Assert.Equal(new byte[] { 10 }, readerA.Current.Data.ToArray());
        Assert.True(await readerB.MoveNextAsync());
        Assert.Equal(new byte[] { 20 }, readerB.Current.Data.ToArray());
        await a.DisconnectAsync();
        await second.SendAsync(new byte[] { 21 }, sentB.RemoteEndPoint, timeout.Token);
        Assert.True(await readerB.MoveNextAsync());
        Assert.Equal(new byte[] { 21 }, readerB.Current.Data.ToArray());
    }

    [Fact]
    public async Task ProbeFactoryReceivesTheActualEphemeralPort()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var discovery = new UdpTargetedDiscoveryTransport(
            "127.0.0.1",
            0,
            [(IPEndPoint)server.Client.LocalEndPoint!]
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = discovery
            .DiscoverAsync(
                port => BitConverter.GetBytes(port),
                TimeSpan.FromSeconds(1),
                timeout.Token
            )
            .GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        var probe = await server.ReceiveAsync(timeout.Token);
        Assert.Equal(probe.RemoteEndPoint.Port, BitConverter.ToInt32(probe.Buffer));
        await server.SendAsync(new byte[] { 1 }, probe.RemoteEndPoint, timeout.Token);
        Assert.True(await next);
    }

    [Fact]
    public async Task SlowRouteOverflowDoesNotAffectOtherDevices()
    {
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = FreePort();
        await using var registry = new SharedUdpHubRegistry(routeCapacity: 1);
        await using var a = registry.CreateTransport(
            new("127.0.0.1", ((IPEndPoint)first.Client.LocalEndPoint!).Port, "127.0.0.1", port)
        );
        await using var b = registry.CreateTransport(
            new("127.0.0.1", ((IPEndPoint)second.Client.LocalEndPoint!).Port, "127.0.0.1", port)
        );
        await a.ConnectAsync();
        await b.ConnectAsync();
        var destination = new IPEndPoint(IPAddress.Loopback, port);
        for (var i = 0; i < 4; i++)
            await first.SendAsync(new byte[] { (byte)i }, destination);
        await Task.Delay(40);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var readerA = a.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await readerA.MoveNextAsync());
        await Assert.ThrowsAsync<TransportCommunicationException>(() =>
            readerA.MoveNextAsync().AsTask()
        );
        await using var readerB = b.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await second.SendAsync(new byte[] { 8 }, destination);
        Assert.True(await readerB.MoveNextAsync());
        Assert.Equal(8, readerB.Current.Data.Span[0]);
    }

    [Fact]
    public async Task ThrowingLoggerDoesNotChangeSuccessfulSendOrReceive()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var transport = new UdpTransport(
            new("127.0.0.1", ((IPEndPoint)server.Client.LocalEndPoint!).Port),
            new ThrowingLogger()
        );
        await transport.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await transport.SendAsync(new byte[] { 1 }, timeout.Token);
        var request = await server.ReceiveAsync(timeout.Token);
        await server.SendAsync(new byte[] { 2 }, request.RemoteEndPoint, timeout.Token);
        await using var reader = transport.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
    }

    private sealed class ThrowingLogger : IByteTrafficLogger
    {
        public void Log(ByteTrafficLogEntry entry) => throw new Exception("logger");
    }

    [Fact]
    public async Task RuntimePreservesEphemeralDiscoveryPortForSubsequentConnection()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        await using var runtime = new DeviceRuntimeBuilder()
            .AddProtocol(
                new EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsProtocolModule(
                    new() { AllowUnverifiedChecksum = true }
                )
            )
            .WithOptions(
                new()
                {
                    UdpMode = UdpConnectionMode.Shared,
                    HeartbeatEnabled = false,
                    NetworkSettings = () => new("127.0.0.1", endpoint.Port, "127.0.0.1", 0),
                }
            )
            .Build();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        await using var discovery = runtime
            .Discovery.DiscoverAsync(TimeSpan.FromSeconds(1), timeout.Token)
            .GetAsyncEnumerator();
        var found = discovery.MoveNextAsync().AsTask();
        var probe = await server.ReceiveAsync(timeout.Token);
        var callbackPort = probe.Buffer[5] | probe.Buffer[6] << 8;
        Assert.Equal(probe.RemoteEndPoint.Port, callbackPort);
        var identity = System
            .Text.Encoding.UTF8.GetBytes("Test\0loopback-device\0")
            .Concat(new byte[] { 1, 2, 3, 4, 5, 6 })
            .ToArray();
        await server.SendAsync(
            codec.Encode(new(1, (byte)EggtCsCommandCode.DiscoverDeviceResponse, identity)),
            probe.RemoteEndPoint,
            timeout.Token
        );
        Assert.True(await found);
        var candidate = discovery.Current;
        await discovery.DisposeAsync();
        var connected = runtime.ConnectVerifiedAsync(candidate, timeout.Token);
        var status = await server.ReceiveAsync(timeout.Token);
        Assert.Equal(callbackPort, status.RemoteEndPoint.Port);
        Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, status.Buffer[4]);
        await server.SendAsync(
            codec.Encode(
                new(
                    status.Buffer[2],
                    (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                    new byte[] { 1, 66 }
                )
            ),
            status.RemoteEndPoint,
            timeout.Token
        );
        Assert.Equal(66, (await connected).State.BatteryPercent);
    }

    [Fact]
    public async Task SharedSocketFailureNotifiesEveryAffectedRoute()
    {
        UdpClient? socket = null;
        await using var registry = new SharedUdpHubRegistry
        {
            SocketFactory = endpoint => socket = new UdpClient(endpoint),
        };
        await using var binding = registry.Bind("127.0.0.1");
        await using var first = registry.CreateTransport(
            new("127.0.0.1", 10001, "127.0.0.1", binding.LocalPort)
        );
        await using var second = registry.CreateTransport(
            new("127.0.0.1", 10002, "127.0.0.1", binding.LocalPort)
        );
        await first.ConnectAsync();
        await second.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var a = first.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        await using var b = second.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        var readA = a.MoveNextAsync().AsTask();
        var readB = b.MoveNextAsync().AsTask();
        socket!.Dispose(); // Unexpected socket closure, distinct from registry/lease shutdown.
        await Assert.ThrowsAsync<TransportCommunicationException>(() => readA);
        await Assert.ThrowsAsync<TransportCommunicationException>(() => readB);
    }

    internal static int FreePort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
