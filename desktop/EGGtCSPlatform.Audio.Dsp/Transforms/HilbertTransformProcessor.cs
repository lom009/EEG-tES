using System.Numerics;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.Transforms;

public sealed record HilbertTransformOptions;

/// <summary>Exact N-point analytic signal, independently for each channel; no sample padding.</summary>
public sealed class HilbertTransformProcessor : SingleInputAudioProcessor<HilbertTransformOptions>
{
    public override string Identity => "hilbert-analytic-1";
    protected override SignalSampleType InputType => SignalSampleType.Float64;

    protected override AudioSignalFormat Describe(
        AudioSignalFormat input,
        HilbertTransformOptions options,
        string nodeId
    ) => input with { SampleType = SignalSampleType.Complex128 };

    protected override AudioProcessResult Process(
        AudioSignal input,
        HilbertTransformOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        var source = input.GetFloat();
        var output = new Complex[source.Frames * source.Channels];
        for (var channel = 0; channel < source.Channels; channel++)
        {
            var spectrum = new Complex[source.Frames];
            for (var i = 0; i < spectrum.Length; i++)
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                spectrum[i] = new(source.Data[i * source.Channels + channel], 0);
            }
            if (spectrum.Length > 1)
            {
                FourierTransform.Transform(spectrum, false, token);
                for (var i = 1; i <= (spectrum.Length - 1) / 2; i++)
                {
                    if ((i & 4095) == 0)
                        token.ThrowIfCancellationRequested();
                    spectrum[i] *= 2;
                }
                for (var i = spectrum.Length / 2 + 1; i < spectrum.Length; i++)
                {
                    if ((i & 4095) == 0)
                        token.ThrowIfCancellationRequested();
                    spectrum[i] = Complex.Zero;
                }
                FourierTransform.Transform(spectrum, true, token);
            }
            for (var i = 0; i < spectrum.Length; i++)
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                output[i * source.Channels + channel] = spectrum[i];
            }
        }
        return AudioProcessResult.Single(
            new ComplexSignal(new(source.SampleRate, source.Channels, output), input.Timeline)
        );
    }
}
