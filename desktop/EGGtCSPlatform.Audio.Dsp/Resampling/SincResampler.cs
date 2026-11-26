using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.Resampling;

public static class SincResampler
{
    public static AudioBuffer Resample(AudioBuffer pcm, int rate, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        token.ThrowIfCancellationRequested();
        if (rate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(rate));
        if (pcm.SampleRate == rate)
            return pcm;
        var input = VocoderDsp.Decode(pcm);
        int n = input[0].Length;
        int count =
            n == 0
                ? 0
                : Math.Max(
                    1,
                    checked(
                        (int)
                            Math.Round(
                                n * (double)rate / pcm.SampleRate,
                                MidpointRounding.AwayFromZero
                            )
                    )
                );
        var output = input.Select(_ => new double[count]).ToArray();
        double cutoff = Math.Min(1d, rate / (double)pcm.SampleRate) * .94;
        int radius = (int)Math.Ceiling(32 / cutoff);
        for (int i = 0; i < count; i++)
        {
            if ((i & 255) == 0)
                token.ThrowIfCancellationRequested();
            double position = i * (double)pcm.SampleRate / rate,
                sum = 0;
            int center = (int)Math.Floor(position);
            for (int j = center - radius + 1; j <= center + radius; j++)
            {
                double d = position - j,
                    v = d * cutoff;
                if (Math.Abs(d) >= radius)
                    continue;
                double weight =
                    cutoff
                    * (Math.Abs(v) < 1e-12 ? 1 : Math.Sin(Math.PI * v) / (Math.PI * v))
                    * (
                        .42
                        + .5 * Math.Cos(Math.PI * d / radius)
                        + .08 * Math.Cos(2 * Math.PI * d / radius)
                    );
                sum += weight;
                // Constant extension preserves DC, including single-frame input.
                for (int c = 0; c < input.Length; c++)
                    output[c][i] += input[c][Math.Clamp(j, 0, n - 1)] * weight;
            }
            for (int c = 0; c < input.Length; c++)
                output[c][i] /= sum;
        }
        return VocoderDsp.Encode(output, rate, out _);
    }
}
