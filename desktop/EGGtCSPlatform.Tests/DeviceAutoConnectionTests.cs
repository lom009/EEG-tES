using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceAutoConnectionTests
{
    [Fact]
    public async Task SimulationSkipsSavedPhysicalEndpointAndPreservesProfileAndNetwork()
    {
        var options = new DeviceBackendOptions
        {
            ConnectionSource = DeviceConnectionSource.Simulated,
        };
        await using var runtime = new EGGtCSPlatform.DeviceRuntime.DeviceRuntimeBuilder()
            .UseSimulation()
            .AddProtocol(new EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsProtocolModule())
            .AddTransport(new ForbiddenTransportFactory())
            .Build();
        var network = new DeviceConnectionNetworkContext(devicePort: 31111, localPort: 31112);
        var snapshot = network.Snapshot;
        var profile = CreateProfile();
        var store = new MemoryProfileStore(profile);
        var selection = new DeviceSelectionContext();
        var logger = new RecordingApplicationLogger();
        await using var coordinator = new DeviceConnectionCoordinator(
            CreateOptions(),
            runtime.Devices,
            selection,
            network,
            new RuntimeDiscovery(runtime),
            store,
            backendOptions: options,
            runtime: runtime,
            logger: logger
        );
        await coordinator.StartAsync();
        await WaitUntilAsync(() => selection.IsConnected);
        Assert.Equal(DeviceId.Simulator.Value, selection.SelectedDeviceId);
        Assert.Equal(snapshot, network.Snapshot);
        Assert.Equal(profile, store.Load());
        Assert.False(store.Saved.Task.IsCompleted);
        Assert.False(await coordinator.ConnectManualAsync(CreateCandidate()));
        Assert.True(selection.IsConnected);
        await WaitUntilAsync(() => logger.Entries.Any(x => x.EventName == "Device.Connected"));
        Assert.Single(logger.Entries.Where(x => x.EventName == "Device.Connected"));
        Assert.Contains(
            logger.Entries,
            x => x.EventName == "Device.ConnectAttempt" && x.Level == ApplicationLogLevel.Debug
        );
    }

    [Fact]
    public async Task RealModeSkipsSavedSimulatorAndKeepsPhysicalDiscoveryOrder()
    {
        var connector = new CapturingConnector();
        await using var manager = new DeviceManager(connectors: [connector]);
        var discovery = new RecordingDiscovery();
        var profile = CreateProfile() with { Scheme = "simulator", ProtocolVersion = "simulator" };
        await using var coordinator = new DeviceConnectionCoordinator(
            CreateOptions(),
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            discovery,
            new MemoryProfileStore(profile),
            backendOptions: new DeviceBackendOptions()
        );
        await coordinator.StartAsync();
        await WaitUntilAsync(() =>
            coordinator.Snapshot.Stage == DeviceConnectionAutomationStage.WaitingToRetry
        );
        Assert.False(connector.Candidate.Task.IsCompleted);
        Assert.Equal(CreateOptions().GetEnabledModes().Select(mode => mode.Kind), discovery.Modes);
    }

    private sealed class ForbiddenTransportFactory : ITransportFactory
    {
        public ITransport Create(TransportEndpoint endpoint) =>
            throw new InvalidOperationException("Simulation accessed a physical transport");
    }

    private sealed class RuntimeDiscovery(EGGtCSPlatform.DeviceRuntime.IDeviceRuntime runtime)
        : IConfiguredDeviceDiscovery
    {
        public IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            DeviceDiscoveryModeDefinition mode,
            CancellationToken cancellationToken = default
        ) => runtime.Discovery.DiscoverAsync(mode.ResponseTimeout, cancellationToken);
    }

    [Fact]
    public void BatteryStateNotifiesOnlyWhenClampedValueChangesAndClearsOnDisconnect()
    {
        var context = new DeviceSelectionContext();
        var changed = new List<string?>();
        context.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        context.UpdateBattery(80);
        context.UpdateBattery(80);

        Assert.Equal(80, context.BatteryPercent);
        Assert.Equal("80%", context.BatteryPercentText);
        Assert.Equal(1, changed.Count(name => name == nameof(context.BatteryPercent)));
        Assert.Equal(1, changed.Count(name => name == nameof(context.BatteryPercentText)));

        context.UpdateBattery(150);
        context.SetConnectedDevice("device", "Device", batteryPercent: 100);
        changed.Clear();
        context.ClearConnection();

        Assert.Null(context.BatteryPercent);
        Assert.Equal("--%", context.BatteryPercentText);
        Assert.Equal(1, changed.Count(name => name == nameof(context.BatteryPercent)));
        Assert.Equal(1, changed.Count(name => name == nameof(context.BatteryPercentText)));
    }

    [Fact]
    public void EnabledModesAreSortedByConfiguredOrder()
    {
        var options = CreateOptions();
        options.GlobalBroadcast.Order = 30;
        options.LocalBroadcast.Order = 10;
        options.SubnetUnicast.Order = 20;

        Assert.Equal(
            [
                DeviceDiscoveryModeKind.LocalBroadcast,
                DeviceDiscoveryModeKind.SubnetUnicast,
                DeviceDiscoveryModeKind.GlobalBroadcast,
            ],
            options.GetEnabledModes().Select(mode => mode.Kind)
        );
        Assert.True(options.IsValid());
    }

    [Fact]
    public void DuplicateOrderIsRejectedOnlyForEnabledModes()
    {
        var options = CreateOptions();
        options.LocalBroadcast.Order = options.GlobalBroadcast.Order;
        Assert.False(options.IsValid());
        Assert.Contains(
            options.GetValidationErrors(),
            error => error.Contains("全广播") && error.Contains("本地广播")
        );

        options.LocalBroadcast.Enabled = false;
        options.LocalBroadcast.Address = "not-an-address";
        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidDefaultDevicePortIsRejected(int port)
    {
        var options = CreateOptions();
        options.DefaultDevicePort = port;

        Assert.False(options.IsValid());
        Assert.Contains(
            options.GetValidationErrors(),
            error => error.Contains("DefaultDevicePort", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void LocalNetworkSnapshotCalculatesDirectedBroadcastAndBoundsUnicastHosts()
    {
        var snapshot = LocalNetworkDiscoveryResolver.CreateSnapshot(
            IPAddress.Parse("192.168.7.42"),
            24,
            254
        );

        Assert.Equal("192.168.7.255", snapshot.DirectedBroadcastAddress.ToString());
        Assert.Equal(253, snapshot.SubnetHosts.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.7.42"), snapshot.SubnetHosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.7.255"), snapshot.SubnetHosts);
    }

    [Fact]
    public async Task ProfileSavePreservesOtherAppSettingsSections()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "eggtcs-auto-connection-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "appsettings.json");
            await File.WriteAllTextAsync(
                path,
                "{\"Existing\":{\"Value\":42},\"DeviceAutoConnection\":{\"Enabled\":true}}"
            );
            var store = new DeviceConnectionProfileStore(path);
            var profile = CreateProfile();

            await store.SaveAsync(profile);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.Equal(42, root["Existing"]!["Value"]!.GetValue<int>());
            Assert.True(root["DeviceAutoConnection"]!["Enabled"]!.GetValue<bool>());
            Assert.Equal(profile, store.Load());
            Assert.Null(root["DeviceAutoConnection"]!["LastDevice"]!["ProtocolId"]);
            Assert.Null(root["DeviceAutoConnection"]!["LastDevice"]!["RevisionSource"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ManualSearchUsesConfiguredModeOrder()
    {
        var options = CreateOptions();
        options.GlobalBroadcast.Order = 30;
        options.LocalBroadcast.Order = 20;
        options.SubnetUnicast.Order = 10;
        var discovery = new RecordingDiscovery();
        await using var manager = new DeviceManager();
        await using var coordinator = new DeviceConnectionCoordinator(
            options,
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            discovery,
            new MemoryProfileStore()
        );

        await coordinator.SearchManualAsync();

        Assert.Equal(
            [
                DeviceDiscoveryModeKind.SubnetUnicast,
                DeviceDiscoveryModeKind.LocalBroadcast,
                DeviceDiscoveryModeKind.GlobalBroadcast,
            ],
            discovery.Modes
        );
    }

    [Fact]
    public async Task SimulatedConnectionUsesSingleSimulatorDiscoveryPhase()
    {
        var discovery = new RecordingDiscovery();
        await using var manager = new DeviceManager();
        await using var coordinator = new DeviceConnectionCoordinator(
            CreateOptions(),
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            discovery,
            new MemoryProfileStore(),
            faultNotifier: null,
            backendOptions: new DeviceBackendOptions
            {
                ConnectionSource = DeviceConnectionSource.Simulated,
            }
        );

        await coordinator.SearchManualAsync();

        Assert.Equal([DeviceDiscoveryModeKind.Simulator], discovery.Modes);
    }

    [Fact]
    public async Task OpeningManualModeDuringAutomaticSearchLeavesCandidatesForUser()
    {
        var options = CreateOptions();
        options.LocalBroadcast.Enabled = false;
        options.SubnetUnicast.Enabled = false;
        var candidate = CreateCandidate();
        var discovery = new BlockingDiscovery(candidate);
        await using var manager = new DeviceManager();
        await using var coordinator = new DeviceConnectionCoordinator(
            options,
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            discovery,
            new MemoryProfileStore()
        );
        await coordinator.StartAsync();
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.EnterManualMode();
        discovery.Release.TrySetResult();
        await WaitUntilAsync(() =>
            coordinator.Snapshot.Stage == DeviceConnectionAutomationStage.ManualReview
            && !coordinator.Snapshot.IsBusy
        );

        Assert.Single(coordinator.Snapshot.Candidates);
        Assert.Equal(
            candidate.Identity.DeviceId,
            coordinator.Snapshot.Candidates[0].Identity.DeviceId
        );
    }

    [Fact]
    public async Task OpeningManualModeCancelsAutomaticConnectionAndClearsBusyState()
    {
        var options = CreateOptions();
        var connector = new BlockingConnector();
        await using var manager = new DeviceManager(connectors: [connector]);
        await using var coordinator = new DeviceConnectionCoordinator(
            options,
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            new RecordingDiscovery(),
            new MemoryProfileStore(CreateProfile())
        );
        await coordinator.StartAsync();
        await connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.EnterManualMode();
        await WaitUntilAsync(() =>
            coordinator.Snapshot.Stage == DeviceConnectionAutomationStage.ManualReview
            && !coordinator.Snapshot.IsBusy
        );

        Assert.Single(coordinator.Snapshot.Candidates);
    }

    [Fact]
    public async Task AutomaticConnectionRetriesAfterEveryCompletedDiscoveryRound()
    {
        var options = CreateOptions();
        options.RetryInterval = TimeSpan.FromMilliseconds(10);
        options.LocalBroadcast.Enabled = false;
        options.SubnetUnicast.Enabled = false;
        var discovery = new CountingDiscovery();
        await using var manager = new DeviceManager();
        await using var coordinator = new DeviceConnectionCoordinator(
            options,
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            discovery,
            new MemoryProfileStore()
        );

        await coordinator.StartAsync();
        await WaitUntilAsync(() => discovery.Calls >= 2);

        Assert.True(discovery.Calls >= 2);
    }

    [Fact]
    public async Task DisabledAutomaticConnectionDoesNotStartDiscovery()
    {
        var options = CreateOptions();
        options.Enabled = false;
        var discovery = new CountingDiscovery();
        await using var manager = new DeviceManager();
        await using var coordinator = new DeviceConnectionCoordinator(
            options,
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            discovery,
            new MemoryProfileStore()
        );

        await coordinator.StartAsync();
        await Task.Delay(50);

        Assert.Equal(0, discovery.Calls);
    }

    [Fact]
    public async Task FaultNotificationRemovesFaultedDeviceEvenWhenAutomaticConnectionIsDisabled()
    {
        var options = CreateOptions();
        options.Enabled = false;
        var device = new EGGtCSPlatform.DeviceSdk.Simulation.SimulatedEggtCsDevice(
            new DeviceIdentity(new DeviceId("faulted-device"), "Faulted device", null, null)
        );
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var selection = new DeviceSelectionContext();
        selection.SetConnectedDevice(device.Identity.DeviceId.Value, device.Identity.Model);
        var notifier = new DeviceConnectionFaultNotifier();
        await using var coordinator = new DeviceConnectionCoordinator(
            options,
            manager,
            selection,
            new DeviceConnectionNetworkContext(),
            new RecordingDiscovery(),
            new MemoryProfileStore(),
            notifier
        );
        await coordinator.StartAsync();

        notifier.Notify(
            device.Identity,
            new HeartbeatTimeoutException(Guid.NewGuid(), TimeSpan.FromSeconds(8))
        );

        await WaitUntilAsync(() => manager.Devices.Count == 0);
        Assert.Empty(manager.Devices);
    }

    [Fact]
    public async Task SavedEndpointStatusFailureNeverRestoresConnectedState()
    {
        var options = CreateOptions();
        options.RetryInterval = TimeSpan.FromMilliseconds(20);
        var connector = new FailedStatusConnector();
        await using var manager = new DeviceManager(connectors: [connector]);
        var selection = new DeviceSelectionContext();
        await using var coordinator = new DeviceConnectionCoordinator(
            options,
            manager,
            selection,
            new DeviceConnectionNetworkContext(),
            new RecordingDiscovery(),
            new MemoryProfileStore(CreateProfile())
        );

        await coordinator.StartAsync();
        await WaitUntilAsync(() => connector.ConnectCalls > 0 && manager.Devices.Count == 0);

        Assert.False(selection.IsConnected);
        Assert.Empty(selection.SelectedDeviceId);
        Assert.Empty(manager.Devices);
    }

    [Theory]
    [InlineData("udp", "1.0.1", "eggtcs.gen1", ProtocolRevisionSource.CompatibilityAssumption)]
    [InlineData("simulator", "simulator", "simulator", ProtocolRevisionSource.Unspecified)]
    public async Task SavedProfileAddsOnlyInMemoryProtocolIdentity(
        string scheme,
        string revision,
        string protocolId,
        ProtocolRevisionSource source
    )
    {
        var connector = new CapturingConnector();
        await using var manager = new DeviceManager(connectors: [connector]);
        var profile = CreateProfile() with { Scheme = scheme, ProtocolVersion = revision };
        await using var coordinator = new DeviceConnectionCoordinator(
            CreateOptions(),
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            new RecordingDiscovery(),
            new MemoryProfileStore(profile)
        );
        await coordinator.StartAsync();
        var candidate = await connector.Candidate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(protocolId, candidate.ProtocolId);
        Assert.Equal(source, candidate.RevisionSource);
        Assert.Equal(revision, candidate.ProtocolVersion);
        Assert.Equal(profile.DeviceId, candidate.Identity.DeviceId.Value);
    }

    private sealed class CapturingConnector : IDeviceConnector
    {
        public TaskCompletionSource<DeviceCandidate> Candidate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanConnect(DeviceCandidate candidate) => true;

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        )
        {
            Candidate.TrySetResult(candidate);
            return Task.FromResult<IEggtCsDevice>(
                new EGGtCSPlatform.DeviceSdk.Simulation.SimulatedEggtCsDevice(candidate.Identity)
            );
        }
    }

    [Fact]
    public async Task RestoredProfileKeepsOldInputButSavesRuntimeSelectedRevision()
    {
        var module = new SavedRevisionModule();
        await using var runtime = new EGGtCSPlatform.DeviceRuntime.DeviceRuntimeBuilder()
            .AddProtocol(module)
            .WithOptions(new() { HeartbeatEnabled = false })
            .Build();
        var old = CreateProfile();
        var store = new MemoryProfileStore(old);
        var selection = new DeviceSelectionContext();
        await using var coordinator = new DeviceConnectionCoordinator(
            CreateOptions(),
            runtime.Devices,
            selection,
            new DeviceConnectionNetworkContext(),
            new RecordingDiscovery(),
            store,
            runtime: runtime
        );
        await coordinator.StartAsync();
        var saved = await store.Saved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("1.0.1", old.ProtocolVersion);
        Assert.Equal("test.2", saved.ProtocolVersion);
        Assert.Equal(old with { ProtocolVersion = "test.2" }, saved);
        Assert.Equal(
            ProtocolRevisionSource.CompatibilityAssumption,
            module.Original!.RevisionSource
        );
        Assert.Equal("1.0.1", module.Original.ProtocolVersion);
        Assert.True(selection.IsConnected);
        Assert.Equal(
            "test.2",
            ((IDeviceProtocolBinding)Assert.Single(runtime.Devices.Devices))
                .ProtocolInfo
                .ActiveRevision
        );
    }

    // Test-only stand-in for a later software revision; never sends real protocol bytes.
    private sealed class SavedRevisionModule : IDeviceProtocolModule
    {
        public DeviceCandidate? Original;
        public string ProtocolId => "eggtcs.gen1";
        public IReadOnlySet<string> SupportedRevisions => new HashSet<string> { "test.2" };

        public ProtocolRevisionSelection ResolveRevision(DeviceCandidate candidate)
        {
            Original = candidate;
            return new("test.2", ProtocolRevisionSource.SoftwareDefault);
        }

        public bool CanConnect(DeviceCandidate candidate) => candidate.ProtocolVersion == "test.2";

        public IReadOnlySet<Type> GetSupportedCommands(string revision) =>
            new HashSet<Type> { typeof(ReadDeviceStatusRequest) };

        public DeviceCapabilities GetCapabilities(
            DeviceCandidate candidate,
            IReadOnlySet<DeviceCapabilityKind> realCapabilities
        ) => EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default;

        public Task<IEggtCsDevice> ConnectAsync(
            ProtocolConnectionContext context,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<IEggtCsDevice>(
                new EGGtCSPlatform.DeviceSdk.Simulation.SimulatedEggtCsDevice(
                    context.Candidate.Identity
                )
            );

        public IDeviceDiscovery CreateDiscovery(IDiscoveryTransport transport, int callbackPort) =>
            throw new NotSupportedException();
    }

    private static DeviceAutoConnectionOptions CreateOptions() => new();

    private static SavedDeviceConnectionProfile CreateProfile() =>
        new(
            "device-1",
            "Device",
            "SERIAL",
            "AA:BB:CC:DD:EE:FF",
            "udp",
            "192.168.1.10",
            30307,
            "1.0.1",
            "0.0.0.0",
            30302
        );

    private static DeviceCandidate CreateCandidate() =>
        new(
            new DeviceIdentity(new DeviceId("device-1"), "Device", "SERIAL", "AA:BB:CC:DD:EE:FF"),
            new TransportEndpoint("udp", "192.168.1.10", 30307),
            "1.0.1"
        );

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached before the deadline.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingDiscovery : IConfiguredDeviceDiscovery
    {
        public List<DeviceDiscoveryModeKind> Modes { get; } = [];

        public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            DeviceDiscoveryModeDefinition mode,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            Modes.Add(mode.Kind);
            await Task.Yield();
            yield break;
        }
    }

    private sealed class BlockingDiscovery(DeviceCandidate candidate) : IConfiguredDeviceDiscovery
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            DeviceDiscoveryModeDefinition mode,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return candidate;
        }
    }

    private sealed class BlockingConnector : IDeviceConnector
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanConnect(DeviceCandidate candidate) => true;

        public async Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        )
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class CountingDiscovery : IConfiguredDeviceDiscovery
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            DeviceDiscoveryModeDefinition mode,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _calls);
            await Task.Yield();
            yield break;
        }
    }

    private sealed class FailedStatusConnector : IDeviceConnector
    {
        private int _connectCalls;
        public int ConnectCalls => Volatile.Read(ref _connectCalls);

        public bool CanConnect(DeviceCandidate candidate) => true;

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _connectCalls);
            return Task.FromResult<IEggtCsDevice>(new FailedStatusDevice(candidate.Identity));
        }
    }

    private sealed class FailedStatusDevice(DeviceIdentity identity) : IEggtCsDevice
    {
        public DeviceIdentity Identity { get; } = identity;
        public DeviceCapabilities Capabilities =>
            EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default;
        public DeviceStateSnapshot State { get; private set; } =
            new(
                DeviceConnectionState.Connecting,
                DeviceOperationState.Unknown,
                null,
                DateTimeOffset.UtcNow
            );
        public IEegAcquisitionCapability? EegAcquisition => null;
        public IStimulationCapability? Stimulation => null;
        public IImpedanceCapability? Impedance => null;
        public IToleranceCapability? Tolerance => null;

        public Task<DeviceStatusResponse> ReadStatusAsync(
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new DeviceStatusResponse(
                    DeviceCommandStatus.Failed,
                    DeviceOperationState.Unknown,
                    0
                )
            );

        public async IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = State with { Connection = DeviceConnectionState.Disconnected };
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryProfileStore(SavedDeviceConnectionProfile? profile = null)
        : IDeviceConnectionProfileStore
    {
        public TaskCompletionSource<SavedDeviceConnectionProfile> Saved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SavedDeviceConnectionProfile? Load() => profile;

        public Task SaveAsync(
            SavedDeviceConnectionProfile profile,
            CancellationToken cancellationToken = default
        )
        {
            Saved.TrySetResult(profile);
            return Task.CompletedTask;
        }
    }
}
