using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Audio.Core.Playback;
using EGGtCSPlatform.Audio.Sdl;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed partial class PictureNamingPageViewModel : PageViewModel, IDisposable
{
    private enum Phase { Ready, Ramp, Interval, Fixation, Picture, Review, Finished, Detail }
    private static readonly string[][] Sets =
    [
        ["apple", "cup", "key", "umbrella", "clock", "fish"],
        ["scissors", "pencil", "glasses", "book", "spoon", "toothbrush"],
    ];
    public string[] Materials { get; } = ["常见物品 01", "日常用品 02"];
    public string[] Orders { get; } = ["固定顺序", "随机顺序"];
    private string[] _trials = Sets[0];
    private string?[] _scores = new string?[Sets[0].Length];
    private int _index;
    private Phase _phase;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FileSpeechNoiseProvider _noise = new();
    private IAudioPlayer? _player;
    private bool _disposed;
    private string? _audioError;
    private double _audibleVolume = 75;

    public PictureNamingPageViewModel(ExperimentRunRouteData route)
        : base(ApplicationPageNames.PictureNaming, "图片命名训练")
    {
        if (route.CreationMode != ExperimentCreationMode.StimulusOnly || route.StimulusConfiguration.Kind != StimulusKind.TDcs)
            throw new ArgumentException("图片命名需要单刺激 tDCS 配置。", nameof(route));
        HeaderBadges = [new("患者ID", route.SubjectId), new("实验ID", route.ExperimentId),
            PageHeaderBadgeViewModel.CreateExperimentMode(route.CreationMode)];
        CurrentText = StimulusConfigurationSummaryBuilder.CurrentText(route.StimulusConfiguration);
        RampSeconds = route.StimulusConfiguration.RampSeconds;
        RecordSpeech = SpeechAvailable;
        Electrodes = string.Join("   ", route.StimulusElectrodes.Select(e =>
            $"{e.SiteId} · CH{e.PhysicalChannelId}（{StimulationElectrodeRoleResolver.GetLabel(route.StimulusConfiguration.Direction, e.Role)}）"));
    }

    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges { get; }
    public string CurrentText { get; }
    public string Electrodes { get; }
    public double RampSeconds { get; set; }
    private double _rampRemaining;
    public string RampText => IsRamping ? $"{Math.Ceiling(_rampRemaining):0}" : $"{RampSeconds:0.#}";
    public string RampLabel => IsRamping ? "剩余时间" : "缓升时长";
    public double RampProgress => IsRamping && RampSeconds > 0 ? Math.Clamp(1 - _rampRemaining / RampSeconds, 0, 1) * 100 : (ShowIntroArt ? 0 : 100);
    [ObservableProperty] private int _materialIndex;
    [ObservableProperty] private int _orderIndex;
    [ObservableProperty] private double _fixationSeconds = 2;
    [ObservableProperty] private double _answerSeconds = 2;
    [ObservableProperty] private double _intervalMs = 40;
    [ObservableProperty] private bool _interference;
    [ObservableProperty] private double _volume = 75;

    partial void OnVolumeChanged(double value)
    {
        if (_player is not null) _player.Volume = Math.Clamp(value / 100, 0, 1);
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (Volume == 0) Volume = _audibleVolume;
        else { _audibleVolume = Volume; Volume = 0; }
    }

    partial void OnMaterialIndexChanged(int value)
    {
        if (!CanConfigure || value < 0 || value >= Sets.Length) return;
        _trials = Sets[value];
        _scores = new string?[_trials.Length];
        _suggested = new bool[_trials.Length];
        _suggestions = new string?[_trials.Length];
        _reaction = new int?[_trials.Length];
        _correctOnset = new int?[_trials.Length];
        _outcomes = new string?[_trials.Length];
        _hesitated = new bool[_trials.Length];
        _transcripts = new string?[_trials.Length];
        _index = 0;
        _phase = Phase.Ready;
        Refresh();
    }

    private static readonly Dictionary<string,string> Words = new()
    {
        ["apple"]="苹果", ["cup"]="杯子", ["key"]="钥匙", ["umbrella"]="雨伞", ["clock"]="时钟", ["fish"]="鱼",
        ["scissors"]="剪刀", ["pencil"]="铅笔", ["glasses"]="眼镜", ["book"]="书", ["spoon"]="勺子", ["toothbrush"]="牙刷"
    };
    private readonly LocalSpeechResponseRecognizer _recognizer = new();
    private int?[] _reaction = new int?[6];
    private int?[] _correctOnset = new int?[6];
    private string?[] _outcomes = new string?[6];
    private bool[] _hesitated = new bool[6];
    private string?[] _transcripts = new string?[6];
    private bool[] _suggested = new bool[6];
    private string?[] _suggestions = new string?[6];
    private DateTimeOffset _started;
    private int _remaining;
    public ObservableCollection<NamingRound> Rounds { get; } = [];
    [ObservableProperty] private NamingRound? _selectedRound;
    [ObservableProperty] private string? _resultMessage;
    [ObservableProperty] private bool _recordSpeech;
    public bool SpeechAvailable => _recognizer.IsAvailable;
    public string Target => Words[_trials[_index]];
    internal Func<CancellationToken, Task<long?>>? WaitForPicturePresentationAsync { get; set; }
    internal long? PicturePresentedAt { get; private set; }
    public string FirstReaction => FormatReviewOnset(_reaction[_index]);
    public string CorrectReaction => FormatReviewOnset(_correctOnset[_index]);
    public string Recognition => (_transcripts[_index] ?? (RecordSpeech ? "等待本地识别" : "本轮采用人工判分"))
        + (IsSuggested ? " · 已预选，可改判" : "");
    public bool IsSuggested => _suggested[_index] && _scores[_index] is not null;
    public string Selection => _scores[_index] ?? "";
    public bool IsCorrect => Selection == "正确";
    public bool IsWrong => Selection == "错误";
    public bool IsNoResponse => Selection == "无反应";
    public bool IsDetail => _phase == Phase.Detail;
    public bool IsFinished => _phase == Phase.Finished;
    public bool IsFixation => _phase == Phase.Fixation;
    public bool IsRamping => _phase == Phase.Ramp;
    public bool ShowIntroArt => _phase is Phase.Ready or Phase.Ramp;
    public string IntroTitle => IsRamping ? "缓升结束后进入注视阶段" : "准备开始图片命名";
    public bool ShowRampCard => ShowIntroArt || (IsFixation && _index == 0);
    public string RampTitle => _phase switch { Phase.Ready => "刺激缓升", Phase.Ramp => "刺激缓升中", _ => "刺激缓升已结束" };
    public string RampHint => _phase switch
    {
        Phase.Ready => "点击「开始实验」后启动，完成后进入注视阶段。",
        Phase.Ramp => $"电流正在升至目标值，共 {RampSeconds:0.#} 秒，结束后自动进入注视阶段。",
        _ => "完成后进入注视阶段，开始图片命名",
    };
    public bool IsAnswering => _phase == Phase.Picture;
    public bool InExperiment => !ShowIntro && !IsDetail;
    public bool ShowCard => ShowPicture;
    public bool ShowApple => _trials[_index] == "apple";
    public bool ShowDistractors => Interference && IsAnswering;
    public string[] Distractors => ShowApple ? ["梨", "哈密瓜", "火龙果", "西瓜"] :
        new[]{"帽子","木马","铅笔","西瓜","书包"}.Where(word => word != Target).Take(4).ToArray();
    public string SubjectId => HeaderBadges[0].Value;
    public string ExperimentId => HeaderBadges[1].Value;
    public string CardTitle => IsAnswering ? $"回答阶段倒计时：{_remaining} s" : $"目标词：{Target}";
    public string Instruction => IsAnswering ? "请在规定时间内说出图片名称" : "请在下方确认命名结果";
    public string AudioPosition => _player is null ? "00:00/00:00" :
        _player.Position.ToString(@"mm\:ss") + "/" + _player.Duration.ToString(@"mm\:ss");
    public string DeviceStatus => _phase switch { Phase.Ready => "待刺激", Phase.Ramp => "刺激缓升中", _ => "图片呈现 · 未启动刺激" };
    public int TotalCount => Rounds.Count;
    public int CompletedCount => Rounds.Count(r => r.Complete);
    public int InterruptedCount => TotalCount - CompletedCount;
    public string StartedAtText => Rounds.FirstOrDefault()?.Started.ToString("yyyy/MM/dd HH:mm") ?? "—";
    public string RunSummary => $"训练总次数　{Rounds.Count} 次　　有效完成　{Rounds.Count(r => r.Complete)} 次　　中断　{Rounds.Count(r => !r.Complete)} 次";
    public bool CanRestartOrStart => IsFinished || ShowIntro;
    public string Identity => string.Join("　", HeaderBadges.Take(2).Select(b => b.DisplayText));
    public bool CanConfigure => _phase == Phase.Ready;
    public bool CanScore => _phase is Phase.Review or Phase.Finished;
    public bool CanAdvance => CanScore && _scores[_index] is not null;
    public bool ShowIntro => _phase == Phase.Ready;
    public bool ShowPicture => _phase is Phase.Picture or Phase.Review or Phase.Finished;
    public bool ShowPrompt => !ShowIntro && !ShowPicture;
    public string PicturePath => $"avares://EGGtCSPlatform/Assets/Images/picture-naming/{_trials[_index]}.svg";
    public string MaterialName => Materials[Math.Clamp(MaterialIndex, 0, Sets.Length - 1)];
    public string Progress => $"第 {_index + 1:00} / {_trials.Length:00} 张图";
    public int Scored => _scores.Count(x => x is not null);
    public int Correct => _scores.Count(x => x == "正确");
    public int Wrong => _scores.Count(x => x == "错误");
    public int Unanswered => _scores.Count(x => x == "无反应");
    public string CountText => $"{Scored}/{_scores.Length}";
    public string Accuracy => Scored == 0 ? "待判定" : $"{(double)Correct / Scored:P0}";
    public string Status => _phase switch {
        Phase.Ready => "等待开始", Phase.Ramp => "刺激缓升中", Phase.Fixation => "注视点", Phase.Picture => "请口头命名",
        Phase.Review => "核对评分", Phase.Interval => "试次间隔", _ => "已完成"
    };
    public string Prompt => _phase switch {
        Phase.Fixation => "+", Phase.Review => _scores[_index] ?? "请判分",
        Phase.Finished => $"已完成 · 无反应 {Unanswered} 题", _ => ""
    };
    public string ActionLabel => _phase == Phase.Finished ? "再来一次训练" :
        _phase == Phase.Ready ? "开始实验  ▶" : _index == _trials.Length - 1 ? "完成实验" : "下一张";
    public bool CanAct => !_disposed && (CanConfigure || (CanScore && CanAdvance));
    public string? AudioError => _audioError;

    [RelayCommand]
    private async Task AdvanceAsync()
    {
        if (!CanAct) return;
        var token = _lifetime.Token;
        try
        {
            if (CanConfigure || IsFinished)
            {
                if (IsFinished) SaveRound();
                _trials = (string[])Sets[Math.Clamp(MaterialIndex, 0, Sets.Length - 1)].Clone();
                if (OrderIndex == 1) Random.Shared.Shuffle(_trials);
                _scores = new string?[_trials.Length];
                _started = DateTimeOffset.Now;
                _reaction = new int?[_trials.Length];
                _correctOnset = new int?[_trials.Length];
                _outcomes = new string?[_trials.Length];
                _hesitated = new bool[_trials.Length];
                _transcripts = new string?[_trials.Length];
                _suggested = new bool[_trials.Length];
                _suggestions = new string?[_trials.Length];
                _index = 0;
                _audioError = null;
                _rampRemaining = RampSeconds;
                SetPhase(Phase.Ramp);
                var rampClock = Stopwatch.StartNew();
                while (RampSeconds - rampClock.Elapsed.TotalSeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(RampSeconds - rampClock.Elapsed.TotalSeconds, 0, 0.1)), token);
                    _rampRemaining = Math.Max(0, RampSeconds - rampClock.Elapsed.TotalSeconds);
                    OnPropertyChanged(nameof(RampText));
                    OnPropertyChanged(nameof(RampProgress));
                }
                _rampRemaining = 0;
            }
            else
            {
                if (_index == _trials.Length - 1) { SetPhase(Phase.Finished); return; }
                SetPhase(Phase.Interval);
                await Task.Delay(TimeSpan.FromMilliseconds(IntervalMs), token);
                _index++;
            }
            SetPhase(Phase.Fixation);
            await Task.Delay(TimeSpan.FromSeconds(FixationSeconds), token);
            if (Interference && AnswerSeconds > 0)
            {
                _player ??= new SdlAudioPlayer();
                _player.Volume = Volume / 100;
                await _player.PlayAsync(_noise.Read("Babble-MSP.wav").Audio, _index + 1, token);
            }
            using var recorder = AnswerSeconds > 0 ? OpenRecorder() : null;
            var pictureRequestedAt = Stopwatch.GetTimestamp();
            PicturePresentedAt = null;
            _remaining = (int)Math.Ceiling(AnswerSeconds);
            SetPhase(Phase.Picture);
            if (AnswerSeconds > 0 && WaitForPicturePresentationAsync is { } waitForPresentation)
            {
                using var presentation = CancellationTokenSource.CreateLinkedTokenSource(token);
                presentation.CancelAfter(TimeSpan.FromSeconds(AnswerSeconds));
                try { PicturePresentedAt = await waitForPresentation(presentation.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            var answerStartedAt = PicturePresentedAt ?? pictureRequestedAt;
            var window = TimeSpan.FromSeconds(AnswerSeconds);
            for (var remaining = window - Stopwatch.GetElapsedTime(answerStartedAt);
                remaining > TimeSpan.Zero; remaining = window - Stopwatch.GetElapsedTime(answerStartedAt))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 0, 100)), token);
                _remaining = (int)Math.Ceiling(Math.Max(0, (window - Stopwatch.GetElapsedTime(answerStartedAt)).TotalSeconds));
                OnPropertyChanged(nameof(CardTitle));
            }
            _player?.Stop();
            if (recorder is not null)
            {
                try
                {
                    var audio = recorder.Complete();
                    var result = await _recognizer.RecognizeNamingAsync(audio, Target, token);
                    token.ThrowIfCancellationRequested();
                    ApplyNamingSuggestion(result, recorder.CaptureStartedTimestamp, window);
                }
                catch (Exception e) when (!token.IsCancellationRequested) { _transcripts[_index] = $"识别不可用，请人工核对：{e.Message}"; }
            }
            token.ThrowIfCancellationRequested();
            SetPhase(Phase.Review);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _player?.Dispose();
            _player = null;
            _audioError = $"实验中断：{error.Message}";
            SetPhase(Phase.Ready);
        }
    }

    internal void ApplyNamingSuggestion(SpeechNamingSuggestion? result, long captureStartedAt, TimeSpan answerWindow)
    {
        _reaction[_index] = FromPictureOnset(result?.FirstOnsetMs, captureStartedAt, PicturePresentedAt, answerWindow);
        _correctOnset[_index] = FromPictureOnset(result?.CorrectOnsetMs, captureStartedAt, PicturePresentedAt, answerWindow);
        _outcomes[_index] = result?.Outcome;
        _hesitated[_index] = result?.Hesitated == true;
        var suggestion = SuggestedScore(result);
        _transcripts[_index] = result is null ? "未识别到回答，请人工核对" : DescribeNaming(result, suggestion);
        if ((result?.FirstOnsetMs is not null && _reaction[_index] is null)
            || (result?.CorrectOnsetMs is not null && _correctOnset[_index] is null))
            _transcripts[_index] += " · 反应时需人工核对";
        if (suggestion is not null)
        {
            _scores[_index] = suggestion;
            _suggested[_index] = true;
            _suggestions[_index] = suggestion;
        }
        Refresh();
    }

    internal static int? FromPictureOnset(int? onsetMs, long captureStartedAt, long? picturePresentedAt, TimeSpan answerWindow)
    {
        if (onsetMs is not int onset || picturePresentedAt is not long picture) return null;
        var relative = TimeSpan.FromMilliseconds(onset) - Stopwatch.GetElapsedTime(captureStartedAt, picture);
        return relative >= TimeSpan.Zero && relative < answerWindow ? (int)relative.TotalMilliseconds : null;
    }

    private static string FormatReviewOnset(int? onset) => onset is int ms ? $"{ms} ms" : "—";

    [RelayCommand]
    private void Score(string? result)
    {
        if (!CanScore || result is not ("正确" or "错误" or "无反应" or "重置")) return;
        _scores[_index] = result == "重置" ? null : result;
        _suggested[_index] = false;
        if (_index == _trials.Length - 1) _phase = result == "重置" ? Phase.Review : Phase.Finished;
        Refresh();
    }

    /// <summary>
    /// Accuracy follows the first answer: target said first → 正确; said only after another word (自我纠正) or never → 错误.
    /// Anything uncertain stays for the examiner.
    /// </summary>
    internal static string? SuggestedScore(SpeechNamingSuggestion? result) => result?.Outcome switch
    {
        "正确" => "正确",
        "错误" or "自我纠正" => "错误",
        _ => null,
    };

    internal static string DescribeNaming(SpeechNamingSuggestion result, string? suggestion)
    {
        var parts = new List<string> { $"「{result.Transcript}」" };
        if (result.Outcome is "自我纠正" or "待确认") parts.Add(result.Outcome);
        if (result.Hesitated) parts.Add("犹豫");
        parts.Add(suggestion is null ? "待人工确认" : $"建议{suggestion}");
        if (result.ReviewNote is not null) parts.Add(result.ReviewNote);
        return string.Join(" · ", parts);
    }

    /// <summary>Records whether the final score came from the system suggestion or from the examiner.</summary>
    internal static string ScoreSource(string? score, string? suggestion, bool untouched) =>
        score is null ? "—"
        : suggestion is null ? "人工判分"
        : score != suggestion ? $"人工改判（系统建议{suggestion}）"
        : untouched ? "系统预选，未改动" : "系统预选，人工确认";

    private SdlSpeechRecorder? OpenRecorder()
    {
        if (!RecordSpeech || !SpeechAvailable) return null;
        try { return new SdlSpeechRecorder(); }
        catch (Exception e) { _transcripts[_index] = $"麦克风不可用：{e.Message}"; return null; }
    }

    private void SaveRound()
    {
        var rows = _trials.Take(_index + 1).Select((key,i) =>
            new NamingTrial(i+1, Words[key], _scores[i] ?? "未评", _reaction[i], _transcripts[i] ?? "人工判分",
                ScoreSource(_scores[i], _suggestions[i], _suggested[i]), _correctOnset[i], _outcomes[i], _hesitated[i])).ToArray();
        var round = new NamingRound($"第 {Rounds.Count+1} 次 · {MaterialName}", IsFinished, rows, _trials.Length,
            $"{MaterialName}　{Orders[OrderIndex]}　注视 {FixationSeconds} s　回答 {AnswerSeconds} s　间隔 {IntervalMs} ms", _started, DateTimeOffset.Now);
        Rounds.Add(round);
        SelectedRound = round;
    }

    internal Task WriteResultsAsync(Stream stream) =>
        JsonSerializer.SerializeAsync(stream, new { Identity, CurrentText, Electrodes, Rounds });

    [RelayCommand]
    private void End()
    {
        if (!InExperiment) return;
        SaveRound();
        _lifetime.Cancel();
        _player?.Stop();
        PageTitle = "实验结果详情";
        SetPhase(Phase.Detail);
    }
    private void SetPhase(Phase phase) { _phase = phase; Refresh(); }
    private void Refresh() => OnPropertyChanged(string.Empty);

    public override void OnNavigatedFrom() => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _player?.Dispose();
    }
}

