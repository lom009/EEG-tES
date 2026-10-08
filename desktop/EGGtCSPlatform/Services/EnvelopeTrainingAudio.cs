using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace EGGtCSPlatform.Services;

/// <summary>One immutable playback buffer also supplies the plotted and device envelope samples.</summary>
public sealed record EnvelopeTrainingAudio(byte[] WavBytes, double[] WaveSamples, byte[] EnvelopeSamples,
    int EnvelopeSampleRateHz, TimeSpan Duration, string Sha256)
{
    /// <summary>Unnormalised RMS of the final PCM, used only for the orange audio envelope overlay.</summary>
    public double[] AudioEnvelope { get; init; } = [];

    public static readonly string[] DemoSentences = [
        "今天的阳光真好", "窗外的小鸟在唱歌", "我们一起慢慢练习", "公园里面开满鲜花",
        "妈妈正在准备晚饭", "小朋友们喜欢画画", "请把桌上的书给我", "明天我们去看电影",
        "早晨的空气很清新", "这杯温水刚刚合适", "爸爸每天坚持散步", "小猫安静地睡着了",
        "花园里的树长高了", "请你慢慢说一遍", "今天我们学得很好", "窗外下起了小雨",
        "朋友送来一本新书", "大家一起整理房间", "这条小路通向学校", "我们完成今天练习"];

    public static EnvelopeTrainingAudio LoadDemo(int index, double speed, double maximumCurrent)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            $"EGGtCSPlatform.Assets.Training.sentence-{index + 1}.wav")
            ?? throw new InvalidOperationException("训练音频资源缺失。");
        return Prepare(stream, DemoSentences[index], speed, maximumCurrent);
    }

    public static EnvelopeTrainingAudio Prepare(Stream stream, string sentence, double speed, double maximumCurrent)
    {
        if (!double.IsFinite(speed) || speed < .5 || speed > 5) throw new ArgumentException("语速须为 0.5–5 字/s。");
        var (pcm, rate) = ReadPcm(stream);
        // Linear PCM resampling changes duration AND pitch; this is explicitly a demo voice.
        var length = checked((int)Math.Round(sentence.EnumerateRunes().Count() / speed * rate));
        var resized = new short[length];
        for (var i = 0; i < length; i++)
        {
            var position = (double)i * (pcm.Length - 1) / Math.Max(1, length - 1);
            var left = (int)position;
            resized[i] = (short)(pcm[left] + (pcm[Math.Min(left + 1, pcm.Length - 1)] - pcm[left]) * (position - left));
        }
        return Create(resized, rate, maximumCurrent, 50);
    }

    public static EnvelopeTrainingAudio Create(short[] pcm, int rate, double maximumCurrent, int envelopeRate)
    {
        if (pcm.Length == 0 || rate <= 0 || envelopeRate <= 0 || !double.IsFinite(maximumCurrent)
            || maximumCurrent <= 0 || maximumCurrent > 2)
            throw new ArgumentException("音频或包络参数无效，最大电流须在 0–2 mA 内。");
        var duration = (double)pcm.Length / rate;
        var count = (int)Math.Ceiling(duration * envelopeRate);
        if (count > ushort.MaxValue - 17) throw new ArgumentException("音频超出设备包络数据长度限制。");
        var rms = new double[count];
        for (var i = 0; i < count; i++)
        {
            var from = (int)((long)i * rate / envelopeRate);
            var to = Math.Min(pcm.Length, (int)((long)(i + 1) * rate / envelopeRate));
            double sum = 0;
            for (var j = from; j < to; j++) sum += Math.Pow(pcm[j] / 32768d, 2);
            rms[i] = Math.Sqrt(sum / Math.Max(1, to - from));
        }
        var maximum = rms.Max();
        // Round DOWN: the protocol's 0.033 mA step must never exceed the user's limit.
        var envelope = rms.Select(value => (byte)Math.Floor(
            (maximum == 0 ? 0 : value / maximum) * maximumCurrent / .033)).ToArray();
        var waveform = new double[Math.Min(1200, pcm.Length)];
        for (var i = 0; i < waveform.Length; i++)
        {
            var from = (int)((long)i * pcm.Length / waveform.Length);
            var to = (int)((long)(i + 1) * pcm.Length / waveform.Length);
            waveform[i] = pcm.Skip(from).Take(to - from).Select(sample => Math.Abs(sample / 32768d)).DefaultIfEmpty().Max();
        }
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length * 2);
            foreach (var sample in pcm) writer.Write(sample);
        }
        var wav = output.ToArray();
        return new(wav, waveform, envelope, envelopeRate, TimeSpan.FromSeconds(duration),
            Convert.ToHexString(SHA256.HashData(wav))) { AudioEnvelope = rms };
    }

    private static (short[] Pcm, int Rate) ReadPcm(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("需要 PCM WAV。");
        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("WAV 格式无效。");
        int rate = 0, channels = 0, format = 0, bits = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            if (size < 0 || size > stream.Length - stream.Position) throw new InvalidDataException("WAV 块长度无效。");
            var end = stream.Position + size;
            if (id == "fmt ")
            {
                format = reader.ReadInt16(); channels = reader.ReadInt16(); rate = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadInt16(); bits = reader.ReadInt16();
            }
            else if (id == "data")
            {
                if (format != 1 || channels != 1 || bits != 16 || rate <= 0)
                    throw new InvalidDataException("训练音频必须为单声道 16-bit PCM WAV。");
                var samples = new short[size / 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16();
                return (samples, rate);
            }
            stream.Position = end + size % 2;
        }
        throw new InvalidDataException("音频没有 PCM 数据。");
    }
}
