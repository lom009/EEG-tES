using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class CommandCodeTests
{
    // Independent wire values: these must not be derived from the production enum.
    [Theory]
    [InlineData(EggtCsCommandCode.DiscoverDeviceRequest, 0x01)]
    [InlineData(EggtCsCommandCode.ReadDeviceStatusRequest, 0x04)]
    [InlineData(EggtCsCommandCode.StartAutomaticExperimentRequest, 0x07)]
    [InlineData(EggtCsCommandCode.ConfigureEegImpedanceRequest, 0x19)]
    [InlineData(EggtCsCommandCode.ControlAcquisitionRequest, 0x1A)]
    [InlineData(EggtCsCommandCode.ConfigureEnvelopeStimulationRequest, 0x4A)]
    [InlineData(EggtCsCommandCode.ConfigureStimulationImpedanceRequest, 0x4B)]
    [InlineData(EggtCsCommandCode.ControlStimulationRequest, 0x4C)]
    [InlineData(EggtCsCommandCode.ControlEnvelopeStimulationRequest, 0x4D)]
    [InlineData(EggtCsCommandCode.AdjustCurrentRequest, 0x51)]
    [InlineData(EggtCsCommandCode.DiscoverDeviceResponse, 0x81)]
    [InlineData(EggtCsCommandCode.ReadDeviceStatusResponse, 0x84)]
    [InlineData(EggtCsCommandCode.StartAutomaticExperimentResponse, 0x87)]
    [InlineData(EggtCsCommandCode.ConfigureEegImpedanceResponse, 0x99)]
    [InlineData(EggtCsCommandCode.ControlAcquisitionResponse, 0x9A)]
    [InlineData(EggtCsCommandCode.ConfigureEnvelopeStimulationResponse, 0xCA)]
    [InlineData(EggtCsCommandCode.ConfigureStimulationImpedanceResponse, 0xCB)]
    [InlineData(EggtCsCommandCode.ControlStimulationResponse, 0xCC)]
    [InlineData(EggtCsCommandCode.ControlEnvelopeStimulationResponse, 0xCD)]
    [InlineData(EggtCsCommandCode.AdjustCurrentResponse, 0xD1)]
    [InlineData(EggtCsCommandCode.EegData, 0x97)]
    [InlineData(EggtCsCommandCode.EegImpedanceData, 0x98)]
    [InlineData(EggtCsCommandCode.AcquisitionCompleted, 0x9C)]
    [InlineData(EggtCsCommandCode.StimulationImpedanceData, 0xD0)]
    [InlineData(EggtCsCommandCode.StimulationProgress, 0xDB)]
    [InlineData(EggtCsCommandCode.StimulationCompleted, 0xCE)]
    public void CodesPreserveProtocolBytes(EggtCsCommandCode code, byte expected) =>
        Assert.Equal(expected, (byte)code);

    [Fact]
    public void CodesAreUniqueBytesAndRequestsHaveMatchingResponses()
    {
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(EggtCsCommandCode)));
        var codes = Enum.GetValues<EggtCsCommandCode>();
        Assert.Equal(26, codes.Length);
        Assert.Equal(codes.Length, codes.Distinct().Count());
        foreach (var name in Enum.GetNames<EggtCsCommandCode>().Where(n => n.EndsWith("Request")))
        {
            var request = Enum.Parse<EggtCsCommandCode>(name);
            var response = Enum.Parse<EggtCsCommandCode>(name.Replace("Request", "Response"));
            Assert.Equal((byte)request + 0x80, (byte)response);
        }
    }
}
