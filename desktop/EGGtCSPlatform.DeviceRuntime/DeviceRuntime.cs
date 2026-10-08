using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;

namespace EGGtCSPlatform.DeviceRuntime;

public interface IDeviceRuntime : IAsyncDisposable
{
    IDeviceManager Devices { get; }
    IDeviceDiscovery Discovery { get; }
    IDeviceConnector Connector { get; }
    IHeartbeatPauseService HeartbeatPause { get; }
    Task<IEggtCsDevice> ReconnectAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default
    );
    Task<IEggtCsDevice> ConnectVerifiedAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    );
    IAsyncEnumerable<DeviceCandidate> DiscoverModesAsync(
        IReadOnlyList<DiscoveryModeRequest> modes,
        CancellationToken cancellationToken = default
    );
}

public sealed class DeviceRuntimeBuilder
{
    private DeviceRuntimeOptions _options = new();
    private readonly List<ITransportFactory> _transports = [];
    private readonly List<IDeviceConnector> _connectors = [];
    private readonly List<IDeviceDiscovery> _discoveries = [];
    private readonly List<IDeviceProtocolModule> _protocols = [];

    public DeviceRuntimeBuilder WithOptions(DeviceRuntimeOptions options)
    {
        _options = options;
        return this;
    }

    public DeviceRuntimeBuilder UseSimulation()
    {
        _options = _options with { Backend = DeviceBackendProfile.Simulation() };
        return this;
    }

    public DeviceRuntimeBuilder AddTransport(ITransportFactory factory)
    {
        _transports.Add(factory);
        return this;
    }

    public DeviceRuntimeBuilder AddConnector(IDeviceConnector connector)
    {
        _connectors.Add(connector);
        return this;
    }

    public DeviceRuntimeBuilder AddDiscovery(IDeviceDiscovery discovery)
    {
        _discoveries.Add(discovery);
        return this;
    }

    public DeviceRuntimeBuilder AddProtocol(IDeviceProtocolModule module)
    {
        _protocols.Add(module);
        return this;
    }

    public DeviceRuntimeBuilder OnFailure(CommunicationFailureHandler handler)
    {
        _options = _options with { FailureHandler = handler };
        return this;
    }

    public IDeviceRuntime Build() =>
        new DeviceRuntime(
            _options,
            _transports.ToArray(),
            _connectors.ToArray(),
            _discoveries.ToArray(),
            _protocols.ToArray()
        );
}

internal sealed class DeviceRuntime : IDeviceRuntime, IDeviceDiscovery, ICommunicationFailureSink
{
    private sealed record ConnectionPlan(
        DeviceCandidate Candidate,
        IDeviceProtocolModule? Module,
        IDeviceConnector? Connector
    );

    private sealed class Registration(
        DeviceCandidate candidate,
        long generation,
        IDeviceProtocolModule? module
    )
    {
        public DeviceCandidate Candidate { get; } = candidate;
        public long Generation { get; } = generation;
        public IDeviceProtocolModule? Module { get; } = module;
        public Task? Validation;
        public TaskCompletionSource<IEggtCsDevice> Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Active = true;
        public int FaultReported;
        public int FailureReported;
    }

    private readonly DeviceRuntimeOptions _options;
    private readonly IReadOnlyList<IDeviceConnector> _connectors;
    private readonly IReadOnlyList<IDeviceDiscovery> _discoveries;
    private readonly IReadOnlyList<IDeviceProtocolModule> _protocols;
    private readonly ITransportFactory _transportFactory;
    private readonly SharedUdpHubRegistry _hubs;
    private readonly object _bindingGate = new();
    private readonly Dictionary<string, SharedUdpHubRegistry.UdpBindingLease> _ephemeralBindings =
    [];
    private readonly AsyncCommunicationDiagnostics _diagnostics;
    private readonly DeviceManager _manager;
    private readonly IDeviceManager _publicManager;
    private readonly ConcurrentDictionary<DeviceId, CancellationTokenSource> _reconnections = new();
    private readonly ConcurrentDictionary<DeviceId, Registration> _registrations = new();
    private readonly ConcurrentDictionary<string, Channel<CommunicationFailureContext>> _failures =
        new();
    private readonly object _failureGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _generation;
    private int _disposed;

