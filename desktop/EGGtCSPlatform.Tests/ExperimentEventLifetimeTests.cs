using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ExperimentEventLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamTerminationFailsActiveAcquisitionWithoutWaitingForDuration(
        bool overflow
    )
    {
        var device = new ControlledDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(manager, new Mapping());
        var running = Acquire(service);
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failure = overflow
            ? new DeviceSubscriptionOverflowException(EventDeliveryClass.Data)
            : null;
        device.Events.Writer.TryComplete(failure);
        var error = await Assert.ThrowsAsync<EegAcquisitionException>(() =>
            running.WaitAsync(TimeSpan.FromSeconds(2))
        );
        if (overflow)
            Assert.Same(failure, error.InnerException);
        Assert.Equal(1, device.Stops);
    }

    [Fact]
    public async Task ClosedSubscriptionBeforeStartNeverSendsAcquisitionCommand()
    {
        var device = new ControlledDevice();
        device.Events.Writer.TryComplete();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(manager, new Mapping());
        await Assert.ThrowsAsync<EegAcquisitionException>(() =>
            Acquire(service).WaitAsync(TimeSpan.FromSeconds(2))
        );
        Assert.False(device.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task CompletionDoesNotHideSubsequentDisconnectBeforeTimeWindowEnds()
    {
        var device = new ControlledDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(manager, new Mapping());
        var running = Acquire(service);
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        device.Send(new AcquisitionCompletedEvent());
        device.Events.Writer.TryComplete();
        await Assert.ThrowsAsync<EegAcquisitionException>(() =>
            running.WaitAsync(TimeSpan.FromSeconds(2))
        );
    }

    [Fact]
    public async Task OldDeviceCompletionAndClosureCannotCompleteOrFailReplacementAcquisition()
    {
        var old = new ControlledDevice();
        var replacement = new ControlledDevice();
        await using var manager = new SwappableManager(old);
        using var service = new DeviceExperimentRunService(manager, new Mapping());
        using var cancel = new CancellationTokenSource();
        var oldRun = Acquire(service, cancel.Token);
        await old.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldRun);
        manager.Current = replacement;
        var newRun = service.StartAcquisitionAsync(new(TimeSpan.Zero, ["F3"], 500, "controlled"));
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        old.Send(new AcquisitionCompletedEvent());
        old.Events.Writer.TryComplete(new InvalidOperationException("old connection failure"));
        await old.StreamEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // The new completion is delivered after the old stream has terminated.
        replacement.Send(new AcquisitionCompletedEvent());
        await newRun.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, replacement.Stops);
    }

    private static Task Acquire(
        DeviceExperimentRunService service,
        CancellationToken token = default
    ) =>
        service.StartAcquisitionAsync(
            new(TimeSpan.FromMinutes(10), ["F3"], 500, "controlled"),
            token
        );

    private sealed class Mapping : IEegPhysicalChannelMappingService
    {
        public EegPhysicalChannelMappingSnapshot Load() => new(32, [new("F3", 1)], []);

        public void Save(int count, IReadOnlyList<EegPhysicalChannelMapping> mappings) =>
            throw new NotSupportedException();

        public string StoragePath => "memory";
    }

    private sealed class ControlledDevice
        : IEggtCsDevice,
            IDeviceCapabilitySourceProfile,
            IEegAcquisitionCapability
    {
        public Channel<DeviceEventEnvelope> Events { get; } =
            Channel.CreateUnbounded<DeviceEventEnvelope>();
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StreamEnded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public DeviceIdentity Identity { get; } = new(new("controlled"), "Controlled", null, null);
        public DeviceCapabilities Capabilities => DeviceCapabilities.Simulator;
        public DeviceStateSnapshot State { get; private set; } =
            new(
                DeviceConnectionState.Connected,
                DeviceOperationState.Ready,
                100,
                DateTimeOffset.UtcNow
            );
        public IEegAcquisitionCapability EegAcquisition => this;
        public IStimulationCapability? Stimulation => null;
        public IImpedanceCapability? Impedance => null;
        public IToleranceCapability? Tolerance => null;

        public DeviceCapabilitySource GetCapabilitySource(DeviceCapabilityKind kind) =>
            DeviceCapabilitySource.Simulated;

        public Task<DeviceStatusResponse> ReadStatusAsync(CancellationToken token = default) =>
            Task.FromResult(
                new DeviceStatusResponse(DeviceCommandStatus.Success, State.Operation, 100)
            );

        public Task<DeviceCommandResult> StartAsync(
            TimeSpan duration,
            IReadOnlySet<int> channels,
            int rate,
            CancellationToken token = default
        )
        {
            Started.TrySetResult();
            return Task.FromResult(DeviceCommandResult.Success);
        }

        public Task<DeviceCommandResult> StopAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref Stops);
            return Task.FromResult(DeviceCommandResult.Success);
        }

        public void Send(DeviceEvent item) =>
            Events.Writer.TryWrite(
                new(
                    Identity.DeviceId,
                    Guid.Empty,
                    DateTimeOffset.UtcNow,
                    item,
                    EventDeliveryClass.Control
                )
            );

        public async IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken token = default
        )
        {
            try
            {
                await foreach (var item in Events.Reader.ReadAllAsync(token))
                    yield return item;
            }
            finally
            {
                StreamEnded.TrySetResult();
            }
        }

        public ValueTask DisconnectAsync(CancellationToken token = default)
        {
            State = State with { Connection = DeviceConnectionState.Disconnected };
            Events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SwappableManager(IEggtCsDevice device) : IDeviceManager
    {
        public IEggtCsDevice Current = device;
        public IReadOnlyCollection<IEggtCsDevice> Devices => [Current];

        public bool TryGet(DeviceId id, out IEggtCsDevice? found)
        {
            found = Current;
            return id == Current.Identity.DeviceId;
        }

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken token = default
        ) => Task.FromResult(Current);

        public Task DisconnectAsync(DeviceId id, CancellationToken token = default) =>
            Current.DisconnectAsync(token).AsTask();

        public Task RemoveAsync(DeviceId id, CancellationToken token = default) =>
            Current.DisposeAsync().AsTask();

        public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            TimeSpan window,
            [EnumeratorCancellation] CancellationToken token = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => Current.DisposeAsync();
    }
}
