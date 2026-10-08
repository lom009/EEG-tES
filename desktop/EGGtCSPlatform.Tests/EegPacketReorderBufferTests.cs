using System;
using System.Linq;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EegPacketReorderBufferTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InOrderPacketsAreReleasedImmediately()
    {
        var (buffer, statistics) = Create();

        Assert.Equal([0], Values(buffer.Accept(Packet(0, 0))));
        Assert.Equal([1], Values(buffer.Accept(Packet(1, 1))));
        Assert.Equal([2], Values(buffer.Accept(Packet(2, 2))));
        Assert.Equal(3, statistics.Snapshot().ReceivedPacketCount);
    }

    [Fact]
    public void OutOfOrderPacketsAreReleasedInSequence()
    {
        var (buffer, statistics) = Create();

        Assert.Equal([0], Values(buffer.Accept(Packet(0, 0))));
        Assert.Empty(buffer.Accept(Packet(2, 2)));
        Assert.Equal([1, 2], Values(buffer.Accept(Packet(1, 1))));

        var snapshot = statistics.Snapshot();
        Assert.Equal(3, snapshot.ReceivedPacketCount);
        Assert.Equal(0, snapshot.LostPacketCount);
        Assert.Equal(1, snapshot.OutOfOrderPacketCount);
    }

    [Fact]
    public void TimeoutConfirmsLossAndAnnotatesTheFollowingPacket()
    {
        var (buffer, statistics) = Create();
        buffer.Accept(Packet(0, 0));
        buffer.Accept(Packet(2, 2));

        Assert.Empty(buffer.FlushExpired(StartedAt.AddMilliseconds(49)));
        var released = Assert.Single(buffer.FlushExpired(StartedAt.AddMilliseconds(51)));

        Assert.Equal(2, released.Value);
        Assert.Equal(1, released.MissingPacketsBefore);
        Assert.Equal(1, statistics.Snapshot().LostPacketCount);
    }

    [Fact]
    public void SequenceWrapsFrom255ToZero()
    {
        var (buffer, statistics) = Create();
        for (var index = 0; index < 254; index++)
            Assert.Single(buffer.Accept(Packet((byte)index, index)));

        Assert.Equal([254], Values(buffer.Accept(Packet(254, 254))));
        Assert.Equal([255], Values(buffer.Accept(Packet(255, 255))));
        Assert.Equal([256], Values(buffer.Accept(Packet(0, 256))));
        Assert.Equal([257], Values(buffer.Accept(Packet(1, 257))));
        Assert.Equal(258, statistics.Snapshot().ReceivedPacketCount);
    }

    [Fact]
    public void DuplicateAndLatePacketsAreDiscarded()
    {
        var (buffer, statistics) = Create();
        buffer.Accept(Packet(0, 0));
        Assert.Empty(buffer.Accept(Packet(0, 100)));
        buffer.Accept(Packet(2, 2));
        buffer.FlushExpired(StartedAt.AddMilliseconds(51));
        Assert.Empty(buffer.Accept(Packet(1, 101)));

        var snapshot = statistics.Snapshot();
        Assert.Equal(1, snapshot.DuplicatePacketCount);
        Assert.Equal(1, snapshot.LateDiscardedPacketCount);
    }

    [Fact]
    public void FullWindowImmediatelyConfirmsTheGap()
    {
        var statistics = new EegPacketStatisticsAccumulator(true);
        var buffer = new EegPacketReorderBuffer<int>(true, TimeSpan.FromSeconds(1), 2, statistics);
        buffer.Accept(Packet(0, 0));
        Assert.Empty(buffer.Accept(Packet(2, 2)));

        var released = buffer.Accept(Packet(3, 3));

        Assert.Equal([2, 3], Values(released));
        Assert.Equal(1, released[0].MissingPacketsBefore);
        Assert.Equal(1, statistics.Snapshot().LostPacketCount);
    }

    [Fact]
    public void CompletionOnlyCountsGapsConfirmedByBufferedPackets()
    {
        var (buffer, statistics) = Create();
        buffer.Accept(Packet(0, 0));
        buffer.Accept(Packet(3, 3));

        var released = Assert.Single(buffer.Complete());

        Assert.Equal(2, released.MissingPacketsBefore);
        Assert.Equal(2, statistics.Snapshot().LostPacketCount);
        Assert.Empty(buffer.Complete());
    }

    [Fact]
    public void NewAcquisitionRestartsAtZeroWhileSharingStatistics()
    {
        var statistics = new EegPacketStatisticsAccumulator(true);
        var first = new EegPacketReorderBuffer<int>(
            true,
            TimeSpan.FromMilliseconds(50),
            16,
            statistics
        );
        var second = new EegPacketReorderBuffer<int>(
            true,
            TimeSpan.FromMilliseconds(50),
            16,
            statistics
        );

        Assert.Single(first.Accept(Packet(0, 0)));
        Assert.Single(first.Accept(Packet(1, 1)));
        Assert.Single(second.Accept(Packet(0, 10)));

        Assert.Equal(3, statistics.Snapshot().ReceivedPacketCount);
    }

    [Fact]
    public void DisabledModePreservesArrivalOrderWithoutSequenceStatistics()
    {
        var statistics = new EegPacketStatisticsAccumulator(false);
        var buffer = new EegPacketReorderBuffer<int>(
            false,
            TimeSpan.FromMilliseconds(50),
            16,
            statistics
        );

        Assert.Equal([0], Values(buffer.Accept(Packet(0, 0))));
        Assert.Equal([2], Values(buffer.Accept(Packet(2, 2))));
        Assert.Equal([1], Values(buffer.Accept(Packet(1, 1))));
        var snapshot = statistics.Snapshot();
        Assert.Equal(3, snapshot.ReceivedPacketCount);
        Assert.Equal(0, snapshot.OutOfOrderPacketCount);
        Assert.Equal(0, snapshot.LostPacketCount);
    }

    [Fact]
    public void MissingPacketAdvancesSampleTimelineWithoutCreatingSamples()
    {
        var timeline = new EegSampleTimeline(500);
        Assert.Equal(0, timeline.GetNextPacketFirstSampleTicks(8));

        timeline.AdvanceMissingPackets(1, 8);

        Assert.Equal(16 * TimeSpan.TicksPerSecond / 500, timeline.GetNextPacketFirstSampleTicks(8));
    }

    private static (EegPacketReorderBuffer<int>, EegPacketStatisticsAccumulator) Create()
    {
        var statistics = new EegPacketStatisticsAccumulator(true);
        return (
            new EegPacketReorderBuffer<int>(true, TimeSpan.FromMilliseconds(50), 16, statistics),
            statistics
        );
    }

    private static EegSequencedPacket<int> Packet(byte index, int value) =>
        new(index, 8, StartedAt, value);

    private static int[] Values(
        System.Collections.Generic.IReadOnlyList<EegReorderedPacket<int>> packets
    ) => packets.Select(packet => packet.Value).ToArray();
}
