using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace EGGtCSPlatform.Communication.Core;

public enum DeviceSessionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Faulted,
}

public sealed record DeviceSessionEvent(
    Guid SessionId,
    DateTimeOffset Timestamp,
    object Message,
    EventDeliveryClass DeliveryClass,
    long ReceivedTimestamp = 0,
    TimeSpan? DecodeLatency = null,
    ReadOnlyMemory<byte> RawPacketData = default
)
{
    public ReadOnlyMemory<byte> RawFrameData { get; init; }
    public long TransportReceiveId { get; init; }
}

public sealed class DeviceSessionFaultedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public interface IDeviceSession : IAsyncDisposable
{
    event EventHandler<DeviceSessionFaultedEventArgs>? Faulted;

    Guid SessionId { get; }

    DeviceSessionState State { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    Task<TResponse> SendAsync<TResponse>(
        IDeviceRequest<TResponse> request,
        CancellationToken cancellationToken = default
    );

    Task SendAsync(IDeviceCommand command, CancellationToken cancellationToken = default);

    IAsyncEnumerable<DeviceSessionEvent> ReadControlEventsAsync(
        CancellationToken cancellationToken = default
    );

    IAsyncEnumerable<DeviceSessionEvent> ReadDataEventsAsync(
        CancellationToken cancellationToken = default
    );
}

public sealed class DeviceSession : IDeviceSession
{
    private sealed record PendingRequest(
        ProtocolFrame Request,
        CommandDescriptor Descriptor,
        TaskCompletionSource<object> Completion
    );

    private readonly ITransport _transport;
    private readonly ISessionProtocol _protocol;
    private readonly AsyncCommunicationDiagnostics _diagnostics;
    private readonly IRequestScheduler _scheduler;
    private readonly DeviceSessionOptions _options;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Channel<DeviceSessionEvent> _controlEvents;
    private Channel<DeviceSessionEvent> _dataEvents;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private int _faulted;
    private bool _started;
    private long _receiveId;
    private bool _disposed;

    public DeviceSession(
        ITransport transport,
        IFrameCodec frameCodec,
        IDeviceProtocolProfile protocol,
        IResponseMatcher? responseMatcher = null,
        IRequestScheduler? scheduler = null,
        DeviceSessionOptions? options = null
    )
        : this(
            transport,
            new LegacySessionProtocol(frameCodec, protocol, responseMatcher),
            scheduler,
            options
        ) { }

    public DeviceSession(
        ITransport transport,
        ISessionProtocol protocol,
        IRequestScheduler? scheduler = null,
        DeviceSessionOptions? options = null
    )
    {
        _transport = transport;
        _protocol = protocol;
        _scheduler = scheduler ?? new ProtocolRequestScheduler(protocol);
        _options = options ?? new DeviceSessionOptions();
        _diagnostics = new AsyncCommunicationDiagnostics(_options.Diagnostics);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.ControlEventCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.DataEventCapacity);
        if (_options.HeartbeatPolicy is { } heartbeat)
        {
            if (heartbeat.Interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Heartbeat interval must be positive."
                );
            if (heartbeat.FailureTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Heartbeat failure timeout must be positive."
                );
        }
        _controlEvents = CreateBoundedChannel(_options.ControlEventCapacity);
        _dataEvents = CreateBoundedChannel(_options.DataEventCapacity);
        SessionId = Guid.NewGuid();
    }

    public Guid SessionId { get; }

    public event EventHandler<DeviceSessionFaultedEventArgs>? Faulted;

