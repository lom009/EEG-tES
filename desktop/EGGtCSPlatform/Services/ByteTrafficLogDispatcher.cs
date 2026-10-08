using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.Services;

public sealed class ByteTrafficLogDispatcher : IByteTrafficLogger, IAsyncDisposable
{
    private const int Capacity = 2048;
    private readonly IByteTrafficLogger _primary;
    private readonly Channel<PendingLogEntry> _entries;
    private readonly Task _pump;
    private IByteTrafficLogger? _observer;
    private long _droppedEntryCount;
    private volatile bool _disposed;

    public ByteTrafficLogDispatcher(IByteTrafficLogger primary)
    {
        _primary = primary;
        _entries = Channel.CreateBounded<PendingLogEntry>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            },
            _ => Interlocked.Increment(ref _droppedEntryCount)
        );
        _pump = PumpAsync();
    }

    public long DroppedEntryCount => Interlocked.Read(ref _droppedEntryCount);

    public IDisposable Attach(IByteTrafficLogger observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (Interlocked.CompareExchange(ref _observer, observer, null) is not null)
            throw new InvalidOperationException("A byte traffic observer is already attached.");
        return new ObserverSubscription(this, observer);
    }

    public void Log(ByteTrafficLogEntry entry)
    {
        if (_disposed)
            return;
        var stableEntry = entry.DataIsImmutable
            ? entry
            : entry with
            {
                Data = entry.Data.ToArray(),
                DataIsImmutable = true,
            };
        _entries.Writer.TryWrite(new PendingLogEntry(stableEntry, Volatile.Read(ref _observer)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _entries.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (var pending in _entries.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var entry = pending.Entry;
            _primary.Log(entry);
            var observer = pending.Observer;
            if (observer is null)
                continue;
            try
            {
                observer.Log(entry);
            }
            catch (Exception exception)
            {
                // A developer-only observer must never interfere with device communication.
                ApplicationLog.Write(
                    ApplicationLogLevel.Error,
                    nameof(ByteTrafficLogDispatcher),
                    "Communication.ObserverFailed",
                    "通信调试观察器处理失败",
                    exception: exception
                );
            }
        }
    }

    private sealed record PendingLogEntry(ByteTrafficLogEntry Entry, IByteTrafficLogger? Observer);

    private void Detach(IByteTrafficLogger observer) =>
        Interlocked.CompareExchange(ref _observer, null, observer);

    private sealed class ObserverSubscription(
        ByteTrafficLogDispatcher owner,
        IByteTrafficLogger observer
    ) : IDisposable
    {
        private ByteTrafficLogDispatcher? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Detach(observer);
    }
}
