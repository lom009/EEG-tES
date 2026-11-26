using System.Numerics;

namespace EGGtCSPlatform.Audio.Core.Buffers;

public enum SignalSampleType
{
    Pcm16,
    Float64,
    Complex128,
}

/// <summary>Format of immutable, interleaved sampled data.</summary>
public interface ISignalBuffer
{
    int SampleRate { get; }
    int Channels { get; }
    int Frames { get; }
    SignalSampleType SampleType { get; }
}

public sealed class FloatBuffer : ISignalBuffer
{
    private readonly double[] _data;
    public int SampleRate { get; }
    public int Channels { get; }
    public int Frames => _data.Length / Channels;
    public SignalSampleType SampleType => SignalSampleType.Float64;
    public ReadOnlySpan<double> Data => _data;

    public FloatBuffer(int sampleRate, int channels, ReadOnlySpan<double> data)
    {
        SignalBufferValidation.Validate(sampleRate, channels, data.Length);
        foreach (var value in data)
            if (!double.IsFinite(value))
                throw new ArgumentException("Signal samples must be finite.", nameof(data));
        (SampleRate, Channels, _data) = (sampleRate, channels, data.ToArray());
    }

    public double[] ToArray() => (double[])_data.Clone();
}

public sealed class ComplexBuffer : ISignalBuffer
{
    private readonly Complex[] _data;
    public int SampleRate { get; }
    public int Channels { get; }
    public int Frames => _data.Length / Channels;
    public SignalSampleType SampleType => SignalSampleType.Complex128;
    public ReadOnlySpan<Complex> Data => _data;

    public ComplexBuffer(int sampleRate, int channels, ReadOnlySpan<Complex> data)
    {
        SignalBufferValidation.Validate(sampleRate, channels, data.Length);
        foreach (var value in data)
            if (!double.IsFinite(value.Real) || !double.IsFinite(value.Imaginary))
                throw new ArgumentException("Signal samples must be finite.", nameof(data));
        (SampleRate, Channels, _data) = (sampleRate, channels, data.ToArray());
    }

    public Complex[] ToArray() => (Complex[])_data.Clone();
}

internal static class SignalBufferValidation
{
    public static void Validate(int sampleRate, int channels, int samples)
    {
        if (sampleRate is < 8000 or > 192000 || channels is < 1 or > 2 || samples % channels != 0)
            throw new ArgumentException("Invalid sampled signal format.");
    }
}
