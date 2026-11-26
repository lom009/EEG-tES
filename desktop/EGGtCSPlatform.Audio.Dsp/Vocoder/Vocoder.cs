using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.Vocoder;

public sealed class Vocoder
{
    public static AudioBuffer Apply(
        AudioBuffer input,
        VocoderOptions settings,
        CancellationToken token,
        out bool clipped
    )
    {
        if (input.SampleRate != 16000)
            throw new ArgumentException("声码器前必须将音频重采样到 16 kHz。");
        // Reset the same stream for each channel so the carrier realization is shared.
        var channels = VocoderDsp
            .Decode(input)
            .Select(x => Synthesize(x, settings, token: token))
            .ToArray();
        return VocoderDsp.Encode(channels, 16000, out clipped);
    }

    internal static double[] Synthesize(
        double[] x,
        VocoderOptions settings,
        IVocoderRandom? random = null,
        CancellationToken token = default
    )
    {
        settings.Validate();
        token.ThrowIfCancellationRequested();
        if (x.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("声码器输入必须为有限数值。");
        if (x.Length == 0)
            return [];
        random ??= new VocoderRandom(settings.Seed);
        var output = settings.UsesMaxima
            ? Ace(x, settings, random, token)
            : Classical(x, settings, random, token);
        VocoderDsp.MatchRms(output, x);
        if (output.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("声码器计算产生非有限数值。");
        return output;
    }

    private static double[] Classical(
        double[] x,
        VocoderOptions s,
        IVocoderRandom random,
        CancellationToken token
    )
    {
        var output = new double[x.Length];
        var noise = Enumerable.Range(0, x.Length).Select(_ => random.Normal() / 10).ToArray();
        double lo = Math.Log10(80 / 165.4 + 1) / 2.1,
            hi = Math.Log10(7999 / 165.4 + 1) / 2.1;
        var edges = Enumerable
            .Range(0, s.Bands + 1)
            .Select(i => 165.4 * (Math.Pow(10, (lo + (hi - lo) * i / s.Bands) * 2.1) - 1))
            .ToArray();
        for (int b = 0; b < s.Bands; b++)
        {
            token.ThrowIfCancellationRequested();
            var band = VocoderDsp.Butter(x, 3, edges[b], edges[b + 1], token);
            double width = edges[b + 1] - edges[b];
            double[] env;
            if (s.Algorithm == VocoderAlgorithm.ClassicalTle && edges[b + 1] < 250)
                env = band;
            else if (s.Algorithm == VocoderAlgorithm.ClassicalTle && width <= 250)
            {
                var shifted = band.Select(
                        (v, i) => v * Math.Cos(2 * Math.PI * (edges[b] - 50) * i / 16000)
                    )
                    .ToArray();
                env = VocoderDsp.Butter(shifted, 8, width + 150, null, token);
            }
            else
                env = VocoderDsp.Butter(
                    band.Select(v => Math.Max(0, v)).ToArray(),
                    8,
                    125,
                    null,
                    token
                );
            var carrier =
                s.Carrier == VocoderCarrier.Noise
                    ? VocoderDsp.Butter(noise, 3, edges[b], edges[b + 1], token)
                    : null;
            for (int i = 0; i < x.Length; i++)
                output[i] +=
                    env[i]
                    * (
                        carrier?[i]
                        ?? Math.Sin(2 * Math.PI * (edges[b] + edges[b + 1]) / 2 * i / 16000)
                    );
        }
        return output;
    }

    internal static readonly int[][] BandWidths =
    [
        [62],
        [7, 55],
        [7, 15, 40],
        [7, 8, 16, 31],
        [3, 4, 8, 16, 31],
        [3, 4, 6, 9, 15, 25],
        [3, 4, 4, 6, 9, 14, 22],
        [2, 2, 3, 4, 6, 9, 14, 22],
        [2, 2, 3, 3, 5, 7, 9, 13, 18],
        [2, 2, 3, 3, 4, 5, 7, 9, 12, 15],
        [1, 2, 2, 2, 3, 4, 5, 7, 9, 12, 15],
        [1, 2, 2, 2, 2, 3, 4, 5, 7, 9, 11, 14],
        [1, 2, 2, 2, 2, 3, 3, 4, 5, 7, 8, 10, 13],
        [1, 2, 2, 2, 2, 2, 3, 3, 4, 5, 6, 8, 9, 13],
        [1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 5, 6, 8, 9, 13],
        [1, 1, 1, 2, 2, 2, 2, 2, 3, 4, 4, 5, 6, 7, 9, 11],
        [1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 7, 8, 9],
        [1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 7, 8, 9],
        [1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 5, 6, 7, 8, 9],
        [1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 5, 6, 7, 8, 8],
        [1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 6, 7, 8],
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8],
    ];
    internal static readonly double Alpha = FindAlpha();

    private static double FindAlpha()
    {
        double lo = 0,
            hi = 20,
            r = (.5859 / Math.Sqrt(10) - .0156) / (.5859 - .0156);
        for (int i = 0; i < 100; i++)
        {
            double m = (lo + hi) / 2,
                a = Math.Exp(m);
            if (Math.Log(1 + a * r) / Math.Log(1 + a) < .8)
                lo = m;
            else
                hi = m;
        }
        return Math.Exp((lo + hi) / 2);
    }

    internal sealed record AceSequence(
        int[] Electrodes,
        int[] Levels,
        double[] Magnitudes,
        int Frames
    );

    internal static AceSequence AnalyzeAce(
        double[] x,
        VocoderOptions s,
        CancellationToken token = default
    )
    {
        var filtered = VocoderDsp.FirstOrder(
            x.Select(v => v * 1059).ToArray(),
            .5006,
            -.5006,
            -.0012
        );
        for (int i = 0; i < filtered.Length; i++)
            filtered[i] *= .00055325;
        // MATLAB buffer(..., []): 112 initial zeros, advance 16 samples, final partial frame padded.
        int frames = (x.Length + 15) / 16;
        var electrodes = new int[frames * s.Maxima];
        var levels = new int[electrodes.Length];
        var magnitudes = new double[electrodes.Length];
        var widths = BandWidths[s.Bands - 1];
        var starts = new int[s.Bands];
        int next = 2;
        for (int b = 0; b < s.Bands; b++)
        {
            starts[b] = next;
            next += widths[b];
        }
        var window = Enumerable
            .Range(0, 128)
            .Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / 128))
            .ToArray();
        double Power(int k)
        {
            Complex z = 0;
            for (int j = 0; j < 128; j++)
                z += window[j] / 2 * Complex.FromPolarCoordinates(1, -Math.PI * k * j / 128);
            return z.Magnitude * z.Magnitude;
        }
        double p1 = Power(0),
            p2 = 2 * Power(1),
            p3 = Power(0) + 2 * Power(2);
        var gains = widths
            .Select(w =>
                1
                / (
                    w == 1 ? p1
                    : w == 2 ? p2
                    : p3
                )
            )
            .ToArray();
        var theta = new double[s.Bands];
        int outIndex = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            token.ThrowIfCancellationRequested();
            var spectrum = new Complex[128];
            for (int j = 0; j < 128; j++)
            {
                int index = frame * 16 - 112 + j;
                if (index >= 0 && index < x.Length)
                    spectrum[j] = filtered[index] * window[j];
            }
            FourierTransform.Transform(spectrum, false, token);
            var energy = new double[s.Bands];
            var env = new double[s.Bands];
            for (int b = 0; b < s.Bands; b++)
            {
                for (int k = starts[b]; k < starts[b] + widths[b]; k++)
                    energy[b] += spectrum[k].Magnitude * spectrum[k].Magnitude * gains[b];
                energy[b] = Math.Sqrt(energy[b]) * Math.Pow(10, 25d / 20);
                env[b] = energy[b];
                if (s.Algorithm == VocoderAlgorithm.AceTle)
                {
                    Complex value;
                    if (b == 0)
                        value = Math.Sqrt(gains[b]) * spectrum[2];
                    else if (widths[b] < 3 && (starts[b] + widths[b] - 1) * 125 < 1500)
                    {
                        if (frame > 0)
                            theta[b] =
                                (theta[b] + 2 * Math.PI * (starts[b] * 125 - 50) * .001)
                                % (2 * Math.PI);
                        value = 0;
                        for (int k = starts[b]; k < starts[b] + widths[b]; k++)
                            value += spectrum[k] * Complex.FromPolarCoordinates(1, Math.PI * k);
                        value *=
                            widths[b]
                            * Math.Sqrt(gains[b])
                            * Complex.FromPolarCoordinates(1, -theta[b]);
                    }
                    else
                        value = energy[b] / Math.Pow(10, 25d / 20);
                    env[b] = Math.Max(0, value.Real) * Math.Pow(10, 25d / 20);
                }
            }
            // MATLAB min rejects the first lowest channel on ties, then omits NaNs.
            var rejected = Enumerable
                .Range(0, s.Bands)
                .OrderBy(i => energy[i])
                .ThenBy(i => i)
                .Take(s.Bands - s.Maxima)
                .ToHashSet();
            for (int b = s.Bands - 1; b >= 0; b--)
                if (!rejected.Contains(b))
                {
                    double mag =
                        env[b] < .0156
                            ? -1e-10
                            : Math.Log(1 + Alpha * Math.Min(1, (env[b] - .0156) / (.5859 - .0156)))
                                / Math.Log(1 + Alpha);
                    magnitudes[outIndex] = mag;
                    electrodes[outIndex] = mag < 0 ? 0 : s.Bands - b;
                    levels[outIndex] =
                        mag < 0
                            ? 0
                            : (int)Math.Round(100 + 155 * mag, MidpointRounding.AwayFromZero);
                    outIndex++;
                }
        }
        return new(electrodes, levels, magnitudes, frames);
    }

    private static double[] Ace(
        double[] x,
        VocoderOptions s,
        IVocoderRandom random,
        CancellationToken token
    )
    {
        var q = AnalyzeAce(x, s, token);
        double period = 1d / 1000 / s.Maxima;
        int length = (int)
            Math.Round((q.Electrodes.Length * period + .06) * 16000, MidpointRounding.AwayFromZero);
        var output = new double[length];
        var widths = BandWidths[s.Bands - 1];
        int[] starts = new int[s.Bands];
        int next = 2;
        for (int b = 0; b < s.Bands; b++)
        {
            starts[b] = next;
            next += widths[b];
        }
        for (int electrode = 1; electrode <= s.Bands; electrode++)
        {
            token.ThrowIfCancellationRequested();
            int b = s.Bands - electrode;
            double fc = (starts[b] + (widths[b] - 1) / 2d) * 125,
                d = (s.Carrier == VocoderCarrier.Tone ? 2 : 3) / fc;
            var carrier = new double[length];
            if (s.Carrier == VocoderCarrier.Tone)
                for (int i = 0; i < length; i++)
                    carrier[i] = Math.Sin(2 * Math.PI * fc * i / 16000) / s.Maxima;
            else
                for (int tone = 0; tone < (int)Math.Ceiling(widths[b] * 125 * .1); tone++)
                {
                    token.ThrowIfCancellationRequested();
                    double frequency = random.Uniform() * widths[b] * 125 + starts[b] * 125 - 62.5,
                        phase = random.Uniform() * 2 * Math.PI;
                    for (int i = 0; i < length; i++)
                        carrier[i] += Math.Sin(2 * Math.PI * frequency * i / 16000 + phase);
                }
            var env = new double[length];
            int half = 3 * (int)Math.Round(d / 2 * 16000, MidpointRounding.AwayFromZero);
            double levelPower = 0;
            int levelCount = 0;
            for (int p = 0; p < q.Electrodes.Length; p++)
                if (q.Electrodes[p] == electrode)
                {
                    if ((p & 255) == 0)
                        token.ThrowIfCancellationRequested();
                    levelPower += q.Levels[p] * q.Levels[p];
                    levelCount++;
                    double acoustic = (Math.Exp(q.Levels[p] / 255d) * (1 + Alpha) - 1) / Alpha;
                    int center =
                        (int)Math.Round((p + 1) * period * 16000, MidpointRounding.AwayFromZero)
                        - 1;
                    for (int offset = -half; offset <= half; offset++)
                        if (center + offset >= 0 && center + offset < length)
                            env[center + offset] = Math.Max(
                                env[center + offset],
                                acoustic
                                    * Math.Exp(-Math.PI * Math.Pow(offset / 16000d, 2) / (d * d))
                            );
                }
            for (int i = 0; i < length; i++)
                carrier[i] *= env[i];
            var rms = VocoderDsp.Rms(carrier);
            double gain =
                rms == 0 || levelCount == 0 ? 0 : Math.Sqrt(levelPower / levelCount) / rms;
            for (int i = 0; i < length; i++)
                output[i] += carrier[i] * gain;
        }
        output = VocoderDsp.FirstOrder(output, .4994, .4994, -.0012);
        // The research peak scaling is followed by RMS matching; only its sign survives.
        if (output.Max() < 0)
            for (int i = 0; i < length; i++)
                output[i] = -output[i];
        return output;
    }
}

internal interface IVocoderRandom
{
    double Uniform();
    double Normal();
}

// Fixed algorithm rather than System.Random's runtime-dependent implementation.
internal sealed class VocoderRandom(int seed) : IVocoderRandom
{
    private uint state = unchecked((uint)seed) ^ 0x9E3779B9u;

    public double Uniform()
    {
        state = unchecked(state * 1664525u + 1013904223u);
        return (state + .5) / 4294967296d;
    }

    public double Normal() =>
        Math.Sqrt(-2 * Math.Log(Uniform())) * Math.Cos(2 * Math.PI * Uniform());
}
