using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceConnectionDialogViewModelTests
{
    [Fact]
    public async Task SimulatorConnectionUsesDiscoveryConnectorAndStatusQuery()
    {
        await using var manager = CreateSimulatorManager();
        var context = new DeviceSelectionContext();
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        await model.SearchCommand.ExecuteAsync(null);
        Assert.Equal("EGG/tCS Simulator", model.SelectedCandidate!.DisplayName);
        Assert.Empty(manager.Devices);

        await model.ConnectCommand.ExecuteAsync(null);

        Assert.False(model.IsDialogOpen);
        Assert.True(context.IsConnected);
        Assert.Equal(DeviceId.Simulator.Value, context.SelectedDeviceId);
        Assert.True(manager.TryGet(DeviceId.Simulator, out var device));
        Assert.Equal(DeviceConnectionState.Connected, device!.State.Connection);
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
        Assert.Equal(100, context.BatteryPercent);
        Assert.Equal("100%", context.BatteryPercentText);
    }

    [Fact]
    public async Task ConnectIsDisabledUntilADeviceHasBeenDiscovered()
    {
        await using var manager = CreateSimulatorManager();
        var context = new DeviceSelectionContext();
        var model = new DeviceConnectionDialogViewModel(manager, context, () => 42001);
        model.Show();

        await model.ConnectCommand.ExecuteAsync(null);

        Assert.True(model.IsDialogOpen);
        Assert.False(model.CanConnect);
        Assert.False(context.IsConnected);
        Assert.Empty(manager.Devices);
        Assert.Equal("42001", model.LocalPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public async Task InvalidEditableDevicePortKeepsDialogOpen(string devicePort)
    {
        await using var manager = CreateSimulatorManager();
        var context = new DeviceSelectionContext();
        var model = new DeviceConnectionDialogViewModel(manager, context, () => 42001);
        model.Show();
        await model.SearchCommand.ExecuteAsync(null);
        model.DevicePort = devicePort;

        await model.ConnectCommand.ExecuteAsync(null);

        Assert.True(model.IsDialogOpen);
        Assert.False(context.IsConnected);
        Assert.Empty(manager.Devices);
        Assert.Contains("设备端口", model.StatusText);
    }

    [Fact]
    public async Task LocalPortIsAutomaticallyAssignedFromAnAvailableUdpPort()
    {
        await using var manager = CreateSimulatorManager();
        var model = new DeviceConnectionDialogViewModel(manager, new DeviceSelectionContext());
        var port = int.Parse(model.LocalPort, System.Globalization.CultureInfo.InvariantCulture);

        Assert.InRange(port, 1, ushort.MaxValue);
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        Assert.Equal(port, ((IPEndPoint)socket.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task MissingCandidateKeepsDialogOpenAndReportsFailure()
    {
        await using var manager = new DeviceManager();
        var context = new DeviceSelectionContext();
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        await model.SearchCommand.ExecuteAsync(null);

        Assert.True(model.IsDialogOpen);
        Assert.Null(model.SelectedCandidate);
        Assert.False(model.CanConnect);
        Assert.False(context.IsConnected);
        Assert.Empty(manager.Devices);
        Assert.Contains("未发现", model.StatusText);
    }

    [Fact]
    public async Task DiscoveryFailureIsReportedWithoutChangingExistingConnection()
    {
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(
            discoveries: [new ThrowingDiscovery()],
            connectedDevices: [device]
        );
        var context = new DeviceSelectionContext();
        context.SetConnectedDevice(device.Identity.DeviceId.Value, device.Identity.Model);
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        await model.SearchCommand.ExecuteAsync(null);

        Assert.True(model.IsDialogOpen);
        Assert.True(context.IsConnected);
        Assert.Same(device, Assert.Single(manager.Devices));
        Assert.Contains("搜索失败", model.StatusText);
    }

    [Fact]
    public async Task ConnectionFailureLeavesSessionDisconnectedAndDialogOpen()
    {
        var candidate = CreateCandidate("connect-failure", "throw-connect");
        await using var manager = new DeviceManager(
            discoveries: [new SingleCandidateDiscovery(candidate)],
            connectors: [new ThrowingConnector()]
        );
        var context = new DeviceSelectionContext();
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        await model.SearchCommand.ExecuteAsync(null);
        await model.ConnectCommand.ExecuteAsync(null);

        Assert.True(model.IsDialogOpen);
        Assert.False(context.IsConnected);
        Assert.Empty(manager.Devices);
        Assert.Contains("连接失败", model.StatusText);
    }

    [Fact]
    public async Task RepeatedConnectSubmissionOnlyStartsOneConnection()
    {
        var candidate = CreateCandidate(DeviceId.Simulator.Value, "blocking");
        var connector = new BlockingConnector();
        await using var manager = new DeviceManager(
            discoveries: [new SingleCandidateDiscovery(candidate)],
            connectors: [connector]
        );
        var context = new DeviceSelectionContext();
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        await model.SearchCommand.ExecuteAsync(null);
        var first = model.ConnectCommand.ExecuteAsync(null);
        await connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var repeated = model.ConnectCommand.ExecuteAsync(null);

        Assert.False(model.CanInteract);
        Assert.Equal(1, connector.ConnectCalls);
        connector.Release.TrySetResult();
        await Task.WhenAll(first, repeated);
        Assert.Equal(1, connector.ConnectCalls);
        Assert.True(context.IsConnected);
    }

    [Fact]
    public async Task FailedStatusQueryRemovesCandidateAndKeepsSessionDisconnected()
    {
        var candidate = new DeviceCandidate(
            new DeviceIdentity(new DeviceId("status-failure"), "Status failure", "FAIL-1", null),
            new TransportEndpoint("test", "192.168.1.100", 30307),
            "test"
        );
        await using var manager = new DeviceManager(
            discoveries: [new SingleCandidateDiscovery(candidate)],
            connectors: [new StatusFailureConnector()]
        );
        var context = new DeviceSelectionContext();
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        await model.SearchCommand.ExecuteAsync(null);
        await model.ConnectCommand.ExecuteAsync(null);

        Assert.True(model.IsDialogOpen);
        Assert.False(context.IsConnected);
        Assert.Empty(manager.Devices);
        Assert.Contains("状态验证失败", model.StatusText);
    }

    [Fact]
    public async Task ReconfigurationRemovesOldSessionBeforeConnectingReplacement()
    {
        var oldDevice = new SimulatedEggtCsDevice();
        var oldSession = oldDevice.SessionId;
        await using var manager = new DeviceManager(
            discoveries: [new SimulatedDeviceDiscovery()],
            connectors: [new SimulatedDeviceConnector()],
            connectedDevices: [oldDevice]
        );
        var context = new DeviceSelectionContext();
        context.SetConnectedDevice(oldDevice.Identity.DeviceId.Value, oldDevice.Identity.Model);
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        await model.SearchCommand.ExecuteAsync(null);
        await model.ConnectCommand.ExecuteAsync(null);

        var replacement = Assert.IsType<SimulatedEggtCsDevice>(Assert.Single(manager.Devices));
        Assert.NotEqual(oldSession, replacement.SessionId);
        Assert.True(context.IsConnected);
        Assert.False(model.IsDialogOpen);
    }

    [Fact]
    public async Task FailedReconfigurationRemovesOldDeviceAndClearsSession()
    {
        var oldDevice = new SimulatedEggtCsDevice();
        var candidate = CreateCandidate("replacement-failure", "throw-connect");
        await using var manager = new DeviceManager(
            discoveries: [new SingleCandidateDiscovery(candidate)],
            connectors: [new ThrowingConnector()],
            connectedDevices: [oldDevice]
        );
        var context = new DeviceSelectionContext();
        context.SetConnectedDevice(oldDevice.Identity.DeviceId.Value, oldDevice.Identity.Model);
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        await model.SearchCommand.ExecuteAsync(null);
        await model.ConnectCommand.ExecuteAsync(null);

        Assert.True(model.IsDialogOpen);
        Assert.False(context.IsConnected);
        Assert.Empty(manager.Devices);
        Assert.Contains("连接失败", model.StatusText);
    }

    [Fact]
    public async Task SearchPopulatesDeviceSelectorAndSelectionUpdatesEndpointDisplay()
    {
        var first = new DeviceCandidate(
            new DeviceIdentity(new DeviceId("first"), "First device", "FIRST-1", null),
            new TransportEndpoint("test", "192.168.10.21", 31001),
            "test"
        );
        var second = new DeviceCandidate(
            new DeviceIdentity(new DeviceId("second"), "Second device", "SECOND-2", null),
            new TransportEndpoint("test", "192.168.10.22", 31002),
            "test"
        );
        var connector = new CandidateIdentityConnector();
        await using var manager = new DeviceManager(
            discoveries:
            [
                new SingleCandidateDiscovery(first),
                new SingleCandidateDiscovery(second),
            ],
            connectors: [connector]
        );
        var context = new DeviceSelectionContext();
        var network = new DeviceConnectionNetworkContext();
        var model = new DeviceConnectionDialogViewModel(manager, context, network, () => 42002);

        await model.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, model.DiscoveredDevices.Count);
        Assert.Equal("first", model.SelectedCandidate!.DeviceId);
        Assert.Equal("192.168.10.21", model.DeviceIp);
        Assert.Equal("31001", model.DevicePort);
        model.SelectedCandidate = model.DiscoveredDevices[1];
        Assert.Equal("192.168.10.22", model.DeviceIp);
        Assert.Equal("31002", model.DevicePort);
        Assert.Equal("42002", model.LocalPort);
        model.DevicePort = "32002";
        await model.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("second", context.SelectedDeviceId);
        Assert.Equal("192.168.10.22", context.SelectedDeviceIp);
        Assert.Equal(32002, connector.LastCandidate!.Endpoint.Port);
        Assert.Equal(32002, network.Snapshot.DevicePort);
        Assert.Equal(42002, network.Snapshot.LocalPort);
    }

    [Fact]
    public async Task CancelPreservesExistingConnection()
    {
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var context = new DeviceSelectionContext();
        context.SetConnectedDevice(device.Identity.DeviceId.Value, device.Identity.Model);
        var model = new DeviceConnectionDialogViewModel(manager, context);
        model.Show();

        model.CancelCommand.Execute(null);

        Assert.False(model.IsDialogOpen);
        Assert.True(context.IsConnected);
        Assert.Same(device, Assert.Single(manager.Devices));
    }

    [Fact]
    public void NewApplicationSessionAlwaysStartsDisconnected()
    {
        var first = new DeviceSelectionContext();
        first.SetConnectedDevice(DeviceId.Simulator.Value, "EGG/tCS Simulator");

        var restarted = new DeviceSelectionContext();

        Assert.True(first.IsConnected);
        Assert.False(restarted.IsConnected);
        Assert.Empty(restarted.SelectedDeviceId);
        Assert.Equal(DeviceConnectionState.Disconnected, restarted.ConnectionState);
    }

    private static DeviceManager CreateSimulatorManager() =>
        new(
            discoveries: [new SimulatedDeviceDiscovery()],
            connectors: [new SimulatedDeviceConnector()]
        );

    private static DeviceCandidate CreateCandidate(string deviceId, string scheme) =>
        new(
            new DeviceIdentity(new DeviceId(deviceId), "Test device", "TEST-1", null),
            new TransportEndpoint(scheme, "192.168.1.100", 30307),
            scheme
        );

    private sealed class SingleCandidateDiscovery(DeviceCandidate candidate) : IDeviceDiscovery
    {
        public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            TimeSpan window,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return candidate;
        }
    }

    private sealed class ThrowingDiscovery : IDeviceDiscovery
    {
        public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            TimeSpan window,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (window < TimeSpan.Zero)
                yield break;
            throw new InvalidOperationException("Discovery failed");
        }
    }

    private sealed class ThrowingConnector : IDeviceConnector
    {
        public bool CanConnect(DeviceCandidate candidate) =>
            candidate.Endpoint.Scheme == "throw-connect";

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        ) => Task.FromException<IEggtCsDevice>(new InvalidOperationException("Connect failed"));
    }

    private sealed class CandidateIdentityConnector : IDeviceConnector
    {
        public DeviceCandidate? LastCandidate { get; private set; }

        public bool CanConnect(DeviceCandidate candidate) => candidate.Endpoint.Scheme == "test";

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        )
        {
            LastCandidate = candidate;
            return Task.FromResult<IEggtCsDevice>(new SimulatedEggtCsDevice(candidate.Identity));
        }
    }

    private sealed class BlockingConnector : IDeviceConnector
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectCalls { get; private set; }

        public bool CanConnect(DeviceCandidate candidate) =>
            candidate.Endpoint.Scheme == "blocking";

        public async Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        )
        {
            ConnectCalls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new SimulatedEggtCsDevice();
        }
    }

    private sealed class StatusFailureConnector : IDeviceConnector
    {
        public bool CanConnect(DeviceCandidate candidate) => candidate.Endpoint.Scheme == "test";

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IEggtCsDevice>(new StatusFailureDevice(candidate.Identity));
    }

    private sealed class StatusFailureDevice(DeviceIdentity identity) : IEggtCsDevice
    {
        public DeviceIdentity Identity { get; } = identity;

        public DeviceCapabilities Capabilities =>
            EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default;

        public DeviceStateSnapshot State { get; private set; } =
            new(
                DeviceConnectionState.Connected,
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
                    DeviceOperationState.Faulted,
                    0
                )
            );

        public async IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
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
}
