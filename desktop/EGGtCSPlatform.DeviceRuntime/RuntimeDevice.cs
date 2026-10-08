using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.DeviceRuntime;

internal sealed class RuntimeDevice
    : IEggtCsDevice,
        IDeviceCapabilitySourceProfile,
        IDeviceProtocolBinding
{
    private readonly IEggtCsDevice _inner;
    private readonly IHeartbeatPauseService _pause;
    private readonly bool _automaticPause;
    private readonly Action _deactivate;
    private readonly object _gate = new();
    private readonly Dictionary<string, IDisposable> _operations = [];
    private readonly Dictionary<string, long> _operationEpochs = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DeviceEventSubscription? _subscription;
    private readonly Task _pump = Task.CompletedTask;
    private int _disposed;
    private readonly IReadOnlySet<Type>? _supportedCommands;
    public DeviceProtocolInfo ProtocolInfo { get; }
    public string ProtocolId => ProtocolInfo.ProtocolId;
    public string ProtocolRevision => ProtocolInfo.ActiveRevision;

    public bool SupportsCommand(Type commandType) =>
        _supportedCommands?.Contains(commandType)
        ?? (_inner is IDeviceProtocolBinding binding && binding.SupportsCommand(commandType));

    public RuntimeDevice(
        IEggtCsDevice inner,
        IHeartbeatPauseService pause,
        bool automaticPause,
        Action deactivate,
        string protocolId,
        string protocolRevision,
        IReadOnlySet<Type>? supportedCommands = null,
        ProtocolRevisionSource revisionSource = ProtocolRevisionSource.Unspecified
    )
    {
        ProtocolInfo = new(protocolId, protocolRevision, revisionSource);
        _supportedCommands = supportedCommands;
        _inner = inner;
        _pause = pause;
        _automaticPause = automaticPause;
        _deactivate = deactivate;
        if (inner.EegAcquisition is not null)
            EegAcquisition = new Acquisition(this, inner.EegAcquisition);
        if (inner.Stimulation is not null)
            Stimulation = new StimulationCapability(this, inner.Stimulation);
        if (inner.EnvelopeStimulation is not null)
            EnvelopeStimulation = new EnvelopeCapability(this, inner.EnvelopeStimulation);
        if (inner.Impedance is not null)
            Impedance = new ImpedanceCapability(this, inner.Impedance);
        if (automaticPause)
        {
            _subscription = inner.SubscribeEvents(
                new()
                {
                    Filter = envelope =>
                        envelope.Event
                            is AcquisitionCompletedEvent
                                or StimulationCompletedEvent
                                or DeviceStateChangedEvent,
                }
            );
            _pump = ObserveAsync();
        }
    }

    public DeviceIdentity Identity => _inner.Identity;
    public DeviceCapabilities Capabilities => _inner.Capabilities;
    public DeviceStateSnapshot State => _inner.State;
    public IEegAcquisitionCapability? EegAcquisition { get; }
    public IStimulationCapability? Stimulation { get; }
    public IEnvelopeStimulationCapability? EnvelopeStimulation { get; }
    public IImpedanceCapability? Impedance { get; }
    public IToleranceCapability? Tolerance => _inner.Tolerance;

    public DeviceCapabilitySource GetCapabilitySource(DeviceCapabilityKind capability) =>
        _inner.CapabilitySource(capability);

    public Task<DeviceStatusResponse> ReadStatusAsync(
        CancellationToken cancellationToken = default
    ) => _inner.ReadStatusAsync(cancellationToken);

    public IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
        CancellationToken cancellationToken = default
    ) => _inner.ReadEventsAsync(cancellationToken);

    public DeviceEventSubscription SubscribeEvents(
        DeviceEventSubscriptionOptions? options = null
    ) => _inner.SubscribeEvents(options);

    private async Task<DeviceCommandResult> StartAsync(
        string key,
        DeviceCapabilityKind kind,
        Func<Task<DeviceCommandResult>> action
    )
    {
        long epoch;
        lock (_gate)
        {
            epoch = _operationEpochs.GetValueOrDefault(key);
            _operationEpochs.TryAdd(key, epoch);
        }
        IDisposable? scope =
            _automaticPause && GetCapabilitySource(kind) == DeviceCapabilitySource.Real
                ? _pause.Pause(Identity.DeviceId)
                : null;
        try
        {
            var result = await action().ConfigureAwait(false);
            if (result.IsSuccess && scope is not null)
            {
                lock (_gate)
                {
                    if (
                        !_lifetime.IsCancellationRequested
                        && _operationEpochs.GetValueOrDefault(key) == epoch
                    )
                    {
                        if (_operations.Remove(key, out var previous))
                            previous.Dispose();
                        _operations[key] = scope;
                        scope = null;
                    }
                }
            }
            return result;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private async Task<DeviceCommandResult> StopAsync(
        string key,
        Func<Task<DeviceCommandResult>> action
    )
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Release(key);
        }
    }

    private void Release(string key)
    {
        lock (_gate)
        {
            _operationEpochs[key] = _operationEpochs.GetValueOrDefault(key) + 1;
            if (_operations.Remove(key, out var scope))
                scope.Dispose();
        }
    }

    private void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var key in _operationEpochs.Keys.ToArray())
                _operationEpochs[key]++;
            foreach (var scope in _operations.Values)
                scope.Dispose();
            _operations.Clear();
        }
    }

    private async Task ObserveAsync()
    {
        try
        {
            await foreach (
                var envelope in _subscription!
                    .ReadEventsAsync(_lifetime.Token)
                    .ConfigureAwait(false)
            )
                switch (envelope.Event)
                {
                    case AcquisitionCompletedEvent:
                        Release("eeg");
                        break;
                    case StimulationCompletedEvent:
                        Release("stimulation");
                        break;
                    case DeviceStateChangedEvent changed
                        when changed.State.Connection != DeviceConnectionState.Connected:
                        ReleaseAll();
                        break;
                }
        }
        catch
        {
            ReleaseAll();
        }
        finally
        {
            ReleaseAll();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _deactivate();
        _lifetime.Cancel();
        try
        {
            await _inner.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseAll();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _deactivate();
        _lifetime.Cancel();
        ReleaseAll();
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_subscription is not null)
                await _subscription.DisposeAsync().ConfigureAwait(false);
            await _pump.ConfigureAwait(false);
        }
    }

    private sealed class Acquisition(RuntimeDevice owner, IEegAcquisitionCapability inner)
        : IEegAcquisitionCapability
    {
        public Task<DeviceCommandResult> StartAsync(
            TimeSpan duration,
            IReadOnlySet<int> physicalChannels,
            int sampleRateHz,
            CancellationToken cancellationToken = default
        ) =>
            owner.StartAsync(
                "eeg",
                DeviceCapabilityKind.EegAcquisition,
                () => inner.StartAsync(duration, physicalChannels, sampleRateHz, cancellationToken)
            );

        public Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default) =>
            owner.StopAsync("eeg", () => inner.StopAsync(cancellationToken));
    }

    private sealed class EnvelopeCapability(
        RuntimeDevice owner,
        IEnvelopeStimulationCapability inner
    ) : IEnvelopeStimulationCapability
    {
        public Task<DeviceCommandResult> ConfigureAsync(
            EnvelopeStimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        ) => inner.ConfigureAsync(configuration, cancellationToken);

        public Task<DeviceCommandResult> StartAsync(
            CancellationToken cancellationToken = default
        ) =>
            owner.StartAsync(
                "stimulation",
                DeviceCapabilityKind.Stimulation,
                () => inner.StartAsync(cancellationToken)
            );

        public Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default) =>
            owner.StopAsync("stimulation", () => inner.StopAsync(cancellationToken));
    }

    private sealed class StimulationCapability(RuntimeDevice owner, IStimulationCapability inner)
        : IStimulationCapability
    {
        public Task<DeviceCommandResult> ConfigureAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        ) => inner.ConfigureAsync(configuration, cancellationToken);

        public Task<DeviceCommandResult> StartAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default
        ) =>
            owner.StartAsync(
                "stimulation",
                DeviceCapabilityKind.Stimulation,
                () => inner.StartAsync(duration, cancellationToken)
            );

        public Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default) =>
            owner.StopAsync("stimulation", () => inner.StopAsync(cancellationToken));
    }

    private sealed class ImpedanceCapability(RuntimeDevice owner, IImpedanceCapability inner)
        : IImpedanceCapability
    {
        public Task<DeviceCommandResult> StartEegAsync(
            IReadOnlySet<int> physicalChannels,
            CancellationToken cancellationToken = default
        ) =>
            owner.StartAsync(
                "eegImpedance",
                DeviceCapabilityKind.EegImpedance,
                () => inner.StartEegAsync(physicalChannels, cancellationToken)
            );

        public Task<DeviceCommandResult> StopEegAsync(
            IReadOnlySet<int> physicalChannels,
            CancellationToken cancellationToken = default
        ) =>
            owner.StopAsync(
                "eegImpedance",
                () => inner.StopEegAsync(physicalChannels, cancellationToken)
            );

        public Task<DeviceCommandResult> StartStimulationAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        ) =>
            owner.StartAsync(
                "stimulationImpedance",
                DeviceCapabilityKind.StimulationImpedance,
                () => inner.StartStimulationAsync(configuration, cancellationToken)
            );

        public Task<DeviceCommandResult> StopStimulationAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        ) =>
            owner.StopAsync(
                "stimulationImpedance",
                () => inner.StopStimulationAsync(configuration, cancellationToken)
            );
    }
}
