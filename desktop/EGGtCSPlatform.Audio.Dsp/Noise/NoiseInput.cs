using System.Buffers.Binary;
using System.Security.Cryptography;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.Noise;

/// <summary>Identity of the actual graph input, before target-length matching; no sample arrays.</summary>
public sealed record NoiseInputMetadata(
    SignalSampleType SampleType,
    int SampleRate,
    int Channels,
    int Frames,
    string ContentHash
)
{
    public static NoiseInputMetadata FromBuffer(
        ISignalBuffer buffer,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        string hash;
        if (buffer is AudioBuffer pcm)
            hash = pcm.ContentHash;
        else if (buffer is FloatBuffer floats)
        {
            // Canonical little-endian IEEE754, without PCM quantization.
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> chunk = stackalloc byte[4096];
            for (var start = 0; start < floats.Data.Length; start += chunk.Length / 8)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(chunk.Length / 8, floats.Data.Length - start);
                for (var i = 0; i < count; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(
                        chunk.Slice(i * 8, 8),
                        BitConverter.DoubleToInt64Bits(floats.Data[start + i])
                    );
                hasher.AppendData(chunk[..(count * 8)]);
            }
            hash = Convert.ToHexString(hasher.GetHashAndReset());
        }
        else
            throw new ArgumentException("Noise must be PCM16 or Float64.");
        return new(buffer.SampleType, buffer.SampleRate, buffer.Channels, buffer.Frames, hash);
    }
}

internal static class NoiseInput
{
    public static AudioSignalFormat ValidatePorts(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs,
        bool withNoise
    )
    {
        if (
            inputs.Count != (withNoise ? 2 : 1)
            || !inputs.TryGetValue("audio", out var audio)
            || audio.SampleType != SignalSampleType.Pcm16
        )
            throw new ArgumentException(
                "Expected PCM16 audio and exactly the configured noise input."
            );
        if (withNoise)
        {
            if (
                !inputs.TryGetValue("noise", out var noise)
                || noise.SampleType is not (SignalSampleType.Pcm16 or SignalSampleType.Float64)
            )
                throw new ArgumentException("The noise port requires PCM16 or Float64.");
            if (
                noise.SampleType == SignalSampleType.Float64
                && noise.SampleRate != audio.SampleRate
            )
                throw new ArgumentException(
                    "Float64 noise must already have the speech sample rate."
                );
        }
        return audio;
    }

    public static void ValidateOptions(int offset, double snr)
    {
        if (offset < 0 || !double.IsFinite(snr))
            throw new ArgumentException("噪声参数无效。");
    }

    public static double[][] Match(
        AudioSignal noise,
        int frames,
        int sampleRate,
        int channels,
        int offset,
        CancellationToken token
    )
    {
        noise.Timeline.Validate();
        if (noise.Timeline.StartSeconds != 0 || !noise.Timeline.Regions.IsEmpty)
            throw new ArgumentException(
                "Noise is independent material and must not carry offsets or zero regions."
            );
        return noise.Buffer switch
        {
            AudioBuffer pcm => NoiseMixer.MatchNoise(
                pcm,
                frames,
                sampleRate,
                channels,
                offset,
                token
            ),
            FloatBuffer floats => NoiseMixer.MatchNoise(
                floats,
                frames,
                sampleRate,
                channels,
                offset,
                token
            ),
            _ => throw new ArgumentException("Noise must be PCM16 or Float64."),
        };
    }
}
