using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed partial class SpeechExperimentResultsPageViewModel : PageViewModel
{
    private readonly INavigationRouter? _router;
    private readonly DialogService? _dialogs;
    private readonly IDialogProvider? _host;
    private readonly IExperimentConfigurationTransferContext? _transfer;
    private SpeechExperimentResult? _experiment;
    private bool _updating;

    public SpeechExperimentResultsPageViewModel(
        SpeechExperimentResultsRouteData route,
        ISpeechExperimentService? experiments = null,
        INavigationRouter? router = null,
        DialogService? dialogs = null,
        IDialogProvider? host = null,
        IExperimentConfigurationTransferContext? transfer = null
    )
        : base(ApplicationPageNames.SpeechTrainingResults, "实验结果详情")
    {
        _router = router;
        _dialogs = dialogs;
        _host = host;
        _transfer = transfer;
        ViewRoundCommand = new RelayCommand<SpeechRoundRow>(ViewRound);
        CompareSelectedCommand = new RelayCommand(
            () =>
            {
                SelectedTab = 1;
                RefreshComparison();
            },
            () => SelectedCount >= 2
        );
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => _experiment is not null);
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => Rounds.Count > 0);
        SelectedTab = Math.Clamp(route.Tab, 0, 2);
        LoadTask = LoadAsync(route, experiments);
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public Task LoadTask { get; }
    public ObservableCollection<SpeechRoundRow> Rounds { get; } = [];
    public ObservableCollection<SpeechListFilter> SentenceLists { get; } = [];
    public IReadOnlyList<string> ModeOptions =>
        new[] { "全部模式" }.Concat(Rounds.Select(x => x.Mode).Distinct()).ToArray();
    public IReadOnlyList<string> ComparisonModeOptions =>
        Rounds.Select(x => x.Mode).Distinct().ToArray();
    public IReadOnlyList<SpeechRoundRow> VisibleRounds =>
        Rounds.Where(x => ModeFilter == "全部模式" || x.Mode == ModeFilter).ToArray();
    public IReadOnlyList<SpeechRoundRow> ComparisonRows =>
        Rounds
            .Where(x =>
                x.Mode == ComparisonMode
                && SentenceLists.Any(f => f.Label == x.CorpusList && f.Selected)
            )
            .ToArray();
    public IReadOnlyList<SpeechRoundRow> ChartRows { get; private set; } = [];
    public int SelectedCount => Rounds.Count(x => x.Selected);
    public string SelectionText => $"已选 {SelectedCount} 次训练";
    public string TotalText => $"共 {Rounds.Count} 次训练";
    public int TotalCount => Rounds.Count;
    public int CompletedCount => Rounds.Count(x => x.Complete);
    public int InterruptedCount =>
        Rounds.Count(x => x.Round.Status == SpeechRoundStatus.Interrupted);
    public string CompletedText => $"{CompletedCount} / {TotalCount}";
    public string ModesText => $"{Rounds.Select(x => x.Mode).Distinct().Count()} 种";
    public string ExperimentCode => _experiment?.Source.ExperimentCode ?? "—";
    public string SubjectId => _experiment?.Source.SubjectId ?? "—";
    public string StartedAtText =>
        _experiment?.StartedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "—";
    public string CurrentText =>
        _experiment is null
            ? "—"
            : StimulusConfigurationSummaryBuilder.CurrentText(_experiment.Source.Stimulation);
    public string Electrodes =>
        _experiment is null
            ? "—"
            : string.Join(" · ", _experiment.Source.Electrodes.Select(x => x.SiteId));
    public string DelayRange =>
        Rounds.Count == 0
            ? "—"
            : $"{Rounds.Min(x => x.Round.DelayMilliseconds):0}–{Rounds.Max(x => x.Round.DelayMilliseconds):0} ms";
    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges =>
        [
            new("患者ID", SubjectId),
            new("实验ID", ExperimentCode),
            PageHeaderBadgeViewModel.CreateExperimentMode(ExperimentCreationMode.StimulusOnly),
        ];
    public bool IsEmpty => Rounds.Count == 0;
    public bool HasDetail => Detail is not null;
    public bool IsFixedDetail => Detail?.IsAdaptive != true;
    public string DetailStatus => SelectedRound?.Status ?? "";
    public string DetailSummary =>
        SelectedRound is null
            ? ""
            : $"{SelectedRound.CorpusList}   {SelectedRound.Mode}   {SelectedRound.Condition}   {SelectedRound.Round.Result.Settings.Carrier} · {SelectedRound.Delay}";
    public string DetailSecondLabel => IsFixedDetail ? "总字数" : "正确 / 已评字数";
    public string DetailSecondValue =>
        IsFixedDetail ? Detail?.RatedCount.ToString() ?? "—" : DetailScoreText;
    public string DetailThirdLabel => IsFixedDetail ? "正确字数" : "字正确率";
    public string DetailThirdValue =>
        IsFixedDetail ? Detail?.CorrectCount.ToString() ?? "—" : Detail?.AccuracyText ?? "—";
    public string DetailScoreText =>
        Detail is null ? "—" : $"{Detail.CorrectCount}/{Detail.RatedCount}";
    public string DetailFourthLabel => IsFixedDetail ? "正确率" : Detail?.AdaptiveLabel ?? "SRT";
    public string DetailFourthValue =>
        IsFixedDetail ? Detail?.AccuracyText ?? "—"
        : SelectedRound?.Complete == true ? Detail?.AdaptiveValue ?? "—"
        : "—";
    public string ChartUnit => ChartRows.FirstOrDefault()?.Unit ?? "";

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    private string _modeFilter = "全部模式";

    [ObservableProperty]
    private string _comparisonMode = "";

    [ObservableProperty]
    private bool _differentDelay;
    public bool SameParameters
    {
        get => !DifferentDelay;
        set
        {
            if (value)
                DifferentDelay = false;
        }
    }

    [ObservableProperty]
    private SpeechRoundRow? _selectedRound;

    [ObservableProperty]
    private SpeechTrainingResultsPageViewModel? _detail;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _comparisonHint = "请选择至少两次有效完成的训练。";
    public RelayCommand<SpeechRoundRow> ViewRoundCommand { get; }
    public RelayCommand CompareSelectedCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand CopyCommand { get; }

    private async Task LoadAsync(
        SpeechExperimentResultsRouteData route,
        ISpeechExperimentService? service
    )
    {
        try
        {
            _experiment =
                route.Snapshot
                ?? (
                    service is not null
                        ? await service.LoadAsync(route.ExperimentId)
                        : throw new InvalidOperationException("结果读取服务不可用。")
                );
            foreach (var r in _experiment.Rounds.OrderBy(x => x.Number))
                Rounds.Add(new(r, SelectionChanged, CanSelect));
            SelectedRound =
                Rounds.FirstOrDefault(x => x.Round.Id == route.TrainingId)
                ?? Rounds.FirstOrDefault();
            ComparisonMode = Rounds.FirstOrDefault()?.Mode ?? "";
            foreach (
                var property in new[]
                {
                    nameof(ModeOptions),
                    nameof(ComparisonModeOptions),
                    nameof(VisibleRounds),
                    nameof(TotalCount),
                    nameof(CompletedCount),
                    nameof(InterruptedCount),
                    nameof(CompletedText),
                    nameof(ModesText),
                    nameof(ExperimentCode),
                    nameof(SubjectId),
                    nameof(StartedAtText),
                    nameof(CurrentText),
                    nameof(Electrodes),
                    nameof(DelayRange),
                    nameof(HeaderBadges),
                    nameof(IsEmpty),
                    nameof(TotalText),
                }
            )
                OnPropertyChanged(property);
            ExportCommand.NotifyCanExecuteChanged();
            CopyCommand.NotifyCanExecuteChanged();
        }
        catch (Exception e)
        {
            StatusText = $"无法读取言语实验结果：{e.Message}";
        }
    }

    partial void OnModeFilterChanged(string value) => OnPropertyChanged(nameof(VisibleRounds));

    partial void OnComparisonModeChanged(string value)
    {
        _updating = true;
        foreach (var row in Rounds.Where(x => x.Mode != value))
            row.Selected = false;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        CompareSelectedCommand.NotifyCanExecuteChanged();
        SentenceLists.Clear();
        foreach (
            var list in Rounds.Where(x => x.Mode == value).Select(x => x.CorpusList).Distinct()
        )
            SentenceLists.Add(new(list, RefreshComparison));
        _updating = false;
        RefreshComparison();
    }

    partial void OnDifferentDelayChanged(bool value)
    {
        OnPropertyChanged(nameof(SameParameters));
        RefreshComparison();
    }

    partial void OnSelectedRoundChanged(SpeechRoundRow? value)
    {
        Detail =
            value is null || _experiment is null
                ? null
                : new(SpeechResultRoutes.Detail(_experiment.Source, value.Round));
        OnPropertyChanged(nameof(DetailSecondLabel));
        OnPropertyChanged(nameof(DetailSecondValue));
        OnPropertyChanged(nameof(DetailThirdLabel));
        OnPropertyChanged(nameof(DetailThirdValue));
        OnPropertyChanged(nameof(DetailScoreText));
        OnPropertyChanged(nameof(DetailFourthLabel));
        OnPropertyChanged(nameof(DetailFourthValue));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(IsFixedDetail));
        OnPropertyChanged(nameof(DetailSummary));
        OnPropertyChanged(nameof(DetailStatus));
    }

    private void ViewRound(SpeechRoundRow? row)
    {
        if (row is not null)
        {
            SelectedRound = row;
            SelectedTab = 2;
        }
    }

    private bool CanSelect(SpeechRoundRow row)
    {
        var first = Rounds.FirstOrDefault(x => x != row && x.Selected && x.Key(DifferentDelay) != row.Key(DifferentDelay));
        if (row.CanCompare && first is null)
            return true;
        ComparisonHint = !row.CanCompare
            ? "该训练未有效完成或缺少有效主结果，不能加入对比。"
            : row.Incompatibility(first!, DifferentDelay);
        StatusText = ComparisonHint;
        return false;
    }

    private void SelectionChanged(SpeechRoundRow row)
    {
        if (_updating)
            return;
        if (row.Selected)
            ComparisonMode = row.Mode;
        StatusText = "";
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        CompareSelectedCommand.NotifyCanExecuteChanged();
        RefreshComparison();
    }

    private void RefreshComparison()
    {
        if (_updating)
            return;
        foreach (var row in Rounds)
            row.UpdateSelectionAvailability(row.Selected || (row.CanCompare && Rounds
                .Where(x => x.Selected)
                .All(x => x.Key(DifferentDelay) == row.Key(DifferentDelay))));
        var selected = ComparisonRows
            .Where(x => x.Selected && x.CanCompare)
            .OrderBy(x => x.Round.Number)
            .ToArray();
        var compatible = selected.Select(x => x.Key(DifferentDelay)).Distinct().Count() <= 1;
        ChartRows = selected.Length >= 2 && compatible ? selected : [];
        ComparisonHint =
            !compatible ? "所选训练的测试条件不一致，请调整选择。"
            : selected.Length < 2 ? "请选择至少两次有效完成的训练。"
            : "仅对比有效完成训练；中断记录不计入。";
        OnPropertyChanged(nameof(ComparisonRows));
        OnPropertyChanged(nameof(ChartRows));
        OnPropertyChanged(nameof(ChartUnit));
    }

    private async Task ExportAsync()
    {
        if (_experiment is null || _dialogs is null)
            return;
        try
        {
            var directory = await _dialogs.FolderPicker("选择言语实验结果导出目录");
            if (directory is null)
                return;
            var path = await SpeechExperimentExportService.ExportAsync(_experiment, directory);
            StatusText = $"实验结果已导出：{path}";
        }
        catch (Exception e)
        {
            StatusText = $"导出失败：{e.Message}";
        }
    }

    private async Task CopyAsync()
    {
        if (
            _experiment is null
            || _dialogs is null
            || _host is null
            || _transfer is null
            || _router is null
        )
            return;
        var dialog = new SpeechCopyDialogViewModel(
            Rounds,
            SelectedTab == 2 ? SelectedRound : Rounds.LastOrDefault()
        );
        await _dialogs.ShowDialog(_host, dialog);
        if (!dialog.Confirmed || dialog.Selected is null)
            return;
        _transfer.Set(CreateCopyTemplate(_experiment.Source, dialog.Selected.Round));
        _router.Navigate(StartExperimentRouteData.NewExperiment);
    }

    public static ExperimentConfigurationTemplate CreateCopyTemplate(
        SpeechExperimentSource source,
        SpeechTrainingRound round
    )
    {
        var route = source.ToRoute().Configuration;
        route = SpeechResultRoutes.WithEnvelope(
            route,
            route.StimulusConfiguration.Envelope! with
            {
                DelayMilliseconds = round.DelayMilliseconds,
                DebugFrequencyHz = 500,
            }
        );
        return SingleStimulusConfiguration.Snapshot(route, TimeSpan.Zero) with
        {
            SpeechTrainingPreset = round.Result.Settings,
        };
    }
}

