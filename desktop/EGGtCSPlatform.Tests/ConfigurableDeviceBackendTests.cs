using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ConfigurableDeviceBackendTests
{
    [Fact]
    public async Task CancelingMergedEventStreamCompletesEnumerationWithoutThrowing()
    {
        var identity = new DeviceIdentity(
            new DeviceId("cancel-events"),
            "Simulator",
            "SIM-CANCEL",
            null
        );
        await using var device = new ConfigurableEggtCsDevice(
            identity,
            CreateSimulatedOptions(),
            simulation: new SimulatedEggtCsDevice(identity)
        );
        using var cancellation = new CancellationTokenSource();
        await using var events = device.ReadEventsAsync(cancellation.Token).GetAsyncEnumerator();

        var pendingRead = events.MoveNextAsync().AsTask();
        cancellation.Cancel();

        Assert.False(await pendingRead);
    }

    [Fact]
    public async Task SimulatedConnectionDiscoversWithoutUsingNetworkSettings()
    {
        var options = CreateSimulatedOptions();
        var backend = new ConfigurableDeviceBackend(
            options,
            new DeviceConnectionNetworkContext("not-an-address", "also-invalid")
        );

        var candidates = new List<DeviceCandidate>();
        await foreach (var candidate in backend.DiscoverAsync(TimeSpan.FromMilliseconds(1)))
            candidates.Add(candidate);

        var discovered = Assert.Single(candidates);
        Assert.Equal("simulator", discovered.Endpoint.Scheme);
        Assert.False(
            backend.CanConnect(
                discovered with
                {
                    Endpoint = new TransportEndpoint("udp", "127.0.0.1", 30307),
                    ProtocolVersion = EggtCsRevisions.V101,
                }
            )
        );
    }

    [Fact]
    public async Task CapabilityConfigurationRoutesEachImpedanceEventToItsSelectedSource()
    {
        var identity = new DeviceIdentity(new DeviceId("mixed-source"), "Mixed", "MIX-1", null);
        var physical = new SimulatedEggtCsDevice(identity);
        var simulation = new SimulatedEggtCsDevice(identity);
        var options = new DeviceBackendOptions
        {
            ConnectionSource = DeviceConnectionSource.Real,
            Capabilities = new DeviceCapabilitySourceOptions
            {
                Status = DeviceCapabilitySource.Real,
                EegAcquisition = DeviceCapabilitySource.Disabled,
                Stimulation = DeviceCapabilitySource.Disabled,
                EegImpedance = DeviceCapabilitySource.Real,
                StimulationImpedance = DeviceCapabilitySource.Simulated,
                Tolerance = DeviceCapabilitySource.Disabled,
            },
        };
        await using var device = new ConfigurableEggtCsDevice(
            identity,
            options,
            physical,
            simulation
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = device.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();

        var eegEvent = events.MoveNextAsync().AsTask();
        await device.Impedance!.StartEegAsync(new HashSet<int> { 1 }, timeout.Token);
        Assert.True(await eegEvent);
        Assert.Equal(physical.SessionId, events.Current.SessionId);
        Assert.IsType<EegImpedanceReceivedEvent>(events.Current.Event);

        var stimulationEvent = events.MoveNextAsync().AsTask();
        await device.Impedance.StartStimulationAsync(
            new StimulationConfiguration(
                false,
                [
                    new StimulationChannel(7, 1m, DeviceStimulationChannelRole.FixedActive),
                    new StimulationChannel(1, 1m),
                ],
                StimulationWaveform.TDcs,
                StimulationDirection.Positive,
                0.1m,
                50,
                TimeSpan.Zero
            ),
            timeout.Token
        );
        Assert.True(await stimulationEvent);
        Assert.Equal(simulation.SessionId, events.Current.SessionId);
        Assert.IsType<StimulationImpedanceReceivedEvent>(events.Current.Event);
    }

    [Fact]
    public async Task DisabledCapabilitiesAreNotAdvertisedAndDoNotFallback()
    {
        var options = CreateSimulatedOptions();
        options.Capabilities.Stimulation = DeviceCapabilitySource.Disabled;
        options.Capabilities.StimulationImpedance = DeviceCapabilitySource.Disabled;
        options.Capabilities.Tolerance = DeviceCapabilitySource.Disabled;
        await using var device = new ConfigurableEggtCsDevice(
            new DeviceIdentity(DeviceId.Simulator, "Simulator", "SIM-1", null),
            options
        );

        Assert.False(device.Capabilities.CanStimulate);
        Assert.False(device.Capabilities.CanCheckStimulationImpedance);
        Assert.False(device.Capabilities.CanRunToleranceTest);
        Assert.Null(device.Stimulation);
        Assert.Null(device.Tolerance);
        Assert.NotNull(device.Impedance);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            device.Impedance!.StartStimulationAsync(
                new StimulationConfiguration(
                    false,
                    Array.Empty<StimulationChannel>(),
                    StimulationWaveform.TDcs,
                    StimulationDirection.Positive,
                    0.1m,
                    50,
                    TimeSpan.Zero
                )
            )
        );
    }

    [Fact]
    public async Task LoopbackHybridUsesRealSocketForEegAcquisitionAndImpedanceDetection()
    {
        using var physicalDevice = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var devicePort = ((IPEndPoint)physicalDevice.Client.LocalEndPoint!).Port;
        var localPort = FindAvailableUdpPort();
        var context = new DeviceConnectionNetworkContext(
            broadcastAddress: IPAddress.Loopback.ToString(),
            localAddress: IPAddress.Loopback.ToString(),
            devicePort,
            localPort
        );
        var backend = new ConfigurableDeviceBackend(new DeviceBackendOptions(), context);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var responder = RespondToDiscoveryAsync(physicalDevice, timeout.Token);

        var candidates = new List<DeviceCandidate>();
        await foreach (
            var discovered in backend.DiscoverAsync(TimeSpan.FromMilliseconds(250), timeout.Token)
        )
        {
            candidates.Add(discovered);
        }
        await responder;

        var candidate = Assert.Single(candidates);
        Assert.Equal(IPAddress.Loopback.ToString(), candidate.Endpoint.Address);
        Assert.Equal("B8:F8:62:69:82:A8", candidate.Identity.MacAddress);

        await using var device = await backend.ConnectAsync(candidate, timeout.Token);
        Assert.IsAssignableFrom<IEggtCsDevice>(device);
        Assert.Equal(
            DeviceCapabilitySource.Real,
            device.CapabilitySource(DeviceCapabilityKind.EegAcquisition)
        );
        Assert.Equal(
            DeviceCapabilitySource.Simulated,
            device.CapabilitySource(DeviceCapabilityKind.Tolerance)
        );
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        Assert.Equal(DeviceConnectionState.Connected, device.State.Connection);
        var statusTask = device.ReadStatusAsync(timeout.Token);
        var statusPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, statusPacket.Buffer[4]);
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    statusPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                    new byte[] { 0x01, 0x64 }
                )
            ),
            statusPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.Equal(DeviceCommandStatus.Success, (await statusTask).Status);
        Assert.Equal(DeviceConnectionState.Connected, device.State.Connection);
        var acquisitionStartTask = device.EegAcquisition!.StartAsync(
            TimeSpan.FromSeconds(1),
            new HashSet<int> { 1 },
            500,
            timeout.Token
        );
        var acquisitionStartPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal(
            (byte)EggtCsCommandCode.ControlAcquisitionRequest,
            acquisitionStartPacket.Buffer[4]
        );
        Assert.Equal(
            new byte[] { 0x01, 0x01, 0x00 },
            acquisitionStartPacket.Buffer.AsSpan(5, 3).ToArray()
        );
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    acquisitionStartPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            ),
            acquisitionStartPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await acquisitionStartTask).IsSuccess);
        var acquisitionStopTask = device.EegAcquisition.StopAsync(timeout.Token);
        var acquisitionStopPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal(
            (byte)EggtCsCommandCode.ControlAcquisitionRequest,
            acquisitionStopPacket.Buffer[4]
        );
        Assert.Equal(
            new byte[] { 0x02, 0x00, 0x00 },
            acquisitionStopPacket.Buffer.AsSpan(5, 3).ToArray()
        );
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    acquisitionStopPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                    new byte[] { 0x00 }
                )
            ),
            acquisitionStopPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await acquisitionStopTask).IsSuccess);

        var eegStartTask = device.Impedance!.StartEegAsync(new HashSet<int> { 1 }, timeout.Token);
        var eegStartPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal(
            (byte)EggtCsCommandCode.ConfigureEegImpedanceRequest,
            eegStartPacket.Buffer[4]
        );
        Assert.Equal((byte)0x01, eegStartPacket.Buffer[5]);
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    eegStartPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new byte[] { 0x00 }
                )
            ),
            eegStartPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await eegStartTask).IsSuccess);

        var eegStopTask = device.Impedance.StopEegAsync(new HashSet<int> { 1 }, timeout.Token);
        var eegStopPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal((byte)EggtCsCommandCode.ConfigureEegImpedanceRequest, eegStopPacket.Buffer[4]);
        Assert.Equal((byte)0x02, eegStopPacket.Buffer[5]);
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    eegStopPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                    new byte[] { 0x00 }
                )
            ),
            eegStopPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await eegStopTask).IsSuccess);
        var stimulationConfiguration = new StimulationConfiguration(
            false,
            [
                new StimulationChannel(7, 1m, DeviceStimulationChannelRole.FixedActive),
                new StimulationChannel(1, 1m),
            ],
            StimulationWaveform.TDcs,
            StimulationDirection.Positive,
            0.1m,
            50,
            TimeSpan.Zero
        );
        var configureStimulation = device.Stimulation!.ConfigureAsync(
            stimulationConfiguration,
            timeout.Token
        );
        var configureStimulationPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal(
            (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
            configureStimulationPacket.Buffer[4]
        );
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    configureStimulationPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            ),
            configureStimulationPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await configureStimulation).IsSuccess);

        var startStimulation = device.Stimulation.StartAsync(
            TimeSpan.FromSeconds(1),
            timeout.Token
        );
        var startStimulationPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal(
            (byte)EggtCsCommandCode.ControlStimulationRequest,
            startStimulationPacket.Buffer[4]
        );
        Assert.Equal(
            new byte[] { 0x01, 0x01, 0x00 },
            startStimulationPacket.Buffer.AsSpan(5, 3).ToArray()
        );
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    startStimulationPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x00 }
                )
            ),
            startStimulationPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await startStimulation).IsSuccess);

        var stopStimulation = device.Stimulation.StopAsync(timeout.Token);
        var stopStimulationPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal(
            (byte)EggtCsCommandCode.ControlStimulationRequest,
            stopStimulationPacket.Buffer[4]
        );
        Assert.Equal(
            new byte[] { 0x02, 0x00, 0x00 },
            stopStimulationPacket.Buffer.AsSpan(5, 3).ToArray()
        );
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    stopStimulationPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ControlStimulationResponse,
                    new byte[] { 0x00 }
                )
            ),
            stopStimulationPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await stopStimulation).IsSuccess);

        var impedanceConfiguration = new StimulationConfiguration(
            false,
            [
                new StimulationChannel(7, 0.04m, DeviceStimulationChannelRole.FixedActive),
                new StimulationChannel(1, 0.04m),
            ],
            StimulationWaveform.TDcs,
            StimulationDirection.Positive,
            0.1m,
            50,
            TimeSpan.Zero
        );
        var startTask = device.Impedance.StartStimulationAsync(
            impedanceConfiguration,
            timeout.Token
        );
        var startPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal(
            (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
            startPacket.Buffer[4]
        );
        Assert.Equal((byte)0x01, startPacket.Buffer[^3]);
        Assert.Equal(
            new byte[] { 0x01, 0x01, 0x01, 0x04 },
            startPacket.Buffer.AsSpan(5, 4).ToArray()
        );
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    startPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            ),
            startPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await startTask).IsSuccess);

        await using var events = device
            .ReadEventsAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var nextEvent = events.MoveNextAsync().AsTask();
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    0x01,
                    (byte)EggtCsCommandCode.StimulationImpedanceData,
                    new byte[] { 0x02, 0x07, 0x01, 0x01, 0x00 }
                )
            ),
            startPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True(await nextEvent);
        var impedanceEvent = Assert.IsType<StimulationImpedanceReceivedEvent>(events.Current.Event);
        Assert.Equal(2, impedanceEvent.Readings.Count);

        var stopTask = device.Impedance.StopStimulationAsync(impedanceConfiguration, timeout.Token);
        var stopPacket = await physicalDevice.ReceiveAsync(timeout.Token);
        Assert.Equal(
            (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
            stopPacket.Buffer[4]
        );
        Assert.Equal((byte)0x02, stopPacket.Buffer[^3]);
        await physicalDevice.SendAsync(
            codec.Encode(
                new WireMessage(
                    stopPacket.Buffer[2],
                    (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                    new byte[] { 0x00 }
                )
            ),
            stopPacket.RemoteEndPoint,
            timeout.Token
        );
        Assert.True((await stopTask).IsSuccess);

        using var noPacket = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await physicalDevice.ReceiveAsync(noPacket.Token)
        );
    }

    [Fact]
    public void NetworkContextKeepsSessionConfigurationOnlyInMemory()
    {
        var first = new DeviceConnectionNetworkContext();
        first.Configure(31007, 42001);

        var restarted = new DeviceConnectionNetworkContext();

        Assert.Equal(31007, first.Snapshot.DevicePort);
        Assert.Equal(42001, first.Snapshot.LocalPort);
        Assert.Equal(30307, restarted.Snapshot.DevicePort);
        Assert.Equal(30302, restarted.Snapshot.LocalPort);
    }

    [Fact]
    public async Task StatusQueryWithoutPhysicalResponseDoesNotMarkHybridDeviceConnected()
    {
        using var physicalDevice = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var devicePort = ((IPEndPoint)physicalDevice.Client.LocalEndPoint!).Port;
        var localPort = FindAvailableUdpPort();
        var context = new DeviceConnectionNetworkContext(
            IPAddress.Loopback.ToString(),
            IPAddress.Loopback.ToString(),
            devicePort,
            localPort
        );
        var options = new DeviceHeartbeatOptions
        {
            Enabled = false,
            Interval = TimeSpan.FromMilliseconds(30),
            ResponseTimeout = TimeSpan.FromMilliseconds(100),
            DisconnectTimeout = TimeSpan.FromMilliseconds(300),
        };
        var backend = new ConfigurableDeviceBackend(
            new DeviceBackendOptions(),
            context,
            new DeviceHeartbeatPauseService(),
            options,
            NullByteTrafficLogger.Instance
        );
        var candidate = new DeviceCandidate(
            new DeviceIdentity(new DeviceId("silent-device"), "Silent device", "SILENT-1", null),
            new TransportEndpoint("udp", IPAddress.Loopback.ToString(), devicePort),
            EggtCsRevisions.V101
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await using var device = await backend.ConnectAsync(candidate, timeout.Token);
        var statusTask = device.ReadStatusAsync(timeout.Token);
        var statusPacket = await physicalDevice.ReceiveAsync(timeout.Token);

        Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, statusPacket.Buffer[4]);
        await Assert.ThrowsAsync<RequestTimeoutException>(() => statusTask);
        Assert.Equal(DeviceConnectionState.Connected, device.State.Connection);
    }

    [Fact]
    public async Task RealHeartbeatAcceptsFieldResponsesAndPauseExcludesDisconnectWindow()
    {
        using var physicalDevice = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var devicePort = ((IPEndPoint)physicalDevice.Client.LocalEndPoint!).Port;
        var localPort = FindAvailableUdpPort();
        var context = new DeviceConnectionNetworkContext(
            IPAddress.Loopback.ToString(),
            IPAddress.Loopback.ToString(),
            devicePort,
            localPort
        );
        var pauseService = new DeviceHeartbeatPauseService();
        var options = new DeviceHeartbeatOptions
        {
            Enabled = true,
            Interval = TimeSpan.FromMilliseconds(30),
            ResponseTimeout = TimeSpan.FromMilliseconds(20),
            DisconnectTimeout = TimeSpan.FromMilliseconds(180),
        };
        var logger = new CapturingByteTrafficLogger();
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var heartbeatStatuses =
            new ConcurrentQueue<(IEggtCsDevice Device, DeviceStatusResponse Status)>();
        var backend = new ConfigurableDeviceBackend(
            new DeviceBackendOptions(),
            context,
            pauseService,
            options,
            logger,
            (_, exception) => faulted.TrySetResult(exception),
            heartbeatReceived: (sourceDevice, status) =>
            {
                heartbeatStatuses.Enqueue((sourceDevice, status));
                throw new InvalidOperationException(
                    "UI observer failure must not fault heartbeat."
                );
            }
        );
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var discoveryResponder = RespondToDiscoveryAsync(physicalDevice, testTimeout.Token);
        var candidate = Assert.Single(await DiscoverAsync(backend, testTimeout.Token));
        await discoveryResponder;

        await using var device = await backend.ConnectAsync(candidate, testTimeout.Token);
        var heartbeatPackets = new ConcurrentQueue<byte[]>();
        using var heartbeatCancellation = new CancellationTokenSource();
        var heartbeatResponder = RespondToHeartbeatsAsync(
            physicalDevice,
            heartbeatPackets,
            heartbeatCancellation.Token
        );

        await WaitUntilAsync(() => heartbeatPackets.Count >= 3, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => heartbeatStatuses.Count >= 2, TimeSpan.FromSeconds(2));
        Assert.All(
            heartbeatStatuses,
            item =>
            {
                Assert.Same(device, item.Device);
                Assert.InRange(item.Status.BatteryPercent, 0, 100);
            }
        );
        Assert.Equal(
            DeviceCommandStatus.Success,
            (await device.ReadStatusAsync(testTimeout.Token)).Status
        );
        using (pauseService.Pause(candidate.Identity.DeviceId))
        {
            await Task.Delay(options.Interval + options.ResponseTimeout + options.Interval);
            var pausedCount = heartbeatPackets.Count;
            await Task.Delay(options.DisconnectTimeout + options.Interval);
            Assert.Equal(pausedCount, heartbeatPackets.Count);
            Assert.Equal(DeviceConnectionState.Connected, device.State.Connection);
            Assert.False(faulted.Task.IsCompleted);
        }

        var countBeforeResume = heartbeatPackets.Count;
        await WaitUntilAsync(
            () => heartbeatPackets.Count > countBeforeResume,
            TimeSpan.FromSeconds(2)
        );
        Assert.All(
            heartbeatPackets,
            packet => Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, packet[4])
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Direction == ByteTrafficDirection.Transmit
                && entry.Data.Span[4] == (byte)EggtCsCommandCode.DiscoverDeviceRequest
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Direction == ByteTrafficDirection.Receive
                && entry.Data.Span[4] == (byte)EggtCsCommandCode.DiscoverDeviceResponse
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Direction == ByteTrafficDirection.Transmit
                && entry.Data.Span[4] == (byte)EggtCsCommandCode.ReadDeviceStatusRequest
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Direction == ByteTrafficDirection.Receive
                && entry.Data.Span[4] == (byte)EggtCsCommandCode.ReadDeviceStatusResponse
        );

        heartbeatCancellation.Cancel();
        await heartbeatResponder;
    }

    [Fact]
    public async Task ConsecutiveHeartbeatTimeoutFaultsDeviceAndReleasesLocalUdpPort()
    {
        using var physicalDevice = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var devicePort = ((IPEndPoint)physicalDevice.Client.LocalEndPoint!).Port;
        var localPort = FindAvailableUdpPort();
        var context = new DeviceConnectionNetworkContext(
            IPAddress.Loopback.ToString(),
            IPAddress.Loopback.ToString(),
            devicePort,
            localPort
        );
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var backend = new ConfigurableDeviceBackend(
            new DeviceBackendOptions(),
            context,
            new DeviceHeartbeatPauseService(),
            new DeviceHeartbeatOptions
            {
                Enabled = true,
                Interval = TimeSpan.FromMilliseconds(20),
                ResponseTimeout = TimeSpan.FromMilliseconds(15),
                DisconnectTimeout = TimeSpan.FromMilliseconds(100),
            },
            NullByteTrafficLogger.Instance,
            (_, exception) => faulted.TrySetResult(exception)
        );
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var discoveryResponder = RespondToDiscoveryAsync(physicalDevice, testTimeout.Token);
        var candidate = Assert.Single(await DiscoverAsync(backend, testTimeout.Token));
        await discoveryResponder;

        await using var device = await backend.ConnectAsync(candidate, testTimeout.Token);
        var exception = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsType<HeartbeatTimeoutException>(exception);
        Assert.Equal(DeviceConnectionState.Faulted, device.State.Connection);
        await WaitUntilAsync(() => CanBind(localPort), TimeSpan.FromSeconds(2));
    }

    private static async Task RespondToDiscoveryAsync(
        UdpClient device,
        CancellationToken cancellationToken
    )
    {
        var request = await device.ReceiveAsync(cancellationToken);
        Assert.Equal(
            new byte[] { 0xAA, 0xCC, 0x01, 0x09, 0x01 },
            request.Buffer.AsSpan(0, 5).ToArray()
        );
        var callbackPort = request.Buffer[5] | request.Buffer[6] << 8;
        Assert.Equal(callbackPort, request.RemoteEndPoint.Port);
        var response = EggtCsDeviceDiscoveryTests.CreateResponse(
            "ESP32_EEG",
            "EEG-LOOPBACK",
            [0xB8, 0xF8, 0x62, 0x69, 0x82, 0xA8]
        );
        await device.SendAsync(
            response,
            new IPEndPoint(request.RemoteEndPoint.Address, callbackPort),
            cancellationToken
        );
    }

    private static DeviceBackendOptions CreateSimulatedOptions() =>
        new()
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

    private static int FindAvailableUdpPort()
    {
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(
        ConfigurableDeviceBackend backend,
        CancellationToken cancellationToken
    )
    {
        var candidates = new List<DeviceCandidate>();
        await foreach (
            var candidate in backend.DiscoverAsync(
                TimeSpan.FromMilliseconds(500),
                cancellationToken
            )
        )
        {
            candidates.Add(candidate);
        }
        return candidates;
    }

    private static async Task RespondToHeartbeatsAsync(
        UdpClient physicalDevice,
        ConcurrentQueue<byte[]> packets,
        CancellationToken cancellationToken
    )
    {
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var responseNumber = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await physicalDevice.ReceiveAsync(cancellationToken);
                packets.Enqueue(request.Buffer);
                Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, request.Buffer[4]);
                // Deliberately drop one heartbeat to verify that a single timeout does not disconnect.
                if (responseNumber++ == 1)
                    continue;
                var payload =
                    responseNumber % 2 == 0 ? new byte[] { 0x64 } : new byte[] { 0x01, 0x63 };
                var response = codec.Encode(
                    new WireMessage(0x01, (byte)EggtCsCommandCode.ReadDeviceStatusResponse, payload)
                );
                await physicalDevice.SendAsync(response, request.RemoteEndPoint, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(5, cancellation.Token);
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp
            );
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private sealed class CapturingByteTrafficLogger : IByteTrafficLogger
    {
        private readonly ConcurrentQueue<ByteTrafficLogEntry> _entries = new();

        public IReadOnlyList<ByteTrafficLogEntry> Entries => _entries.ToArray();

        public void Log(ByteTrafficLogEntry entry) =>
            _entries.Enqueue(entry with { Data = entry.Data.ToArray() });
    }
}
