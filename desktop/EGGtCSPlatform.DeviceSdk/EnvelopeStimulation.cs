using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk;

/// <summary>Wire-ready envelope data, independent of the legacy stimulation and EEG simulator models.</summary>
public sealed class EnvelopeStimulationConfiguration
{
    private readonly byte[] _samples;

    public EnvelopeStimulationConfiguration(
        ushort delayMilliseconds,
        byte channelMask,
        ushort sampleRateHz,
        ushort debugFrequencyHz,
        ReadOnlyMemory<byte> samples
    )
    {
        if (channelMask == 0)
            throw new ArgumentOutOfRangeException(nameof(channelMask));
        if (sampleRateHz == 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (samples.Length == 0 || samples.Length > ushort.MaxValue - 17)
            throw new ArgumentOutOfRangeException(nameof(samples));
        DelayMilliseconds = delayMilliseconds;
        ChannelMask = channelMask;
        SampleRateHz = sampleRateHz;
        DebugFrequencyHz = debugFrequencyHz;
        _samples = samples.ToArray();
    }

    public ushort DelayMilliseconds { get; }
    public byte ChannelMask { get; }
    public ushort SampleRateHz { get; }
    public ushort DebugFrequencyHz { get; }
    public ReadOnlyMemory<byte> Samples => _samples;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_samples.Length / SampleRateHz);

    public static decimal ToMilliAmps(byte sample) => sample * 0.033m;
}

public sealed record ConfigureEnvelopeStimulationRequest(
    EnvelopeStimulationConfiguration Configuration
) : IDeviceRequest<DeviceCommandResult>;

public sealed record ControlEnvelopeStimulationRequest(RunControl Control)
    : IDeviceRequest<DeviceCommandResult>;

public interface IEnvelopeStimulationCapability
{
    Task<DeviceCommandResult> ConfigureAsync(
        EnvelopeStimulationConfiguration configuration,
        CancellationToken cancellationToken = default
    );
    Task<DeviceCommandResult> StartAsync(CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default);
}