public sealed partial class SpeechRoundRow : ObservableObject
{
    private readonly Action<SpeechRoundRow> _changed;
    private readonly Func<SpeechRoundRow, bool>? _canSelect;

    public SpeechRoundRow(
        SpeechTrainingRound round,
        Action<SpeechRoundRow> changed,
        Func<SpeechRoundRow, bool>? canSelect = null)
    {
        Round = round;
        _changed = changed;
        _canSelect = canSelect;
        _canToggle = CanCompare;
        ToggleCommand = new RelayCommand(() => Selected = !Selected, () => CanToggle);
    }

    public SpeechTrainingRound Round { get; }
    public string Label => $"第 {Round.Number} 次 · {Mode}";
    public string Number => $"第 {Round.Number} 次";
    public string Time => Round.Result.StartedAt.ToLocalTime().ToString("HH:mm");
    public string Mode =>
        Round.Result.Settings.TrainingMode switch
        {
            SpeechTrainingMode.FixedRate => "固定语速",
            SpeechTrainingMode.AdaptiveRate => "自适应语速",
            SpeechTrainingMode.AdaptiveSnr => "自适应信噪比",
            SpeechTrainingMode.FixedSnr => "固定信噪比",
            _ => "固定 RIR",
        };
    public string CorpusList =>
        $"{Round.Result.Settings.Corpus} · {Round.Result.Settings.SentenceList}";
    public string Delay => $"{Round.DelayMilliseconds:0} ms";
    public bool Complete => Round.Status == SpeechRoundStatus.Completed;
    public string Status =>
        Complete ? "完成"
        : Round.Status == SpeechRoundStatus.Interrupted ? "中断"
        : "进行中";
    public string StatusBackground => Complete ? "#EAF6EF" : "#FDEBEC";
    public string StatusColor => Complete ? "#32A269" : "#EF4444";
    public string Progress => $"{Round.Result.Sentences.Count}/{Round.Result.SentenceCount}";
    public string Condition =>
        Round.Result.Settings.TrainingMode switch
        {
            SpeechTrainingMode.FixedRate => Round.Result.Settings.SpeechRate == 0
                ? "原速"
                : $"{Round.Result.Settings.SpeechRate:0.###} 字/秒",
            SpeechTrainingMode.AdaptiveRate => "自适应语速（安静）",
            SpeechTrainingMode.AdaptiveSnr => $"初始 {Round.Result.Settings.InitialSnrDb:0.##} dB",
            SpeechTrainingMode.FixedSnr => $"{Round.Result.Settings.FixedSnrDb:0.##} dB",
            _ => $"T60 {Round.Result.Settings.Rir?.Acoustics.T60:0.###} s",
        };
    public bool Adaptive =>
        Round.Result.Settings.TrainingMode
            is SpeechTrainingMode.AdaptiveRate
                or SpeechTrainingMode.AdaptiveSnr;
    public double? Value =>
        Round.Result.Settings.TrainingMode switch
        {
            SpeechTrainingMode.AdaptiveRate => Complete
                ? Round.Result.AdaptiveSummary?.MeanTargetRate
                : null,
            SpeechTrainingMode.AdaptiveSnr => Complete ? Round.Result.SnrSummary?.MeanSnrDb : null,
            _ => Round.Result.Accuracy * 100,
        };
    public string Unit =>
        Round.Result.Settings.TrainingMode switch
        {
            SpeechTrainingMode.AdaptiveRate => "字/秒",
            SpeechTrainingMode.AdaptiveSnr => "dB",
            _ => "%",
        };
    public string MainResult => Value is { } value ? $"{value:0.##} {Unit}" : "—";
    public bool CanCompare => Complete && Value is not null;
    private bool _canToggle;
    public bool CanToggle => _canToggle;