/// <summary>ReactionMs is the first real word (filler sounds excluded); CorrectOnsetMs is when the target word was said.</summary>
public sealed record NamingTrial(int Number, string Target, string Score, int? ReactionMs, string Note, string Source = "人工判分",
    int? CorrectOnsetMs = null, string? Outcome = null, bool Hesitated = false)
{
    public string Reaction => ReactionMs is int ms ? $"{ms} ms" : "—";
    public string CorrectReaction => CorrectOnsetMs is int ms ? $"{ms} ms" : "—";
    /// <summary>Only answers that were right the first time count toward the mean reaction time.</summary>
    public bool CountsForMeanReaction => Score == "正确" && ReactionMs is >= 0 && Outcome == "正确";
}
public sealed record NamingRound(string Label, bool Complete, NamingTrial[] Rows, int Planned, string Settings, DateTimeOffset Started, DateTimeOffset Ended)
{
    public override string ToString() => Label;
    public string Count => $"{Scored}/{Planned}";
    public int Scored => Rows.Count(r => r.Score != "未评");
    public string Accuracy => Scored == 0 ? "—" : $"{(double)Rows.Count(r => r.Score == "正确") / Scored:P0}";
    public string Metrics => $"已评图片 {Scored}/{Planned}   平均命名反应时 {MeanReaction}   命名正确率 {Accuracy}" +
        (ExcludedReactions > 0 ? $"   未计入反应时 {ExcludedReactions} 题" : "");
    public string MeanReaction => Rows.Any(r => r.CountsForMeanReaction)
        ? $"{Rows.Where(r => r.CountsForMeanReaction).Average(r => r.ReactionMs):0} ms" : "—";
    /// <summary>Correct answers left out of the mean because they were self-corrected or need review.</summary>
    public int ExcludedReactions => Rows.Count(r => r.Score == "正确" && !r.CountsForMeanReaction);
}
