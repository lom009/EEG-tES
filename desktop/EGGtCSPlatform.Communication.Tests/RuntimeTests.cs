using System.Collections.Concurrent;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class RuntimeTests
{
    [Theory]
    [InlineData(DeviceCapabilitySource.Simulated)]
    [InlineData(DeviceCapabilitySource.Disabled)]
    public async Task RealConnectionWithSimulatedOrDisabledEnvelopeDoesNotSendEnvelopePackets(
        DeviceCapabilitySource source
    )
    {
        var factory = new TestTransportFactory { Respond = true };
        var backend = new DeviceBackendProfile();
        backend.Sources[DeviceCapabilityKind.Stimulation] = source;
        await using var runtime = Build(factory, Options() with { Backend = backend });
        var device = await runtime.ConnectVerifiedAsync(Candidate);
        Assert.Equal(
            DeviceCapabilitySource.Real,
            device.CapabilitySource(DeviceCapabilityKind.StimulationImpedance)
        );
        if (source == DeviceCapabilitySource.Disabled)
            Assert.Null(device.EnvelopeStimulation);
        else
        {
            await device.EnvelopeStimulation!.ConfigureAsync(
                new(40, 3, 100, 500, new byte[] { 10, 20 })
            );
            Assert.True((await device.EnvelopeStimulation.StartAsync()).IsSuccess);
            Assert.True((await device.EnvelopeStimulation.StopAsync()).IsSuccess);
        }
        Assert.DoesNotContain(
            Assert.Single(factory.Transports).Inner.SentPackets,
            p => p[1] == 0xBB || p[4] == (byte)EggtCsCommandCode.ControlEnvelopeStimulationRequest
        );
    }

    [Fact]
    public async Task PureSimulationExposesEnvelopeThroughRuntimeWrapper()
    {
        await using var runtime = new DeviceRuntimeBuilder().UseSimulation().Build();
        await foreach (var candidate in runtime.Discovery.DiscoverAsync(TimeSpan.Zero))
        {
            var device = await runtime.ConnectVerifiedAsync(candidate);
            Assert.Equal(
                DeviceCapabilitySource.Simulated,
                device.CapabilitySource(DeviceCapabilityKind.Stimulation)
            );
            await device.EnvelopeStimulation!.ConfigureAsync(
                new(300, 3, 100, 500, new byte[] { 10, 20 })
            );
            Assert.True((await device.EnvelopeStimulation.StartAsync()).IsSuccess);
            Assert.True((await device.EnvelopeStimulation.StopAsync()).IsSuccess);
            return;
        }
        Assert.Fail("Simulator discovery returned no device.");
    }

    [Fact]
    public async Task EnvelopeUsesRealRuntimeSessionAndIndependentCommands()
    {
        var factory = new TestTransportFactory { Respond = true };
        await using var runtime = Build(factory);
        var device = await runtime.ConnectVerifiedAsync(Candidate);
        var capability = Assert.IsAssignableFrom<IEnvelopeStimulationCapability>(
            device.EnvelopeStimulation
        );
        Assert.True(
            (await capability.ConfigureAsync(new(40, 3, 100, 500, new byte[] { 10, 20 }))).IsSuccess
        );
        Assert.True((await capability.StartAsync()).IsSuccess);
        Assert.Equal(DeviceOperationState.Stimulating, device.State.Operation);
        Assert.True((await capability.StopAsync()).IsSuccess);
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
        var packets = Assert.Single(factory.Transports).Inner.SentPackets;
        Assert.Contains(
            packets,
            p => p[1] == 0xBB && p[5] == (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationRequest
        );
        Assert.Contains(
            packets,
            p =>
                p[1] == 0xCC
                && p[4] == (byte)EggtCsCommandCode.ControlEnvelopeStimulationRequest
                && p[5] == 1
        );
        Assert.Contains(
            packets,
            p =>
                p[1] == 0xCC
                && p[4] == (byte)EggtCsCommandCode.ControlEnvelopeStimulationRequest
                && p[5] == 2
        );
    }

    [Fact]
    public async Task SimulationRejectsPhysicalCandidatesEvenWithARegisteredProtocol()
    {
        var factory = new TestTransportFactory();
        await using var runtime = Build(
            factory,
            Options() with
            {
                Backend = DeviceBackendProfile.Simulation(),
            }
        );
        Assert.False(runtime.Connector.CanConnect(Candidate));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.ConnectVerifiedAsync(Candidate)
        );
        var disguised = Candidate with
        {
            Endpoint = new("simulator", "local", 0),
            ProtocolId = "simulator",
            ConnectionEndpoint = new TransportEndpoint("udp", "127.0.0.1", 12345),
        };
        Assert.False(runtime.Connector.CanConnect(disguised));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.Devices.ConnectAsync(disguised)
        );
        Assert.Empty(factory.Transports);
    }

    [Fact]
    public async Task SimulationModeDiscoveryNeverRunsPhysicalDiscovery()
    {
        await using var runtime = new DeviceRuntimeBuilder()
            .WithOptions(
                new()
                {
                    Backend = DeviceBackendProfile.Simulation(),
                    NetworkSettings = () =>
                        throw new InvalidOperationException("Physical network accessed"),
                }
            )
            .AddProtocol(new EggtCsProtocolModule())
            .Build();
        var candidates = new List<DeviceCandidate>();
        await foreach (
            var candidate in runtime.DiscoverModesAsync([
                new(DiscoveryModeKind.GlobalBroadcast, TimeSpan.FromMilliseconds(1)),
                new(DiscoveryModeKind.LocalBroadcast, TimeSpan.FromMilliseconds(1)),
            ])
        )
            candidates.Add(candidate);
        Assert.Equal("simulator", Assert.Single(candidates).Endpoint.Scheme);
    }

    private static readonly DeviceCandidate Candidate = new(
        new(new("runtime-test"), "Test", null, null),
        new("udp", "127.0.0.1", 12345),
        "1.0.1"
    );

    private static DeviceRuntimeOptions Options() =>
        new() { ValidateConnection = false, HeartbeatEnabled = false };

    private static IDeviceRuntime Build(
        TestTransportFactory factory,
        DeviceRuntimeOptions? options = null,
        CommunicationFailureHandler? handler = null,
        bool allowUnverifiedChecksum = true
    ) =>
        new DeviceRuntimeBuilder()
            .WithOptions(options ?? Options())
            .AddProtocol(
                new EggtCsProtocolModule(
                    new()
                    {
                        AllowUnverifiedChecksum = allowUnverifiedChecksum,
                        Protocol = EggtCsProtocolOptions.CreateUniform(
                            TimeSpan.FromMilliseconds(40)
                        ),
                    }
                )
            )
            .AddTransport(factory)
            .OnFailure(
                handler ?? ((_, _) => ValueTask.FromResult(CommunicationFailureDecision.UseDefault))
            )
            .Build();

    [Fact]
    public async Task ConcurrentConnectCreatesOnlyOneDeviceAndDisconnectThenConnectReplacesIt()
    {
        var factory = new TestTransportFactory();
        await using var runtime = Build(factory);
        var devices = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => runtime.Devices.ConnectAsync(Candidate))
        );
        Assert.All(devices, device => Assert.Same(devices[0], device));
        Assert.Single(factory.Transports);
        await runtime.Devices.DisconnectAsync(Candidate.Identity.DeviceId);
        var replacement = await runtime.Devices.ConnectAsync(Candidate);
        Assert.NotSame(devices[0], replacement);
        Assert.Equal(2, factory.Transports.Count);
        Assert.True(factory.Transports.First().Disposed);
    }

    [Fact]
    public async Task RuntimeIsUsableWithNoUiAndSimulationDoesNotReadNetworkSettings()
    {
        await using var runtime = new DeviceRuntimeBuilder()
            .WithOptions(
                new()
                {
                    Backend = DeviceBackendProfile.Simulation(),
                    NetworkSettings = () => throw new Exception("Network was touched"),
                }
            )
            .Build();
        await foreach (
            var candidate in runtime.Discovery.DiscoverAsync(TimeSpan.FromMilliseconds(1))
        )
        {
            var device = await runtime.Devices.ConnectAsync(candidate);
            Assert.Equal(DeviceConnectionState.Connected, device.State.Connection);
            Assert.Equal(
                DeviceCapabilitySource.Simulated,
                device.CapabilitySource(DeviceCapabilityKind.EegAcquisition)
            );
        }
        Assert.DoesNotContain(
            typeof(DeviceRuntimeBuilder).Assembly.GetReferencedAssemblies(),
            item => item.Name!.Contains("Avalonia")
        );
    }

    [Fact]
    public async Task PhysicalChecksumRequiresExplicitOptIn()
    {
        await using var runtime = Build(new(), allowUnverifiedChecksum: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.Devices.ConnectAsync(Candidate)
        );
    }

    [Fact]
    public async Task TimeoutCallbackReceivesCleanedRequestAndCanReenterManager()
    {
        var factory = new TestTransportFactory();
        var callback = new TaskCompletionSource<CommunicationFailureContext>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        IDeviceRuntime? runtime = null;
        runtime = Build(
            factory,
            handler: async (context, token) =>
            {
                await runtime!.Devices.DisconnectAsync(Candidate.Identity.DeviceId, token);
                callback.TrySetResult(context);
                return CommunicationFailureDecision.FailCurrentOperation;
            }
        );
        await using (runtime)
        {
            var device = await runtime.Devices.ConnectAsync(Candidate);
            await Assert.ThrowsAsync<RequestTimeoutException>(() => device.ReadStatusAsync());
            var context = await callback.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(Candidate.Identity.DeviceId.Value, context.DeviceId);
            Assert.True(context.WasSent);
            Assert.True(context.IsIdempotent);
            Assert.True(context.ResponseMayBeAmbiguous);
            Assert.Equal(typeof(ReadDeviceStatusRequest), context.CommandType);
            Assert.Equal(DeviceConnectionState.Disconnected, device.State.Connection);
        }
    }

    [Fact]
    public async Task ReconnectDecisionReplacesDeviceWithoutReplayingTheFailedCommand()
    {
        var factory = new TestTransportFactory();
        await using var runtime = Build(
            factory,
            handler: (_, _) => ValueTask.FromResult(CommunicationFailureDecision.ReconnectDevice)
        );
        var original = await runtime.Devices.ConnectAsync(Candidate);
        await Assert.ThrowsAsync<RequestTimeoutException>(() => original.ReadStatusAsync());
        await Eventually(() =>
            runtime.Devices.TryGet(Candidate.Identity.DeviceId, out var current)
            && !ReferenceEquals(original, current)
        );
        Assert.Equal(2, factory.Transports.Count);
        Assert.Empty(factory.Transports.Last().Inner.SentPackets);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("timeout")]
    [InlineData("invalid")]
    public async Task BrokenCallbacksDoNotReconnectOrBreakSubsequentFailures(string behavior)
    {
        var factory = new TestTransportFactory();
        var calls = 0;
        var release = new TaskCompletionSource<CommunicationFailureDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var runtime = Build(
            factory,
            Options() with
            {
                FailureHandlerTimeout = TimeSpan.FromMilliseconds(25),
            },
            (context, token) =>
            {
                Interlocked.Increment(ref calls);
                return behavior switch
                {
                    "throw" => throw new InvalidOperationException("consumer failed"),
                    "timeout" => new ValueTask<CommunicationFailureDecision>(release.Task),
                    _ => ValueTask.FromResult((CommunicationFailureDecision)1234),
                };
            }
        );
        var device = await runtime.Devices.ConnectAsync(Candidate);
        await Assert.ThrowsAsync<RequestTimeoutException>(() => device.ReadStatusAsync());
        await Assert.ThrowsAsync<RequestTimeoutException>(() => device.ReadStatusAsync());
        await Eventually(() => Volatile.Read(ref calls) == 2);
        await Task.Delay(60);
        release.TrySetResult(CommunicationFailureDecision.ReconnectDevice);
        await Task.Delay(30);
        Assert.Single(factory.Transports);
        Assert.Equal(DeviceConnectionState.Connected, device.State.Connection);
    }

    [Fact]
    public async Task CallbackFromOldGenerationCannotRemoveNewDevice()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TestTransportFactory();
        await using var runtime = Build(
            factory,
            handler: async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return CommunicationFailureDecision.ReconnectDevice;
            }
        );
        var old = await runtime.Devices.ConnectAsync(Candidate);
        await Assert.ThrowsAsync<RequestTimeoutException>(() => old.ReadStatusAsync());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.Devices.RemoveAsync(Candidate.Identity.DeviceId);
        var current = await runtime.Devices.ConnectAsync(Candidate);
        release.TrySetResult();
        await Task.Delay(70);
        Assert.True(runtime.Devices.TryGet(Candidate.Identity.DeviceId, out var registered));
        Assert.Same(current, registered);
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task SuccessfulConnectionValidationSendsExactlyOneStatusCommand()
    {
        var factory = new TestTransportFactory { Respond = true };
        await using var runtime = Build(factory, Options() with { ValidateConnection = true });
        var device = await runtime.ConnectVerifiedAsync(Candidate);
        Assert.Equal(73, device.State.BatteryPercent);
        Assert.Single(factory.Transports.Single().Inner.SentPackets);
    }

    [Fact]
    public async Task OperationScopePausesHeartbeatAndCompletionReleasesIt()
    {
        var factory = new TestTransportFactory { Respond = true };
        await using var runtime = Build(factory);
        var device = await runtime.Devices.ConnectAsync(Candidate);
        await device.EegAcquisition!.StartAsync(
            TimeSpan.FromSeconds(1),
            new HashSet<int> { 1 },
            500
        );
        Assert.True(runtime.HeartbeatPause.IsPaused(Candidate.Identity.DeviceId));
        factory
            .Transports.Single()
            .Inner.Inject(
                new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()).Encode(
                    new WireMessage(
                        0,
                        (byte)EggtCsCommandCode.AcquisitionCompleted,
                        ReadOnlyMemory<byte>.Empty
                    )
                )
            );
        await Eventually(() => !runtime.HeartbeatPause.IsPaused(Candidate.Identity.DeviceId));
    }

    [Fact]
    public async Task HeartbeatUpdatesSdkStateWithoutUiCallback()
    {
        var factory = new TestTransportFactory { Respond = true };
        await using var runtime = Build(
            factory,
            Options() with
            {
                HeartbeatEnabled = true,
                HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            }
        );
        var device = await runtime.Devices.ConnectAsync(Candidate);
        await Eventually(() => device.State.BatteryPercent == 73);
    }

    [Fact]
    public async Task FatalFaultRebuildsOnlyOnceAndOldEventsCannotRestoreOldState()
    {
        var factory = new TestTransportFactory();
        var decisions = 0;
        await using var runtime = Build(
            factory,
            handler: (context, _) =>
            {
                Assert.Equal("Session", context.Category);
                Interlocked.Increment(ref decisions);
                return ValueTask.FromResult(CommunicationFailureDecision.ReconnectDevice);
            }
        );
        var old = await runtime.Connector.ConnectAsync(Candidate);
        await factory.Transports.Single().Inner.DisconnectAsync();
        await Eventually(() =>
            runtime.Devices.TryGet(Candidate.Identity.DeviceId, out var current)
            && !ReferenceEquals(old, current)
        );
        Assert.Equal(1, decisions);
        Assert.Equal(2, factory.Transports.Count);
        Assert.NotEqual(DeviceConnectionState.Connected, old.State.Connection);
    }

    [Fact]
    public async Task CallerCancellationDoesNotTriggerFailureRecovery()
    {
        var factory = new TestTransportFactory();
        var calls = 0;
        await using var runtime = Build(
            factory,
            handler: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(CommunicationFailureDecision.ReconnectDevice);
            }
        );
        var device = await runtime.Devices.ConnectAsync(Candidate);
        using var cancellation = new CancellationTokenSource();
        var request = device.ReadStatusAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, calls);
        Assert.Single(factory.Transports);
    }

    [Fact]
    public async Task DisposingManagerCancelsConnectionAndQueuedConnects()
    {
        var connector = new BlockingConnector();
        await using var manager = new DeviceManager(connectors: [connector]);
        var connecting = manager.ConnectAsync(Candidate);
        await connector.Entered.Task;
        var queued = manager.ConnectAsync(Candidate);
        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Empty(manager.Devices);
    }

    [Fact]
    public async Task ValidationFailureCleansConnectionBeforeCallbackAndDoesNotOfferReconnect()
    {
        var factory = new TestTransportFactory();
        var callback = new TaskCompletionSource<CommunicationFailureContext>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var runtime = Build(
            factory,
            Options() with
            {
                ValidateConnection = true,
            },
            (context, _) =>
            {
                Assert.True(factory.Transports.Single().Disposed);
                callback.TrySetResult(context);
                return ValueTask.FromResult(CommunicationFailureDecision.FailCurrentOperation);
            }
        );
        await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            runtime.ConnectVerifiedAsync(Candidate)
        );
        var failure = await callback.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(typeof(ReadDeviceStatusRequest), failure.CommandType);
        Assert.DoesNotContain(
            CommunicationFailureDecision.ReconnectDevice,
            failure.AllowedDecisions
        );
        Assert.Empty(runtime.Devices.Devices);
    }

    [Fact]
    public async Task AStatusOnlyDeviceStillPublishesHeartbeatStateEvents()
    {
        var profile = new DeviceBackendProfile
        {
            Sources = Enum.GetValues<DeviceCapabilityKind>()
                .ToDictionary(
                    kind => kind,
                    kind =>
                        kind == DeviceCapabilityKind.Status
                            ? DeviceCapabilitySource.Real
                            : DeviceCapabilitySource.Disabled
                ),
        };
        await using var runtime = Build(
            new() { Respond = true },
            Options() with
            {
                Backend = profile,
                HeartbeatEnabled = true,
                HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            }
        );
        var device = await runtime.Devices.ConnectAsync(Candidate);
        await using var subscription = device.SubscribeEvents(
            new() { Filter = item => item.Event is DeviceStateChangedEvent }
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = subscription.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(
            73,
            Assert.IsType<DeviceStateChangedEvent>(reader.Current.Event).State.BatteryPercent
        );
    }

    private sealed class BlockingConnector : IDeviceConnector
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanConnect(DeviceCandidate candidate) => true;

        public async Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        )
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The manager failed to cancel the connector.");
        }
    }

    [Fact]
    public async Task ManualDisconnectCancelsAnInFlightReconnectAndDoesNotPublishItsResult()
    {
        var connector = new ReconnectBlockingConnector();
        await using var runtime = new DeviceRuntimeBuilder()
            .WithOptions(Options())
            .AddConnector(connector)
            .Build();
        await runtime.Devices.ConnectAsync(Candidate);
        var reconnect = runtime.ReconnectAsync(Candidate.Identity.DeviceId);
        await connector.Reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime
            .Devices.DisconnectAsync(Candidate.Identity.DeviceId)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnect);
        Assert.Empty(runtime.Devices.Devices);
    }

    private sealed class ReconnectBlockingConnector : IDeviceConnector
    {
        private int _attempts;
        public TaskCompletionSource Reconnecting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanConnect(DeviceCandidate candidate) => true;

        public async Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Increment(ref _attempts) == 1)
                return new SimulatedEggtCsDevice(candidate.Identity);
            Reconnecting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Reconnect should have been canceled.");
        }
    }

    [Fact]
    public async Task ADisposalFailureDoesNotPreventOtherDevicesFromBeingReleased()
    {
        var failing = new DisposalDevice("fails", true);
        var healthy = new DisposalDevice("healthy", false);
        var manager = new DeviceManager(connectedDevices: [failing, healthy]);
        await Assert.ThrowsAsync<AggregateException>(() => manager.DisposeAsync().AsTask());
        Assert.True(failing.Disposed);
        Assert.True(healthy.Disposed);
        Assert.Empty(manager.Devices);
    }

    private sealed class DisposalDevice(string id, bool fail) : IEggtCsDevice
    {
        public bool Disposed { get; private set; }
        public DeviceIdentity Identity { get; } = new(new(id), "Test", null, null);
        public DeviceCapabilities Capabilities => EggtCsCapabilities.Default;
        public DeviceStateSnapshot State { get; } =
            new(
                DeviceConnectionState.Connected,
                DeviceOperationState.Ready,
                100,
                DateTimeOffset.UtcNow
            );
        public IEegAcquisitionCapability? EegAcquisition => null;
        public IStimulationCapability? Stimulation => null;
        public IImpedanceCapability? Impedance => null;
        public IToleranceCapability? Tolerance => null;

        public Task<DeviceStatusResponse> ReadStatusAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (fail)
                throw new IOException("Test disposal failure.");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task WaitingOnOneDeviceCallbackDoesNotDelayOtherDevices()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherHandled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var second = Candidate with
        {
            Identity = Candidate.Identity with { DeviceId = new("second-device") },
        };
        await using var runtime = Build(
            new(),
            handler: async (context, token) =>
            {
                if (context.DeviceId == Candidate.Identity.DeviceId.Value)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                else
                    otherHandled.TrySetResult();
                return CommunicationFailureDecision.UseDefault;
            }
        );
        var a = await runtime.Devices.ConnectAsync(Candidate);
        var b = await runtime.Devices.ConnectAsync(second);
        await Assert.ThrowsAsync<RequestTimeoutException>(() => a.ReadStatusAsync());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<RequestTimeoutException>(() => b.ReadStatusAsync());
        await otherHandled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    internal static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    internal sealed class TestTransportFactory : ITransportFactory
    {
        public ConcurrentQueue<AutoTransport> Transports { get; } = new();
        public bool Respond { get; init; }

        public ITransport Create(TransportEndpoint endpoint)
        {
            var transport = new AutoTransport(Respond);
            Transports.Enqueue(transport);
            return transport;
        }
    }

    internal sealed class AutoTransport(bool respond) : ITransport
    {
        public FakeTransport Inner { get; } = new();
        public bool Disposed { get; private set; }
        public TransportConnectionState State => Inner.State;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) =>
            Inner.ConnectAsync(cancellationToken);

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
            Inner.DisconnectAsync(cancellationToken);

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default
        )
        {
            await Inner.SendAsync(data, cancellationToken);
            if (!respond)
                return;
            var command = data.Span[data.Span[1] == 0xBB ? 5 : 4];
            if (command == (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationRequest)
            {
                Inner.Inject(
                    new byte[]
                    {
                        0xAA,
                        0xBB,
                        data.Span[2],
                        8,
                        (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationResponse,
                        0,
                        0xFF,
                        0xFF,
                    }
                );
                return;
            }
            var response = command switch
            {
                (byte)EggtCsCommandCode.ReadDeviceStatusRequest => (byte)
                    EggtCsCommandCode.ReadDeviceStatusResponse,
                (byte)EggtCsCommandCode.ControlAcquisitionRequest => (byte)
                    EggtCsCommandCode.ControlAcquisitionResponse,
                (byte)EggtCsCommandCode.ConfigureEegImpedanceRequest => (byte)
                    EggtCsCommandCode.ConfigureEegImpedanceResponse,
                (byte)EggtCsCommandCode.ControlEnvelopeStimulationRequest => (byte)
                    EggtCsCommandCode.ControlEnvelopeStimulationResponse,
                _ => (byte)EggtCsCommandCode.ControlStimulationResponse,
            };
            Inner.Inject(
                new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()).Encode(
                    new(
                        data.Span[2],
                        response,
                        command == (byte)EggtCsCommandCode.ReadDeviceStatusRequest
                            ? new byte[] { 1, 73 }
                            : new byte[] { 0 }
                    )
                )
            );
        }

        public IAsyncEnumerable<TransportPacket> ReceiveAsync(
            CancellationToken cancellationToken = default
        ) => Inner.ReceiveAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await Inner.DisposeAsync();
        }
    }
}
