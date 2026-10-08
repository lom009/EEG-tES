using System.Text;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using Xunit;

namespace EGGtCSPlatform.DeviceRuntime.Tests;

public sealed class ProtocolModuleTests
{
    private static DeviceRuntimeOptions Options() =>
        new()
        {
            HeartbeatEnabled = false,
            DiscoveryNetworkResolver = (_, _) =>
                new(
                    System.Net.IPAddress.Loopback,
                    System.Net.IPAddress.Loopback,
                    [System.Net.IPAddress.Loopback]
                ),
            NetworkSettings = () => new(LocalAddress: "127.0.0.1", LocalPort: 0),
        };

    private static DeviceRuntimeBuilder Builder(
        TestTransportFactory factory,
        params IDeviceProtocolModule[] protocols
    )
    {
        var builder = new DeviceRuntimeBuilder().WithOptions(Options()).AddTransport(factory);
        foreach (var protocol in protocols)
            builder.AddProtocol(protocol);
        return builder;
    }

    private static async Task<List<DeviceCandidate>> Collect(
        IAsyncEnumerable<DeviceCandidate> source
    )
    {
        var values = new List<DeviceCandidate>();
        await foreach (var value in source)
            values.Add(value);
        return values;
    }

    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task RuntimeWithoutConcreteProtocolAssemblyDiscoversConnectsAndValidatesOnlyOnce()
    {
        var protocol = new TestProtocolModule();
        var factory = new TestTransportFactory();
        await using var runtime = Builder(factory, protocol).Build();
        var candidate = Assert.Single(
            await Collect(runtime.Discovery.DiscoverAsync(TimeSpan.FromMilliseconds(1)))
        );
        var devices = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => runtime.ConnectVerifiedAsync(candidate))
        );
        Assert.All(devices, device => Assert.Same(devices[0], device));
        Assert.Equal(1, protocol.Connections);
        Assert.Equal(1, protocol.Validations);
        Assert.Single(Assert.Single(factory.Transports).Sent);
        Assert.Equal(77, devices[0].State.BatteryPercent);
        Assert.Equal(12, devices[0].Capabilities.MaximumEegChannels);
        var binding = Assert.IsAssignableFrom<IDeviceProtocolBinding>(devices[0]);
        Assert.Equal(protocol.ProtocolId, binding.ProtocolId);
        Assert.True(binding.SupportsCommand(typeof(ReadDeviceStatusRequest)));
        Assert.False(binding.SupportsCommand(typeof(ControlStimulationRequest)));
        var dependencies = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "EGGtCSPlatform.DeviceRuntime.Tests.deps.json")
        );
        Assert.DoesNotContain("Protocol.EggtCs.Gen1", dependencies);
        Assert.DoesNotContain("Protocol.V101", dependencies);
        Assert.DoesNotContain("Avalonia", dependencies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModuleDiscoveryUsesRegistrationOrderAndKeepsBothProtocolCandidates(bool modes)
    {
        var first = new TestProtocolModule("test.first");
        var second = new TestProtocolModule("test.second");
        await using var runtime = Builder(new(), first, second).Build();
        var candidates = await Collect(
            modes
                ? runtime.DiscoverModesAsync([
                    new(
                        DiscoveryModeKind.GlobalBroadcast,
                        TimeSpan.FromMilliseconds(1),
                        "127.0.0.1"
                    ),
                ])
                : runtime.Discovery.DiscoverAsync(TimeSpan.FromMilliseconds(1))
        );
        Assert.Equal(
            new[] { "test.first", "test.second" },
            candidates.Select(candidate => candidate.ProtocolId)
        );
        Assert.Equal(1, first.Discoveries);
        Assert.Equal(1, second.Discoveries);
    }

    [Fact]
    public async Task NoRegistrationNeverImplicitlyConnectsOrDiscoversGen1()
    {
        await using var runtime = new DeviceRuntimeBuilder()
            .WithOptions(
                Options() with
                {
                    NetworkSettings = () =>
                        throw new Exception(
                            "Unregistered discovery must not read network settings."
                        ),
                }
            )
            .Build();
        var candidate = new TestProtocolModule().Candidate() with
        {
            ProtocolId = null,
            Endpoint = new("udp", "127.0.0.1", 30307),
        };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.ConnectVerifiedAsync(candidate)
        );
        Assert.Empty(await Collect(runtime.Discovery.DiscoverAsync(TimeSpan.FromMilliseconds(1))));
        Assert.Empty(
            await Collect(
                runtime.DiscoverModesAsync([
                    new(DiscoveryModeKind.GlobalBroadcast, TimeSpan.FromMilliseconds(1)),
                ])
            )
        );
    }

    [Fact]
    public async Task ExplicitIdentityWinsAndUnspecifiedAmbiguityIsRejectedRegardlessOfRegistrationOrder()
    {
        var first = new TestProtocolModule("test.first");
        var second = new TestProtocolModule("test.second");
        var factory = new TestTransportFactory();
        await using var runtime = Builder(factory, first, second).Build();
        await Assert.ThrowsAsync<AmbiguousDeviceProtocolException>(() =>
            runtime.ConnectVerifiedAsync(first.Candidate() with { ProtocolId = null })
        );
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.ConnectVerifiedAsync(first.Candidate() with { ProtocolId = "unknown" })
        );
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.ConnectVerifiedAsync(first.Candidate() with { ProtocolVersion = "99.0" })
        );
        Assert.Empty(factory.Transports);
        var device = await runtime.ConnectVerifiedAsync(second.Candidate());
        Assert.Equal(0, first.Connections);
        Assert.Equal(1, second.Connections);
        await Assert.ThrowsAsync<DeviceProtocolConflictException>(() =>
            runtime.ConnectVerifiedAsync(first.Candidate())
        );
        await Assert.ThrowsAsync<DeviceProtocolConflictException>(() =>
            runtime.ConnectVerifiedAsync(second.Candidate(revision: "test.2"))
        );
        Assert.Same(device, Assert.Single(runtime.Devices.Devices));
    }

    [Fact]
    public async Task ReconnectPinsModuleAndRevisionAndRejectsStaleGenerationCallbacks()
    {
        var first = new TestProtocolModule("test.first");
        var second = new TestProtocolModule("test.second");
        var factory = new TestTransportFactory();
        var faults = 0;
        await using var runtime = Builder(factory, first, second)
            .WithOptions(
                Options() with
                {
                    SessionFaulted = (_, _) => Interlocked.Increment(ref faults),
                }
            )
            .Build();
        var old = await runtime.ConnectVerifiedAsync(second.Candidate(revision: "test.2"));
        var previous = second.Contexts[0];
        var replacement = await runtime.ReconnectAsync(old.Identity.DeviceId);
        Assert.NotSame(old, replacement);
        Assert.True(factory.Transports.First().Disposed);
        Assert.Equal(0, first.Connections);
        Assert.Equal(2, second.Connections);
        Assert.All(
            second.Contexts,
            context => Assert.Equal("test.2", context.Candidate.ProtocolVersion)
        );
        Assert.True(
            second.Contexts[1].SessionOptions.ConnectionGeneration
                > previous.SessionOptions.ConnectionGeneration
        );
        previous.SessionFaulted(old.Identity, new IOException("stale"));
        previous.SessionOptions.FailureSink!.ReportFailure(
            new(
                old.Identity.DeviceId.Value,
                Guid.NewGuid(),
                previous.SessionOptions.ConnectionGeneration,
                "Session",
                new IOException("stale"),
                null,
                false,
                false,
                false,
                new HashSet<CommunicationFailureDecision>
                {
                    CommunicationFailureDecision.ReconnectDevice,
                }
            )
        );
        Assert.Equal(0, faults);
        Assert.Equal(DeviceConnectionState.Connected, replacement.State.Connection);
        await runtime.Devices.DisconnectAsync(old.Identity.DeviceId);
        var third = await runtime.ConnectVerifiedAsync(second.Candidate(revision: "test.2"));
        Assert.NotSame(replacement, third);
        Assert.Equal(3, second.Connections);
    }

    [Fact]
    public async Task ModuleHeartbeatUsesRuntimePauseAndPublishesBatteryUpdates()
    {
        var protocol = new TestProtocolModule();
        var factory = new TestTransportFactory();
        var observed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var runtime = Builder(factory, protocol)
            .WithOptions(
                Options() with
                {
                    HeartbeatEnabled = true,
                    HeartbeatInterval = TimeSpan.FromMilliseconds(20),
                    HeartbeatReceived = (_, status) =>
                    {
                        if (status.BatteryPercent == 66)
                            observed.TrySetResult(status.BatteryPercent);
                    },
                }
            )
            .Build();
        using var pause = runtime.HeartbeatPause.Pause(protocol.Candidate().Identity.DeviceId);
        var device = await runtime.ConnectVerifiedAsync(protocol.Candidate());
        var transport = Assert.Single(factory.Transports);
        await Task.Delay(70);
        Assert.Single(transport.Sent);
        await using var events = device.SubscribeEvents();
        await events.Ready;
        transport.Battery = 66;
        pause.Dispose();
        Assert.Equal(66, await observed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(66, device.State.BatteryPercent);
        using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var item in events.ReadEventsAsync(eventTimeout.Token))
            if (item.Event is DeviceStateChangedEvent { State.BatteryPercent: 66 })
                break;
    }

    [Fact]
    public async Task AlternateProtocolFailureCanReconnectWithoutReplayingOldCommands()
    {
        var protocol = new TestProtocolModule();
        var factory = new TestTransportFactory();
        var observed = new TaskCompletionSource<CommunicationFailureContext>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var runtime = Builder(factory, protocol)
            .OnFailure(
                (context, _) =>
                {
                    observed.TrySetResult(context);
                    return ValueTask.FromResult(CommunicationFailureDecision.ReconnectDevice);
                }
            )
            .Build();
        var device = await runtime.ConnectVerifiedAsync(protocol.Candidate());
        factory.Transports.First().Answer = false;
        await Assert.ThrowsAsync<RequestTimeoutException>(() => device.ReadStatusAsync());
        var failure = await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(device.Identity.DeviceId.Value, failure.DeviceId);
        Assert.Equal(typeof(ReadDeviceStatusRequest), failure.CommandType);
        Assert.True(failure.WasSent);
        await Until(() =>
            runtime.Devices.TryGet(device.Identity.DeviceId, out var current)
            && !ReferenceEquals(device, current)
        );
        Assert.Equal(2, protocol.Connections);
        Assert.Equal(2, protocol.Validations);
        Assert.Single(factory.Transports.Last().Sent); // Only the new connection's validation.
        Assert.All(
            factory.Transports.Last().Sent,
            packet => Assert.Contains("test.alpha.status", Encoding.UTF8.GetString(packet))
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomDiscoveryDoesNotAppendImplicitProtocolProbes(bool modes)
    {
        var custom = new CustomDiscovery();
        await using var runtime = new DeviceRuntimeBuilder()
            .AddDiscovery(custom)
            .WithOptions(
                Options() with
                {
                    NetworkSettings = () =>
                        throw new Exception("No protocol transport should be created."),
                }
            )
            .Build();
        var candidates = await Collect(
            modes
                ? runtime.DiscoverModesAsync([
                    new(DiscoveryModeKind.GlobalBroadcast, TimeSpan.FromMilliseconds(1)),
                ])
                : runtime.Discovery.DiscoverAsync(TimeSpan.FromMilliseconds(1))
        );
        Assert.Single(candidates);
        Assert.Equal(1, custom.Calls);
    }

    private sealed class CustomDiscovery : IDeviceDiscovery
    {
        public int Calls;

        public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            TimeSpan window,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new TestProtocolModule().Candidate();
        }
    }

    [Fact]
    public void AddedTestCommandUsesDefaultWithoutRequiringNewOverrideAndHonorsRevisionGating()
    {
        var definitions = new ProtocolCommandCatalog([
            new(
                typeof(ReadDeviceStatusRequest),
                TimeSpan.FromSeconds(1),
                new HashSet<string> { "test.1", "test.2" }
            ),
            new(typeof(AddedCommand), TimeSpan.FromSeconds(3), new HashSet<string> { "test.2" }),
        ]);
        var oldOverrides = new Dictionary<Type, TimeSpan>
        {
            [typeof(ReadDeviceStatusRequest)] = TimeSpan.FromSeconds(2),
        };
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            definitions.GetTimeout(typeof(AddedCommand), "test.2", oldOverrides)
        );
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            definitions.GetTimeout(typeof(ReadDeviceStatusRequest), "test.2", oldOverrides)
        );
        Assert.DoesNotContain(typeof(AddedCommand), definitions.GetSupportedCommands("test.1"));
        Assert.Throws<NotSupportedException>(() =>
            definitions.GetTimeout(typeof(AddedCommand), "test.1", oldOverrides)
        );
        Assert.Throws<NotSupportedException>(() =>
            definitions.GetTimeout(typeof(AddedCommand), "test.3", oldOverrides)
        );
    }

    private sealed record AddedCommand : IDeviceRequest<int>;
}
