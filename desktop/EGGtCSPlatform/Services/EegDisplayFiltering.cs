using System;
using System.Collections.Generic;

namespace EGGtCSPlatform.Services;

public sealed record EegFilterOption(string Label, double? FrequencyHz)
{
    public override string ToString() => Label;
}

public sealed record EegDisplayFilterSettings(
    double? HighPassHz,
    double? LowPassHz,
    double? NotchHz
);

public interface IEegDisplayFilter
{
    EegDisplayFilterSettings Settings { get; }

    void Reset();

    double Process(double sample);

    IReadOnlyList<double> Process(IReadOnlyList<double> samples);
}

public sealed class CausalEegDisplayFilter : IEegDisplayFilter
{
    private const double ButterworthQ = 0.7071067811865476d;
    private const double NotchQ = 30d;
    private readonly Biquad? _highPass;
    private readonly Biquad? _notch;
    private readonly Biquad? _lowPass;

    public CausalEegDisplayFilter(int sampleRateHz, EegDisplayFilterSettings settings)
    {
        if (sampleRateHz is not (250 or 500))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        ArgumentNullException.ThrowIfNull(settings);
        ValidateFrequency(settings.HighPassHz, sampleRateHz, nameof(settings.HighPassHz));
        ValidateFrequency(settings.LowPassHz, sampleRateHz, nameof(settings.LowPassHz));
        ValidateFrequency(settings.NotchHz, sampleRateHz, nameof(settings.NotchHz));
        if (settings.HighPassHz is { } high && settings.LowPassHz is { } low && high >= low)
            throw new ArgumentException(
                "High-pass frequency must be below low-pass frequency.",
                nameof(settings)
            );

        Settings = settings;
        if (settings.HighPassHz is { } highPass)
            _highPass = Biquad.CreateHighPass(sampleRateHz, highPass, ButterworthQ);
        if (settings.NotchHz is { } notch)
            _notch = Biquad.CreateNotch(sampleRateHz, notch, NotchQ);
        if (settings.LowPassHz is { } lowPass)
            _lowPass = Biquad.CreateLowPass(sampleRateHz, lowPass, ButterworthQ);
    }

    public EegDisplayFilterSettings Settings { get; }

    public void Reset()
    {
        _highPass?.Reset();
        _notch?.Reset();
        _lowPass?.Reset();
    }

    public double Process(double sample)
    {
        var value = sample;
        if (_highPass is not null)
            value = _highPass.Process(value);
        if (_notch is not null)
            value = _notch.Process(value);
        if (_lowPass is not null)
            value = _lowPass.Process(value);
        return value;
    }

    public IReadOnlyList<double> Process(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (_highPass is null && _notch is null && _lowPass is null)
            return samples;
        var result = new double[samples.Count];
        for (var index = 0; index < samples.Count; index++)
            result[index] = Process(samples[index]);
        return result;
    }

    private static void ValidateFrequency(double? frequency, int sampleRateHz, string parameterName)
    {
        if (frequency is not { } value)
            return;
        if (!double.IsFinite(value) || value <= 0d || value >= sampleRateHz / 2d)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;

        public static Biquad CreateLowPass(double sampleRate, double frequency, double q) =>
            Create(sampleRate, frequency, q, BiquadKind.LowPass);

        public static Biquad CreateHighPass(double sampleRate, double frequency, double q) =>
            Create(sampleRate, frequency, q, BiquadKind.HighPass);

        public static Biquad CreateNotch(double sampleRate, double frequency, double q) =>
            Create(sampleRate, frequency, q, BiquadKind.Notch);

        public double Process(double sample)
        {
            var output = b0 * sample + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1;
            _x1 = sample;
            _y2 = _y1;
            _y1 = output;
            return output;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0d;

        private static Biquad Create(double sampleRate, double frequency, double q, BiquadKind kind)
        {
            var omega = 2d * Math.PI * frequency / sampleRate;
            var cosine = Math.Cos(omega);
            var sine = Math.Sin(omega);
            var alpha = sine / (2d * q);
            double rawB0;
            double rawB1;
            double rawB2;
            if (kind == BiquadKind.LowPass)
            {
                rawB0 = (1d - cosine) / 2d;
                rawB1 = 1d - cosine;
                rawB2 = rawB0;
            }
            else if (kind == BiquadKind.HighPass)
            {
                rawB0 = (1d + cosine) / 2d;
                rawB1 = -(1d + cosine);
                rawB2 = rawB0;
            }
            else
            {
                rawB0 = 1d;
                rawB1 = -2d * cosine;
                rawB2 = 1d;
            }
            var a0 = 1d + alpha;
            return new Biquad(
                rawB0 / a0,
                rawB1 / a0,
                rawB2 / a0,
                -2d * cosine / a0,
                (1d - alpha) / a0
            );
        }

        private enum BiquadKind
        {
            LowPass,
            HighPass,
            Notch,
        }
    }
}
