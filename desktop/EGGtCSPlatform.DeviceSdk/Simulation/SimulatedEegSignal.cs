namespace EGGtCSPlatform.DeviceSdk.Simulation;

/// <summary>
/// Deterministic, random-access resting EEG in microvolts (signal-v3).
/// This is a phenomenological mixture, not an anatomical or treatment-response model.
/// No mutable state or duration-dependent buffers are needed to seek or change block size.
/// </summary>
public static class SimulatedEegSignal
{
    public const string AlgorithmVersion = "signal-v3";
    public const int DefaultSeed = 42;
    public const int DefaultSubject = 1;

    public static double Sample(
        int seed,
        int subject,
        int physicalChannel,
        long sampleIndex,
        int sampleRateHz,
        double amplitudeMicrovolts = 25,
        double noiseMicrovolts = 5,
        double rhythmHz = 10
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        var time = sampleIndex / (double)sampleRateHz;
        var common = new Source(seed, subject, 0);
        var local = new Source(seed, subject, physicalChannel);
        var variation = local.Uniform(1, 0);
        var exponent = 1.2 + 0.2 * common.Uniform(2, 0);
        var frequency = Math.Max(0.5, rhythmHz + 0.6 * common.Uniform(3, 0));

        var background =
            0.55 * Background(common, time, sampleRateHz, exponent)
            + 0.80 * Background(local, time, sampleRateHz, exponent);
        var rhythms =
            0.65 * Rhythm(common, 100, frequency, time, sampleRateHz)
            + 0.50 * Rhythm(local, 110, frequency + 0.25 * variation, time, sampleRateHz)
            + 0.16 * Rhythm(common, 120, 5.7 + 0.4 * variation, time, sampleRateHz)
            + 0.10 * Rhythm(local, 130, 2.1, time, sampleRateHz)
            + 0.14 * Rhythm(local, 140, 19 + 2 * variation, time, sampleRateHz)
            + 0.05 * Rhythm(local, 150, 36 + 3 * variation, time, sampleRateHz);
        var activity =
            amplitudeMicrovolts * (1 + 0.2 * variation) * (0.80 * background + 0.65 * rhythms);

        // Acquisition contamination has its own scale, separate from neural activity.
        if (noiseMicrovolts == 0)
            return activity;
        var drift = 0.30 * local.Smooth(200, time * 0.12);
        var measurement = 0.35 * local.Gaussian(210, sampleIndex);
        var mains =
            0.12
            * BandGain(51, sampleRateHz)
            * (1 + 0.2 * common.Smooth(220, time * 0.15))
            * Math.Sin(2 * Math.PI * 50 * time + 0.2 * variation);
        var blink = (3 + variation) * Events(common, 230, time, 6, 0.18, 0.38);
        var muscle =
            Events(local, 240, time, 9, 0.25, 0.9)
            * (
                0.55 * BandNoise(local, 250, 32, time, sampleRateHz)
                + 0.35 * BandNoise(local, 260, 64, time, sampleRateHz)
            );
        return activity + noiseMicrovolts * (drift + measurement + mains + blink + muscle);
    }

    private static double Background(Source source, double time, int rate, double exponent)
    {
        double value = 0,
            energy = 0;
        for (var octave = 0; octave < 8; octave++)
        {
            var frequency = 0.5 * (1 << octave);
            // Equal energy per octave gives a 1/f-like spectrum; modest subject variation
            // changes its slope. Quadrature noise broadens each octave instead of lines.
            var weight = Math.Pow(frequency, (1 - exponent) / 2);
            value += weight * BandNoise(source, 10 + octave * 4, frequency, time, rate);
            energy += weight * weight;
        }
        return value / Math.Sqrt(energy);
    }

    private static double BandNoise(Source source, int key, double frequency, double time, int rate)
    {
        // Leave room for the random envelope's sidebands. Quintic interpolation has
        // continuous first/second derivatives; its small spectral tails are not a brick-wall filter.
        var gain = BandGain(frequency * 1.8, rate);
        if (gain == 0)
            return 0;
        var angle = 2 * Math.PI * frequency * time;
        var (sin, cos) = Math.SinCos(angle);
        return gain
            * (
                source.Smooth(key, time * frequency) * cos
                + source.Smooth(key + 1, time * frequency) * sin
            );
    }

    private static double Rhythm(Source source, int key, double frequency, double time, int rate)
    {
        var gain = BandGain(frequency + 2, rate);
        if (frequency <= 0 || gain == 0)
            return 0;
        var envelope = 0.65 + 0.45 * Math.Tanh(source.Smooth(key, time * 0.45));
        var phase =
            Math.PI * source.Uniform(key + 1, 0) + 1.2 * source.Smooth(key + 2, time * 0.22);
        return gain * envelope * Math.Sin(2 * Math.PI * frequency * time + phase);
    }

    private static double BandGain(double upperFrequency, int rate)
    {
        // Fade out before Nyquist rather than folding an out-of-band oscillator down.
        var position = Math.Clamp((upperFrequency / rate - 0.30) / 0.15, 0, 1);
        return 1 - Fade(position);
    }

    private static double Events(
        Source source,
        int key,
        double time,
        double interval,
        double minimumWidth,
        double maximumWidth
    )
    {
        var block = (long)Math.Floor(time / interval);
        double value = 0;
        for (var index = block - 1; index <= block + 1; index++)
        {
            if (source.Uniform(key, index) < -0.1)
                continue;
            var centre = (index + 0.5 + 0.48 * source.Uniform(key + 1, index)) * interval;
            var width =
                minimumWidth
                + (maximumWidth - minimumWidth) * (1 + source.Uniform(key + 2, index)) / 2;
            var distance = (time - centre) / width;
            if (Math.Abs(distance) >= 1)
                continue;
            // Compact pulse with zero value, slope and curvature at both edges. Including
            // neighbours keeps pulses whole even when their support crosses an event block.
            var bell = 1 - distance * distance;
            value += (1 + 0.5 * source.Uniform(key + 3, index)) * bell * bell * bell;
        }
        return value;
    }

    private static double Fade(double x) => x * x * x * (x * (x * 6 - 15) + 10);

    private readonly struct Source(int seed, int subject, int channel)
    {
        private readonly ulong _key =
            Mix((uint)seed ^ 0xA0761D6478BD642FUL)
            ^ Mix((uint)subject ^ 0xE7037ED1A0B428DBUL)
            ^ Mix((uint)channel ^ 0x8EBC6AF09C88C6E3UL);

        public double Uniform(int stream, long index)
        {
            var bits = Mix(
                _key
                    ^ Mix(unchecked((ulong)index) ^ 0x589965CC75374CC3UL)
                    ^ Mix((uint)stream ^ 0x1D8E4E27C47D124FUL)
            );
            return (bits >> 11) * (1d / (1UL << 53)) * 2 - 1;
        }

        public double Gaussian(int stream, long index)
        {
            var u = Math.Max(1e-12, (Uniform(stream, index) + 1) / 2);
            var v = (Uniform(stream + 10000, index) + 1) / 2;
            return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
        }

        public double Smooth(int stream, double position)
        {
            var index = (long)Math.Floor(position);
            var fraction = Fade(position - index);
            return Gaussian(stream, index) * (1 - fraction)
                + Gaussian(stream, index + 1) * fraction;
        }

        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
