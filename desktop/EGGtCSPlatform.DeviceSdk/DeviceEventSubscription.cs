using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk;

public enum DeviceDataDeliveryPolicy
{
    Reliable,
    Latest,
}

public sealed record DeviceEventSubscriptionOptions
{
    public int ControlCapacity { get; init; } = 256;
    public int DataCapacity { get; init; } = 512;
    public DeviceDataDeliveryPolicy DataPolicy { get; init; } = DeviceDataDeliveryPolicy.Reliable;
    public Func<DeviceEventEnvelope, bool>? Filter { get; init; }
}

public sealed class DeviceSubscriptionOverflowException(EventDeliveryClass deliveryClass)
    : CommunicationException(
        $"The subscriber's {deliveryClass} queue is full. This subscription was stopped without silently losing data."
    );

/// <summary>Registration is immediate. Ready completes before a device command may be sent.</summary>
public sealed class DeviceEventSubscription : IAsyncDisposable
{
    private sealed record QueuedEvent(long Sequence, DeviceEventEnvelope Envelope);

    private readonly Channel<QueuedEvent> _control;
    private readonly Channel<QueuedEvent> _data;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        }
    );
    private readonly DeviceEventSubscriptionOptions _options;
    private readonly object _gate = new();
    private readonly Action? _unsubscribe;
    private Exception? _failure;
    private bool _completed;
    private long _dropped;
    private int _reading;
    private long _sequence;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    internal DeviceEventSubscription(
        DeviceEventSubscriptionOptions options,
        Action? unsubscribe = null,
        bool ready = true
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ControlCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.DataCapacity);
        _options = options;
        _unsubscribe = unsubscribe;
        _control = Create(options.ControlCapacity);
        _data = Create(options.DataCapacity);
        if (ready)
            _ready.TrySetResult();
    }

    public Task Ready => _ready.Task;
    public long DroppedDataCount => Interlocked.Read(ref _dropped);
    internal CancellationToken Lifetime => _lifetime.Token;

    internal void MarkReady() => _ready.TrySetResult();

    internal void Publish(DeviceEventEnvelope envelope)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            try
            {
                if (_options.Filter is not null && !_options.Filter(envelope))
                    return;
            }
            catch (Exception exception)
            {
                Complete(exception);
                return;
            }
            var channel = envelope.DeliveryClass == EventDeliveryClass.Data ? _data : _control;
            var queued = new QueuedEvent(++_sequence, envelope);
            if (!channel.Writer.TryWrite(queued))
            {
                if (
                    envelope.DeliveryClass == EventDeliveryClass.Data
                    && _options.DataPolicy == DeviceDataDeliveryPolicy.Latest
                )
                {
                    if (channel.Reader.TryRead(out _))
                        Interlocked.Increment(ref _dropped);
                    channel.Writer.TryWrite(queued);
                }
                else
                {
                    Complete(new DeviceSubscriptionOverflowException(envelope.DeliveryClass));
                    return;
                }
            }
            _wake.Writer.TryWrite(true);
        }
    }

    internal void Complete(Exception? failure = null)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            _failure = failure;
            _control.Writer.TryComplete();
            _data.Writer.TryComplete();
            _wake.Writer.TryComplete();
            _ready.TrySetResult();
        }
    }

    public async IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0)
            throw new InvalidOperationException("A subscription has a single reader.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token
        );
        while (true)
        {
            DeviceEventEnvelope? item;
            lock (_gate)
            {
                if (_failure is not null)
                    throw _failure;
                _control.Reader.TryPeek(out var control);
                _data.Reader.TryPeek(out var data);
                var reader =
                    control is not null && (data is null || control.Sequence < data.Sequence)
                        ? _control.Reader
                        : _data.Reader;
                item = reader.TryRead(out var queued) ? queued.Envelope : null;
                if (item is null && _completed)
                    yield break;
            }
            if (item is not null)
            {
                yield return item;
                continue;
            }
            if (await _wake.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                _wake.Reader.TryRead(out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        _unsubscribe?.Invoke();
        Complete();
        _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }

    private static Channel<QueuedEvent> Create(int capacity) =>
        Channel.CreateBounded<QueuedEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            }
        );

    public static DeviceEventSubscription FromStream(
        IAsyncEnumerable<DeviceEventEnvelope> stream,
        DeviceEventSubscriptionOptions? options = null
    )
    {
        var subscription = new DeviceEventSubscription(options ?? new(), ready: false);
        _ = PumpAsync();
        return subscription;
        async Task PumpAsync()
        {
            try
            {
                await using var reader = stream.GetAsyncEnumerator(subscription.Lifetime);
                var next = reader.MoveNextAsync();
                subscription.MarkReady();
                while (await next.ConfigureAwait(false))
                {
                    subscription.Publish(reader.Current);
                    next = reader.MoveNextAsync();
                }
                subscription.Complete();
            }
            catch (OperationCanceledException) when (subscription.Lifetime.IsCancellationRequested)
            {
                subscription.Complete();
            }
            catch (Exception exception)
            {
                subscription.Complete(exception);
            }
        }
    }
}
