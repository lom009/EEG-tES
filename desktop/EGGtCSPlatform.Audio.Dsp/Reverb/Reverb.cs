using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.Reverb;

public sealed record ReverbResult(
    AudioBuffer Audio,
    double CalibrationGain,
    double PeakGain,
    double EarlySpeechRms,
    double NoiseGain
);

public static class Reverb
{
    public static ReverbResult Apply(
        AudioBuffer input,
        RirResponse response,
        double calibrationRms,
        double[][]? matched = null,
        double snrDb = 0,
        CancellationToken token = default
    )
    {
        if (
            input.SampleRate != RirSettings.SampleRate
            || !double.IsFinite(calibrationRms)
            || calibrationRms <= 0
            || !double.IsFinite(snrDb)
        )
            throw new ArgumentException("RIR 需要 16 kHz 音频及有效校准参数。");
        var target = VocoderDsp.Decode(input);
        if (target[0].Length == 0 || EGGtCSPlatform.Audio.Dsp.Noise.NoiseMixer.Rms(target) <= 0)
            throw new InvalidDataException("目标语音没有有效能量。");
        var gain = calibrationRms / response.EarlyCoefficientRms;
        var mix = target.Select(channel => Convolve(channel, response.Effective, token)).ToArray();
        var early = target
            .Select(channel => Convolve(channel, response.EarlyReference, token))
            .ToArray();
        for (var channel = 0; channel < mix.Length; channel++)
        {
            Array.Resize(ref early[channel], mix[channel].Length);
            for (var i = 0; i < mix[channel].Length; i++)
            {
                mix[channel][i] *= gain;
                early[channel][i] *= gain;
            }
        }
        var earlyRms = EGGtCSPlatform.Audio.Dsp.Noise.NoiseMixer.Rms(early);
        var frames = mix[0].Length;
        double noiseGain = 0;
        if (matched is not null)
        {
            if (matched.Length != target.Length || matched.Any(c => c.Length != frames))
                throw new ArgumentException("RIR 噪声长度不匹配。");
            noiseGain =
                earlyRms
                / EGGtCSPlatform.Audio.Dsp.Noise.NoiseMixer.Rms(matched)
                / Math.Pow(10, snrDb / 20);
            if (!double.IsFinite(noiseGain) || noiseGain <= 0)
                throw new ArgumentException("SNR 超出可表示范围。");
            for (var channel = 0; channel < mix.Length; channel++)
            for (var i = 0; i < frames; i++)
                mix[channel][i] += noiseGain * matched[channel][i];
        }
        var peak = mix.Max(channel => channel.Max(Math.Abs));
        if (!double.IsFinite(peak) || peak <= 0)
            throw new InvalidDataException("RIR 混音没有有效能量。");
        var peakGain = Math.Min(1, (32767d / 32768) / peak);
        for (var channel = 0; channel < mix.Length; channel++)
        for (var i = 0; i < frames; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            mix[channel][i] *= peakGain;
        }
        var audio = VocoderDsp.Encode(mix, RirSettings.SampleRate, out var clipped);
        if (clipped)
            throw new InvalidOperationException("RIR 混音意外削波。");
        return new(audio, gain, peakGain, earlyRms, noiseGain);
    }

    public static double[] Convolve(
        double[] signal,
        ImmutableArray<double> impulse,
        CancellationToken token
    )
    {
        var count = checked(signal.Length + impulse.Length - 1);
        var size = 1;
        while (size < count)
            size = checked(size * 2);
        var a = new Complex[size];
        var b = new Complex[size];
        for (var i = 0; i < signal.Length; i++)
            a[i] = signal[i];
        for (var i = 0; i < impulse.Length; i++)
            b[i] = impulse[i];
        FourierTransform.Transform(a, false, token);
        FourierTransform.Transform(b, false, token);
        for (var i = 0; i < size; i++)
            a[i] *= b[i];
        FourierTransform.Transform(a, true, token);
        return a.Take(count).Select(v => v.Real).ToArray();
    }
}
