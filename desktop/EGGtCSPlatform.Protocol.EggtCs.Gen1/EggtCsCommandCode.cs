namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

/// <summary>Gen1 CMD field values. Headers, payload controls and result codes are separate fields.</summary>
public enum EggtCsCommandCode : byte
{
    DiscoverDeviceRequest = 0x01,
    ReadDeviceStatusRequest = 0x04,
    StartAutomaticExperimentRequest = 0x07,
    ConfigureEegImpedanceRequest = 0x19,
    ControlAcquisitionRequest = 0x1A,
    ConfigureEnvelopeStimulationRequest = 0x4A,
    ConfigureStimulationImpedanceRequest = 0x4B,
    ControlStimulationRequest = 0x4C,
    ControlEnvelopeStimulationRequest = 0x4D,
    AdjustCurrentRequest = 0x51,

    DiscoverDeviceResponse = 0x81,
    ReadDeviceStatusResponse = 0x84,
    StartAutomaticExperimentResponse = 0x87,
    ConfigureEegImpedanceResponse = 0x99,
    ControlAcquisitionResponse = 0x9A,
    ConfigureEnvelopeStimulationResponse = 0xCA,
    ConfigureStimulationImpedanceResponse = 0xCB,
    ControlStimulationResponse = 0xCC,
    ControlEnvelopeStimulationResponse = 0xCD,
    AdjustCurrentResponse = 0xD1,

    EegData = 0x97,
    EegImpedanceData = 0x98,
    AcquisitionCompleted = 0x9C,
    StimulationImpedanceData = 0xD0,
    StimulationProgress = 0xDB,
    StimulationCompleted = 0xCE,
}
