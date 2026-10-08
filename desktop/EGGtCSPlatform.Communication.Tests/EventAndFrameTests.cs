using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class EventAndFrameTests
{
    private static DeviceEventEnvelope Event(EventDeliveryClass delivery, int index = 0) =>
        new(
            new("test"),
            Guid.Empty,
            DateTimeOffset.UtcNow,
            new UninterpretedEegPacketEvent(index),
            delivery
        );

    [Fact]
    public async Task SlowReliableSubscriberFailsWithoutBlockingFastSubscriber()
    {
        var hub = new DeviceEventHub(1);
        await using var slow = hub.Subscribe();
        await using var fast = hub.Subscribe(new() { DataCapacity = 8 });
        await slow.Ready;
        for (var i = 0; i < 3; i++)
            await hub.PublishAsync(Event(EventDeliveryClass.Data, i));
        await using var slowReader = slow.ReadEventsAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<DeviceSubscriptionOverflowException>(() =>
            slowReader.MoveNextAsync().AsTask()
        );
        await using var fastReader = fast.ReadEventsAsync().GetAsyncEnumerator();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await fastReader.MoveNextAsync());
            Assert.Equal(i, ((UninterpretedEegPacketEvent)fastReader.Current.Event).PayloadLength);
        }
    }

    [Fact]
    public async Task LatestDataDoesNotDiscardControlAndPreservesRelativeOrder()
    {
        var hub = new DeviceEventHub(1);
        await using var subscription = hub.Subscribe(
            new() { DataCapacity = 1, DataPolicy = DeviceDataDeliveryPolicy.Latest }
        );
        await hub.PublishAsync(Event(EventDeliveryClass.Data, 1));
        await hub.PublishAsync(Event(EventDeliveryClass.Control, 2));
        await hub.PublishAsync(Event(EventDeliveryClass.Data, 3));
        Assert.Equal(1, subscription.DroppedDataCount);
        await using var reader = subscription.ReadEventsAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(EventDeliveryClass.Control, reader.Current.DeliveryClass);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(3, ((UninterpretedEegPacketEvent)reader.Current.Event).PayloadLength);
    }

    [Fact]
    public async Task ReadyRegistrationReceivesAnEventBeforeEnumerationStarts()
    {
        var hub = new DeviceEventHub(4);
        await using var subscription = hub.Subscribe();
        await subscription.Ready;
        await hub.PublishAsync(Event(EventDeliveryClass.Control));
        await using var reader = subscription.ReadEventsAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
    }

    [Fact]
    public void MixedValidAndInvalidFramesRetainAllSuccessfulFrames()
    {
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var valid = codec.Encode(
            new(1, (byte)EggtCsCommandCode.ReadDeviceStatusResponse, new byte[] { 1, 73 })
        );
        var invalid = valid.ToArray();
        invalid[^1] = 0;
        var results = codec.FeedResults(valid.Concat(invalid).Concat(valid).ToArray(), true);
        Assert.Equal(3, results.Count);
        Assert.NotNull(results[0].Frame);
        Assert.NotNull(results[1].Error);
        Assert.NotNull(results[2].Frame);
        Assert.Equal(valid, results[0].RawFrame.ToArray());
    }

    [Fact]
    public void DatagramRemainderPolicyCanDiscardFragmentsWithoutChangingLegacyDefault()
    {
        var codec = new EggtCsFrameCodec(
            new EggtCsUnverifiedFixedChecksum(),
            remainderPolicy: DatagramRemainderPolicy.Discard
        );
        var valid = codec.Encode(
            new(1, (byte)EggtCsCommandCode.ReadDeviceStatusResponse, new byte[] { 1, 73 })
        );
        Assert.Empty(codec.FeedResults(valid.AsSpan(0, 4), true));
        Assert.Empty(codec.FeedResults(valid.AsSpan(4), true));
        Assert.Single(codec.FeedResults(valid, true));
    }

    [Fact]
    public async Task NormalSessionRestartReopensEventsButFaultedSessionCannotRestart()
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        await using var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults())
        );
        await session.StartAsync();
        await session.StopAsync();
        await session.StartAsync();
        transport.Inject(
            codec.Encode(
                new(1, (byte)EggtCsCommandCode.AcquisitionCompleted, ReadOnlyMemory<byte>.Empty)
            )
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = session.ReadControlEventsAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.NotEmpty(reader.Current.RawFrameData.ToArray());
        await transport.DisconnectAsync();
        await RuntimeTests.Eventually(() => session.State == DeviceSessionState.Faulted);
        await session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync().AsTask());
    }

    [Fact]
    public async Task StopCancelsRequestsWaitingForTheirCommandScheduler()
    {
        var transport = new FakeTransport();
        await using var session = new DeviceSession(
            transport,
            new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()),
            new EggtCsProtocolProfile(
                EggtCsProtocolOptions.CreateTestDefaults(TimeSpan.FromSeconds(10))
            ),
            new EggtCsResponseMatcher()
        );
        await session.StartAsync();
        var first = session.SendAsync(new ReadDeviceStatusRequest());
        var waiting = session.SendAsync(new ReadDeviceStatusRequest());
        await session.StopAsync();
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => first);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => waiting);
        Assert.Single(transport.SentPackets);
    }
}
