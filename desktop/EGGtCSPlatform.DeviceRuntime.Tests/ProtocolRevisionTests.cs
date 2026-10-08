using System.Collections.Concurrent;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using Xunit;

namespace EGGtCSPlatform.DeviceRuntime.Tests;

public sealed class ProtocolRevisionTests
{
    private static DeviceRuntimeOptions Options() =>
        new()
        {
            HeartbeatEnabled = false,
            NetworkSettings = () => new(LocalAddress: "127.0.0.1", LocalPort: 0),
        };

    private static DeviceRuntimeBuilder Builder(
        TestProtocolModule module,
        TestTransportFactory? transport = null
    ) =>
        new DeviceRuntimeBuilder()
            .WithOptions(Options())
            .AddTransport(transport ?? new())
            .AddProtocol(module);

    [Theory]
    [InlineData(
        ProtocolRevisionSource.SoftwareDefault,
        "test.2",
        ProtocolRevisionSource.SoftwareDefault
    )]
    [InlineData(
        ProtocolRevisionSource.CompatibilityAssumption,
        "test.2",
        ProtocolRevisionSource.SoftwareDefault
    )]
    [InlineData(ProtocolRevisionSource.Explicit, "1.0.1", ProtocolRevisionSource.Explicit)]
    [InlineData(ProtocolRevisionSource.Unspecified, "1.0.1", ProtocolRevisionSource.Unspecified)]
    [InlineData(
        ProtocolRevisionSource.DeviceReported,
        "1.0.1",
        ProtocolRevisionSource.DeviceReported
    )]
    public async Task ResolvedSelectionIsUsedOnceByContextBindingAndSession(
        ProtocolRevisionSource source,
        string expected,
        ProtocolRevisionSource expectedSource
    )
    {
        var module = new TestProtocolModule { DefaultRevision = "test.2" };
        var factory = new TestTransportFactory();
        await using var runtime = Builder(module, factory).Build();
        var original = module.Candidate() with { RevisionSource = source };
        var device = await runtime.ConnectVerifiedAsync(original);
        var binding = Assert.IsAssignableFrom<IDeviceProtocolBinding>(device);
        Assert.Equal(new(module.ProtocolId, expected, expectedSource), binding.ProtocolInfo);
        Assert.Equal(expected, binding.ProtocolRevision);
        var context = Assert.Single(module.Contexts);
        Assert.Equal(original.Identity, context.Candidate.Identity);
        Assert.Same(original.Endpoint, context.Candidate.Endpoint);
        Assert.Equal(expected, context.Candidate.ProtocolVersion);
        Assert.Equal(expected, context.SessionOptions.ProtocolRevision);
        Assert.Equal(module.ProtocolId, context.SessionOptions.ProtocolId);
        Assert.Equal(1, module.Resolutions);
        Assert.Equal(1, module.Validations);
        Assert.Single(Assert.Single(factory.Transports).Sent);
        Assert.Equal("1.0.1", original.ProtocolVersion);
    }

