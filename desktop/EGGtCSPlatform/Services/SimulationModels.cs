using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed record EnvelopeParameters(
    double ModulationHz = 4,
    double Depth = 0.8,
    double DelayMilliseconds = 0,
    bool IsSham = false,
    bool IsDeviceSetup = false
);

public sealed record SimulationProvenance(
    Guid BatchId,
    int Seed,
    int SubjectIndex,
    string Group,
    DateTimeOffset GeneratedAtUtc,
    string AlgorithmVersion = SimulatedEegSignal.AlgorithmVersion,
    double EegAmplitudeMicrovolts = 25,
    double NoiseMicrovolts = 5,
    double RhythmHz = 10
);

public sealed record RecordIntervalRange(double MinimumMinutes, double MaximumMinutes);

[Flags]
public enum GenerationWeekdays
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend,
}

public sealed record DailyGenerationTimeRange(TimeOnly Start, TimeOnly End);

public sealed record DailyGenerationSchedule
{
    public DailyGenerationSchedule(
        IReadOnlyList<DailyGenerationTimeRange> timeRanges,
        GenerationWeekdays allowedWeekdays
    )
    {
        ArgumentNullException.ThrowIfNull(timeRanges);
        if (timeRanges.Count == 0)
            throw new ArgumentException("请至少配置一个每日允许时段。", nameof(timeRanges));

        var ordered = timeRanges.OrderBy(x => x.Start).ThenBy(x => x.End).ToArray();
        if (ordered.Any(x => x.End <= x.Start))
            throw new ArgumentException(
                "每日允许时段的结束时间必须晚于开始时间，且不支持跨午夜时段。",
                nameof(timeRanges)
            );

        var normalized = new List<DailyGenerationTimeRange>(ordered.Length);
        foreach (var range in ordered)
        {
            if (normalized.Count == 0 || range.Start > normalized[^1].End)
            {
                normalized.Add(range);
                continue;
            }

            var previous = normalized[^1];
            if (range.End > previous.End)
                normalized[^1] = previous with { End = range.End };
        }

        TimeRanges = normalized.AsReadOnly();
        AllowedWeekdays = allowedWeekdays;
    }

    public DailyGenerationSchedule(TimeOnly start, TimeOnly end, GenerationWeekdays allowedWeekdays)
        : this([new DailyGenerationTimeRange(start, end)], allowedWeekdays) { }

    public IReadOnlyList<DailyGenerationTimeRange> TimeRanges { get; }
    public GenerationWeekdays AllowedWeekdays { get; }

    public static DailyGenerationSchedule Default { get; } =
        new(
            [new DailyGenerationTimeRange(new TimeOnly(9, 30), new TimeOnly(18, 30))],
            GenerationWeekdays.Weekdays
        );

    public bool Allows(DayOfWeek day) =>
        (
            AllowedWeekdays
            & (
                day switch
                {
                    DayOfWeek.Monday => GenerationWeekdays.Monday,
                    DayOfWeek.Tuesday => GenerationWeekdays.Tuesday,
                    DayOfWeek.Wednesday => GenerationWeekdays.Wednesday,
                    DayOfWeek.Thursday => GenerationWeekdays.Thursday,
                    DayOfWeek.Friday => GenerationWeekdays.Friday,
                    DayOfWeek.Saturday => GenerationWeekdays.Saturday,
                    DayOfWeek.Sunday => GenerationWeekdays.Sunday,
                    _ => GenerationWeekdays.None,
                }
            )
        ) != 0;
}

public sealed record SimulationRequest(
    ExperimentConfigurationTemplate Template,
    int Count,
    string SubjectPrefix,
    DateTimeOffset StartedAt,
    double IntervalMinutes,
    int Seed,
    bool Randomized32 = false,
    double EegAmplitudeMicrovolts = 25,
    double NoiseMicrovolts = 5,
    double RhythmHz = 10,
    IReadOnlyList<EegPhysicalChannelMapping>? PhysicalChannels = null,
    RecordIntervalRange? IntervalRange = null,
    DailyGenerationSchedule? GenerationSchedule = null
)
{
    public DailyGenerationSchedule EffectiveGenerationSchedule =>
        GenerationSchedule ?? DailyGenerationSchedule.Default;
}

public sealed record SimulationProgress(int Completed, int Total, string Message);

public sealed record SimulationResult(
    Guid BatchId,
    IReadOnlyList<Guid> RunIds,
    bool Canceled,
    string? Error
);

