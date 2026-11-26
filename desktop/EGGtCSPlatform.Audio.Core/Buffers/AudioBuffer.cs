using System.Security.Cryptography;

namespace EGGtCSPlatform.Audio.Core.Buffers;

/// <summary>Immutable interleaved little-endian PCM16. Construction snapshots caller-owned data.</summary>
public sealed class AudioBuffer : IEquatable<AudioBuffer>, ISignalBuffer
{
    public SignalSampleType SampleType => SignalSampleType.Pcm16;
    private readonly byte[] _data;
    public int SampleRate { get; }
    public int Channels { get; }
    public ReadOnlySpan<byte> Data => _data;
    public int Frames => _data.Length / (Channels * 2);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);
    public string ContentHash { get; }

    public AudioBuffer(int sampleRate, int channels, ReadOnlySpan<byte> data)
    {
        if (
            sampleRate is < 8000 or > 192000
            || channels is < 1 or > 2
            || data.Length % (channels * 2) != 0
        )
            throw new ArgumentException("音频处理需要有效的 PCM16 音频。");
        SampleRate = sampleRate;
        Channels = channels;
        _data = data.ToArray();
        ContentHash = Convert.ToHexString(SHA256.HashData(_data));
    }

    public byte[] ToArray() => (byte[])_data.Clone();

    public bool Equals(AudioBuffer? other) =>
        other is not null
        && SampleRate == other.SampleRate
        && Channels == other.Channels
        && ContentHash == other.ContentHash;

    public override bool Equals(object? obj) => obj is AudioBuffer buffer && Equals(buffer);

    public override int GetHashCode() => HashCode.Combine(SampleRate, Channels, ContentHash);
}
