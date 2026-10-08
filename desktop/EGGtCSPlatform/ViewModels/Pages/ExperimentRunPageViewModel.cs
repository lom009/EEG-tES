using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public partial class ExperimentRunPageViewModel : PageViewModel, IDisposable
{
    private const double PlaceholderTimelineSpanSeconds = 5d;
    private const int WaveformPointsPerVisibleWindow = 1200;
    private const int WaveformPreviewPointsPerVisibleWindow = 240;
    private static readonly TimeSpan TimelineResizeDebounceDelay = TimeSpan.FromMilliseconds(120);
    private static readonly string[] WaveformColors = ["#9536F3", "#FD5B38", "#3686FF", "#38A169"];
    private static readonly string[] WaveformBackgrounds = ["#FFFFFF", "#F5F7FA"];
    private static readonly EegFilterOption[] HighPassFilters =
    [
        new("关闭", null),
        new("0.1 Hz", 0.1d),
        new("0.5 Hz", 0.5d),
        new("1.0 Hz", 1d),
    ];
    private static readonly EegFilterOption[] LowPassFilters =
    [
        new("关闭", null),
        new("30 Hz", 30d),
        new("70 Hz", 70d),
        new("100 Hz", 100d),
    ];
    private static readonly EegFilterOption[] NotchFilters =
    [
        new("关闭", null),
        new("50 Hz", 50d),
        new("60 Hz", 60d),
    ];
    private readonly INavigationRouter _router;
    private readonly IExperimentRunService _runService;
    private readonly IExperimentRunClock _clock;
    private readonly ExperimentRunTimingOptions _timingOptions;
    private readonly IExperimentRunErrorDialogService? _errorDialogService;
    private readonly IEegHistoryWindowProvider? _historyWindowProvider;
    private readonly IReadOnlyDictionary<int, string> _rawPacketChannelNames;
    private readonly IExperimentRunPersistenceCoordinator? _persistence;
    private readonly IEegExportService? _exportService;
    private readonly DialogService? _dialogs;
    private readonly IDeviceSelectionContext? _deviceSelection;
    private readonly ExperimentRunTelemetryBuffer _telemetryBuffer = new();
    private readonly IReadOnlyDictionary<string, WaveformChannelViewModel> _waveformChannelsById;
    private readonly Dictionary<string, CausalEegDisplayFilter> _displayFilters = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, int> _displayFilterSegments = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, double> _displayFilterLastSampleTimes = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<
        string,
        List<(WaveformChannelBatch Batch, int SegmentId)>
    > _rawWaveformHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer? _telemetryFlushTimer;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _filterRebuildCancellation;
    private CancellationTokenSource? _historyWindowCancellation;
    private CancellationTokenSource? _waveformDetailRefreshCancellation;
    private CancellationTokenSource? _playbackCancellation;
    private long _filterRebuildGeneration;
    private long _historyWindowGeneration;
    private long _activeHistoryRequestDataVersion = -1;
    private long _rawDataVersion;
    private bool _filterRebuildInProgress;
    private double _visibleSnapshotStart = double.NaN;
    private double _visibleSnapshotEnd = double.NaN;
    private double _visibleSnapshotPointsPerSecond;
    private EegDisplayFilterSettings? _visibleSnapshotFilterSettings;
    private Guid _visibleSnapshotRecordingId;
    private bool _disposed;
    private bool _suppressWaveformRefresh;
    private bool _waveformRefreshScheduled;
    private bool _timelineViewportRefreshScheduled;
    private bool _deferWaveformRefresh;
    private bool _isManualFinalAcquisitionReady;
    private bool _isFinalAcquisitionRunning;
    private int _finalAcquisitionSegmentId;
    private int _lastTelemetryCycle;
    private DateTimeOffset? _latestEegReceivedAtUtc;
    private long _latestEegReceivedTimestamp;
    private Guid _recordingId;
    private bool _recordingActive;
    private int _persistenceTransitionFailure;
    private bool _missingRawFileDialogShown;
    private bool _restoringHistoricalReviewMode;
    private readonly object _historyPreviewGate = new();
    private HistoricalPreviewWork? _pendingHistoricalPreview;
    private bool _historicalPreviewWorkerRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualMode))]
    [NotifyPropertyChangedFor(nameof(IsAutomaticMode))]
    private ExperimentRunMode _selectedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimingSetup))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsEmergencyStopped))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsResults))]
    [NotifyPropertyChangedFor(nameof(IsModeLocked))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(IsStandardFooterVisible))]
    [NotifyPropertyChangedFor(nameof(IsCompletionActionVisible))]
    [NotifyCanExecuteChangedFor(nameof(RerunExperimentCommand))]
    private ExperimentRunPageState _pageState = ExperimentRunPageState.TimingSetup;

    [ObservableProperty]
    private ExperimentRunStage _currentStage = ExperimentRunStage.Standby;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStageMonitorText))]
    private int _cycleCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStageMonitorText))]
    private int _currentCycle;

    [ObservableProperty]
    private double _runProgress;

    [ObservableProperty]
    private double _actualCurrentMilliAmps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AverageImpedanceMonitorText))]
    private double _averageImpedanceKiloOhms = 3.9d;

    [ObservableProperty]
    private TimeSpan _elapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommunicationStatusText))]
    private bool _communicationHealthy = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommunicationStatusText))]
    private bool _isWaveformDisplayLagging;

    [ObservableProperty]
    private double _eegDataFreshnessMilliseconds;

    [ObservableProperty]
    private double _eegDecodeLatencyMilliseconds;

    [ObservableProperty]
    private double _eegModelLatencyMilliseconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommunicationStatusText))]
    [NotifyPropertyChangedFor(nameof(PacketQualitySummaryText))]
    [NotifyPropertyChangedFor(nameof(PacketQualityDetailText))]
    [NotifyPropertyChangedFor(nameof(ResultAcquisitionItems))]
    private EegPacketStatistics? _packetStatistics;

    [ObservableProperty]
    private bool _isHistoricalWaveformLoading;

    [ObservableProperty]
    private string _historicalWaveformErrorText = string.Empty;

    [ObservableProperty]
    private decimal _displayRangeMicrovolts = 200m;

    [ObservableProperty]
    private int _timeRangeSeconds = 10;

    [ObservableProperty]
    private EegFilterOption _selectedHighPassFilter = HighPassFilters[2];

    [ObservableProperty]
    private EegFilterOption _selectedLowPassFilter = LowPassFilters[2];

    [ObservableProperty]
    private EegFilterOption _selectedNotchFilter = NotchFilters[0];

    [ObservableProperty]
    private bool _showXCursor = true;

    [ObservableProperty]
    private bool _showYCursor = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleColumnLayout))]
    [NotifyPropertyChangedFor(nameof(IsDoubleColumnLayout))]
    [NotifyPropertyChangedFor(nameof(IsComb32Layout))]
    private WaveformLeadLayout _leadLayout;

    [ObservableProperty]
    private WaveformLayoutOption? _selectedLeadLayoutOption;

    [ObservableProperty]
    private bool _isAllChannelsViewOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WaveformDataAvailableThrough))]
    private double _timelineMaximum = PlaceholderTimelineSpanSeconds;

    [ObservableProperty]
    private double _timelineViewStart;

    [ObservableProperty]
    private double _timelineViewEnd = PlaceholderTimelineSpanSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WaveformDataAvailableThrough))]
    private double _timelineLivePosition;

    [ObservableProperty]
    private bool _isFollowingLatest = true;

    [ObservableProperty]
    private bool _isTimelineDragging;

    [ObservableProperty]
    private bool _exportEdf = true;

    [ObservableProperty]
    private bool _exportCsv = true;

    [ObservableProperty]
    private bool _exportExpp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RerunExperimentCommand))]
    private bool _isExporting;

    [ObservableProperty]
    private double _exportProgress;

    [ObservableProperty]
    private string _exportStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackActionText))]
    private bool _isPlaybackActive;

    [ObservableProperty]
    private double _playbackSpeed = 1d;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultOverviewItems))]
    private DateTimeOffset? _experimentStartedAt;

    public ExperimentRunPageViewModel(
        ExperimentRunRouteData routeData,
        INavigationRouter router,
        IExperimentRunService runService,
        IExperimentRunClock? clock = null,
        ExperimentRunTimingOptions? timingOptions = null,
        IExperimentRunErrorDialogService? errorDialogService = null,
        IEegHistoryWindowProvider? historyWindowProvider = null,
        IEegPhysicalChannelMappingService? eegChannelMappings = null,
        IExperimentRunPersistenceCoordinator? persistence = null,
        IEegExportService? exportService = null,
        DialogService? dialogs = null,
        IDeviceSelectionContext? deviceSelection = null,
        DisplayOptions? displayOptions = null
    )
        : base(ApplicationPageNames.ExperimentRun, "时序与运行")
    {
        RouteData = routeData;
        StimulationConfigurationItems = IsAcquisitionOnly
            ? []
            : StimulusConfigurationSummaryBuilder.Build(routeData);
        ConfigurationImpedance = PreRunImpedanceSummaryCalculator.Calculate(routeData);
        _router = router;
        _runService = runService;
        _clock = clock ?? new SystemExperimentRunClock();
        _timingOptions = timingOptions ?? new ExperimentRunTimingOptions();
        _errorDialogService = errorDialogService;
        _historyWindowProvider = historyWindowProvider;
        _persistence = persistence;
        _exportService = exportService;
        _dialogs = dialogs;
        _deviceSelection = deviceSelection;
        WaveformStrokeThickness = DisplayOptions.NormalizeWaveformStrokeThickness(
            displayOptions?.WaveformStrokeThickness ?? DisplayOptions.DefaultWaveformStrokeThickness
        );
        _rawPacketChannelNames =
            routeData.PhysicalChannelNames
            ?? routeData.HistoricalResult?.PhysicalChannelNames
            ?? eegChannelMappings
                ?.Load()
                .Mappings.Where(mapping =>
                    mapping.PhysicalChannel.HasValue
                    && routeData.AcquisitionChannels.Contains(
                        mapping.ElectrodeId,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                .ToDictionary(
                    mapping => mapping.PhysicalChannel!.Value,
                    mapping => mapping.ElectrodeId
                )
            ?? new Dictionary<int, string>();
        if (routeData.HistoricalResult is null)
            _runService.TelemetryReceived += OnTelemetryReceived;
        HeaderBadges =
        [
            new PageHeaderBadgeViewModel("患者ID", routeData.SubjectId),
            new PageHeaderBadgeViewModel("实验ID", routeData.ExperimentId),
            PageHeaderBadgeViewModel.CreateExperimentMode(routeData.CreationMode),
        ];
        DurationUnits =
        [
            new DurationUnitOption(ExperimentDurationUnit.Milliseconds, "ms"),
            new DurationUnitOption(ExperimentDurationUnit.Seconds, "s"),
            new DurationUnitOption(ExperimentDurationUnit.Minutes, "mins"),
        ];
        LeadLayoutOptions =
        [
            new WaveformLayoutOption(WaveformLeadLayout.SingleColumn, "单列"),
            new WaveformLayoutOption(WaveformLeadLayout.DoubleColumn, "双列"),
            new WaveformLayoutOption(WaveformLeadLayout.Comb32, "8×4"),
        ];
        SelectedLeadLayoutOption = LeadLayoutOptions[0];
        var seconds = DurationUnits[1];
        ExperimentStageViewModel[] allStages =
        [
            new ExperimentStageViewModel(
                1,
                ExperimentRunStage.Acquisition,
                "采集",
                seconds,
                true,
                _timingOptions.DurationStepMilliseconds,
                _timingOptions.DurationMaximumMilliseconds,
                _timingOptions.DurationMinimumMilliseconds
            )
            {
                Units = DurationUnits,
            },
            new ExperimentStageViewModel(
                2,
                ExperimentRunStage.Blanking,
                "消隐",
                seconds,
                false,
                _timingOptions.DurationStepMilliseconds,
                _timingOptions.DurationMaximumMilliseconds,
                _timingOptions.DurationMinimumMilliseconds
            )
            {
                Units = DurationUnits,
            },
            new ExperimentStageViewModel(
                3,
                ExperimentRunStage.Stimulation,
                "刺激",
                seconds,
                true,
                _timingOptions.DurationStepMilliseconds,
                _timingOptions.DurationMaximumMilliseconds,
                _timingOptions.DurationMinimumMilliseconds
            )
            {
                Units = DurationUnits,
            },
            new ExperimentStageViewModel(
                4,
                ExperimentRunStage.Recovery,
                "恢复",
                seconds,
                false,
                _timingOptions.DurationStepMilliseconds,
                _timingOptions.DurationMaximumMilliseconds,
                _timingOptions.DurationMinimumMilliseconds
            )
            {
                Units = DurationUnits,
            },
        ];
        AcquisitionStage = allStages.Single(stage => stage.Stage == ExperimentRunStage.Acquisition);
        BlankingStage = allStages.Single(stage => stage.Stage == ExperimentRunStage.Blanking);
        StimulationStage = allStages.Single(stage => stage.Stage == ExperimentRunStage.Stimulation);
        RecoveryStage = allStages.Single(stage => stage.Stage == ExperimentRunStage.Recovery);
        Stages = new(
            allStages.Where(stage =>
                !IsAcquisitionOnly
                || stage.Stage is ExperimentRunStage.Acquisition or ExperimentRunStage.Blanking
            )
        );
        BlankingStage.SetDurationMilliseconds(_timingOptions.BlankingDurationMilliseconds);
        RecoveryStage.SetDurationMilliseconds(_timingOptions.RecoveryDurationMilliseconds);
        TimelineSegments = [];
        foreach (var stage in Stages)
            stage.PropertyChanged += OnStagePropertyChanged;

        WaveformHover = new WaveformHoverState();
        WaveformChannels = new ObservableCollection<WaveformChannelViewModel>(
            routeData.AcquisitionChannels.Select(
                (channel, index) =>
                    new WaveformChannelViewModel(
                        channel,
                        WaveformColors[index % WaveformColors.Length],
                        WaveformBackgrounds[index % WaveformBackgrounds.Length],
                        routeData.SampleRateHz,
                        isEven: (index + 1) % 2 == 0
                    )
                    {
                        ShowAmplitudeUnit = index == 0,
                    }
            )
        );
        _waveformChannelsById = WaveformChannels.ToDictionary(
            channel => channel.ChannelId,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var channel in WaveformChannels)
        {
            _displayFilters[channel.ChannelId] = new CausalEegDisplayFilter(
                routeData.SampleRateHz,
                DisplayFilterSettings
            );
            _rawWaveformHistory[channel.ChannelId] = [];
        }
        if (Application.Current is not null && routeData.HistoricalResult is null)
        {
            _telemetryFlushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1d / _timingOptions.WaveformRefreshRateFps),
            };
            _telemetryFlushTimer.Tick += OnTelemetryFlushTimerTick;
            _telemetryFlushTimer.Start();
        }
        Exceptions = [];
        if (routeData.ImportedTiming is { } importedTiming)
        {
            SelectedMode = importedTiming.Mode;
            CycleCount = importedTiming.CycleCount;
            SetDurationMilliseconds(AcquisitionStage, importedTiming.AcquisitionMilliseconds);
            SetDurationMilliseconds(BlankingStage, importedTiming.BlankingMilliseconds);
            SetDurationMilliseconds(StimulationStage, importedTiming.StimulationMilliseconds);
            SetDurationMilliseconds(RecoveryStage, importedTiming.RecoveryMilliseconds);
        }
        RefreshValidationAndCommands();
        if (routeData.HistoricalResult is { } historical)
            InitializeHistoricalResult(historical);
    }

    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges { get; }

    public override object? HeaderAction => IsHistoricalResult ? null : this;

    public override bool CanGoBack =>
        IsTimingSetup || IsEmergencyStopped || IsCompleted || IsResults;

    public ExperimentRunRouteData RouteData { get; }

    public double WaveformStrokeThickness { get; }

    public bool IsHistoricalResult => RouteData.HistoricalResult is not null;

    public double WaveformDataAvailableThrough =>
        IsHistoricalResult ? TimelineMaximum : TimelineLivePosition;

    public IReadOnlyList<double> PlaybackSpeedOptions { get; } = [0.5d, 1d, 2d];

    public string PlaybackActionText => IsPlaybackActive ? "暂停" : "播放";

    public IReadOnlyList<DurationUnitOption> DurationUnits { get; }

    public ObservableCollection<ExperimentStageViewModel> Stages { get; }
    public ExperimentStageViewModel AcquisitionStage { get; }
    public ExperimentStageViewModel BlankingStage { get; }
    public ExperimentStageViewModel StimulationStage { get; }
    public ExperimentStageViewModel RecoveryStage { get; }
    public bool IsAcquisitionOnly =>
        RouteData.CreationMode == ExperimentCreationMode.AcquisitionOnly;
    public Avalonia.Controls.GridLength StimulusSeparatorHeight => new(IsAcquisitionOnly ? 0 : 15);
    public Avalonia.Controls.GridLength StimulusTitleHeight => new(IsAcquisitionOnly ? 0 : 25);
    public Avalonia.Controls.GridLength StimulusDetailsHeight =>
        IsAcquisitionOnly ? new(0) : new(0.5, Avalonia.Controls.GridUnitType.Star);

    private TimeSpan? GetStageDuration(ExperimentStageViewModel stage) =>
        IsAcquisitionOnly
        && stage.Stage is ExperimentRunStage.Stimulation or ExperimentRunStage.Recovery
            ? TimeSpan.Zero
            : stage.GetDuration();

    public ObservableCollection<WaveformChannelViewModel> WaveformChannels { get; }

    public WaveformHoverState WaveformHover { get; }

    public ObservableCollection<ExperimentTimelineSegment> TimelineSegments { get; }

    public ObservableCollection<ExperimentExceptionViewModel> Exceptions { get; }

    public IReadOnlyList<decimal> DisplayRangeOptions { get; } =
    [20m, 50m, 100m, 200m, 500m, 1000m, 2000m];

    public IReadOnlyList<int> TimeRangeOptions { get; } = [5, 10, 30, 60];

    public IReadOnlyList<EegFilterOption> HighPassOptions { get; } = HighPassFilters;

    public IReadOnlyList<EegFilterOption> LowPassOptions { get; } = LowPassFilters;

    public IReadOnlyList<EegFilterOption> NotchOptions { get; } = NotchFilters;

    public EegDisplayFilterSettings DisplayFilterSettings =>
        new(
            SelectedHighPassFilter.FrequencyHz,
            SelectedLowPassFilter.FrequencyHz,
            SelectedNotchFilter.FrequencyHz
        );

    public IReadOnlyList<WaveformLayoutOption> LeadLayoutOptions { get; }

    public bool IsManualMode => SelectedMode == ExperimentRunMode.Manual;

    public bool IsAutomaticMode => SelectedMode == ExperimentRunMode.Automatic;

    public bool IsTimingSetup => PageState == ExperimentRunPageState.TimingSetup;

    public bool IsRunning => PageState == ExperimentRunPageState.Running;

    public bool IsEmergencyStopped => PageState == ExperimentRunPageState.EmergencyStopped;

    public bool IsCompleted => PageState == ExperimentRunPageState.Completed;

    public bool IsResults => PageState == ExperimentRunPageState.Results;

    public bool IsModeLocked => !IsTimingSetup;

    public bool IsCycleCountVisible => IsAutomaticMode && IsTimingSetup;

    public bool IsManualStimulusReady =>
        !IsAcquisitionOnly
        && IsManualMode
        && IsRunning
        && CurrentStage == ExperimentRunStage.Standby
        && BlankingStage.Status == ExperimentStageStatus.Completed
        && StimulationStage.Status == ExperimentStageStatus.Pending;

    public bool IsManualFinalAcquisitionReady =>
        !IsAcquisitionOnly && IsManualMode && IsRunning && _isManualFinalAcquisitionReady;

    public bool IsPrimaryActionVisible =>
        IsTimingSetup || IsManualStimulusReady || IsManualFinalAcquisitionReady;

    public bool IsEmergencyOnlyFooter => IsRunning && !IsPrimaryActionVisible;

    public bool IsEmergencyActionVisible => true;

    public bool IsEmergencyActionEnabled => CanEmergencyStop();

    public bool IsStandardFooterVisible => !IsCompleted;

    public bool IsCompletionActionVisible => IsCompleted;

    public string PrimaryActionText =>
        IsAutomaticMode ? "开始自动运行"
        : IsManualFinalAcquisitionReady ? "开始末次采集"
        : IsManualStimulusReady ? "开始刺激"
        : "开始采集";

    public string FooterPrimaryText =>
        IsEmergencyStopped ? "保存当前数据"
        : IsCompleted ? "结束实验并查看结果"
        : IsRunning && !IsManualStimulusReady && !IsManualFinalAcquisitionReady ? "运行中"
        : PrimaryActionText;

    public bool IsFooterPrimaryEnabled => CanExecuteFooterPrimary();

    public bool IsSingleColumnLayout => LeadLayout == WaveformLeadLayout.SingleColumn;

    public bool IsDoubleColumnLayout => LeadLayout == WaveformLeadLayout.DoubleColumn;

    public bool IsComb32Layout => LeadLayout == WaveformLeadLayout.Comb32;

    public bool HasWaveformData =>
        WaveformChannels.Any(item => item.HistoryDurationSeconds > 0d || item.Samples.Length > 0);

    public bool ShowWaveformStartHint =>
        !IsHistoricalResult
        && IsTimingSetup
        && ExperimentStartedAt is null
        && !HasWaveformData
        && !IsHistoricalWaveformLoading
        && string.IsNullOrWhiteSpace(HistoricalWaveformErrorText);

    public bool ShowHistoricalWaveformError =>
        !IsHistoricalWaveformLoading && !string.IsNullOrWhiteSpace(HistoricalWaveformErrorText);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (
            e.PropertyName
            is nameof(PageState)
                or nameof(ExperimentStartedAt)
                or nameof(HasWaveformData)
                or nameof(IsHistoricalWaveformLoading)
                or nameof(HistoricalWaveformErrorText)
        )
            OnPropertyChanged(nameof(ShowWaveformStartHint));
        if (
            e.PropertyName
            is nameof(IsHistoricalWaveformLoading)
                or nameof(HistoricalWaveformErrorText)
        )
            OnPropertyChanged(nameof(ShowHistoricalWaveformError));
    }

    public bool IsStimulating => CurrentStage == ExperimentRunStage.Stimulation;

    public double PlannedCycleDurationSeconds =>
        GetCycleDuration()?.TotalSeconds ?? PlaceholderTimelineSpanSeconds;

    public string StageDisplayText =>
        CurrentStage switch
        {
            ExperimentRunStage.Acquisition => "采集进行中",
            ExperimentRunStage.Blanking => "消隐中",
            ExperimentRunStage.Stimulation => "刺激进行中",
            ExperimentRunStage.Recovery => "恢复中",
            ExperimentRunStage.Completed => "已完成",
            ExperimentRunStage.Stopped => "已停止",
            _ => IsManualFinalAcquisitionReady ? "待末次采集"
            : IsManualStimulusReady ? "待刺激"
            : "待机中",
        };

    public string CurrentStageMonitorText =>
        IsAutomaticMode && CurrentCycle > 0
            ? $"{StageDisplayText}（循环 {CycleText}）"
            : StageDisplayText;

    public string CommunicationStatusText =>
        !CommunicationHealthy ? "通信链路异常"
        : IsWaveformDisplayLagging ? $"波形显示滞后 {EegDataFreshnessMilliseconds:0} ms"
        : PacketStatistics is null ? "通信链路正常"
        : $"通信正常 · 接收 {PacketStatistics.ReceivedPacketCount} / 丢包 {PacketStatistics.LostPacketCount} ({PacketStatistics.LossRate:P2})";

    public string PacketQualitySummaryText =>
        PacketStatistics is null
            ? "暂无统计"
            : $"接收 {PacketStatistics.ReceivedPacketCount}，丢包 {PacketStatistics.LostPacketCount}（{PacketStatistics.LossRate:P2}）";

    public string PacketQualityDetailText =>
        PacketStatistics is null
            ? "暂无统计"
            : $"乱序 {PacketStatistics.OutOfOrderPacketCount} / 重复 {PacketStatistics.DuplicatePacketCount} / 迟到丢弃 {PacketStatistics.LateDiscardedPacketCount} / 重排{(PacketStatistics.ReorderingEnabled ? "已开启" : "已关闭")}";

    public PreRunImpedanceSummary ConfigurationImpedance { get; }

    public string ImpedanceStatusText => ConfigurationImpedance.AcquisitionText;

    public string AverageImpedanceMonitorText =>
        AverageImpedanceKiloOhms > 0d ? $"{AverageImpedanceKiloOhms:0.0} kΩ 平均" : "未检测";

    public string StimulationImpedanceStatusText => ConfigurationImpedance.StimulationText;

    public string ImpedanceDetectionTimeText =>
        IsAcquisitionOnly
            ? $"配置检测时间：采集 {ConfigurationImpedance.AcquisitionMeasuredAt?.ToLocalTime().ToString("MM/dd HH:mm:ss") ?? "未检测"}"
            : ConfigurationImpedance.DetectionTimeText;

    public string ExceptionSummaryText => Exceptions.Count == 0 ? "无" : $"{Exceptions.Count} 项";

    public string ElapsedText => Elapsed.ToString(@"hh\:mm\:ss");

    public string CycleText =>
        CycleCount == 0
            ? $"{Math.Max(1, CurrentCycle)}/∞"
            : $"{Math.Max(0, CurrentCycle)}/{CycleCount}";

    public string StimulusModeText =>
        StimulusParameterPolicy.ModeName(
            RouteData.StimulusConfiguration.Kind,
            RouteData.StimulusConfiguration.ShamMode
        );

    public string StimulusCurrentText =>
        StimulusConfigurationSummaryBuilder.CurrentText(RouteData.StimulusConfiguration);

    public string StimulusCurrentLabel =>
        StimulusConfigurationSummaryBuilder.CurrentLabel(RouteData.StimulusConfiguration);

    public bool IsStimulusRampVisible => HasRampParameter(RouteData.StimulusConfiguration);

    public string StimulusRampText => $"{RouteData.StimulusConfiguration.RampSeconds:0.#}s 缓升";

    public WaveformDescriptor StimulusWaveform =>
        StimulusConfigurationSummaryBuilder.Waveform(RouteData.StimulusConfiguration);

    public string StimulusSummary =>
        IsStimulusRampVisible
            ? $"{StimulusModeText}　{StimulusCurrentText}　{StimulusRampText}"
            : $"{StimulusModeText}　{StimulusCurrentText}";

    public string ResultRunTimeText => Elapsed.ToString(@"hh\:mm\:ss");

    public string ResultStatusText =>
        RouteData.HistoricalResult?.Status switch
        {
            ExperimentRunStatus.Completed => "正常完成",
            ExperimentRunStatus.InterruptedByUser => "人工急停",
            ExperimentRunStatus.InterruptedByExit => "退出中断",
            ExperimentRunStatus.Failed => "运行失败",
            ExperimentRunStatus.RecoveredAfterCrash => "崩溃恢复",
            _ => IsEmergencyStopped ? "已紧急停止" : "正常完成",
        };

    public IReadOnlyList<ExperimentResultDetailItem> ResultOverviewItems =>
        [
            new("实验ID", RouteData.ExperimentId),
            new(
                "实验时间",
                ExperimentStartedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "--"
            ),
            new("被试ID", RouteData.SubjectId),
            new("运行时长", ResultRunTimeText),
            new(
                "异常事件数",
                Exceptions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ),
        ];

    public IReadOnlyList<ExperimentResultDetailItem> ResultAcquisitionItems =>
        [
            new("REF / GND", $"{RouteData.ReferenceChannel}/{RouteData.GroundChannel}"),
            new("点位", string.Join(", ", RouteData.AcquisitionChannels)),
            new("采样率", $"{RouteData.SampleRateHz} Hz"),
            new("数据质量", PacketQualitySummaryText),
            new("质量详情", PacketQualityDetailText),
        ];

    public IReadOnlyList<ExperimentResultDetailItem> StimulationConfigurationItems { get; }

    public IReadOnlyList<ExperimentResultDetailItem> ResultStimulationItems =>
        StimulationConfigurationItems;

    private static bool HasRampParameter(ExperimentStimulusConfigurationSnapshot configuration) =>
        StimulusParameterPolicy.For(configuration.Kind, configuration.ShamMode).ShowRamp;

    private void SetDurationMilliseconds(ExperimentStageViewModel stage, double milliseconds)
    {
        stage.SetDurationMilliseconds((decimal)Math.Max(0, milliseconds));
    }

    [RelayCommand]
    private void SelectMode(ExperimentRunMode mode)
    {
        if (IsModeLocked)
            return;
        SelectedMode = mode;
        RefreshValidationAndCommands();
    }

    private bool CanStartAcquisition() => IsTimingSetup && IsManualMode && IsTimelineValid;

    [RelayCommand(CanExecute = nameof(CanStartAcquisition))]
    private async Task StartAcquisitionAsync()
    {
        if (
            AcquisitionStage.GetDuration() is not { } acquisitionDuration
            || BlankingStage.GetDuration() is not { } blankingDuration
        )
            return;
        try
        {
            await BeginRunAsync();
            await _runService.StartAcquisitionAsync(
                new AcquisitionRunRequest(
                    acquisitionDuration,
                    RouteData.AcquisitionChannels,
                    RouteData.SampleRateHz,
                    RouteData.DeviceId,
                    RecordingId: _recordingId
                ),
                _runCancellation!.Token
            );
            if (_persistence is not null)
                await _persistence.CompleteCurrentStageAsync(acquisitionDuration);
            FlushPendingTelemetry();
            MarkStageCompleted(ExperimentRunStage.Acquisition);
            if (_persistence is not null)
                await _persistence.RequestStageAsync(
                    ExperimentRunStage.Blanking,
                    1,
                    acquisitionDuration
                );
            await RunLocalStageAsync(
                ExperimentRunStage.Blanking,
                blankingDuration,
                acquisitionDuration,
                _runCancellation.Token
            );
            if (IsAcquisitionOnly)
            {
                _runCancellation.Token.ThrowIfCancellationRequested();
                if (PageState == ExperimentRunPageState.Running)
                    await CompleteRunAsync();
                return;
            }
            CurrentStage = ExperimentRunStage.Standby;
            NotifyRunProperties();
        }
        catch (OperationCanceledException) when (_runCancellation?.IsCancellationRequested == true)
        { }
        catch (Exception exception)
        {
            await HandleRunFailureAsync(exception);
        }
    }

    private bool CanStartStimulation() =>
        !IsAcquisitionOnly
        && IsRunning
        && IsManualMode
        && BlankingStage.Status == ExperimentStageStatus.Completed
        && StimulationStage.Status == ExperimentStageStatus.Pending
        && GetCycleDuration() is not null;

    [RelayCommand(CanExecute = nameof(CanStartStimulation))]
    private async Task StartStimulationAsync()
    {
        if (IsAcquisitionOnly)
            return;
        if (
            AcquisitionStage.GetDuration() is not { } acquisitionDuration
            || BlankingStage.GetDuration() is not { } blankingDuration
            || GetStageDuration(StimulationStage) is not { } stimulationDuration
            || GetStageDuration(RecoveryStage) is not { } recoveryDuration
        )
            return;
        try
        {
            var stimulationOffset = acquisitionDuration + blankingDuration;
            if (_persistence is not null)
                await _persistence.RequestStageAsync(
                    ExperimentRunStage.Stimulation,
                    1,
                    stimulationOffset
                );
            await _runService.StartStimulationAsync(
                new StimulationRunRequest(
                    stimulationDuration,
                    IsAcquisitionOnly
                        ? 0
                        : ExperimentRunRouteDataDefaults.GetTotalCurrent(
                            RouteData.StimulusConfiguration
                        ),
                    stimulationOffset,
                    RouteData.AcquisitionChannels,
                    RouteData.SampleRateHz,
                    RouteData.StimulusConfiguration,
                    RouteData.StimulusElectrodes,
                    RouteData.DeviceId,
                    RecordingId: _recordingId
                ),
                _runCancellation!.Token
            );
            if (_persistence is not null)
                await _persistence.CompleteCurrentStageAsync(
                    stimulationOffset + stimulationDuration
                );
            FlushPendingTelemetry();
            MarkStageCompleted(ExperimentRunStage.Stimulation);
            if (_persistence is not null)
                await _persistence.RequestStageAsync(
                    ExperimentRunStage.Recovery,
                    1,
                    stimulationOffset + stimulationDuration
                );
            await RunLocalStageAsync(
                ExperimentRunStage.Recovery,
                recoveryDuration,
                stimulationOffset + stimulationDuration,
                _runCancellation.Token
            );
            _isManualFinalAcquisitionReady = true;
            CurrentStage = ExperimentRunStage.Standby;
            RunProgress = 0d;
            NotifyRunProperties();
        }
        catch (OperationCanceledException) when (_runCancellation?.IsCancellationRequested == true)
        { }
        catch (Exception exception)
        {
            await HandleRunFailureAsync(exception);
        }
    }

    private bool CanStartAutomaticExperiment() =>
        IsTimingSetup && IsAutomaticMode && IsTimelineValid;

    [RelayCommand(CanExecute = nameof(CanStartAutomaticExperiment))]
    private async Task StartAutomaticExperimentAsync()
    {
        if (
            AcquisitionStage.GetDuration() is not { } acquisition
            || BlankingStage.GetDuration() is not { } blanking
            || GetStageDuration(StimulationStage) is not { } stimulation
            || GetStageDuration(RecoveryStage) is not { } recovery
        )
            return;
        try
        {
            await BeginRunAsync();
            await _runService.StartAutomaticExperimentAsync(
                new AutomaticExperimentRunRequest(
                    acquisition,
                    blanking,
                    stimulation,
                    recovery,
                    CycleCount,
                    IsAcquisitionOnly
                        ? 0
                        : ExperimentRunRouteDataDefaults.GetTotalCurrent(
                            RouteData.StimulusConfiguration
                        ),
                    RouteData.AcquisitionChannels,
                    RouteData.SampleRateHz,
                    RouteData.StimulusConfiguration,
                    RouteData.StimulusElectrodes,
                    RouteData.DeviceId,
                    _recordingId,
                    RouteData.CreationMode
                ),
                _runCancellation!.Token
            );
            if (!IsAcquisitionOnly && PageState == ExperimentRunPageState.Running && CycleCount > 0)
            {
                var cycleDuration = GetCycleDuration()!.Value;
                CurrentCycle = CycleCount;
                await RunFinalAcquisitionCoreAsync(
                    acquisition,
                    TimeSpan.FromTicks(cycleDuration.Ticks * CycleCount),
                    CycleCount + 1,
                    _runCancellation.Token
                );
            }
            if (PageState == ExperimentRunPageState.Running)
                await CompleteRunAsync();
        }
        catch (OperationCanceledException) when (_runCancellation?.IsCancellationRequested == true)
        { }
        catch (Exception exception)
        {
            await HandleRunFailureAsync(exception);
        }
    }

    private bool CanEmergencyStop() => IsRunning;

    private bool CanStartFinalAcquisition() =>
        IsManualFinalAcquisitionReady && GetCycleDuration() is not null;

    private async Task StartFinalAcquisitionAsync()
    {
        if (
            AcquisitionStage.GetDuration() is not { } acquisitionDuration
            || GetCycleDuration() is not { } cycleDuration
        )
        {
            return;
        }

        _isManualFinalAcquisitionReady = false;
        try
        {
            await RunFinalAcquisitionCoreAsync(
                acquisitionDuration,
                cycleDuration,
                finalAcquisitionSegmentId: 2,
                _runCancellation!.Token
            );
            if (PageState == ExperimentRunPageState.Running)
                await CompleteRunAsync();
        }
        catch (OperationCanceledException) when (_runCancellation?.IsCancellationRequested == true)
        { }
        catch (Exception exception)
        {
            await HandleRunFailureAsync(exception);
        }
    }

    private async Task RunFinalAcquisitionCoreAsync(
        TimeSpan acquisitionDuration,
        TimeSpan timelineOffset,
        int finalAcquisitionSegmentId,
        CancellationToken cancellationToken
    )
    {
        // Apply all telemetry from the last normal recovery stage before reusing
        // stage 1 for the final acquisition. Otherwise a queued recovery update
        // can arrive after this method and put the completed recovery row back
        // into the running/spinning state.
        FlushPendingTelemetry();
        foreach (var stage in Stages)
        {
            stage.Status = ExperimentStageStatus.Completed;
            stage.Progress = 1d;
        }
        var acquisitionStage = AcquisitionStage;
        acquisitionStage.Status = ExperimentStageStatus.Running;
        acquisitionStage.Progress = 0d;
        _isFinalAcquisitionRunning = true;
        _finalAcquisitionSegmentId = finalAcquisitionSegmentId;
        CurrentStage = ExperimentRunStage.Acquisition;
        RunProgress = 0d;
        NotifyRunProperties();
        try
        {
            if (_persistence is not null)
                await _persistence.RequestStageAsync(
                    ExperimentRunStage.Acquisition,
                    finalAcquisitionSegmentId,
                    timelineOffset,
                    cancellationToken
                );
            await _runService.StartAcquisitionAsync(
                new AcquisitionRunRequest(
                    acquisitionDuration,
                    RouteData.AcquisitionChannels,
                    RouteData.SampleRateHz,
                    RouteData.DeviceId,
                    timelineOffset,
                    finalAcquisitionSegmentId,
                    finalAcquisitionSegmentId,
                    _recordingId
                ),
                cancellationToken
            );
            if (_persistence is not null)
                await _persistence.CompleteCurrentStageAsync(
                    timelineOffset + acquisitionDuration,
                    cancellationToken
                );
            MarkStageCompleted(ExperimentRunStage.Acquisition);
        }
        finally
        {
            _isFinalAcquisitionRunning = false;
            _finalAcquisitionSegmentId = 0;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEmergencyStop))]
    private async Task EmergencyStopAsync()
    {
        _runCancellation?.Cancel();
        Exception? interruptionRequestFailure = null;
        if (_persistence is not null)
        {
            try
            {
                await _persistence.RequestInterruptionAsync("人工急停");
            }
            catch (Exception exception)
            {
                interruptionRequestFailure = exception;
            }
        }
        bool? deviceStopSucceeded = null;
        string? deviceStopError = null;
        try
        {
            await _runService.EmergencyStopAsync(RouteData.DeviceId);
            deviceStopSucceeded = true;
        }
        catch (Exception exception)
        {
            deviceStopSucceeded = false;
            deviceStopError = exception.Message;
        }
        finally
        {
            EegRecordingCompletionResult? finalStatistics = null;
            try
            {
                finalStatistics = await FinalizeRecordingAsync(
                    EegRecordingCompletionStatus.EmergencyStopped
                );
            }
            catch
            {
                // Preserve the database terminal state even when the raw writer has failed.
            }
            if (_persistence is not null)
            {
                try
                {
                    await _persistence.FinishAsync(
                        interruptionRequestFailure is null
                            ? ExperimentRunStatus.InterruptedByUser
                            : ExperimentRunStatus.Failed,
                        interruptionRequestFailure is null
                            ? ExperimentIncidentKind.UserEmergencyStop
                            : ExperimentIncidentKind.PersistenceFailure,
                        interruptionRequestFailure,
                        source: "人工急停",
                        deviceStopSucceeded: deviceStopSucceeded,
                        deviceStopError: deviceStopError,
                        packetStatistics: finalStatistics?.PacketStatistics,
                        dataEndExclusiveSeconds: finalStatistics?.DataEndExclusiveSeconds,
                        timelineEnd: Elapsed
                    );
                }
                catch
                {
                    // 数据库仍不可写时，下次启动按最后一次心跳恢复。
                }
            }
        }
        FlushPendingTelemetry();
        CurrentStage = ExperimentRunStage.Stopped;
        PageState = ExperimentRunPageState.EmergencyStopped;
        foreach (var stage in Stages.Where(item => item.Status == ExperimentStageStatus.Running))
            stage.Status = ExperimentStageStatus.Stopped;
        NotifyRunProperties();
    }

    [RelayCommand]
    private async Task ReturnToTimingAsync()
    {
        await _runService.StopCurrentOperationAsync(RouteData.DeviceId);
        await ResetForAnotherRunAsync(clearWaveforms: true);
    }

    [RelayCommand]
    private Task SaveCurrentDataAsync()
    {
        ExportStatusText = "已保留停止时刻的数据，请选择需要导出的内容";
        ShowResults();
        return Task.CompletedTask;
    }

    private bool CanExecuteFooterPrimary() =>
        IsEmergencyStopped || IsCompleted || CanExecutePrimaryAction();

    [RelayCommand(CanExecute = nameof(CanExecuteFooterPrimary))]
    private async Task ExecuteFooterPrimaryAsync()
    {
        if (IsEmergencyStopped)
            await SaveCurrentDataAsync();
        else if (IsCompleted)
            ShowResults();
        else
            await ExecutePrimaryActionAsync();
    }

    [RelayCommand]
    private void ShowResults()
    {
        PageState = ExperimentRunPageState.Results;
        NotifyRunProperties();
    }

    private void InitializeHistoricalResult(HistoricalExperimentRunContext historical)
    {
        _recordingId = historical.RunId;
        ExperimentStartedAt = historical.StartedAtUtc;
        Elapsed =
            historical.EndedAtUtc > historical.StartedAtUtc
                ? historical.EndedAtUtc - historical.StartedAtUtc
                : TimeSpan.Zero;
        CurrentStage =
            historical.Status == ExperimentRunStatus.Completed
                ? ExperimentRunStage.Completed
                : ExperimentRunStage.Stopped;
        CurrentCycle = Math.Max(
            1,
            historical.StageIntervals.Select(x => x.Cycle).DefaultIfEmpty(1).Max()
        );
        PacketStatistics = historical.PacketStatistics;
        TimelineSegments.Clear();
        foreach (
            var interval in historical.StageIntervals.Where(x => x.EndSeconds > x.StartSeconds)
        )
        {
            var colors = GetStageColors(interval.Stage);
            TimelineSegments.Add(
                new ExperimentTimelineSegment(
                    interval.StartSeconds,
                    interval.EndSeconds,
                    interval.Stage,
                    $"{GetStageLabel(interval.Stage)} {ExperimentTimelineFormatter.Format(interval.EndSeconds - interval.StartSeconds)}",
                    colors.Fill,
                    colors.Foreground,
                    interval.Cycle
                )
            );
        }
        var stageTimelineEnd = historical
            .StageIntervals.Select(x => x.EndSeconds)
            .DefaultIfEmpty(0d)
            .Max();
        TimelineMaximum = Math.Max(
            1d,
            historical.LogicalTimelineEndSeconds ?? Math.Max(Elapsed.TotalSeconds, stageTimelineEnd)
        );
        TimelineLivePosition = 0d;
        TimelineViewStart = 0d;
        TimelineViewEnd = Math.Min(TimelineMaximum, Math.Max(1d, TimeRangeSeconds));
        IsFollowingLatest = false;
        RunProgress = 1d;
        foreach (var stage in Stages)
        {
            stage.Status =
                historical.Status == ExperimentRunStatus.Completed
                    ? ExperimentStageStatus.Completed
                    : ExperimentStageStatus.Stopped;
            stage.Progress = 1d;
            stage.IsEditable = false;
        }
        PageState = ExperimentRunPageState.Results;
        NotifyRunProperties();
        ScheduleHistoricalWindowLoad();
    }

    private static string GetStageLabel(ExperimentRunStage stage) =>
        stage switch
        {
            ExperimentRunStage.Acquisition => "采集",
            ExperimentRunStage.Blanking => "消隐",
            ExperimentRunStage.Stimulation => "刺激",
            ExperimentRunStage.Recovery => "恢复",
            _ => stage.ToString(),
        };

    [RelayCommand]
    private void TogglePlayback()
    {
        if (!IsHistoricalResult)
            return;
        if (IsPlaybackActive)
        {
            StopPlayback();
            return;
        }
        if (TimelineLivePosition >= TimelineMaximum - 0.0001d)
            TimelineLivePosition = 0d;
        if (TimelineLivePosition < TimelineViewStart || TimelineLivePosition > TimelineViewEnd)
            TimelineLivePosition = TimelineViewStart;
        _playbackCancellation?.Cancel();
        _playbackCancellation?.Dispose();
        _playbackCancellation = new CancellationTokenSource();
        IsPlaybackActive = true;
        _ = RunPlaybackAsync(_playbackCancellation.Token);
    }

    private async Task RunPlaybackAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var previous = stopwatch.Elapsed;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                var now = stopwatch.Elapsed;
                var advance = (now - previous).TotalSeconds * Math.Clamp(PlaybackSpeed, 0.5d, 2d);
                previous = now;
                PostToUi(() => AdvancePlayback(advance));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void AdvancePlayback(double advanceSeconds)
    {
        if (!IsPlaybackActive || _disposed)
            return;
        var next = Math.Min(TimelineMaximum, TimelineLivePosition + Math.Max(0d, advanceSeconds));
        TimelineLivePosition = next;
        var span = Math.Max(1d, TimelineViewEnd - TimelineViewStart);
        if (next > TimelineViewEnd)
        {
            TimelineViewEnd = Math.Min(TimelineMaximum, next);
            TimelineViewStart = Math.Max(0d, TimelineViewEnd - span);
        }
        if (next >= TimelineMaximum - 0.0001d)
            StopPlayback();
    }

    private void StopPlayback()
    {
        _playbackCancellation?.Cancel();
        _playbackCancellation?.Dispose();
        _playbackCancellation = null;
        IsPlaybackActive = false;
    }

    private bool CanRerunExperiment() =>
        IsResults
        && !IsExporting
        && (
            IsAcquisitionOnly
            || (
                RouteData.StimulusConfiguration.Kind != StimulusKind.EnvelopeTAcs
                && RouteData.StimulusConfiguration.Envelope is null
            )
        );

    [RelayCommand(CanExecute = nameof(CanRerunExperiment))]
    private async Task RerunExperimentAsync()
    {
        var configuration = RouteData.StimulusConfiguration;
        var parameterError = IsAcquisitionOnly
            ? null
            : StimulusParameterPolicy.Validate(
                configuration.Kind,
                configuration.ShamMode,
                configuration.Frequency,
                configuration.RampSeconds,
                configuration.DutyPercent,
                true
            );
        if (parameterError is not null)
        {
            if (_errorDialogService is not null)
                await _errorDialogService.ShowAsync("无法再次运行", parameterError);
            return;
        }
        if (
            !IsAcquisitionOnly
            && (
                RouteData.StimulusConfiguration.Kind == StimulusKind.EnvelopeTAcs
                || RouteData.StimulusConfiguration.Envelope is not null
            )
        )
        {
            if (_errorDialogService is not null)
                await _errorDialogService.ShowAsync(
                    "无法再次运行",
                    "包络-tACS 仅支持数据生成与历史回放，不能下发设备。"
                );
            return;
        }
        if (
            _deviceSelection is null
            || !_deviceSelection.IsConnected
            || string.IsNullOrWhiteSpace(_deviceSelection.SelectedDeviceId)
        )
        {
            if (_errorDialogService is not null)
                await _errorDialogService.ShowAsync("无法再次运行", "请先连接设备后再试。");
            return;
        }

        try
        {
            var acquisition =
                AcquisitionStage.GetDuration()
                ?? throw new InvalidOperationException("采集时长不完整。");
            var blanking =
                BlankingStage.GetDuration()
                ?? throw new InvalidOperationException("消隐时长不完整。");
            var stimulation =
                GetStageDuration(StimulationStage)
                ?? throw new InvalidOperationException("刺激时长不完整。");
            var recovery =
                GetStageDuration(RecoveryStage)
                ?? throw new InvalidOperationException("恢复时长不完整。");
            var timing = new ExperimentTimingTemplate(
                SelectedMode,
                acquisition.TotalMilliseconds,
                blanking.TotalMilliseconds,
                stimulation.TotalMilliseconds,
                recovery.TotalMilliseconds,
                CycleCount
            );
            var route = new ExperimentRunRouteData(
                RouteData.ExperimentId,
                RouteData.SubjectId,
                RouteData.StimulusConfiguration,
                RouteData.StimulusElectrodes,
                RouteData.AcquisitionChannels,
                RouteData.ReferenceChannel,
                RouteData.GroundChannel,
                RouteData.SampleRateHz,
                _deviceSelection.SelectedDeviceId,
                RouteData.ExperimentDatabaseId,
                RouteData.ScheduledAt,
                RouteData.Remarks,
                importedTiming: timing,
                historicalResult: null,
                physicalChannelNames: _rawPacketChannelNames,
                creationMode: RouteData.CreationMode
            );
            StopPlayback();
            _router.Navigate(new ExperimentRerunRouteData(route));
        }
        catch (Exception exception)
        {
            if (_errorDialogService is not null)
                await _errorDialogService.ShowAsync("无法再次运行", exception.Message);
        }
    }

    [RelayCommand]
    private void ShowAllChannels()
    {
        LeadLayout = WaveformLeadLayout.Comb32;
        IsAllChannelsViewOpen = true;
    }

    [RelayCommand]
    private void CloseAllChannels() => IsAllChannelsViewOpen = false;

    [RelayCommand]
    private void ViewHistory() => _router.Navigate(StartExperimentRouteData.History);

    [RelayCommand]
    private void ReturnHome() => _router.GoHome();

    private bool CanExportData() =>
        IsResults && !IsExporting && (ExportEdf || ExportCsv || ExportExpp);

    [RelayCommand(CanExecute = nameof(CanExportData))]
    private async Task ExportDataAsync() => await ExportToAsync(null);

    [RelayCommand(CanExecute = nameof(CanExportData))]
    private async Task SaveExportAsAsync()
    {
        if (_dialogs is null)
            return;
        var directory = await _dialogs.FolderPicker();
        if (!string.IsNullOrWhiteSpace(directory))
            await ExportToAsync(directory);
    }

    private async Task ExportToAsync(string? directory)
    {
        IsExporting = true;
        ExportProgress = 10;
        ExportStatusText = "正在导出原始脑电文件…";
        try
        {
            if (_exportService is null)
                throw new InvalidOperationException("导出服务不可用。");
            var result = await _exportService.ExportAsync(
                _recordingId,
                new EegExportSelection(ExportEdf, ExportCsv, ExportExpp),
                directory
            );
            ExportProgress = 100;
            ExportStatusText = $"导出完成：{result.Paths.Count} 个文件";
        }
        catch (Exception exception)
        {
            ExportProgress = 0;
            ExportStatusText = "导出失败，请查看提示。";
            if (_errorDialogService is not null)
            {
                var message =
                    exception is FileNotFoundException
                        ? "原始数据文件不存在，无法导出。"
                        : exception.Message;
                await _errorDialogService.ShowAsync("无法导出", message);
            }
        }
        finally
        {
            IsExporting = false;
            ExportDataCommand.NotifyCanExecuteChanged();
            SaveExportAsCommand.NotifyCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        StopPlayback();
        CancelDisplayFilterRebuild();
        CancelHistoricalWindowLoad(invalidateSnapshot: true);
        CancelWaveformDetailRefresh();
        _runService.TelemetryReceived -= OnTelemetryReceived;
        if (_telemetryFlushTimer is not null)
        {
            _telemetryFlushTimer.Stop();
            _telemetryFlushTimer.Tick -= OnTelemetryFlushTimerTick;
        }
        _telemetryBuffer.Clear();
        WaveformHover.Clear();
        foreach (var stage in Stages)
            stage.PropertyChanged -= OnStagePropertyChanged;
    }

    private bool IsTimelineValid => GetCycleDuration() is not null && ValidateStimulationDuration();

    private bool ValidateStimulationDuration()
    {
        if (IsAcquisitionOnly)
            return true;
        var stimulation = GetStageDuration(StimulationStage);
        var message = string.Empty;
        if (stimulation is null)
            message = "请输入刺激时长";
        else
        {
            var configuration = RouteData.StimulusConfiguration;
            message =
                StimulusParameterPolicy
                    .For(configuration.Kind, configuration.ShamMode)
                    .ValidateDuration(
                        stimulation.Value.TotalSeconds,
                        configuration.Frequency,
                        configuration.RampSeconds
                    )
                ?? string.Empty;
        }
        StimulationStage.ValidationText = message;
        return string.IsNullOrEmpty(message);
    }

    private void RefreshValidationAndCommands()
    {
        AcquisitionStage.ValidationText = AcquisitionStage.GetDuration() is null
            ? "请输入采集时长"
            : string.Empty;
        ValidateStimulationDuration();
        if (IsTimingSetup)
            RefreshTimelinePlan(resetWindow: true);
        var editable = IsTimingSetup;
        foreach (var stage in Stages)
            stage.IsEditable =
                editable
                && stage.Stage is ExperimentRunStage.Acquisition or ExperimentRunStage.Stimulation;
        StartAcquisitionCommand.NotifyCanExecuteChanged();
        StartStimulationCommand.NotifyCanExecuteChanged();
        StartAutomaticExperimentCommand.NotifyCanExecuteChanged();
        ExecutePrimaryActionCommand.NotifyCanExecuteChanged();
        ExecuteFooterPrimaryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsFooterPrimaryEnabled));
        EmergencyStopCommand.NotifyCanExecuteChanged();
        ExportDataCommand.NotifyCanExecuteChanged();
        SaveExportAsCommand.NotifyCanExecuteChanged();
    }

    private TimeSpan? GetCycleDuration()
    {
        var durations = Stages.Select(item => item.GetDuration()).ToArray();
        if (durations.Any(item => item is null))
            return null;
        return TimeSpan.FromTicks(durations.Sum(item => item.GetValueOrDefault().Ticks));
    }

    private void RefreshTimelinePlan(bool resetWindow)
    {
        var cycleDuration = GetCycleDuration();
        if (
            cycleDuration is not { } validCycleDuration
            || Stages.Any(stage => !string.IsNullOrWhiteSpace(stage.ValidationText))
        )
        {
            TimelineSegments.Clear();
            TimelineMaximum = PlaceholderTimelineSpanSeconds;
            if (resetWindow)
            {
                TimelineViewStart = 0d;
                TimelineViewEnd = GetSelectedTimelineWindow();
            }
            return;
        }

        var cycleCount = IsAutomaticMode
            ? CycleCount == 0
                ? Math.Max(1, CurrentCycle)
                : Math.Max(1, CycleCount)
            : 1;
        var includeFinalAcquisition = !IsAcquisitionOnly && (IsManualMode || CycleCount > 0);
        RebuildTimelineSegments(cycleCount, includeFinalAcquisition);
        var finalAcquisitionDuration = includeFinalAcquisition
            ? AcquisitionStage.GetDuration()!.Value.TotalSeconds
            : 0d;
        TimelineMaximum = Math.Max(
            0.001d,
            validCycleDuration.TotalSeconds * cycleCount + finalAcquisitionDuration
        );
        if (resetWindow)
        {
            TimelineViewStart = 0d;
            TimelineViewEnd = GetSelectedTimelineWindow();
            TimelineLivePosition = 0d;
            IsFollowingLatest = true;
        }
    }

    private void RebuildTimelineSegments(int cycleCount, bool includeFinalAcquisition)
    {
        TimelineSegments.Clear();
        var stageDurations = Stages
            .Select(stage => (Stage: stage, Duration: stage.GetDuration()))
            .ToArray();
        if (stageDurations.Any(item => item.Duration is null))
            return;
        var offset = 0d;
        for (var cycle = 1; cycle <= Math.Max(1, cycleCount); cycle++)
        {
            foreach (var item in stageDurations)
            {
                var stage = item.Stage;
                var duration = item.Duration.GetValueOrDefault().TotalSeconds;
                var colors = GetStageColors(stage.Stage);
                TimelineSegments.Add(
                    new ExperimentTimelineSegment(
                        offset,
                        offset + duration,
                        stage.Stage,
                        stage.Stage is ExperimentRunStage.Blanking or ExperimentRunStage.Recovery
                            ? string.Empty
                            : $"{stage.Title} {ExperimentTimelineFormatter.Format(duration)}",
                        colors.Fill,
                        colors.Foreground,
                        cycle
                    )
                );
                offset += duration;
            }
        }
        if (!includeFinalAcquisition)
            return;

        var acquisitionDuration = stageDurations[0].Duration.GetValueOrDefault().TotalSeconds;
        var acquisitionColors = GetStageColors(ExperimentRunStage.Acquisition);
        TimelineSegments.Add(
            new ExperimentTimelineSegment(
                offset,
                offset + acquisitionDuration,
                ExperimentRunStage.Acquisition,
                $"采集 {ExperimentTimelineFormatter.Format(acquisitionDuration)}",
                acquisitionColors.Fill,
                acquisitionColors.Foreground,
                Math.Max(1, cycleCount)
            )
        );
    }

    private void EnsureInfiniteTimelineCycle(int currentCycle)
    {
        if (!IsAutomaticMode || CycleCount != 0 || currentCycle <= 0)
            return;
        var cycleDuration = GetCycleDuration();
        if (cycleDuration is null)
            return;
        var requiredMaximum = cycleDuration.Value.TotalSeconds * currentCycle;
        if (TimelineMaximum >= requiredMaximum - 0.001d)
            return;
        RebuildTimelineSegments(currentCycle, includeFinalAcquisition: false);
        TimelineMaximum = requiredMaximum;
    }

    private void UpdateTimelineLivePosition()
    {
        EnsureInfiniteTimelineCycle(Math.Max(1, CurrentCycle));
        TimelineLivePosition = Math.Clamp(Elapsed.TotalSeconds, 0d, TimelineMaximum);
        if (!IsFollowingLatest)
        {
            if (!_deferWaveformRefresh)
                RefreshWaveformWindow();
            return;
        }

        var span = GetSelectedTimelineWindow();
        _suppressWaveformRefresh = true;
        try
        {
            if (TimelineLivePosition <= span)
            {
                TimelineViewStart = 0d;
                TimelineViewEnd = span;
            }
            else
            {
                TimelineViewEnd = TimelineLivePosition;
                TimelineViewStart = TimelineLivePosition - span;
            }
        }
        finally
        {
            _suppressWaveformRefresh = false;
        }
        RefreshWaveformWindow();
    }

    private static (string Fill, string Foreground) GetStageColors(ExperimentRunStage stage) =>
        stage switch
        {
            ExperimentRunStage.Acquisition => ("#EEF4FF", "#3686FF"),
            ExperimentRunStage.Blanking => ("#FFF1E8", "#A46A42"),
            ExperimentRunStage.Stimulation => ("#F7F1FF", "#9536F3"),
            ExperimentRunStage.Recovery => ("#EBF8F0", "#478060"),
            _ => ("#F2F4F8", "#65748A"),
        };

    private double GetSelectedTimelineWindow() =>
        Math.Min(TimelineMaximum, Math.Max(0.001d, TimeRangeSeconds));

    private async Task BeginRunAsync()
    {
        var configuration = RouteData.StimulusConfiguration;
        var parameterError = IsAcquisitionOnly
            ? null
            : StimulusParameterPolicy.Validate(
                configuration.Kind,
                configuration.ShamMode,
                configuration.Frequency,
                configuration.RampSeconds,
                configuration.DutyPercent,
                true
            );
        if (parameterError is not null)
            throw new InvalidOperationException(parameterError);
        _telemetryBuffer.Clear();
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        _persistenceTransitionFailure = 0;
        ExperimentStartedAt = _clock.UtcNow;
        _recordingId = Guid.NewGuid();
        _recordingCompletionTask = null;
        _rawDataVersion = 0;
        if (_persistence is not null && RouteData.ExperimentDatabaseId > 0)
        {
            await _persistence.PrepareAsync(
                new ExperimentRunPreparation(
                    _recordingId,
                    RouteData.ExperimentDatabaseId,
                    SelectedMode.ToString(),
                    CycleCount,
                    RouteData.SampleRateHz,
                    AcquisitionStage.GetDuration()!.Value,
                    BlankingStage.GetDuration()!.Value,
                    GetStageDuration(StimulationStage)!.Value,
                    GetStageDuration(RecoveryStage)!.Value
                ),
                RouteData.DeviceId,
                _runCancellation.Token
            );
            _runCancellation.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _persistence.RunCancellationToken
            );
            await _persistence.RequestStageAsync(
                ExperimentRunStage.Acquisition,
                1,
                TimeSpan.Zero,
                _runCancellation.Token
            );
        }
        await _runService.BeginRecordingAsync(
            new EegRecordingMetadata(
                _recordingId,
                RouteData.ExperimentId,
                RouteData.SubjectId,
                RouteData.DeviceId,
                RouteData.SampleRateHz,
                RouteData.AcquisitionChannels,
                ExperimentStartedAt.Value,
                DisplayFilterSettings,
                _rawPacketChannelNames
            )
        );
        _recordingActive = true;
        PageState = ExperimentRunPageState.Running;
        CurrentCycle = 1;
        IsFollowingLatest = true;
        TimelineLivePosition = 0d;
        TimelineViewStart = 0d;
        TimelineViewEnd = GetSelectedTimelineWindow();
        WaveformHover.Clear();
        _lastTelemetryCycle = 0;
        _isManualFinalAcquisitionReady = false;
        _isFinalAcquisitionRunning = false;
        _finalAcquisitionSegmentId = 0;
        foreach (var stage in Stages)
        {
            stage.Status = ExperimentStageStatus.Pending;
            stage.Progress = 0d;
            stage.IsEditable = false;
        }
        RefreshValidationAndCommands();
        NotifyRunProperties();
    }

    private async Task RunLocalStageAsync(
        ExperimentRunStage stage,
        TimeSpan duration,
        TimeSpan timelineOffset,
        CancellationToken cancellationToken
    )
    {
        var model = Stages.First(item => item.Stage == stage);
        if (_persistence is not null)
            await _persistence.ObserveStageAsync(
                stage,
                Math.Max(1, CurrentCycle),
                timelineOffset,
                timelineOffset,
                cancellationToken
            );
        CurrentStage = stage;
        model.Status = ExperimentStageStatus.Running;
        var started = _clock.UtcNow;
        while (_clock.UtcNow - started < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stageElapsed = _clock.UtcNow - started;
            model.Progress = Math.Clamp(stageElapsed.TotalSeconds / duration.TotalSeconds, 0d, 1d);
            RunProgress = model.Progress;
            Elapsed = timelineOffset + stageElapsed;
            _persistence?.ReportProgress(_recordingId, stage, Math.Max(1, CurrentCycle), Elapsed);
            UpdateTimelineLivePosition();
            NotifyRunProperties();
            await _clock.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        model.Progress = 1d;
        model.Status = ExperimentStageStatus.Completed;
        Elapsed = timelineOffset + duration;
        UpdateTimelineLivePosition();
        if (_persistence is not null)
            await _persistence.CompleteCurrentStageAsync(
                timelineOffset + duration,
                cancellationToken
            );
    }

    private void MarkStageCompleted(ExperimentRunStage stage)
    {
        var model = Stages.First(item => item.Stage == stage);
        model.Progress = 1d;
        model.Status = ExperimentStageStatus.Completed;
    }

    private async Task CompleteRunAsync()
    {
        _runCancellation?.Token.ThrowIfCancellationRequested();
        FlushPendingTelemetry();
        var finalStatistics = await FinalizeRecordingAsync(EegRecordingCompletionStatus.Completed);
        var logicalEnd = TimeSpan.FromSeconds(TimelineMaximum);
        if (_persistence is not null)
        {
            await _persistence.CompleteCurrentStageAsync(logicalEnd);
            await _persistence.FinishAsync(
                ExperimentRunStatus.Completed,
                packetStatistics: finalStatistics?.PacketStatistics,
                dataEndExclusiveSeconds: finalStatistics?.DataEndExclusiveSeconds,
                timelineEnd: logicalEnd
            );
        }
        if (CycleCount != 0 || IsManualMode)
        {
            Elapsed = logicalEnd;
            UpdateTimelineLivePosition();
        }
        CurrentStage = ExperimentRunStage.Completed;
        RunProgress = 1d;
        PageState = ExperimentRunPageState.Completed;
        foreach (var stage in Stages)
        {
            stage.Status = ExperimentStageStatus.Completed;
            stage.Progress = 1d;
        }
        NotifyRunProperties();
    }

    private async Task ResetForAnotherRunAsync(bool clearWaveforms)
    {
        var finalStatistics = await FinalizeRecordingAsync(EegRecordingCompletionStatus.Canceled);
        if (_persistence is not null && _persistence.IsActive)
        {
            await _persistence.FinishAsync(
                ExperimentRunStatus.InterruptedByUser,
                ExperimentIncidentKind.UserEmergencyStop,
                source: "返回时序设置",
                packetStatistics: finalStatistics?.PacketStatistics,
                dataEndExclusiveSeconds: finalStatistics?.DataEndExclusiveSeconds,
                timelineEnd: Elapsed
            );
        }
        if (_recordingId != Guid.Empty)
            _historyWindowProvider?.InvalidateRecording(_recordingId);
        CancelDisplayFilterRebuild();
        CancelHistoricalWindowLoad(invalidateSnapshot: true);
        CancelWaveformDetailRefresh();
        _telemetryBuffer.Clear();
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = null;
        PageState = ExperimentRunPageState.TimingSetup;
        CurrentStage = ExperimentRunStage.Standby;
        CurrentCycle = 0;
        RunProgress = 0d;
        ActualCurrentMilliAmps = 0d;
        Elapsed = TimeSpan.Zero;
        ExperimentStartedAt = null;
        TimelineLivePosition = 0d;
        IsFollowingLatest = true;
        WaveformHover.Clear();
        _lastTelemetryCycle = 0;
        _isManualFinalAcquisitionReady = false;
        _isFinalAcquisitionRunning = false;
        _finalAcquisitionSegmentId = 0;
        Exceptions.Clear();
        OnPropertyChanged(nameof(ExceptionSummaryText));
        foreach (var stage in Stages)
        {
            stage.Status = ExperimentStageStatus.Pending;
            stage.Progress = 0d;
        }
        if (clearWaveforms)
        {
            foreach (var channel in WaveformChannels)
                channel.Clear();
            foreach (var history in _rawWaveformHistory.Values)
                history.Clear();
            ResetDisplayFilters();
            OnPropertyChanged(nameof(HasWaveformData));
        }
        RefreshValidationAndCommands();
        NotifyRunProperties();
    }

    private async Task HandleRunFailureAsync(Exception exception)
    {
        if (_persistence?.RunCancellationToken.IsCancellationRequested == true)
            return;
        FlushPendingTelemetry();
        _runCancellation?.Cancel();
        bool? deviceStopSucceeded = null;
        string? deviceStopError = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _runService.StopCurrentOperationAsync(RouteData.DeviceId, timeout.Token);
            deviceStopSucceeded = true;
        }
        catch (Exception stopException)
        {
            deviceStopSucceeded = false;
            deviceStopError = stopException.Message;
        }
        EegRecordingCompletionResult? finalStatistics = null;
        try
        {
            finalStatistics = await FinalizeRecordingAsync(EegRecordingCompletionStatus.Failed);
        }
        catch
        {
            // A writer failure intentionally leaves the recoverable .tmp file in place.
        }
        if (_persistence is not null)
        {
            var incidentKind =
                exception
                    is EegAcquisitionException { Kind: EegAcquisitionFailureKind.DataPacketTimeout }
                        or StimulationException
                        {
                            Kind: StimulationFailureKind.ProgressPacketTimeout
                        }
                    ? ExperimentIncidentKind.DataTimeout
                : exception
                    is Microsoft.EntityFrameworkCore.DbUpdateException
                        or Microsoft.Data.Sqlite.SqliteException
                    ? ExperimentIncidentKind.PersistenceFailure
                : exception is System.IO.IOException ? ExperimentIncidentKind.FileFailure
                : ExperimentIncidentKind.DeviceFailure;
            try
            {
                await _persistence.FinishAsync(
                    ExperimentRunStatus.Failed,
                    incidentKind,
                    exception,
                    "实验运行异常",
                    deviceStopSucceeded,
                    deviceStopError,
                    packetStatistics: finalStatistics?.PacketStatistics,
                    dataEndExclusiveSeconds: finalStatistics?.DataEndExclusiveSeconds,
                    timelineEnd: Elapsed
                );
            }
            catch
            {
                // 数据库不可写时停止设备并保留原始文件；下次启动按最后心跳恢复。
            }
        }
        _isManualFinalAcquisitionReady = false;
        _isFinalAcquisitionRunning = false;
        _finalAcquisitionSegmentId = 0;
        Exceptions.Add(new ExperimentExceptionViewModel(DateTime.Now, exception.Message));
        OnPropertyChanged(nameof(ExceptionSummaryText));
        PageState = ExperimentRunPageState.EmergencyStopped;
        CurrentStage = ExperimentRunStage.Stopped;
        NotifyRunProperties();
        if (_errorDialogService is not null)
        {
            var title = exception switch
            {
                EegAcquisitionException acquisitionException => acquisitionException.Kind switch
                {
                    EegAcquisitionFailureKind.StartResponseTimeout => "EEG 采集启动超时",
                    EegAcquisitionFailureKind.DataPacketTimeout => "EEG 数据接收超时",
                    EegAcquisitionFailureKind.RawRecordingFailed => "原始数据记录失败",
                    _ => "EEG 采集启动失败",
                },
                StimulationException stimulationException => stimulationException.Kind switch
                {
                    StimulationFailureKind.ConfigurationFailed => "刺激参数下发失败",
                    StimulationFailureKind.StartResponseTimeout => "刺激启动超时",
                    StimulationFailureKind.ProgressPacketTimeout => "刺激状态接收超时",
                    StimulationFailureKind.CompletionEventTimeout => "刺激完成确认超时",
                    _ => "刺激启动失败",
                },
                _ => "实验运行失败",
            };
            await _errorDialogService.ShowAsync(title, exception.Message);
        }
    }

    private async Task ObserveStageDurablyAsync(ExperimentRunTelemetry telemetry)
    {
        try
        {
            await _persistence!.ObserveStageAsync(
                telemetry.Stage,
                telemetry.CurrentCycle,
                telemetry.TotalElapsed - telemetry.StageElapsed,
                telemetry.TotalElapsed
            );
        }
        catch (OperationCanceledException)
            when (_persistence?.RunCancellationToken.IsCancellationRequested == true) { }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _persistenceTransitionFailure, 1) == 0)
                await HandleRunFailureAsync(exception);
        }
    }

    private void OnStagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(ExperimentStageViewModel.DurationValue)
                or nameof(ExperimentStageViewModel.DurationMilliseconds)
                or nameof(ExperimentStageViewModel.SelectedUnit)
        )
            RefreshValidationAndCommands();
    }

    private void OnTelemetryReceived(object? sender, ExperimentRunTelemetryEventArgs e)
    {
        if (!string.Equals(e.DeviceId, RouteData.DeviceId, StringComparison.Ordinal))
            return;
        if (!e.Telemetry.IsWaveformOnly)
            _persistence?.ReportProgress(
                _recordingId,
                e.Telemetry.Stage,
                e.Telemetry.CurrentCycle,
                e.Telemetry.TotalElapsed
            );
        if (Application.Current is null)
        {
            ApplyTelemetry(e.Telemetry);
            return;
        }
        _telemetryBuffer.Enqueue(e.Telemetry);
    }

    private void OnTelemetryFlushTimerTick(object? sender, EventArgs e)
    {
        RefreshEegFreshness();
        FlushPendingTelemetry();
    }

    internal void FlushPendingTelemetry()
    {
        if (_disposed)
            return;
        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(FlushPendingTelemetry);
            return;
        }

        var pending = _telemetryBuffer.Drain();
        if (pending.IsEmpty)
            return;

        _deferWaveformRefresh = true;
        try
        {
            foreach (var telemetry in pending.StateUpdates)
                ApplyTelemetry(telemetry with { WaveformBatches = [] });
            foreach (var telemetry in pending.WaveformUpdates)
                AppendWaveformBatches(telemetry);
        }
        finally
        {
            _deferWaveformRefresh = false;
        }

        if (pending.WaveformUpdates.Count > 0)
            OnPropertyChanged(nameof(HasWaveformData));
        if (!_deferWaveformRefresh)
            RefreshWaveformWindow();
    }

    private void ApplyTelemetry(ExperimentRunTelemetry telemetry)
    {
        if (
            _disposed
            || _persistence?.RunCancellationToken.IsCancellationRequested == true
            || (
                telemetry.IsWaveformOnly
                    ? PageState
                        is not (
                            ExperimentRunPageState.Running
                            or ExperimentRunPageState.Completed
                            or ExperimentRunPageState.Results
                        )
                    : PageState != ExperimentRunPageState.Running
            )
        )
            return;
        if (
            _isFinalAcquisitionRunning
            && !telemetry.IsWaveformOnly
            && telemetry.Stage != ExperimentRunStage.Acquisition
        )
        {
            // Late state telemetry from the final normal cycle must not overwrite
            // the explicit final-acquisition UI state. Any attached waveform or
            // exception payload is still accepted without applying stage state.
            ApplyTelemetryPayloadWithoutState(telemetry);
            return;
        }
        if (
            !telemetry.IsWaveformOnly
            && telemetry.CurrentCycle <= CurrentCycle
            && Stages.FirstOrDefault(stage => stage.Stage == telemetry.Stage) is { } completedStage
            && completedStage.Status == ExperimentStageStatus.Completed
        )
        {
            // A progress update may have entered the 50 ms UI buffer immediately
            // before its device task returned. A completed stage is authoritative:
            // delayed progress must not restore its spinner or regress the monitor.
            ApplyTelemetryPayloadWithoutState(telemetry);
            return;
        }
        if (telemetry.IsWaveformOnly)
        {
            AppendWaveformBatches(telemetry);
            OnPropertyChanged(nameof(HasWaveformData));
            if (!_deferWaveformRefresh)
                ScheduleWaveformWindowRefresh();
            return;
        }
        var persistenceTransition =
            telemetry.Stage != CurrentStage || telemetry.CurrentCycle != CurrentCycle;
        if (persistenceTransition && _persistence is not null)
            _ = ObserveStageDurablyAsync(telemetry);
        _persistence?.ReportProgress(
            _recordingId,
            telemetry.Stage,
            telemetry.CurrentCycle,
            telemetry.TotalElapsed
        );
        CurrentStage = telemetry.Stage;
        Elapsed = telemetry.TotalElapsed;
        RunProgress = telemetry.StageProgress;
        if (!_isFinalAcquisitionRunning)
        {
            CurrentCycle = telemetry.CurrentCycle;
            EnsureInfiniteTimelineCycle(telemetry.CurrentCycle);
        }
        ActualCurrentMilliAmps = telemetry.ActualCurrentMilliAmps;
        AverageImpedanceKiloOhms = telemetry.AverageImpedanceKiloOhms;
        CommunicationHealthy = telemetry.CommunicationHealthy;

        if (
            IsAutomaticMode
            && !_isFinalAcquisitionRunning
            && telemetry.CurrentCycle > 0
            && telemetry.CurrentCycle != _lastTelemetryCycle
        )
        {
            _lastTelemetryCycle = telemetry.CurrentCycle;
            foreach (var stage in Stages)
            {
                stage.Status = ExperimentStageStatus.Pending;
                stage.Progress = 0d;
            }
        }
        if (
            telemetry.Stage
            is ExperimentRunStage.Acquisition
                or ExperimentRunStage.Blanking
                or ExperimentRunStage.Stimulation
                or ExperimentRunStage.Recovery
        )
        {
            var currentIndex = Stages.ToList().FindIndex(item => item.Stage == telemetry.Stage);
            for (var index = 0; index < Stages.Count; index++)
            {
                if (index < currentIndex)
                {
                    Stages[index].Status = ExperimentStageStatus.Completed;
                    Stages[index].Progress = 1d;
                }
                else if (index == currentIndex)
                {
                    Stages[index].Status = ExperimentStageStatus.Running;
                    Stages[index].Progress = telemetry.StageProgress;
                }
            }
        }

        ApplyTelemetryPayloadWithoutState(telemetry);

        UpdateTimelineLivePosition();
        NotifyRunProperties();
    }

    private void ApplyTelemetryPayloadWithoutState(ExperimentRunTelemetry telemetry)
    {
        if (telemetry.PacketStatistics is not null)
            PacketStatistics = telemetry.PacketStatistics;
        AppendWaveformBatches(telemetry);
        if (telemetry.WaveformBatches.Count > 0 && !_deferWaveformRefresh)
            RefreshWaveformWindow();
        foreach (var exception in telemetry.Exceptions)
            Exceptions.Add(new ExperimentExceptionViewModel(DateTime.Now, exception));
        if (telemetry.Exceptions.Count > 0)
            OnPropertyChanged(nameof(ExceptionSummaryText));
        OnPropertyChanged(nameof(HasWaveformData));
    }

    private void AppendWaveformBatches(ExperimentRunTelemetry telemetry)
    {
        if (telemetry.Stage != ExperimentRunStage.Acquisition)
            return;
        if (telemetry.WaveformBatches.Count > 0)
            _rawDataVersion++;
        UpdateEegFreshness(telemetry);
        var acquisitionSegmentId = Math.Max(1, telemetry.CurrentCycle);
        foreach (var batch in telemetry.WaveformBatches)
        {
            if (
                !_waveformChannelsById.TryGetValue(batch.ChannelId, out var channel)
                || !_rawWaveformHistory.TryGetValue(batch.ChannelId, out var history)
            )
                continue;

            history.Add((batch, acquisitionSegmentId));
            if (!_filterRebuildInProgress)
                TrimRawWaveformHistory(history, batch.StartTimeSeconds);
            if (!_filterRebuildInProgress)
                AppendFilteredBatch(channel, batch, acquisitionSegmentId);
        }
    }

    private Task<EegRecordingCompletionResult>? _recordingCompletionTask;

    private async Task<EegRecordingCompletionResult?> FinalizeRecordingAsync(
        EegRecordingCompletionStatus status
    )
    {
        if (_recordingCompletionTask is null)
        {
            if (!_recordingActive || _recordingId == Guid.Empty)
                return null;
            _recordingActive = false;
            _recordingCompletionTask = _runService.CompleteRecordingAsync(_recordingId, status);
        }
        var completion = await _recordingCompletionTask;
        PacketStatistics = completion.PacketStatistics;
        return completion;
    }

    private void AppendFilteredBatch(
        WaveformChannelViewModel channel,
        WaveformChannelBatch batch,
        int acquisitionSegmentId
    )
    {
        var filter = _displayFilters[channel.ChannelId];
        if (
            !_displayFilterSegments.TryGetValue(channel.ChannelId, out var previousSegment)
            || previousSegment != acquisitionSegmentId
            || StartsDataDiscontinuity(
                batch,
                _displayFilterLastSampleTimes.GetValueOrDefault(channel.ChannelId, double.NaN),
                RouteData.SampleRateHz
            )
        )
            filter.Reset();
        _displayFilterSegments[channel.ChannelId] = acquisitionSegmentId;
        channel.Append(
            batch with
            {
                Samples = filter.Process(batch.Samples),
            },
            acquisitionSegmentId
        );
        if (batch.Samples.Count > 0)
            _displayFilterLastSampleTimes[channel.ChannelId] = GetBatchLastSampleTime(
                batch,
                RouteData.SampleRateHz
            );
    }

    private static void TrimRawWaveformHistory(
        List<(WaveformChannelBatch Batch, int SegmentId)> history,
        double latestStartSeconds
    )
    {
        const double retainedSeconds = 120d;
        var cutoff = latestStartSeconds - retainedSeconds;
        var removeCount = 0;
        while (removeCount < history.Count && history[removeCount].Batch.StartTimeSeconds < cutoff)
            removeCount++;
        if (removeCount > 0)
            history.RemoveRange(0, removeCount);
    }

    private void ResetDisplayFilters()
    {
        _displayFilterSegments.Clear();
        _displayFilterLastSampleTimes.Clear();
        foreach (var channel in WaveformChannels)
            _displayFilters[channel.ChannelId] = new CausalEegDisplayFilter(
                RouteData.SampleRateHz,
                DisplayFilterSettings
            );
    }

    private void CancelDisplayFilterRebuild()
    {
        _filterRebuildGeneration++;
        _filterRebuildInProgress = false;
        _filterRebuildCancellation?.Cancel();
        _filterRebuildCancellation?.Dispose();
        _filterRebuildCancellation = null;
    }

    private void ScheduleDisplayFilterRebuild()
    {
        OnPropertyChanged(nameof(DisplayFilterSettings));
        CancelHistoricalWindowLoad(invalidateSnapshot: true);
        _filterRebuildCancellation?.Cancel();
        _filterRebuildCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _filterRebuildCancellation = cancellation;
        var generation = ++_filterRebuildGeneration;
        var settings = DisplayFilterSettings;
        var snapshots = _rawWaveformHistory.ToDictionary(
            item => item.Key,
            item => item.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase
        );
        _filterRebuildInProgress = true;

        _ = Task.Run(
                () => BuildFilteredHistory(snapshots, settings, cancellation.Token),
                cancellation.Token
            )
            .ContinueWith(
                task =>
                {
                    if (task.IsCanceled || task.IsFaulted || generation != _filterRebuildGeneration)
                        return;
                    Dispatcher.UIThread.Post(() =>
                        ApplyFilteredHistory(task.Result, snapshots, settings, generation)
                    );
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
    }

    private FilteredHistory BuildFilteredHistory(
        IReadOnlyDictionary<string, (WaveformChannelBatch Batch, int SegmentId)[]> snapshots,
        EegDisplayFilterSettings settings,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<string, FilteredChannelHistory>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var (channelId, batches) in snapshots)
        {
            var filter = new CausalEegDisplayFilter(RouteData.SampleRateHz, settings);
            var filtered = new List<(WaveformChannelBatch Batch, int SegmentId)>(batches.Length);
            var lastSegment = int.MinValue;
            var lastSampleTime = double.NaN;
            foreach (var item in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    item.SegmentId != lastSegment
                    || StartsDataDiscontinuity(item.Batch, lastSampleTime, RouteData.SampleRateHz)
                )
                    filter.Reset();
                filtered.Add(
                    (
                        item.Batch with
                        {
                            Samples = filter.Process(item.Batch.Samples),
                        },
                        item.SegmentId
                    )
                );
                lastSegment = item.SegmentId;
                if (item.Batch.Samples.Count > 0)
                    lastSampleTime = GetBatchLastSampleTime(item.Batch, RouteData.SampleRateHz);
            }
            result[channelId] = new FilteredChannelHistory(
                filter,
                filtered,
                lastSegment,
                lastSampleTime
            );
        }
        return new FilteredHistory(result);
    }

    private void ApplyFilteredHistory(
        FilteredHistory filteredHistory,
        IReadOnlyDictionary<string, (WaveformChannelBatch Batch, int SegmentId)[]> snapshots,
        EegDisplayFilterSettings settings,
        long generation
    )
    {
        if (
            _disposed
            || generation != _filterRebuildGeneration
            || settings != DisplayFilterSettings
        )
            return;

        foreach (var channel in WaveformChannels)
        {
            var channelId = channel.ChannelId;
            var result = filteredHistory.Channels[channelId];
            var snapshotLength = snapshots[channelId].Length;
            var currentHistory = _rawWaveformHistory[channelId];
            for (var index = snapshotLength; index < currentHistory.Count; index++)
            {
                var item = currentHistory[index];
                if (
                    item.SegmentId != result.LastSegment
                    || StartsDataDiscontinuity(
                        item.Batch,
                        result.LastSampleTime,
                        RouteData.SampleRateHz
                    )
                )
                    result.Filter.Reset();
                result.Batches.Add(
                    (
                        item.Batch with
                        {
                            Samples = result.Filter.Process(item.Batch.Samples),
                        },
                        item.SegmentId
                    )
                );
                result.LastSegment = item.SegmentId;
                if (item.Batch.Samples.Count > 0)
                    result.LastSampleTime = GetBatchLastSampleTime(
                        item.Batch,
                        RouteData.SampleRateHz
                    );
            }

            if (IsFollowingLatest)
                channel.Clear();
            else
                channel.ClearLiveHistory();
            foreach (var item in result.Batches)
                channel.Append(item.Batch, item.SegmentId);
            _displayFilters[channelId] = result.Filter;
            _displayFilterSegments[channelId] = result.LastSegment;
            if (double.IsFinite(result.LastSampleTime))
                _displayFilterLastSampleTimes[channelId] = result.LastSampleTime;
            else
                _displayFilterLastSampleTimes.Remove(channelId);
            if (currentHistory.Count > 0)
                TrimRawWaveformHistory(currentHistory, currentHistory[^1].Batch.StartTimeSeconds);
        }
        _filterRebuildInProgress = false;
        if (IsFollowingLatest)
        {
            foreach (var channel in WaveformChannels)
                channel.ClearVisibleWindowOverride();
            RefreshWaveformWindow();
        }
        else
        {
            ScheduleHistoricalWindowLoad();
        }
        OnPropertyChanged(nameof(HasWaveformData));
    }

    private void ScheduleHistoricalWindowLoad() => ScheduleTimelineViewportRefresh();

    private void ScheduleTimelineViewportRefresh()
    {
        if (_disposed || _timelineViewportRefreshScheduled)
            return;
        if (Application.Current is null)
        {
            ProcessTimelineViewportRefresh();
            return;
        }

        _timelineViewportRefreshScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _timelineViewportRefreshScheduled = false;
                if (!_disposed)
                    ProcessTimelineViewportRefresh();
            },
            DispatcherPriority.Render
        );
    }

    private void ProcessTimelineViewportRefresh()
    {
        RefreshWaveformWindow(
            IsTimelineDragging
                ? WaveformPreviewPointsPerVisibleWindow
                : WaveformPointsPerVisibleWindow
        );
        if (!IsTimelineDragging)
            ScheduleDebouncedWaveformDetailRefresh();
        ProcessHistoricalWindowLoad();
    }

    private void ProcessHistoricalWindowLoad()
    {
        if (
            IsFollowingLatest
            || _historyWindowProvider is null
            || _recordingId == Guid.Empty
            || _rawPacketChannelNames.Count == 0
            || TimelineViewEnd <= TimelineViewStart
        )
            return;
        var generation = ++_historyWindowGeneration;
        if (HasHistoricalSnapshotCoverage())
        {
            CancelHistoricalDetailLoad();
            IsHistoricalWaveformLoading = false;
            HistoricalWaveformErrorText = string.Empty;
            return;
        }
        if (
            TryShowRecentFilteredWindow(
                IsTimelineDragging
                    ? WaveformPreviewPointsPerVisibleWindow
                    : WaveformPointsPerVisibleWindow
            )
        )
            return;

        IsHistoricalWaveformLoading = false;
        var viewStart = TimelineViewStart;
        var viewEnd = TimelineViewEnd;
        var span = Math.Max(0.001d, viewEnd - viewStart);
        var coverageStart = Math.Max(0d, viewStart - span);
        var coverageEnd = Math.Min(TimelineMaximum, viewEnd + span);
        var settings = DisplayFilterSettings;
        var recordingId = _recordingId;
        var dataVersion = _rawDataVersion;
        _activeHistoryRequestDataVersion = dataVersion;
        var availableThrough = Math.Max(TimelineLivePosition, viewEnd);
        var coverageSpan = Math.Max(0.001d, coverageEnd - coverageStart);
        var maximumPoints = Math.Max(
            WaveformPointsPerVisibleWindow,
            (int)Math.Ceiling(WaveformPointsPerVisibleWindow * coverageSpan / span)
        );
        var request = new EegHistoryWindowRequest(
            recordingId,
            coverageStart,
            coverageEnd,
            RouteData.SampleRateHz,
            settings,
            _rawPacketChannelNames,
            dataVersion,
            availableThrough,
            maximumPoints,
            RouteData.HistoricalResult?.RawFilePath
        );
        var previewPoints = WaveformPreviewPointsPerVisibleWindow;
        var previewRequest = request with
        {
            StartTimeSeconds = viewStart,
            EndTimeSeconds = viewEnd,
            MaximumPointsPerChannel = previewPoints,
        };
        QueueCachedHistoricalPreview(
            new HistoricalPreviewWork(previewRequest, generation, previewPoints / span)
        );
        if (IsTimelineDragging)
        {
            CancelHistoricalDetailLoad();
            return;
        }

        ScheduleHistoricalDetailLoad(request, generation, maximumPoints / coverageSpan);
    }

    private void QueueCachedHistoricalPreview(HistoricalPreviewWork work)
    {
        lock (_historyPreviewGate)
        {
            _pendingHistoricalPreview = work;
            if (_historicalPreviewWorkerRunning)
                return;
            _historicalPreviewWorkerRunning = true;
        }

        _ = RunCachedHistoricalPreviewWorkerAsync();
    }

    private async Task RunCachedHistoricalPreviewWorkerAsync()
    {
        while (true)
        {
            HistoricalPreviewWork? work;
            lock (_historyPreviewGate)
            {
                work = _pendingHistoricalPreview;
                _pendingHistoricalPreview = null;
                if (work is null)
                {
                    _historicalPreviewWorkerRunning = false;
                    return;
                }
            }

            try
            {
                var result = await _historyWindowProvider!
                    .LoadCachedAsync(work.Request)
                    .ConfigureAwait(false);
                if (result is not null)
                {
                    PostToUi(() =>
                    {
                        if (!IsHistoricalWaveformLoading)
                            ApplyHistoricalWindow(result, work.Generation, work.PointsPerSecond);
                    });
                }
            }
            catch (Exception)
            {
                // Cached previews are best-effort; the detailed load reports real failures.
            }
        }
    }

    private void ScheduleHistoricalDetailLoad(
        EegHistoryWindowRequest request,
        long generation,
        double pointsPerSecond
    )
    {
        CancelHistoricalDetailLoad();
        var cancellation = new CancellationTokenSource();
        _historyWindowCancellation = cancellation;
        _ = LoadHistoricalWindowAfterDelayAsync(
            request,
            generation,
            pointsPerSecond,
            cancellation.Token
        );
    }

    private bool TryShowRecentFilteredWindow(int maximumPoints)
    {
        if (_filterRebuildInProgress)
            return false;
        var histories = _rawWaveformHistory.Values.Where(history => history.Count > 0).ToArray();
        if (histories.Length == 0)
            return false;
        var earliest = histories.Max(history => history[0].Batch.StartTimeSeconds);
        var latest = histories.Min(history =>
            GetBatchLastSampleTime(history[^1].Batch, RouteData.SampleRateHz)
        );
        var requiredEnd = Math.Min(TimelineViewEnd, TimelineLivePosition);
        if (
            TimelineViewStart < earliest - 0.0000001d
            || requiredEnd > latest + 1d / Math.Max(1, RouteData.SampleRateHz) + 0.0000001d
        )
            return false;

        CancelHistoricalWindowLoad(invalidateSnapshot: true);
        foreach (var channel in WaveformChannels)
            channel.ClearVisibleWindowOverride();
        RefreshWaveformWindow(maximumPoints);
        HistoricalWaveformErrorText = string.Empty;
        return true;
    }

    private async Task LoadHistoricalWindowAfterDelayAsync(
        EegHistoryWindowRequest request,
        long generation,
        double pointsPerSecond,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(TimelineResizeDebounceDelay, cancellationToken).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (!IsCurrentHistoricalRequest(request, generation))
                    return;
                IsHistoricalWaveformLoading = true;
                HistoricalWaveformErrorText = string.Empty;
            });
            var result = await _historyWindowProvider!
                .LoadAsync(request, cancellationToken)
                .ConfigureAwait(false);
            PostToUi(() => ApplyHistoricalWindow(result, generation, pointsPerSecond));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PostToUi(() => ApplyHistoricalWindowFailure(request, generation, exception));
        }
    }

    private void ApplyHistoricalWindow(
        EegHistoryWindowResult result,
        long generation,
        double pointsPerSecond
    )
    {
        if (!IsCurrentHistoricalResult(result, generation))
            return;
        var channels = result.Channels.ToDictionary(
            item => item.ChannelId,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var channel in WaveformChannels)
        {
            channel.ReplaceVisibleWindow(
                channels.TryGetValue(channel.ChannelId, out var window) ? window.Samples : []
            );
        }
        _visibleSnapshotStart = result.StartTimeSeconds;
        _visibleSnapshotEnd = result.EndTimeSeconds;
        _visibleSnapshotPointsPerSecond = pointsPerSecond;
        _visibleSnapshotFilterSettings = result.FilterSettings;
        _visibleSnapshotRecordingId = result.RecordingId;
        IsHistoricalWaveformLoading = false;
        HistoricalWaveformErrorText = string.Empty;
        OnPropertyChanged(nameof(HasWaveformData));
    }

    private void ApplyHistoricalWindowFailure(
        EegHistoryWindowRequest request,
        long generation,
        Exception exception
    )
    {
        if (!IsCurrentHistoricalRequest(request, generation))
            return;
        foreach (var channel in WaveformChannels)
            channel.ReplaceVisibleWindow([]);
        InvalidateVisibleSnapshot();
        IsHistoricalWaveformLoading = false;
        var missing =
            exception is FileNotFoundException
            || !string.IsNullOrWhiteSpace(request.RawFilePath) && !File.Exists(request.RawFilePath);
        HistoricalWaveformErrorText = missing ? "原始数据文件不存在" : "历史波形加载失败";
        if (missing && !_missingRawFileDialogShown && _errorDialogService is not null)
        {
            _missingRawFileDialogShown = true;
            StopPlayback();
            _ = _errorDialogService.ShowAsync("无法回放", "原始数据文件不存在，无法回放历史波形。");
        }
    }

    private bool HasHistoricalSnapshotCoverage() =>
        _visibleSnapshotRecordingId == _recordingId
        && _visibleSnapshotFilterSettings == DisplayFilterSettings
        && double.IsFinite(_visibleSnapshotStart)
        && _visibleSnapshotPointsPerSecond + 0.0000001d >= RequiredVisiblePointDensity()
        && TimelineViewStart >= _visibleSnapshotStart - 0.0000001d
        && TimelineViewEnd <= _visibleSnapshotEnd + 0.0000001d;

    private double RequiredVisiblePointDensity() =>
        WaveformPointsPerVisibleWindow / Math.Max(0.001d, TimelineViewEnd - TimelineViewStart);

    private bool IsCurrentHistoricalRequest(EegHistoryWindowRequest request, long generation) =>
        !_disposed
        && !IsFollowingLatest
        && generation == _historyWindowGeneration
        && request.RecordingId == _recordingId
        && request.FilterSettings == DisplayFilterSettings
        && TimelineViewStart >= request.StartTimeSeconds - 0.0000001d
        && TimelineViewEnd <= request.EndTimeSeconds + 0.0000001d;

    private bool IsCurrentHistoricalResult(EegHistoryWindowResult result, long generation) =>
        !_disposed
        && !IsFollowingLatest
        && generation == _historyWindowGeneration
        && result.RecordingId == _recordingId
        && result.FilterSettings == DisplayFilterSettings
        && result.DataVersion == _activeHistoryRequestDataVersion
        && TimelineViewStart >= result.StartTimeSeconds - 0.0000001d
        && TimelineViewEnd <= result.EndTimeSeconds + 0.0000001d;

    private void CancelHistoricalWindowLoad(bool invalidateSnapshot)
    {
        _historyWindowGeneration++;
        CancelHistoricalDetailLoad();
        lock (_historyPreviewGate)
            _pendingHistoricalPreview = null;
        _activeHistoryRequestDataVersion = -1;
        if (invalidateSnapshot)
            InvalidateVisibleSnapshot();
    }

    private void CancelHistoricalDetailLoad()
    {
        _historyWindowCancellation?.Cancel();
        _historyWindowCancellation?.Dispose();
        _historyWindowCancellation = null;
        IsHistoricalWaveformLoading = false;
    }

    private void InvalidateVisibleSnapshot()
    {
        _visibleSnapshotStart = double.NaN;
        _visibleSnapshotEnd = double.NaN;
        _visibleSnapshotPointsPerSecond = 0d;
        _visibleSnapshotFilterSettings = null;
        _visibleSnapshotRecordingId = Guid.Empty;
    }

    private static void PostToUi(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private sealed record FilteredHistory(
        IReadOnlyDictionary<string, FilteredChannelHistory> Channels
    );

    private sealed record HistoricalPreviewWork(
        EegHistoryWindowRequest Request,
        long Generation,
        double PointsPerSecond
    );

    private sealed class FilteredChannelHistory(
        CausalEegDisplayFilter filter,
        List<(WaveformChannelBatch Batch, int SegmentId)> batches,
        int lastSegment,
        double lastSampleTime
    )
    {
        public CausalEegDisplayFilter Filter { get; } = filter;
        public List<(WaveformChannelBatch Batch, int SegmentId)> Batches { get; } = batches;
        public int LastSegment { get; set; } = lastSegment;
        public double LastSampleTime { get; set; } = lastSampleTime;
    }

    private static bool StartsDataDiscontinuity(
        WaveformChannelBatch batch,
        double lastSampleTime,
        int sampleRateHz
    )
    {
        if (!double.IsFinite(lastSampleTime) || batch.Samples.Count == 0)
            return false;
        var interval =
            batch.SampleIntervalSeconds > 0d
                ? batch.SampleIntervalSeconds
                : 1d / Math.Max(1, sampleRateHz);
        return Math.Abs(batch.StartTimeSeconds - (lastSampleTime + interval)) > interval * 1.5d;
    }

    private static double GetBatchLastSampleTime(WaveformChannelBatch batch, int sampleRateHz)
    {
        var interval =
            batch.SampleIntervalSeconds > 0d
                ? batch.SampleIntervalSeconds
                : 1d / Math.Max(1, sampleRateHz);
        return batch.StartTimeSeconds + (batch.Samples.Count - 1) * interval;
    }

    private void UpdateEegFreshness(ExperimentRunTelemetry telemetry)
    {
        _latestEegReceivedAtUtc = telemetry.LatestPacketReceivedAtUtc;
        _latestEegReceivedTimestamp = telemetry.LatestPacketReceivedTimestamp;
        EegDecodeLatencyMilliseconds = Math.Max(
            0d,
            telemetry.DecodeLatency?.TotalMilliseconds ?? 0d
        );
        EegModelLatencyMilliseconds = Math.Max(
            0d,
            telemetry.LocalProcessingLatency?.TotalMilliseconds ?? 0d
        );
        RefreshEegFreshness();
    }

    private void RefreshEegFreshness()
    {
        if (
            PageState != ExperimentRunPageState.Running
            || CurrentStage != ExperimentRunStage.Acquisition
        )
            return;
        TimeSpan freshness;
        if (_latestEegReceivedTimestamp > 0)
            freshness = Stopwatch.GetElapsedTime(_latestEegReceivedTimestamp);
        else if (_latestEegReceivedAtUtc is { } receivedAtUtc)
            freshness = DateTimeOffset.UtcNow - receivedAtUtc;
        else
            return;

        EegDataFreshnessMilliseconds = Math.Max(0d, freshness.TotalMilliseconds);
        IsWaveformDisplayLagging = EegDataFreshnessMilliseconds > 100d;
        OnPropertyChanged(nameof(CommunicationStatusText));
    }

    partial void OnTimeRangeSecondsChanged(int value)
    {
        if (!TimeRangeOptions.Contains(value))
        {
            TimeRangeSeconds = 10;
            return;
        }

        ApplySelectedTimeRange();
    }

    partial void OnSelectedHighPassFilterChanged(EegFilterOption value)
    {
        ScheduleDisplayFilterRebuild();
        QueueDisplayFilterChangeRecord();
    }

    partial void OnSelectedLowPassFilterChanged(EegFilterOption value)
    {
        ScheduleDisplayFilterRebuild();
        QueueDisplayFilterChangeRecord();
    }

    partial void OnSelectedNotchFilterChanged(EegFilterOption value)
    {
        ScheduleDisplayFilterRebuild();
        QueueDisplayFilterChangeRecord();
    }

    private void QueueDisplayFilterChangeRecord()
    {
        if (!_recordingActive)
            return;
        _ = PersistDisplayFilterChangeAsync(
            new EegFilterChangeRecord(
                _recordingId,
                _clock.UtcNow,
                Elapsed.TotalSeconds,
                DisplayFilterSettings
            )
        );
    }

    private async Task PersistDisplayFilterChangeAsync(EegFilterChangeRecord change)
    {
        try
        {
            await _runService.RecordDisplayFilterChangeAsync(change);
        }
        catch (Exception exception)
        {
            await HandleRunFailureAsync(
                new EegAcquisitionException(
                    EegAcquisitionFailureKind.RawRecordingFailed,
                    $"原始数据记录失败：{exception.Message}",
                    innerException: exception
                )
            );
        }
    }

    private void ApplySelectedTimeRange()
    {
        var span = GetSelectedTimelineWindow();
        _suppressWaveformRefresh = true;
        try
        {
            if (IsFollowingLatest)
            {
                var livePosition = Math.Clamp(TimelineLivePosition, 0d, TimelineMaximum);
                if (livePosition <= span)
                {
                    TimelineViewStart = 0d;
                    TimelineViewEnd = span;
                }
                else
                {
                    TimelineViewStart = livePosition - span;
                    TimelineViewEnd = livePosition;
                }
            }
            else
            {
                var center = (TimelineViewStart + TimelineViewEnd) / 2d;
                var maximumStart = Math.Max(0d, TimelineMaximum - span);
                TimelineViewStart = Math.Clamp(center - span / 2d, 0d, maximumStart);
                TimelineViewEnd = TimelineViewStart + span;
            }
        }
        finally
        {
            _suppressWaveformRefresh = false;
        }
        RefreshWaveformWindow();
    }

    partial void OnTimelineViewStartChanged(double value)
    {
        _historyWindowGeneration++;
        ScheduleHistoricalWindowLoad();
    }

    partial void OnTimelineViewEndChanged(double value)
    {
        _historyWindowGeneration++;
        ScheduleHistoricalWindowLoad();
    }

    partial void OnIsTimelineDraggingChanged(bool value)
    {
        _historyWindowGeneration++;
        if (value)
        {
            CancelHistoricalDetailLoad();
            CancelWaveformDetailRefresh();
        }
        ScheduleHistoricalWindowLoad();
    }

    partial void OnIsFollowingLatestChanged(bool value)
    {
        if (value && IsHistoricalResult)
        {
            _restoringHistoricalReviewMode = true;
            try
            {
                IsFollowingLatest = false;
            }
            finally
            {
                _restoringHistoricalReviewMode = false;
            }
            return;
        }
        if (_restoringHistoricalReviewMode)
            return;
        if (value)
        {
            CancelHistoricalWindowLoad(invalidateSnapshot: true);
            HistoricalWaveformErrorText = string.Empty;
            foreach (var channel in WaveformChannels)
                channel.ClearVisibleWindowOverride();
            RefreshWaveformWindow();
        }
        else
        {
            ScheduleHistoricalWindowLoad();
        }
    }

    partial void OnCycleCountChanged(int value)
    {
        if (value < 0 || value > 200)
            CycleCount = Math.Clamp(value, 0, 200);
        RefreshValidationAndCommands();
    }

    partial void OnSelectedLeadLayoutOptionChanged(WaveformLayoutOption? value)
    {
        if (value is not null)
            LeadLayout = value.Value;
    }

    partial void OnLeadLayoutChanged(WaveformLeadLayout value)
    {
        if (SelectedLeadLayoutOption?.Value != value)
            SelectedLeadLayoutOption = LeadLayoutOptions.First(item => item.Value == value);
    }

    partial void OnPageStateChanged(ExperimentRunPageState value)
    {
        OnPropertyChanged(nameof(IsCycleCountVisible));
        NotifyPrimaryActionProperties();
        RefreshValidationAndCommands();
    }

    private bool CanExecutePrimaryAction() =>
        IsPrimaryActionVisible
        && (
            IsAutomaticMode ? CanStartAutomaticExperiment()
            : IsManualFinalAcquisitionReady ? CanStartFinalAcquisition()
            : IsManualStimulusReady ? CanStartStimulation()
            : CanStartAcquisition()
        );

    [RelayCommand(CanExecute = nameof(CanExecutePrimaryAction))]
    private async Task ExecutePrimaryActionAsync()
    {
        if (IsAutomaticMode)
            await StartAutomaticExperimentAsync();
        else if (IsManualFinalAcquisitionReady)
            await StartFinalAcquisitionAsync();
        else if (IsManualStimulusReady)
            await StartStimulationAsync();
        else
            await StartAcquisitionAsync();
    }

    partial void OnSelectedModeChanged(ExperimentRunMode value)
    {
        OnPropertyChanged(nameof(IsCycleCountVisible));
        OnPropertyChanged(nameof(CurrentStageMonitorText));
        NotifyPrimaryActionProperties();
        RefreshValidationAndCommands();
    }

    partial void OnCurrentStageChanged(ExperimentRunStage value)
    {
        if (value != ExperimentRunStage.Acquisition)
            IsWaveformDisplayLagging = false;
        OnPropertyChanged(nameof(IsStimulating));
        OnPropertyChanged(nameof(CurrentStageMonitorText));
        NotifyPrimaryActionProperties();
    }

    partial void OnExportEdfChanged(bool value)
    {
        ExportDataCommand.NotifyCanExecuteChanged();
        SaveExportAsCommand.NotifyCanExecuteChanged();
    }

    partial void OnExportCsvChanged(bool value)
    {
        ExportDataCommand.NotifyCanExecuteChanged();
        SaveExportAsCommand.NotifyCanExecuteChanged();
    }

    partial void OnExportExppChanged(bool value)
    {
        ExportDataCommand.NotifyCanExecuteChanged();
        SaveExportAsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshWaveformWindow(int maximumPoints = WaveformPointsPerVisibleWindow)
    {
        foreach (var channel in WaveformChannels)
            channel.RefreshVisible(TimelineViewStart, TimelineViewEnd, maximumPoints);
    }

    private void ScheduleWaveformWindowRefresh()
    {
        if (_suppressWaveformRefresh || _waveformRefreshScheduled)
            return;
        if (Application.Current is null)
        {
            RefreshWaveformWindow();
            return;
        }

        _waveformRefreshScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _waveformRefreshScheduled = false;
                if (!_disposed)
                    RefreshWaveformWindow();
            },
            DispatcherPriority.Background
        );
    }

    private void ScheduleDebouncedWaveformDetailRefresh()
    {
        _waveformDetailRefreshCancellation?.Cancel();
        _waveformDetailRefreshCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _waveformDetailRefreshCancellation = cancellation;
        var expectedStart = TimelineViewStart;
        var expectedEnd = TimelineViewEnd;
        _ = RefreshWaveformDetailAfterDelayAsync(expectedStart, expectedEnd, cancellation.Token);
    }

    private async Task RefreshWaveformDetailAfterDelayAsync(
        double expectedStart,
        double expectedEnd,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(TimelineResizeDebounceDelay, cancellationToken).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (
                    _disposed
                    || cancellationToken.IsCancellationRequested
                    || Math.Abs(TimelineViewStart - expectedStart) > 0.0000001d
                    || Math.Abs(TimelineViewEnd - expectedEnd) > 0.0000001d
                )
                    return;
                RefreshWaveformWindow();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void CancelWaveformDetailRefresh()
    {
        _waveformDetailRefreshCancellation?.Cancel();
        _waveformDetailRefreshCancellation?.Dispose();
        _waveformDetailRefreshCancellation = null;
    }

    private void NotifyRunProperties()
    {
        OnPropertyChanged(nameof(StageDisplayText));
        OnPropertyChanged(nameof(CurrentStageMonitorText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(CycleText));
        OnPropertyChanged(nameof(ResultRunTimeText));
        OnPropertyChanged(nameof(ResultStatusText));
        OnPropertyChanged(nameof(ResultOverviewItems));
        OnPropertyChanged(nameof(CanGoBack));
        NotifyPrimaryActionProperties();
        StartStimulationCommand.NotifyCanExecuteChanged();
        EmergencyStopCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPrimaryActionProperties()
    {
        OnPropertyChanged(nameof(IsManualStimulusReady));
        OnPropertyChanged(nameof(IsManualFinalAcquisitionReady));
        OnPropertyChanged(nameof(IsPrimaryActionVisible));
        OnPropertyChanged(nameof(IsEmergencyOnlyFooter));
        OnPropertyChanged(nameof(IsEmergencyActionVisible));
        OnPropertyChanged(nameof(IsEmergencyActionEnabled));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(FooterPrimaryText));
        OnPropertyChanged(nameof(IsFooterPrimaryEnabled));
        ExecutePrimaryActionCommand.NotifyCanExecuteChanged();
        ExecuteFooterPrimaryCommand.NotifyCanExecuteChanged();
    }
}
