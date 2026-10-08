using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk;

public sealed class SessionEggtCsDevice : IEggtCsDevice, IDeviceCapabilitySourceProfile
{
    private readonly IDeviceSession _session;
    private readonly DeviceEventHub _events;
    private readonly Action<DeviceIdentity, Exception>? _faulted;
    private readonly CancellationTokenSource _eventCancellation = new();
    private readonly object _stateGate = new();
    private readonly Task _controlPump;
    private readonly Task _dataPump;
    private bool _disposed;
    private bool _terminal;

    public SessionEggtCsDevice(
        DeviceIdentity identity,
        DeviceCapabilities capabilities,
        IDeviceSession session,
        int eventCapacity = 512,
        Action<DeviceIdentity, Exception>? faulted = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventCapacity);
        Identity = identity;
        Capabilities = capabilities;
        _session = session;
        _faulted = faulted;
        _session.Faulted += OnSessionFaulted;
        State = new DeviceStateSnapshot(
            session.State == DeviceSessionState.Connected
                ? DeviceConnectionState.Connected
                : DeviceConnectionState.Connecting,
            DeviceOperationState.Unknown,
            null,
            DateTimeOffset.UtcNow
        );
        _events = new DeviceEventHub(eventCapacity);

        if (capabilities.CanAcquireEeg)
            EegAcquisition = new SessionEegAcquisitionCapability(this);
        if (capabilities.CanStimulate)
            Stimulation = new SessionStimulationCapability(this);
        if (capabilities.CanStimulate)
            EnvelopeStimulation = new SessionEnvelopeCapability(this);
        if (capabilities.CanCheckEegImpedance || capabilities.CanCheckStimulationImpedance)
            Impedance = new SessionImpedanceCapability(this);
        if (capabilities.CanRunToleranceTest)
            Tolerance = new SessionToleranceCapability(this);

