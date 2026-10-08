using System.Diagnostics;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk.Simulation;

public sealed class SimulatedEggtCsDevice : IEggtCsDevice, IDeviceCapabilitySourceProfile
{
    private const double SimulatedAverageImpedanceKiloOhms = 3.9d;
    private readonly DeviceEventHub _events;
    private readonly object _operationSync = new();
    private CancellationTokenSource? _operationCancellation;
    private Task? _operationTask;
    private Task? _disposeTask;
    private long _operationEpoch;
    private decimal _currentMilliAmps;
    private int _eegImpedanceAttempts;
    private int _stimulationImpedanceAttempts;
    private bool _disposed;

    public SimulatedEggtCsDevice(DeviceIdentity? identity = null)
    {
        Identity =
            identity
            ?? new DeviceIdentity(
                DeviceId.Simulator,
                "EGG/tCS Simulator",
                "SIM-0001",
                "00:00:00:00:00:01"
            );
        Capabilities = DeviceCapabilities.Simulator;
        State = new DeviceStateSnapshot(
            DeviceConnectionState.Connected,
            DeviceOperationState.Ready,
            100,
            DateTimeOffset.UtcNow
        );
        SessionId = Guid.NewGuid();
        _events = new DeviceEventHub(512);
        EegAcquisition = new SimulatedEegCapability(this);
        Stimulation = new SimulatedStimulationCapability(this);
        EnvelopeStimulation = new SimulatedEnvelopeCapability(this);
        Impedance = new SimulatedImpedanceCapability(this);
        Tolerance = new SimulatedToleranceCapability(this);
    }

    public Guid SessionId { get; }

    public DeviceIdentity Identity { get; }

    public DeviceCapabilities Capabilities { get; }

    public DeviceStateSnapshot State { get; private set; }

    public IEegAcquisitionCapability EegAcquisition { get; }

    public IStimulationCapability Stimulation { get; }

    public IEnvelopeStimulationCapability EnvelopeStimulation { get; }

    public IImpedanceCapability Impedance { get; }

    public IToleranceCapability Tolerance { get; }

    IEegAcquisitionCapability? IEggtCsDevice.EegAcquisition => EegAcquisition;

    IStimulationCapability? IEggtCsDevice.Stimulation => Stimulation;

    IImpedanceCapability? IEggtCsDevice.Impedance => Impedance;

    IToleranceCapability? IEggtCsDevice.Tolerance => Tolerance;

    public DeviceCapabilitySource GetCapabilitySource(DeviceCapabilityKind capability) =>
        Enum.IsDefined(capability)
            ? DeviceCapabilitySource.Simulated
            : DeviceCapabilitySource.Disabled;

