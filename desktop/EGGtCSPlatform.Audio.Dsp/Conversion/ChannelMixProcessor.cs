using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.Conversion;

public enum ChannelMixMode
{
    Average,
    Select,
}

public sealed record ChannelMixOptions(
    ChannelMixMode Mode = ChannelMixMode.Average,
    int ChannelIndex = 0
);

public sealed class ChannelMixProcessor : SingleInputAudioProcessor<ChannelMixOptions>
{
    public override string Identity => "channel-mix-1";
    protected override SignalSampleType InputType => SignalSampleType.Float64;

    protected override AudioSignalFormat Describe(
        AudioSignalFormat input,
        ChannelMixOptions options,
        string nodeId
    )
    {
        if (
            !Enum.IsDefined(options.Mode)
            || options.Mode == ChannelMixMode.Select
                && (options.ChannelIndex < 0 || options.ChannelIndex >= input.Channels)
        )
            throw new ArgumentException("Invalid channel selection.");
        return input with { Channels = 1 };
    }

    protected override AudioProcessResult Process(
        AudioSignal input,
        ChannelMixOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        var source = input.GetFloat();
        var samples = new double[source.Frames];
        for (var i = 0; i < samples.Length; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            if (options.Mode == ChannelMixMode.Select)
                samples[i] = source.Data[i * source.Channels + options.ChannelIndex];
            else
            {
                for (var channel = 0; channel < source.Channels; channel++)
                    samples[i] += source.Data[i * source.Channels + channel];
                samples[i] /= source.Channels;
            }
        }
        return AudioProcessResult.Single(
            new FloatSignal(new(source.SampleRate, 1, samples), input.Timeline),
            options
        );
    }
}
