using EGGtCSPlatform.Audio.Core.Buffers;

namespace EGGtCSPlatform.SpeechTraining;

public static class SpeechWaveform
{
    /// <summary>Peak-preserving buckets and absolute peak envelope, normalized to PCM full scale.</summary>
    public static IReadOnlyList<SpeechWaveformBucket> GetWaveform(
        this AudioBuffer audio,
        int maximumBuckets = 1200
    )
    {
        var Data = audio.Data;
        var Channels = audio.Channels;
        var SampleRate = audio.SampleRate;
        if (maximumBuckets <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBuckets));
        var frames = Data.Length / (Channels * 2);
        var stride = Math.Max(1, (int)Math.Ceiling((double)frames / maximumBuckets));
        var points = new List<SpeechWaveformBucket>();
        for (var start = 0; start < frames; start += stride)
        {
            double min = 1,
                max = -1;
            for (var i = start; i < Math.Min(start + stride, frames); i++)
            {
                double sample = 0;
                for (var channel = 0; channel < Channels; channel++)
                    sample +=
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                            Data.Slice((i * Channels + channel) * 2, 2)
                        ) / 32768d;
                sample /= Channels;
                min = Math.Min(min, sample);
                max = Math.Max(max, sample);
            }
            points.Add(
                new((double)start / SampleRate, min, max, Math.Max(Math.Abs(min), Math.Abs(max)))
            );
        }
        return points;
    }
}

public sealed record SpeechWaveformBucket(
    double Seconds,
    double Minimum,
    double Maximum,
    double Envelope
);
