namespace EGGtCSPlatform.Audio.Dsp.Filtering;

internal static class ButterworthFilter
{
    // Internal for numerical comparison with the research project's pure SciPy functions.
    public static double[] Apply(
        double[] samples,
        int sampleRate,
        CancellationToken token = default,
        double cutoffHz = 150,
        int order = 5
    )
    {
        Validate(sampleRate, cutoffHz, order);
        token.ThrowIfCancellationRequested();
        if (samples.Length < 2)
            return (double[])samples.Clone();
        // scipy.signal.filtfilt(b, a): odd extension, 3 * max(len(a), len(b)) = 18.
        var edge = Math.Min(3 * (order + 1), samples.Length - 1);
        var extended = new double[samples.Length + 2 * edge];
        for (var i = 0; i < edge; i++)
        {
            extended[i] = 2 * samples[0] - samples[edge - i];
            extended[edge + samples.Length + i] = 2 * samples[^1] - samples[samples.Length - 2 - i];
        }
        samples.CopyTo(extended, edge);
        var sections = DesignSections(sampleRate, cutoffHz, order);
        FilterForward(extended, sections, token);
        Array.Reverse(extended);
        FilterForward(extended, sections, token);
        Array.Reverse(extended);
        return extended.AsSpan(edge, samples.Length).ToArray();
    }

    private readonly record struct Section(double B0, double B1, double B2, double A1, double A2);

    public static void Validate(int sampleRate, double cutoffHz, int order)
    {
        if (
            !double.IsFinite(cutoffHz)
            || cutoffHz <= 0
            || cutoffHz >= sampleRate / 2d
            || order is < 1 or > 12
        )
            throw new ArgumentOutOfRangeException(
                nameof(cutoffHz),
                "Invalid Butterworth cutoff/order."
            );
    }

    private static Section[] DesignSections(int sampleRate, double cutoffHz, int order)
    {
        var c = Math.Tan(Math.PI * cutoffHz / sampleRate);
        var sections = new Section[(order + 1) / 2];
        int offset = order % 2;
        if (offset == 1)
            sections[0] = new(c / (1 + c), c / (1 + c), 0, (c - 1) / (c + 1), 0);
        for (var k = 1; k <= order / 2; k++)
        {
            // For odd orders this is the original formula and section ordering (including N=5).
            var damping =
                order % 2 == 1
                    ? 2 * Math.Cos(k * Math.PI / order)
                    : 2 * Math.Cos((2 * k - 1) * Math.PI / (2 * order));
            var denominator = 1 + damping * c + c * c;
            var b = c * c / denominator;
            sections[k - 1 + offset] = new(
                b,
                2 * b,
                b,
                2 * (c * c - 1) / denominator,
                (1 - damping * c + c * c) / denominator
            );
        }
        return sections;
    }

    private static void FilterForward(double[] data, Section[] sections, CancellationToken token)
    {
        foreach (var s in sections)
        {
            // Steady-state zi scaled by the endpoint (equivalent to lfilter_zi).
            var z1 = (1 - s.B0) * data[0];
            var z2 = (s.B2 - s.A2) * data[0];
            for (var i = 0; i < data.Length; i++)
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                var x = data[i];
                var y = s.B0 * x + z1;
                z1 = s.B1 * x - s.A1 * y + z2;
                z2 = s.B2 * x - s.A2 * y;
                data[i] = y;
            }
        }
    }
}