    internal void UpdateSelectionAvailability(bool value)
    {
        if (SetProperty(ref _canToggle, value, nameof(CanToggle)))
            ToggleCommand.NotifyCanExecuteChanged();
    }

    public string CompareAction => Selected ? "取消对比" : "加入对比";
    public RelayCommand ToggleCommand { get; }

    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            // Validate before committing. The checkbox uses a command with a one-way binding,
            // so a rejected click must explicitly restore its locally toggled visual state.
            if (value && _canSelect?.Invoke(this) == false)
            {
                OnPropertyChanged(nameof(Selected));
                return;
            }
            SetProperty(ref _selected, value);
            OnPropertyChanged(nameof(CompareAction));
            _changed(this);
        }
    }

    public string Key(bool differentDelay)
    {
        var settings = Round.Result.Settings;
        var audio = settings.AudioSettings;
        if (
            audio is not null
            && settings.TrainingMode
                is SpeechTrainingMode.FixedRate
                    or SpeechTrainingMode.AdaptiveRate
                    or SpeechTrainingMode.FixedRir
        )
            audio = audio with { SnrDb = 0, NoiseKind = SpeechNoiseKind.Ssn };
        return SimulationJson.Write(
                settings with
                {
                    SentenceList = "",
                    ResourceStart = 0,
                    ResourceEnd = 0,
                    AudioSettings = audio,
                }
            )
            + $"|{Round.Result.SentenceCount}"
            + (differentDelay ? "" : Delay);
    }

    public string Incompatibility(SpeechRoundRow other, bool differentDelay)
    {
        var a = Round.Result.Settings;
        var b = other.Round.Result.Settings;
        if (a.Corpus != b.Corpus)
            return "语料库不同，不能加入本组对比。";
        if (a.TrainingMode != b.TrainingMode)
            return "训练模式不同，不能混用正确率与不同单位的 SRT。";
        if (
            a.ListSize != b.ListSize
            || Round.Result.SentenceCount != other.Round.Result.SentenceCount
        )
            return "训练句数不同，不能加入本组对比。";
        if (a.Carrier != b.Carrier)
            return "载体不同，不能加入本组对比。";
        if (a.AdaptiveScoring != b.AdaptiveScoring)
            return "评分规则不同，不能加入本组对比。";
        if (Key(true) != other.Key(true))
            return "模式配置不同（语速、声码器、噪声或 RIR 参数），不能加入本组对比。";
        return !differentDelay && Round.DelayMilliseconds != other.Round.DelayMilliseconds
            ? "延时不同；请切换到“不同延时对比”。"
            : "测试条件不一致，请调整选择。";
    }
}

public sealed partial class SpeechListFilter(string label, Action changed) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _selected = true;

    partial void OnSelectedChanged(bool value) => changed();
}
