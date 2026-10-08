using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;

namespace EGGtCSPlatform.DeviceRuntime;

public class MixedSourceDevice : IEggtCsDevice, IDeviceCapabilitySourceProfile
{
    private readonly DeviceBackendProfile _options;
    private readonly IEggtCsDevice? _physical;
    private readonly SimulatedEggtCsDevice _simulation;
    private readonly bool _readPhysicalEvents;
    private readonly bool _readSimulationEvents;
    private bool _disposed;

    public MixedSourceDevice(
        DeviceIdentity identity,
        IDeviceSession session,
        DeviceCapabilities capabilities,
        Action<DeviceIdentity, Exception>? faulted = null
    )
        : this(
            identity,
            new DeviceBackendProfile(),
            new SessionEggtCsDevice(identity, capabilities, session, faulted: faulted)
        ) { }

    public MixedSourceDevice(
        DeviceIdentity identity,
        DeviceBackendProfile options,
        IEggtCsDevice? physical = null,
        SimulatedEggtCsDevice? simulation = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ConnectionSource == DeviceConnectionSource.Real && physical is null)
            throw new ArgumentNullException(
                nameof(physical),
                "A real connection requires a physical device session."
            );

        Identity = identity;
        _options = options;
        _physical = physical;
        _simulation = simulation ?? new SimulatedEggtCsDevice(identity);

        EegAcquisition = Select(
            DeviceCapabilityKind.EegAcquisition,
            physical?.EegAcquisition,
            _simulation.EegAcquisition
        );
        Stimulation = Select(
            DeviceCapabilityKind.Stimulation,
            physical?.Stimulation,
            _simulation.Stimulation
        );
        Tolerance = Select(
            DeviceCapabilityKind.Tolerance,
            physical?.Tolerance,
            _simulation.Tolerance
        );
        EnvelopeStimulation =
            GetCapabilitySource(DeviceCapabilityKind.Stimulation) == DeviceCapabilitySource.Real
                ? physical?.EnvelopeStimulation
                : Select(
                    DeviceCapabilityKind.Stimulation,
                    physical?.EnvelopeStimulation,
                    _simulation.EnvelopeStimulation
                );

        var eegImpedance = Select(
            DeviceCapabilityKind.EegImpedance,
            physical?.Impedance,
            _simulation.Impedance
        );
        var stimulationImpedance = Select(
            DeviceCapabilityKind.StimulationImpedance,
            physical?.Impedance,
            _simulation.Impedance
        );
        if (eegImpedance is not null || stimulationImpedance is not null)
            Impedance = new ConfigurableImpedanceCapability(eegImpedance, stimulationImpedance);

