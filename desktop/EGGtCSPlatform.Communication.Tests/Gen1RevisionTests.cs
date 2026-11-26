using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class Gen1RevisionTests
{
    public static IEnumerable<object[]> LegacyCommands()
    {
        yield return new object[] { new ReadDeviceStatusRequest(), new byte[] { 1, 100 } };
        yield return new object[]
        {
            new ConfigureEegImpedanceRequest(
                MeasurementControl.Start,
                new HashSet<int> { 1, 8, 32 }
            ),
            new byte[] { 0 },
        };
        yield return new object[]
        {
            new ControlAcquisitionRequest(RunControl.Start, TimeSpan.FromSeconds(60)),
            new byte[] { 0 },
        };
        yield return new object[]
        {
            new ControlStimulationRequest(RunControl.Start, TimeSpan.FromSeconds(60)),
            new byte[] { 0 },
        };
        yield return new object[]
        {
            new AdjustCurrentRequest(CurrentAdjustment.Increase),
            new byte[] { 0, 4 },
        };
        yield return new object[]
        {
            new ConfigureStimulationImpedanceRequest(
                MeasurementControl.Start,
                new StimulationConfiguration(
                    false,
                    new StimulationChannel[]
                    {
                        new(7, 0.04m, DeviceStimulationChannelRole.FixedActive),
                        new(1, 0.04m),
                    },
                    StimulationWaveform.TAcs,
                    StimulationDirection.Bidirectional,
                    10m,
                    50,
                    TimeSpan.Zero
                )
            ),
            new byte[] { 0 },
        };
    }

    [Theory]
    [MemberData(nameof(LegacyCommands))]
    public void LegacyCommandBehaviorIsIdenticalAcrossRevisions(
        IDeviceCommand command,
        byte[] responsePayload
    )
    {
        var oldProfile = new EggtCsProtocolProfile(
            new(new Dictionary<Type, TimeSpan>(), revision: EggtCsRevisions.V101)
        );
        var newProfile = new EggtCsProtocolProfile(
            new(new Dictionary<Type, TimeSpan>(), revision: EggtCsRevisions.V102)
        );
        var descriptor = oldProfile.Describe(command);
        Assert.Equal(descriptor, newProfile.Describe(command));
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        Assert.Equal(
            codec.Encode(oldProfile.Encode(command, 7)),
            codec.Encode(newProfile.Encode(command, 7))
        );
        var response = new WireMessage(
            7,
            descriptor.ExpectedResponseCommand!.Value,
            responsePayload
        );
        Assert.Equal(oldProfile.Decode(response), newProfile.Decode(response));
    }

    [Fact]
    public async Task ExplicitV101RejectsEnvelopeBeforeSendingAndRemainsPinnedOnReconnect()
    {
        var factory = new RuntimeTests.TestTransportFactory { Respond = true };
        await using var runtime = new DeviceRuntimeBuilder()
            .AddTransport(factory)
            .AddProtocol(new EggtCsProtocolModule(new() { AllowUnverifiedChecksum = true }))
            .WithOptions(new() { HeartbeatEnabled = false })
            .Build();
        var candidate = new DeviceCandidate(
            new(new("legacy"), "Gen1", null, null),
            new("udp", "127.0.0.1", 12345),
            EggtCsRevisions.V101
        )
        {
            ProtocolId = EggtCsProtocolModule.Id,
            RevisionSource = ProtocolRevisionSource.Explicit,
        };
        var device = await runtime.ConnectVerifiedAsync(candidate);
        var options = new EggtCsProtocolOptions(
            new Dictionary<Type, TimeSpan>(),
            revision: EggtCsRevisions.V101
        );
        var profile = new EggtCsProtocolProfile(options);
        var configuration = new EnvelopeStimulationConfiguration(
            100,
            3,
            100,
            500,
            new byte[] { 10 }
        );
        IDeviceCommand[] commands =
        [
            new ConfigureEnvelopeStimulationRequest(configuration),
            new ControlEnvelopeStimulationRequest(RunControl.Start),
            new ControlEnvelopeStimulationRequest(RunControl.Stop),
        ];
        foreach (var command in commands)
        {
            Assert.False(((IDeviceProtocolBinding)device).SupportsCommand(command.GetType()));
            Assert.Throws<NotSupportedException>(() => profile.Describe(command));
            Assert.Throws<NotSupportedException>(() => profile.Encode(command, 1));
            Assert.Throws<NotSupportedException>(() => options.GetTimeout(command.GetType()));
        }
        Assert.NotNull(device.EnvelopeStimulation);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            device.EnvelopeStimulation.ConfigureAsync(configuration)
        );
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            device.EnvelopeStimulation.StartAsync()
        );
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            device.EnvelopeStimulation.StopAsync()
        );
        // The only transmitted frame is the initial status validation.
        var sent = Assert.Single(Assert.Single(factory.Transports).Inner.SentPackets);
        Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, sent[4]);
        var replacement = await runtime.ReconnectAsync(device.Identity.DeviceId);
        Assert.Equal(
            ((IDeviceProtocolBinding)device).ProtocolInfo,
            ((IDeviceProtocolBinding)replacement).ProtocolInfo
        );
        Assert.Equal(EggtCsRevisions.V101, ((IDeviceProtocolBinding)replacement).ProtocolRevision);
    }

    [Fact]
    public void DefaultIsResolvedAtConstructionAndFutureRevisionIsNotSupported()
    {
        Assert.Equal(EggtCsRevisions.V102, EggtCsRevisions.DefaultRevision);
        Assert.Null(
            typeof(EggtCsProtocolOptions)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Single(p => p.Name == "revision")
                .DefaultValue
        );
        var options = new EggtCsProtocolOptions(new Dictionary<Type, TimeSpan>());
        Assert.Equal("1.0.2", options.Revision);
        Assert.Equal(options.Revision, new EggtCsProtocolProfile(options).Version);
        Assert.Throws<NotSupportedException>(() =>
            new EggtCsProtocolOptions(new Dictionary<Type, TimeSpan>(), revision: "9.9.9")
        );
        Assert.Throws<NotSupportedException>(() => options.ForRevision("9.9.9"));
        Assert.Equal(new[] { "1.0.1", "1.0.2" }, EggtCsRevisions.Supported.Order());
        Assert.Equal("1.0.1", options.ForRevision(EggtCsRevisions.V101).Revision);
        Assert.Equal("1.0.2", options.ForRevision(EggtCsRevisions.V102).Revision);
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
    [InlineData(
        ProtocolRevisionSource.SoftwareDefault,
        "1.0.1",
        true,
        ProtocolRevisionSource.SoftwareDefault
    )]
    [InlineData(
        ProtocolRevisionSource.CompatibilityAssumption,
        "1.0.1",
        true,
        ProtocolRevisionSource.SoftwareDefault
    )]
    [InlineData(
        ProtocolRevisionSource.Unspecified,
        "1.0.2",
        true,
        ProtocolRevisionSource.Unspecified
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
    [InlineData(ProtocolRevisionSource.Explicit, "1.0.2", true, ProtocolRevisionSource.Explicit)]
    [InlineData(ProtocolRevisionSource.Explicit, "9.9.9", false, ProtocolRevisionSource.Explicit)]
    [InlineData(
        ProtocolRevisionSource.Unspecified,
        "9.9.9",
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
        var expectedRevision =
            expectedSource == ProtocolRevisionSource.SoftwareDefault ? EggtCsRevisions.V102 : input;
        Assert.Equal(
            new(module.ProtocolId, expectedRevision, expectedSource),
            binding.ProtocolInfo
        );
        Assert.All(
            EggtCsCommandCatalog.Commands,
            command =>
                Assert.Equal(
                    command.Revisions.Contains(expectedRevision),
                    binding.SupportsCommand(command.CommandType)
                )
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
        Assert.Equal(6, EggtCsCommandCatalog.GetSupportedCommands(EggtCsRevisions.V101).Count);
        Assert.Equal(8, EggtCsCommandCatalog.GetSupportedCommands(EggtCsRevisions.V102).Count);
        var overrides = new Dictionary<Type, TimeSpan>
        {
            [typeof(ReadDeviceStatusRequest)] = TimeSpan.FromMilliseconds(987),
        };
        var options = new EggtCsProtocolOptions(overrides);
        Assert.All(
            EggtCsCommandCatalog.Commands,
            command =>
            {
                var envelope =
                    command.CommandType == typeof(ConfigureEnvelopeStimulationRequest)
                    || command.CommandType == typeof(ControlEnvelopeStimulationRequest);
                Assert.Equal(envelope ? "1.0.2" : "1.0.1", command.IntroducedIn);
                Assert.False(string.IsNullOrWhiteSpace(command.DisplayName));
                Assert.Equal(
                    envelope ? new[] { "1.0.2" } : new[] { "1.0.1", "1.0.2" },
                    command.Revisions.Order()
                );
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
