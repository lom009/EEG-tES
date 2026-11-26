using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.Noise;

public sealed record NoiseMixResult(
    AudioBuffer Audio,
    double NormalizationGain,
    double PeakGain,
    double OutputRms
);

public static class NoiseMixer
{
    public static NoiseMixResult Mix(
        AudioBuffer input,
        double[][] noise,
        double snrDb,
        double calibrationRms,
        CancellationToken token = default
    )
    {
        if (!double.IsFinite(snrDb) || !double.IsFinite(calibrationRms) || calibrationRms <= 0)
            throw new ArgumentException("噪声参数无效。");
        var target = VocoderDsp.Decode(input);
        var frames = target[0].Length;
        if (
            frames == 0
            || Rms(target) <= 0
            || noise.Length != target.Length
            || noise.Any(c => c.Length != frames)
        )
            throw new InvalidDataException("目标语音或噪声无效。");
        var noiseRms = Rms(noise);
        if (noiseRms <= 0)
            throw new InvalidDataException("截取后的噪声没有有效能量。");
        // Scale the speech relative to the selected noise, then normalize the complete mix.
        var speechGain = noiseRms / Rms(target) * Math.Pow(10, snrDb / 20);
        if (!double.IsFinite(speechGain) || speechGain <= 0)
            throw new ArgumentException("SNR 超出可表示范围。");
        var mix = target
            .Select(
                (channel, c) => channel.Select((v, i) => v * speechGain + noise[c][i]).ToArray()
            )
            .ToArray();
        var rms = Rms(mix);
        if (!double.IsFinite(rms) || rms <= 0)
            throw new InvalidDataException("混音没有有效能量。");
        var normalization = calibrationRms / rms;
        var peak = mix.Max(c => c.Max(Math.Abs)) * normalization;
        var peakGain = Math.Min(1, (32767d / 32768) / peak);
        for (var c = 0; c < mix.Length; c++)
        for (var i = 0; i < frames; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            mix[c][i] *= normalization * peakGain;
        }
        var playback = VocoderDsp.Encode(mix, input.SampleRate, out var clipped);
        if (clipped)
            throw new InvalidOperationException("噪声混音意外削波。");
        return new(playback, normalization, peakGain, Rms(VocoderDsp.Decode(playback)));
    }

    public static double[][] MatchNoise(
        AudioBuffer resource,
        int frames,
        int sampleRate,
        int channels,
        int offset,
        CancellationToken token = default
    )
    {
        if (frames <= 0 || channels is < 1 or > 2 || offset < 0 || offset >= frames)
            throw new ArgumentException("噪声截取参数无效。");
        var source = VocoderDsp.Decode(
            EGGtCSPlatform.Audio.Dsp.Resampling.SincResampler.Resample(resource, sampleRate, token)
        );
        return MatchSamples(source, frames, channels, offset, token);
    }

    public static double[][] MatchNoise(
        FloatBuffer resource,
        int frames,
        int sampleRate,
        int channels,
        int offset,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        if (
            frames <= 0
            || channels is < 1 or > 2
            || offset < 0
            || offset >= frames
            || resource.SampleRate != sampleRate
        )
            throw new ArgumentException("浮点噪声截取参数或采样率无效。");
        var source = new double[resource.Channels][];
        for (var c = 0; c < resource.Channels; c++)
        {
            source[c] = new double[resource.Frames];
            for (var i = 0; i < resource.Frames; i++)
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                source[c][i] = resource.Data[i * resource.Channels + c];
            }
        }
        return MatchSamples(source, frames, channels, offset, token);
    }

    private static double[][] MatchSamples(
        double[][] source,
        int frames,
        int channels,
        int offset,
        CancellationToken token
    )
    {
        var noise = new double[channels][];
        for (var c = 0; c < noise.Length; c++)
        {
            noise[c] = new double[frames];
            for (var i = 0; i < frames; i++)
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                var j = (i + offset) % frames;
                if (j < source[0].Length)
                    noise[c][i] =
                        channels == 1 && source.Length == 2
                            ? (source[0][j] + source[1][j]) / 2
                            : source[Math.Min(c, source.Length - 1)][j];
            }
        }
        if (Rms(noise) <= 0)
            throw new InvalidDataException("截取后的噪声没有有效能量。");
        return noise;
    }

    public static double Rms(double[][] data) =>
        Math.Sqrt(data.Sum(c => c.Sum(v => v * v)) / data.Sum(c => c.Length));
}
