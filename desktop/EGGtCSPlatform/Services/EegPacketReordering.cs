using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EGGtCSPlatform.Services;

public sealed record EegPacketStatistics(
    bool ReorderingEnabled,
    long ReceivedPacketCount,
    long LostPacketCount,
    long OutOfOrderPacketCount,
    long DuplicatePacketCount,
    long LateDiscardedPacketCount
)
{
    public double LossRate =>
        ReceivedPacketCount + LostPacketCount == 0
            ? 0d
            : LostPacketCount / (double)(ReceivedPacketCount + LostPacketCount);

    public static EegPacketStatistics Empty(bool reorderingEnabled = true) =>
        new(reorderingEnabled, 0, 0, 0, 0, 0);
}

public sealed class EegPacketStatisticsAccumulator(bool reorderingEnabled)
{
    private long _received;
    private long _lost;
    private long _outOfOrder;
    private long _duplicate;
    private long _lateDiscarded;

    public bool ReorderingEnabled { get; } = reorderingEnabled;

    internal void RecordReceived(long count = 1) => Interlocked.Add(ref _received, count);

    internal void RecordLost(long count) => Interlocked.Add(ref _lost, count);

    internal void RecordOutOfOrder() => Interlocked.Increment(ref _outOfOrder);

    internal void RecordDuplicate() => Interlocked.Increment(ref _duplicate);

    internal void RecordLateDiscarded() => Interlocked.Increment(ref _lateDiscarded);

    public EegPacketStatistics Snapshot() =>
        new(
            ReorderingEnabled,
            Interlocked.Read(ref _received),
            Interlocked.Read(ref _lost),
            Interlocked.Read(ref _outOfOrder),
            Interlocked.Read(ref _duplicate),
            Interlocked.Read(ref _lateDiscarded)
        );
}

public readonly record struct EegSequencedPacket<T>(
    byte PacketIndex,
    int SampleCount,
    DateTimeOffset ReceivedAt,
    T Value
);

public readonly record struct EegReorderedPacket<T>(T Value, int MissingPacketsBefore);

public sealed class EegPacketReorderBuffer<T>
{
    private readonly bool _enabled;
    private readonly TimeSpan _timeout;
    private readonly int _windowPackets;
    private readonly EegPacketStatisticsAccumulator _statistics;
    private readonly SortedDictionary<long, EegSequencedPacket<T>> _buffer = [];
    private readonly HashSet<long> _accepted = [];
    private long _expectedSequence;

    public EegPacketReorderBuffer(
        bool enabled,
        TimeSpan timeout,
        int windowPackets,
        EegPacketStatisticsAccumulator statistics
    )
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (windowPackets is < 1 or > 127)
            throw new ArgumentOutOfRangeException(nameof(windowPackets));
        _enabled = enabled;
        _timeout = timeout;
        _windowPackets = windowPackets;
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
    }

    public bool HasPendingGap =>
        _enabled && _buffer.Count > 0 && !_buffer.ContainsKey(_expectedSequence);

    public IReadOnlyList<EegReorderedPacket<T>> Accept(EegSequencedPacket<T> packet)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(packet.SampleCount);
        if (!_enabled)
        {
            _statistics.RecordReceived();
            return [new EegReorderedPacket<T>(packet.Value, 0)];
        }

        var sequence = ResolveSequence(packet.PacketIndex);
        if (sequence < _expectedSequence)
        {
            if (_accepted.Contains(sequence))
                _statistics.RecordDuplicate();
            else
                _statistics.RecordLateDiscarded();
            return [];
        }
        if (_buffer.ContainsKey(sequence))
        {
            _statistics.RecordDuplicate();
            return [];
        }

        if (sequence > _expectedSequence)
            _statistics.RecordOutOfOrder();
        _buffer.Add(sequence, packet);
        var output = new List<EegReorderedPacket<T>>();
        DrainContiguous(output, 0);
        if (_buffer.Count >= _windowPackets)
            ResolveNextGap(output);
        PruneAccepted();
        return output;
    }

    public IReadOnlyList<EegReorderedPacket<T>> FlushExpired(DateTimeOffset now)
    {
        if (!_enabled || _buffer.Count == 0 || _buffer.ContainsKey(_expectedSequence))
            return [];
        var first = _buffer.First();
        if (now - first.Value.ReceivedAt < _timeout)
            return [];
        var output = new List<EegReorderedPacket<T>>();
        ResolveNextGap(output);
        PruneAccepted();
        return output;
    }

    public IReadOnlyList<EegReorderedPacket<T>> Complete()
    {
        if (!_enabled || _buffer.Count == 0)
            return [];
        var output = new List<EegReorderedPacket<T>>();
        while (_buffer.Count > 0)
        {
            DrainContiguous(output, 0);
            if (_buffer.Count > 0)
                ResolveNextGap(output);
        }
        PruneAccepted();
        return output;
    }

    private long ResolveSequence(byte packetIndex)
    {
        var cycleBase = _expectedSequence & ~255L;
        var candidate = cycleBase + packetIndex;
        if (candidate < _expectedSequence - 128)
            candidate += 256;
        else if (candidate > _expectedSequence + 127)
            candidate -= 256;
        return candidate;
    }

    private void ResolveNextGap(List<EegReorderedPacket<T>> output)
    {
        if (_buffer.Count == 0)
            return;
        var firstSequence = _buffer.First().Key;
        var missing = checked((int)(firstSequence - _expectedSequence));
        if (missing > 0)
        {
            _statistics.RecordLost(missing);
            _expectedSequence = firstSequence;
        }
        DrainContiguous(output, missing);
    }

    private void DrainContiguous(List<EegReorderedPacket<T>> output, int missingBeforeFirst)
    {
        var missing = missingBeforeFirst;
        while (_buffer.Remove(_expectedSequence, out var packet))
        {
            output.Add(new EegReorderedPacket<T>(packet.Value, missing));
            missing = 0;
            _accepted.Add(_expectedSequence);
            _statistics.RecordReceived();
            _expectedSequence++;
        }
    }

    private void PruneAccepted()
    {
        var minimum = _expectedSequence - 256;
        _accepted.RemoveWhere(sequence => sequence < minimum);
    }
}
