using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class UdpLogTimestampTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrafficUsesLocalOffsetAndReceivePacketPreservesTheSameInstant(bool shared)
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var logger = new TrafficLogger();
        await using var registry = new SharedUdpHubRegistry(logger);
        await using ITransport transport = shared
            ? registry.CreateTransport(new("127.0.0.1", port, "127.0.0.1", 0))
            : new UdpTransport(new("127.0.0.1", port, "127.0.0.1", 0), logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await transport.ConnectAsync(timeout.Token);
        var before = DateTimeOffset.Now;
        await transport.SendAsync(new byte[] { 1 }, timeout.Token);
        var request = await server.ReceiveAsync(timeout.Token);
        await server.SendAsync(new byte[] { 2 }, request.RemoteEndPoint, timeout.Token);
        await using var reader = transport.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var packet = reader.Current;
        var tx = await logger.Entries.Reader.ReadAsync(timeout.Token);
        var rx = await logger.Entries.Reader.ReadAsync(timeout.Token);
        var after = DateTimeOffset.Now;
        Assert.Equal(ByteTrafficDirection.Transmit, tx.Direction);
        Assert.Equal(ByteTrafficDirection.Receive, rx.Direction);
        foreach (var entry in new[] { tx, rx })
        {
            Assert.Equal(
                TimeZoneInfo.Local.GetUtcOffset(entry.Timestamp.UtcDateTime),
                entry.Timestamp.Offset
            );
            Assert.InRange(entry.Timestamp, before, after);
        }
        Assert.Equal(TimeSpan.Zero, packet.ReceivedAtUtc.Offset);
        Assert.Equal(rx.Timestamp.ToUniversalTime(), packet.ReceivedAtUtc);
        Assert.True(packet.ReceivedTimestamp > 0);
    }

    private sealed class TrafficLogger : IByteTrafficLogger
    {
        public Channel<ByteTrafficLogEntry> Entries { get; } =
            Channel.CreateUnbounded<ByteTrafficLogEntry>();

        public void Log(ByteTrafficLogEntry entry) => Entries.Writer.TryWrite(entry);
    }
}