    public DeviceSessionState State { get; private set; } = DeviceSessionState.Disconnected;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State != DeviceSessionState.Disconnected || Volatile.Read(ref _faulted) != 0)
                throw new InvalidOperationException(
                    $"Cannot start a session in state {State}; replace faulted sessions."
                );
            if (_started)
            {
                _controlEvents = CreateBoundedChannel(_options.ControlEventCapacity);
                _dataEvents = CreateBoundedChannel(_options.DataEventCapacity);
            }
            _started = true;
            State = DeviceSessionState.Connecting;
            _sessionCancellation = new CancellationTokenSource();
            try
            {
                await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                State = DeviceSessionState.Connected;
                _receiveTask = ReceiveLoopAsync(_sessionCancellation.Token);
                if (_options.HeartbeatPolicy is not null)
                    _heartbeatTask = HeartbeatLoopAsync(
                        _options.HeartbeatPolicy,
                        _sessionCancellation.Token
                    );
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (State == DeviceSessionState.Disconnected)
            return;
        State = DeviceSessionState.Disconnecting;
        _sessionCancellation?.Cancel();
        FailAllPending(new DeviceDisconnectedException(SessionId));
        try
        {
            await _transport.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            await AwaitBackgroundTaskAsync(_receiveTask).ConfigureAwait(false);
            await AwaitBackgroundTaskAsync(_heartbeatTask).ConfigureAwait(false);
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _protocol.Reset();
            }
            finally
            {
                _sendGate.Release();
            }
            _controlEvents.Writer.TryComplete();
            _dataEvents.Writer.TryComplete();
            State =
                Volatile.Read(ref _faulted) == 0
                    ? DeviceSessionState.Disconnected
                    : DeviceSessionState.Faulted;
        }
    }

    public async Task<TResponse> SendAsync<TResponse>(
        IDeviceRequest<TResponse> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await SendCoreAsync(request, typeof(TResponse), cancellationToken)
            .ConfigureAwait(false);
        return result is TResponse typed
            ? typed
            : throw new ProtocolDecodingException(
                $"Response for {request.GetType().Name} was {result.GetType().Name}, expected {typeof(TResponse).Name}."
            );
    }

    public async Task SendAsync(
        IDeviceCommand command,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        var descriptor = _protocol.Describe(command);
        if (descriptor.Pattern != CommunicationPattern.FireAndForget)
            throw new InvalidOperationException($"{command.GetType().Name} requires a response.");
        await SendCoreAsync(command, null, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<DeviceSessionEvent> ReadControlEventsAsync(
        CancellationToken cancellationToken = default
    ) => _controlEvents.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<DeviceSessionEvent> ReadDataEventsAsync(
        CancellationToken cancellationToken = default
    ) => _dataEvents.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _controlEvents.Writer.TryComplete();
                _dataEvents.Writer.TryComplete();
                try
                {
                    await _transport.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await _scheduler.DisposeAsync().ConfigureAwait(false);
                    await _diagnostics.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<object> SendCoreAsync(
        IDeviceCommand command,
        Type? responseType,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != DeviceSessionState.Connected)
            throw new DeviceDisconnectedException(SessionId);

        var descriptor = _protocol.Describe(command);
        if (descriptor.Pattern == CommunicationPattern.RequestResponse && responseType is null)
            throw new InvalidOperationException(
                $"{command.GetType().Name} requires a typed response."
            );
        if (
            descriptor.ResponseType is not null
            && responseType is not null
            && !responseType.IsAssignableFrom(descriptor.ResponseType)
        )
            throw new InvalidOperationException(
                $"The protocol response type for {command.GetType().Name} is incompatible."
            );

        var lifetime = _sessionCancellation!.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime
        );
        var wasSent = false;
        try
        {
            return await SendScheduledAsync(command, descriptor, linked.Token, () => wasSent = true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DeviceDisconnectedException(SessionId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested)
                ReportFailure("Request", exception, descriptor, wasSent);
            throw;
        }
    }

    private async Task<object> SendScheduledAsync(
        IDeviceCommand command,
        CommandDescriptor descriptor,
        CancellationToken cancellationToken,
        Action sending
    )
    {
        await using var lease = await _scheduler
            .AcquireAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await SendAttemptAsync(command, descriptor, cancellationToken, sending)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException
                    && descriptor.IsIdempotent
                    && _options.RetryPolicy.ShouldRetry(descriptor, attempt, exception)
                )
            {
                var delay = _options.RetryPolicy.GetDelay(attempt);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _options.TimeProvider, cancellationToken)
                        .ConfigureAwait(false);
            }
        }
    }

    private async Task<object> SendAttemptAsync(
        IDeviceCommand command,
        CommandDescriptor descriptor,
        CancellationToken cancellationToken,
        Action sending
    )
    {
        string index;
        ProtocolFrame request;
        PendingRequest? pending = null;
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State != DeviceSessionState.Connected)
                throw new DeviceDisconnectedException(SessionId);
            index = _protocol.AllocateCorrelationId(
                _pending.Keys.ToHashSet(StringComparer.Ordinal)
            );
            request = _protocol.Encode(command, index);
            if (descriptor.Pattern == CommunicationPattern.RequestResponse)
            {
                pending = new PendingRequest(
                    request,
                    descriptor,
                    new TaskCompletionSource<object>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )
                );
                if (!_pending.TryAdd(index, pending))
                    throw new InvalidOperationException(
                        $"Request index {index} is already pending."
                    );
            }

            var bytes = _protocol.EncodeFrame(request);
            sending();
            await _transport.SendAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (pending is not null)
                ((ICollection<KeyValuePair<string, PendingRequest>>)_pending).Remove(
                    new(pending.Request.CorrelationId, pending)
                );
            throw;
        }
        finally
        {
            _sendGate.Release();
        }

        try
        {
            if (pending is null)
                return Unit.Value;

            using var timeout = new CancellationTokenSource(
                descriptor.Timeout,
                _options.TimeProvider
            );
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _sessionCancellation?.Token ?? CancellationToken.None,
                timeout.Token
            );
            try
            {
                return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new RequestTimeoutException(descriptor.CommandType, descriptor.Timeout);
            }
            catch (OperationCanceledException)
                when (_sessionCancellation?.IsCancellationRequested == true
                    && !cancellationToken.IsCancellationRequested
                )
            {
                throw new DeviceDisconnectedException(SessionId);
            }
        }
        finally
        {
            if (pending is not null)
                ((ICollection<KeyValuePair<string, PendingRequest>>)_pending).Remove(
                    new(index, pending)
                );
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (
                var incoming in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false)
            )
            {
                var packet = incoming with { Data = incoming.Data.ToArray() };
                var receiveId = Interlocked.Increment(ref _receiveId);
                IReadOnlyList<FrameParseResult> frames;
                try
                {
                    frames = _protocol.Feed(packet.Data.Span, packet.PreservesMessageBoundary);
                }
                catch (CommunicationException exception)
                {
                    Report("Frame", exception.Message, exception);
                    continue;
                }

                foreach (var result in frames)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    if (result.Error is { } error)
                    {
                        Report("Frame", error.Message, error);
                        continue;
                    }
                    if (result.Frame is not { } frame)
                        continue;
                    ProtocolDecodedMessage decoded;
                    try
                    {
                        decoded = _protocol.Decode(frame);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        Report(
                            "Protocol",
                            $"Failed to decode message {frame.MessageId}.",
                            exception
                        );
                        continue;
                    }

                    if (decoded.Kind == ProtocolMessageKind.Response && TryCompletePending(decoded))
                        continue;
                    if (decoded.Kind == ProtocolMessageKind.Response)
                        Report(
                            "Response",
                            $"Orphan response {decoded.MessageId} with correlation {decoded.CorrelationId}."
                        );
                    else
                        PublishEvent(decoded, packet, frame.RawFrame, receiveId);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
                Fault(
                    new TransportCommunicationException(
                        "The transport receive stream completed unexpectedly."
                    )
                );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Fault(exception);
        }
    }

    private async Task HeartbeatLoopAsync(
        IHeartbeatPolicy policy,
        CancellationToken cancellationToken
    )
    {
        var lastSuccess = _options.TimeProvider.GetTimestamp();
        var wasPaused = false;
        try
        {
            using var timer = new PeriodicTimer(policy.Interval, _options.TimeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (State != DeviceSessionState.Connected)
                    continue;
                if (!policy.ShouldRun)
                {
                    wasPaused = true;
                    continue;
                }
                if (wasPaused)
                {
                    lastSuccess = _options.TimeProvider.GetTimestamp();
                    wasPaused = false;
                }
                try
                {
                    await policy.ExecuteAsync(this, cancellationToken).ConfigureAwait(false);
                    lastSuccess = _options.TimeProvider.GetTimestamp();
                }
                catch (CommunicationException exception)
                {
                    Report("Heartbeat", exception.Message, exception);
                    if (!policy.ShouldRun)
                    {
                        wasPaused = true;
                        continue;
                    }
                    if (_options.TimeProvider.GetElapsedTime(lastSuccess) >= policy.FailureTimeout)
                    {
                        Fault(
                            new HeartbeatTimeoutException(
                                SessionId,
                                policy.FailureTimeout,
                                exception
                            )
                        );
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Fault(exception);
        }
    }

    private bool TryCompletePending(ProtocolDecodedMessage decoded)
    {
        foreach (var pending in _pending.Values)
        {
            if (
                _protocol.IsMatch(pending.Descriptor, pending.Request, decoded)
                && pending.Completion.TrySetResult(decoded.Value)
            )
                return true;
        }
        return false;
    }

    private void PublishEvent(
        ProtocolDecodedMessage decoded,
        TransportPacket packet,
        ReadOnlyMemory<byte> rawFrame,
        long receiveId
    )
    {
        var receivedAtUtc =
            packet.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : packet.ReceivedAtUtc;
        var receivedTimestamp =
            packet.ReceivedTimestamp == 0 ? Stopwatch.GetTimestamp() : packet.ReceivedTimestamp;
        var sessionEvent = new DeviceSessionEvent(
            SessionId,
            receivedAtUtc,
            decoded.Value,
            decoded.DeliveryClass,
            receivedTimestamp,
            Stopwatch.GetElapsedTime(receivedTimestamp),
            packet.Data
        )
        {
            RawFrameData = rawFrame.ToArray(),
            TransportReceiveId = receiveId,
        };
        var writer =
            decoded.DeliveryClass == EventDeliveryClass.Data
                ? _dataEvents.Writer
                : _controlEvents.Writer;
        if (!writer.TryWrite(sessionEvent))
            Fault(new DeviceEventBufferOverflowException(decoded.DeliveryClass));
    }

    private void Fault(Exception exception)
    {
        if (
            State is DeviceSessionState.Disconnecting or DeviceSessionState.Disconnected
            || Interlocked.Exchange(ref _faulted, 1) != 0
        )
            return;
        State = DeviceSessionState.Faulted;
        Report("Session", "The device session faulted.", exception);
        _sessionCancellation?.Cancel();
        FailAllPending(exception);
        _controlEvents.Writer.TryComplete(exception);
        _dataEvents.Writer.TryComplete(exception);
        ReportFailure("Session", exception, null, false);
        foreach (var handler in Faulted?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<DeviceSessionFaultedEventArgs>)handler)(
                    this,
                    new DeviceSessionFaultedEventArgs(exception)
                );
            }
            catch (Exception handlerException)
            {
                Report("FaultHandler", handlerException.Message, handlerException);
            }
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var pending in _pending.Values)
            pending.Completion.TrySetException(exception);
        _pending.Clear();
    }

    private void Report(string category, string message, Exception? exception = null) =>
        _diagnostics.Report(
            new CommunicationDiagnostic(
                SessionId,
                _options.TimeProvider.GetUtcNow(),
                category,
                message,
                exception
            )
            {
                ProtocolId = _options.ProtocolId,
                ProtocolRevision = _options.ProtocolRevision,
            }
        );

    private void ReportFailure(
        string category,
        Exception exception,
        CommandDescriptor? descriptor,
        bool wasSent
    )
    {
        try
        {
            _options.FailureSink?.ReportFailure(
                new CommunicationFailureContext(
                    _options.DeviceId,
                    SessionId,
                    _options.ConnectionGeneration,
                    category,
                    exception,
                    descriptor?.CommandType,
                    wasSent,
                    descriptor?.IsIdempotent ?? false,
                    wasSent
                        && descriptor?.ResponseCorrelation == ResponseCorrelationMode.CommandOnly,
                    category == "Session"
                        ? new HashSet<CommunicationFailureDecision>
                        {
                            CommunicationFailureDecision.UseDefault,
                            CommunicationFailureDecision.ReconnectDevice,
                        }
                        : new HashSet<CommunicationFailureDecision>(
                            Enum.GetValues<CommunicationFailureDecision>()
                        )
                )
                {
                    ProtocolId = _options.ProtocolId,
                    ProtocolRevision = _options.ProtocolRevision,
                }
            );
        }
        catch (Exception error)
        {
            Report("FailureSink", error.Message, error);
        }
    }

    private static Channel<DeviceSessionEvent> CreateBoundedChannel(int capacity) =>
        Channel.CreateBounded<DeviceSessionEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            }
        );

    private static async Task AwaitBackgroundTaskAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private sealed class Unit
    {
        public static Unit Value { get; } = new();
    }
}
