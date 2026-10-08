using System;
using System.Collections.Generic;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed record StimulusParameterRange(
    double Minimum,
    double Maximum,
    double Increment,
    int DecimalPlaces
)
{
    public bool Contains(double value) =>
        double.IsFinite(value)
        && value >= Minimum
        && value <= Maximum
        && (decimal)value % (decimal)Increment == 0;
}

public enum StimulusDurationRule
{
    Positive,
    Ramp,
    Period,
}

/// <summary>Application semantics; wire compatibility remains a separate check.</summary>
public sealed record StimulusParameterPolicy(
    bool ShowFrequency,
    bool ShowRamp,
    bool ShowDutyPercent,
    StimulusParameterRange Frequency,
    StimulusParameterRange Ramp,
    StimulusDurationRule DurationRule
)
{
    public static StimulusParameterRange Duty { get; } = new(1, 99, 1, 0);
    private static readonly StimulusParameterRange DeviceFrequency = new(0.1, 2500, 0.1, 1);
    private static readonly StimulusParameterRange DeviceRamp = new(0, 30, 1, 0);

    public static StimulusParameterPolicy For(StimulusKind kind, ShamWaveformMode shamMode) =>
        kind switch
        {
            StimulusKind.TDcs => new(
                false,
                true,
                false,
                DeviceFrequency,
                DeviceRamp,
                StimulusDurationRule.Ramp
            ),
            StimulusKind.Sham when shamMode == ShamWaveformMode.Direct => new(
                false,
                true,
                false,
                new(0.1, 100, 0.1, 1),
                DeviceRamp,
                StimulusDurationRule.Ramp
            ),
            StimulusKind.TAcs or StimulusKind.Sham => new(
                true,
                false,
                false,
                new(0.1, 100, 0.1, 1),
                DeviceRamp,
                StimulusDurationRule.Period
            ),
            StimulusKind.TPcs => new(
                true,
                false,
                true,
                new(1, 100, 0.1, 1),
                DeviceRamp,
                StimulusDurationRule.Period
            ),
            StimulusKind.EnvelopeTAcs => new(
                true,
                true,
                false,
                new(0, 1000, 0.1, 1),
                new(0, 60, 0.1, 1),
                StimulusDurationRule.Positive
            ),
            _ => new(
                false,
                false,
                false,
                DeviceFrequency,
                DeviceRamp,
                StimulusDurationRule.Positive
            ),
        };

    public static string ModeName(StimulusKind kind, ShamWaveformMode shamMode) =>
        kind switch
        {
            StimulusKind.TDcs => "tDCS",
            StimulusKind.TAcs => "tACS",
            StimulusKind.TRns => "tRNS",
            StimulusKind.TPcs => "tPCS",
            StimulusKind.EnvelopeTAcs => "包络-tACS",
            StimulusKind.Sham => shamMode == ShamWaveformMode.Direct
                ? "Sham（直流）"
                : "Sham（交流）",
            _ => kind.ToString(),
        };

    public string FormatSummary(double frequency, double ramp, double duty)
    {
        var parts = new List<string>();
        if (ShowFrequency)
            parts.Add($"{frequency:0.###} Hz");
        if (ShowRamp)
            parts.Add($"缓升降 {ramp:0.###} s");
        if (ShowDutyPercent)
            parts.Add($"占空比 {duty:0.###}%");
        return string.Join(" / ", parts);
    }

    public static string? Validate(
        StimulusKind kind,
        ShamWaveformMode shamMode,
        double frequency,
        double ramp,
        double duty,
        bool deviceCompatibility = false
    )
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(shamMode))
            return "刺激范式或 Sham 模式无效。";
        var policy = For(kind, shamMode);
        if (
            policy.ShowFrequency
            && !(
                kind == StimulusKind.EnvelopeTAcs
                    ? double.IsFinite(frequency) && frequency > 0 && frequency <= 1000
                    : policy.Frequency.Contains(frequency)
            )
        )
            return $"频率须为 {policy.Frequency.Minimum}～{policy.Frequency.Maximum} Hz，步进 {policy.Frequency.Increment} Hz。";
        if (
            policy.ShowRamp
            && !(
                kind == StimulusKind.EnvelopeTAcs
                    ? double.IsFinite(ramp) && ramp >= 0 && ramp <= 60
                    : policy.Ramp.Contains(ramp)
            )
        )
            return $"缓升缓降时间须为 {policy.Ramp.Minimum}～{policy.Ramp.Maximum} s，步进 {policy.Ramp.Increment} s。";
        if (policy.ShowDutyPercent && !Duty.Contains(duty))
            return "占空比须为 1～99% 的整数。";
        if (deviceCompatibility)
        {
            if (kind == StimulusKind.EnvelopeTAcs)
                return "包络-tACS 仅支持数据生成与历史回放，不能下发设备。";
            if (!DeviceFrequency.Contains(frequency))
                return "设备协议兼容性字段无效：频率须为 0.1～2500 Hz，步进 0.1 Hz。";
            if (!DeviceRamp.Contains(ramp))
                return "设备协议兼容性字段无效：缓升缓降时间须为 0～30 s 的整数。";
            if (!Duty.Contains(duty))
                return "设备协议兼容性字段无效：占空比须为 1～99% 的整数。";
        }
        if (!double.IsFinite(frequency) || !double.IsFinite(ramp) || !double.IsFinite(duty))
            return "刺激波形参数必须为有限数值。";
        return null;
    }

    public string? ValidateDuration(double seconds, double frequency, double ramp)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return "请输入有效的正刺激时长";
        if (DurationRule == StimulusDurationRule.Ramp)
        {
            if (!double.IsFinite(ramp) || ramp < 0)
                return "缓升缓降时间无效";
            if (seconds <= ramp * 2)
                return $"刺激时长需大于缓升缓降总时长（{ramp * 2:0.#} s）";
        }
        if (DurationRule == StimulusDurationRule.Period)
        {
            if (!double.IsFinite(frequency) || frequency <= 0)
                return "刺激频率无效，无法计算完整波形周期";
            if (seconds < 1 / frequency)
                return $"刺激时长需至少包含一个完整波形周期（{1000 / frequency:0.#} ms）";
        }
        return null;
    }

    public static string? ValidateExecutableTemplate(ExperimentConfigurationTemplate template)
    {
        if (template.CreationMode == ExperimentCreationMode.AcquisitionOnly)
            return null;
        var s = template.StimulusConfiguration;
        if (s.Envelope is not null)
            return "包络-tACS 仅支持数据生成与历史回放，不能下发设备。";
        return Validate(s.Kind, s.ShamMode, s.Frequency, s.RampSeconds, s.DutyPercent, true)
            ?? For(s.Kind, s.ShamMode)
                .ValidateDuration(
                    template.Timing.StimulationMilliseconds / 1000,
                    s.Frequency,
                    s.RampSeconds
                );
    }
}
