using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

/// <summary>Native MVVM training flow. Device/audio and persistence are injected services.</summary>
public sealed partial class EnvelopeStimulationPageViewModel : PageViewModel, IDisposable
{
    private readonly IEnvelopeTrainingExecutionService? _execution;
    private readonly IEnvelopeTrainingResultStore _store;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _operation;
    private EnvelopeTrainingAudio? _audio;
    private bool _disposed, _saving, _stopUnconfirmed;
    private int _sentenceIndex, _interruptedTrials, _detailIndex;
    private DateTimeOffset? _completedAt, _endedAt;
    private EnvelopeTrainingStage _stage;
    private decimal _speed = 2;
    private EnvelopeTrainingCorpus _corpus = EnvelopeTrainingCorpus.Demo;
    private EnvelopeTrainingSentenceTable _table = EnvelopeTrainingCorpus.Demo.Tables[0];

    public EnvelopeStimulationPageViewModel(EnvelopeStimulationRouteData routeData, INavigationRouter router,
        IEnvelopeTrainingExecutionService? execution = null, IEnvelopeTrainingResultStore? store = null)
        : base(ApplicationPageNames.EnvelopeStimulation, "言语训练")
    {
        RouteData = routeData;
        if (routeData.Configuration.CreationMode != ExperimentCreationMode.StimulusOnly
            || routeData.Configuration.StimulusConfiguration.Kind != StimulusKind.EnvelopeTAcs)
            throw new ArgumentException("需要单刺激模式的包络-tACS 配置。", nameof(routeData));
        _execution = execution;
        _store = store ?? new EnvelopeTrainingResultStore();
        var route = routeData.Configuration;
        HeaderBadges = [new("患者ID", route.SubjectId), new("实验ID", route.ExperimentId),
            PageHeaderBadgeViewModel.CreateExperimentMode(route.CreationMode)];
        BackCommand = new RelayCommand(() => router.GoBack(), () => CanGoBack);
        HomeCommand = new RelayCommand(() => router.GoHome(), () => IsResults);
        PlayAndStimulateCommand = new AsyncRelayCommand(() => ExecuteAsync(true), () => CanStart);
        ReplayAudioCommand = new AsyncRelayCommand(() => ExecuteAsync(false), () => CanReplay);
        StopCommand = new RelayCommand(() => _operation?.Cancel(), () => IsExecuting);
        SetAllCorrectCommand = new RelayCommand(() => SetAll(true), () => CanScore);
        SetAllIncorrectCommand = new RelayCommand(() => SetAll(false), () => CanScore);
        ResetScoresCommand = new RelayCommand(() => SetAll(null), () => CanScore);
        SaveTrialCommand = new AsyncRelayCommand(SaveTrialAsync, () => CanSave);
        NextSentenceCommand = new RelayCommand(NextSentence, () => CanAdvance);
        RequestEndCommand = new RelayCommand(() => IsEndConfirmationOpen = true, () => !IsExecuting && !_saving && !IsResults && !StopUnconfirmed);
        RetryStopCommand = new AsyncRelayCommand(RetryStopAsync, () => StopUnconfirmed && !_saving && !IsExecuting);
        CancelEndCommand = new RelayCommand(() => IsEndConfirmationOpen = false, () => !_saving);
        ConfirmEndCommand = new AsyncRelayCommand(EndAsync, () => !_saving && !IsExecuting);
        ShowDetailCommand = new RelayCommand<EnvelopeTrainingTrial>(ShowDetail);
        CloseDetailCommand = new RelayCommand(() => IsDetailOpen = false);
        PreviousDetailCommand = new RelayCommand(() => SetDetail(_detailIndex - 1), () => _detailIndex > 0);
        NextDetailCommand = new RelayCommand(() => SetDetail(_detailIndex + 1), () => _detailIndex + 1 < SavedTrials.Count);
        ResetSentence();
    }

