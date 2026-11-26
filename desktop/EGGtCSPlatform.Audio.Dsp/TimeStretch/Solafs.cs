using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.TimeStretch;

public static class Solafs
{
    public static AudioBuffer Stretch(
        AudioBuffer input,
        int targetFrames,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetFrames);
        token.ThrowIfCancellationRequested();
        var channels = input.Channels;
        var sourceFrames = input.Data.Length / (2 * channels);
        if (sourceFrames == 0)
            return input;
        if (sourceFrames == targetFrames)
            return input;
        var source = new double[sourceFrames * channels];
        for (var i = 0; i < source.Length; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            source[i] = BinaryPrimitives.ReadInt16LittleEndian(input.Data.Slice(i * 2, 2));
        }
        var output = new double[checked(targetFrames * channels)];
        var window = Math.Min(
            Math.Min(sourceFrames, targetFrames),
            Math.Max(2, (int)Math.Round(200d * input.SampleRate / 16000))
        );
        if (window < 2)
        {
            for (var i = 0; i < output.Length; i++)
                output[i] = source[i % channels];
        }
        else
        {
            var overlap = window / 2;
            var hop = window - overlap;
            var maximumSkew = Math.Max(1, (int)Math.Round(400d * input.SampleRate / 16000));
            var sampleStep = Math.Max(1, (int)Math.Round(8d * input.SampleRate / 16000));
            var searchStep = Math.Max(1, (int)Math.Round(2d * input.SampleRate / 16000));
            var factor = (double)sourceFrames / targetFrames;
            Array.Copy(source, output, window * channels);
            var written = window;
            var previousSource = 0;
            var previousOutput = 0;
            var finalPosition = targetFrames - window;
            for (
                var position = Math.Min(hop, finalPosition);
                position > previousOutput;
                position = Math.Min(position + hop, finalPosition)
            )
            {
                token.ThrowIfCancellationRequested();
                var blend = written - position;
                var ideal = Math.Clamp(
                    (int)Math.Round(position * factor),
                    0,
                    sourceFrames - window
                );
                var end = Math.Min(sourceFrames - window, ideal + maximumSkew);
                var selected = ideal;
                if (position == finalPosition)
                    selected = sourceFrames - window;
                else
                {
                    // Predict continuation from the last actual source/output positions.
                    var prediction = previousSource + position - previousOutput;
                    if (prediction >= ideal && prediction <= end)
                        selected = prediction;
                    else
                    {
                        var best = double.NegativeInfinity;
                        for (var candidate = ideal; candidate <= end; candidate += searchStep)
                        {
                            double dot = 0,
                                energy = 0;
                            for (var j = 0; j < blend; j += sampleStep)
                            for (var c = 0; c < channels; c++)
                            {
                                var x = source[(candidate + j) * channels + c];
                                dot += x * output[(position + j) * channels + c];
                                energy += x * x;
                            }
                            var similarity = energy > 0 ? dot / Math.Sqrt(energy) : 0;
                            if (similarity > best)
                            {
                                best = similarity;
                                selected = candidate;
                            }
                        }
                    }
                }
                for (var j = 0; j < window; j++)
                for (var c = 0; c < channels; c++)
                {
                    var x = source[(selected + j) * channels + c];
                    var index = (position + j) * channels + c;
                    var weight = (j + 1d) / (blend + 1);
                    output[index] = j < blend ? output[index] * (1 - weight) + x * weight : x;
                }
                previousSource = selected;
                previousOutput = position;
                written = position + window;
            }
        }
        var bytes = new byte[checked(output.Length * 2)];
        for (var i = 0; i < output.Length; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(i * 2, 2),
                (short)Math.Clamp(Math.Round(output[i]), short.MinValue, short.MaxValue)
            );
        }
        return new(input.SampleRate, channels, bytes);
    }
}
