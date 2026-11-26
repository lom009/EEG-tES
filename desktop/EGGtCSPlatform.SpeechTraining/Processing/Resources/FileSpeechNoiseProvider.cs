using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Snapshots resources on first use, including failures. Recreate to reload files.</summary>
public sealed class FileSpeechNoiseProvider(string? root = null) : ISpeechNoiseProvider
{
    private readonly string _root = Path.GetFullPath(
        root ?? Path.Combine(AppContext.BaseDirectory, "SpeechNoises")
    );
    private readonly ConcurrentDictionary<string, Lazy<SpeechNoiseResource>> _cache = new(
        StringComparer.Ordinal
    );
    private double? _calibrationRms;
    public double CalibrationRms =>
        _calibrationRms ??= NoiseMixer.Rms(Pcm16Codec.Decode(Read("SSN-MSP.wav").Audio)) / 2;

    public TrainingResourceSnapshot CalibrationResource
    {
        get
        {
            var resource = Read("SSN-MSP.wav");
            return new("calibration", "SSN-MSP.wav", resource.Sha256, resource.Audio.ContentHash);
        }
    }

    public static void ValidateFileName(string name)
    {
        if (
            string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(['/', '\\', ':']) >= 0
            || name != Path.GetFileName(name)
            || !name.EndsWith(".wav", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        )
            throw new ArgumentException("噪声必须是 SpeechNoises 内的 WAV 文件名。", nameof(name));
    }

    public SpeechNoiseResource Read(string fileName)
    {
        ValidateFileName(fileName);
        return _cache
            .GetOrAdd(
                fileName,
                name =>
                    new(() =>
                    {
                        var path = Path.Combine(_root, name);
                        if (!Directory.EnumerateFiles(_root).Any(p => Path.GetFileName(p) == name))
                            throw new FileNotFoundException(
                                $"噪声资源缺失或大小写错误：{path}",
                                path
                            );
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException($"噪声不能是文件链接：{path}");
                        var bytes = File.ReadAllBytes(path);
                        using var stream = new MemoryStream(bytes, writable: false);
                        var audio = Pcm16WaveReader.Read(stream);
                        if (NoiseMixer.Rms(Pcm16Codec.Decode(audio)) <= 0)
                            throw new InvalidDataException($"噪声没有有效能量：{path}");
                        return new(audio, Convert.ToHexString(SHA256.HashData(bytes)));
                    })
            )
            .Value;
    }
}
