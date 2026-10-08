using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class UdpTransportTests
{
    [Fact]
    public async Task SendsAndReceivesDatagramsOverLoopback()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerEndpoint = (IPEndPoint)listener.Client.LocalEndPoint!;
        await using var transport = new UdpTransport(
            new UdpTransportOptions(
                IPAddress.Loopback.ToString(),
                listenerEndpoint.Port,
                IPAddress.Loopback.ToString(),
                0
            )
        );
        await transport.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await transport.SendAsync(new byte[] { 1, 2, 3 }, timeout.Token);
        var receivedByListener = await listener.ReceiveAsync(timeout.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, receivedByListener.Buffer);

        await using var enumerator = transport
            .ReceiveAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var moveNext = enumerator.MoveNextAsync().AsTask();
        await listener.SendAsync(
            new byte[] { 4, 5 },
            receivedByListener.RemoteEndPoint,
            timeout.Token
        );
        Assert.True(await moveNext);
        Assert.Equal(new byte[] { 4, 5 }, enumerator.Current.Data.ToArray());
        Assert.True(enumerator.Current.PreservesMessageBoundary);
    }

    [Fact]
    public async Task ReceiveHonorsCancellation()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerEndpoint = (IPEndPoint)listener.Client.LocalEndPoint!;
        await using var transport = new UdpTransport(
            new UdpTransportOptions(
                IPAddress.Loopback.ToString(),
                listenerEndpoint.Port,
                IPAddress.Loopback.ToString(),
                0
            )
        );
        await transport.ConnectAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await using var enumerator = transport
            .ReceiveAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await enumerator.MoveNextAsync().AsTask()
        );
    }
}