        Capabilities = CreateCapabilities();
        _readPhysicalEvents =
            options.ConnectionSource == DeviceConnectionSource.Real
            || Enum.GetValues<DeviceCapabilityKind>()
                .Any(capability =>
                    capability != DeviceCapabilityKind.Status
                    && GetCapabilitySource(capability) == DeviceCapabilitySource.Real
                );
        _readSimulationEvents =
            options.ConnectionSource == DeviceConnectionSource.Simulated
            || Enum.GetValues<DeviceCapabilityKind>()
                .Any(capability =>
                    capability != DeviceCapabilityKind.Status
                    && GetCapabilitySource(capability) == DeviceCapabilitySource.Simulated
                );
    }

    public DeviceIdentity Identity { get; }

    public DeviceCapabilities Capabilities { get; }

    public DeviceStateSnapshot State
    {
        get
        {
            var connectionState =
                _options.ConnectionSource == DeviceConnectionSource.Real
                    ? _physical!.State
                    : _simulation.State;
            var statusState =
                GetCapabilitySource(DeviceCapabilityKind.Status) == DeviceCapabilitySource.Real
                    ? _physical!.State
                    : _simulation.State;
            var operationState = ResolveOperationState(statusState);
            return new DeviceStateSnapshot(
                connectionState.Connection,
                connectionState.Connection == DeviceConnectionState.Faulted
                    ? DeviceOperationState.Faulted
                    : operationState.Operation,
                statusState.BatteryPercent,
                Latest(
                    connectionState.UpdatedAt,
                    statusState.UpdatedAt,
                    _physical?.State.UpdatedAt,
                    _simulation.State.UpdatedAt
                ),
                connectionState.Fault ?? operationState.Fault
            );
        }
    }

    public IEegAcquisitionCapability? EegAcquisition { get; }

    public IStimulationCapability? Stimulation { get; }

    public IEnvelopeStimulationCapability? EnvelopeStimulation { get; }

    public IImpedanceCapability? Impedance { get; }

    public IToleranceCapability? Tolerance { get; }

    public DeviceCapabilitySource GetCapabilitySource(DeviceCapabilityKind capability) =>
        _options.GetSource(capability);

    public Task<DeviceStatusResponse> ReadStatusAsync(
        CancellationToken cancellationToken = default
    ) =>
        GetCapabilitySource(DeviceCapabilityKind.Status) == DeviceCapabilitySource.Real
            ? _physical!.ReadStatusAsync(cancellationToken)
            : _simulation.ReadStatusAsync(cancellationToken);

    public DeviceEventSubscription SubscribeEvents(
        DeviceEventSubscriptionOptions? options = null
    ) => DeviceEventSubscription.FromStream(MergeEventsAsync(CancellationToken.None), options);

    public IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
        CancellationToken cancellationToken = default
    ) => MergeEventsAsync(cancellationToken);

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;
        try
        {
            if (_physical is not null)
                await _physical.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _simulation.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_physical is not null)
                await _physical.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _simulation.DisposeAsync().ConfigureAwait(false);
        }
    }

    private TCapability? Select<TCapability>(
        DeviceCapabilityKind capability,
        TCapability? physical,
        TCapability simulated
    )
        where TCapability : class =>
        GetCapabilitySource(capability) switch
        {
            DeviceCapabilitySource.Real => physical
                ?? throw new InvalidOperationException(
                    $"Real capability {capability} is not available from the physical device."
                ),
            DeviceCapabilitySource.Simulated => simulated,
            DeviceCapabilitySource.Disabled => null,
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    private DeviceCapabilities CreateCapabilities()
    {
        var eegProvider =
            GetCapabilitySource(DeviceCapabilityKind.EegAcquisition) == DeviceCapabilitySource.Real
                ? _physical?.Capabilities
                : _simulation.Capabilities;
        var stimulationProvider =
            GetCapabilitySource(DeviceCapabilityKind.Stimulation) == DeviceCapabilitySource.Real
                ? _physical?.Capabilities
                : _simulation.Capabilities;
        return new DeviceCapabilities(
            CanReadStatus: true,
            CanAcquireEeg: EegAcquisition is not null,
            CanStimulate: Stimulation is not null,
            CanCheckEegImpedance: GetCapabilitySource(DeviceCapabilityKind.EegImpedance)
                != DeviceCapabilitySource.Disabled,
            CanCheckStimulationImpedance: GetCapabilitySource(
                DeviceCapabilityKind.StimulationImpedance
            ) != DeviceCapabilitySource.Disabled,
            CanRunToleranceTest: Tolerance is not null,
            MaximumEegChannels: eegProvider?.MaximumEegChannels ?? 0,
            SupportedSampleRatesHz: eegProvider?.SupportedSampleRatesHz ?? new HashSet<int>(),
            StimulationPhysicalChannelCount: stimulationProvider?.StimulationPhysicalChannelCount
                ?? 0
        );
    }

    private DeviceStateSnapshot ResolveOperationState(DeviceStateSnapshot statusState)
    {
        if (
            _physical is not null
            && IsSelectedOperation(_physical.State.Operation, DeviceCapabilitySource.Real)
        )
            return _physical.State;
        if (IsSelectedOperation(_simulation.State.Operation, DeviceCapabilitySource.Simulated))
            return _simulation.State;
        return statusState;
    }

    private bool IsSelectedOperation(
        DeviceOperationState operation,
        DeviceCapabilitySource source
    ) =>
        operation switch
        {
            DeviceOperationState.Acquiring => GetCapabilitySource(
                DeviceCapabilityKind.EegAcquisition
            ) == source,
            DeviceOperationState.Stimulating or DeviceOperationState.Stopping =>
                GetCapabilitySource(DeviceCapabilityKind.Stimulation) == source,
            DeviceOperationState.ImpedanceChecking => GetCapabilitySource(
                DeviceCapabilityKind.EegImpedance
            ) == source
                || GetCapabilitySource(DeviceCapabilityKind.StimulationImpedance) == source,
            DeviceOperationState.Faulted => true,
            _ => false,
        };

    private async IAsyncEnumerable<DeviceEventEnvelope> MergeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var sources = new List<(IEggtCsDevice Device, DeviceCapabilitySource Source)>();
        if (_readPhysicalEvents && _physical is not null)
            sources.Add((_physical, DeviceCapabilitySource.Real));
        if (_readSimulationEvents)
            sources.Add((_simulation, DeviceCapabilitySource.Simulated));
        if (sources.Count == 0)
            yield break;

        var channel = Channel.CreateBounded<DeviceEventEnvelope>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = sources.Count == 1,
                AllowSynchronousContinuations = false,
            }
        );
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var remainingProducers = sources.Count;

        async Task PumpAsync(IEggtCsDevice device, DeviceCapabilitySource source)
        {
            try
            {
                await foreach (
                    var item in device
                        .ReadEventsAsync(linkedCancellation.Token)
                        .ConfigureAwait(false)
                )
                {
                    if (ShouldForward(source, item.Event))
                        await channel
                            .Writer.WriteAsync(item, linkedCancellation.Token)
                            .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
                linkedCancellation.Cancel();
            }
            finally
            {
                if (Interlocked.Decrement(ref remainingProducers) == 0)
                    channel.Writer.TryComplete();
            }
        }

        var pumps = sources.Select(source => PumpAsync(source.Device, source.Source)).ToArray();
        var events = channel.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation is the normal way consumers stop the merged event stream.
                    // End the iterator cleanly so it does not surface as an unhandled user
                    // exception in the debugger or leak into the page layer.
                    break;
                }

                if (!hasNext)
                    break;
                yield return events.Current;
            }
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
            linkedCancellation.Cancel();
            foreach (var pump in pumps)
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    private bool ShouldForward(DeviceCapabilitySource source, DeviceEvent deviceEvent) =>
        deviceEvent switch
        {
            EegSamplesReceivedEvent
            or EegDataPacketReceivedEvent
            or UninterpretedEegPacketEvent
            or AcquisitionCompletedEvent => GetCapabilitySource(DeviceCapabilityKind.EegAcquisition)
                == source,
            StimulationCompletedEvent => GetCapabilitySource(DeviceCapabilityKind.Stimulation)
                == source,
            EegImpedanceReceivedEvent => GetCapabilitySource(DeviceCapabilityKind.EegImpedance)
                == source,
            StimulationImpedanceReceivedEvent => GetCapabilitySource(
                DeviceCapabilityKind.StimulationImpedance
            ) == source,
            DeviceProgressEvent progress => IsSelectedOperation(progress.Operation, source)
                || progress.Operation == DeviceOperationState.Ready,
            DeviceStateChangedEvent => _options.ConnectionSource switch
            {
                DeviceConnectionSource.Real => source == DeviceCapabilitySource.Real,
                DeviceConnectionSource.Simulated => source == DeviceCapabilitySource.Simulated,
                _ => false,
            },
            _ => false,
        };

    private static DateTimeOffset Latest(
        DateTimeOffset first,
        DateTimeOffset second,
        DateTimeOffset? third,
        DateTimeOffset fourth
    ) => new[] { first, second, third ?? DateTimeOffset.MinValue, fourth }.Max();

    private sealed class ConfigurableImpedanceCapability(
        IImpedanceCapability? eeg,
        IImpedanceCapability? stimulation
    ) : IImpedanceCapability
    {
        public Task<DeviceCommandResult> StartEegAsync(
            IReadOnlySet<int> physicalChannels,
            CancellationToken cancellationToken = default
        ) =>
            (eeg ?? throw Disabled("EEG impedance")).StartEegAsync(
                physicalChannels,
                cancellationToken
            );

        public Task<DeviceCommandResult> StopEegAsync(
            IReadOnlySet<int> physicalChannels,
            CancellationToken cancellationToken = default
        ) =>
            (eeg ?? throw Disabled("EEG impedance")).StopEegAsync(
                physicalChannels,
                cancellationToken
            );

        public Task<DeviceCommandResult> StartStimulationAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        ) =>
            (stimulation ?? throw Disabled("Stimulation impedance")).StartStimulationAsync(
                configuration,
                cancellationToken
            );

        public Task<DeviceCommandResult> StopStimulationAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        ) =>
            (stimulation ?? throw Disabled("Stimulation impedance")).StopStimulationAsync(
                configuration,
                cancellationToken
            );

        private static NotSupportedException Disabled(string capability) =>
            new($"{capability} is disabled by DeviceBackend configuration.");
    }
}
