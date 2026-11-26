using System;
using System.Collections.Generic;

namespace EGGtCSPlatform.MainApp;

public sealed record SpeechTrainingSettings(
    string Corpus,
    string SentenceList,
    string Mode,
    string Carrier,
    decimal SpeechRate,
    int ListSize = 20,
    int ResourceStart = 1,
    int ResourceEnd = 0,
    SpeechTraining.TrainingAudioSettings? AudioSettings = null,
    SpeechTraining.SpeechTrainingMode TrainingMode = SpeechTraining.SpeechTrainingMode.FixedRate,
    SpeechTraining.AdaptiveScoringRule? AdaptiveScoring = null,
    double? InitialSnrDb = null,
    double? FixedSnrDb = null,
    SpeechTraining.TrainingRoomSettings? Rir = null
);

public sealed record SpeechSentenceResult(
    int Number,
    string Text,
    int Correct,
    int Total,
    decimal? SpeechRate = null,
    SpeechTraining.TrainingAudioSnapshot? AudioSnapshot = null,
    string? LegacyCarrier = null,
    SpeechTraining.AdaptiveSpeechRecord? Adaptive = null,
    bool? PlaybackCompleted = null,
    SpeechTraining.AdaptiveSnrRecord? AdaptiveSnr = null,
    SpeechTraining.SpeechNoiseMetadata? Noise = null,
    bool IsFixedSnr = false,
    SpeechTraining.SpeechRirMetadata? Rir = null,
    bool IsFixedRir = false
)
{
    public string CarrierText =>
        AudioSnapshot?.Settings.CarrierSummary ?? LegacyCarrier ?? "原始语音";
    public string NumberText => Number.ToString("00");
    public string ScoreText => $"{Correct}/{Total}";
    public string AccuracyText => Total == 0 ? "待判定" : $"{100d * Correct / Total:0}%";
    public string SpeechRateText =>
        Adaptive is { } adaptive ? $"{adaptive.Timing.TargetSpeechRate:0.###} 字/s"
        : SpeechRate is null ? "—"
        : SpeechRate == 0 ? "原速"
        : $"{SpeechRate:0.###} 字/s";
    public bool IsAdaptive => Adaptive is not null || AdaptiveSnr is not null;
    public string AdaptiveText =>
        AdaptiveSnr is { } snr
            ? $"{(snr.Submission.Passed ? "通过" : "未通过")} · {snr.Submission.SnrDb:0.###} dB · 反转 {snr.Submission.Reversals}"
        : Adaptive is { } a
            ? $"{(a.Submission.Passed ? "通过" : "未通过")} · {a.Submission.Multiplier:0.###} 倍 · 反转 {a.Submission.Reversals}"
        : "";
    public string PlaybackCompletedText =>
        PlaybackCompleted switch
        {
            true => "已播放完成",
            false => "播放未完成时提交",
            _ => "未知（旧记录）",
        };
    public SpeechTraining.SpeechNoiseMetadata? NoiseSnapshot => Noise ?? AdaptiveSnr?.Noise;
    public bool HasFixedNoise => Noise is not null && AdaptiveSnr is null;
    public string NoiseText =>
        NoiseSnapshot is { } n
            ? $"SNR：{n.Settings.SnrDb:0.###} dB · {n.Settings.Kind}"
            : "噪声快照：未记录";
    private string NoiseDetails =>
        NoiseSnapshot is { } n
            ? $"{NoiseText} · {n.Settings.FileName}\nSHA256：{n.Settings.Sha256}\n"
                + $"种子：{n.Settings.Seed}；偏移：{n.OffsetFrames} 帧（{n.SampleRate} Hz）\n"
                + $"校准 RMS：{n.Settings.CalibrationRms:G17}；归一化增益：{n.NormalizationGain:G17}；峰值保护增益：{n.PeakGain:G17}\n实际混音 RMS：{n.OutputRms:G17}"
            : "噪声快照：未记录";

    public static string RirSettingsText(SpeechTraining.TrainingRoomSettings? r) =>
        r is null
            ? "RIR 参数：未记录"
            : $"房间：{r.Acoustics.Room.X:0.##}×{r.Acoustics.Room.Y:0.##}×{r.Acoustics.Room.Z:0.##} m；声源：({r.Acoustics.Source.X:0.##}, {r.Acoustics.Source.Y:0.##}, {r.Acoustics.Source.Z:0.##}) m；听者：({r.Acoustics.Listener.X:0.##}, {r.Acoustics.Listener.Y:0.##}, {r.Acoustics.Listener.Z:0.##}) m\n"
                + $"T60：{r.Acoustics.T60:0.###} s；请求长度：{r.Acoustics.LengthSeconds:0.###} s；早晚分界：直达后 {r.Acoustics.EarlyLateMilliseconds:0.##} ms；早期：{(r.Acoustics.Early ? "开" : "关")}；晚期：{(r.Acoustics.Late ? "开" : "关")}\n"
                + (
                    r.AddNoise
                        ? $"噪声：{r.NoiseKind}；SNR：{r.SnrDb:0.##} dB（直达声＋启用的早期反射）"
                        : "噪声：关闭"
                );

    public string RirDetails =>
        Rir is not { } r
            ? "RIR 快照：未记录"
            : RirSettingsText(r.Configuration.Settings)
                + $"\n算法：{r.Configuration.AlgorithmVersion}；采样率：{r.SampleRate} Hz\n完整 RIR SHA256：{r.FullHash}\n生效 RIR SHA256：{r.EffectiveHash}\n"
                + $"直达帧：{r.DirectFrame}；分界帧：{r.DivisionFrame}；RIR 帧数：{r.RirFrames}；输出帧数：{r.OutputFrames}\n"
                + $"校准 RMS：{r.Configuration.CalibrationRms:G17}；早期系数 RMS：{r.EarlyCoefficientRms:G17}；校准增益：{r.CalibrationGain:G17}\n"
                + $"早期语音 RMS：{r.EarlySpeechRms:G17}；噪声增益：{r.NoiseGain:G17}；峰值保护增益：{r.PeakGain:G17}";
    public string DetailText =>
        IsFixedRir || Rir is not null
            ? $"{RirDetails}\n"
                + (NoiseSnapshot is not null ? $"{NoiseDetails}\n" : "")
                + $"提交时：{PlaybackCompletedText}\n载体：{CarrierDetails}"
        : AdaptiveSnr is { } snr
            ? $"{AdaptiveText}；步长 {snr.Submission.StepDb} dB；下一句 {snr.Submission.NextSnrDb:0.###} dB\n"
                + $"噪声：{snr.Noise.Settings.Kind} · {snr.Noise.Settings.FileName}\nSHA256：{snr.Noise.Settings.Sha256}\n"
                + $"种子：{snr.Noise.Settings.Seed}；偏移：{snr.Noise.OffsetFrames} 帧（{snr.Noise.SampleRate} Hz）\n"
                + $"校准 RMS：{snr.Noise.Settings.CalibrationRms:G17}；归一化增益：{snr.Noise.NormalizationGain:G17}；峰值保护增益：{snr.Noise.PeakGain:G17}\n"
                + $"实际混音 RMS：{snr.Noise.OutputRms:G17}\n提交时：{PlaybackCompletedText}\n载体：{CarrierDetails}"
        : Adaptive is { } a
            ? $"原始语速：{a.Timing.OriginalSpeechRate:0.###} 字/s；原始时长：{a.Timing.OriginalDurationSeconds:0.###} s\n"
                + $"R：{a.Submission.R:G17}；倍率：{a.Submission.Multiplier:G17}\n"
                + $"目标语速：{a.Timing.TargetSpeechRate:0.###} 字/s；实际输出语速：{a.Timing.ActualSpeechRate:0.###} 字/s（SOLAFS 输出）\n"
                + $"输出帧数：{a.Timing.OutputFrames}；采样率：{a.Timing.SampleRate} Hz\n"
                + $"{AdaptiveText}；采用系数：{a.Submission.Factor}；下一句 R：{a.Submission.NextR:G17}\n"
                + $"提交时：{PlaybackCompletedText}\n载体：{CarrierDetails}"
        : (IsFixedSnr || NoiseSnapshot is not null ? $"{NoiseDetails}\n" : "")
            + $"提交时：{PlaybackCompletedText}\n载体：{CarrierDetails}";
    private string CarrierDetails =>
        AudioSnapshot?.Settings.Vocoder is { } v
            ? $"{v.Summary}；最大激活数：{v.Maxima}；随机种子：{v.Seed}"
            : CarrierText;
}

/// <summary>In-memory speech training result; never persisted as a device experiment.</summary>
public sealed record SpeechTrainingResultsRouteData(
    EnvelopeStimulationRouteData Source,
    SpeechTrainingSettings Settings,
    DateTimeOffset StartedAt,
    int SentenceCount,
    IReadOnlyList<SpeechSentenceResult> Results,
    SpeechTraining.AdaptiveSpeechSummary? AdaptiveSummary = null,
    System.Collections.Immutable.ImmutableArray<SpeechTraining.SpeechPlaybackRecord> Playbacks =
        default,
    SpeechTraining.AdaptiveSnrSummary? SnrSummary = null
);