    public EnvelopeStimulationRouteData RouteData { get; }
    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges { get; }
    public override bool CanGoBack => !IsExecuting && !_saving && Stage == EnvelopeTrainingStage.Ready && SavedTrials.Count == 0 && _interruptedTrials == 0;
    public double MaximumCurrent => RouteData.Configuration.StimulusConfiguration.Targets.Sum(target => target.PeakCurrent);
    public string CurrentText => $"{MaximumCurrent:0.##} mA";
    public double DelaySeconds => (RouteData.Configuration.StimulusConfiguration.Envelope?.DelayMilliseconds ?? 0) / 1000;
    public string DelayText => $"{DelaySeconds * 1000:0} ms";
    public string Electrodes => string.Join("\n", RouteData.Configuration.StimulusElectrodes.Select(assignment =>
        $"{assignment.SiteId} · CH{assignment.PhysicalChannelId} · {StimulationElectrodeRoleResolver.GetChannelRoleLabel(assignment.Role)}"));
    public string Impedances => DetectionStatus;
    public string DetectionStatus => RouteData.DetectionStatus;
    public string CorpusText => $"{_corpus.Name} · 句表 {_table.Name}";
    public string VoiceText => _corpus.Voice;
    public ObservableCollection<EnvelopeTrainingCorpus> Corpora { get; } = [EnvelopeTrainingCorpus.Demo];
    public EnvelopeTrainingCorpus SelectedCorpus
    {
        get => _corpus;
        set
        {
            if (!CanEditCorpus || value is null || !SetProperty(ref _corpus, value)) return;
            _table = value.Tables[0]; _sentenceIndex = 0;
            OnPropertyChanged(nameof(SentenceTables)); OnPropertyChanged(nameof(SelectedSentenceTable));
            OnPropertyChanged(nameof(VoiceText)); ResetSentence();
        }
    }
    public IReadOnlyList<EnvelopeTrainingSentenceTable> SentenceTables => _corpus.Tables;
    public EnvelopeTrainingSentenceTable SelectedSentenceTable
    {
        get => _table;
        set
        {
            if (!CanEditCorpus || value is null || !SetProperty(ref _table, value)) return;
            _sentenceIndex = 0; ResetSentence();
        }
    }
    public bool CanEditCorpus => CanEditSettings && SavedTrials.Count == 0;
    public async Task ImportCorpusAsync(string path)
    {
        if (!CanEditCorpus) return;
        try
        {
            var corpus = await EnvelopeTrainingCorpus.LoadAsync(path);
            if (!CanEditCorpus || _disposed) return;
            Corpora.Add(corpus); SelectedCorpus = corpus;
        }
        catch (Exception exception) { SetStatus($"语料导入失败：{exception.Message}", true); }
    }
    public string Sentence => _table.Sentences[_sentenceIndex].Text;
    public int CharacterColumns => Math.Min(8, Characters.Count);
    public int DetailCharacterColumns => Math.Min(7, DetailCharacters.Count);
    public bool HasSavedTrials => SavedTrials.Count > 0;
    public string ResultCorpusName => _corpus.Name;
    public string ResultTableName => _table.Name;
    public string ResultSpeedText
    {
        get
        {
            if (SavedTrials.Count == 0) return "未执行";
            var speeds = SavedTrials.Select(trial => trial.CharactersPerSecond).Distinct().Order().ToArray();
            return speeds.Length == 1 ? $"{speeds[0]:0.##} 字/s"
                : $"{speeds[0]:0.##}–{speeds[^1]:0.##} 字/s（逐句调整）";
        }
    }
    public string ResultScoredText => $"{SavedTrials.Sum(trial => trial.Characters.Count)}/{SavedTrials.Sum(trial => trial.Characters.Count)}";
    public string ResultCorrectCountText => SavedTrials.Sum(trial => trial.CorrectCount).ToString();
    public string ExperimentIdText => RouteData.Configuration.ExperimentId;
    public string SubjectIdText => RouteData.Configuration.SubjectId;
    public string StartedAtText => _startedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string InterruptionsText => _interruptedTrials.ToString();
    public string CurrentSentenceNumberText => $"{_sentenceIndex + 1:00}";
    public string SentenceTotalText => $"/{_table.Sentences.Count} 句";
    public string SentenceCounter => $"第 {_sentenceIndex + 1:00}/{_table.Sentences.Count} 句";
    public ObservableCollection<EnvelopeTrainingCharacterViewModel> Characters { get; } = [];
    public ObservableCollection<EnvelopeTrainingTrial> SavedTrials { get; } = [];
    public ObservableCollection<EnvelopeTrainingCharacterViewModel> DetailCharacters { get; } = [];
    public EnvelopeTrainingAudio? Audio => _audio;
    public decimal CharactersPerSecond
    {
        get => _speed;
        set
        {
            if (!CanEditSettings || value is < .5m or > 5m || !SetProperty(ref _speed, value)) return;
            ResetSentence();
        }
    }
    public EnvelopeTrainingStage Stage => _stage;
    public bool IsResults => Stage == EnvelopeTrainingStage.Results;
    public bool ShowEndAction => Stage != EnvelopeTrainingStage.Ready || SavedTrials.Count > 0;
    public bool IsExecuting => _operation is not null;
    public bool CanEditSettings => !StopUnconfirmed && !IsExecuting && !_saving && Stage is EnvelopeTrainingStage.Ready or EnvelopeTrainingStage.Aborted;
    public bool StopUnconfirmed => _stopUnconfirmed;
    public bool CanStart => !StopUnconfirmed && CanEditSettings && _execution is not null && _audio is not null;
    public bool CanScore => !IsExecuting && !_saving && Stage == EnvelopeTrainingStage.Scoring;
    public bool CanReplay => _execution is not null && !StopUnconfirmed && !IsExecuting && !_saving && _audio is not null && Stage is EnvelopeTrainingStage.Scoring or EnvelopeTrainingStage.Saved;
    public bool CanSave => CanScore && Characters.All(character => character.IsCorrect.HasValue);
    public bool CanAdvance => !IsExecuting && !_saving && Stage == EnvelopeTrainingStage.Saved && _sentenceIndex + 1 < _table.Sentences.Count;
    public string ScoredCountText => $"{Characters.Count(character => character.IsCorrect.HasValue)}/{Characters.Count}";
    public string CorrectCountText => Characters.Count(character => character.IsCorrect == true).ToString();
    public string IncorrectCountText => Characters.Count(character => character.IsCorrect == false).ToString();
    public string AccuracyValueText => Characters.All(character => character.IsCorrect.HasValue)
        ? $"{(double)Characters.Count(character => character.IsCorrect == true) / Characters.Count:P0}" : "待判定";
    public string ScoredText => $"已评 {Characters.Count(character => character.IsCorrect.HasValue)}/{Characters.Count}";
    public string CorrectText => $"正确 {Characters.Count(character => character.IsCorrect == true)}";
    public string IncorrectText => $"错误 {Characters.Count(character => character.IsCorrect == false)}";
    public string AccuracyText => Characters.All(character => character.IsCorrect.HasValue)
        ? $"正确率 {(double)Characters.Count(character => character.IsCorrect == true) / Characters.Count:P0}" : "正确率 待判定";
    // The three primary instructions match Figma. Transient operations take priority over the stage.
    public string Instruction => StopUnconfirmed ? "设备停止未确认，请先重试停止"
        : IsExecuting ? Stage == EnvelopeTrainingStage.Running ? "请等待音频和刺激结束" : "正在重复播放题目音频，请等待播放结束"
        : _saving ? "正在保存本句评分，请稍候"
        : Stage switch
        {
            EnvelopeTrainingStage.Scoring => "等待回答与评分后，可保存并进入下一句",
            EnvelopeTrainingStage.Saved => _sentenceIndex + 1 < _table.Sentences.Count
                ? "本句已保存，可进入下一句" : "本句已保存，句表训练已完成，可结束实验",
            EnvelopeTrainingStage.Aborted => "本句已中断，请重新播放并刺激",
            _ => "先进行题目音频播放并刺激"
        };
    public string AudioTimeText => $"音频 {FormatTime(AudioSeconds)} / {FormatTime(_audio?.Duration.TotalSeconds ?? 0)}";
    public string EndMessage => $"已保存 {SavedTrials.Count} 句。" +
        (Stage == EnvelopeTrainingStage.Saved ? "本句已保存。" : "当前句未保存，将不计入结果。") + "确定结束本次实验？";
    public string ResultProgressText => $"{SavedTrials.Count}/{_table.Sentences.Count}";
    public string ResultCorrectText => $"{SavedTrials.Sum(trial => trial.CorrectCount)}/{SavedTrials.Sum(trial => trial.Characters.Count)}";
    public string ResultAccuracyText => SavedTrials.Count == 0 ? "—" :
        $"{(double)SavedTrials.Sum(trial => trial.CorrectCount) / SavedTrials.Sum(trial => trial.Characters.Count):P0}";
    public string ResultSessionText => $"{RouteData.Configuration.ExperimentId}\n患者 {RouteData.Configuration.SubjectId}\n{_startedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n中断次数 {_interruptedTrials}";
    public string DetailTitle => SavedTrials.Count == 0 ? "单句评分详情" : $"第 {SavedTrials[_detailIndex].SentenceNumber:00}/{_table.Sentences.Count} 句";

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusMessageIsError;
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
    public void SetStatus(string message, bool isError = false)
    { StatusMessageIsError = isError; StatusMessage = message; }
    [ObservableProperty] private bool _isEndConfirmationOpen;
    [ObservableProperty] private bool _isDetailOpen;
    [ObservableProperty] private double _audioSeconds;
    [ObservableProperty] private double _stimulationSeconds;
    partial void OnAudioSecondsChanged(double value) => OnPropertyChanged(nameof(AudioTimeText));
    public RelayCommand BackCommand { get; }
    public RelayCommand HomeCommand { get; }
    public AsyncRelayCommand PlayAndStimulateCommand { get; }
    public AsyncRelayCommand ReplayAudioCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand RetryStopCommand { get; }
    public RelayCommand SetAllCorrectCommand { get; }
    public RelayCommand SetAllIncorrectCommand { get; }
    public RelayCommand ResetScoresCommand { get; }
    public AsyncRelayCommand SaveTrialCommand { get; }
    public RelayCommand NextSentenceCommand { get; }
    public RelayCommand RequestEndCommand { get; }
    public RelayCommand CancelEndCommand { get; }
    public AsyncRelayCommand ConfirmEndCommand { get; }
    public RelayCommand<EnvelopeTrainingTrial> ShowDetailCommand { get; }
    public RelayCommand CloseDetailCommand { get; }
    public RelayCommand PreviousDetailCommand { get; }
    public RelayCommand NextDetailCommand { get; }

