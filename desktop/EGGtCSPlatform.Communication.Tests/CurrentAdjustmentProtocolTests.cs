using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class CurrentAdjustmentProtocolTests
{
    private readonly EggtCsProtocolProfile _profile = new(
        EggtCsProtocolOptions.CreateTestDefaults()
    );

    [Fact]
    public void ToleranceStartUsesDedicatedControlAndZeroDuration()
    {
        var request = new ControlStimulationRequest(RunControl.Start, TimeSpan.Zero)
        {
            StartMode = StimulationStartMode.Tolerance,
        };
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        Assert.Equal(
            new byte[] { 0xAA, 0xCC, 1, 10, 0x4C, 3, 0, 0, 0xFF, 0xFF },
            codec.Encode(_profile.Encode(request, 1))
        );
        Assert.Equal((byte)0xCC, _profile.Describe(request).ExpectedResponseCommand);
        Assert.Equal(
            new byte[] { 1, 60, 0 },
            _profile
                .Encode(
                    new ControlStimulationRequest(RunControl.Start, TimeSpan.FromSeconds(60)),
                    1
                )
                .Payload.ToArray()
        );
        Assert.Equal(
            new byte[] { 2, 0, 0 },
            _profile.Encode(request with { Control = RunControl.Stop }, 1).Payload.ToArray()
        );
        Assert.Equal(
            new byte[] { 1, 60, 0 },
            _profile
                .Encode(
                    new ControlAcquisitionRequest(RunControl.Start, TimeSpan.FromSeconds(60)),
                    1
                )
                .Payload.ToArray()
        );
    }

    [Theory]
    [InlineData(CurrentAdjustment.Increase, 0xAA)]
    [InlineData(CurrentAdjustment.Decrease, 0x55)]
    public void EncodesOneStepWithoutRetry(CurrentAdjustment direction, byte value)
    {
        var request = new AdjustCurrentRequest(direction);
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        Assert.Equal(
            new byte[] { 0xAA, 0xCC, 1, 8, 0x51, value, 0xFF, 0xFF },
            codec.Encode(_profile.Encode(request, 1))
        );
        var descriptor = _profile.Describe(request);
        Assert.False(descriptor.IsIdempotent);
        Assert.Equal((byte)0xD1, descriptor.ExpectedResponseCommand);
    }

    [Theory]
    [InlineData(0, DeviceCommandStatus.Success)]
    [InlineData(1, DeviceCommandStatus.InvalidParameter)]
    [InlineData(2, DeviceCommandStatus.MaximumReached)]
    [InlineData(3, DeviceCommandStatus.MinimumReached)]
    [InlineData(4, DeviceCommandStatus.NotConfigured)]
    [InlineData(255, DeviceCommandStatus.Failed)]
    public void DecodesStatusAndHundredthsWithoutConfigurationMultiplier(
        byte raw,
        DeviceCommandStatus status
    )
    {
        var decoded = _profile.Decode(new(1, 0xD1, new byte[] { raw, 104 }));
        var result = Assert.IsType<CurrentAdjustmentResponse>(decoded.Value);
        Assert.Equal(status, result.Status);
        Assert.Equal(1.04m, result.CurrentMilliAmps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void RejectsWrongResponseLength(int length) =>
        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(new(1, 0xD1, new byte[length]))
        );
}
