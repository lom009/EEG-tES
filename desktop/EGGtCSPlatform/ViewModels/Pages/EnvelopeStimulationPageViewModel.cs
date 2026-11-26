using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed partial class EnvelopeStimulationPageViewModel : PageViewModel, IDisposable
{
    private readonly INavigationRouter _router;
    private SpeechTrainingSession? _session;
    private readonly string? _loadError;
    private string? _outputError;
    private readonly Func<string, Task>? _notifyStopFailure;
    private string? _displayedSentence;
    private AudioBuffer? _displayedAudio;
    private bool _disposed;
    private double _lastNonZeroVolume = 100;
    private bool _refreshing;
    private double _voiceMaximumAmplitude = 1;
    private double _stimulusMaximumAmplitude = 2;

    private readonly bool _playheadEnabled;
    public bool ShowPlaybackPlayhead => _playheadEnabled && HasWaveform;
    public bool EnvelopeWaveformRealtimeEnabled { get; }

    public double VoiceMaximumAmplitude => _voiceMaximumAmplitude;
    public double StimulusMaximumAmplitude => _stimulusMaximumAmplitude;

    public void ZoomVoice(double delta) =>
        ZoomAxis(ref _voiceMaximumAmplitude, delta, 0.001, 10, nameof(VoiceMaximumAmplitude));

    public void ZoomStimulus(double delta) =>
        ZoomAxis(ref _stimulusMaximumAmplitude, delta, 0.01, 20, nameof(StimulusMaximumAmplitude));

    private void ZoomAxis(
        ref double range,
        double delta,
        double minimum,
        double maximum,
        string property
    )
    {
        if (_disposed || !double.IsFinite(delta) || delta == 0)
            return;
        var next = Math.Clamp(range * Math.Pow(1.25, -delta), minimum, maximum);
        if (next == range)
            return;
        range = next;
        OnPropertyChanged(property);
    }

    private string? _listedCorpus;
    private string _sentenceList = "";
    private SpeechTrainingResultsRouteData? _resultRoute;
    private IReadOnlyList<TimedWaveformPoint> _voice = [],
        _envelope = [];

    public EnvelopeStimulationPageViewModel(
        EnvelopeStimulationRouteData routeData,
        INavigationRouter router,
        Func<SpeechTrainingSession> createSession,
        Func<ExperimentRunRouteData, ISpeechStimulationOutput>? createOutput = null,
        SpeechOutputLifetime? outputLifetime = null,
        Func<string, Task>? notifyStopFailure = null,
        EGGtCSPlatform.Persistence.ISpeechExperimentService? experiments = null,
        Func<SpeechEndDialogViewModel, Task>? showEndDialog = null,
        DisplayOptions? displayOptions = null
    )
        : base(ApplicationPageNames.EnvelopeStimulation, "单刺激言语训练")
    {
        routeData = routeData with
        {
            Configuration = SpeechResultRoutes.WithEnvelope(
                routeData.Configuration,
                (
                    routeData.Configuration.StimulusConfiguration.Envelope
                    ?? new EnvelopeParameters()
                ) with
                {
                    DebugFrequencyHz = 500,
                }
            ),
        };
        _playheadEnabled = (displayOptions ?? new()).EnvelopePlayheadEnabled;
        EnvelopeWaveformRealtimeEnabled = (displayOptions ?? new()).EnvelopeWaveformRealtimeEnabled;
        RouteData = routeData;
        _createSession = createSession;
        _createOutput = createOutput;
        _outputLifetime = outputLifetime;
        _experiments = experiments;
        _showEndDialog = showEndDialog;
        _router = router;
        _notifyStopFailure = notifyStopFailure;
        var route = routeData.Configuration;
        if (
            route.CreationMode != ExperimentCreationMode.StimulusOnly
            || route.StimulusConfiguration.Kind != StimulusKind.EnvelopeTAcs
        )
            throw new ArgumentException("需要单刺激模式的包络-tACS 配置。", nameof(routeData));
        _headerBadges =
        [
            new("患者ID", route.SubjectId),
            new("实验ID", route.ExperimentId),
            PageHeaderBadgeViewModel.CreateExperimentMode(route.CreationMode),
        ];
        CurrentText = StimulusConfigurationSummaryBuilder.CurrentText(route.StimulusConfiguration);
        Electrodes = string.Join(
            "   ",
            route.StimulusElectrodes.Select(e => $"{e.SiteId} · CH{e.PhysicalChannelId}")
        );
        try
        {
            _session = NewSession();
            if (routeData.Preset is { } preset)
            {
                _session.SelectCorpus(preset.Corpus);
                _session.SelectListSize(preset.ListSize);
                _session.SelectSentenceList(preset.SentenceList);
                _session.ConfigureAudio(preset.AudioSettings ?? new());
                _session.SetScoringRule(preset.AdaptiveScoring);
                if (preset.AudioSettings?.Vocoder is { } vocoder)
                    _vocoderSettings = vocoder;
            }
        }
        catch (Exception exception)
        {
            _loadError =
                $"{(_session is null ? "语料加载失败" : "训练初始化失败")}：{exception.Message}";
            _session?.Dispose();
            _session = null;
        }
        InitializeRirCommands();
        InitializeResponseCommands();
        CorpusOptions = _session?.Corpora.Select(x => x.Id).ToArray() ?? [];
        StartCommand = new AsyncRelayCommand(
            StartRoundAsync,
            () => CanEndExperiment && _session?.CanStart == true
        );
        AgainCommand = new AsyncRelayCommand(AgainAsync, () => CanTrainAgain);
        ReplayCommand = new AsyncRelayCommand(
            async () =>
            {
                ClearResponseReview();
                _outputError = null;
                if (_session is not null)
                {
                    if (_session.ResponsePlaybackId is { } playbackId)
                        _session.ClearResponseSuggestion(playbackId);
                    await _session.ReplayAsync();
                }
                RefreshPlaybackState();
            },
            () => !_disposed && _session?.CanReplay == true
        );
        SaveNextCommand = new AsyncRelayCommand(
            async () =>
            {
                if (ResponseBusy)
                    return;
                try
                {
                    _outputError = null;
                    if (_session is not null)
                        await _session.SaveNextAsync();
                }
                catch (Exception e)
                {
                    _outputError = $"保存失败：{e.Message}";
                }
                Refresh();
            },
            () => !_disposed && !ResponseBusy && _session?.CanSaveNext == true
        );
        EndCommand = new AsyncRelayCommand(RequestEndAsync, () => CanEndExperiment);
        AllCorrectCommand = new RelayCommand(
            () =>
            {
                _session?.RateAll(true);
                Refresh();
            },
            () => CanScore
        );
        AllWrongCommand = new RelayCommand(
            () =>
            {
                _session?.RateAll(false);
                Refresh();
            },
            () => CanScore
        );
        ResetCommand = new RelayCommand(
            () =>
            {
                ClearResponseReview();
                _session?.RateAll(null);
                Refresh();
            },
            () => CanScore
        );
        ShowResultsCommand = new RelayCommand(
            () =>
            {
                if (_experimentEnded)
                    _router.Navigate(
                        new SpeechExperimentResultsRouteData(_experimentId, ExperimentSnapshot())
                    );
            },
            () => _experimentEnded && !_disposed
        );
        MuteVolumeCommand = new RelayCommand(() => VolumePercent = 0, () => CanAdjustVolume);
        RestoreVolumeCommand = new RelayCommand(
            () =>
            {
                if (VolumePercent == 0)
                    VolumePercent = _lastNonZeroVolume;
            },
            () => CanAdjustVolume
        );
        if (VolumePercent > 0)
            _lastNonZeroVolume = VolumePercent;
        ToggleMuteCommand = new RelayCommand(
            () =>
            {
                if (!CanAdjustVolume)
                    return;
                if (IsMuted)
                    RestoreVolumeCommand.Execute(null);
                else
                    MuteVolumeCommand.Execute(null);
            },
            () => CanAdjustVolume
        );
        BackCommand = new RelayCommand(
            () =>
            {
                if (CanGoBack)
                    router.GoBack();
            },
            () => CanGoBack
        );
        HomeCommand = new RelayCommand(router.GoHome);
        if (_session is not null)
        {
            _session.Changed += OnSessionChanged;
            _session.TimelineChanged += OnTimelineChanged;
        }
        Refresh();
    }

    public EnvelopeStimulationRouteData RouteData { get; private set; }
    private IReadOnlyList<PageHeaderBadgeViewModel> _headerBadges = [];
    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges => _headerBadges;

    public override void UpdateExperimentIdentity(
        long previousId,
        long id,
        string code,
        EGGtCSPlatform.MainApp.ExperimentConfigurationTemplate? configuration = null
    )
    {
        if (
            _session?.Started == true
            || _experimentSource is not null
            || RouteData.Configuration.ExperimentDatabaseId != previousId
        )
            return;
        RouteData = RouteData with
        {
            Configuration = RouteData.Configuration with
            {
                ExperimentDatabaseId = id,
                ExperimentId = code,
            },
        };
        _headerBadges =
        [
            new("患者ID", RouteData.Configuration.SubjectId),
            new("实验ID", code),
            PageHeaderBadgeViewModel.CreateExperimentMode(RouteData.Configuration.CreationMode),
        ];
        OnPropertyChanged(nameof(HeaderBadges));
        OnPropertyChanged(nameof(RouteData));
    }

    public IReadOnlyList<string> CorpusOptions { get; }
    public IReadOnlyList<string> SentenceListOptions { get; private set; } = [];
    public IReadOnlyList<int> ListSizeOptions { get; } = [10, 20, 30, 50];
    public int ListSize
    {
        get => _session?.ListSize ?? 20;
        set
        {
            if (
                !_refreshing
                && CanConfigure
                && ListSizeOptions.Contains(value)
                && value != ListSize
            )
            {
                _session?.SelectListSize(value);
                Refresh();
            }
        }
    }
    public string SelectedCorpus
    {
        get => _session?.Corpus.Id ?? "";
        set
        {
            if (
                !_refreshing
                && CanConfigure
                && value != SelectedCorpus
                && CorpusOptions.Contains(value)
            )
            {
                _session?.SelectCorpus(value);
                Refresh();
            }
        }
    }
    public string SentenceList
    {
        get => _sentenceList;
        set
        {
            if (
                !_refreshing
                && CanConfigure
                && value != SentenceList
                && SentenceListOptions.Contains(value)
            )
            {
                _session?.SelectSentenceList(value);
                Refresh();
            }
        }
    }
    public IReadOnlyList<string> ModeOptions { get; } =
    [
        "固定语速测试(安静)",
        "自适应语速测试(安静)",
        "自适应信噪比测试",
        "固定信噪比测试",
        "固定 RIR 测试",
    ];
    public IReadOnlyList<string> CarrierOptions { get; } = ["原始语音", "声码器仿真"];
    public string SelectedMode
    {
        get =>
            ModeOptions[
                IsRirMode ? 4
                : IsFixedSnrMode ? 3
                : IsSnrMode ? 2
                : IsAdaptiveMode ? 1
                : 0
            ];
        set
        {
            if (
                CanConfigure
                && (
                    value == ModeOptions[0]
                    || value == ModeOptions[1]
                    || value == ModeOptions[2]
                    || value == ModeOptions[3]
                    || value == ModeOptions[4]
                )
            )
                _session?.SelectMode(
                    value == ModeOptions[1] ? SpeechTrainingMode.AdaptiveRate
                    : value == ModeOptions[2] ? SpeechTrainingMode.AdaptiveSnr
                    : value == ModeOptions[3] ? SpeechTrainingMode.FixedSnr
                    : value == ModeOptions[4] ? SpeechTrainingMode.FixedRir
                    : SpeechTrainingMode.FixedRate
                );
        }
    }
    public bool IsAdaptiveMode => _session?.Mode == SpeechTrainingMode.AdaptiveRate;
    public bool IsFixedMode => !IsAdaptiveMode && !IsSnrMode && !IsRirMode;
    public bool IsSnrMode => _session?.IsSnrMode == true;
    public bool IsAdaptiveSnrMode => _session?.Mode == SpeechTrainingMode.AdaptiveSnr;
    public bool IsFixedSnrMode => _session?.Mode == SpeechTrainingMode.FixedSnr;
    public bool ShowAdaptiveScoringHint => false;
    public string SnrInputLabel => IsFixedSnrMode ? "固定 SNR" : "初始 SNR";
    public decimal FixedSnrMinimum => decimal.MinValue;
    public decimal FixedSnrMaximum => decimal.MaxValue;
    public decimal FixedSnrDb
    {
        get
        {
            var value = _session?.FixedSnrDb ?? 10;
            // The decimal endpoints round outward when converted to double.
            return value >= (double)decimal.MaxValue ? decimal.MaxValue
                : value <= (double)decimal.MinValue ? decimal.MinValue
                : (decimal)value;
        }
        set
        {
            if (CanConfigure)
                _session?.SetFixedSnr((double)value);
        }
    }
    public bool CanUseAdaptiveSnr => _session?.CanUseAdaptiveSnr == true;
    public string RateLabel =>
        IsRirMode ? "混响"
        : IsSnrMode ? "信噪比"
        : "语速";
    public IReadOnlyList<string> NoiseOptions { get; } =
    ["SSN（言语谱噪声）", "Babble（多人言语噪声）"];
    public string SelectedNoise
    {
        get => NoiseOptions[_session?.NoiseKind == SpeechNoiseKind.Babble ? 1 : 0];
        set
        {
            if (CanConfigure && NoiseOptions.Contains(value))
                _session?.SelectNoise(
                    value == NoiseOptions[1] ? SpeechNoiseKind.Babble : SpeechNoiseKind.Ssn
                );
        }
    }
    public decimal InitialSnrDb
    {
        get => (decimal)(_session?.InitialSnrDb ?? 10);
        set
        {
            if (CanConfigure)
                _session?.SetInitialSnr((double)value);
        }
    }
    public string SnrText =>
        $"{_session?.Waveform?.Preparation?.Noise?.Settings.SnrDb ?? _session?.CurrentSnrDb ?? 10:0.###} dB"
        + (IsFixedSnrMode ? "" : $" · 反转 {_session?.SnrReversals ?? 0}");
    public bool CanUseAdaptiveMode => _session?.CanUseAdaptiveMode == true;
    public string AdaptiveScoringHint =>
        (IsSnrMode ? _session?.SnrUnavailableReason : _session?.AdaptiveUnavailableReason)
        ?? _session?.ScoringRule?.Description
        ?? "自适应评分规则未配置";
    public string AdaptiveRateText =>
        $"{_session?.CurrentTiming?.Multiplier ?? 1 + (_session?.AdaptiveR ?? 0.5):0.###} 倍 · "
        + (
            _session?.CurrentTiming is { } timing
                ? $"目标 {timing.TargetSpeechRate:0.###} 字/s"
                : "目标语速待准备"
        )
        + $" · 反转 {_session?.AdaptiveReversals ?? 0}";
    private VocoderOptions _vocoderSettings = new();
    public bool CanAdjustCarrier => CanConfigure && _session?.CanAdjustCarrier == true;
    public bool IsVocoderEnabled => _session?.AudioSettings.Vocoder is not null;
    public string SelectedCarrier
    {
        get => CarrierOptions[IsVocoderEnabled ? 1 : 0];
        set
        {
            if (CanAdjustCarrier && CarrierOptions.Contains(value))
                _session?.SetVocoder(value == CarrierOptions[1] ? _vocoderSettings : null);
        }
    }
    public IReadOnlyList<string> VocoderAlgorithms { get; } =
    ["经典 CIS", "ACE", "经典 TLE", "ACE-TLE"];
    public IReadOnlyList<string> VocoderCarriers { get; } = ["噪声", "纯音"];
    public string VocoderAlgorithm
    {
        get => VocoderAlgorithms[(int)_vocoderSettings.Algorithm];
        set
        {
            var index = VocoderAlgorithms.ToList().IndexOf(value);
            if (index >= 0)
                UpdateVocoder(_vocoderSettings with { Algorithm = (VocoderAlgorithm)index });
        }
    }
    public string VocoderCarrier
    {
        get => VocoderCarriers[(int)_vocoderSettings.Carrier];
        set
        {
            var index = VocoderCarriers.ToList().IndexOf(value);
            if (index >= 0)
                UpdateVocoder(_vocoderSettings with { Carrier = (VocoderCarrier)index });
        }
    }
    public decimal VocoderBands
    {
        get => _vocoderSettings.Bands;
        set
        {
            var bands = (int)Math.Round(Math.Clamp(value, 1, 22));
            UpdateVocoder(
                _vocoderSettings with
                {
                    Bands = bands,
                    Maxima = Math.Min(bands, _vocoderSettings.Maxima),
                }
            );
        }
    }
    public decimal VocoderMaxima
    {
        get => _vocoderSettings.Maxima;
        set =>
            UpdateVocoder(
                _vocoderSettings with
                {
                    Maxima = (int)Math.Round(Math.Clamp(value, 1, _vocoderSettings.Bands)),
                }
            );
    }
    public bool ShowVocoderMaxima => _vocoderSettings.UsesMaxima;
    public string CarrierHint =>
        _session?.CurrentAudioSettings is { } actual
        && actual.CarrierSummary != _session.AudioSettings.CarrierSummary
            ? $"当前音频：{actual.CarrierSummary}；下次播放：{_session.AudioSettings.CarrierSummary}"
            : "每轮开始前可设置载体，训练中不可修改；声码器默认不改变参考语音包络。";

    private void UpdateVocoder(VocoderOptions settings)
    {
        if (!CanAdjustCarrier || settings == _vocoderSettings)
            return;
        settings.Validate();
        _vocoderSettings = settings;
        if (IsVocoderEnabled)
            _session?.SetVocoder(settings);
        else
            RefreshCarrierProperties();
    }

    private void RefreshCarrierProperties()
    {
        foreach (
            var property in new[]
            {
                nameof(CanAdjustCarrier),
                nameof(IsVocoderEnabled),
                nameof(SelectedCarrier),
                nameof(VocoderAlgorithm),
                nameof(VocoderCarrier),
                nameof(VocoderBands),
                nameof(VocoderMaxima),
                nameof(ShowVocoderMaxima),
                nameof(CarrierHint),
            }
        )
            OnPropertyChanged(property);
    }

    public decimal SpeechRate
    {
        get => _session?.SpeechRate ?? 0;
        set
        {
            if (CanAdjustSpeechRate)
            {
                _session?.SetSpeechRate(
                    Math.Round(Math.Clamp(value, 0, 5), MidpointRounding.AwayFromZero)
                );
                OnPropertyChanged();
            }
        }
    }
    public bool CanAdjustSpeechRate => CanConfigure && _session?.CanAdjustSpeechRate == true;
    public string SpeechRateHint =>
        IsRirMode ? "原速播放；保持房间与反射条件，按逐字评分统计正确率。"
        : IsFixedSnrMode
            ? "原速播放；全句表保持固定 SNR；同句重播保持相同噪声，按逐字评分统计正确率。"
        : IsSnrMode ? "原速播放；完整评分并保存后调整下一句 SNR；同句重播保持相同噪声。"
        : IsAdaptiveMode ? "完整评分并保存后调整下一句倍率；重播不改变倍率。"
        : _session?.CurrentAudioSettings is { } actual && actual.SpeechRate != SpeechRate
            ? $"当前音频：{FormatRate(actual.SpeechRate)}；下次播放：{FormatRate(SpeechRate)}"
        : "0＝原速；每轮开始前可设置，训练中不可修改。";

    private static string FormatRate(decimal rate) => rate == 0 ? "原速" : $"{rate:0} 字/s";

    public string CurrentText { get; }
    public double DelayMilliseconds
    {
        get => RouteData.Configuration.StimulusConfiguration.Envelope?.DelayMilliseconds ?? 0;
        set
        {
            if (!CanAdjustStimulation || !double.IsFinite(value))
                return;
            var delay = Math.Round(Math.Clamp(value, 0, 300), MidpointRounding.AwayFromZero);
            if (delay == DelayMilliseconds)
                return;

            var route = RouteData.Configuration;
            var configuration = route.StimulusConfiguration with
            {
                Envelope = (route.StimulusConfiguration.Envelope ?? new EnvelopeParameters()) with
                {
                    DelayMilliseconds = delay,
                },
            };
            // Rebuild the immutable route through its constructor to preserve snapshot freezing.
            RouteData = RouteData with
            {
                Configuration = new ExperimentRunRouteData(
                    route.ExperimentId,
                    route.SubjectId,
                    configuration,
                    route.StimulusElectrodes,
                    route.AcquisitionChannels,
                    route.ReferenceChannel,
                    route.GroundChannel,
                    route.SampleRateHz,
                    route.DeviceId,
                    route.ExperimentDatabaseId,
                    route.ScheduledAt,
                    route.Remarks,
                    route.ImportedTiming,
                    route.HistoricalResult,
                    route.PhysicalChannelNames,
                    route.PreRunImpedance,
                    route.CreationMode
                ),
            };
            OnPropertyChanged(nameof(RouteData));
            OnPropertyChanged(nameof(DelayMilliseconds));
            OnPropertyChanged(nameof(DelayText));
            OnPropertyChanged(nameof(Waveform));
            RebuildStimulusPreview();
        }
    }
    public int DebugFrequencyHz => 500;
    public bool CanAdjustStimulation => CanConfigure && _session?.CanAdjustStimulation == true;

    private async void RebuildStimulusPreview()
    {
        try
        {
            if (_session is not null)
                await _session.ConfigureStimulationAsync(
                    DeviceEnvelopeOutput.Settings(RouteData.Configuration)
                );
        }
        catch (Exception e)
        {
            _outputError = e.Message;
        }
        Refresh();
    }

    public string DelayText => $"{DelayMilliseconds / 1000:0.###} s";
    public string Electrodes { get; }
    public WaveformDescriptor Waveform =>
        StimulusConfigurationSummaryBuilder.Waveform(RouteData.Configuration.StimulusConfiguration);
    public ObservableCollection<SpeechWordViewModel> Words { get; } = [];
    public IReadOnlyList<TimedWaveformPoint> VoiceSamples => _voice;
    public IReadOnlyList<TimedWaveformPoint> VoiceEnvelope => _envelope;
    public SpeechStimulusPreview? StimulusPreview =>
        _session?.IsPreparingStimulus == true
            ? SpeechStimulusPreview.Empty
            : _session?.StimulusPlan?.Preview ?? _session?.Waveform?.Stimulus;
    public double PreviewCurrentScale => _session?.StimulusPlan is null ? Waveform.Current : 1;
    public double DisplayDurationSeconds =>
        _session?.StimulusPlan?.DisplayDurationSeconds ?? AudioDurationSeconds;
    public double AudioDurationSeconds =>
        _session?.Duration.TotalSeconds is > 0 ? _session.Duration.TotalSeconds : 1;
    public SpeechTimelineAnimation? TimelineAnimation => _session?.TimelineAnimation;
    public double PlaybackProgress =>
        _session is { Duration.Ticks: > 0 } session
            ? Math.Clamp(session.TimelinePosition.TotalSeconds / DisplayDurationSeconds, 0d, 1d)
            : 0d;
    public bool HasWaveform => _displayedAudio is not null;
    public bool CanConfigure => CanEndExperiment && _session?.CanConfigure == true;
    public bool CanScore =>
        !_disposed && !_closing && !_experimentEnded && _session?.CanScore == true;
    public bool IsPlaying => _session?.IsPlaying == true;
    public bool IsEnded => _session?.Ended == true;
    public bool ShowRunActions => _session is { Started: true, Ended: false };
    public string CorpusLabel =>
        _session is null
            ? "语料不可用"
            : $"{SelectedCorpus} · 每表 {ListSize} 句 · 句表 {SentenceList}";
    public string SentencePosition =>
        _session is null
            ? "无可用句子"
            : $"第 {_session.SentenceIndex + 1:00} / {_session.SentenceList.Sentences.Count:00} 句";
    public string PlaybackText =>
        $"{(_session?.IsLoading == true ? "加载中" : "音频")}   {FormatTime(_session?.Position ?? TimeSpan.Zero)}/{FormatTime(_session?.Duration ?? TimeSpan.Zero)}";

    private static string FormatTime(TimeSpan time) =>
        $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    public string RatedText => $"{Words.Count(w => w.IsCorrect.HasValue)}/{Words.Count}";
    public int CorrectCount => Words.Count(w => w.IsCorrect == true);
    public int WrongCount => Words.Count(w => w.IsCorrect == false);
    public string AccuracyText =>
        Words.Any(w => w.IsCorrect.HasValue)
            ? $"{100d * CorrectCount / Words.Count(w => w.IsCorrect.HasValue):0}%"
            : "待判定";
    public double VolumePercent
    {
        get => (_session?.Volume ?? 1) * 100;
        set
        {
            if (_session is not null && !_disposed)
            {
                _session.Volume = value / 100;
                if (_session.VolumeError is null && VolumePercent > 0)
                    _lastNonZeroVolume = VolumePercent;
            }
        }
    }
    public string VolumeText => $"{VolumePercent:0}%";
    public bool IsMuted => VolumePercent == 0;
    public string MuteActionText => IsMuted ? "恢复音量" : "静音";
    public bool CanAdjustVolume => _session is not null && !_disposed;

    public string StatusText =>
        _loadError
        ?? _outputError
        ?? _session?.PersistenceError
        ?? _session?.Error
        ?? _session?.VolumeError
        ?? (_session?.IsPreparingStimulus == true ? "正在准备刺激预览" : null)
        ?? (_session?.IsOutputBusy == true ? _session.OutputStatus : null)
        ?? (
            IsRirMode ? _session?.RirUnavailableReason
            : IsFixedSnrMode ? _session?.FixedSnrUnavailableReason
            : IsSnrMode ? _session?.SnrUnavailableReason
            : IsAdaptiveMode ? _session?.AdaptiveUnavailableReason
            : null
        )
        ?? (
            IsEnded ? RoundCompletionText
            : _session?.IsLoading == true ? "正在加载题目音频"
            : _session?.Started != true ? "选择语料库和句表后按当前设置播放音频"
            : IsPlaying ? "播放中，可同步评分；评完可保存并进入下一句"
            : !CanScore ? "播放已中断，请重播本句"
            : "等待回答与评分后，可保存并进入下一句"
        );
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand ReplayCommand { get; }
    public AsyncRelayCommand SaveNextCommand { get; }
    public RelayCommand MuteVolumeCommand { get; }
    public RelayCommand RestoreVolumeCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public AsyncRelayCommand EndCommand { get; }
    public RelayCommand AllCorrectCommand { get; }
    public RelayCommand AllWrongCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ShowResultsCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand HomeCommand { get; }

    private void OnSessionChanged(object? sender, EventArgs args)
    {
        if (args is SpeechPlaybackStateChangedEventArgs)
        {
            QueuePlaybackStateRefresh();
            return;
        }
        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    private int _playbackStateRefreshPending;

    private void QueuePlaybackStateRefresh()
    {
        if (System.Threading.Interlocked.Exchange(ref _playbackStateRefreshPending, 1) != 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            System.Threading.Interlocked.Exchange(ref _playbackStateRefreshPending, 0);
            RefreshPlaybackState();
        });
    }

    private void RefreshPlaybackState()
    {
        if (_disposed)
            return;
        // New waveform/sentence data needs one full publication. Playback updates
        // only touch state and actions, leaving the expensive plots unchanged.
        if (!ReferenceEquals(_displayedAudio, _session?.Waveform?.Audio)
            || _displayedSentence != _session?.CurrentSentence.Id)
        {
            Refresh();
            return;
        }
        foreach (var name in new[]
        {
            nameof(IsPlaying), nameof(CanScore), nameof(CanConfigure),
            nameof(CanAdjustStimulation), nameof(CanAdjustSpeechRate), nameof(CanAdjustCarrier),
            nameof(CanGoBack), nameof(CanEndExperiment), nameof(StatusText),
            nameof(PlaybackText), nameof(TimelineAnimation),
        })
            OnPropertyChanged(name);
        if (TimelineAnimation is null)
            OnPropertyChanged(nameof(PlaybackProgress));
        NotifyResponseState();
        foreach (IRelayCommand command in new IRelayCommand[]
        {
            StartCommand, ReplayCommand, SaveNextCommand, EndCommand, BackCommand,
            AllCorrectCommand, AllWrongCommand, ResetCommand,
        })
            command.NotifyCanExecuteChanged();
        foreach (var word in Words)
            word.RefreshCommands();
    }

    private int _timelineRefreshPending;
    private SpeechTimelineAnimation? _presentedTimelineAnimation;

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        if (System.Threading.Interlocked.Exchange(ref _timelineRefreshPending, 1) != 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            System.Threading.Interlocked.Exchange(ref _timelineRefreshPending, 0);
            if (!_disposed)
            {
                var animation = TimelineAnimation;
                if (!ReferenceEquals(animation, _presentedTimelineAnimation))
                {
                    _presentedTimelineAnimation = animation;
                    OnPropertyChanged(nameof(TimelineAnimation));
                }
                if (animation is null)
                    OnPropertyChanged(nameof(PlaybackProgress));
                OnPropertyChanged(nameof(PlaybackText));
            }
        });
    }

    private void Refresh()
    {
        if (_disposed || _refreshing)
            return;
        _refreshing = true;
        try
        {
            RefreshCore();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshCore()
    {
        if (_session is not null)
        {
            if (_listedCorpus != $"{_session.Corpus.Id}/{_session.ListSize}")
            {
                // Clear the old selection before replacing ItemsSource. Avalonia otherwise
                // may clear the newly selected item while reconciling the previous list.
                _sentenceList = "";
                OnPropertyChanged(nameof(SentenceList));
                _listedCorpus = $"{_session.Corpus.Id}/{_session.ListSize}";
                SentenceListOptions = _session.Lists.Select(x => x.Id).ToArray();
                OnPropertyChanged(nameof(SentenceListOptions));
            }
            _sentenceList = _session.SentenceList.Id;
            if (_displayedSentence != _session.CurrentSentence.Id)
            {
                ClearResponseReview();
                _displayedSentence = _session.CurrentSentence.Id;
                Words.Clear();
                foreach (
                    var (text, index) in _session.CurrentSentence.ScoringCharacters.Select(
                        (text, index) => (text, index)
                    )
                )
                    Words.Add(
                        new SpeechWordViewModel(
                            text,
                            () => CanScore,
                            correct =>
                            {
                                _session.Rate(index, correct);
                                Refresh();
                            }
                        )
                    );
            }
            var answers = _session.Answers;
            for (var i = 0; i < Words.Count; i++)
                Words[i].Update(answers[i]);
            var prepared = _session.Waveform;
            if (!ReferenceEquals(_displayedAudio, prepared?.Audio))
            {
                _displayedAudio = prepared?.Audio;
                var buckets = prepared?.Voice ?? [];
                _voice = buckets
                    .SelectMany(p =>
                        new[]
                        {
                            new TimedWaveformPoint(p.Seconds, p.Minimum, false),
                            new TimedWaveformPoint(p.Seconds, p.Maximum, false),
                        }
                    )
                    .ToArray();
                var envelope = prepared?.Envelope ?? [];
                _envelope = envelope
                    .Select(p => new TimedWaveformPoint(p.Seconds, p.Amplitude, false))
                    .Concat(
                        envelope.Select(
                            (p, i) => new TimedWaveformPoint(p.Seconds, -p.Amplitude, i == 0)
                        )
                    )
                    .ToArray();
            }
        }
        RefreshCarrierProperties();
        foreach (
            var name in new[]
            {
                nameof(SelectedCorpus),
                nameof(SentenceList),
                nameof(ListSize),
                nameof(CorpusLabel),
                nameof(CanConfigure),
                nameof(CanEndExperiment),
                nameof(CanGoBack),
                nameof(CanTrainAgain),
                nameof(CanAdjustSpeechRate),
                nameof(SpeechRate),
                nameof(SpeechRateHint),
                nameof(SelectedMode),
                nameof(IsRirMode),
                nameof(RirConditionText),
                nameof(RirStatus),
                nameof(RirPreview),
                nameof(IsGeneratingRir),
                nameof(CanCancelRir),
                nameof(IsAdaptiveMode),
                nameof(IsFixedMode),
                nameof(IsSnrMode),
                nameof(IsAdaptiveSnrMode),
                nameof(IsFixedSnrMode),
                nameof(ShowAdaptiveScoringHint),
                nameof(SnrInputLabel),
                nameof(FixedSnrDb),
                nameof(CanUseAdaptiveSnr),
                nameof(RateLabel),
                nameof(SelectedNoise),
                nameof(InitialSnrDb),
                nameof(SnrText),
                nameof(CanUseAdaptiveMode),
                nameof(AdaptiveScoringHint),
                nameof(AdaptiveRateText),
                nameof(CanScore),
                nameof(IsPlaying),
                nameof(IsEnded),
                nameof(ShowRunActions),
                nameof(HasWaveform),
                nameof(ShowPlaybackPlayhead),
                nameof(VoiceSamples),
                nameof(VoiceEnvelope),
                nameof(StimulusPreview),
                nameof(PreviewCurrentScale),
                nameof(DisplayDurationSeconds),
                nameof(CanAdjustStimulation),
                nameof(AudioDurationSeconds),
                nameof(PlaybackProgress),
                nameof(TimelineAnimation),
                nameof(SentencePosition),
                nameof(PlaybackText),
                nameof(RatedText),
                nameof(CorrectCount),
                nameof(WrongCount),
                nameof(AccuracyText),
                nameof(StatusText),
                nameof(VolumePercent),
                nameof(VolumeText),
                nameof(IsMuted),
                nameof(MuteActionText),
                nameof(CanAdjustVolume),
            }
        )
            OnPropertyChanged(name);
        foreach (
            IRelayCommand command in new IRelayCommand[]
            {
                GenerateRirCommand,
                CancelRirCommand,
                MuteVolumeCommand,
                RestoreVolumeCommand,
                ToggleMuteCommand,
                StartCommand,
                ReplayCommand,
                SaveNextCommand,
                EndCommand,
                AgainCommand,
                AllCorrectCommand,
                AllWrongCommand,
                ResetCommand,
                ShowResultsCommand,
                BackCommand,
            }
        )
            command.NotifyCanExecuteChanged();
        foreach (var word in Words)
            word.RefreshCommands();
        NotifyResponseState();
        if (_session?.Result is { } result && _resultRoute is null)
        {
            _resultRoute = new(
                RouteData,
                new(
                    result.Settings.Corpus,
                    result.Settings.SentenceList,
                    result.Settings.Mode,
                    result.Settings.Carrier,
                    result.Settings.SpeechRate,
                    result.Settings.ListSize,
                    result.Settings.ResourceStart,
                    result.Settings.ResourceEnd,
                    result.Settings.AudioSettings,
                    result.Settings.TrainingMode,
                    result.Settings.AdaptiveScoring,
                    result.Settings.InitialSnrDb,
                    result.Settings.FixedSnrDb,
                    result.Settings.Rir
                ),
                result.StartedAt,
                result.SentenceCount,
                result
                    .Sentences.Select(x => new MainApp.SpeechSentenceResult(
                        x.Number,
                        x.Text,
                        x.Correct,
                        x.Total,
                        x.SpeechRate,
                        x.AudioSnapshot,
                        Adaptive: x.Adaptive,
                        PlaybackCompleted: x.PlaybackCompleted,
                        AdaptiveSnr: x.AdaptiveSnr,
                        Noise: x.Noise,
                        Rir: x.Rir
                    ))
                    .ToArray(),
                result.AdaptiveSummary,
                result.Playbacks,
                result.SnrSummary
            );
            // The experiment remains open between training rounds.
        }
    }

    public override void OnNavigatedFrom()
    {
        ClearResponseReview();
        _session?.Stop();
    }

    public override bool RequiresAsyncNavigation => true;

    public override async Task<bool> PrepareToLeaveAsync()
    {
        if (_experimentEnded)
            return true;
        var stopped = await FinishAsync(
            true,
            EGGtCSPlatform.Persistence.ExperimentRunStatus.InterruptedByExit
        );
        if (!stopped && _notifyStopFailure is not null)
            await _notifyStopFailure(StatusText);
        return stopped;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ClearResponseReview();
        NotifyResponseState();
        OnPropertyChanged(nameof(CanGoBack));
        BackCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAdjustCarrier));
        OnPropertyChanged(nameof(CanAdjustSpeechRate));
        OnPropertyChanged(nameof(CanAdjustVolume));
        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
            _session.TimelineChanged -= OnTimelineChanged;
            _session.Dispose();
        }
        foreach (
            IRelayCommand command in new IRelayCommand[]
            {
                GenerateRirCommand,
                CancelRirCommand,
                MuteVolumeCommand,
                RestoreVolumeCommand,
                ToggleMuteCommand,
                StartCommand,
                ReplayCommand,
                SaveNextCommand,
                EndCommand,
                AgainCommand,
                AllCorrectCommand,
                AllWrongCommand,
                ResetCommand,
                ShowResultsCommand,
            }
        )
            command.NotifyCanExecuteChanged();
        foreach (var word in Words)
            word.RefreshCommands();
    }
}

