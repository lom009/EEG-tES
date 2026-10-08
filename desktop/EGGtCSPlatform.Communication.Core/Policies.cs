using System.Collections.Concurrent;

namespace EGGtCSPlatform.Communication.Core;

public interface IRequestScheduler : IAsyncDisposable
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        CommandDescriptor descriptor,
        CancellationToken cancellationToken = default
    );
}

public sealed class SerialRequestScheduler : IRequestScheduler
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        CommandDescriptor descriptor,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }

    public ValueTask DisposeAsync()
    {
        // Session cancellation releases queued callers; outstanding leases may still unwind.
        return ValueTask.CompletedTask;
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class CorrelatedRequestScheduler : IRequestScheduler
{
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _commandGates = new();
    private bool _disposed;

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        CommandDescriptor descriptor,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (
            descriptor.Pattern != CommunicationPattern.RequestResponse
            || descriptor.ResponseCorrelation != ResponseCorrelationMode.CommandOnly
        )
        {
            return NoopLease.Instance;
        }

        var responseCommand =
            descriptor.ExpectedResponseCommand
            ?? throw new InvalidOperationException(
                $"{descriptor.CommandType.Name} uses command-only correlation without an expected response command."
            );
        var gate = _commandGates.GetOrAdd(responseCommand, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new CommandLease(gate);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _commandGates.Clear();
        return ValueTask.CompletedTask;
    }

    private sealed class CommandLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public interface IRetryPolicy
{
    bool ShouldRetry(CommandDescriptor descriptor, int completedAttempts, Exception exception);

    TimeSpan GetDelay(int completedAttempts);
}

public sealed class NoRetryPolicy : IRetryPolicy
{
    public static NoRetryPolicy Instance { get; } = new();

    private NoRetryPolicy() { }

    public bool ShouldRetry(
        CommandDescriptor descriptor,
        int completedAttempts,
        Exception exception
    ) => false;

    public TimeSpan GetDelay(int completedAttempts) => TimeSpan.Zero;
}

public sealed record ReconnectPolicy(bool Enabled, TimeSpan InitialDelay, TimeSpan MaximumDelay)
{
    public static ReconnectPolicy Disabled { get; } = new(false, TimeSpan.Zero, TimeSpan.Zero);
}

public interface IHeartbeatPolicy
{
    TimeSpan Interval { get; }

    TimeSpan FailureTimeout { get; }

    bool ShouldRun { get; }

    ValueTask ExecuteAsync(IDeviceSession session, CancellationToken cancellationToken);
}

public interface ICommunicationDiagnostics
{
    void Report(CommunicationDiagnostic diagnostic);
}

public sealed record CommunicationDiagnostic(
    Guid SessionId,
    DateTimeOffset Timestamp,
    string Category,
    string Message,
    Exception? Exception = null
)
{
    public string? ProtocolId { get; init; }
    public string? ProtocolRevision { get; init; }
}

public sealed class NullCommunicationDiagnostics : ICommunicationDiagnostics
{
    public static NullCommunicationDiagnostics Instance { get; } = new();

    private NullCommunicationDiagnostics() { }

    public void Report(CommunicationDiagnostic diagnostic) { }
}

public sealed record DeviceSessionOptions
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public string? DeviceId { get; init; }
    public string? ProtocolId { get; init; }
    public string? ProtocolRevision { get; init; }
    public long ConnectionGeneration { get; init; }
    public ICommunicationFailureSink? FailureSink { get; init; }
    public int ControlEventCapacity { get; init; } = 256;

    public int DataEventCapacity { get; init; } = 256;

    public IRetryPolicy RetryPolicy { get; init; } = NoRetryPolicy.Instance;

    public ReconnectPolicy ReconnectPolicy { get; init; } = ReconnectPolicy.Disabled;

    public IHeartbeatPolicy? HeartbeatPolicy { get; init; }

    public ICommunicationDiagnostics Diagnostics { get; init; } =
        NullCommunicationDiagnostics.Instance;
}
