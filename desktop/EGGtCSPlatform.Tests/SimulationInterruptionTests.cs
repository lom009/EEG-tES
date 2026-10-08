using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SimulationInterruptionTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(12)]
    public async Task RuntimeSimulationCompletesFullAcquisitionAndAutomaticStages(int seconds)
    {
        await using var runtime = new DeviceRuntimeBuilder()
            .UseSimulation()
            .AddProtocol(new EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsProtocolModule())
            .AddTransport(new ForbiddenTransport())
            .Build();
        DeviceCandidate? candidate = null;
        await foreach (var found in runtime.Discovery.DiscoverAsync(TimeSpan.Zero))
            candidate = found;
        var device = await runtime.ConnectVerifiedAsync(candidate!);
        using var service = new DeviceExperimentRunService(runtime.Devices, new Mapping());
        var samples = 0;
        var stages = new System.Collections.Concurrent.ConcurrentQueue<ExperimentRunStage>();
        service.TelemetryReceived += (_, e) =>
        {
            stages.Enqueue(e.Telemetry.Stage);
            foreach (var batch in e.Telemetry.WaveformBatches)
                Interlocked.Add(ref samples, batch.Samples.Count);
        };
        await service
            .StartAutomaticExperimentAsync(
                new(
                    TimeSpan.FromSeconds(seconds),
                    TimeSpan.FromMilliseconds(30),
                    TimeSpan.FromMilliseconds(60),
                    TimeSpan.FromMilliseconds(30),
                    1,
                    1,
                    ["F3"],
                    500,
                    DeviceId: device.Identity.DeviceId.Value
                )
            )
            .WaitAsync(TimeSpan.FromSeconds(seconds + 5));
        Assert.Equal(seconds * 500, samples);
        Assert.Equal(
            new[]
            {
                ExperimentRunStage.Acquisition,
                ExperimentRunStage.Blanking,
                ExperimentRunStage.Stimulation,
                ExperimentRunStage.Recovery,
            },
            stages.Distinct()
        );
        // A later acquisition on the same runtime must get its own completion signal.
        await service
            .StartAcquisitionAsync(
                new(TimeSpan.FromMilliseconds(100), ["F3"], 500, device.Identity.DeviceId.Value)
            )
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(seconds * 500 + 50, samples);
    }

    private sealed class ForbiddenTransport : ITransportFactory
    {
        public ITransport Create(TransportEndpoint endpoint) =>
            throw new InvalidOperationException("Physical transport used");
    }

    [Fact]
    public async Task ConcurrentStopAndDisposeWaitForProducerAndKeepDeviceDisconnected()
    {
        var device = new SimulatedEggtCsDevice();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.TryStartOperation(DeviceOperationState.Acquiring, _ => release.Task);
        var stop = device.EegAcquisition.StopAsync();
        var dispose = device.DisposeAsync().AsTask();
        var secondDispose = device.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.Same(dispose, secondDispose);
        release.SetResult();
        await Task.WhenAll(stop, dispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DeviceConnectionState.Disconnected, device.State.Connection);
    }

    [Fact]
    public async Task BackgroundProducerFailureEndsSubscriptionWithOriginalException()
    {
        await using var device = new SimulatedEggtCsDevice();
        await using var subscription = device.SubscribeEvents();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("producer failed");
        device.TryStartOperation(DeviceOperationState.Acquiring, _ => release.Task);
        release.SetException(expected);
        var error = await Record.ExceptionAsync(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await foreach (var _ in subscription.ReadEventsAsync(timeout.Token)) { }
        });
        Assert.Same(expected, error);
        Assert.Equal(DeviceConnectionState.Faulted, device.State.Connection);
    }

    private sealed class Mapping : IEegPhysicalChannelMappingService
    {
        public EegPhysicalChannelMappingSnapshot Load() => new(32, [new("F3", 1)], []);

        public void Save(int count, IReadOnlyList<EegPhysicalChannelMapping> mappings) =>
            throw new NotSupportedException();

        public string StoragePath => "memory";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedSimulationEndsOperationInsteadOfWaitingForCompletion(
        bool stimulation
    )
    {
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(manager, new Mapping());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.TelemetryReceived += (_, _) => received.TrySetResult();
        var running = stimulation
            ? service.StartStimulationAsync(
                new(
                    TimeSpan.FromMinutes(1),
                    1,
                    TimeSpan.Zero,
                    ["F3"],
                    500,
                    DeviceId: device.Identity.DeviceId.Value
                ),
                cancellation.Token
            )
            : service.StartAcquisitionAsync(
                new(TimeSpan.FromMinutes(1), ["F3"], 500, device.Identity.DeviceId.Value),
                cancellation.Token
            );
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await manager.RemoveAsync(device.Identity.DeviceId);
        var error = await Record.ExceptionAsync(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        if (stimulation)
            Assert.IsType<StimulationException>(error);
        else
            Assert.IsType<EegAcquisitionException>(error);
    }

    [Fact]
    public async Task StopAfterDisposeIsSafeAndCannotResurrectConnection()
    {
        var device = new SimulatedEggtCsDevice();
        await device.EegAcquisition.StartAsync(
            TimeSpan.FromMinutes(1),
            new HashSet<int> { 1 },
            500
        );
        await Task.WhenAll(device.DisposeAsync().AsTask(), device.DisposeAsync().AsTask());
        await device.EegAcquisition.StopAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, device.State.Connection);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            device.EegAcquisition.StartAsync(TimeSpan.FromSeconds(1), new HashSet<int> { 1 }, 500)
        );
    }

    [Fact]
    public async Task DisconnectStopsProducerAndPublishesTerminalState()
    {
        await using var device = new SimulatedEggtCsDevice();
        await using var subscription = device.SubscribeEvents();
        await device.EegAcquisition.StartAsync(
            TimeSpan.FromMinutes(1),
            new HashSet<int> { 1 },
            500
        );
        await device.DisconnectAsync();
        await device.EegAcquisition.StopAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, device.State.Connection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var item in subscription.ReadEventsAsync(timeout.Token))
            if (item.Event is DeviceStateChangedEvent changed)
            {
                Assert.Equal(DeviceConnectionState.Disconnected, changed.State.Connection);
                return;
            }
        Assert.Fail("Missing disconnect event");
    }
}