public sealed class SpeechWordViewModel : ViewModelBase
{
    private bool? _isCorrect;

    public SpeechWordViewModel(string text, Func<bool> canRate, Action<bool?> rate)
    {
        Text = text;
        CorrectCommand = new RelayCommand(() => rate(true), canRate);
        WrongCommand = new RelayCommand(() => rate(false), canRate);
    }

    public string Text { get; }
    public bool? IsCorrect => _isCorrect;
    public bool IsCorrectScore => _isCorrect == true;
    public bool IsIncorrectScore => _isCorrect == false;
    public string Accent =>
        _isCorrect switch
        {
            true => "#38A169",
            false => "#E53E3E",
            _ => "#A9BAD4",
        };
    public string CorrectColor => _isCorrect == true ? "#38A169" : "#6B7D99";
    public string WrongColor => _isCorrect == false ? "#E53E3E" : "#6B7D99";
    public RelayCommand CorrectCommand { get; }
    public RelayCommand WrongCommand { get; }

    internal void Update(bool? value)
    {
        if (_isCorrect == value)
            return;
        _isCorrect = value;
        OnPropertyChanged(nameof(IsCorrect));
        OnPropertyChanged(nameof(IsCorrectScore));
        OnPropertyChanged(nameof(IsIncorrectScore));
        OnPropertyChanged(nameof(Accent));
        OnPropertyChanged(nameof(CorrectColor));
        OnPropertyChanged(nameof(WrongColor));
    }

    internal void RefreshCommands()
    {
        CorrectCommand.NotifyCanExecuteChanged();
        WrongCommand.NotifyCanExecuteChanged();
    }
}