    public Task<DeviceStatusResponse> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new DeviceStatusResponse(
                DeviceCommandStatus.Success,
                State.Operation,
                State.BatteryPercent ?? 100
            )
        );
    }

    public IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
        CancellationToken cancellationToken = default
    ) => _events.SubscribeAsync(cancellationToken);

    public DeviceEventSubscription SubscribeEvents(
        DeviceEventSubscriptionOptions? options = null
    ) => _events.Subscribe(options);

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? operation;
        DeviceStateSnapshot snapshot;
        lock (_operationSync)
        {
            snapshot = MarkDisconnected();
            _operationCancellation?.Cancel();
            operation = _operationTask;
        }
        await PublishAsync(
                new DeviceStateChangedEvent(snapshot),
                EventDeliveryClass.Control,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        if (operation is not null)
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_operationSync)
        {
            if (_disposeTask is not null)
                return new(_disposeTask);
            _disposed = true;
            var snapshot = MarkDisconnected();
            _operationCancellation?.Cancel();
            _disposeTask = FinishDisposeAsync(_operationTask, snapshot);
            return new(_disposeTask);
        }
    }

    private async Task FinishDisposeAsync(Task? operation, DeviceStateSnapshot snapshot)
    {
        try
        {
            await PublishAsync(
                    new DeviceStateChangedEvent(snapshot),
                    EventDeliveryClass.Control,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            if (operation is not null)
                await operation.ConfigureAwait(false);
        }
        finally
        {
            lock (_operationSync)
            {
                _operationCancellation?.Dispose();
                _operationCancellation = null;
            }
            _events.Complete();
        }
    }

    private DeviceStateSnapshot MarkDisconnected() =>
        State = State with
        {
            Connection = DeviceConnectionState.Disconnected,
            Operation = DeviceOperationState.Unknown,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    internal DeviceCommandResult TryStartOperation(
        DeviceOperationState operation,
        Func<CancellationToken, Task> run
    )
    {
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State.Connection != DeviceConnectionState.Connected)
                throw new InvalidOperationException("The simulator is not connected.");
            if (_operationTask is { IsCompleted: false })
                return new DeviceCommandResult(
                    DeviceCommandStatus.DeviceBusy,
                    "A simulated operation is already running."
                );
            _operationCancellation?.Dispose();
            _operationCancellation = new CancellationTokenSource();
            UpdateState(operation);
            _operationTask = RunOperationAsync(
                run,
                _operationCancellation.Token,
                ++_operationEpoch
            );
        }
        return DeviceCommandResult.Success;
    }

    internal async Task<DeviceCommandResult> StopOperationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? task;
        lock (_operationSync)
        {
            _operationCancellation?.Cancel();
            task = _operationTask;
        }
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
        lock (_operationSync)
            if (
                ReferenceEquals(task, _operationTask)
                && State.Connection == DeviceConnectionState.Connected
            )
                UpdateState(DeviceOperationState.Ready);
        return DeviceCommandResult.Success;
    }

    internal async Task RunAcquisitionAsync(
        TimeSpan duration,
        IReadOnlySet<int> channels,
        int sampleRateHz,
        int currentCycle,
        int totalCycles,
        CancellationToken cancellationToken
    )
    {
        var interval = TimeSpan.FromMilliseconds(50);
        var startedTimestamp = Stopwatch.GetTimestamp();
        var orderedChannels = channels.OrderBy(channel => channel).ToArray();
        var samplesPerFrame = Math.Max(1, (int)Math.Round(sampleRateHz * interval.TotalSeconds));
        var totalSampleCount = SimulatedAcquisitionSchedule.GetTotalSampleCount(
            duration,
            sampleRateHz
        );
        long nextSampleIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
            var boundedElapsed = elapsed > duration ? duration : elapsed;
            var progress =
                duration <= TimeSpan.Zero
                    ? 1d
                    : Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, 0d, 1d);
            await PublishAsync(
                    new DeviceProgressEvent(
                        DeviceOperationState.Acquiring,
                        boundedElapsed,
                        duration - boundedElapsed > TimeSpan.Zero
                            ? duration - boundedElapsed
                            : TimeSpan.Zero,
                        progress,
                        currentCycle,
                        totalCycles,
                        BatteryPercent: State.BatteryPercent,
                        AverageImpedanceKiloOhms: SimulatedAverageImpedanceKiloOhms
                    ),
                    EventDeliveryClass.Control,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var sampleElapsed = Stopwatch.GetElapsedTime(startedTimestamp);
            var targetSampleCount = SimulatedAcquisitionSchedule.GetTargetSampleCount(
                sampleElapsed,
                duration,
                sampleRateHz
            );
            while (nextSampleIndex < targetSampleCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sampleCount = (int)
                    Math.Min(samplesPerFrame, targetSampleCount - nextSampleIndex);
                var batches = CreateEegSampleBatches(
                    orderedChannels,
                    sampleRateHz,
                    sampleCount,
                    nextSampleIndex
                );
                await PublishAsync(
                        new EegSamplesReceivedEvent(batches),
                        EventDeliveryClass.Data,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                nextSampleIndex += sampleCount;
            }

            var currentElapsed = Stopwatch.GetElapsedTime(startedTimestamp);
            if (currentElapsed >= duration)
            {
                if (nextSampleIndex < totalSampleCount)
                    continue;
                break;
            }

            var delay = SimulatedAcquisitionSchedule.GetDelayUntilNextCadence(
                currentElapsed,
                interval
            );
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        await PublishAsync(
                new AcquisitionCompletedEvent(),
                EventDeliveryClass.Control,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal async Task RunStimulationAsync(
        TimeSpan duration,
        int currentCycle,
        int totalCycles,
        CancellationToken cancellationToken
    )
    {
        var started = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromMilliseconds(100);
        while (DateTimeOffset.UtcNow - started < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = DateTimeOffset.UtcNow - started;
            var progress =
                duration <= TimeSpan.Zero
                    ? 1d
                    : Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, 0d, 1d);
            await PublishAsync(
                    new DeviceProgressEvent(
                        DeviceOperationState.Stimulating,
                        elapsed,
                        duration - elapsed > TimeSpan.Zero ? duration - elapsed : TimeSpan.Zero,
                        progress,
                        currentCycle,
                        totalCycles,
                        _currentMilliAmps,
                        State.BatteryPercent,
                        SimulatedAverageImpedanceKiloOhms
                    ),
                    EventDeliveryClass.Control,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        await PublishAsync(
                new StimulationCompletedEvent(),
                EventDeliveryClass.Control,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal Task PublishAsync(
        DeviceEvent deviceEvent,
        EventDeliveryClass deliveryClass,
        CancellationToken cancellationToken
    ) =>
        _events
            .PublishAsync(
                new DeviceEventEnvelope(
                    Identity.DeviceId,
                    SessionId,
                    DateTimeOffset.UtcNow,
                    deviceEvent,
                    deliveryClass,
                    Stopwatch.GetTimestamp()
                ),
                cancellationToken
            )
            .AsTask();

    internal void SetCurrent(decimal value) => _currentMilliAmps = Math.Clamp(value, 0m, 2m);

    internal decimal Current => _currentMilliAmps;

    internal int NextImpedanceAttempt(bool eeg) =>
        eeg
            ? Interlocked.Increment(ref _eegImpedanceAttempts)
            : Interlocked.Increment(ref _stimulationImpedanceAttempts);

    private async Task RunOperationAsync(
        Func<CancellationToken, Task> run,
        CancellationToken cancellationToken,
        long epoch
    )
    {
        try
        {
            await run(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DeviceStateSnapshot snapshot;
            lock (_operationSync)
            {
                if (_operationEpoch == epoch && State.Connection == DeviceConnectionState.Connected)
                    State = State with
                    {
                        Connection = DeviceConnectionState.Faulted,
                        Operation = DeviceOperationState.Faulted,
                        Fault = exception.Message,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                snapshot = State;
            }
            try
            {
                await PublishAsync(
                        new DeviceStateChangedEvent(snapshot),
                        EventDeliveryClass.Control,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            finally
            {
                _events.Complete(exception);
            }
        }
        finally
        {
            lock (_operationSync)
                if (_operationEpoch == epoch && State.Connection == DeviceConnectionState.Connected)
                    UpdateState(DeviceOperationState.Ready);
        }
    }

    private void UpdateState(DeviceOperationState operation)
    {
        State = State with { Operation = operation, UpdatedAt = DateTimeOffset.UtcNow };
    }

    internal static IReadOnlyList<EegChannelSamples> CreateEegSampleBatches(
        IReadOnlyList<int> physicalChannels,
        int sampleRateHz,
        int sampleCount,
        long firstSampleIndex
    )
    {
        var result = new List<EegChannelSamples>(physicalChannels.Count);
        var sampleIntervalSeconds = 1d / Math.Max(1, sampleRateHz);
        for (var channelIndex = 0; channelIndex < physicalChannels.Count; channelIndex++)
        {
            var values = new double[sampleCount];
            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                values[sampleIndex] = SimulatedEegSignal.Sample(
                    SimulatedEegSignal.DefaultSeed,
                    SimulatedEegSignal.DefaultSubject,
                    physicalChannels[channelIndex],
                    firstSampleIndex + sampleIndex,
                    sampleRateHz
                );
            }
            result.Add(
                new EegChannelSamples(
                    physicalChannels[channelIndex],
                    firstSampleIndex / (double)sampleRateHz,
                    sampleIntervalSeconds,
                    values
                )
            );
        }
        return result;
    }

    private static int StableSeed(string value) =>
        value.Select((character, index) => character * (index + 17)).Sum();

    private sealed class SimulatedEegCapability(SimulatedEggtCsDevice owner)
        : IEegAcquisitionCapability
    {
        public Task<DeviceCommandResult> StartAsync(
            TimeSpan duration,
            IReadOnlySet<int> physicalChannels,
            int sampleRateHz,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                owner.TryStartOperation(
                    DeviceOperationState.Acquiring,
                    token =>
                        owner.RunAcquisitionAsync(
                            duration,
                            physicalChannels,
                            sampleRateHz,
                            1,
                            1,
                            token
                        )
                )
            );
        }

        public Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default) =>
            owner.StopOperationAsync(cancellationToken);
    }

    private sealed class SimulatedEnvelopeCapability(SimulatedEggtCsDevice owner)
        : IEnvelopeStimulationCapability
    {
        private EnvelopeStimulationConfiguration? _configuration;
        private Task? _running;

        public Task<DeviceCommandResult> ConfigureAsync(
            EnvelopeStimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(configuration);
            cancellationToken.ThrowIfCancellationRequested();
            lock (owner._operationSync)
            {
                EnsureConnected();
                if (owner._operationTask is { IsCompleted: false })
                    return Task.FromResult(new DeviceCommandResult(DeviceCommandStatus.DeviceBusy));
                _configuration = configuration;
                return Task.FromResult(Result(DeviceCommandStatus.Success, 0));
            }
        }

        public Task<DeviceCommandResult> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (owner._operationSync)
            {
                EnsureConnected();
                if (_configuration is not { } configuration)
                    return Task.FromResult(Result(DeviceCommandStatus.NotConfigured, 1));
                if (_running is { IsCompleted: false })
                    return Task.FromResult(Result(DeviceCommandStatus.AlreadyRunning, 2));
                var result = owner.TryStartOperation(
                    DeviceOperationState.Stimulating,
                    async token =>
                    {
                        await Task.Delay(configuration.DelayMilliseconds, token)
                            .ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(configuration.Duration, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        await owner
                            .PublishAsync(
                                new StimulationCompletedEvent(),
                                EventDeliveryClass.Control,
                                token
                            )
                            .ConfigureAwait(false);
                    }
                );
                if (result.IsSuccess)
                    _running = owner._operationTask;
                return Task.FromResult(
                    result.IsSuccess ? Result(DeviceCommandStatus.Success, 0) : result
                );
            }
        }

        public async Task<DeviceCommandResult> StopAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task running;
            lock (owner._operationSync)
            {
                EnsureConnected();
                if (_running is not { IsCompleted: false })
                    return Result(DeviceCommandStatus.AlreadyStopped, 3);
                running = _running;
                // Cancel only this operation, never a subsequent ordinary stimulation/EEG operation.
                if (ReferenceEquals(running, owner._operationTask))
                    owner._operationCancellation?.Cancel();
            }
            await running.ConfigureAwait(false);
            return Result(DeviceCommandStatus.Success, 0);
        }

        private void EnsureConnected()
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            if (owner.State.Connection != DeviceConnectionState.Connected)
                throw new InvalidOperationException("The simulator is not connected.");
        }

        private static DeviceCommandResult Result(DeviceCommandStatus status, byte code) =>
            new(status) { RawResultCode = code };
    }

    private sealed class SimulatedStimulationCapability(SimulatedEggtCsDevice owner)
        : IStimulationCapability
    {
        public Task<DeviceCommandResult> ConfigureAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.SetCurrent(configuration.Channels.Sum(item => item.CurrentMilliAmps));
            return Task.FromResult(DeviceCommandResult.Success);
        }

        public Task<DeviceCommandResult> StartAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                owner.TryStartOperation(
                    DeviceOperationState.Stimulating,
                    token => owner.RunStimulationAsync(duration, 1, 1, token)
                )
            );
        }

        public Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default) =>
            owner.StopOperationAsync(cancellationToken);
    }

    private sealed class SimulatedImpedanceCapability(SimulatedEggtCsDevice owner)
        : IImpedanceCapability
    {
        public Task<DeviceCommandResult> StartEegAsync(
            IReadOnlySet<int> physicalChannels,
            CancellationToken cancellationToken = default
        ) => PublishAsync(physicalChannels, true, cancellationToken);

        public Task<DeviceCommandResult> StopEegAsync(
            IReadOnlySet<int> physicalChannels,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(DeviceCommandResult.Success);

        public Task<DeviceCommandResult> StartStimulationAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        ) =>
            PublishAsync(
                configuration
                    .Channels.Where(item => item.Role == DeviceStimulationChannelRole.Selectable)
                    .Select(item => item.PhysicalChannel)
                    .ToHashSet(),
                false,
                cancellationToken
            );

        public Task<DeviceCommandResult> StopStimulationAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(DeviceCommandResult.Success);

        private async Task<DeviceCommandResult> PublishAsync(
            IReadOnlySet<int> channels,
            bool eeg,
            CancellationToken cancellationToken
        )
        {
            var attempt = owner.NextImpedanceAttempt(eeg);
            var readings = channels
                .Select(
                    (channel, index) =>
                        new ImpedanceReading(
                            channel,
                            attempt == 1 && index == 0
                                ? ImpedanceBand.UpTo20KOhms
                                : ImpedanceBand.UpTo10KOhms
                        )
                )
                .ToArray();
            DeviceEvent deviceEvent = eeg
                ? new EegImpedanceReceivedEvent(readings)
                : new StimulationImpedanceReceivedEvent(readings);
            await owner
                .PublishAsync(deviceEvent, EventDeliveryClass.Control, cancellationToken)
                .ConfigureAwait(false);
            return DeviceCommandResult.Success;
        }
    }

    private sealed class SimulatedToleranceCapability(SimulatedEggtCsDevice owner)
        : IToleranceCapability
    {
        public Task<CurrentAdjustmentResponse> AdjustCurrentAsync(
            CurrentAdjustment adjustment,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = owner.Current + (adjustment == CurrentAdjustment.Increase ? 0.04m : -0.04m);
            var status = next switch
            {
                > 2m => DeviceCommandStatus.MaximumReached,
                < 0m => DeviceCommandStatus.MinimumReached,
                _ => DeviceCommandStatus.Success,
            };
            owner.SetCurrent(next);
            return Task.FromResult(new CurrentAdjustmentResponse(status, owner.Current));
        }
    }
}

