using System.Threading.Channels;

namespace EGGtCSPlatform.Communication.Core;

/// <summary>Bounded asynchronous isolation for untrusted or slow diagnostic consumers.</summary>
public sealed class AsyncCommunicationDiagnostics
    : ICommunicationDiagnostics,
        IByteTrafficLogger,
        IAsyncDisposable
{
    private readonly Channel<object> _queue;
    private readonly ICommunicationDiagnostics? _diagnostics;
    private readonly IByteTrafficLogger? _traffic;
    private readonly Task _pump;
    private long _dropped;
    private long _failures;

    public AsyncCommunicationDiagnostics(
        ICommunicationDiagnostics? diagnostics = null,
        IByteTrafficLogger? traffic = null,
        int capacity = 2048
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _diagnostics = diagnostics;
        _traffic = traffic;
        _queue = Channel.CreateBounded<object>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                AllowSynchronousContinuations = false,
            },
            _ => Interlocked.Increment(ref _dropped)
        );
        _pump = Task.Run(PumpAsync);
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long ConsumerFailureCount => Interlocked.Read(ref _failures);

    public void Report(CommunicationDiagnostic diagnostic) => _queue.Writer.TryWrite(diagnostic);

    public void Log(ByteTrafficLogEntry entry) =>
        _queue.Writer.TryWrite(entry with { Data = entry.Data.ToArray(), DataIsImmutable = true });

    private async Task PumpAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (item is CommunicationDiagnostic diagnostic)
                    _diagnostics?.Report(diagnostic);
                else
                    _traffic?.Log((ByteTrafficLogEntry)item);
            }
            catch
            {
                Interlocked.Increment(ref _failures);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        // A consumer that never returns must not prevent transport shutdown.
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
    }
}
