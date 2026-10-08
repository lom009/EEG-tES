using System.Runtime.CompilerServices;

namespace EGGtCSPlatform.DeviceSdk;

public sealed class DeviceEventHub(int subscriberCapacity)
{
    private readonly object _gate = new();
    private readonly HashSet<DeviceEventSubscription> _subscribers = [];
    private Exception? _failure;
    private bool _completed;

    public DeviceEventSubscription Subscribe(DeviceEventSubscriptionOptions? options = null)
    {
        DeviceEventSubscription? subscription = null;
        subscription = new DeviceEventSubscription(
            options
                ?? new()
                {
                    ControlCapacity = subscriberCapacity,
                    DataCapacity = subscriberCapacity,
                },
            () =>
            {
                lock (_gate)
                    _subscribers.Remove(subscription!);
            }
        );
        lock (_gate)
        {
            if (_completed)
                subscription.Complete(_failure);
            else
                _subscribers.Add(subscription);
        }
        return subscription;
    }

    public ValueTask PublishAsync(
        DeviceEventEnvelope envelope,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeviceEventSubscription[] subscriptions;
        lock (_gate)
            subscriptions = _subscribers.ToArray();
        foreach (var subscription in subscriptions)
            subscription.Publish(envelope);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<DeviceEventEnvelope> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await using var subscription = Subscribe();
        await foreach (
            var item in subscription.ReadEventsAsync(cancellationToken).ConfigureAwait(false)
        )
            yield return item;
    }

    public void Complete(Exception? exception = null)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            _failure = exception;
            foreach (var subscription in _subscribers)
                subscription.Complete(exception);
            _subscribers.Clear();
        }
    }
}