    public DeviceRuntime(
        DeviceRuntimeOptions options,
        IReadOnlyList<ITransportFactory> transports,
        IReadOnlyList<IDeviceConnector> connectors,
        IReadOnlyList<IDeviceDiscovery> discoveries,
        IReadOnlyList<IDeviceProtocolModule> protocols
    )
    {
        options.Backend.Validate();
        if (options.FailureHandlerTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.FailureHandlerTimeout));
        if (
            options.SessionOptions.ReconnectPolicy is { Enabled: true } reconnect
            && (
                reconnect.InitialDelay <= TimeSpan.Zero
                || reconnect.MaximumDelay < reconnect.InitialDelay
            )
        )
            throw new ArgumentException(
                "Reconnect delays must be positive and ordered.",
                nameof(options)
            );
        _options = options with
        {
            Backend = new DeviceBackendProfile
            {
                ConnectionSource = options.Backend.ConnectionSource,
                Sources = new(options.Backend.Sources),
            },
        };
        if (
            protocols.Any(module =>
                string.IsNullOrWhiteSpace(module.ProtocolId)
                || module.ProtocolId == "simulator"
                || module.ProtocolId.StartsWith("connector:", StringComparison.Ordinal)
            )
            || protocols
                .Select(module => module.ProtocolId)
                .Distinct(StringComparer.Ordinal)
                .Count() != protocols.Count
        )
            throw new ArgumentException(
                "Protocol identifiers must be nonempty, unique and cannot use simulator or connector: reserved identities.",
                nameof(protocols)
            );
        _connectors = connectors;
        _discoveries = discoveries;
        _protocols = protocols;
        HeartbeatPause = options.HeartbeatPause ?? new HeartbeatPauseService();
        _diagnostics = new(options.Diagnostics, options.ByteTrafficLogger);
        _hubs = new(_diagnostics);
        _transportFactory = new RuntimeTransportFactory(
            transports,
            ResolveNetworkSettings,
            options.UdpMode,
            _hubs,
            _diagnostics
        );
        _manager = new DeviceManager(
            discoveries: [this],
            connectors: [new ResolvedConnector(this)]
        );
        _publicManager = new RuntimeManagerFacade(this, _manager);
    }

    public IDeviceManager Devices => _publicManager;
    public IDeviceDiscovery Discovery => this;
    public IDeviceConnector Connector => new ManagedConnector(this);

    private sealed class ManagedConnector(DeviceRuntime owner) : IDeviceConnector
    {
        public bool CanConnect(DeviceCandidate candidate) =>
            owner.CanResolve(candidate, resolveRevision: true);

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        ) => owner.Devices.ConnectAsync(candidate, cancellationToken);
    }

    public IHeartbeatPauseService HeartbeatPause { get; }

    private sealed class ResolvedConnector(DeviceRuntime owner) : IDeviceConnector
    {
        public bool CanConnect(DeviceCandidate candidate) =>
            owner.CanResolve(candidate, resolveRevision: false);

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        ) => owner.ConnectResolvedAsync(candidate, cancellationToken);
    }

    private bool CanResolve(DeviceCandidate candidate, bool resolveRevision)
    {
        try
        {
            ResolvePlan(candidate, resolveRevision);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private ConnectionPlan ResolvePlan(DeviceCandidate candidate, bool resolveRevision = true)
    {
        if (_options.Backend.ConnectionSource == DeviceConnectionSource.Simulated)
        {
            if (
                candidate.Endpoint.Scheme.Equals("simulator", StringComparison.OrdinalIgnoreCase)
                && (
                    candidate.ConnectionEndpoint is null
                    || candidate.ConnectionEndpoint.Scheme.Equals(
                        "simulator",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                && candidate.ProtocolId is null or "simulator"
            )
                return new(candidate with { ProtocolId = "simulator" }, null, null);
            throw new NotSupportedException(
                "Simulation mode only accepts simulator endpoints and protocol identity."
            );
        }
        var matches = new List<ConnectionPlan>();
        foreach (var module in _protocols)
        {
            if (candidate.ProtocolId is not null && candidate.ProtocolId != module.ProtocolId)
                continue;
            var selected = candidate;
            if (resolveRevision)
            {
                var revision = module.ResolveRevision(candidate);
                selected = candidate with
                {
                    ProtocolVersion = revision.Revision,
                    RevisionSource = revision.Source,
                };
            }
            if (
                module.SupportedRevisions.Contains(selected.ProtocolVersion)
                && module.CanConnect(selected)
                && _transportFactory.CanCreate(selected.ConnectionEndpoint ?? selected.Endpoint)
            )
                matches.Add(new(selected with { ProtocolId = module.ProtocolId }, module, null));
        }
        for (var i = 0; i < _connectors.Count; i++)
        {
            var id = $"connector:{i}";
            if (
                (candidate.ProtocolId is null || candidate.ProtocolId == id)
                && _connectors[i].CanConnect(candidate with { ProtocolId = null })
            )
                matches.Add(new(candidate with { ProtocolId = id }, null, _connectors[i]));
        }
        return matches.Count switch
        {
            0 => throw new NotSupportedException(
                $"No registered protocol supports {candidate.ProtocolId ?? "(unspecified)"}/{candidate.ProtocolVersion}."
            ),
            1 => matches[0],
            _ => throw new AmbiguousDeviceProtocolException(
                "Multiple protocols support this candidate. Specify ProtocolId explicitly."
            ),
        };
    }

    private async Task<IEggtCsDevice> ConnectResolvedAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // The public manager resolves once. Internal manager calls and reconnects consume that snapshot.
        var plan = ResolvePlan(candidate, resolveRevision: false);
        candidate = plan.Candidate;
        var registration = new Registration(
            candidate,
            Interlocked.Increment(ref _generation),
            plan.Module
        );
        _registrations[candidate.Identity.DeviceId] = registration;
        IEggtCsDevice? device = null;
        IEggtCsDevice? exposedDevice = null;
        try
        {
            var custom = plan.Connector;
            var connectorCandidate = candidate with { ProtocolId = null };
            if (custom is IContextualDeviceConnector contextual)
                device = await contextual
                    .ConnectAsync(
                        connectorCandidate,
                        SessionOptions(registration),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            else if (custom is not null)
                device = await custom
                    .ConnectAsync(connectorCandidate, cancellationToken)
                    .ConfigureAwait(false);
            else if (plan.Module is { } module)
            {
                var heartbeat = new ProtocolHeartbeatSettings(
                    _options.HeartbeatEnabled,
                    _options.HeartbeatInterval,
                    _options.HeartbeatFailureTimeout,
                    () =>
                        IsCurrent(registration)
                        && !HeartbeatPause.IsPaused(candidate.Identity.DeviceId),
                    status =>
                    {
                        if (IsCurrent(registration) && exposedDevice is not null)
                            _options.HeartbeatReceived?.Invoke(exposedDevice, status);
                    }
                );
                device = await module
                    .ConnectAsync(
                        new(
                            candidate,
                            _transportFactory,
                            SessionOptions(registration),
                            Enum.GetValues<DeviceCapabilityKind>()
                                .Where(kind =>
                                    _options.Backend.GetSource(kind) == DeviceCapabilitySource.Real
                                )
                                .ToHashSet(),
                            heartbeat,
                            (identity, error) =>
                            {
                                if (IsCurrent(registration))
                                    _options.SessionFaulted?.Invoke(identity, error);
                            }
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            else
                device = new SimulatedEggtCsDevice(candidate.Identity);
            // Modules provide physical devices; every module uses the same capability mixing and lifecycle.
            if (plan.Module is not null)
                device = new MixedSourceDevice(candidate.Identity, _options.Backend, device);
            else if (custom is null)
                device = new MixedSourceDevice(
                    candidate.Identity,
                    _options.Backend,
                    simulation: (SimulatedEggtCsDevice)device
                );
            if (_options.ValidateConnection)
                await VerifyAsync(registration, device, cancellationToken).ConfigureAwait(false);
            device = new RuntimeDevice(
                device,
                HeartbeatPause,
                _options.PauseHeartbeatDuringOperations,
                () => registration.Active = false,
                candidate.ProtocolId!,
                candidate.ProtocolVersion,
                plan.Module?.GetSupportedCommands(candidate.ProtocolVersion),
                candidate.RevisionSource
            );
            exposedDevice = device;
            registration.Ready.TrySetResult(device);
            return device;
        }
        catch (Exception error)
        {
            registration.Active = false;
            try
            {
                if (device is not null)
                    await device.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                registration.Ready.TrySetCanceled();
                if (
                    error is not OperationCanceledException
                    && Volatile.Read(ref registration.FailureReported) == 0
                )
                    ReportFailure(
                        new(
                            candidate.Identity.DeviceId.Value,
                            Guid.Empty,
                            registration.Generation,
                            "Connection",
                            error,
                            null,
                            false,
                            false,
                            false,
                            ConnectionFailureDecisions
                        )
                    );
            }
            throw;
        }
    }

    private static readonly IReadOnlySet<CommunicationFailureDecision> ConnectionFailureDecisions =
        new HashSet<CommunicationFailureDecision>
        {
            CommunicationFailureDecision.UseDefault,
            CommunicationFailureDecision.FailCurrentOperation,
        };

    private DeviceSessionOptions SessionOptions(Registration registration) =>
        _options.SessionOptions with
        {
            DeviceId = registration.Candidate.Identity.DeviceId.Value,
            ProtocolId = registration.Candidate.ProtocolId,
            ProtocolRevision = registration.Candidate.ProtocolVersion,
            ConnectionGeneration = registration.Generation,
            FailureSink = this,
            Diagnostics = _diagnostics,
            TimeProvider = _options.TimeProvider,
        };

    private Task VerifyAsync(
        Registration registration,
        IEggtCsDevice device,
        CancellationToken cancellationToken
    )
    {
        lock (registration)
            return registration.Validation ??= VerifyCoreAsync();
        async Task VerifyCoreAsync()
        {
            var status = registration.Module is { } module
                ? await module
                    .ValidateConnectionAsync(device, cancellationToken)
                    .ConfigureAwait(false)
                : await device.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (
                status.Status != DeviceCommandStatus.Success
                || device.State.Connection != DeviceConnectionState.Connected
            )
                throw new DeviceConnectionValidationException(status.Status);
        }
    }

    public async Task<IEggtCsDevice> ConnectVerifiedAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    )
    {
        var device = await Devices.ConnectAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (
            !_registrations.TryGetValue(device.Identity.DeviceId, out var registration)
            || !IsCurrent(registration)
            || !registration.Ready.Task.IsCompletedSuccessfully
            || !ReferenceEquals(registration.Ready.Task.Result, device)
        )
            throw new DeviceDisconnectedException(Guid.Empty);
        try
        {
            await VerifyAsync(registration, device, cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _manager.RemoveIfCurrentAsync(device).ConfigureAwait(false);
            throw;
        }
        return device;
    }

    public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_options.Backend.ConnectionSource == DeviceConnectionSource.Simulated)
        {
            await foreach (
                var candidate in new SimulatedDeviceDiscovery()
                    .DiscoverAsync(window, cancellationToken)
                    .ConfigureAwait(false)
            )
                yield return candidate with
                {
                    ProtocolId = "simulator",
                };
            yield break;
        }
        if (
            _options.Backend.ConnectionSource == DeviceConnectionSource.Real
            && _options.DiscoveryModes is { } modes
        )
        {
            await foreach (
                var candidate in DiscoverModesAsync(modes, cancellationToken).ConfigureAwait(false)
            )
                yield return candidate;
            yield break;
        }
        var seen = new HashSet<(DeviceId, string?, string)>();
        foreach (var discovery in _discoveries)
        await foreach (
            var candidate in discovery
                .DiscoverAsync(window, cancellationToken)
                .ConfigureAwait(false)
        )
            if (
                seen.Add(
                    (candidate.Identity.DeviceId, candidate.ProtocolId, candidate.ProtocolVersion)
                )
            )
                yield return candidate;
        foreach (var module in _protocols)
        {
            var network = ResolveNetworkSettings();
            var transport = new UdpTargetedDiscoveryTransport(
                network.LocalAddress,
                network.LocalPort,
                UdpDiscoveryTargets.Legacy(network),
                _options.UdpMode == UdpConnectionMode.Shared ? _hubs : null,
                _diagnostics
            );
            await foreach (
                var candidate in module
                    .CreateDiscovery(transport, network.LocalPort)
                    .DiscoverAsync(window, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                var identified = IdentifyDiscovery(module, candidate);
                if (
                    seen.Add(
                        (
                            identified.Identity.DeviceId,
                            identified.ProtocolId,
                            identified.ProtocolVersion
                        )
                    )
                )
                    yield return identified;
            }
        }
    }

    internal static DeviceCandidate IdentifyDiscovery(
        IDeviceProtocolModule module,
        DeviceCandidate candidate
    )
    {
        if (candidate.ProtocolId is not null && candidate.ProtocolId != module.ProtocolId)
            throw new DeviceProtocolConflictException(
                "A discovery provider returned a different protocol identity."
            );
        return candidate with { ProtocolId = module.ProtocolId };
    }

    public async Task<IEggtCsDevice> ReconnectAsync(
        DeviceId id,
        CancellationToken cancellationToken = default
    )
    {
        using var reconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token
        );
        CancelReconnect(id);
        _reconnections[id] = reconnectCancellation;
        cancellationToken = reconnectCancellation.Token;
        try
        {
            if (!_registrations.TryGetValue(id, out var registration))
                throw new KeyNotFoundException($"Unknown device {id}.");
            var expected = await registration
                .Ready.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!IsCurrent(registration))
                throw new DeviceDisconnectedException(Guid.Empty);
            if (
                !await _manager
                    .RemoveIfCurrentAsync(expected, cancellationToken)
                    .ConfigureAwait(false)
            )
                throw new InvalidOperationException(
                    "The connection was replaced before reconnect could start."
                );
            var delay = _options.SessionOptions.ReconnectPolicy.InitialDelay;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await _manager
                        .ConnectAsync(registration.Candidate, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception error)
                    when (error is not OperationCanceledException
                        && _options.SessionOptions.ReconnectPolicy.Enabled
                    )
                {
                    Report(registration.Candidate, Guid.Empty, "Reconnect", error);
                    try
                    {
                        await foreach (
                            var found in DiscoverAsync(
                                    TimeSpan.FromMilliseconds(500),
                                    cancellationToken
                                )
                                .ConfigureAwait(false)
                        )
                        {
                            if (
                                found.Identity.DeviceId != id
                                || found.ProtocolId != registration.Candidate.ProtocolId
                            )
                                continue;
                            if (
                                found.ProtocolVersion != registration.Candidate.ProtocolVersion
                                && found.RevisionSource
                                    is not (
                                        ProtocolRevisionSource.SoftwareDefault
                                        or ProtocolRevisionSource.CompatibilityAssumption
                                    )
                            )
                                continue;
                            // Discovery may reflect a new default. Only replace the endpoint, never the selected rules.
                            var pinned = found with
                            {
                                ProtocolVersion = registration.Candidate.ProtocolVersion,
                                RevisionSource = registration.Candidate.RevisionSource,
                            };
                            try
                            {
                                return await _manager
                                    .ConnectAsync(pinned, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception candidateError)
                                when (candidateError is not OperationCanceledException)
                            {
                                Report(
                                    registration.Candidate,
                                    Guid.Empty,
                                    "ReconnectCandidate",
                                    candidateError
                                );
                            }
                        }
                    }
                    catch (Exception discoveryError)
                        when (discoveryError is not OperationCanceledException)
                    {
                        Report(
                            registration.Candidate,
                            Guid.Empty,
                            "ReconnectDiscovery",
                            discoveryError
                        );
                    }
                    await Task.Delay(delay, _options.TimeProvider, cancellationToken)
                        .ConfigureAwait(false);
                    delay = TimeSpan.FromTicks(
                        (long)
                            Math.Min(
                                (double)delay.Ticks * 2,
                                _options.SessionOptions.ReconnectPolicy.MaximumDelay.Ticks
                            )
                    );
                }
            }
        }
        finally
        {
            ((ICollection<KeyValuePair<DeviceId, CancellationTokenSource>>)_reconnections).Remove(
                new(id, reconnectCancellation)
            );
        }
    }

    private void Report(
        DeviceCandidate candidate,
        Guid sessionId,
        string category,
        Exception error
    ) =>
        _diagnostics.Report(
            new(sessionId, _options.TimeProvider.GetUtcNow(), category, error.Message, error)
            {
                ProtocolId = candidate.ProtocolId,
                ProtocolRevision = candidate.ProtocolVersion,
            }
        );

    private void CancelReconnect(DeviceId id)
    {
        if (_reconnections.TryRemove(id, out var cancellation))
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                _diagnostics.Report(
                    new(
                        Guid.Empty,
                        _options.TimeProvider.GetUtcNow(),
                        "ReconnectCancellation",
                        error.Message,
                        error
                    )
                );
            }
    }

    private sealed class RuntimeManagerFacade(DeviceRuntime owner, DeviceManager manager)
        : IDeviceManager
    {
        public IReadOnlyCollection<IEggtCsDevice> Devices => manager.Devices;

        public IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            TimeSpan window,
            CancellationToken cancellationToken = default
        ) => owner.DiscoverAsync(window, cancellationToken);

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        )
        {
            var selected = owner.ResolvePlan(candidate).Candidate;
            owner.CancelReconnect(candidate.Identity.DeviceId);
            return manager.ConnectAsync(selected, cancellationToken);
        }

        public bool TryGet(DeviceId deviceId, out IEggtCsDevice? device) =>
            manager.TryGet(deviceId, out device);

        public Task DisconnectAsync(
            DeviceId deviceId,
            CancellationToken cancellationToken = default
        )
        {
            owner.CancelReconnect(deviceId);
            return manager.DisconnectAsync(deviceId, cancellationToken);
        }

        public Task RemoveAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            owner.CancelReconnect(deviceId);
            return manager.RemoveAsync(deviceId, cancellationToken);
        }

        public Task<bool> RemoveIfCurrentAsync(
            IEggtCsDevice expected,
            CancellationToken cancellationToken = default
        ) => manager.RemoveIfCurrentAsync(expected, cancellationToken);

        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }

    public async IAsyncEnumerable<DeviceCandidate> DiscoverModesAsync(
        IReadOnlyList<DiscoveryModeRequest> modes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_options.Backend.ConnectionSource == DeviceConnectionSource.Simulated)
        {
            await foreach (
                var candidate in DiscoverAsync(TimeSpan.Zero, cancellationToken)
                    .ConfigureAwait(false)
            )
                yield return candidate;
            yield break;
        }
        var coordinator = new DeviceDiscoveryCoordinator(
            ResolveNetworkSettings,
            _protocols,
            _diagnostics,
            _options.UdpMode == UdpConnectionMode.Shared ? _hubs : null,
            _options.DiscoveryNetworkResolver
        );
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var seen = new HashSet<(DeviceId, string?, string)>();
        foreach (var mode in modes)
        {
            foreach (var discovery in _discoveries)
            await foreach (
                var candidate in discovery
                    .DiscoverAsync(mode.ResponseTimeout, cancellationToken)
                    .ConfigureAwait(false)
            )
                if (
                    seen.Add(
                        (
                            candidate.Identity.DeviceId,
                            candidate.ProtocolId,
                            candidate.ProtocolVersion
                        )
                    )
                )
                    yield return candidate;
            IAsyncEnumerator<DeviceCandidate>? reader = null;
            try
            {
                reader = coordinator
                    .DiscoverAsync(mode, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _diagnostics.Report(
                    new(
                        Guid.Empty,
                        _options.TimeProvider.GetUtcNow(),
                        "Discovery",
                        error.Message,
                        error
                    )
                );
            }
            if (reader is null)
                continue;
            await using (reader)
            {
                while (true)
                {
                    bool next;
                    try
                    {
                        next = await reader.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        _diagnostics.Report(
                            new(
                                Guid.Empty,
                                _options.TimeProvider.GetUtcNow(),
                                "Discovery",
                                error.Message,
                                error
                            )
                        );
                        break;
                    }
                    if (!next)
                        break;
                    if (
                        seen.Add(
                            (
                                reader.Current.Identity.DeviceId,
                                reader.Current.ProtocolId,
                                reader.Current.ProtocolVersion
                            )
                        )
                    )
                        yield return reader.Current;
                }
            }
        }
    }

    private bool IsCurrent(Registration registration) =>
        registration.Active
        && !_lifetime.IsCancellationRequested
        && _registrations.TryGetValue(registration.Candidate.Identity.DeviceId, out var current)
        && ReferenceEquals(current, registration);

    public void ReportFailure(CommunicationFailureContext context)
    {
        if (_lifetime.IsCancellationRequested || context.DeviceId is null)
            return;
        if (
            !_registrations.TryGetValue(new(context.DeviceId), out var registration)
            || registration.Generation != context.ConnectionGeneration
        )
            return;
        context = context with
        {
            ProtocolId = registration.Candidate.ProtocolId,
            ProtocolRevision = registration.Candidate.ProtocolVersion,
        };
        if (
            context.Category == "Session"
            && Interlocked.Exchange(ref registration.FaultReported, 1) != 0
        )
            return;
        Interlocked.Exchange(ref registration.FailureReported, 1);
        Channel<CommunicationFailureContext> queue;
        lock (_failureGate)
        {
            queue = _failures.GetOrAdd(
                context.DeviceId,
                key =>
                {
                    var channel = Channel.CreateUnbounded<CommunicationFailureContext>(
                        new() { SingleReader = true, AllowSynchronousContinuations = false }
                    );
                    _ = Task.Run(() => ProcessFailuresAsync(channel));
                    return channel;
                }
            );
        }
        queue.Writer.TryWrite(context);
    }

    private async Task ProcessFailuresAsync(Channel<CommunicationFailureContext> queue)
    {
        try
        {
            await foreach (
                var reported in queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false)
            )
            {
                var context = reported;
                if (
                    !_registrations.TryGetValue(new(context.DeviceId!), out var registration)
                    || registration.Generation != context.ConnectionGeneration
                )
                    continue;
                try
                {
                    await registration.Ready.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!_lifetime.IsCancellationRequested && registration.Ready.Task.IsCanceled)
                {
                    context = context with { AllowedDecisions = ConnectionFailureDecisions };
                }
                if (
                    !_registrations.TryGetValue(
                        registration.Candidate.Identity.DeviceId,
                        out var latest
                    ) || !ReferenceEquals(registration, latest)
                )
                    continue;
                if (!IsCurrent(registration) && !registration.Ready.Task.IsCanceled)
                    continue;
                var decision = CommunicationFailureDecision.UseDefault;
                if (_options.FailureHandler is { } handler)
                {
                    using var callbackCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    Task<CommunicationFailureDecision>? callback = null;
                    try
                    {
                        callback = Task.Run(async () =>
                            await handler(context, callbackCancellation.Token).ConfigureAwait(false)
                        );
                        decision = await callback
                            .WaitAsync(
                                _options.FailureHandlerTimeout,
                                _options.TimeProvider,
                                _lifetime.Token
                            )
                            .ConfigureAwait(false);
                        if (!context.AllowedDecisions.Contains(decision))
                            throw new InvalidOperationException(
                                "The failure handler returned an unavailable decision."
                            );
                    }
                    catch (Exception error)
                    {
                        decision = CommunicationFailureDecision.UseDefault;
                        _diagnostics.Report(
                            new(
                                context.SessionId,
                                _options.TimeProvider.GetUtcNow(),
                                "FailureHandler",
                                error.Message,
                                error
                            )
                            {
                                ProtocolId = context.ProtocolId,
                                ProtocolRevision = context.ProtocolRevision,
                            }
                        );
                    }
                    finally
                    {
                        try
                        {
                            callbackCancellation.Cancel();
                        }
                        catch (Exception error)
                        {
                            _diagnostics.Report(
                                new(
                                    context.SessionId,
                                    _options.TimeProvider.GetUtcNow(),
                                    "FailureHandlerCancellation",
                                    error.Message,
                                    error
                                )
                                {
                                    ProtocolId = context.ProtocolId,
                                    ProtocolRevision = context.ProtocolRevision,
                                }
                            );
                        }
                        if (callback is not null)
                            _ = callback.ContinueWith(
                                task => _ = task.Exception,
                                CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted,
                                TaskScheduler.Default
                            );
                    }
                }
                if (
                    (
                        decision == CommunicationFailureDecision.ReconnectDevice
                        || decision == CommunicationFailureDecision.UseDefault
                            && context.Category == "Session"
                            && _options.SessionOptions.ReconnectPolicy.Enabled
                    ) && IsCurrent(registration)
                )
                {
                    try
                    {
                        await ReconnectAsync(
                                registration.Candidate.Identity.DeviceId,
                                _lifetime.Token
                            )
                            .ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        _diagnostics.Report(
                            new(
                                context.SessionId,
                                _options.TimeProvider.GetUtcNow(),
                                "Reconnect",
                                error.Message,
                                error
                            )
                            {
                                ProtocolId = context.ProtocolId,
                                ProtocolRevision = context.ProtocolRevision,
                            }
                        );
                    }
                }
                // Requests have already failed and been removed before either default/fail decision.
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private UdpNetworkSettings ResolveNetworkSettings()
    {
        var network = _options.NetworkSettings();
        if (_options.UdpMode != UdpConnectionMode.Shared || network.LocalPort != 0)
            return network;
        lock (_bindingGate)
        {
            if (!_ephemeralBindings.TryGetValue(network.LocalAddress, out var binding))
            {
                binding = _hubs.Bind(network.LocalAddress);
                _ephemeralBindings.Add(network.LocalAddress, binding);
            }
            return network with { LocalPort = binding.LocalPort };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            _lifetime.Cancel();
        }
        catch (Exception error)
        {
            _diagnostics.Report(
                new(
                    Guid.Empty,
                    _options.TimeProvider.GetUtcNow(),
                    "RuntimeCancellation",
                    error.Message,
                    error
                )
            );
        }
        foreach (var queue in _failures.Values)
            queue.Writer.TryComplete();
        try
        {
            await _manager.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                SharedUdpHubRegistry.UdpBindingLease[] bindings;
                lock (_bindingGate)
                {
                    bindings = _ephemeralBindings.Values.ToArray();
                    _ephemeralBindings.Clear();
                }
                foreach (var binding in bindings)
                    await binding.DisposeAsync().ConfigureAwait(false);
                await _hubs.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _diagnostics.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

internal sealed class RuntimeTransportFactory(
    IReadOnlyList<ITransportFactory> factories,
    Func<UdpNetworkSettings> settings,
    UdpConnectionMode mode,
    SharedUdpHubRegistry hubs,
    IByteTrafficLogger logger
) : ITransportFactory
{
    public bool CanCreate(ITransportEndpoint endpoint) =>
        factories.Any(factory => factory.CanCreate(endpoint))
        || endpoint.Scheme.Equals("udp", StringComparison.OrdinalIgnoreCase)
            && endpoint is TransportEndpoint or UdpEndpoint;

    public ITransport Create(TransportEndpoint endpoint) => Create((ITransportEndpoint)endpoint);

    public ITransport Create(ITransportEndpoint endpoint)
    {
        var registered = factories.FirstOrDefault(factory => factory.CanCreate(endpoint));
        if (registered is not null)
            return registered.Create(endpoint);
        var network = settings();
        var udp =
            endpoint as UdpEndpoint
            ?? (
                endpoint is TransportEndpoint legacy && legacy.Scheme == "udp"
                    ? new UdpEndpoint(
                        legacy.Address,
                        legacy.Port,
                        network.LocalAddress,
                        network.LocalPort
                    )
                    : throw new NotSupportedException("Unsupported transport endpoint.")
            );
        return mode == UdpConnectionMode.Shared
            ? hubs.CreateTransport(udp)
            : new UdpTransport(
                new(udp.RemoteAddress, udp.RemotePort, udp.LocalAddress, udp.LocalPort),
                logger
            );
    }
}
