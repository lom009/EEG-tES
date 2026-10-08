using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace EGGtCSPlatform.DeviceSdk;

public interface IDeviceDiscovery
{
    IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default
    );
}

public interface IDeviceConnector
{
    bool CanConnect(DeviceCandidate candidate);

    Task<IEggtCsDevice> ConnectAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    );
}

public interface IDeviceManager : IAsyncDisposable
{
    IReadOnlyCollection<IEggtCsDevice> Devices { get; }

    IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default
    );

    Task<IEggtCsDevice> ConnectAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    );

    bool TryGet(DeviceId deviceId, out IEggtCsDevice? device);

    Task DisconnectAsync(DeviceId deviceId, CancellationToken cancellationToken = default);

    Task RemoveAsync(DeviceId deviceId, CancellationToken cancellationToken = default);

    // Implementations should override to perform this comparison under their per-device gate.
    async Task<bool> RemoveIfCurrentAsync(
        IEggtCsDevice expected,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !TryGet(expected.Identity.DeviceId, out var current)
            || !ReferenceEquals(expected, current)
        )
            return false;
        await RemoveAsync(expected.Identity.DeviceId, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>An optional connector contract for runtime-provided session identity, clock and failure handling.</summary>
public interface IContextualDeviceConnector : IDeviceConnector
{
    Task<IEggtCsDevice> ConnectAsync(
        DeviceCandidate candidate,
        EGGtCSPlatform.Communication.Core.DeviceSessionOptions sessionOptions,
        CancellationToken cancellationToken = default
    );
}

public sealed class DeviceManager : IDeviceManager
{
    private readonly IReadOnlyList<IDeviceDiscovery> _discoveries;
    private readonly IReadOnlyList<IDeviceConnector> _connectors;
    private readonly ConcurrentDictionary<DeviceId, IEggtCsDevice> _devices = new();
    private readonly ConcurrentDictionary<DeviceId, DeviceCandidate> _candidates = new();
    private readonly ConcurrentDictionary<DeviceId, SemaphoreSlim> _gates = new();
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public DeviceManager(
        IEnumerable<IDeviceDiscovery>? discoveries = null,
        IEnumerable<IDeviceConnector>? connectors = null,
        IEnumerable<IEggtCsDevice>? connectedDevices = null
    )
    {
        _discoveries = discoveries?.ToArray() ?? [];
        _connectors = connectors?.ToArray() ?? [];
        foreach (var device in connectedDevices ?? [])
            _devices.TryAdd(device.Identity.DeviceId, device);
    }

    public IReadOnlyCollection<IEggtCsDevice> Devices => _devices.Values.ToArray();

    public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var seen = new HashSet<DeviceId>();
        foreach (var discovery in _discoveries)
        await foreach (
            var candidate in discovery
                .DiscoverAsync(window, cancellationToken)
                .ConfigureAwait(false)
        )
            if (seen.Add(candidate.Identity.DeviceId))
                yield return candidate;
    }

    private SemaphoreSlim GetGate(DeviceId id)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _gates.GetOrAdd(id, _ => new(1, 1));
        }
    }

    public async Task<IEggtCsDevice> ConnectAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    )
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token
        );
        cancellationToken = linked.Token;
        var gate = GetGate(candidate.Identity.DeviceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_devices.TryGetValue(candidate.Identity.DeviceId, out var existing))
            {
                if (existing.State.Connection == DeviceConnectionState.Connected)
                {
                    var binding = existing as IDeviceProtocolBinding;
                    _candidates.TryGetValue(candidate.Identity.DeviceId, out var prior);
                    var protocolId = binding?.ProtocolId ?? prior?.ProtocolId;
                    var revision = binding?.ProtocolRevision ?? prior?.ProtocolVersion;
                    if (
                        candidate.ProtocolId is not null
                            && protocolId is not null
                            && protocolId != candidate.ProtocolId
                        || revision is not null && revision != candidate.ProtocolVersion
                    )
                        throw new DeviceProtocolConflictException(
                            $"Device {candidate.Identity.DeviceId} already uses {protocolId}/{revision}."
                        );
                    return existing;
                }
                _devices.TryRemove(candidate.Identity.DeviceId, out _);
                _candidates.TryRemove(candidate.Identity.DeviceId, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }
            var connector =
                _connectors.FirstOrDefault(item => item.CanConnect(candidate))
                ?? throw new NotSupportedException(
                    $"No connector supports {candidate.Endpoint.Scheme}/{candidate.ProtocolVersion}."
                );
            var device = await connector
                .ConnectAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (device.Identity.DeviceId != candidate.Identity.DeviceId)
            {
                await device.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The connector changed the candidate DeviceId."
                );
            }
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                await device.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(DeviceManager));
            }
            _devices[candidate.Identity.DeviceId] = device;
            _candidates[candidate.Identity.DeviceId] = candidate;
            return device;
        }
        finally
        {
            gate.Release();
        }
    }

    public bool TryGet(DeviceId deviceId, out IEggtCsDevice? device) =>
        _devices.TryGetValue(deviceId, out device);

    public async Task DisconnectAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default
    )
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token
        );
        cancellationToken = linked.Token;
        var gate = GetGate(deviceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_devices.TryGetValue(deviceId, out var device))
                await device.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task RemoveAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
        RemoveCoreAsync(deviceId, null, cancellationToken);

    // Identity comparison prevents a stale callback from removing a replacement connection.
    public async Task<bool> RemoveIfCurrentAsync(
        IEggtCsDevice expected,
        CancellationToken cancellationToken = default
    ) =>
        await RemoveCoreAsync(expected.Identity.DeviceId, expected, cancellationToken)
            .ConfigureAwait(false);

    private async Task<bool> RemoveCoreAsync(
        DeviceId id,
        IEggtCsDevice? expected,
        CancellationToken cancellationToken
    )
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token
        );
        cancellationToken = linked.Token;
        var gate = GetGate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (
                !_devices.TryGetValue(id, out var device)
                || expected is not null && !ReferenceEquals(expected, device)
            )
                return false;
            _devices.TryRemove(id, out _);
            _candidates.TryRemove(id, out _);
            try
            {
                await device.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await device.DisposeAsync().ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        SemaphoreSlim[] gates;
        lock (_lifetimeGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            gates = _gates.Values.ToArray();
        }
        var errors = new List<Exception>();
        try
        {
            _lifetime.Cancel();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        foreach (var gate in gates)
            await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var device in _devices.Values)
                try
                {
                    await device.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            _devices.Clear();
            _candidates.Clear();
        }
        finally
        {
            foreach (var gate in gates)
                gate.Release();
        }
        if (errors.Count > 0)
            throw new AggregateException("One or more devices failed to dispose.", errors);
    }
}
