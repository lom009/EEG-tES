using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Bootstrap;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Options;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceManagerAndApplicationTests
{
    [Theory]
    [InlineData(2, ExperimentRunStage.Completed)]
    [InlineData(0, ExperimentRunStage.Acquisition)]
    [InlineData(0, ExperimentRunStage.Blanking)]
    public async Task AcquisitionOnlyAutomaticWorksWithoutStimulationCapability(
        int cycles,
        ExperimentRunStage stopAt
    )
    {
        var device = new AcquisitionOnlyDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService([new EegPhysicalChannelMapping("F3", 1)])
        );
        var received = new ConcurrentQueue<ExperimentRunTelemetryEventArgs>();
        var reachedStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        service.TelemetryReceived += (_, args) =>
        {
            received.Enqueue(args);
            if (args.Telemetry.CurrentCycle == 2 && args.Telemetry.Stage == stopAt)
                reachedStop.TrySetResult();
        };
        var running = service.StartAutomaticExperimentAsync(
            new AutomaticExperimentRunRequest(
                TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(20),
                cycles,
                2,
                ["F3"],
                500,
                DeviceId: DeviceId.Simulator.Value,
                CreationMode: ExperimentCreationMode.AcquisitionOnly
            )
        );
        if (cycles == 0)
        {
            await reachedStop.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.EmergencyStopAsync(DeviceId.Simulator.Value);
        }
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(
            received,
            x => x.Telemetry.Stage is ExperimentRunStage.Stimulation or ExperimentRunStage.Recovery
        );
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
        Assert.Contains(received, x => x.Telemetry.CurrentCycle == 2);
        var secondCycleSamples = received
            .Where(x => x.Telemetry.CurrentCycle == 2)
            .SelectMany(x => x.Telemetry.WaveformBatches)
            .ToArray();
        if (cycles > 0)
        {
            Assert.Equal(
                new[]
                {
                    (1, ExperimentRunStage.Acquisition),
                    (1, ExperimentRunStage.Blanking),
                    (2, ExperimentRunStage.Acquisition),
                    (2, ExperimentRunStage.Blanking),
                },
                GetStageTransitions(received.ToList())
            );
            Assert.NotEmpty(secondCycleSamples);
            Assert.Equal(0.09, secondCycleSamples.Min(x => x.StartTimeSeconds), 8);
        }
    }

    private sealed class AcquisitionOnlyDevice : IEggtCsDevice, IDeviceCapabilitySourceProfile
    {
        private readonly SimulatedEggtCsDevice _inner = new();
        public DeviceIdentity Identity => _inner.Identity;
        public DeviceCapabilities Capabilities => _inner.Capabilities;
        public DeviceStateSnapshot State => _inner.State;
        public IEegAcquisitionCapability? EegAcquisition => _inner.EegAcquisition;
        public IStimulationCapability? Stimulation => null;
        public IImpedanceCapability? Impedance => null;
        public IToleranceCapability? Tolerance => null;

        public DeviceCapabilitySource GetCapabilitySource(DeviceCapabilityKind capability) =>
            capability == DeviceCapabilityKind.EegAcquisition
                ? DeviceCapabilitySource.Simulated
                : DeviceCapabilitySource.Disabled;

        public Task<DeviceStatusResponse> ReadStatusAsync(CancellationToken token = default) =>
            _inner.ReadStatusAsync(token);

        public IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
            CancellationToken token = default
        ) => _inner.ReadEventsAsync(token);

        public DeviceEventSubscription SubscribeEvents(
            DeviceEventSubscriptionOptions? options = null
        ) => _inner.SubscribeEvents(options);

        public ValueTask DisconnectAsync(CancellationToken token = default) =>
            _inner.DisconnectAsync(token);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [Theory]
    [InlineData(500, 0.014d)]
    [InlineData(250, 0.028d)]
    public void EegPacketTimelineAnchorsFirstSampleToStageOffset(
        int sampleRateHz,
        double expectedPacketSpanSeconds
    )
    {
        var packet = new EegDataPacketReceivedEvent(
            TimeSpan.FromSeconds(99),
            80,
            8,
            [
                new EegPacketChannelSamples(
                    3,
                    Enumerable.Range(1, 8).Select(value => (double)value).ToArray()
                ),
            ],
            0
        );

        var batch = Assert.Single(
            EegPacketTimelineMapper.CreateBatches(
                packet,
                new Dictionary<int, string> { [3] = "F3" },
                sampleRateHz,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(2)
            )
        );

        Assert.Equal(4d, batch.StartTimeSeconds, 12);
        Assert.Equal(1d / sampleRateHz, batch.SampleIntervalSeconds, 12);
        Assert.Equal(
            4d + expectedPacketSpanSeconds,
            batch.StartTimeSeconds + (batch.Samples.Count - 1) * batch.SampleIntervalSeconds,
            12
        );
        Assert.Equal("F3", batch.ChannelId);
    }

    [Fact]
    public void EegPacketTimelineKeepsPacketSpacingStableWhenProcessingIsDelayed()
    {
        var channelNames = new Dictionary<int, string> { [3] = "F3" };
        var timeline = new EegSampleTimeline(500);
        var packet = new EegDataPacketReceivedEvent(
            TimeSpan.Zero,
            80,
            8,
            [
                new EegPacketChannelSamples(
                    3,
                    Enumerable.Range(1, 8).Select(value => (double)value).ToArray()
                ),
            ],
            0
        );

        var first = Assert.Single(
            EegPacketTimelineMapper.CreateBatches(
                packet,
                channelNames,
                500,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10),
                timeline
            )
        );
        var delayed = Assert.Single(
            EegPacketTimelineMapper.CreateBatches(
                packet,
                channelNames,
                500,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10),
                timeline
            )
        );

        var expectedNextStart =
            first.StartTimeSeconds + first.Samples.Count * first.SampleIntervalSeconds;
        Assert.Equal(expectedNextStart, delayed.StartTimeSeconds, 12);
    }

    [Theory]
    [InlineData(250, 1)]
    [InlineData(250, 8)]
    [InlineData(500, 1)]
    [InlineData(500, 8)]
    public void EegSampleTimelineAdvancesOnlyBySampleCount(int sampleRateHz, int sampleCount)
    {
        var timeline = new EegSampleTimeline(sampleRateHz);

        var first = timeline.GetNextPacketFirstSampleTicks(sampleCount);
        var second = timeline.GetNextPacketFirstSampleTicks(sampleCount);

        Assert.Equal(0, first);
        Assert.Equal(sampleCount * TimeSpan.TicksPerSecond / sampleRateHz, second);
    }

    [Theory]
    [InlineData(500, 0.010, 6)]
    [InlineData(500, 0.012, 7)]
    [InlineData(500, 0.014, 8)]
    [InlineData(250, 0.012, 4)]
    [InlineData(250, 0.028, 8)]
    public void EegPacketTimelineClipsSamplesAfterAcquisitionDuration(
        int sampleRateHz,
        double durationSeconds,
        int expectedSamples
    )
    {
        var packet = new EegDataPacketReceivedEvent(
            TimeSpan.Zero,
            80,
            8,
            [
                new EegPacketChannelSamples(
                    3,
                    Enumerable.Range(1, 8).Select(value => (double)value).ToArray()
                ),
            ],
            0
        );

        var batches = EegPacketTimelineMapper.CreateBatches(
            packet,
            new Dictionary<int, string> { [3] = "F3" },
            sampleRateHz,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(durationSeconds)
        );

        var batch = Assert.Single(batches);
        Assert.Equal(expectedSamples, batch.Samples.Count);
        Assert.Equal(0d, batch.StartTimeSeconds, 12);
        Assert.True(
            batch.StartTimeSeconds + (batch.Samples.Count - 1) * batch.SampleIntervalSeconds
                <= durationSeconds
        );
    }

    [Fact]
    public void EegPacketTimelineDoesNotShiftFirstPacketForReceiveDelay()
    {
        var packet = new EegDataPacketReceivedEvent(
            TimeSpan.Zero,
            80,
            8,
            [
                new EegPacketChannelSamples(
                    3,
                    Enumerable.Range(1, 8).Select(value => (double)value).ToArray()
                ),
            ],
            0
        );

        var batch = Assert.Single(
            EegPacketTimelineMapper.CreateBatches(
                packet,
                new Dictionary<int, string> { [3] = "F3" },
                500,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1)
            )
        );

        Assert.Equal(8, batch.Samples.Count);
        Assert.Equal(0d, batch.StartTimeSeconds, 12);
    }

    [Fact]
    public async Task ConfigurableSimulationBackendRunsDiscoveryConnectionImpedanceAndExperimentChain()
    {
        var backendOptions = new DeviceBackendOptions
        {
            ConnectionSource = DeviceConnectionSource.Simulated,
            Capabilities = new DeviceCapabilitySourceOptions
            {
                Status = DeviceCapabilitySource.Simulated,
                EegAcquisition = DeviceCapabilitySource.Simulated,
                Stimulation = DeviceCapabilitySource.Simulated,
                EegImpedance = DeviceCapabilitySource.Simulated,
                StimulationImpedance = DeviceCapabilitySource.Simulated,
                Tolerance = DeviceCapabilitySource.Simulated,
            },
        };
        var backend = new ConfigurableDeviceBackend(
            backendOptions,
            new DeviceConnectionNetworkContext()
        );
        await using var manager = new DeviceManager(discoveries: [backend], connectors: [backend]);
        var candidates = new List<DeviceCandidate>();
        await foreach (var candidate in manager.DiscoverAsync(TimeSpan.FromMilliseconds(10)))
            candidates.Add(candidate);
        var simulator = Assert.Single(candidates);
        Assert.Equal("simulator", simulator.Endpoint.Scheme);
        Assert.Equal("EGG/tCS Simulator", simulator.Identity.Model);

        var device = await manager.ConnectAsync(simulator);
        var status = await device.ReadStatusAsync();
        Assert.IsAssignableFrom<IEggtCsDevice>(device);
        Assert.Equal(
            DeviceCapabilitySource.Simulated,
            device.CapabilitySource(DeviceCapabilityKind.EegAcquisition)
        );
        Assert.Equal(DeviceCommandStatus.Success, status.Status);
        Assert.Equal(DeviceConnectionState.Connected, device.State.Connection);

        var mappings = new InMemoryChannelMappingService([
            new EegPhysicalChannelMapping("F3", 1),
            new EegPhysicalChannelMapping("C3", 2),
        ]);
        var impedance = new DeviceImpedanceDetectionService(manager, mappings);
        var targetId = Guid.NewGuid();
        StimulusElectrodeAssignment[] assignments =
        [
            new("F3", targetId, 1, StimulationChannelRole.FixedActive),
            new("C3", targetId, 2, StimulationChannelRole.Selectable),
        ];
        var stimulationRequest = CreateStimulationImpedanceRequest(assignments);

        var firstEeg = await impedance.StartEegAsync(device.Identity.DeviceId.Value, ["F3", "C3"]);
        await impedance.StopEegAsync(device.Identity.DeviceId.Value, ["F3", "C3"]);
        var secondEeg = await impedance.StartEegAsync(device.Identity.DeviceId.Value, ["F3", "C3"]);
        await impedance.StopEegAsync(device.Identity.DeviceId.Value, ["F3", "C3"]);
        var firstStimulation = await impedance.StartStimulationAsync(
            device.Identity.DeviceId.Value,
            stimulationRequest
        );
        await impedance.StopStimulationAsync(device.Identity.DeviceId.Value, stimulationRequest);
        var secondStimulation = await impedance.StartStimulationAsync(
            device.Identity.DeviceId.Value,
            stimulationRequest
        );
        await impedance.StopStimulationAsync(device.Identity.DeviceId.Value, stimulationRequest);

        Assert.Contains(firstEeg.Values, value => value > 10d);
        Assert.All(secondEeg.Values, value => Assert.True(value <= 10d));
        Assert.Contains(firstStimulation.Values, value => value > 10d);
        Assert.All(secondStimulation.Values, value => Assert.True(value <= 10d));

        using var run = new DeviceExperimentRunService(manager, mappings);
        var received = new List<ExperimentRunTelemetry>();
        run.TelemetryReceived += (_, args) => received.Add(args.Telemetry);
        await run.StartAcquisitionAsync(
            new AcquisitionRunRequest(
                TimeSpan.FromMilliseconds(120),
                ["F3", "C3"],
                500,
                device.Identity.DeviceId.Value
            )
        );
        await run.StartStimulationAsync(
            new StimulationRunRequest(
                TimeSpan.FromMilliseconds(120),
                1.25d,
                TimeSpan.FromMilliseconds(120),
                ["F3", "C3"],
                500,
                DeviceId: device.Identity.DeviceId.Value
            )
        );
        await run.StartAutomaticExperimentAsync(
            new AutomaticExperimentRunRequest(
                TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(30),
                1,
                1.25d,
                ["F3", "C3"],
                500,
                DeviceId: device.Identity.DeviceId.Value
            )
        );

        Assert.Contains(received, item => item.WaveformBatches.Count == 2);
        Assert.Contains(
            received,
            item => item.Stage == ExperimentRunStage.Stimulation && item.ActualCurrentMilliAmps > 0d
        );
        Assert.Contains(received, item => item.Stage == ExperimentRunStage.Blanking);
        Assert.Contains(received, item => item.Stage == ExperimentRunStage.Recovery);

        await manager.DisconnectAsync(device.Identity.DeviceId);
        Assert.Equal(DeviceConnectionState.Disconnected, device.State.Connection);
    }

    [Fact]
    public async Task CapabilityStopIsScopedToOneDevice()
    {
        var first = new SimulatedEggtCsDevice(
            new DeviceIdentity(new DeviceId("sim-a"), "Simulator A", "A", null)
        );
        var second = new SimulatedEggtCsDevice(
            new DeviceIdentity(new DeviceId("sim-b"), "Simulator B", "B", null)
        );
        await using var manager = new DeviceManager(connectedDevices: [first, second]);

        var firstStart = await first.Stimulation.StartAsync(TimeSpan.FromMilliseconds(300));
        var secondStart = await second.Stimulation.StartAsync(TimeSpan.FromMilliseconds(300));
        Assert.True(firstStart.IsSuccess);
        Assert.True(secondStart.IsSuccess);
        Assert.Equal(DeviceOperationState.Stimulating, first.State.Operation);
        Assert.Equal(DeviceOperationState.Stimulating, second.State.Operation);

        await first.Stimulation.StopAsync();

        Assert.Equal(DeviceOperationState.Ready, first.State.Operation);
        Assert.Equal(DeviceOperationState.Stimulating, second.State.Operation);
        Assert.True(manager.TryGet(new DeviceId("sim-b"), out var resolved));
        Assert.Same(second, resolved);
    }

    [Fact]
    public async Task CommunicationServicesRejectDeviceThatRemainsRegisteredButDisconnected()
    {
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var mappings = new InMemoryChannelMappingService([new EegPhysicalChannelMapping("F3", 1)]);
        using var runService = new DeviceExperimentRunService(manager, mappings);
        var impedanceService = new DeviceImpedanceDetectionService(manager, mappings);
        await device.DisconnectAsync();

        var runException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runService.StartAcquisitionAsync(
                new AcquisitionRunRequest(
                    TimeSpan.FromMilliseconds(10),
                    ["F3"],
                    500,
                    DeviceId.Simulator.Value
                )
            )
        );
        var impedanceException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            impedanceService.StartEegAsync(DeviceId.Simulator.Value, ["F3"])
        );

        Assert.Contains("not connected", runException.Message);
        Assert.Contains("not connected", impedanceException.Message);
    }

    [Fact]
    public async Task CommunicationServicesRejectInvalidEegMappingConfiguration()
    {
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var mappings = new InMemoryChannelMappingService(
            [new EegPhysicalChannelMapping("F3", 1)],
            ["物理通道重复分配：1。"]
        );
        using var runService = new DeviceExperimentRunService(manager, mappings);
        var impedanceService = new DeviceImpedanceDetectionService(manager, mappings);

        var runException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runService.StartAcquisitionAsync(
                new AcquisitionRunRequest(
                    TimeSpan.FromMilliseconds(10),
                    ["F3"],
                    500,
                    DeviceId.Simulator.Value
                )
            )
        );
        var impedanceException = await Assert.ThrowsAsync<ImpedanceDetectionConfigurationException>(
            () =>
                impedanceService.StartEegAsync(DeviceId.Simulator.Value, ["F3"])
        );

        Assert.Contains("EEG采集物理通道配置无效", runException.Message, StringComparison.Ordinal);
        Assert.Contains(
            "EEG采集物理通道配置无效",
            impedanceException.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ManualAcquisitionPublishesEveryConfiguredElectrode()
    {
        string[] channelIds = ["F3", "C3", "Cz", "P3", "CP4", "FP2"];
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService(
                channelIds
                    .Select((channel, index) => new EegPhysicalChannelMapping(channel, index + 1))
                    .ToArray()
            )
        );
        var received = new List<ExperimentRunTelemetryEventArgs>();
        service.TelemetryReceived += (_, args) => received.Add(args);

        await service.StartAcquisitionAsync(
            new AcquisitionRunRequest(
                TimeSpan.FromMilliseconds(120),
                channelIds,
                500,
                DeviceId.Simulator.Value,
                TimelineOffset: TimeSpan.FromSeconds(5)
            )
        );

        Assert.NotEmpty(received);
        Assert.All(received, item => Assert.Equal(DeviceId.Simulator.Value, item.DeviceId));
        Assert.Contains(
            received,
            item => item.Telemetry.WaveformBatches.Count == channelIds.Length
        );
        Assert.All(
            received,
            item => Assert.True(item.Telemetry.TotalElapsed >= TimeSpan.FromSeconds(5))
        );
        Assert.All(
            received.SelectMany(item => item.Telemetry.WaveformBatches),
            batch => Assert.True(batch.StartTimeSeconds >= 5d)
        );
        Assert.Equal(
            5d,
            received
                .SelectMany(item => item.Telemetry.WaveformBatches)
                .Min(batch => batch.StartTimeSeconds),
            12
        );
        Assert.Equal(
            channelIds.OrderBy(item => item),
            received
                .SelectMany(item => item.Telemetry.WaveformBatches)
                .Select(item => item.ChannelId)
                .Distinct()
                .OrderBy(item => item)
        );
        var f3Samples = received
            .SelectMany(item => item.Telemetry.WaveformBatches)
            .Where(item => item.ChannelId == "F3")
            .SelectMany(item => item.Samples)
            .ToArray();
        var c3Samples = received
            .SelectMany(item => item.Telemetry.WaveformBatches)
            .Where(item => item.ChannelId == "C3")
            .SelectMany(item => item.Samples)
            .ToArray();
        Assert.True(f3Samples.Max() - f3Samples.Min() > 20d);
        Assert.False(f3Samples.Take(20).SequenceEqual(c3Samples.Take(20)));
        Assert.Contains(received, item => item.Telemetry.AverageImpedanceKiloOhms == 3.9d);
    }

    [Fact]
    public async Task AutomaticExperimentPublishesFourStagesAndAllChannelsAcrossCycles()
    {
        string[] channelIds = ["F3", "C3", "Cz", "P3", "CP4", "FP2"];
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService(
                channelIds
                    .Select((channel, index) => new EegPhysicalChannelMapping(channel, index + 1))
                    .ToArray()
            )
        );
        var received = new List<ExperimentRunTelemetryEventArgs>();
        service.TelemetryReceived += (_, args) => received.Add(args);
        var cycleDuration = TimeSpan.FromMilliseconds(180);

        await service.StartAutomaticExperimentAsync(
            new AutomaticExperimentRunRequest(
                AcquisitionDuration: TimeSpan.FromMilliseconds(60),
                BlankingDuration: TimeSpan.FromMilliseconds(30),
                StimulationDuration: TimeSpan.FromMilliseconds(60),
                RecoveryDuration: TimeSpan.FromMilliseconds(30),
                CycleCount: 2,
                TargetCurrentMilliAmps: 1.5d,
                ChannelIds: channelIds,
                SampleRateHz: 500,
                DeviceId: DeviceId.Simulator.Value
            )
        );

        Assert.All(received, item => Assert.Equal(DeviceId.Simulator.Value, item.DeviceId));
        Assert.Equal(
            [
                (1, ExperimentRunStage.Acquisition),
                (1, ExperimentRunStage.Blanking),
                (1, ExperimentRunStage.Stimulation),
                (1, ExperimentRunStage.Recovery),
                (2, ExperimentRunStage.Acquisition),
                (2, ExperimentRunStage.Blanking),
                (2, ExperimentRunStage.Stimulation),
                (2, ExperimentRunStage.Recovery),
            ],
            GetStageTransitions(received)
        );

        foreach (var cycle in new[] { 1, 2 })
        {
            Assert.Equal(
                channelIds.OrderBy(item => item),
                received
                    .Where(item => item.Telemetry.CurrentCycle == cycle)
                    .SelectMany(item => item.Telemetry.WaveformBatches)
                    .Select(item => item.ChannelId)
                    .Distinct()
                    .OrderBy(item => item)
            );
        }

        var secondCycleStarts = received
            .Where(item => item.Telemetry.CurrentCycle == 2)
            .SelectMany(item => item.Telemetry.WaveformBatches)
            .Select(item => item.StartTimeSeconds)
            .ToArray();
        Assert.NotEmpty(secondCycleStarts);
        Assert.All(secondCycleStarts, value => Assert.True(value >= cycleDuration.TotalSeconds));
        Assert.Equal(cycleDuration.TotalSeconds, secondCycleStarts.Min(), 12);
    }

    [Fact]
    public async Task InfiniteAutomaticExperimentRunsUntilEmergencyStop()
    {
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService([
                new EegPhysicalChannelMapping("F3", 1),
                new EegPhysicalChannelMapping("C3", 2),
            ])
        );
        var reachedSecondCycle = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        service.TelemetryReceived += (_, args) =>
        {
            if (args.Telemetry.CurrentCycle >= 2)
                reachedSecondCycle.TrySetResult();
        };

        var running = service.StartAutomaticExperimentAsync(
            new AutomaticExperimentRunRequest(
                AcquisitionDuration: TimeSpan.FromMilliseconds(30),
                BlankingDuration: TimeSpan.FromMilliseconds(20),
                StimulationDuration: TimeSpan.FromMilliseconds(30),
                RecoveryDuration: TimeSpan.FromMilliseconds(20),
                CycleCount: 0,
                TargetCurrentMilliAmps: 1d,
                ChannelIds: ["F3", "C3"],
                SampleRateHz: 500,
                DeviceId: DeviceId.Simulator.Value
            )
        );

        await reachedSecondCycle.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(running.IsCompleted);
        await service.EmergencyStopAsync(DeviceId.Simulator.Value);
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
    }

    [Fact]
    public async Task ExperimentRunViewModelCompletesAutomaticSimulatorFlowWithEveryElectrode()
    {
        string[] channelIds = ["F3", "C3", "Cz", "P3", "CP4", "FP2"];
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService(
                channelIds
                    .Select((channel, index) => new EegPhysicalChannelMapping(channel, index + 1))
                    .ToArray()
            )
        );
        var defaults = ExperimentRunRouteDataDefaults.Create();
        var route = new ExperimentRunRouteData(
            defaults.ExperimentId,
            defaults.SubjectId,
            defaults.StimulusConfiguration with
            {
                RampSeconds = 0d,
            },
            defaults.StimulusElectrodes,
            channelIds,
            defaults.ReferenceChannel,
            defaults.GroundChannel,
            defaults.SampleRateHz,
            DeviceId.Simulator.Value
        );
        using var model = new ExperimentRunPageViewModel(
            route,
            new NullRouter(),
            service,
            timingOptions: new ExperimentRunTimingOptions
            {
                DurationStepMilliseconds = 10,
                DurationMinimumMilliseconds = 10,
            }
        );
        foreach (var stage in model.Stages)
        {
            stage.SelectedUnit = model.DurationUnits[0];
            stage.DurationValue = stage.Stage
                is ExperimentRunStage.Acquisition
                    or ExperimentRunStage.Stimulation
                ? 50m
                : 20m;
        }
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 2;

        await model.StartAutomaticExperimentCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunPageState.Completed, model.PageState);
        Assert.Equal(2, model.CurrentCycle);
        Assert.True(model.HasWaveformData);
        Assert.All(
            model.WaveformChannels,
            channel => Assert.True(channel.HistoryDurationSeconds > 0d)
        );
        Assert.All(
            model.Stages,
            stage => Assert.Equal(ExperimentStageStatus.Completed, stage.Status)
        );
    }

    [Fact]
    public async Task ImpedanceAttemptsAreIndependentForEegAndStimulation()
    {
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var service = new DeviceImpedanceDetectionService(
            manager,
            new InMemoryChannelMappingService([
                new EegPhysicalChannelMapping("F3", 3),
                new EegPhysicalChannelMapping("C3", 7),
            ])
        );
        var targetId = Guid.NewGuid();
        StimulusElectrodeAssignment[] stimulationAssignments =
        [
            new("F3", targetId, 1, StimulationChannelRole.FixedActive),
            new("C3", targetId, 2, StimulationChannelRole.Selectable),
        ];
        var stimulationRequest = CreateStimulationImpedanceRequest(stimulationAssignments);

        var firstEeg = await service.StartEegAsync(DeviceId.Simulator.Value, ["F3", "C3"]);
        var secondEeg = await service.StartEegAsync(DeviceId.Simulator.Value, ["F3", "C3"]);
        var firstStimulation = await service.StartStimulationAsync(
            DeviceId.Simulator.Value,
            stimulationRequest
        );
        var secondStimulation = await service.StartStimulationAsync(
            DeviceId.Simulator.Value,
            stimulationRequest
        );

        Assert.Equal(15d, firstEeg["F3"]);
        Assert.All(secondEeg.Values, value => Assert.Equal(8d, value));
        Assert.Single(firstStimulation);
        Assert.Equal(15d, firstStimulation["C3"]);
        Assert.DoesNotContain("F3", firstStimulation.Keys);
        Assert.All(secondStimulation.Values, value => Assert.Equal(8d, value));
    }

    [Fact]
    public async Task DeviceImpedanceWatchStreamsSimulationReadingsEveryHalfSecond()
    {
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var service = new DeviceImpedanceDetectionService(
            manager,
            new InMemoryChannelMappingService([
                new EegPhysicalChannelMapping("F3", 3),
                new EegPhysicalChannelMapping("C3", 7),
            ])
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var stream = service
            .WatchEegAsync(DeviceId.Simulator.Value, ["F3", "C3"], cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await stream.MoveNextAsync());
        var first = stream.Current;
        Assert.True(await stream.MoveNextAsync());
        var second = stream.Current;

        Assert.Equal(15d, first["F3"]);
        Assert.Equal(8d, second["F3"]);
        await service.StopEegAsync(DeviceId.Simulator.Value, ["F3", "C3"]);
        cancellation.Cancel();
    }

    [Fact]
    public async Task PhysicalEegWatchBuffersPushBeforeResponseAndReturnsOnlyRequestedChannels()
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults()),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(
                new DeviceId("EEG-HYBRID"),
                "ESP32_EEG",
                "EEG-HYBRID",
                "B8:F8:62:69:82:A8"
            ),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var heartbeatPause = new DeviceHeartbeatPauseService();
        var impedance = new DeviceImpedanceDetectionService(
            manager,
            new InMemoryChannelMappingService([
                new EegPhysicalChannelMapping("F3", 3),
                new EegPhysicalChannelMapping("Cz", 18),
            ]),
            heartbeatPause
        );
        using var cancellation = new CancellationTokenSource();
        await using var watch = impedance
            .WatchEegAsync(device.Identity.DeviceId.Value, ["F3", "Cz"], cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var firstReading = watch.MoveNextAsync().AsTask();
        while (transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var startPacket = transport.SentPackets[0];
        Assert.True(heartbeatPause.IsPaused(device.Identity.DeviceId));
        Assert.Equal((byte)EggtCsCommandCode.ConfigureEegImpedanceRequest, startPacket[4]);
        Assert.Equal(
            new byte[] { 0x01, 0xFB, 0xFF, 0xFD, 0xFF },
            startPacket.AsSpan(5, 5).ToArray()
        );

        var firstPush = new byte[33];
        firstPush[0] = 0x03;
        firstPush[2] = 0x01;
        firstPush[17] = 0x05;
        firstPush[32] = 0x04;
        transport.Inject(
            codec.Encode(new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, firstPush))
        );
        await Task.Delay(20, timeout.Token);
        Assert.False(firstReading.IsCompleted);

        transport.Inject(
            codec.Encode(
                new WireMessage(
                    startPacket[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        Assert.True(await firstReading);
        Assert.Equal(2, watch.Current.Count);
        Assert.Equal(8d, watch.Current["F3"]);
        Assert.Equal(45d, watch.Current["Cz"]);
        Assert.DoesNotContain("channel-33", watch.Current.Keys);

        var secondReading = watch.MoveNextAsync().AsTask();
        var secondPush = new byte[33];
        secondPush[2] = 0x02;
        secondPush[17] = 0x00;
        secondPush[32] = 0x05;
        transport.Inject(
            codec.Encode(
                new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, secondPush)
            )
        );
        Assert.True(await secondReading);
        Assert.Single(watch.Current);
        Assert.Equal(15d, watch.Current["F3"]);
        Assert.DoesNotContain("Cz", watch.Current.Keys);

        cancellation.Cancel();
        var stop = impedance.StopEegAsync(device.Identity.DeviceId.Value, ["F3", "Cz"]);
        while (transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var stopPacket = transport.SentPackets[1];
        Assert.Equal((byte)EggtCsCommandCode.ConfigureEegImpedanceRequest, stopPacket[4]);
        Assert.Equal(
            new byte[] { 0x02, 0xFB, 0xFF, 0xFD, 0xFF },
            stopPacket.AsSpan(5, 5).ToArray()
        );
        transport.Inject(
            codec.Encode(
                new WireMessage(
                    stopPacket[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        await stop;
        Assert.False(heartbeatPause.IsPaused(device.Identity.DeviceId));
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
    }

    [Fact]
    public async Task PhysicalEegWatchTimesOutStopsOnceAndDoesNotReplayLatePushIntoNextRun()
    {
        var setup = await CreatePhysicalEegServiceAsync("EEG-TIMEOUT-HYBRID");
        await using var manager = setup.Manager;
        using var firstCancellation = new CancellationTokenSource();
        await using var firstWatch = setup
            .Service.WatchEegAsync(setup.DeviceId, ["F3"], firstCancellation.Token)
            .GetAsyncEnumerator(firstCancellation.Token);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        var firstReading = firstWatch.MoveNextAsync().AsTask();
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, testTimeout.Token);
        var firstStart = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    firstStart[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        var firstPush = new byte[33];
        firstPush[2] = 0x01;
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, firstPush)
            )
        );
        Assert.True(await firstReading);

        var timedOutReading = firstWatch.MoveNextAsync().AsTask();
        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(5, testTimeout.Token);
        var automaticStop = setup.Transport.SentPackets[1];
        Assert.Equal((byte)EggtCsCommandCode.ConfigureEegImpedanceRequest, automaticStop[4]);
        Assert.Equal((byte)0x02, automaticStop[5]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    automaticStop[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        var timeoutException = await Assert.ThrowsAsync<EegImpedanceDataTimeoutException>(() =>
            timedOutReading
        );
        Assert.Equal(DeviceImpedanceDetectionService.EegDataTimeout, timeoutException.Timeout);
        Assert.True(timeoutException.StopCommandRequested);
        Assert.False(setup.HeartbeatPause.IsPaused(new DeviceId(setup.DeviceId)));
        Assert.Equal(2, setup.Transport.SentPackets.Count);

        var latePush = new byte[33];
        latePush[2] = 0x05;
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, latePush)
            )
        );
        await Task.Delay(50, testTimeout.Token);

        using var secondCancellation = new CancellationTokenSource();
        await using var secondWatch = setup
            .Service.WatchEegAsync(setup.DeviceId, ["F3"], secondCancellation.Token)
            .GetAsyncEnumerator(secondCancellation.Token);
        var nextRunReading = secondWatch.MoveNextAsync().AsTask();
        while (setup.Transport.SentPackets.Count < 3)
            await Task.Delay(1, testTimeout.Token);
        var secondStart = setup.Transport.SentPackets[2];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    secondStart[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        await Task.Delay(50, testTimeout.Token);
        Assert.False(nextRunReading.IsCompleted);

        var freshPush = new byte[33];
        freshPush[2] = 0x02;
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, freshPush)
            )
        );
        Assert.True(await nextRunReading);
        Assert.Equal(15d, secondWatch.Current["F3"]);

        secondCancellation.Cancel();
        var manualStop = setup.Service.StopEegAsync(setup.DeviceId, ["F3"]);
        while (setup.Transport.SentPackets.Count < 4)
            await Task.Delay(1, testTimeout.Token);
        var manualStopPacket = setup.Transport.SentPackets[3];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    manualStopPacket[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        await manualStop;
    }

    [Theory]
    [InlineData(0x01, DeviceCommandStatus.InvalidParameter)]
    [InlineData(0x02, DeviceCommandStatus.DeviceBusy)]
    public async Task PhysicalEegRejectedStartDiscardsPushBufferedBeforeResponse(
        byte responseStatus,
        DeviceCommandStatus expectedStatus
    )
    {
        var setup = await CreatePhysicalEegServiceAsync($"EEG-REJECT-{responseStatus}");
        await using var manager = setup.Manager;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var watch = setup
            .Service.WatchEegAsync(setup.DeviceId, ["F3"], cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var reading = watch.MoveNextAsync().AsTask();
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, cancellation.Token);
        var start = setup.Transport.SentPackets[0];
        var bufferedPush = new byte[33];
        bufferedPush[2] = 0x01;
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, bufferedPush)
            )
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new[] { responseStatus }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<DeviceCommandRejectedException>(() => reading);
        Assert.Equal(expectedStatus, exception.Status);
        Assert.False(setup.HeartbeatPause.IsPaused(new DeviceId(setup.DeviceId)));
        Assert.Single(setup.Transport.SentPackets);
    }

    [Fact]
    public async Task PhysicalStimulationImpedanceRejectionPreservesDeviceStatusForUiDialog()
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults()),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(
                new DeviceId("TES-HYBRID"),
                "ESP32_EEG",
                "TES-HYBRID",
                "B8:F8:62:69:82:A9"
            ),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var heartbeatPause = new DeviceHeartbeatPauseService();
        var service = new DeviceImpedanceDetectionService(
            manager,
            new InMemoryChannelMappingService([]),
            heartbeatPause
        );
        var targetId = Guid.NewGuid();
        StimulusElectrodeAssignment[] assignments =
        [
            new("F3", targetId, 7, StimulationChannelRole.FixedActive),
            new("C3", targetId, 1, StimulationChannelRole.Selectable),
        ];
        var stimulationRequest = CreateStimulationImpedanceRequest(assignments);

        var start = service.StartStimulationAsync(
            device.Identity.DeviceId.Value,
            stimulationRequest
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (transport.SentPackets.Count == 0)
            await Task.Delay(1, timeout.Token);
        var request = Assert.Single(transport.SentPackets);
        transport.Inject(
            codec.Encode(
                new WireMessage(
                    request[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x02 }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<DeviceCommandRejectedException>(() => start);
        Assert.Equal(DeviceCommandStatus.DeviceBusy, exception.Status);
        Assert.Contains("启动刺激阻抗检测", exception.Operation, StringComparison.Ordinal);
        Assert.False(heartbeatPause.IsPaused(device.Identity.DeviceId));
        Assert.Single(transport.SentPackets);
    }

    [Fact]
    public async Task PhysicalEegAcquisitionUsesRealPacketsAndIgnoresReportedRemainingTime()
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults()),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(
                new DeviceId("EEG-ACQUISITION-HYBRID"),
                "ESP32_EEG",
                "EEG-ACQUISITION-HYBRID",
                "B8:F8:62:69:82:AD"
            ),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var heartbeatPause = new DeviceHeartbeatPauseService();
        using var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService([new EegPhysicalChannelMapping("F3", 3)]),
            heartbeatPause: heartbeatPause
        );
        var received = new ConcurrentQueue<ExperimentRunTelemetry>();
        service.TelemetryReceived += (_, args) => received.Enqueue(args.Telemetry);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        var acquisition = service.StartAcquisitionAsync(
            new AcquisitionRunRequest(
                TimeSpan.FromSeconds(1),
                ["F3"],
                500,
                device.Identity.DeviceId.Value
            ),
            timeout.Token
        );
        Assert.True(heartbeatPause.IsPaused(device.Identity.DeviceId));
        while (transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var startPacket = transport.SentPackets[0];
        Assert.Equal((byte)EggtCsCommandCode.ControlAcquisitionRequest, startPacket[4]);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00 }, startPacket[5..^2]);
        Assert.Contains(received, telemetry => telemetry.Stage == ExperimentRunStage.Acquisition);

        transport.Inject(
            codec.Encode(
                new WireMessage(
                    startPacket[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        await Task.Delay(20, timeout.Token);
        var payload = new byte[3 + 32 * 8 * 3 + 1];
        payload[0] = 99;
        payload[2] = 32;
        payload[^1] = 73;
        for (var sample = 0; sample < 8; sample++)
        {
            var offset = 3 + (sample * 32 + 2) * 3;
            payload[offset + 2] = (byte)(sample + 1);
        }
        transport.Inject(CreateV101DataFrame(1, (byte)EggtCsCommandCode.EegData, payload));
        transport.Inject(
            codec.Encode(
                new WireMessage(
                    1,
                    (byte)EggtCsCommandCode.AcquisitionCompleted,
                    ReadOnlyMemory<byte>.Empty
                )
            )
        );

        await acquisition;
        var waveform = Assert.Single(
            received
                .SelectMany(telemetry => telemetry.WaveformBatches)
                .Where(batch => batch.ChannelId == "F3")
        );
        Assert.All(
            received.Where(telemetry => telemetry.WaveformBatches.Count > 0),
            telemetry => Assert.True(telemetry.IsWaveformOnly)
        );
        Assert.Equal(8, waveform.Samples.Count);
        Assert.Equal(1d / 500d, waveform.SampleIntervalSeconds, 12);
        Assert.DoesNotContain(
            received,
            telemetry => telemetry.StageElapsed >= TimeSpan.FromSeconds(99)
        );
        Assert.DoesNotContain(
            transport.SentPackets,
            packet => packet[4] == (byte)EggtCsCommandCode.StartAutomaticExperimentRequest
        );
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
        Assert.False(heartbeatPause.IsPaused(device.Identity.DeviceId));
    }

    [Fact]
    public async Task PhysicalStimulationUsesManualPacketsAndLocalProgress()
    {
        var setup = await CreatePhysicalStimulationServiceAsync("TES-RUN-HYBRID");
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var request = CreateStimulationRunRequest(setup.DeviceId, TimeSpan.FromSeconds(1));
        var received = new ConcurrentQueue<ExperimentRunTelemetry>();
        setup.Service.TelemetryReceived += (_, args) => received.Enqueue(args.Telemetry);

        var stimulation = setup.Service.StartStimulationAsync(request, timeout.Token);
        Assert.True(setup.HeartbeatPause.IsPaused(new DeviceId(setup.DeviceId)));
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var configurePacket = setup.Transport.SentPackets[0];
        Assert.Equal(
            (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
            configurePacket[4]
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    configurePacket[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var startPacket = setup.Transport.SentPackets[1];
        Assert.Equal((byte)EggtCsCommandCode.ControlStimulationRequest, startPacket[4]);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00 }, startPacket[5..^2]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    startPacket[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    0x01,
                    (byte)EggtCsCommandCode.StimulationProgress,
                    new byte[] { 0x63, 0x00, 0x51 }
                )
            )
        );

        await Task.Delay(TimeSpan.FromMilliseconds(1050), timeout.Token);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    0x02,
                    (byte)EggtCsCommandCode.StimulationCompleted,
                    ReadOnlyMemory<byte>.Empty
                )
            )
        );
        await stimulation;

        Assert.Contains(
            received,
            telemetry =>
                telemetry.Stage == ExperimentRunStage.Stimulation
                && telemetry.StageProgress == 1d
                && telemetry.StageElapsed == TimeSpan.FromSeconds(1)
        );
        Assert.DoesNotContain(
            received,
            telemetry => telemetry.StageElapsed >= TimeSpan.FromSeconds(99)
        );
        Assert.DoesNotContain(
            setup.Transport.SentPackets,
            packet => packet[4] == (byte)EggtCsCommandCode.StartAutomaticExperimentRequest
        );
        Assert.Equal(DeviceOperationState.Ready, setup.Device.State.Operation);
        Assert.False(setup.HeartbeatPause.IsPaused(new DeviceId(setup.DeviceId)));
    }

    [Fact]
    public async Task PhysicalAutomaticRunIsOrchestratedWithManualCommandsOnly()
    {
        var setup = await CreatePhysicalStimulationServiceAsync("TES-AUTO-MANUAL-COMMANDS");
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var stimulation = CreateStimulationRunRequest(setup.DeviceId, TimeSpan.FromSeconds(1));
        var automatic = setup.Service.StartAutomaticExperimentAsync(
            new AutomaticExperimentRunRequest(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10),
                1,
                1d,
                ["F3"],
                500,
                stimulation.StimulusConfiguration,
                stimulation.StimulusElectrodes,
                setup.DeviceId
            ),
            timeout.Token
        );

        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var acquisitionPacket = setup.Transport.SentPackets[0];
        Assert.Equal((byte)EggtCsCommandCode.ControlAcquisitionRequest, acquisitionPacket[4]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    acquisitionPacket[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        setup.Transport.Inject(
            CreateV101DataFrame(
                0x01,
                (byte)EggtCsCommandCode.EegData,
                CreateEegPayload(sampleCount: 1, rawValue: 1)
            )
        );

        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var configurePacket = setup.Transport.SentPackets[1];
        Assert.Equal(
            (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
            configurePacket[4]
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    configurePacket[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        while (setup.Transport.SentPackets.Count < 3)
            await Task.Delay(1, timeout.Token);
        var startPacket = setup.Transport.SentPackets[2];
        Assert.Equal((byte)EggtCsCommandCode.ControlStimulationRequest, startPacket[4]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    startPacket[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    0x02,
                    (byte)EggtCsCommandCode.StimulationProgress,
                    new byte[] { 0x01, 0x00, 0x50 }
                )
            )
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    0x03,
                    (byte)EggtCsCommandCode.StimulationCompleted,
                    ReadOnlyMemory<byte>.Empty
                )
            )
        );

        await automatic;
        Assert.Equal(
            new byte[]
            {
                (byte)EggtCsCommandCode.ControlAcquisitionRequest,
                (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
                (byte)EggtCsCommandCode.ControlStimulationRequest,
            },
            setup.Transport.SentPackets.Select(packet => packet[4]).ToArray()
        );
        Assert.DoesNotContain(
            setup.Transport.SentPackets,
            packet => packet[4] == (byte)EggtCsCommandCode.StartAutomaticExperimentRequest
        );
    }

    [Fact]
    public async Task PhysicalStimulationProgressTimeoutStopsExactlyOnce()
    {
        var setup = await CreatePhysicalStimulationServiceAsync(
            "TES-PROGRESS-TIMEOUT",
            progressTimeout: TimeSpan.FromMilliseconds(100),
            completionGrace: TimeSpan.FromMilliseconds(100)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var stimulation = setup.Service.StartStimulationAsync(
            CreateStimulationRunRequest(setup.DeviceId, TimeSpan.FromSeconds(1)),
            timeout.Token
        );
        await RespondToStimulationConfigurationAndStartAsync(setup, timeout.Token);

        while (setup.Transport.SentPackets.Count < 3)
            await Task.Delay(1, timeout.Token);
        var stopPacket = setup.Transport.SentPackets[2];
        Assert.Equal((byte)EggtCsCommandCode.ControlStimulationRequest, stopPacket[4]);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, stopPacket[5..^2]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stopPacket[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<StimulationException>(() => stimulation);
        Assert.Equal(StimulationFailureKind.ProgressPacketTimeout, exception.Kind);
        Assert.True(exception.StopCommandRequested);
        Assert.Single(
            setup.Transport.SentPackets.Where(packet =>
                packet[4] == (byte)EggtCsCommandCode.ControlStimulationRequest && packet[5] == 0x02
            )
        );
    }

    [Fact]
    public async Task PhysicalStimulationStartResponseTimeoutStopsAndPreservesFailureKind()
    {
        var setup = await CreatePhysicalStimulationServiceAsync(
            "TES-START-TIMEOUT",
            requestTimeout: TimeSpan.FromMilliseconds(100)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var stimulation = setup.Service.StartStimulationAsync(
            CreateStimulationRunRequest(setup.DeviceId, TimeSpan.FromSeconds(1)),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var configurePacket = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    configurePacket[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        while (setup.Transport.SentPackets.Count < 3)
            await Task.Delay(1, timeout.Token);
        var startPacket = setup.Transport.SentPackets[1];
        var stopPacket = setup.Transport.SentPackets[2];
        Assert.Equal((byte)0x01, startPacket[5]);
        Assert.Equal((byte)0x02, stopPacket[5]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stopPacket[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<StimulationException>(() => stimulation);
        Assert.Equal(StimulationFailureKind.StartResponseTimeout, exception.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(100), exception.Timeout);
        Assert.True(exception.StopCommandRequested);
    }

    [Fact]
    public async Task PhysicalStimulationCompletionTimeoutStopsExactlyOnce()
    {
        var setup = await CreatePhysicalStimulationServiceAsync(
            "TES-COMPLETION-TIMEOUT",
            progressTimeout: TimeSpan.FromSeconds(2),
            completionGrace: TimeSpan.FromMilliseconds(100)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        var stimulation = setup.Service.StartStimulationAsync(
            CreateStimulationRunRequest(setup.DeviceId, TimeSpan.FromSeconds(1)),
            timeout.Token
        );
        await RespondToStimulationConfigurationAndStartAsync(setup, timeout.Token);

        while (setup.Transport.SentPackets.Count < 3)
            await Task.Delay(1, timeout.Token);
        var stopPacket = setup.Transport.SentPackets[2];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stopPacket[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x03 }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<StimulationException>(() => stimulation);
        Assert.Equal(StimulationFailureKind.CompletionEventTimeout, exception.Kind);
        Assert.True(exception.StopCommandRequested);
        Assert.Null(exception.StopFailure);
        Assert.Single(
            setup.Transport.SentPackets.Where(packet =>
                packet[4] == (byte)EggtCsCommandCode.ControlStimulationRequest && packet[5] == 0x02
            )
        );
    }

    [Fact]
    public async Task PhysicalStimulationEmergencyStopUsesManualStopAndAcceptsRampingDown()
    {
        var setup = await CreatePhysicalStimulationServiceAsync("TES-EMERGENCY-STOP");
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stimulation = setup.Service.StartStimulationAsync(
            CreateStimulationRunRequest(setup.DeviceId, TimeSpan.FromSeconds(10)),
            timeout.Token
        );
        await RespondToStimulationConfigurationAndStartAsync(setup, timeout.Token);

        var emergencyStop = setup.Service.EmergencyStopAsync(setup.DeviceId, timeout.Token);
        while (setup.Transport.SentPackets.Count < 3)
            await Task.Delay(1, timeout.Token);
        var stopPacket = setup.Transport.SentPackets[2];
        Assert.Equal((byte)EggtCsCommandCode.ControlStimulationRequest, stopPacket[4]);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, stopPacket[5..^2]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stopPacket[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x04 }
                )
            )
        );

        await emergencyStop;
        await stimulation;
        Assert.Equal(DeviceOperationState.Stopping, setup.Device.State.Operation);
        Assert.Single(
            setup.Transport.SentPackets.Where(packet =>
                packet[4] == (byte)EggtCsCommandCode.ControlStimulationRequest && packet[5] == 0x02
            )
        );
    }

    [Fact]
    public async Task PhysicalEegAcquisitionDataTimeoutStopsOnceAndReturnsTypedFailure()
    {
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-ACQUISITION-DATA-TIMEOUT",
            TimeSpan.FromMilliseconds(100)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(TimeSpan.FromSeconds(1), ["F3"], 500, setup.DeviceId),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var stop = setup.Transport.SentPackets[1];
        Assert.Equal((byte)EggtCsCommandCode.ControlAcquisitionRequest, stop[4]);
        Assert.Equal((byte)0x02, stop[5]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stop[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<EegAcquisitionException>(() => acquisition);
        Assert.Equal(EegAcquisitionFailureKind.DataPacketTimeout, exception.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(100), exception.Timeout);
        Assert.True(exception.StopCommandRequested);
        Assert.Equal(2, setup.Transport.SentPackets.Count);
    }

    [Fact]
    public async Task PhysicalEegAcquisitionDistinguishesEarlyCompletionAfterOnePacketFromMissingFirstPacket()
    {
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-ACQUISITION-EARLY-END-AFTER-DATA",
            TimeSpan.FromMilliseconds(100)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var dataReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        setup.Service.TelemetryReceived += (_, args) =>
        {
            if (args.Telemetry.WaveformBatches.Count > 0)
                dataReceived.TrySetResult();
        };

        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(TimeSpan.FromSeconds(5), ["F3"], 500, setup.DeviceId),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        setup.Transport.Inject(
            CreateV101DataFrame(
                1,
                (byte)EggtCsCommandCode.EegData,
                CreateEegPayload(sampleCount: 8, rawValue: 1)
            )
        );
        await dataReceived.Task.WaitAsync(timeout.Token);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    1,
                    (byte)EggtCsCommandCode.AcquisitionCompleted,
                    ReadOnlyMemory<byte>.Empty
                )
            )
        );

        var exception = await Assert.ThrowsAsync<EegAcquisitionException>(() => acquisition);
        Assert.Equal(EegAcquisitionFailureKind.DataPacketTimeout, exception.Kind);
        Assert.Contains("已收到 1 个 EEG 数据包", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "采集时间结束前上报了采集完成",
            exception.Message,
            StringComparison.Ordinal
        );
        Assert.False(exception.StopCommandRequested);
        Assert.Single(setup.Transport.SentPackets);
    }

    [Fact]
    public async Task PhysicalEegAcquisitionStartTimeoutStillSendsOneStopCommand()
    {
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-ACQUISITION-START-TIMEOUT",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(75)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(TimeSpan.FromSeconds(1), ["F3"], 500, setup.DeviceId),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var stop = setup.Transport.SentPackets[1];
        Assert.Equal((byte)0x02, stop[5]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stop[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<EegAcquisitionException>(() => acquisition);
        Assert.Equal(EegAcquisitionFailureKind.StartResponseTimeout, exception.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(75), exception.Timeout);
        Assert.True(exception.StopCommandRequested);
        Assert.Equal(2, setup.Transport.SentPackets.Count);
    }

    [Fact]
    public async Task PhysicalEegAcquisitionRejectedStartStopsOnceAndPreservesReason()
    {
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-ACQUISITION-START-REJECTED",
            TimeSpan.FromSeconds(1)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(TimeSpan.FromSeconds(1), ["F3"], 500, setup.DeviceId),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x02 }
                )
            )
        );
        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var stop = setup.Transport.SentPackets[1];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stop[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<EegAcquisitionException>(() => acquisition);
        Assert.Equal(EegAcquisitionFailureKind.StartRejected, exception.Kind);
        Assert.Contains(nameof(DeviceCommandStatus.DeviceBusy), exception.Message);
        Assert.True(exception.StopCommandRequested);
        Assert.Equal(2, setup.Transport.SentPackets.Count);
    }

    [Fact]
    public async Task PhysicalEegAcquisitionReportsStopFailureWithOriginalDataTimeout()
    {
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-ACQUISITION-STOP-FAILED",
            TimeSpan.FromMilliseconds(100)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(TimeSpan.FromSeconds(1), ["F3"], 500, setup.DeviceId),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var stop = setup.Transport.SentPackets[1];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stop[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x02 }
                )
            )
        );

        var exception = await Assert.ThrowsAsync<EegAcquisitionException>(() => acquisition);
        Assert.Equal(EegAcquisitionFailureKind.DataPacketTimeout, exception.Kind);
        Assert.True(exception.StopCommandRequested);
        Assert.NotNull(exception.StopFailure);
        Assert.Contains("停止采集指令失败", exception.Message);
        Assert.Equal(2, setup.Transport.SentPackets.Count);
    }

    [Fact]
    public async Task PhysicalEegAcquisitionCompletesWithoutCompletionEventWhilePacketsKeepArriving()
    {
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-ACQUISITION-NO-COMPLETION",
            TimeSpan.FromMilliseconds(150)
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(TimeSpan.FromSeconds(1), ["F3"], 500, setup.DeviceId),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        for (var index = 0; index < 11; index++)
        {
            setup.Transport.Inject(
                CreateV101DataFrame(
                    1,
                    (byte)EggtCsCommandCode.EegData,
                    CreateEegPayload(sampleCount: 1, rawValue: index + 1)
                )
            );
            await Task.Delay(90, timeout.Token);
        }

        await acquisition;
        Assert.Single(setup.Transport.SentPackets);
    }

    [Fact]
    public async Task PhysicalEegPacketIsRecordedBeforeWaveformConsumptionWithExactDatagram()
    {
        var recorder = new CapturingRawPacketRecorder();
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-RAW-RECORDING",
            TimeSpan.FromSeconds(2),
            rawPacketRecorder: recorder
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var recordingId = Guid.NewGuid();
        await setup.Service.BeginRecordingAsync(
            new EegRecordingMetadata(
                recordingId,
                "experiment",
                "subject",
                setup.DeviceId,
                500,
                ["F3"],
                DateTimeOffset.UtcNow,
                new EegDisplayFilterSettings(0.5d, 70d, null)
            )
        );
        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(
                TimeSpan.FromSeconds(1),
                ["F3"],
                500,
                setup.DeviceId,
                RecordingId: recordingId
            ),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        await Task.Delay(100, timeout.Token);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        var rawDatagram = CreateV101DataFrame(
            0,
            (byte)EggtCsCommandCode.EegData,
            CreateEegPayload(sampleCount: 8, rawValue: 123)
        );
        setup.Transport.Inject(rawDatagram);

        var recorded = await recorder.PacketReceived.Task.WaitAsync(timeout.Token);

        Assert.Equal(recordingId, recorded.RecordingId);
        Assert.Equal(1, recorded.Sequence);
        Assert.Equal(8, recorded.SampleCount);
        Assert.Equal(0d, recorded.TimelineStartSeconds, 12);
        Assert.Equal(rawDatagram, recorded.Datagram.ToArray());

        for (var index = 0; index < 11; index++)
        {
            await Task.Delay(90, timeout.Token);
            setup.Transport.Inject(rawDatagram);
        }
        await acquisition;
    }

    [Fact]
    public async Task PhysicalEegPacketsAreWrittenInIndexOrder()
    {
        var recorder = new CapturingRawPacketRecorder();
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-RAW-REORDERING",
            TimeSpan.FromSeconds(2),
            rawPacketRecorder: recorder
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var recordingId = Guid.NewGuid();
        await setup.Service.BeginRecordingAsync(
            new EegRecordingMetadata(
                recordingId,
                "experiment",
                "subject",
                setup.DeviceId,
                500,
                ["F3"],
                DateTimeOffset.UtcNow,
                new EegDisplayFilterSettings(0.5d, 70d, null)
            ),
            timeout.Token
        );
        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(
                TimeSpan.FromSeconds(1),
                ["F3"],
                500,
                setup.DeviceId,
                RecordingId: recordingId
            ),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        var packet0 = CreateV101DataFrame(
            0,
            (byte)EggtCsCommandCode.EegData,
            CreateEegPayload(8, 100)
        );
        var packet1 = CreateV101DataFrame(
            1,
            (byte)EggtCsCommandCode.EegData,
            CreateEegPayload(8, 200)
        );
        var packet2 = CreateV101DataFrame(
            2,
            (byte)EggtCsCommandCode.EegData,
            CreateEegPayload(8, 300)
        );
        setup.Transport.Inject(packet0);
        setup.Transport.Inject(packet2);
        await Task.Delay(10, timeout.Token);
        setup.Transport.Inject(packet1);
        while (recorder.PacketCount < 3)
            await Task.Delay(1, timeout.Token);

        var records = recorder.Packets.Take(3).ToArray();
        Assert.Equal(
            [packet0, packet1, packet2],
            records.Select(x => x.Datagram.ToArray()).ToArray()
        );
        Assert.Equal([0d, 0.016d, 0.032d], records.Select(x => x.TimelineStartSeconds).ToArray());

        await acquisition;
        await setup.Service.CompleteRecordingAsync(
            recordingId,
            EegRecordingCompletionStatus.Completed,
            timeout.Token
        );
    }

    [Fact]
    public async Task CompletingRecordingStopsLateAcquisitionPacketsFromReenteringRecorder()
    {
        var recorder = new CapturingRawPacketRecorder();
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-RAW-LATE-PACKET",
            TimeSpan.FromSeconds(2),
            rawPacketRecorder: recorder
        );
        await using var manager = setup.Manager;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var recordingId = Guid.NewGuid();
        await setup.Service.BeginRecordingAsync(
            new EegRecordingMetadata(
                recordingId,
                "experiment",
                "subject",
                setup.DeviceId,
                500,
                ["F3"],
                DateTimeOffset.UtcNow,
                new EegDisplayFilterSettings(0.5d, 70d, null)
            ),
            timeout.Token
        );
        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(
                TimeSpan.FromSeconds(1),
                ["F3"],
                500,
                setup.DeviceId,
                RecordingId: recordingId
            ),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        var datagram = CreateV101DataFrame(
            1,
            (byte)EggtCsCommandCode.EegData,
            CreateEegPayload(sampleCount: 8, rawValue: 123)
        );
        for (var index = 0; index < 11; index++)
        {
            setup.Transport.Inject(datagram);
            await Task.Delay(90, timeout.Token);
        }
        await acquisition;
        await setup.Service.CompleteRecordingAsync(
            recordingId,
            EegRecordingCompletionStatus.Completed,
            timeout.Token
        );
        var packetCountAtCompletion = recorder.PacketCount;

        setup.Transport.Inject(datagram);
        await Task.Delay(100, timeout.Token);

        Assert.Equal(1, recorder.CompletionCount);
        Assert.Equal(packetCountAtCompletion, recorder.PacketCount);
        Assert.Equal(0, recorder.AppendAfterCompletionAttempts);
    }

    [Fact]
    public async Task EegPacketAfterCompletionEventIsStillPublished()
    {
        var setup = await CreatePhysicalAcquisitionServiceAsync(
            "EEG-ACQUISITION-EARLY-COMPLETION",
            TimeSpan.FromSeconds(2)
        );
        await using var manager = setup.Manager;
        var telemetry = new ConcurrentQueue<ExperimentRunTelemetry>();
        setup.Service.TelemetryReceived += (_, args) => telemetry.Enqueue(args.Telemetry);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var acquisition = setup.Service.StartAcquisitionAsync(
            new AcquisitionRunRequest(TimeSpan.FromSeconds(1), ["F3"], 500, setup.DeviceId),
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var start = setup.Transport.SentPackets[0];
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    start[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        await Task.Delay(20, timeout.Token);
        setup.Transport.Inject(
            CreateV101DataFrame(
                0,
                (byte)EggtCsCommandCode.EegData,
                CreateEegPayload(sampleCount: 8, rawValue: 1)
            )
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    1,
                    (byte)EggtCsCommandCode.AcquisitionCompleted,
                    ReadOnlyMemory<byte>.Empty
                )
            )
        );
        await Task.Delay(30, timeout.Token);
        setup.Transport.Inject(
            CreateV101DataFrame(
                1,
                (byte)EggtCsCommandCode.EegData,
                CreateEegPayload(sampleCount: 8, rawValue: 2)
            )
        );

        await acquisition;
        Assert.True(telemetry.Count(item => item.WaveformBatches.Count > 0) >= 2);
    }

    [Fact]
    public async Task HybridPhysicalEegAcquisitionDoesNotUseSimulatedChannelFallback()
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults()),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(
                new DeviceId("EEG-MAPPING-HYBRID"),
                "ESP32_EEG",
                "EEG-MAPPING-HYBRID",
                "B8:F8:62:69:82:AE"
            ),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        await using var manager = new DeviceManager(connectedDevices: [device]);
        using var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService([])
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAcquisitionAsync(
                new AcquisitionRunRequest(
                    TimeSpan.FromSeconds(1),
                    ["F3"],
                    500,
                    device.Identity.DeviceId.Value
                )
            )
        );

        Assert.Contains("no physical channel mapping", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.SentPackets);
    }

    [Fact]
    public async Task PhysicalStimulationImpedanceSendsCompleteFrozenConfigurationForStartAndStop()
    {
        var setup = await CreatePhysicalEegServiceAsync("TES-COMPLETE-CONFIG");
        await using var manager = setup.Manager;
        var targetId = Guid.NewGuid();
        StimulusElectrodeAssignment[] assignments =
        [
            new("F3", targetId, 7, StimulationChannelRole.FixedActive),
            new("C3", targetId, 1, StimulationChannelRole.Selectable),
        ];
        var stimulationRequest = CreateStimulationImpedanceRequest(
            assignments,
            StimulusKind.TDcs,
            StimulusDirection.Negative,
            frequency: 40,
            dutyPercent: 79,
            rampSeconds: 7,
            current: 1
        );
        var expectedStartPayload = new byte[]
        {
            0x01,
            0x01,
            0x01,
            0x64,
            0x41,
            0x90,
            0x01,
            0x4F,
            0x07,
            0x01,
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var start = setup.Service.StartStimulationAsync(
            setup.DeviceId,
            stimulationRequest,
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var startPacket = setup.Transport.SentPackets[0];
        Assert.Equal((byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest, startPacket[4]);
        Assert.Equal(expectedStartPayload, startPacket[5..^2]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    startPacket[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    0x01,
                    (byte)EggtCsCommandCode.StimulationImpedanceData,
                    new byte[] { 0x01, 0x01, 0x01 }
                )
            )
        );
        Assert.Equal(8d, (await start)["C3"]);

        var stop = setup.Service.StopStimulationAsync(
            setup.DeviceId,
            stimulationRequest,
            timeout.Token
        );
        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var stopPacket = setup.Transport.SentPackets[1];
        var expectedStopPayload = expectedStartPayload.ToArray();
        expectedStopPayload[^1] = 0x02;
        Assert.Equal((byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest, stopPacket[4]);
        Assert.Equal(expectedStopPayload, stopPacket[5..^2]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    stopPacket[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        await stop;
    }

    [Fact]
    public async Task PhysicalStimulationImpedanceRejectsMultiTargetBeforeSending()
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults()),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(
                new DeviceId("TES-MULTI-HYBRID"),
                "ESP32_EEG",
                "TES-MULTI-HYBRID",
                "B8:F8:62:69:82:AA"
            ),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var service = new DeviceImpedanceDetectionService(
            manager,
            new InMemoryChannelMappingService([])
        );
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        StimulusElectrodeAssignment[] assignments =
        [
            new("F3", firstTarget, 7, StimulationChannelRole.FixedActive),
            new("C3", firstTarget, 1, StimulationChannelRole.Selectable),
            new("P3", secondTarget, 6, StimulationChannelRole.FixedActive),
            new("C4", secondTarget, 2, StimulationChannelRole.Selectable),
        ];
        var stimulationRequest = CreateStimulationImpedanceRequest(assignments);

        var exception = await Assert.ThrowsAsync<ImpedanceDetectionConfigurationException>(() =>
            service.StartStimulationAsync(device.Identity.DeviceId.Value, stimulationRequest)
        );

        Assert.Contains("单靶点", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.SentPackets);
    }

    [Fact]
    public async Task CancelingPhysicalStimulationWatchEndsCleanlyBeforeStopResponse()
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults()),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(
                new DeviceId("TES-CANCEL-HYBRID"),
                "ESP32_EEG",
                "TES-CANCEL-HYBRID",
                "B8:F8:62:69:82:AB"
            ),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var heartbeatPause = new DeviceHeartbeatPauseService();
        var service = new DeviceImpedanceDetectionService(
            manager,
            new InMemoryChannelMappingService([]),
            heartbeatPause
        );
        var targetId = Guid.NewGuid();
        StimulusElectrodeAssignment[] assignments =
        [
            new("F3", targetId, 7, StimulationChannelRole.FixedActive),
            new("C3", targetId, 1, StimulationChannelRole.Selectable),
        ];
        var stimulationRequest = CreateStimulationImpedanceRequest(assignments);
        using var cancellation = new CancellationTokenSource();
        await using var watch = service
            .WatchStimulationAsync(
                device.Identity.DeviceId.Value,
                stimulationRequest,
                cancellation.Token
            )
            .GetAsyncEnumerator(cancellation.Token);

        var nextReading = watch.MoveNextAsync().AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var startPacket = transport.SentPackets[0];
        Assert.True(heartbeatPause.IsPaused(device.Identity.DeviceId));
        transport.Inject(
            codec.Encode(
                new WireMessage(
                    startPacket[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );
        while (device.State.Operation != DeviceOperationState.ImpedanceChecking)
            await Task.Delay(1, timeout.Token);

        cancellation.Cancel();
        var stop = service.StopStimulationAsync(device.Identity.DeviceId.Value, stimulationRequest);
        while (transport.SentPackets.Count < 2)
            await Task.Delay(1, timeout.Token);
        var stopPacket = transport.SentPackets[1];
        transport.Inject(
            codec.Encode(
                new WireMessage(
                    stopPacket[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        await stop;
        Assert.False(heartbeatPause.IsPaused(device.Identity.DeviceId));
        Assert.False(await nextReading);
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
    }

    private static IReadOnlyList<(int Cycle, ExperimentRunStage Stage)> GetStageTransitions(
        IReadOnlyList<ExperimentRunTelemetryEventArgs> received
    )
    {
        var result = new List<(int Cycle, ExperimentRunStage Stage)>();
        foreach (var item in received)
        {
            var next = (item.Telemetry.CurrentCycle, item.Telemetry.Stage);
            if (result.Count == 0 || result[^1] != next)
                result.Add(next);
        }
        return result;
    }

    private static StimulationImpedanceDetectionRequest CreateStimulationImpedanceRequest(
        IReadOnlyList<StimulusElectrodeAssignment> assignments,
        StimulusKind kind = StimulusKind.TDcs,
        StimulusDirection direction = StimulusDirection.Positive,
        double frequency = 0.1d,
        double dutyPercent = 50d,
        double rampSeconds = 0d,
        double current = 0.04d
    )
    {
        var targets = assignments
            .GroupBy(assignment => assignment.TargetId)
            .Select(
                (group, index) =>
                {
                    var channels = group
                        .Select(assignment => new ExperimentStimulationChannelSnapshot(
                            assignment.PhysicalChannelId,
                            assignment.Role,
                            current
                        ))
                        .ToArray();
                    return new ExperimentStimulusTargetSnapshot(
                        group.Key,
                        index + 1,
                        current,
                        channels
                            .Single(channel => channel.Role == StimulationChannelRole.FixedActive)
                            .PhysicalChannelId,
                        channels
                    );
                }
            )
            .ToArray();
        var arrayMode =
            targets.Length > 1 ? StimulusArrayMode.MultiTarget
            : targets[0].Channels.Count > 2 ? StimulusArrayMode.Hd
            : StimulusArrayMode.DualChannel;
        return new StimulationImpedanceDetectionRequest(
            new ExperimentStimulusConfigurationSnapshot(
                kind,
                arrayMode,
                direction,
                ShamWaveformMode.Direct,
                rampSeconds,
                frequency,
                dutyPercent,
                targets
            ),
            assignments
        );
    }

    private static byte[] CreateV101DataFrame(byte index, byte command, byte[] payload)
    {
        var length = checked(8 + payload.Length);
        var frame = new byte[length];
        frame[0] = 0xAA;
        frame[1] = 0xBB;
        frame[2] = index;
        frame[3] = (byte)length;
        frame[4] = (byte)(length >> 8);
        frame[5] = command;
        payload.CopyTo(frame, 6);
        frame[^2] = 0xFF;
        frame[^1] = 0xFF;
        return frame;
    }

    private static byte[] CreateEegPayload(int sampleCount, int rawValue)
    {
        var payload = new byte[3 + 32 * sampleCount * 3 + 1];
        payload[2] = 32;
        payload[^1] = 80;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            for (var channel = 0; channel < 32; channel++)
            {
                var offset = 3 + (sample * 32 + channel) * 3;
                payload[offset] = (byte)(rawValue >> 16);
                payload[offset + 1] = (byte)(rawValue >> 8);
                payload[offset + 2] = (byte)rawValue;
            }
        }
        return payload;
    }

    private static StimulationRunRequest CreateStimulationRunRequest(
        string deviceId,
        TimeSpan duration
    )
    {
        var targetId = Guid.NewGuid();
        StimulusElectrodeAssignment[] assignments =
        [
            new("F3", targetId, 7, StimulationChannelRole.FixedActive),
            new("C3", targetId, 1, StimulationChannelRole.Selectable),
        ];
        var stimulation = CreateStimulationImpedanceRequest(
            assignments,
            StimulusKind.TDcs,
            StimulusDirection.Negative,
            frequency: 40,
            dutyPercent: 79,
            rampSeconds: 7,
            current: 1
        );
        return new StimulationRunRequest(
            duration,
            1d,
            TimeSpan.Zero,
            ["F3", "C3"],
            500,
            stimulation.Configuration,
            stimulation.Assignments,
            deviceId
        );
    }

    private static async Task VerifyPhysicalConnectionAsync(
        ConfigurableEggtCsDevice device,
        FakeTransport transport,
        EggtCsFrameCodec codec
    )
    {
        var statusTask = device.ReadStatusAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (transport.SentPackets.Count < 1)
            await Task.Delay(1, timeout.Token);
        var statusPacket = transport.SentPackets[0];
        Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, statusPacket[4]);
        transport.Inject(
            codec.Encode(
                new WireMessage(
                    statusPacket[2],
                    (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                    new byte[] { 0x01, 0x64 }
                )
            )
        );
        Assert.Equal(DeviceCommandStatus.Success, (await statusTask).Status);
        Assert.Equal(DeviceConnectionState.Connected, device.State.Connection);
        transport.ClearSentPackets();
    }

    private static async Task RespondToStimulationConfigurationAndStartAsync(
        (
            FakeTransport Transport,
            EggtCsFrameCodec Codec,
            DeviceManager Manager,
            DeviceExperimentRunService Service,
            ConfigurableEggtCsDevice Device,
            DeviceHeartbeatPauseService HeartbeatPause,
            string DeviceId
        ) setup,
        CancellationToken cancellationToken
    )
    {
        while (setup.Transport.SentPackets.Count < 1)
            await Task.Delay(1, cancellationToken);
        var configurePacket = setup.Transport.SentPackets[0];
        Assert.Equal(
            (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
            configurePacket[4]
        );
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    configurePacket[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            )
        );

        while (setup.Transport.SentPackets.Count < 2)
            await Task.Delay(1, cancellationToken);
        var startPacket = setup.Transport.SentPackets[1];
        Assert.Equal((byte)EggtCsCommandCode.ControlStimulationRequest, startPacket[4]);
        setup.Transport.Inject(
            setup.Codec.Encode(
                new WireMessage(
                    startPacket[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x00 }
                )
            )
        );
    }

    private static async Task<(
        FakeTransport Transport,
        EggtCsFrameCodec Codec,
        DeviceManager Manager,
        DeviceExperimentRunService Service,
        ConfigurableEggtCsDevice Device,
        DeviceHeartbeatPauseService HeartbeatPause,
        string DeviceId
    )> CreatePhysicalStimulationServiceAsync(
        string deviceId,
        TimeSpan? progressTimeout = null,
        TimeSpan? completionGrace = null,
        TimeSpan? requestTimeout = null
    )
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults(requestTimeout)),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(new DeviceId(deviceId), "ESP32_EEG", deviceId, "B8:F8:62:69:82:B0"),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        var manager = new DeviceManager(connectedDevices: [device]);
        var heartbeatPause = new DeviceHeartbeatPauseService();
        var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService([new EegPhysicalChannelMapping("F3", 3)]),
            stimulationRunOptions: Options.Create(
                new StimulationRunOptions
                {
                    ProgressPacketTimeout = progressTimeout ?? TimeSpan.FromMilliseconds(2500),
                    CompletionEventGracePeriod = completionGrace ?? TimeSpan.FromSeconds(2),
                }
            ),
            heartbeatPause: heartbeatPause
        );
        return (transport, codec, manager, service, device, heartbeatPause, deviceId);
    }

    private static async Task<(
        FakeTransport Transport,
        EggtCsFrameCodec Codec,
        DeviceManager Manager,
        DeviceExperimentRunService Service,
        string DeviceId
    )> CreatePhysicalAcquisitionServiceAsync(
        string deviceId,
        TimeSpan dataTimeout,
        TimeSpan? requestTimeout = null,
        IEegRawPacketRecorder? rawPacketRecorder = null
    )
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults(requestTimeout)),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(new DeviceId(deviceId), "ESP32_EEG", deviceId, "B8:F8:62:69:82:AF"),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        var manager = new DeviceManager(connectedDevices: [device]);
        var service = new DeviceExperimentRunService(
            manager,
            new InMemoryChannelMappingService([new EegPhysicalChannelMapping("F3", 3)]),
            eegAcquisitionOptions: Options.Create(
                new EegAcquisitionOptions { SampleRateHz = 500, DataPacketTimeout = dataTimeout }
            ),
            rawPacketRecorder: rawPacketRecorder
        );
        return (transport, codec, manager, service, deviceId);
    }

    private sealed class CapturingRawPacketRecorder : IEegRawPacketRecorder
    {
        private EegRecordingMetadata? _metadata;
        private int _active;
        private int _packetCount;
        private int _sampleBatchCount;
        private int _completionCount;
        private int _appendAfterCompletionAttempts;

        public event EventHandler<EegRawRecordingFailedEventArgs>? RecordingFailed
        {
            add { }
            remove { }
        }

        public TaskCompletionSource<EegRawPacketRecord> PacketReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<EegRawPacketRecord> Packets { get; } = new();

        public int PacketCount => Volatile.Read(ref _packetCount);
        public int SampleBatchCount => Volatile.Read(ref _sampleBatchCount);
        public int CompletionCount => Volatile.Read(ref _completionCount);
        public int AppendAfterCompletionAttempts =>
            Volatile.Read(ref _appendAfterCompletionAttempts);

        public ValueTask BeginAsync(
            EegRecordingMetadata metadata,
            CancellationToken cancellationToken = default
        )
        {
            _metadata = metadata;
            Volatile.Write(ref _active, 1);
            return ValueTask.CompletedTask;
        }

        public ValueTask AppendAsync(
            EegRawPacketRecord packet,
            CancellationToken cancellationToken = default
        )
        {
            if (Volatile.Read(ref _active) == 0)
            {
                Interlocked.Increment(ref _appendAfterCompletionAttempts);
                throw new InvalidOperationException(
                    $"EEG recording {packet.RecordingId} is not active."
                );
            }
            Interlocked.Increment(ref _packetCount);
            Packets.Enqueue(packet);
            PacketReceived.TrySetResult(packet);
            return ValueTask.CompletedTask;
        }

        public ValueTask AppendFilterChangeAsync(
            EegFilterChangeRecord change,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public ValueTask AppendSamplesAsync(
            EegRecordedSampleBatch batch,
            CancellationToken cancellationToken = default
        )
        {
            if (Volatile.Read(ref _active) == 0)
            {
                Interlocked.Increment(ref _appendAfterCompletionAttempts);
                throw new InvalidOperationException(
                    $"EEG recording {batch.RecordingId} is not active."
                );
            }
            Interlocked.Increment(ref _sampleBatchCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask<EegRecordingSummary> CompleteAsync(
            Guid recordingId,
            EegRecordingCompletionStatus status,
            CancellationToken cancellationToken = default
        )
        {
            Volatile.Write(ref _active, 0);
            Interlocked.Increment(ref _completionCount);
            var metadata =
                _metadata ?? throw new InvalidOperationException("Recording was not started.");
            return ValueTask.FromResult(
                new EegRecordingSummary(
                    recordingId,
                    string.Empty,
                    true,
                    status,
                    PacketCount,
                    0,
                    metadata.StartedAtUtc,
                    DateTimeOffset.UtcNow,
                    metadata,
                    SampleBatchCount: SampleBatchCount
                )
            );
        }
    }

    private static async Task<(
        FakeTransport Transport,
        EggtCsFrameCodec Codec,
        DeviceManager Manager,
        DeviceImpedanceDetectionService Service,
        DeviceHeartbeatPauseService HeartbeatPause,
        string DeviceId
    )> CreatePhysicalEegServiceAsync(string deviceId)
    {
        var transport = new FakeTransport();
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults()),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            new DeviceSessionOptions()
        );
        await session.StartAsync();
        var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(new DeviceId(deviceId), "ESP32_EEG", deviceId, "B8:F8:62:69:82:AC"),
            session
        );
        await VerifyPhysicalConnectionAsync(device, transport, codec);
        var manager = new DeviceManager(connectedDevices: [device]);
        var heartbeatPause = new DeviceHeartbeatPauseService();
        var service = new DeviceImpedanceDetectionService(
            manager,
            new InMemoryChannelMappingService([new EegPhysicalChannelMapping("F3", 3)]),
            heartbeatPause
        );
        return (transport, codec, manager, service, heartbeatPause, deviceId);
    }

    private sealed class InMemoryChannelMappingService(
        IReadOnlyList<EegPhysicalChannelMapping> values,
        IReadOnlyList<string>? validationErrors = null
    ) : IEegPhysicalChannelMappingService
    {
        private IReadOnlyList<EegPhysicalChannelMapping> _values = values;

        public EegPhysicalChannelMappingSnapshot Load() =>
            new(
                EegPhysicalChannelMappingService.SupportedPhysicalChannelCount,
                _values,
                validationErrors ?? []
            );

        public void Save(
            int physicalChannelCount,
            IReadOnlyList<EegPhysicalChannelMapping> mappings
        ) => _values = mappings;

        public string StoragePath => "memory";
    }

    private sealed class NullRouter : INavigationRouter
    {
        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData routeData) { }

        public void Navigate(StimulusConfigurationRouteData routeData) { }

        public void Navigate(ElectrodeConfigurationRouteData routeData) { }

        public void Navigate(ExperimentRunRouteData routeData) { }

        public void Navigate(ExperimentRerunRouteData routeData) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