        _controlPump = PumpAsync(_session.ReadControlEventsAsync(_eventCancellation.Token));
        _dataPump = PumpAsync(_session.ReadDataEventsAsync(_eventCancellation.Token));
    }

    public DeviceIdentity Identity { get; }

    public DeviceCapabilities Capabilities { get; }

    public DeviceStateSnapshot State { get; private set; }

    public IEegAcquisitionCapability? EegAcquisition { get; }

    public IStimulationCapability? Stimulation { get; }

    public IEnvelopeStimulationCapability? EnvelopeStimulation { get; }

    public IImpedanceCapability? Impedance { get; }

    public IToleranceCapability? Tolerance { get; }

    public async Task<DeviceStatusResponse> ReadStatusAsync(
        CancellationToken cancellationToken = default
    )
    {
        EnsureCapability(Capabilities.CanReadStatus, "Status query");
        var response = await _session
            .SendAsync(new ReadDeviceStatusRequest(), cancellationToken)
            .ConfigureAwait(false);
        UpdateState(response.Operation, response.BatteryPercent);
        return response;
    }

    public DeviceCapabilitySource GetCapabilitySource(DeviceCapabilityKind capability) =>
        capability switch
        {
            DeviceCapabilityKind.Status when Capabilities.CanReadStatus =>
                DeviceCapabilitySource.Real,
            DeviceCapabilityKind.EegAcquisition when Capabilities.CanAcquireEeg =>
                DeviceCapabilitySource.Real,
            DeviceCapabilityKind.Stimulation when Capabilities.CanStimulate =>
                DeviceCapabilitySource.Real,
            DeviceCapabilityKind.EegImpedance when Capabilities.CanCheckEegImpedance =>
                DeviceCapabilitySource.Real,
            DeviceCapabilityKind.StimulationImpedance
                when Capabilities.CanCheckStimulationImpedance => DeviceCapabilitySource.Real,
            DeviceCapabilityKind.Tolerance when Capabilities.CanRunToleranceTest =>
                DeviceCapabilitySource.Real,
            _ => DeviceCapabilitySource.Disabled,
        };

    public IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
        CancellationToken cancellationToken = default
    ) => _events.SubscribeAsync(cancellationToken);

    public DeviceEventSubscription SubscribeEvents(
        DeviceEventSubscriptionOptions? options = null
    ) => _events.Subscribe(options);

    public void ApplyStatus(DeviceStatusResponse status)
    {
        UpdateState(status.Operation, status.BatteryPercent);
        if (!_terminal)
            _ = _events.PublishAsync(
                new(
                    Identity.DeviceId,
                    _session.SessionId,
                    DateTimeOffset.UtcNow,
                    new DeviceStateChangedEvent(State),
                    EventDeliveryClass.Control
                )
            );
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        ChangeState(state => state, terminal: true);
        _session.Faulted -= OnSessionFaulted;
        try
        {
            await _session.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            ChangeState(
                state =>
                    state with
                    {
                        Connection = DeviceConnectionState.Disconnected,
                        Operation = DeviceOperationState.Unknown,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                force: true
            );
            _events.Complete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        ChangeState(
            state =>
                state with
                {
                    Connection =
                        state.Connection == DeviceConnectionState.Faulted
                            ? DeviceConnectionState.Faulted
                            : DeviceConnectionState.Disconnected,
                },
            terminal: true,
            force: true
        );
        _session.Faulted -= OnSessionFaulted;
        _eventCancellation.Cancel();
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await AwaitPumpAsync(_controlPump).ConfigureAwait(false);
                await AwaitPumpAsync(_dataPump).ConfigureAwait(false);
            }
            finally
            {
                _events.Complete();
                _eventCancellation.Dispose();
            }
        }
    }

    internal IDeviceSession Session => _session;

    internal void EnsureCapability(bool supported, string capability)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!supported)
            throw new NotSupportedException(
                $"{capability} is not enabled for device {Identity.DeviceId}."
            );
    }

    private void ChangeState(
        Func<DeviceStateSnapshot, DeviceStateSnapshot> update,
        bool terminal = false,
        bool force = false
    )
    {
        lock (_stateGate)
        {
            if (_terminal && !force)
                return;
            _terminal |= terminal;
            State = update(State);
        }
    }

    internal void UpdateState(DeviceOperationState operation, int? batteryPercent = null) =>
        ChangeState(state =>
            state with
            {
                Connection = DeviceConnectionState.Connected,
                Operation = operation,
                BatteryPercent = batteryPercent ?? state.BatteryPercent,
                UpdatedAt = DateTimeOffset.UtcNow,
                Fault = null,
            }
        );

    private void UpdateBattery(int batteryPercent) =>
        ChangeState(state =>
            state with
            {
                BatteryPercent = batteryPercent,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        );

    private void SetFault(Exception exception) =>
        ChangeState(
            state =>
                state with
                {
                    Connection = DeviceConnectionState.Faulted,
                    Operation = DeviceOperationState.Faulted,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Fault = exception.Message,
                },
            terminal: true
        );

    private async Task PumpAsync(IAsyncEnumerable<DeviceSessionEvent> source)
    {
        try
        {
            await foreach (
                var item in source.WithCancellation(_eventCancellation.Token).ConfigureAwait(false)
            )
            {
                if (item.Message is not DeviceEvent deviceEvent)
                    continue;
                ApplyEventToState(deviceEvent);
                await _events
                    .PublishAsync(
                        new DeviceEventEnvelope(
                            Identity.DeviceId,
                            item.SessionId,
                            item.Timestamp,
                            deviceEvent,
                            item.DeliveryClass,
                            item.ReceivedTimestamp,
                            item.DecodeLatency,
                            item.RawPacketData
                        )
                        {
                            RawFrameData = item.RawFrameData,
                            TransportReceiveId = item.TransportReceiveId,
                        },
                        _eventCancellation.Token
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_eventCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SetFault(exception);
            _events.Complete(exception);
        }
    }

    private void OnSessionFaulted(object? sender, DeviceSessionFaultedEventArgs args)
    {
        SetFault(args.Exception);
        _events.Complete(args.Exception);
        try
        {
            _faulted?.Invoke(Identity, args.Exception);
        }
        finally
        {
            _ = StopFaultedSessionAsync();
        }
    }

    private async Task StopFaultedSessionAsync()
    {
        try
        {
            await _session.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the faulted device state; disposal performs another best-effort cleanup.
        }
    }

    private void ApplyEventToState(DeviceEvent deviceEvent)
    {
        switch (deviceEvent)
        {
            case DeviceStateChangedEvent stateChanged:
                ChangeState(_ => stateChanged.State);
                break;
            case DeviceProgressEvent progress:
                UpdateState(progress.Operation, progress.BatteryPercent);
                break;
            case EegDataPacketReceivedEvent eegData:
                UpdateBattery(eegData.BatteryPercent);
                break;
            case AcquisitionCompletedEvent or StimulationCompletedEvent:
                UpdateState(DeviceOperationState.Ready);
                break;
        }
    }

    private static async Task AwaitPumpAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private sealed class SessionEegAcquisitionCapability(SessionEggtCsDevice owner)
        : IEegAcquisitionCapability
    {
        public async Task<DeviceCommandResult> StartAsync(
            TimeSpan duration,
            IReadOnlySet<int> physicalChannels,
            int sampleRateHz,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanAcquireEeg, "EEG acquisition");
            if (!owner.Capabilities.SupportedSampleRatesHz.Contains(sampleRateHz))
                throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
            if (
                physicalChannels.Count == 0
                || physicalChannels.Any(channel =>
                    channel < 1 || channel > owner.Capabilities.MaximumEegChannels
                )
            )
                throw new ArgumentOutOfRangeException(nameof(physicalChannels));
            var result = await owner
                .Session.SendAsync(
                    new ControlAcquisitionRequest(RunControl.Start, duration),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.IsSuccess)
                owner.UpdateState(DeviceOperationState.Acquiring);
            return result;
        }

        public async Task<DeviceCommandResult> StopAsync(
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanAcquireEeg, "EEG acquisition");
            var result = await owner
                .Session.SendAsync(
                    new ControlAcquisitionRequest(RunControl.Stop, TimeSpan.Zero),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.IsSuccess)
                owner.UpdateState(DeviceOperationState.Ready);
            return result;
        }
    }

    private sealed class SessionEnvelopeCapability(SessionEggtCsDevice owner)
        : IEnvelopeStimulationCapability
    {
        public Task<DeviceCommandResult> ConfigureAsync(
            EnvelopeStimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanStimulate, "Envelope stimulation");
            ArgumentNullException.ThrowIfNull(configuration);
            return owner.Session.SendAsync(
                new ConfigureEnvelopeStimulationRequest(configuration),
                cancellationToken
            );
        }

        public Task<DeviceCommandResult> StartAsync(
            CancellationToken cancellationToken = default
        ) => ControlAsync(RunControl.Start, cancellationToken);

        public Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default) =>
            ControlAsync(RunControl.Stop, cancellationToken);

        private async Task<DeviceCommandResult> ControlAsync(
            RunControl control,
            CancellationToken token
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanStimulate, "Envelope stimulation");
            var result = await owner
                .Session.SendAsync(new ControlEnvelopeStimulationRequest(control), token)
                .ConfigureAwait(false);
            if (
                result.IsSuccess
                || control == RunControl.Stop && result.Status == DeviceCommandStatus.AlreadyStopped
            )
                owner.UpdateState(
                    control == RunControl.Start
                        ? DeviceOperationState.Stimulating
                        : DeviceOperationState.Ready
                );
            return result;
        }
    }

    private sealed class SessionStimulationCapability(SessionEggtCsDevice owner)
        : IStimulationCapability
    {
        public Task<DeviceCommandResult> ConfigureAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanStimulate, "Stimulation");
            return owner.Session.SendAsync(
                new ConfigureStimulationImpedanceRequest(MeasurementControl.Stop, configuration),
                cancellationToken
            );
        }

        public async Task<DeviceCommandResult> StartAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanStimulate, "Stimulation");
            var result = await owner
                .Session.SendAsync(
                    new ControlStimulationRequest(RunControl.Start, duration),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.IsSuccess)
                owner.UpdateState(DeviceOperationState.Stimulating);
            return result;
        }

        public async Task<DeviceCommandResult> StopAsync(
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanStimulate, "Stimulation");
            var result = await owner
                .Session.SendAsync(
                    new ControlStimulationRequest(RunControl.Stop, TimeSpan.Zero),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.IsSuccess || result.Status == DeviceCommandStatus.AlreadyStopped)
                owner.UpdateState(DeviceOperationState.Ready);
            else if (result.Status == DeviceCommandStatus.CurrentRampingDown)
                owner.UpdateState(DeviceOperationState.Stopping);
            return result;
        }
    }

    private sealed class SessionImpedanceCapability(SessionEggtCsDevice owner)
        : IImpedanceCapability
    {
        public async Task<DeviceCommandResult> StartEegAsync(
            IReadOnlySet<int> physicalChannels,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanCheckEegImpedance, "EEG impedance");
            var result = await owner
                .Session.SendAsync(
                    new ConfigureEegImpedanceRequest(MeasurementControl.Start, physicalChannels),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.IsSuccess)
                owner.UpdateState(DeviceOperationState.ImpedanceChecking);
            return result;
        }

        public async Task<DeviceCommandResult> StopEegAsync(
            IReadOnlySet<int> physicalChannels,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanCheckEegImpedance, "EEG impedance");
            var result = await owner
                .Session.SendAsync(
                    new ConfigureEegImpedanceRequest(MeasurementControl.Stop, physicalChannels),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.IsSuccess)
                owner.UpdateState(DeviceOperationState.Ready);
            return result;
        }

        public async Task<DeviceCommandResult> StartStimulationAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(
                owner.Capabilities.CanCheckStimulationImpedance,
                "Stimulation impedance"
            );
            var result = await owner
                .Session.SendAsync(
                    new ConfigureStimulationImpedanceRequest(
                        MeasurementControl.Start,
                        configuration
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.IsSuccess)
                owner.UpdateState(DeviceOperationState.ImpedanceChecking);
            return result;
        }

        public async Task<DeviceCommandResult> StopStimulationAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(
                owner.Capabilities.CanCheckStimulationImpedance,
                "Stimulation impedance"
            );
            var result = await owner
                .Session.SendAsync(
                    new ConfigureStimulationImpedanceRequest(
                        MeasurementControl.Stop,
                        configuration
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.IsSuccess)
                owner.UpdateState(DeviceOperationState.Ready);
            return result;
        }
    }

    private sealed class SessionToleranceCapability(SessionEggtCsDevice owner)
        : IToleranceCapability
    {
        public Task<CurrentAdjustmentResponse> AdjustCurrentAsync(
            CurrentAdjustment adjustment,
            CancellationToken cancellationToken = default
        )
        {
            owner.EnsureCapability(owner.Capabilities.CanRunToleranceTest, "Tolerance test");
            return owner.Session.SendAsync(new AdjustCurrentRequest(adjustment), cancellationToken);
        }
    }
}
