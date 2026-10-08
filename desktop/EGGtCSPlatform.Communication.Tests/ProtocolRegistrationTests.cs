using System.Text;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class ProtocolRegistrationTests
{
    [Fact]
    public async Task Gen1AndAlternativeProtocolKeepCommandsCapabilitiesAndBindingsSeparate()
    {
        var other = new DeviceRuntime.Tests.TestProtocolModule();
        var memory = new DeviceRuntime.Tests.TestTransportFactory();
        var gen1 = new RuntimeTests.TestTransportFactory { Respond = true };
        await using var runtime = new DeviceRuntimeBuilder()
            .AddTransport(memory)
            .AddTransport(gen1)
            .AddProtocol(new EggtCsProtocolModule(new() { AllowUnverifiedChecksum = true }))
            .AddProtocol(other)
            .WithOptions(new() { HeartbeatEnabled = false })
            .Build();
        var first = new DeviceCandidate(
            new(new("gen1"), "EGG/tCS", null, null),
            new("udp", "127.0.0.1", 12345),
            "1.0.1"
        )
        {
            ProtocolId = EggtCsProtocolModule.Id,
        };
        var second = other.Candidate("other");
        await Assert.ThrowsAsync<AmbiguousDeviceProtocolException>(() =>
            runtime.ConnectVerifiedAsync(first with { ProtocolId = null })
        );
        var physical = await runtime.ConnectVerifiedAsync(first);
        var alternative = await runtime.ConnectVerifiedAsync(second);
        Assert.Equal(32, physical.Capabilities.MaximumEegChannels);
        Assert.Equal(12, alternative.Capabilities.MaximumEegChannels);
        Assert.Equal(73, physical.State.BatteryPercent);
        Assert.Equal(77, alternative.State.BatteryPercent);
        Assert.True(
            ((IDeviceProtocolBinding)physical).SupportsCommand(typeof(ControlStimulationRequest))
        );
        Assert.False(
            ((IDeviceProtocolBinding)alternative).SupportsCommand(typeof(ControlStimulationRequest))
        );
        Assert.Equal(
            new byte[] { 0xAA, 0xCC, 0x01, 0x07, 0x04, 0xFF, 0xFF },
            Assert.Single(gen1.Transports.Single().Inner.SentPackets)
        );
        Assert.Contains(
            "test.alpha.status",
            Encoding.UTF8.GetString(Assert.Single(memory.Transports.Single().Sent))
        );
        await Assert.ThrowsAsync<DeviceProtocolConflictException>(() =>
            runtime.ConnectVerifiedAsync(second with { Identity = first.Identity })
        );
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.ConnectVerifiedAsync(first with { ProtocolVersion = "1.0.2" })
        );
    }

    [Fact]
    public async Task LowLevelConnectorRegistrationStillWorksWithExistingConstructor()
    {
        var factory = new RuntimeTests.TestTransportFactory { Respond = true };
        await using var runtime = new DeviceRuntimeBuilder()
            .AddConnector(
                new EggtCsDeviceConnector(
                    factory,
                    new EggtCsUnverifiedFixedChecksum(),
                    EggtCsProtocolOptions.CreateTestDefaults(),
                    true,
                    sessionOptions: new() { HeartbeatPolicy = null }
                )
            )
            .WithOptions(new() { HeartbeatEnabled = false })
            .Build();
        var candidate = new DeviceCandidate(
            new(new("legacy"), "Test", null, null),
            new("udp", "127.0.0.1", 12345),
            "1.0.1"
        );
        var device = await runtime.ConnectVerifiedAsync(candidate);
        var replacement = await runtime.ReconnectAsync(device.Identity.DeviceId);
        Assert.NotSame(device, replacement);
        Assert.Equal(2, factory.Transports.Count);
    }
}
