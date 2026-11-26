using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed class SpeechTrainingResultsPageViewModel : PageViewModel
{
    public SpeechTrainingResultsPageViewModel(SpeechTrainingResultsRouteData route)
        : base(ApplicationPageNames.SpeechTrainingResults, "实验结果详情")
    {
        Route = route;
        Results = route
            .Results.Select(result =>
                result with
                {
                    SpeechRate = result.SpeechRate ?? route.Settings.SpeechRate,
                    IsFixedRir =
                        route.Settings.TrainingMode == SpeechTraining.SpeechTrainingMode.FixedRir,
                    IsFixedSnr =
                        route.Settings.TrainingMode == SpeechTraining.SpeechTrainingMode.FixedSnr,
                    LegacyCarrier =
                        route.Settings.AudioSettings?.CarrierSummary ?? route.Settings.Carrier,
                }
            )
            .ToArray();
        var config = route.Source.Configuration;
        HeaderBadges =
        [
            new("患者ID", config.SubjectId),
            new("实验ID", config.ExperimentId),
            PageHeaderBadgeViewModel.CreateExperimentMode(config.CreationMode),
        ];
        CurrentText = StimulusConfigurationSummaryBuilder.CurrentText(config.StimulusConfiguration);
        DelayText =
            $"{(config.StimulusConfiguration.Envelope?.DelayMilliseconds ?? 0) / 1000:0.###} s";
        Electrodes = string.Join(
            "   ",
            config.StimulusElectrodes.Select(e => $"{e.SiteId} · CH{e.PhysicalChannelId}")
        );
    }

    public SpeechTrainingResultsRouteData Route { get; }
    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges { get; }
    public IReadOnlyList<SpeechSentenceResult> Results { get; }
    public bool IsEmpty => Results.Count == 0;
    public bool IsAdaptive =>
        Settings.TrainingMode
            is SpeechTraining.SpeechTrainingMode.AdaptiveRate
                or SpeechTraining.SpeechTrainingMode.AdaptiveSnr;
    public bool IsSnr =>
        Settings.TrainingMode
            is SpeechTraining.SpeechTrainingMode.AdaptiveSnr
                or SpeechTraining.SpeechTrainingMode.FixedSnr;
    public bool IsFixedSnr => Settings.TrainingMode == SpeechTraining.SpeechTrainingMode.FixedSnr;
    public bool IsFixedRir => Settings.TrainingMode == SpeechTraining.SpeechTrainingMode.FixedRir;
    public string RirSettingsText => SpeechSentenceResult.RirSettingsText(Settings.Rir);
    public string CompletionStatus =>
        Results.Count == Route.SentenceCount ? "测试完成" : "测试未完成";
    public string ModeText =>
        IsFixedRir ? "固定 RIR 测试"
        : IsFixedSnr ? "固定信噪比测试"
        : IsSnr ? "自适应信噪比测试"
        : IsAdaptive ? "自适应语速测试(安静)"
        : string.IsNullOrWhiteSpace(Settings.Mode) ? "固定语速测试(安静)"
        : Settings.Mode;
    public string AdaptiveStatus =>
        (IsSnr ? Route.SnrSummary?.Status : Route.AdaptiveSummary?.Status) ?? "自适应记录不完整";
    public string AdaptiveScoringText => Settings.AdaptiveScoring?.Description ?? "未记录";
    public string AdaptiveLabel =>
        IsSnr
            ? Route.SnrSummary?.Label ?? "自适应信噪比阈值（SRT）"
            : Route.AdaptiveSummary?.Label ?? "自适应语速阈值（SRT）";
    public string AdaptiveValue =>
        IsSnr ? (Route.SnrSummary?.MeanSnrDb is { } snr ? $"{snr:0.###} dB" : "—")
        : Route.AdaptiveSummary?.MeanTargetRate is { } value ? $"{value:0.###} 字/s"
        : "—";
    public bool HasAdaptiveValue =>
        !IsFixedSnr
        && !IsFixedRir
        && (
            IsSnr
                ? Route.SnrSummary?.MeanSnrDb is not null
                : Route.AdaptiveSummary?.MeanTargetRate is not null
        );
    public bool IsMixedCarrier =>
        IsFixedSnr || IsFixedRir
            ? !Route.Playbacks.IsDefaultOrEmpty
                && Route
                    .Playbacks.Select(p => p.AudioSnapshot.Settings.Vocoder)
                    .Distinct()
                    .Skip(1)
                    .Any()
        : IsSnr ? Route.SnrSummary?.MixedCarrier == true
        : Route.AdaptiveSummary?.MixedCarrier == true;
    public string SnrSettingsText =>
        IsFixedSnr
            ? $"固定 SNR：{(Settings.FixedSnrDb is { } fixedDb ? $"{fixedDb:0.###} dB" : "未记录")}；噪声：{Route.Playbacks.FirstOrDefault()?.AudioSnapshot.Noise?.Settings.FileName ?? "未记录"}"
            : $"初始 SNR：{(Settings.InitialSnrDb is { } db ? $"{db:0.###} dB" : "未记录")}；噪声：{Route.Playbacks.FirstOrDefault()?.AudioSnapshot.Noise?.Settings.FileName ?? "未记录"}";
    public string ExperimentId => Route.Source.Configuration.ExperimentId;
    public string SubjectId => Route.Source.Configuration.SubjectId;
    public string StartedAtText => Route.StartedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
    public string ResourceRangeText => $"{Settings.ResourceStart:000}–{Settings.ResourceEnd:000}";
    public int ActualSentenceCount => Route.SentenceCount;
    public string CompletedText => $"{Results.Count}/{Route.SentenceCount}";
    public int RatedCount => Results.Sum(x => x.Total);
    public int CorrectCount => Results.Sum(x => x.Correct);
    public string AccuracyText =>
        RatedCount == 0 ? "待判定" : $"{100d * CorrectCount / RatedCount:0}%";
    public string CurrentText { get; }
    public string DelayText { get; }
    public string Electrodes { get; }
    public SpeechTrainingSettings Settings => Route.Settings;
    public string CarrierText =>
        IsMixedCarrier ? "混合载体"
        : Results.Select(r => r.CarrierText).Distinct().Count() > 1 ? "逐句调整"
        : Results.Count > 0 ? Results[0].CarrierText
        : Settings.AudioSettings?.CarrierSummary ?? Settings.Carrier;
    public string SpeechRateText =>
        IsSnr || IsFixedRir ? "原速"
        : IsAdaptive ? "算法逐句调整"
        : Results.Select(r => r.SpeechRate).Distinct().Count() > 1 ? "逐句调整"
        : (
            Results.Count > 0 ? Results[0].SpeechRateText
            : Settings.SpeechRate == 0 ? "原速"
            : $"{Settings.SpeechRate:0.###} 字/s"
        );
}
