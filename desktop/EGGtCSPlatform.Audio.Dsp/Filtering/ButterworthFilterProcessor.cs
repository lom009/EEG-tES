using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.Filtering;

public sealed record ButterworthFilterOptions(double CutoffHz = 150, int Order = 5);

/// <summary>Per-channel low-pass Butterworth, forward/backward with odd boundary extension.</summary>
public sealed class ButterworthFilterProcessor : SingleInputAudioProcessor<ButterworthFilterOptions>
{
    public override string Identity => "butterworth-filtfilt-1";
    protected override SignalSampleType InputType => SignalSampleType.Float64;

    protected override AudioSignalFormat Describe(
        AudioSignalFormat input,
        ButterworthFilterOptions options,
        string nodeId
    )
    {
        ButterworthFilter.Validate(input.SampleRate, options.CutoffHz, options.Order);
        return input;
    }

    protected override AudioProcessResult Process(
        AudioSignal input,
        ButterworthFilterOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        var source = input.GetFloat();
        var output = new double[source.Frames * source.Channels];
        for (var channel = 0; channel < source.Channels; channel++)
        {
            var samples = new double[source.Frames];
            for (var i = 0; i < samples.Length; i++)
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                samples[i] = source.Data[i * source.Channels + channel];
            }
            var filtered = ButterworthFilter.Apply(
                samples,
                source.SampleRate,
                token,
                options.CutoffHz,
                options.Order
            );
            for (var i = 0; i < samples.Length; i++)
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                output[i * source.Channels + channel] = filtered[i];
            }
        }
        return AudioProcessResult.Single(
            new FloatSignal(new(source.SampleRate, source.Channels, output), input.Timeline),
            options
        );
    }
}