    private async Task ExecuteAsync(bool stimulate)
    {
        if (_execution is null || _audio is null || (stimulate ? !CanStart : !CanReplay)) return;
        var previousStage = Stage;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        if (stimulate) { _stage = EnvelopeTrainingStage.Running; _completedAt = null; }
        SetStatus(""); AudioSeconds = 0;
        if (stimulate) StimulationSeconds = 0;
        Refresh();
        try
        {
            var audio = _audio;
            await _execution.ExecuteAsync(RouteData.Configuration, audio, stimulate,
                new InlineProgress(progress =>
                {
                    void Update()
                    {
                        if (_disposed || !ReferenceEquals(_operation, operation)) return;
                        AudioSeconds = progress.AudioSeconds;
                        if (stimulate) StimulationSeconds = progress.StimulationSeconds;
                    }
                    if (Dispatcher.UIThread.CheckAccess()) Update(); else Dispatcher.UIThread.Post(Update);
                }), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            AudioSeconds = audio.Duration.TotalSeconds;
            if (stimulate)
            {
                StimulationSeconds = (double)audio.EnvelopeSamples.Length / audio.EnvelopeSampleRateHz + DelaySeconds;
                _stage = EnvelopeTrainingStage.Scoring;
                _completedAt = DateTimeOffset.UtcNow;
            }
            else _stage = previousStage;
        }
        catch (OperationCanceledException)
        {
            SetStatus(stimulate ? "音频与刺激已停止，本句未完成。" : "音频重播已停止。");
            _stage = stimulate ? EnvelopeTrainingStage.Aborted : previousStage;
            if (stimulate) _interruptedTrials++;
        }
        catch (Exception exception)
        {
            if (exception is EnvelopeTrainingStopUnconfirmedException) _stopUnconfirmed = true;
            SetStatus($"执行失败：{exception.Message}", true);
            _stage = stimulate ? EnvelopeTrainingStage.Aborted : previousStage;
            if (stimulate) _interruptedTrials++;
        }
        finally { _operation = null; Refresh(); }
    }
    private async Task SaveTrialAsync()
    {
        if (!CanSave || _audio is null || _completedAt is null) return;
        _saving = true; Refresh();
        var trial = new EnvelopeTrainingTrial(_sentenceIndex + 1, Sentence, CorpusText, (double)_speed,
            _completedAt.Value, _audio.Duration.TotalSeconds, _audio.Sha256,
            Characters.Select(character => new EnvelopeTrainingCharacterScore(character.Text, character.IsCorrect!.Value)).ToArray());
        try
        {
            await _store.SaveAsync(CreateSession(SavedTrials.Append(trial).ToArray()));
            SavedTrials.Add(trial); _stage = EnvelopeTrainingStage.Saved;
            SetStatus("本句评分已保存。");
        }
        catch (Exception exception) { SetStatus($"保存失败：{exception.Message}。请重试。", true); }
        finally { _saving = false; Refresh(); }
    }
    private void NextSentence()
    {
        if (!CanAdvance) return;
        _sentenceIndex++; ResetSentence();
    }
    private void ResetSentence()
    {
        _stage = EnvelopeTrainingStage.Ready; _completedAt = null; _audio = null;
        SetStatus(""); AudioSeconds = 0; StimulationSeconds = 0;
        Characters.Clear();
        foreach (var character in Sentence.EnumerateRunes())
            Characters.Add(new(character.ToString(), () => CanScore, Refresh));
        try
        {
            if (_corpus.Directory is null) _audio = EnvelopeTrainingAudio.LoadDemo(_sentenceIndex, (double)_speed, MaximumCurrent);
            else
            {
                using var source = File.OpenRead(Path.Combine(_corpus.Directory, _table.Sentences[_sentenceIndex].AudioFile!));
                _audio = EnvelopeTrainingAudio.Prepare(source, Sentence, (double)_speed, MaximumCurrent);
            }
        }
        catch (Exception exception) { SetStatus($"音频准备失败：{exception.Message}", true); }
        Refresh();
    }
    private void SetAll(bool? value)
    {
        if (!CanScore) return;
        foreach (var character in Characters) character.SetScore(value);
        Refresh();
    }
    private async Task RetryStopAsync()
    {
        if (!StopUnconfirmed || _execution is null) return;
        _saving = true; Refresh();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _execution.ConfirmStoppedAsync(RouteData.Configuration, timeout.Token);
            _stopUnconfirmed = false; SetStatus("设备已确认停止，请重新执行本句。");
        }
        catch (Exception exception) { SetStatus($"停止仍未确认：{exception.Message}", true); }
        finally { _saving = false; Refresh(); }
    }
    private async Task EndAsync()
    {
        if (IsExecuting || _saving || StopUnconfirmed) return;
        _saving = true; Refresh();
        try
        {
            var endedAt = DateTimeOffset.UtcNow;
            await _store.SaveAsync(CreateSession(SavedTrials.ToArray()) with { EndedAtUtc = endedAt });
            _endedAt = endedAt; _stage = EnvelopeTrainingStage.Results; PageTitle = "实验结果";
            IsEndConfirmationOpen = false; SetStatus("");
        }
        catch (Exception exception) { SetStatus($"结束记录保存失败：{exception.Message}", true); }
        finally { _saving = false; Refresh(); }
    }
    public EnvelopeTrainingSession CreateSession(IReadOnlyList<EnvelopeTrainingTrial>? trials = null) =>
        new(_sessionId, _startedAt, _endedAt, new(RouteData.Configuration.ExperimentId,
            RouteData.Configuration.SubjectId, RouteData.Configuration.DeviceId, RouteData.Configuration.ExperimentDatabaseId,
            RouteData.Configuration.StimulusConfiguration, RouteData.Configuration.StimulusElectrodes,
            RouteData.Configuration.PreRunImpedance, DetectionStatus), trials ?? SavedTrials.ToArray(), _interruptedTrials);
    public string CreateSnapshotJson() => JsonSerializer.Serialize(CreateSession(), EnvelopeTrainingResultStore.JsonOptions);
    private void ShowDetail(EnvelopeTrainingTrial? trial)
    {
        if (!IsResults || trial is null) return;
        var index = SavedTrials.IndexOf(trial);
        if (index < 0) return;
        SetDetail(index); IsDetailOpen = true;
    }
    private void SetDetail(int index)
    {
        if (index < 0 || index >= SavedTrials.Count) return;
        _detailIndex = index; DetailCharacters.Clear();
        foreach (var character in SavedTrials[index].Characters)
        {
            var item = new EnvelopeTrainingCharacterViewModel(character.Character, () => false, () => { });
            item.SetScore(character.Correct); DetailCharacters.Add(item);
        }
        OnPropertyChanged(nameof(DetailTitle)); OnPropertyChanged(nameof(DetailCharacterColumns));
        PreviousDetailCommand.NotifyCanExecuteChanged(); NextDetailCommand.NotifyCanExecuteChanged();
    }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(Stage), nameof(CanGoBack), nameof(ShowEndAction), nameof(StopUnconfirmed), nameof(IsExecuting), nameof(IsResults), nameof(CanEditSettings),
            nameof(CanScore), nameof(CanStart), nameof(CanReplay), nameof(CanSave), nameof(CanAdvance), nameof(Instruction),
            nameof(ScoredCountText), nameof(CorrectCountText), nameof(IncorrectCountText), nameof(AccuracyValueText), nameof(CurrentSentenceNumberText), nameof(SentenceTotalText), nameof(ScoredText), nameof(CorrectText), nameof(IncorrectText), nameof(AccuracyText), nameof(SentenceCounter),
            nameof(CharacterColumns), nameof(HasSavedTrials), nameof(ResultCorpusName), nameof(ResultTableName), nameof(ResultSpeedText), nameof(ResultScoredText), nameof(ResultCorrectCountText), nameof(InterruptionsText), nameof(Audio), nameof(CorpusText), nameof(CanEditCorpus), nameof(AudioTimeText), nameof(EndMessage), nameof(ResultProgressText), nameof(ResultCorrectText),
            nameof(ResultAccuracyText), nameof(ResultSessionText) }) OnPropertyChanged(name);
        PlayAndStimulateCommand.NotifyCanExecuteChanged(); ReplayAudioCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged(); RetryStopCommand.NotifyCanExecuteChanged(); SetAllCorrectCommand.NotifyCanExecuteChanged();
        SetAllIncorrectCommand.NotifyCanExecuteChanged(); ResetScoresCommand.NotifyCanExecuteChanged();
        SaveTrialCommand.NotifyCanExecuteChanged(); NextSentenceCommand.NotifyCanExecuteChanged();
        RequestEndCommand.NotifyCanExecuteChanged(); CancelEndCommand.NotifyCanExecuteChanged();
        ConfirmEndCommand.NotifyCanExecuteChanged(); HomeCommand.NotifyCanExecuteChanged(); BackCommand.NotifyCanExecuteChanged();
        foreach (var character in Characters) character.RefreshCommands();
    }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.f");
    public void Dispose() { _disposed = true; _operation?.Cancel(); }
    private sealed class InlineProgress(Action<EnvelopeTrainingProgress> report) : IProgress<EnvelopeTrainingProgress>
    { public void Report(EnvelopeTrainingProgress value) => report(value); }
}
