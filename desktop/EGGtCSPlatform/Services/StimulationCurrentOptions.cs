using System;

namespace EGGtCSPlatform.Services;

public sealed class StimulationCurrentOptions
{
    public const string SectionName = "StimulationCurrent";
    public const int ProtocolMinimum = 4;
    public const int ProtocolMaximum = 200;
    public const int ProtocolQuantum = 4;
    public const double RawUnitsPerMilliAmp = 100d;

    public int Minimum { get; set; } = ProtocolMinimum;

    public int Maximum { get; set; } = ProtocolMaximum;

    public int Step { get; set; } = ProtocolQuantum;

    public bool IsValid() =>
        Minimum >= ProtocolMinimum
        && Minimum < Maximum
        && Maximum <= ProtocolMaximum
        && Step > 0
        && Minimum % ProtocolQuantum == 0
        && Maximum % ProtocolQuantum == 0
        && Step % ProtocolQuantum == 0
        && Minimum % Step == 0
        && Maximum % Step == 0;
}

public sealed class StimulationCurrentPolicy
{
    public StimulationCurrentPolicy(StimulationCurrentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid())
            throw new ArgumentException("StimulationCurrent 配置无效。", nameof(options));
        MinimumRaw = options.Minimum;
        MaximumRaw = options.Maximum;
        StepRaw = options.Step;
    }

    public int MinimumRaw { get; }

    public int MaximumRaw { get; }

    public int StepRaw { get; }

    public double MinimumMilliAmps => ToMilliAmps(MinimumRaw);

    public double MaximumMilliAmps => ToMilliAmps(MaximumRaw);

    public double StepMilliAmps => ToMilliAmps(StepRaw);

    public int ToRaw(double milliAmps) =>
        checked(
            (int)
                Math.Round(
                    milliAmps * StimulationCurrentOptions.RawUnitsPerMilliAmp,
                    MidpointRounding.AwayFromZero
                )
        );

    public double ToMilliAmps(int raw) => raw / StimulationCurrentOptions.RawUnitsPerMilliAmp;

    public double Normalize(double milliAmps)
    {
        if (!double.IsFinite(milliAmps))
            return MinimumMilliAmps;
        var raw = Math.Clamp(ToRaw(milliAmps), MinimumRaw, MaximumRaw);
        var lower = raw / StepRaw * StepRaw;
        var upper = Math.Min(MaximumRaw, lower + StepRaw);
        var snapped = raw - lower < upper - raw ? lower : upper;
        return ToMilliAmps(Math.Clamp(snapped, MinimumRaw, MaximumRaw));
    }

    public bool IsValidMilliAmps(double milliAmps)
    {
        if (!double.IsFinite(milliAmps))
            return false;
        var rawValue = milliAmps * StimulationCurrentOptions.RawUnitsPerMilliAmp;
        var raw = Math.Round(rawValue, MidpointRounding.AwayFromZero);
        return Math.Abs(rawValue - raw) <= 0.000001d
            && raw >= MinimumRaw
            && raw <= MaximumRaw
            && (int)raw % StepRaw == 0;
    }
}