    [Fact]
    public async Task ResolutionPrecedesSupportMatchingAndStillRejectsAmbiguityAndConflicts()
    {
        var first = new TestProtocolModule("test.first") { DefaultRevision = "test.2" };
        var second = new TestProtocolModule("test.second") { DefaultRevision = "1.0.1" };
        await using var runtime = Builder(first).AddProtocol(second).Build();
        var candidate = first.Candidate(revision: "obsolete") with
        {
            ProtocolId = null,
            RevisionSource = ProtocolRevisionSource.CompatibilityAssumption,
        };
        await Assert.ThrowsAsync<AmbiguousDeviceProtocolException>(() =>
            runtime.ConnectVerifiedAsync(candidate)
        );
        var connected = await runtime.ConnectVerifiedAsync(
            candidate with
            {
                ProtocolId = first.ProtocolId,
            }
        );
        Assert.Equal("test.2", ((IDeviceProtocolBinding)connected).ProtocolRevision);
        await Assert.ThrowsAsync<DeviceProtocolConflictException>(() =>
            runtime.ConnectVerifiedAsync(first.Candidate())
        );
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.ConnectVerifiedAsync(first.Candidate("unsupported", "test.unknown"))
        );
        first.DefaultRevision = "test.unknown";
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.ConnectVerifiedAsync(candidate with { ProtocolId = first.ProtocolId })
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectPinsOriginalRevisionEvenWhenDiscoveryDefaultChanges(
        bool endpointMoved
    )
    {
        var module = new TestProtocolModule { DefaultRevision = "1.0.1" };
        await using var runtime = Builder(module)
            .WithOptions(
                Options() with
                {
                    SessionOptions = new()
                    {
                        ReconnectPolicy = new(
                            true,
                            TimeSpan.FromMilliseconds(10),
                            TimeSpan.FromMilliseconds(20)
                        ),
                    },
                }
            )
            .Build();
        var candidate = module.Candidate() with
        {
            RevisionSource = ProtocolRevisionSource.SoftwareDefault,
        };
        var old = await runtime.ConnectVerifiedAsync(candidate);
        var snapshot = ((IDeviceProtocolBinding)old).ProtocolInfo;
        module.DefaultRevision = "test.2";
        if (endpointMoved)
        {
            module.FailAddress = "local";
            module.DiscoveryAddress = "moved";
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var replacement = await runtime.ReconnectAsync(old.Identity.DeviceId, timeout.Token);
        Assert.Equal(snapshot, ((IDeviceProtocolBinding)replacement).ProtocolInfo);
        Assert.All(
            module.Contexts,
            context => Assert.Equal("1.0.1", context.Candidate.ProtocolVersion)
        );
        Assert.Equal(1, module.Resolutions);
        if (endpointMoved)
            Assert.Equal("moved", module.Contexts.Last().Candidate.Endpoint.Address);
        await runtime.Devices.RemoveAsync(old.Identity.DeviceId);
        module.FailAddress = null;
        var fresh = await runtime.ConnectVerifiedAsync(candidate);
        Assert.Equal("test.2", ((IDeviceProtocolBinding)fresh).ProtocolRevision);
        Assert.Equal("1.0.1", snapshot.ActiveRevision);
    }

    [Fact]
    public async Task TimedOutOldCallbackAndSessionDiagnosticsKeepTheirOriginalRevision()
    {
        var module = new TestProtocolModule { DefaultRevision = "1.0.1" };
        var factory = new TestTransportFactory();
        var diagnostics = new Diagnostics();
        var entered = new TaskCompletionSource<CommunicationFailureContext>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Builder(module, factory)
            .WithOptions(
                Options() with
                {
                    Diagnostics = diagnostics,
                    FailureHandlerTimeout = TimeSpan.FromMilliseconds(100),
                }
            )
            .OnFailure(
                async (context, _) =>
                {
                    entered.TrySetResult(context);
                    await release.Task;
                    return CommunicationFailureDecision.ReconnectDevice;
                }
            )
            .Build();
        var candidate = module.Candidate() with
        {
            RevisionSource = ProtocolRevisionSource.SoftwareDefault,
        };
        var old = await runtime.ConnectVerifiedAsync(candidate);
        // Reproduce an orphan response: diagnostics must use the session's immutable metadata.
        await factory
            .Transports.First()
            .SendAsync(System.Text.Encoding.UTF8.GetBytes("orphan|test.alpha.status|0"));
        await ProtocolModuleTests.Until(() =>
            diagnostics.Items.Any(item => item.Category == "Response")
        );
        factory.Transports.First().Answer = false;
        await Assert.ThrowsAsync<RequestTimeoutException>(() => old.ReadStatusAsync());
        var failure = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(module.ProtocolId, failure.ProtocolId);
        Assert.Equal("1.0.1", failure.ProtocolRevision);
        var previous = module.Contexts[0];
        await runtime.Devices.RemoveAsync(old.Identity.DeviceId);
        module.DefaultRevision = "test.2";
        var fresh = await runtime.ConnectVerifiedAsync(candidate);
        await ProtocolModuleTests.Until(() =>
            diagnostics.Items.Any(item => item.Category == "FailureHandler")
        );
        release.TrySetResult();
        Assert.All(
            diagnostics.Items.Where(item => item.Category is "FailureHandler" or "Response"),
            item =>
            {
                Assert.Equal("1.0.1", item.ProtocolRevision);
                Assert.Equal(module.ProtocolId, item.ProtocolId);
            }
        );
        previous.SessionOptions.FailureSink!.ReportFailure(failure);
        await Task.Delay(40);
        Assert.Equal(2, module.Connections);
        Assert.Equal("test.2", ((IDeviceProtocolBinding)fresh).ProtocolRevision);
        Assert.Same(fresh, Assert.Single(runtime.Devices.Devices));
    }

    [Fact]
    public void ExistingModuleAndBindingImplementationsKeepDefaultInterfaceBehavior()
    {
        IDeviceProtocolModule module = new LegacyModule();
        var candidate = new TestProtocolModule().Candidate(revision: "custom");
        Assert.Equal(
            new(candidate.ProtocolVersion, candidate.RevisionSource),
            module.ResolveRevision(candidate)
        );
        IDeviceProtocolBinding binding = new LegacyBinding();
        Assert.Equal(
            new("legacy", "custom", ProtocolRevisionSource.Unspecified),
            binding.ProtocolInfo
        );
    }

    private sealed class Diagnostics : ICommunicationDiagnostics
    {
        public ConcurrentQueue<CommunicationDiagnostic> Items { get; } = new();

        public void Report(CommunicationDiagnostic diagnostic) => Items.Enqueue(diagnostic);
    }

    private sealed class LegacyBinding : IDeviceProtocolBinding
    {
        public string ProtocolId => "legacy";
        public string ProtocolRevision => "custom";

        public bool SupportsCommand(Type commandType) => false;
    }

    private sealed class LegacyModule : IDeviceProtocolModule
    {
        public string ProtocolId => "legacy";
        public IReadOnlySet<string> SupportedRevisions => new HashSet<string>();

        public bool CanConnect(DeviceCandidate candidate) => false;

        public DeviceCapabilities GetCapabilities(
            DeviceCandidate candidate,
            IReadOnlySet<DeviceCapabilityKind> realCapabilities
        ) => throw new NotSupportedException();

        public IReadOnlySet<Type> GetSupportedCommands(string revision) => new HashSet<Type>();

        public Task<IEggtCsDevice> ConnectAsync(
            ProtocolConnectionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IDeviceDiscovery CreateDiscovery(IDiscoveryTransport transport, int callbackPort) =>
            throw new NotSupportedException();
    }
}