public static class SimulationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string? value)
        where T : class => value is null ? null : JsonSerializer.Deserialize<T>(value, Options);
}

// Versioned, stateless noise keeps results identical across block sizes, seeking and restarts.
public static class SimulationSignal
{
    public static double Noise(int seed, int subject, int channel, long sample)
    {
        unchecked
        {
            ulong x =
                (ulong)sample
                ^ ((ulong)(uint)seed << 32)
                ^ (ulong)(subject + 1) * 0x9E3779B97F4A7C15UL
                ^ (ulong)(channel + 1) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return ((x ^ (x >> 31)) >> 11) * (1d / (1UL << 53)) * 2 - 1;
        }
    }

    public static bool[] Allocate(int seed)
    {
        var order = Enumerable.Range(0, 32).OrderBy(i => Noise(seed, 0, 0, i)).ToArray();
        var groups = new bool[32];
        foreach (var index in order.Take(16))
            groups[index] = true;
        return groups;
    }

    public static double Eeg(SimulationProvenance source, int channel, long sample, int rate)
    {
        if (rate <= 0)
            throw new ArgumentOutOfRangeException(nameof(rate));
        if (source.AlgorithmVersion == SimulatedEegSignal.AlgorithmVersion)
            return SimulatedEegSignal.Sample(
                source.Seed,
                source.SubjectIndex,
                channel,
                sample,
                rate,
                source.EegAmplitudeMicrovolts,
                source.NoiseMicrovolts,
                source.RhythmHz
            );
        var t = sample / (double)rate;
        var variation = Noise(source.Seed, source.SubjectIndex, channel, -1);
        if (source.AlgorithmVersion is "signal-v1" or "synthetic-v1")
        {
            var phase = variation * Math.PI;
            return source.EegAmplitudeMicrovolts
                    * (1 + variation * 0.15)
                    * (
                        Math.Sin(2 * Math.PI * source.RhythmHz * t + phase)
                        + 0.2 * Math.Sin(2 * Math.PI * 6 * t + phase * 2)
                    )
                + source.NoiseMicrovolts * Noise(source.Seed, source.SubjectIndex, channel, sample);
        }
        if (source.AlgorithmVersion != "signal-v2")
            throw new ArgumentException("不支持的信号算法版本。", nameof(source));

        // Physical time and independently keyed sources make seeking and chunking invariant.
        // Equal energy per octave approximates a 1/f background; these are phenomenological
        // mixtures, not an anatomical forward model or an assumed treatment response.
        var background =
            0.55 * Colored(source, 0, t, rate) + 0.80 * Colored(source, channel + 100, t, rate);
        var subjectShift = 0.6 * Noise(source.Seed, source.SubjectIndex, 0, -10);
        var rhythm = Math.Clamp(source.RhythmHz + subjectShift, 0.5, rate * 0.4);
        var oscillations =
            0.65
                * (
                    0.65 * Rhythm(source, 10, rhythm, t, rate)
                    + 0.50 * Rhythm(source, channel + 200, rhythm + variation * 0.25, t, rate)
                )
            + 0.16 * Rhythm(source, 20, 5.7 + subjectShift, t, rate)
            + 0.10 * Rhythm(source, channel + 300, 2.1, t, rate)
            + 0.14 * Rhythm(source, channel + 400, 19 + 2 * variation, t, rate)
            + 0.05 * Rhythm(source, channel + 500, 36 + 3 * variation, t, rate);
        var drift = 0.08 * Smooth(source, channel + 600, t * 0.12);
        var activity =
            source.EegAmplitudeMicrovolts
            * (1 + variation * 0.2)
            * (0.80 * background + oscillations + drift);

        // Noise controls measurement contamination separately from the neural background.
        var measurement = 0.35 * Gaussian(source, channel + 700, sample);
        var mains = rate > 125 ? 0.12 * Math.Sin(2 * Math.PI * 50 * t + variation) : 0;
        var blink = Transient(source, 800, t, 8, 0.14) * (3 + variation);
        var muscle =
            Transient(source, channel + 900, t, 13, 0.35)
            * 0.6
            * Gaussian(source, channel + 1000, sample);
        return activity + source.NoiseMicrovolts * (measurement + mains + blink + muscle);
    }

    private static double Gaussian(SimulationProvenance source, int key, long index)
    {
        var u = Math.Max(1e-12, (Noise(source.Seed, source.SubjectIndex, key, index) + 1) / 2);
        var v = (Noise(source.Seed ^ 0x51ed270b, source.SubjectIndex, key, index) + 1) / 2;
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }

