using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.IO;

public static class Pcm16WaveReader
{
    public static AudioBuffer Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static AudioBuffer Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (Tag() != "RIFF")
            throw new InvalidDataException("音频不是 RIFF WAV。");
        var riffSize = reader.ReadUInt32();
        if (Tag() != "WAVE" || riffSize + 8L > reader.BaseStream.Length)
            throw new InvalidDataException("WAV 文件损坏。");
        int rate = 0,
            channels = 0;
        byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= riffSize + 8L)
        {
            var tag = Tag();
            var size = reader.ReadUInt32();
            var end = reader.BaseStream.Position + size;
            if (end > riffSize + 8L)
                throw new InvalidDataException("WAV 数据不完整。");
            if (tag == "fmt ")
            {
                if (size < 16 || reader.ReadUInt16() != 1)
                    throw new InvalidDataException("仅支持 PCM16 WAV。");
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                var byteRate = reader.ReadInt32();
                var blockAlign = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                if (
                    channels is < 1 or > 2
                    || rate is < 8000 or > 192000
                    || bits != 16
                    || blockAlign != channels * 2
                    || byteRate != rate * blockAlign
                )
                    throw new InvalidDataException("WAV 格式无效或不受支持。");
            }
            else if (tag == "data")
                data = reader.ReadBytes(checked((int)size));
            reader.BaseStream.Position = end + (size % 2);
        }
        if (rate == 0 || data is null || data.Length == 0 || data.Length % (channels * 2) != 0)
            throw new InvalidDataException("WAV 缺少有效音频数据。");
        return new(rate, channels, data);
    }
}
