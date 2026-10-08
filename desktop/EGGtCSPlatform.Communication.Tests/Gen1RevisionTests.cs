using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class Gen1RevisionTests
{
    [Fact]
    public void DefaultIsResolvedAtConstructionAndFutureRevisionIsNotSupported()
    {
        Assert.Equal(EggtCsRevisions.V101, EggtCsRevisions.DefaultRevision);
        Assert.Null(
            typeof(EggtCsProtocolOptions)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Single(p => p.Name == "revision")
                .DefaultValue
        );
        var options = new EggtCsProtocolOptions(new Dictionary<Type, TimeSpan>());
        Assert.Equal("1.0.1", options.Revision);
        Assert.Equal(options.Revision, new EggtCsProtocolProfile(options).Version);
        Assert.Throws<NotSupportedException>(() =>
            new EggtCsProtocolOptions(new Dictionary<Type, TimeSpan>(), revision: "1.0.2")
        );
        Assert.Throws<NotSupportedException>(() => options.ForRevision("1.0.2"));
        Assert.Equal(new[] { "1.0.1" }, EggtCsRevisions.Supported);
    }

    [Theory]
    [InlineData(
        ProtocolRevisionSource.SoftwareDefault,
        "stale",
        true,
        ProtocolRevisionSource.SoftwareDefault
    )]
    [InlineData(
        ProtocolRevisionSource.CompatibilityAssumption,
        "stale",
        true,
        ProtocolRevisionSource.SoftwareDefault
    )]
    [InlineData(ProtocolRevisionSource.Explicit, "1.0.1", true, ProtocolRevisionSource.Explicit)]
    [InlineData(
        ProtocolRevisionSource.Unspecified,
        "1.0.1",
        true,
        ProtocolRevisionSource.Unspecified
    )]
    [InlineData(
        ProtocolRevisionSource.Unspecified,
        "",
        true,
        ProtocolRevisionSource.SoftwareDefault
    )]
    [InlineData(ProtocolRevisionSource.Explicit, "1.0.2", false, ProtocolRevisionSource.Explicit)]
    [InlineData(
        ProtocolRevisionSource.Unspecified,
        "1.0.2",
        false,
        ProtocolRevisionSource.Unspecified
    )]
    public async Task ModuleResolutionFeedsProfileSnapshotAndValidationWithoutChangingBytes(
        ProtocolRevisionSource source,
        string input,
        bool supported,
        ProtocolRevisionSource expectedSource
    )
    {
        var factory = new RuntimeTests.TestTransportFactory { Respond = true };
        var module = new EggtCsProtocolModule(new() { AllowUnverifiedChecksum = true });
        await using var runtime = new DeviceRuntimeBuilder()
            .AddTransport(factory)
            .AddProtocol(module)
            .WithOptions(new() { HeartbeatEnabled = false })
            .Build();
        var candidate = new DeviceCandidate(
            new(new("gen1"), "Gen1", null, null),
            new("udp", "127.0.0.1", 12345),
            input
        )
        {
            ProtocolId = EggtCsProtocolModule.Id,
            RevisionSource = source,
        };
        if (!supported)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                runtime.ConnectVerifiedAsync(candidate)
            );
            Assert.Empty(factory.Transports);
            return;
        }
        var device = await runtime.ConnectVerifiedAsync(candidate);
        var binding = (IDeviceProtocolBinding)device;
        Assert.Equal(new(module.ProtocolId, "1.0.1", expectedSource), binding.ProtocolInfo);
        Assert.All(
            EggtCsCommandCatalog.Commands,
            command => Assert.True(binding.SupportsCommand(command.CommandType))
        );
        Assert.Equal(
            new byte[] { 0xAA, 0xCC, 0x01, 0x07, 0x04, 0xFF, 0xFF },
            Assert.Single(Assert.Single(factory.Transports).Inner.SentPackets)
        );
        Assert.Equal(input, candidate.ProtocolVersion);
    }

    [Fact]
    public void CommandsHaveExplicitMetadataAndOriginalTimeoutDefaults()
    {
        Assert.Equal(8, EggtCsCommandCatalog.Commands.Count);
        var overrides = new Dictionary<Type, TimeSpan>
        {
            [typeof(ReadDeviceStatusRequest)] = TimeSpan.FromMilliseconds(987),
        };
        var options = new EggtCsProtocolOptions(overrides);
        Assert.All(
            EggtCsCommandCatalog.Commands,
            command =>
            {
                Assert.Equal("1.0.1", command.IntroducedIn);
                Assert.False(string.IsNullOrWhiteSpace(command.DisplayName));
                Assert.Equal(new[] { "1.0.1" }, command.Revisions);
                Assert.Equal(TimeSpan.FromMilliseconds(1500), command.DefaultTimeout);
                Assert.Equal(
                    command.CommandType == typeof(ReadDeviceStatusRequest)
                        ? TimeSpan.FromMilliseconds(987)
                        : command.DefaultTimeout,
                    options.GetTimeout(command.CommandType)
                );
            }
        );
    }
}