    private static double Smooth(SimulationProvenance source, int key, double position)
    {
        var index = (long)Math.Floor(position);
        var fraction = position - index;
        var weight = fraction * fraction * (3 - 2 * fraction);
        return Gaussian(source, key, index) * (1 - weight)
            + Gaussian(source, key, index + 1) * weight;
    }

    private static double Colored(SimulationProvenance source, int key, double time, int rate)
    {
        double value = 0,
            energy = 0;
        var exponent = 1.2 + 0.2 * Noise(source.Seed, source.SubjectIndex, 0, -20);
        var octave = 0;
        for (var frequency = 0.5; frequency <= Math.Min(64, rate * 0.2); frequency *= 2)
        {
            var weight = Math.Pow(frequency, (1 - exponent) / 2);
            value += weight * Smooth(source, key * 31 + octave++, time * frequency);
            energy += weight * weight;
        }
        return energy > 0 ? value / Math.Sqrt(energy) : 0;
    }

    private static double Rhythm(
        SimulationProvenance source,
        int key,
        double frequency,
        double time,
        int rate
    )
    {
        if (frequency <= 0 || frequency >= rate * 0.45)
            return 0;
        var phase = Math.PI * Noise(source.Seed, source.SubjectIndex, key, -2);
        var gain = 0.65 + 0.30 * Math.Tanh(Smooth(source, key + 2000, time * 0.4));
        var wander =
            0.7 * Math.Sin(2 * Math.PI * 0.09 * time + phase)
            + 0.35 * Smooth(source, key + 3000, time * 0.25);
        return gain * Math.Sin(2 * Math.PI * frequency * time + phase + wander);
    }

    private static double Transient(
        SimulationProvenance source,
        int key,
        double time,
        double interval,
        double width
    )
    {
        var block = (long)Math.Floor(time / interval);
        double value = 0;
        // Neighbouring events contribute their tails so block boundaries cannot cause jumps.
        for (var index = block - 1; index <= block + 1; index++)
        {
            if (Noise(source.Seed, source.SubjectIndex, key, index * 2) < -0.2)
                continue;
            var jitter = Noise(source.Seed, source.SubjectIndex, key, index * 2 + 1);
            var centre = (index + 0.5 + jitter * 0.3) * interval;
            var distance = (time - centre) / width;
            if (Math.Abs(distance) < 9)
                value += (1 - distance * distance) * Math.Exp(-distance * distance / 2);
        }
        return value;
    }

    public static double Envelope(EnvelopeParameters envelope, double seconds) =>
        1
        - envelope.Depth
        + envelope.Depth
            * (
                1
                + Math.Sin(
                    2
                        * Math.PI
                        * envelope.ModulationHz
                        * (seconds - envelope.DelayMilliseconds / 1000)
                )
            )
            / 2;

    public static double Current(
        ExperimentStimulusConfigurationSnapshot stimulus,
        double seconds,
        double duration,
        int seed = 0,
        int channel = 0
    )
    {
        if (seconds < 0 || seconds >= duration)
            return 0;
        var ramp = Math.Min(stimulus.RampSeconds, duration / 2);
        var gain = ramp <= 0 ? 1 : Math.Min(1, Math.Min(seconds, duration - seconds) / ramp);
        if (stimulus.Kind == StimulusKind.Sham || stimulus.Envelope?.IsSham == true)
            gain =
                ramp <= 0 ? 0
                : seconds < ramp ? Math.Sin(Math.PI * seconds / ramp)
                : seconds > duration - ramp ? Math.Sin(Math.PI * (duration - seconds) / ramp)
                : 0;
        var angle = 2 * Math.PI * stimulus.Frequency * seconds;
        var value = stimulus.Kind switch
        {
            StimulusKind.TAcs or StimulusKind.EnvelopeTAcs => Math.Sin(angle),
            StimulusKind.TRns => Noise(seed, 0, channel, (long)(seconds * 1000)),
            StimulusKind.TPcs => seconds * stimulus.Frequency % 1 < stimulus.DutyPercent / 100
                ? 1
                : 0,
            StimulusKind.Sham when stimulus.ShamMode == ShamWaveformMode.Alternating => Math.Sin(
                angle
            ),
            _ => 1d,
        };
        if (stimulus.Envelope is { } envelope)
            value *= Envelope(envelope, seconds);
        if (stimulus.Direction == StimulusDirection.Negative)
            value = -value;
        return value * gain;
    }
}
