using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public static class EggtCsCapabilities
{
    public const int EegChannelCount = 32;
    public const int StimulationChannelCount = 8;
    public static DeviceCapabilities Default { get; } =
        new(
            true,
            true,
            true,
            true,
            true,
            false,
            EegChannelCount,
            new HashSet<int> { 250, 500 },
            StimulationChannelCount
        );

    // The protocol sends all 32 EEG channels; selected channels and sample rate are host metadata.
    public static bool SendsAcquisitionChannelSelection => false;
    public static bool SendsAcquisitionSampleRate => false;
}
