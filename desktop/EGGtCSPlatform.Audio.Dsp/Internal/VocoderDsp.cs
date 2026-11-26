using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Envelope;

namespace EGGtCSPlatform.Audio.Dsp.Internal;

internal static class VocoderDsp
{
    internal static double[][] Decode(AudioBuffer pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        var result = Enumerable
            .Range(0, pcm.Channels)
            .Select(_ => new double[pcm.Data.Length / (2 * pcm.Channels)])
            .ToArray();
        for (int i = 0; i < result[0].Length; i++)
        for (int c = 0; c < pcm.Channels; c++)
            result[c][i] =
                BinaryPrimitives.ReadInt16LittleEndian(
                    pcm.Data.Slice(2 * (i * pcm.Channels + c), 2)
                ) / 32768d;
        return result;
    }

    internal static AudioBuffer Encode(double[][] channels, int rate, out bool clipped)
    {
        clipped = false;
        var data = new byte[checked(channels[0].Length * channels.Length * 2)];
        for (int i = 0; i < channels[0].Length; i++)
        for (int c = 0; c < channels.Length; c++)
        {
            var value = channels[c][i] * 32768;
            if (!double.IsFinite(value))
                throw new InvalidOperationException("声码器输出包含非有限数值。");
            var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            clipped |= rounded < short.MinValue || rounded > short.MaxValue;
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(2 * (i * channels.Length + c), 2),
                (short)Math.Clamp(rounded, short.MinValue, short.MaxValue)
            );
        }
        return new(rate, channels.Length, data);
    }

    internal static double Rms(double[] data) =>
        data.Length == 0 ? 0 : Math.Sqrt(data.Sum(v => v * v) / data.Length);

    internal static void MatchRms(double[] output, double[] input)
    {
        var rms = Rms(output);
        var gain = rms == 0 ? 0 : Rms(input) / rms;
        for (int i = 0; i < output.Length; i++)
            output[i] *= gain;
    }

    internal static double[] FirstOrder(double[] input, double b0, double b1, double a1)
    {
        var output = new double[input.Length];
        double state = 0;
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = b0 * input[i] + state;
            state = b1 * input[i] - a1 * output[i];
        }
        return output;
    }

    // Butterworth analog poles -> lowpass/bandpass transform -> bilinear transform.
    // Conjugate sections avoid cancellation in the eighth-order 125 Hz denominator.
    internal static double[] Butter(
        double[] input,
        int order,
        double low,
        double? high,
        CancellationToken token
    )
    {
        var poles = new List<Complex>();
        double w1 = Math.Tan(Math.PI * low / 16000),
            w2 = high is { } h ? Math.Tan(Math.PI * h / 16000) : 0;
        for (int k = 0; k < order; k++)
        {
            var p = Complex.FromPolarCoordinates(1, Math.PI * (2 * k + order + 1) / (2 * order));
            if (high is null)
                poles.Add(p * w1);
            else
            {
                var b = p * (w2 - w1);
                var d = Complex.Sqrt(b * b - 4 * w1 * w2);
                poles.Add((b + d) / 2);
                poles.Add((b - d) / 2);
            }
        }
        poles = poles.Select(p => (1 + p) / (1 - p)).ToList();
        var pairs = new List<(Complex, Complex)>();
        while (poles.Count > 0)
        {
            var p = poles[0];
            poles.RemoveAt(0);
            if (poles.Count == 0)
            {
                pairs.Add((p, Complex.Zero));
                break;
            }
            var j = Enumerable
                .Range(0, poles.Count)
                .MinBy(i => Complex.Abs(poles[i] - Complex.Conjugate(p)));
            pairs.Add((p, poles[j]));
            poles.RemoveAt(j);
        }
        var result = (double[])input.Clone();
        var z = Complex.FromPolarCoordinates(
            1,
            high is null ? 0 : 2 * Math.Atan(Math.Sqrt(w1 * w2))
        );
        foreach (
            var (p, q) in pairs.OrderBy(pair =>
                Math.Max(pair.Item1.Magnitude, pair.Item2.Magnitude)
            )
        )
        {
            double a1 = -(p + q).Real,
                a2 = (p * q).Real;
            double b1 = high is null ? (q == Complex.Zero ? 1 : 2) : 0,
                b2 = q == Complex.Zero ? 0
                : high is null ? 1
                : -1;
            var gain = Complex.Abs((1 + a1 / z + a2 / (z * z)) / (1 + b1 / z + b2 / (z * z)));
            double s1 = 0,
                s2 = 0;
            for (int i = 0; i < result.Length; i++)
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                var x = result[i];
                var y = gain * x + s1;
                s1 = gain * b1 * x - a1 * y + s2;
                s2 = gain * b2 * x - a2 * y;
                result[i] = y;
            }
        }
        return result;
    }
}