internal static class SimulatedAcquisitionSchedule
{
    public static long GetTotalSampleCount(TimeSpan duration, int sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks, nameof(duration));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        var wholeSeconds = duration.Ticks / TimeSpan.TicksPerSecond;
        var remainingTicks = duration.Ticks % TimeSpan.TicksPerSecond;
        return checked(
            wholeSeconds * sampleRateHz
            + DivideRoundUp(checked(remainingTicks * sampleRateHz), TimeSpan.TicksPerSecond)
        );
    }

    public static long GetTargetSampleCount(TimeSpan elapsed, TimeSpan duration, int sampleRateHz)
    {
        var totalSampleCount = GetTotalSampleCount(duration, sampleRateHz);
        if (elapsed <= TimeSpan.Zero || totalSampleCount == 0)
            return 0;
        if (elapsed >= duration)
            return totalSampleCount;
        return Math.Min(totalSampleCount, GetTotalSampleCount(elapsed, sampleRateHz));
    }

    public static TimeSpan GetDelayUntilNextCadence(TimeSpan elapsed, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsed.Ticks, nameof(elapsed));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval.Ticks, nameof(interval));

        var remainderTicks = elapsed.Ticks % interval.Ticks;
        var remainingTicks = remainderTicks == 0 ? interval.Ticks : interval.Ticks - remainderTicks;
        // Task.Delay truncates sub-millisecond waits. Round up so the producer yields instead
        // of flooding reliable control queues while waiting for the next sampling cadence.
        return TimeSpan.FromTicks(
            DivideRoundUp(remainingTicks, TimeSpan.TicksPerMillisecond)
                * TimeSpan.TicksPerMillisecond
        );
    }

    private static long DivideRoundUp(long value, long divisor) =>
        checked((value + divisor - 1) / divisor);
}
