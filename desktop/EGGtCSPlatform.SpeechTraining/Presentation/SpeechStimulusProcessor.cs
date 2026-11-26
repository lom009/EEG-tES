namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Preview nodes and curves; normalized for standalone use, milliamps for a device plan.</summary>
public sealed record SpeechStimulusPreview(
    IReadOnlyList<double> Amplitudes,
    IReadOnlyList<SpeechStimulusPoint> Waveform,
    IReadOnlyList<SpeechStimulusPoint> Upper,
    IReadOnlyList<SpeechStimulusPoint> Lower
)
{
    public static SpeechStimulusPreview Empty { get; } = new([], [], [], []);
}

public readonly record struct SpeechStimulusPoint(double Seconds, double Value);

/// <summary>
/// Research map_to_tacs -> process_wave_data -> map_to_tacs supplies the amplitudes.
/// PCHIP reconstructs the envelope; standalone previews retain their historical 200 Hz carrier.
/// </summary>
public static class SpeechStimulusProcessor
{
    public static SpeechStimulusPreview Prepare(
        double[] envelope,
        int sampleRate,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        token.ThrowIfCancellationRequested();
        if (envelope.Length == 0)
            return SpeechStimulusPreview.Empty;

        var mapped = Normalize(envelope, token);
        var stride = sampleRate / 100;
        var averages = new double[(envelope.Length + stride - 1) / stride];
        for (var window = 0; window < averages.Length; window++)
        {
            token.ThrowIfCancellationRequested();
            var start = window * stride;
            var end = Math.Min(start + stride, mapped.Length);
            var sum = 0d;
            for (var i = start; i < end; i++)
                sum += mapped[i];
            averages[window] = sum / (end - start);
        }
        var amplitudes = Normalize(averages, token);
        var duration = (double)envelope.Length / sampleRate;
        return BuildPreview(amplitudes, duration, token);
    }

    internal static SpeechStimulusPreview BuildPreview(
        double[] amplitudes,
        double duration,
        CancellationToken token = default,
        int frequencyHz = 200,
        bool preserveZeroIntervals = false
    )
    {
        if (amplitudes.Length == 0)
            return SpeechStimulusPreview.Empty;
        if (preserveZeroIntervals)
            return BuildQuantizedPreview(amplitudes, duration, frequencyHz, token);
        var slopes = PchipSlopes(amplitudes, token);
        var plotRate = checked(frequencyHz * 16);
        var lastRegular = checked((int)Math.Floor(duration * plotRate));
        var count = lastRegular + (lastRegular / (double)plotRate < duration ? 1 : 0);
        var waveform = new SpeechStimulusPoint[count + 1];
        var upper = new SpeechStimulusPoint[count + 1];
        var lower = new SpeechStimulusPoint[count + 1];
        for (var i = 0; i <= count; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            var t = i == count ? duration : i / (double)plotRate;
            var a = Interpolate(amplitudes, slopes, t * 100);
            waveform[i] = new(t, a * Math.Sin(2 * Math.PI * frequencyHz * t));
            upper[i] = new(t, a);
            lower[i] = new(t, -a);
        }
        return new(
            Array.AsReadOnly(amplitudes),
            Array.AsReadOnly(waveform),
            Array.AsReadOnly(upper),
            Array.AsReadOnly(lower)
        );
    }

    private static SpeechStimulusPreview BuildQuantizedPreview(
        double[] currents,
        double duration,
        int frequency,
        CancellationToken token
    )
    {
        var rate = checked(frequency * 16);
        var slopes = PchipSlopes(currents, token);
        var wave = new List<SpeechStimulusPoint>();
        var upper = new List<SpeechStimulusPoint>();
        var lower = new List<SpeechStimulusPoint>();
        void Add(double t, double amplitude)
        {
            wave.Add(new(t, amplitude * Math.Sin(2 * Math.PI * frequency * t)));
            upper.Add(new(t, amplitude));
            lower.Add(new(t, -amplitude));
        }
        // Explicit left/right limits keep zero intervals and the terminal edge vertical.
        for (var node = 0; node < currents.Length; node++)
        {
            token.ThrowIfCancellationRequested();
            var start = node / 100d;
            var end = Math.Min(duration, (node + 1) / 100d);
            if (node > 0 && (currents[node - 1] == 0 || currents[node] == 0))
                Add(start, currents[node - 1]);
            Add(start, currents[node]);
            var first = (int)Math.Floor(start * rate + 1e-8) + 1;
            for (var sample = first; sample / (double)rate < end - 1e-12; sample++)
            {
                var t = sample / (double)rate;
                var amplitude =
                    currents[node] == 0 ? 0
                    : node + 1 < currents.Length && currents[node + 1] == 0 ? currents[node]
                    : Interpolate(currents, slopes, t * 100);
                Add(t, amplitude);
            }
        }
        Add(duration, currents[^1]);
        Add(duration, 0);
        return new(
            Array.AsReadOnly(currents),
            wave.AsReadOnly(),
            upper.AsReadOnly(),
            lower.AsReadOnly()
        );
    }

    // PCHIP derivatives expressed per node interval (all nodes are exactly 10 ms apart).
    // Equal spacing reduces the weighted harmonic mean to the ordinary harmonic mean.
    internal static double[] PchipSlopes(double[] values, CancellationToken token)
    {
        var slopes = new double[values.Length];
        if (values.Length < 2)
            return slopes;
        var differences = new double[values.Length - 1];
        for (var i = 0; i < differences.Length; i++)
            differences[i] = values[i + 1] - values[i];
        if (values.Length == 2)
        {
            slopes[0] = slopes[1] = differences[0];
            return slopes;
        }
        for (var i = 1; i < slopes.Length - 1; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            var before = differences[i - 1];
            var after = differences[i];
            if (before != 0 && after != 0 && Math.Sign(before) == Math.Sign(after))
                slopes[i] = 2 / (1 / before + 1 / after);
        }
        slopes[0] = EndpointSlope(differences[0], differences[1]);
        slopes[^1] = EndpointSlope(differences[^1], differences[^2]);
        return slopes;
    }

    private static double EndpointSlope(double first, double next)
    {
        var slope = (3 * first - next) / 2;
        if (Math.Sign(slope) != Math.Sign(first))
            return 0;
        if (Math.Sign(first) != Math.Sign(next) && Math.Abs(slope) > 3 * Math.Abs(first))
            return 3 * first;
        return slope;
    }

    internal static double Interpolate(double[] values, double[] slopes, double position)
    {
        if (position >= values.Length - 1)
            return values[^1];
        var i = (int)position;
        var t = position - i;
        var t2 = t * t;
        var t3 = t2 * t;
        var result =
            (2 * t3 - 3 * t2 + 1) * values[i]
            + (t3 - 2 * t2 + t) * slopes[i]
            + (-2 * t3 + 3 * t2) * values[i + 1]
            + (t3 - t2) * slopes[i + 1];
        // Guard only roundoff at the interval bounds; PCHIP itself preserves the shape.
        return Math.Clamp(
            result,
            Math.Min(values[i], values[i + 1]),
            Math.Max(values[i], values[i + 1])
        );
    }

    private static double[] Normalize(double[] input, CancellationToken token)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        for (var i = 0; i < input.Length; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            var value = input[i];
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentException("刺激预览包络必须是有限非负数。", nameof(input));
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
        var result = new double[input.Length];
        var range = max - min;
        if (range <= 1e-12 * Math.Max(1, Math.Max(Math.Abs(min), Math.Abs(max))))
            return result;
        for (var i = 0; i < input.Length; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            result[i] = (input[i] - min) / range;
        }
        return result;
    }
}
