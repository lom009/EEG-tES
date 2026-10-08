using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public partial class ElectrodeConfigurationPageViewModel : PageViewModel, IDisposable
{
    private static readonly TimeSpan DetectionToggleThrottle = TimeSpan.FromMilliseconds(300);

    private readonly StimulusCapabilityProfile _capability;
    private readonly INavigationRouter _router;
    private readonly DialogService _dialogService;
    private readonly IDialogProvider _dialogHost;
    private readonly IImpedanceDetectionService _impedanceDetectionService;
    private readonly ImpedanceDetectionOptions _impedanceDetectionOptions;
    private readonly EegAcquisitionOptions _eegAcquisitionOptions;
    private readonly IExperimentPersistenceService? _experimentPersistence;
    private readonly IDeviceCapabilityAvailability _capabilityAvailability;
    private readonly IEegPhysicalChannelMappingService? _eegChannelMappings;
    private DateTimeOffset? _acquisitionMeasuredAt;
    private DateTimeOffset? _stimulationMeasuredAt;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly Dictionary<string, int> _pendingChannelSelections = [];
    private bool _suppressSiteChanges;
    private bool _isStimulusDetecting;
    private bool _isAcquisitionDetecting;
    private bool _isStoppingDetection;
    private bool _isDetectionToggleThrottled;
    private bool _disposed;
    private long _detectionSessionVersion;
    private CancellationTokenSource? _detectionCancellation;
    private Task? _detectionTask;
    private ElectrodeConfigurationMode? _activeDetectionMode;
    private IReadOnlyList<string> _activeDetectionElectrodeIds = [];
    private StimulationImpedanceDetectionRequest? _activeStimulationDetectionRequest;
    private string _detectionErrorText = string.Empty;

    [ObservableProperty]
    private ElectrodeConfigurationMode _currentMode = ElectrodeConfigurationMode.Stimulus;

    [ObservableProperty]
    private ElectrodeSiteViewModel? _selectedPoint;

    [ObservableProperty]
    private StimulusElectrodeSelectionOptionViewModel? _selectedStimulusSelectionOption;

    [ObservableProperty]
    private ElectrodeHeadOrientation _headOrientation;

    [ObservableProperty]
    private ImpedanceDetectionState _stimulusDetectionState;

    [ObservableProperty]
    private ImpedanceDetectionState _acquisitionDetectionState;

    [ObservableProperty]
    private bool _isConfigurationConfirmed;

    public ElectrodeConfigurationPageViewModel(
        ElectrodeConfigurationRouteData routeData,
        INavigationRouter router,
        DialogService dialogService,
        IDialogProvider dialogHost,
        StimulusCapabilityProfile? capability = null,
        IImpedanceDetectionService? impedanceDetectionService = null,
        ImpedanceDetectionOptions? impedanceDetectionOptions = null,
        IElectrodePositionCatalog? electrodeCatalog = null,
        EegAcquisitionOptions? eegAcquisitionOptions = null,
        IExperimentPersistenceService? experimentPersistence = null,
        IDeviceCapabilityAvailability? capabilityAvailability = null,
        IEegPhysicalChannelMappingService? eegChannelMappings = null,
        ISingleStimulusExperimentDialogService? singleStimulusDialog = null
    )
        : base(ApplicationPageNames.ElectrodeConfiguration, "电极与检测")
    {
        RouteData =
            routeData.CreationMode == ExperimentCreationMode.AcquisitionOnly
                ? routeData with
                {
                    StimulusConfiguration = AcquisitionOnlyConfiguration.StimulusPlaceholder,
                }
                : routeData;
        _singleStimulusDialog = singleStimulusDialog;
        _router = router;
        _dialogService = dialogService;
        _dialogHost = dialogHost;
        _capability = capability ?? StimulusCapabilityProfile.Default;
        _impedanceDetectionService =
            impedanceDetectionService ?? new PreviewImpedanceDetectionService();
        _impedanceDetectionOptions = impedanceDetectionOptions ?? new ImpedanceDetectionOptions();
        _eegAcquisitionOptions = eegAcquisitionOptions ?? new EegAcquisitionOptions();
        _experimentPersistence = experimentPersistence;
        _capabilityAvailability = capabilityAvailability ?? AllDeviceCapabilitiesAvailable.Instance;
        _eegChannelMappings = eegChannelMappings;
        electrodeCatalog ??= ElectrodePositionCatalog.Default;
        _uiSynchronizationContext = SynchronizationContext.Current;
        HeaderBadges =
        [
            new PageHeaderBadgeViewModel("患者ID", routeData.SubjectId),
            new PageHeaderBadgeViewModel("实验ID", routeData.ExperimentId),
            PageHeaderBadgeViewModel.CreateExperimentMode(routeData.CreationMode),
        ];
        StimulusSelectionGroups = [];
        StimulusSelectionOptions = [];
        BuildStimulusSelectionGroups();
        SelectedStimulusSelectionOption = StimulusSelectionOptions.FirstOrDefault();
        Points = CreatePoints(_capability, electrodeCatalog.Positions);
        if (IsAcquisitionOnly)
        {
            _currentMode = ElectrodeConfigurationMode.Acquisition;
            foreach (var point in Points.Where(p => p.IsStimulus))
            {
                point.ClearStimulusAssignment();
                point.Role = ElectrodeRole.None;
            }
        }
        if (IsStimulusOnly)
            foreach (
                var point in Points.Where(p =>
                    p.Role
                        is ElectrodeRole.Acquisition
                            or ElectrodeRole.Reference
                            or ElectrodeRole.Ground
                )
            )
                point.Role = ElectrodeRole.None;
        CurrentImpedanceItems = [];
        foreach (var point in Points)
            point.PropertyChanged += OnPointPropertyChanged;
        if (routeData.ImportedTemplate is { } importedTemplate)
            ApplyImportedTemplate(importedTemplate);
        RefreshAllState();
    }

    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges { get; }

    private readonly ISingleStimulusExperimentDialogService? _singleStimulusDialog;
    private bool _isConfirmationOpen;

    public bool IsStimulusOnly => RouteData.CreationMode == ExperimentCreationMode.StimulusOnly;
    public bool IsAcquisitionOnly =>
        RouteData.CreationMode == ExperimentCreationMode.AcquisitionOnly;
    public bool IsSingleMode => IsStimulusOnly || IsAcquisitionOnly;
    public string SingleModeTitle =>
        IsStimulusOnly ? "单刺激模式"
        : IsAcquisitionOnly ? "单采集模式"
        : string.Empty;
    public override bool CanGoBack => !IsAnyDetecting && !_isConfirmationOpen;

    public ElectrodeConfigurationRouteData RouteData { get; }

    public ObservableCollection<ElectrodeSiteViewModel> Points { get; }

    public ObservableCollection<ElectrodeImpedanceItemViewModel> CurrentImpedanceItems { get; }

    public ObservableCollection<StimulusElectrodeSelectionOptionViewModel> StimulusSelectionOptions { get; }

    public ObservableCollection<StimulusElectrodeSelectionGroupViewModel> StimulusSelectionGroups { get; }

    public StimulusConfigurationSnapshot StimulusConfiguration => RouteData.StimulusConfiguration;

    public bool IsStimulusMode => CurrentMode == ElectrodeConfigurationMode.Stimulus;

    public bool IsAcquisitionMode => CurrentMode == ElectrodeConfigurationMode.Acquisition;

    public string HeadOrientationText =>
        HeadOrientation == ElectrodeHeadOrientation.Front ? "前视角" : "后视角";

    public string StimulusKindText =>
        StimulusParameterPolicy.ModeName(
            StimulusConfiguration.Kind,
            StimulusConfiguration.ShamMode
        );

    public string ArrayModeText =>
        StimulusConfiguration.ArrayMode switch
        {
            StimulusArrayMode.Hd => "HD",
            StimulusArrayMode.MultiTarget => "多靶点",
            _ => "双通道",
        };

    public string StimulusChannelCurrentSummary =>
        string.Join("；", GetStimulusChannelCurrentGroups());

    public bool HasStimulusChannelCurrentSummary =>
        !string.IsNullOrWhiteSpace(StimulusChannelCurrentSummary);

    public bool ShowStimulusChannelCurrentSummary =>
        IsStimulusMode && HasStimulusChannelCurrentSummary;

    public string DirectionText =>
        StimulusConfiguration.Direction switch
        {
            StimulusDirection.Positive => "正向",
            StimulusDirection.Negative => "反向",
            StimulusDirection.Bidirectional => "双向",
            _ => "-",
        };

    public ResolvedStimulationRoles ResolvedRoles =>
        StimulationElectrodeRoleResolver.Resolve(StimulusConfiguration.Direction);

    public string PolarityHintText => ResolvedRoles.Description;

    public int RequiredStimulusElectrodeCount =>
        StimulusSelectionOptions.Sum(option => option.RequiredCount);

    public int SelectedStimulusElectrodeCount => Points.Count(point => point.IsStimulus);

    public string StimulusSelectionProgressText =>
        $"点位映射 {SelectedStimulusElectrodeCount}/{RequiredStimulusElectrodeCount}";

    public string StimulusRequirementText =>
        string.Join(
            "；",
            StimulusSelectionGroups.Select(group =>
                $"{group.DisplayName}：{string.Join("、", group.Options.Select(option => $"{option.Label} {option.RequiredCount} 个"))}"
            )
        );

    public string StimulusRequirementDetailsText => StimulusRequirementText;

    public string SupportedStimulusPointsText =>
        $"当前支持 {_capability.StimulusSiteIds.Count} 个刺激点位：{string.Join("、", Points.Where(point => point.CanStimulate).Select(point => point.PositionName))}";

    public string RefGndText
    {
        get
        {
            var reference =
                Points.FirstOrDefault(point => point.Role == ElectrodeRole.Reference)?.PositionName
                ?? "-";
            var ground =
                Points.FirstOrDefault(point => point.Role == ElectrodeRole.Ground)?.PositionName
                ?? "-";
            return $"{reference}/{ground}";
        }
    }

    public string SelectedPointRoleText =>
        SelectedPoint is null
            ? "点击头模点位进行配置"
            : $"当前点位 {SelectedPoint.PositionName} · {SelectedPoint.RoleText}"
                + (SelectedPoint.IsStimulus
                    ? SelectedPoint.StimulationPhysicalChannelId is { } channel ? $" · CH{channel}" : " · 待分配物理通道"
                    : string.Empty);

    public bool HasSelectedPoint => SelectedPoint is not null;

    public bool HasCurrentImpedanceItems => CurrentImpedanceItems.Count > 0;

    public bool HasChannelConflicts => _pendingChannelSelections.Count > 0;

    public bool IsStimulusPositionSelectionComplete =>
        StimulusSelectionOptions.Count > 0
        && StimulusSelectionOptions.All(option => option.AssignedCount == option.RequiredCount);

    public bool IsStimulusChannelMappingComplete
    {
        get
        {
            var points = Points.Where(point => point.IsStimulus).ToArray();
            if (
                !IsStimulusPositionSelectionComplete
                || points.Any(point => !point.HasStimulationChannel)
                || points.Select(point => point.StimulationPhysicalChannelId).Distinct().Count()
                    != points.Length
            )
                return false;

            foreach (var target in StimulusConfiguration.Targets)
            {
                var targetPoints = points
                    .Where(point => point.StimulusTargetId == target.TargetId)
                    .ToArray();
                var fixedCount = targetPoints.Count(point =>
                    point.StimulationChannelRole == StimulationChannelRole.FixedActive
                );
                var selectableCount = targetPoints.Count(point =>
                    point.StimulationChannelRole == StimulationChannelRole.Selectable
                );
                var requiredSelectable =
                    StimulusConfiguration.ArrayMode == StimulusArrayMode.DualChannel
                        ? _capability.DualChannelSelectableCount
                        : _capability.HdSelectableChannelCount;
                if (fixedCount != 1 || selectableCount != requiredSelectable)
                    return false;
            }
            return true;
        }
    }

    public bool IsStimulusConfigurationComplete =>
        IsStimulusChannelMappingComplete && !HasChannelConflicts;

    public bool IsAcquisitionConfigurationComplete =>
        Points.Any(point => point.Role == ElectrodeRole.Acquisition)
        && Points.Any(point => point.Role == ElectrodeRole.Reference)
        && Points.Any(point => point.Role == ElectrodeRole.Ground);

    public bool CanDetectCurrentMode =>
        !(IsAcquisitionOnly && IsStimulusMode)
        && (IsStimulusMode ? IsStimulusConfigurationComplete : IsAcquisitionConfigurationComplete);

    public bool IsAnyDetecting => _isStimulusDetecting || _isAcquisitionDetecting;

    public bool CanEditConfiguration => !IsAnyDetecting;

    public bool CanToggleDetection =>
        !_isStoppingDetection
        && !(IsAcquisitionOnly && IsStimulusMode)
        && !_isDetectionToggleThrottled
        && IsCurrentDetectionCapabilityEnabled
        && (IsCurrentDetecting || !IsAnyDetecting && CanDetectCurrentMode);

    public bool IsCurrentDetectionCapabilityEnabled =>
        _capabilityAvailability.IsEnabled(CurrentImpedanceCapability);

    public string DetectionUnavailableReason =>
        _capabilityAvailability.GetUnavailableReason(CurrentImpedanceCapability);

    private DeviceCapabilityKind CurrentImpedanceCapability =>
        IsStimulusMode
            ? DeviceCapabilityKind.StimulationImpedance
            : DeviceCapabilityKind.EegImpedance;

    public ImpedanceDetectionState CurrentDetectionState =>
        IsStimulusMode ? StimulusDetectionState : AcquisitionDetectionState;

    public bool IsCurrentDetectionPassed => CurrentDetectionState == ImpedanceDetectionState.Passed;

    public bool IsCurrentDetectionFailed => CurrentDetectionState == ImpedanceDetectionState.Failed;

    public string CurrentDetectionStatusText =>
        CurrentDetectionState switch
        {
            _ when IsCurrentDetecting => "检测中",
            ImpedanceDetectionState.Passed => "已通过",
            ImpedanceDetectionState.Failed => "未通过",
            _ => string.Empty,
        };

    public bool IsCurrentDetecting =>
        IsStimulusMode ? _isStimulusDetecting : _isAcquisitionDetecting;

    public string DetectionButtonText => IsCurrentDetecting ? "停止" : "检测";

    public string DetectionErrorText => _detectionErrorText;

    public string CurrentPanelTitle => IsStimulusMode ? "刺激通道映射与阻抗" : "采集阻抗状态";

    public string EmptyStateTitle =>
        IsStimulusMode && SelectedStimulusElectrodeCount == 0
            ? "尚未选择刺激电极位置"
            : "未开始检测";

    public string EmptyStateDescription =>
        IsStimulusMode
            ? "请先选择刺激电极位置，并为每个位置分配刺激物理通道"
            : "请先完成采集点位配置，再开始阻抗检测";

    public string ValidationText
    {
        get
        {
            if (!IsCurrentDetectionCapabilityEnabled)
                return DetectionUnavailableReason;
            if (!string.IsNullOrWhiteSpace(DetectionErrorText))
                return DetectionErrorText;
            if (IsStimulusMode && _capability.FixedActivePhysicalChannelIds.Count == 0)
                return "未配置固定刺激物理通道，请检查 StimulationChannels 配置";
            if (IsStimulusMode && !IsStimulusPositionSelectionComplete)
                return $"请完成刺激电极位置选择：{StimulusSelectionProgressText}";
            if (IsStimulusMode && !IsStimulusChannelMappingComplete)
                return "请为每个刺激电极位置分配唯一通道，并确保固定刺激/自选刺激通道数量正确";
            if (IsAcquisitionMode && !IsAcquisitionConfigurationComplete)
                return "请至少配置 1 个采集电极，并设置 REF 与 GND";
            if (IsCurrentDetectionFailed)
                return "请调整电极或补充导电膏，停止后可重新检测";
            return string.Empty;
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationText);

    public bool CanOpenConfirmation =>
        !IsAnyDetecting
        && !_isConfirmationOpen
        && HasAcceptableDetectionResults
        && (IsAcquisitionOnly || IsStimulusConfigurationComplete)
        && (IsStimulusOnly || IsAcquisitionConfigurationComplete);

    private bool HasAcceptableDetectionResults =>
        _impedanceDetectionOptions.AllowConfirmationWhenFailed
            ? (IsAcquisitionOnly || StimulusDetectionState != ImpedanceDetectionState.NotStarted)
                && (
                    IsStimulusOnly
                    || AcquisitionDetectionState != ImpedanceDetectionState.NotStarted
                )
            : (IsAcquisitionOnly || StimulusDetectionState == ImpedanceDetectionState.Passed)
                && (IsStimulusOnly || AcquisitionDetectionState == ImpedanceDetectionState.Passed);

    public string ConfirmationStatusText =>
        IsConfigurationConfirmed ? "实验配置已确认" : string.Empty;

    public string StimulusSummary => $"{StimulusKindText}/{DirectionText} · {ArrayModeText}";

    public string StimulusParameterSummary
    {
        get
        {
            var peaks = string.Join(
                "；",
                StimulusConfiguration
                    .Targets.OrderBy(target => target.DisplayOrder)
                    .Select(target => $"靶点{target.DisplayOrder}峰值 {target.PeakCurrent:0.00} mA")
            );
            if (StimulusConfiguration.Envelope is { IsDeviceSetup: true } envelope)
                return $"{peaks}；启动延时 {envelope.DelayMilliseconds:0} ms";
            var waveform = StimulusParameterPolicy
                .For(StimulusConfiguration.Kind, StimulusConfiguration.ShamMode)
                .FormatSummary(
                    StimulusConfiguration.Frequency,
                    StimulusConfiguration.RampSeconds,
                    StimulusConfiguration.DutyPercent
                );
            return string.IsNullOrEmpty(waveform) ? peaks : $"{peaks}；{waveform}";
        }
    }

    public string StimulusElectrodeSummary
    {
        get
        {
            var groups = StimulusConfiguration
                .Targets.OrderBy(target => target.DisplayOrder)
                .Select(target =>
                {
                    var assignments = Points
                        .Where(point =>
                            point.StimulusTargetId == target.TargetId && point.HasStimulationChannel
                        )
                        .OrderBy(point => point.StimulationChannelRole)
                        .ThenBy(point => point.StimulationPhysicalChannelId)
                        .Select(point =>
                            $"CH{point.StimulationPhysicalChannelId}={point.PositionName}({point.RoleText})"
                        );
                    return $"靶点{target.DisplayOrder}：{string.Join("、", assignments)}";
                });
            return $"{string.Join("；", groups)}；{PolarityHintText}";
        }
    }

    public string AcquisitionElectrodeSummary =>
        $"EEG：{JoinPointNames(ElectrodeRole.Acquisition)}；REF/GND：{RefGndText}";

    public string SafetySummary =>
        HasAcceptableDetectionResults
            ? (
                IsAcquisitionOnly ? "采集阻抗检测已完成"
                : IsStimulusOnly ? "刺激阻抗检测已完成"
                : "刺激与采集阻抗均已通过"
            )
            : "阻抗检测尚未全部通过";

    public IReadOnlyList<StimulusElectrodeAssignment> CreateStimulusAssignments() =>
        IsAcquisitionOnly
            ? []
            : Points
                .Select(point => point.CreateAssignment())
                .OfType<StimulusElectrodeAssignment>()
                .ToArray();

    private void ApplyImportedTemplate(ExperimentConfigurationTemplate template)
    {
        _suppressSiteChanges = true;
        foreach (
            var assignment in IsAcquisitionOnly
                ? Array.Empty<StimulusElectrodeAssignment>()
                : template.StimulusElectrodes
        )
        {
            var point = Points.Single(x =>
                string.Equals(x.Name, assignment.SiteId, StringComparison.OrdinalIgnoreCase)
            );
            var target = template.StimulusConfiguration.Targets.Single(x =>
                x.TargetId == assignment.TargetId
            );
            point.AssignStimulusPosition(
                assignment.TargetId,
                target.DisplayOrder,
                assignment.PhysicalChannelId,
                assignment.Role,
                template.StimulusConfiguration.Direction
            );
            point.AssignStimulationChannel(
                new StimulationChannelOptionViewModel(
                    assignment.PhysicalChannelId,
                    assignment.Role,
                    template.StimulusConfiguration.Direction
                )
            );
            point.Impedance = null;
        }
        foreach (
            var siteId in IsStimulusOnly ? Array.Empty<string>() : template.AcquisitionChannels
        )
            Points
                .Single(x => string.Equals(x.Name, siteId, StringComparison.OrdinalIgnoreCase))
                .Role = ElectrodeRole.Acquisition;
        if (!IsStimulusOnly && !string.IsNullOrEmpty(template.ReferenceChannel))
            Points
                .Single(x =>
                    string.Equals(
                        x.Name,
                        template.ReferenceChannel,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Role = ElectrodeRole.Reference;
        if (!IsStimulusOnly && !string.IsNullOrEmpty(template.GroundChannel))
            Points
                .Single(x =>
                    string.Equals(
                        x.Name,
                        template.GroundChannel,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Role = ElectrodeRole.Ground;
        _suppressSiteChanges = false;
        StimulusDetectionState = ImpedanceDetectionState.NotStarted;
        AcquisitionDetectionState = ImpedanceDetectionState.NotStarted;
    }

    partial void OnCurrentModeChanged(ElectrodeConfigurationMode value)
    {
        SelectedPoint = null;
        RefreshAllState();
    }

    partial void OnSelectedPointChanged(ElectrodeSiteViewModel? value)
    {
        foreach (var point in Points)
            point.IsSelected = ReferenceEquals(point, value);
        OnPropertyChanged(nameof(HasSelectedPoint));
        OnPropertyChanged(nameof(SelectedPointRoleText));
    }

    partial void OnSelectedStimulusSelectionOptionChanged(
        StimulusElectrodeSelectionOptionViewModel? value
    )
    {
        foreach (var option in StimulusSelectionOptions)
            option.IsSelected = ReferenceEquals(option, value);
    }

    partial void OnHeadOrientationChanged(ElectrodeHeadOrientation value) =>
        OnPropertyChanged(nameof(HeadOrientationText));

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private void SelectMode(ElectrodeConfigurationMode mode)
    {
        if (
            (!IsStimulusOnly || mode == ElectrodeConfigurationMode.Stimulus)
            && (!IsAcquisitionOnly || mode == ElectrodeConfigurationMode.Acquisition)
        )
            CurrentMode = mode;
    }

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private void SelectStimulusSelectionOption(StimulusElectrodeSelectionOptionViewModel? option)
    {
        if (option is not null)
            SelectedStimulusSelectionOption = option;
    }

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private void ToggleHeadOrientation() =>
        HeadOrientation =
            HeadOrientation == ElectrodeHeadOrientation.Front
                ? ElectrodeHeadOrientation.Back
                : ElectrodeHeadOrientation.Front;

    // Panel focus only changes the inspected site; it must never assign a new role.
    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private void FocusPoint(ElectrodeSiteViewModel? point)
    {
        if (point is not null && Points.Contains(point))
            SelectedPoint = point;
    }

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private void SelectPoint(ElectrodeSiteViewModel? point)
    {
        if (point is null || !point.IsAvailable)
            return;
        SelectedPoint = point;
        if (IsStimulusMode)
        {
            var option = SelectedStimulusSelectionOption;
            if (point.IsStimulus || option is null || option.AssignedCount >= option.RequiredCount)
                return;
            point.AssignStimulusPosition(
                option.TargetId,
                option.TargetDisplayOrder,
                SelectedStimulusElectrodeCount + 1,
                option.Role,
                StimulusConfiguration.Direction
            );
            TryAssignOnlyFixedStimulationChannel(point);
            point.Impedance = null;
            InvalidateMode(ElectrodeConfigurationMode.Stimulus);
            RefreshAllState();
            return;
        }
        if (point.Role == ElectrodeRole.None)
            SetPointRole(point, ElectrodeRole.Acquisition);
    }

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private void SetSelectedPointRole(ElectrodeRole role)
    {
        if (SelectedPoint is null || SelectedPoint.IsStimulus)
            return;
        if (role is ElectrodeRole.Reference or ElectrodeRole.Ground)
        {
            var existing = Points.FirstOrDefault(point =>
                point.Role == role && !ReferenceEquals(point, SelectedPoint)
            );
            if (existing is not null)
                SetPointRole(existing, ElectrodeRole.None);
        }
        SetPointRole(SelectedPoint, role);
    }

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private void ClearSelectedPoint()
    {
        if (SelectedPoint is null)
            return;

        ClearPoint(SelectedPoint);
    }

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private void ClearPoint(ElectrodeSiteViewModel? point)
    {
        if (point is null || point.Role == ElectrodeRole.None && !point.IsStimulus)
            return;

        _pendingChannelSelections.Remove(point.Name);
        SetPointRole(point, ElectrodeRole.None);
        ReindexStimulusPositions();
        SelectedPoint = null;
    }

    [RelayCommand(CanExecute = nameof(CanToggleDetection))]
    private async Task ToggleDetectionAsync()
    {
        if (IsAcquisitionOnly && IsStimulusMode)
            return;
        if (_isDetectionToggleThrottled)
            return;

        _isDetectionToggleThrottled = true;
        NotifyDetectionStateChanged();
        var throttleDelay = Task.Delay(DetectionToggleThrottle);
        try
        {
            if (IsAnyDetecting)
            {
                await StopDetectionAsync();
                return;
            }

            var mode = CurrentMode;
            try
            {
                StartDetection(mode);
            }
            catch (Exception exception)
            {
                await HandleDetectionStartupExceptionAsync(mode, exception);
            }
        }
        finally
        {
            await throttleDelay;
            _isDetectionToggleThrottled = false;
            if (!_disposed)
                NotifyDetectionStateChanged();
        }
    }

    private void StartDetection(ElectrodeConfigurationMode mode)
    {
        _detectionErrorText = string.Empty;
        _activeDetectionMode = mode;
        _activeDetectionElectrodeIds = GetImpedanceDetectionPoints(mode)
            .Select(point => point.Name)
            .ToArray();
        if (mode == ElectrodeConfigurationMode.Stimulus)
        {
            var assignments = CreateStimulusAssignments();
            _activeStimulationDetectionRequest = new StimulationImpedanceDetectionRequest(
                ExperimentStimulusConfigurationSnapshot.From(StimulusConfiguration, assignments),
                assignments
            );
        }
        else
        {
            _activeStimulationDetectionRequest = null;
        }
        var detectionCancellation = new CancellationTokenSource();
        var detectionToken = detectionCancellation.Token;
        _detectionCancellation = detectionCancellation;
        var sessionVersion = ++_detectionSessionVersion;
        if (mode == ElectrodeConfigurationMode.Stimulus)
            _isStimulusDetecting = true;
        else
            _isAcquisitionDetecting = true;
        NotifyDetectionStateChanged();
        _detectionTask = RunDetectionAsync(mode, sessionVersion, detectionToken);
        if (IsAutomaticStopEnabled(mode) && IsActiveDetectionSession(sessionVersion))
        {
            _ = StopDetectionWhenNotPassedAsync(mode, sessionVersion, detectionToken);
        }
    }

    private async Task HandleDetectionStartupExceptionAsync(
        ElectrodeConfigurationMode mode,
        Exception exception
    )
    {
        ++_detectionSessionVersion;
        var cancellation = _detectionCancellation;
        _detectionCancellation = null;
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException) { }
        cancellation?.Dispose();

        _detectionTask = null;
        _activeDetectionMode = null;
        _activeDetectionElectrodeIds = [];
        _activeStimulationDetectionRequest = null;
        _isStimulusDetecting = false;
        _isAcquisitionDetecting = false;
        _isStoppingDetection = false;
        SetDetectionState(mode, ImpedanceDetectionState.Failed);

        var message =
            exception is ImpedanceDetectionConfigurationException
                ? exception.Message
                : "阻抗检测会话初始化失败，请检查配置后重试。";
        _detectionErrorText = message;
        NotifyDetectionStateChanged();
        await ShowDetectionErrorDialogAsync("启动阻抗检测失败", message);
    }

    private async Task RunDetectionAsync(
        ElectrodeConfigurationMode mode,
        long sessionVersion,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var stream =
                mode == ElectrodeConfigurationMode.Stimulus
                    ? _impedanceDetectionService.WatchStimulationAsync(
                        RouteData.DeviceId,
                        GetActiveStimulationDetectionRequest(),
                        cancellationToken
                    )
                    : _impedanceDetectionService.WatchEegAsync(
                        RouteData.DeviceId,
                        _activeDetectionElectrodeIds,
                        cancellationToken
                    );
            await foreach (var readings in stream.WithCancellation(cancellationToken))
            {
                if (!IsActiveDetectionSession(sessionVersion))
                    return;
                await ApplyImpedanceReadingsOnUiAsync(mode, readings);
                if (ShouldAutoStopAfterPass(mode, sessionVersion))
                {
                    await StopDetectionSessionAsync(
                        mode,
                        sessionVersion,
                        waitForDetectionTask: false
                    );
                    return;
                }
            }

            if (IsActiveDetectionSession(sessionVersion))
                await FailDetectionAsync(
                    mode,
                    sessionVersion,
                    "阻抗检测数据流已结束，请检查设备连接后重试"
                );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (DeviceCommandRejectedException exception)
        {
            if (IsActiveDetectionSession(sessionVersion))
                await RejectDetectionAsync(mode, sessionVersion, exception);
        }
        catch (ImpedanceDetectionConfigurationException exception)
        {
            if (IsActiveDetectionSession(sessionVersion))
            {
                await RejectDetectionAsync(
                    mode,
                    sessionVersion,
                    "阻抗检测配置不受支持",
                    exception.Message
                );
            }
        }
        catch (EegImpedanceDataTimeoutException exception)
        {
            if (IsActiveDetectionSession(sessionVersion))
                await RejectEegDataTimeoutAsync(mode, sessionVersion, exception);
        }
        catch (Exception)
        {
            if (IsActiveDetectionSession(sessionVersion))
                await FailDetectionAsync(
                    mode,
                    sessionVersion,
                    "阻抗检测失败，请检查设备连接后重试"
                );
        }
    }

    private async Task StopDetectionAsync()
    {
        if (_activeDetectionMode is not { } mode || _isStoppingDetection)
            return;
        await StopDetectionSessionAsync(mode, _detectionSessionVersion, waitForDetectionTask: true);
    }

    private bool IsAutomaticStopEnabled(ElectrodeConfigurationMode mode) =>
        mode == ElectrodeConfigurationMode.Stimulus
            ? _impedanceDetectionOptions.AutoStopStimulationWhenPassed
            : _impedanceDetectionOptions.AutoStopEegWhenPassed;

    private bool ShouldAutoStopAfterPass(ElectrodeConfigurationMode mode, long sessionVersion) =>
        IsActiveDetectionSession(sessionVersion)
        && IsAutomaticStopEnabled(mode)
        && GetDetectionState(mode) == ImpedanceDetectionState.Passed;

    private async Task StopDetectionWhenNotPassedAsync(
        ElectrodeConfigurationMode mode,
        long sessionVersion,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(
                _impedanceDetectionOptions.AutoStopWhenNotPassedTimeout,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var seconds = _impedanceDetectionOptions.AutoStopWhenNotPassedTimeout.TotalSeconds;
        await StopDetectionSessionAsync(
            mode,
            sessionVersion,
            waitForDetectionTask: false,
            onlyIfNotPassed: true,
            completedStopMessage: $"阻抗检测在 {seconds:0.###} 秒内未通过，已自动停止"
        );
    }

    private async Task StopDetectionSessionAsync(
        ElectrodeConfigurationMode mode,
        long sessionVersion,
        bool waitForDetectionTask,
        bool onlyIfNotPassed = false,
        string? completedStopMessage = null
    )
    {
        var stopStarted = false;
        Task? detectionTask = null;
        await RunOnUiAsync(() =>
        {
            if (
                !IsActiveDetectionSession(sessionVersion)
                || _activeDetectionMode != mode
                || _isStoppingDetection
                || (onlyIfNotPassed && GetDetectionState(mode) == ImpedanceDetectionState.Passed)
            )
            {
                return;
            }

            stopStarted = true;
            _isStoppingDetection = true;
            detectionTask = _detectionTask;
            ++_detectionSessionVersion;
            _detectionCancellation?.Cancel();
            NotifyDetectionStateChanged();
        });
        if (!stopStarted)
            return;

        Exception? stopError = null;
        try
        {
            await StopDeviceDetectionAsync(mode);
            if (waitForDetectionTask && detectionTask is not null)
                await detectionTask;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            stopError = exception;
        }
        await RunOnUiAsync(() =>
        {
            if (stopError is not null)
            {
                SetDetectionState(mode, ImpedanceDetectionState.Failed);
                _detectionErrorText = "停止阻抗检测失败，请检查设备连接";
            }
            else if (completedStopMessage is not null)
            {
                SetDetectionState(mode, ImpedanceDetectionState.Failed);
                _detectionErrorText = completedStopMessage;
            }
            CompleteDetectionSession(mode);
        });
        if (stopError is not null)
        {
            await ShowDetectionErrorDialogAsync(
                mode == ElectrodeConfigurationMode.Stimulus
                    ? "停止刺激阻抗检测失败"
                    : "停止采集阻抗检测失败",
                stopError is DeviceCommandRejectedException rejected
                    ? GetDeviceRejectionMessage(rejected)
                    : "停止命令未能正常完成，请检查设备连接后重试。"
            );
        }
    }

    private async Task RejectDetectionAsync(
        ElectrodeConfigurationMode mode,
        long sessionVersion,
        DeviceCommandRejectedException exception
    )
    {
        var message = GetDeviceRejectionMessage(exception);
        await RejectDetectionAsync(
            mode,
            sessionVersion,
            mode == ElectrodeConfigurationMode.Stimulus
                ? "启动刺激阻抗检测失败"
                : "启动采集阻抗检测失败",
            message
        );
    }

    private async Task RejectDetectionAsync(
        ElectrodeConfigurationMode mode,
        long sessionVersion,
        string title,
        string message
    )
    {
        if (!IsActiveDetectionSession(sessionVersion))
            return;
        ++_detectionSessionVersion;
        _detectionCancellation?.Cancel();
        await RunOnUiAsync(() =>
        {
            SetDetectionState(mode, ImpedanceDetectionState.Failed);
            _detectionErrorText = message;
            CompleteDetectionSession(mode);
        });
        await ShowDetectionErrorDialogAsync(title, message);
    }

    private async Task RejectEegDataTimeoutAsync(
        ElectrodeConfigurationMode mode,
        long sessionVersion,
        EegImpedanceDataTimeoutException exception
    )
    {
        var stopStarted = false;
        await RunOnUiAsync(() =>
        {
            if (!IsActiveDetectionSession(sessionVersion) || _isStoppingDetection)
                return;

            stopStarted = true;
            _isStoppingDetection = true;
            ++_detectionSessionVersion;
            _detectionCancellation?.Cancel();
            NotifyDetectionStateChanged();
        });
        if (!stopStarted)
            return;

        if (!exception.StopCommandRequested)
        {
            try
            {
                await StopDeviceDetectionAsync(mode);
            }
            catch
            {
                // Preserve the EEG data-timeout error after making the required stop attempt.
            }
        }

        const string message = "设备未上传 EEG 阻抗数据，请检查电极连接后重试。";
        await RunOnUiAsync(() =>
        {
            SetDetectionState(mode, ImpedanceDetectionState.Failed);
            _detectionErrorText = message;
            CompleteDetectionSession(mode);
        });
        await ShowDetectionErrorDialogAsync("EEG 阻抗数据超时", message);
    }

    private Task ShowDetectionErrorDialogAsync(string title, string message)
    {
        var dialog = new ConfirmDialogViewModel(DialogKind.Error)
        {
            Title = title,
            Message = message,
            ConfirmText = "确认",
            ShowCancelButton = false,
        };
        return _dialogService.ShowDialog(_dialogHost, dialog);
    }

    private static string GetDeviceRejectionMessage(DeviceCommandRejectedException exception)
    {
        var isEeg = exception.Operation.Contains("EEG", StringComparison.OrdinalIgnoreCase);
        var reason = exception.Status switch
        {
            DeviceCommandStatus.InvalidParameter => isEeg
                ? "设备返回配置参数错误，请检查采集物理通道配置。"
                : "设备返回配置参数错误，请检查刺激通道配置。",
            DeviceCommandStatus.DeviceBusy => "设备正在运行中，当前操作不可用。",
            DeviceCommandStatus.Failed => $"设备返回失败，未能{exception.Operation}。",
            _ => $"设备拒绝了阻抗检测命令：{exception.Status}。",
        };
        return string.IsNullOrWhiteSpace(exception.DeviceMessage)
            ? reason
            : $"{reason} {exception.DeviceMessage}";
    }

    private async Task FailDetectionAsync(
        ElectrodeConfigurationMode mode,
        long sessionVersion,
        string message
    )
    {
        if (!IsActiveDetectionSession(sessionVersion))
            return;
        ++_detectionSessionVersion;
        _detectionCancellation?.Cancel();
        try
        {
            await StopDeviceDetectionAsync(mode);
        }
        catch { }
        await RunOnUiAsync(() =>
        {
            SetDetectionState(mode, ImpedanceDetectionState.Failed);
            _detectionErrorText = message;
            CompleteDetectionSession(mode);
        });
    }

    private Task StopDeviceDetectionAsync(ElectrodeConfigurationMode mode) =>
        mode == ElectrodeConfigurationMode.Stimulus
            ? _impedanceDetectionService.StopStimulationAsync(
                RouteData.DeviceId,
                GetActiveStimulationDetectionRequest()
            )
            : _impedanceDetectionService.StopEegAsync(
                RouteData.DeviceId,
                _activeDetectionElectrodeIds
            );

    private bool IsActiveDetectionSession(long sessionVersion) =>
        !_disposed && sessionVersion == _detectionSessionVersion && IsAnyDetecting;

    private void SetDetectionState(ElectrodeConfigurationMode mode, ImpedanceDetectionState state)
    {
        if (mode == ElectrodeConfigurationMode.Stimulus)
            StimulusDetectionState = state;
        else
            AcquisitionDetectionState = state;
    }

    private ImpedanceDetectionState GetDetectionState(ElectrodeConfigurationMode mode) =>
        mode == ElectrodeConfigurationMode.Stimulus
            ? StimulusDetectionState
            : AcquisitionDetectionState;

    private void CompleteDetectionSession(ElectrodeConfigurationMode mode)
    {
        if (mode == ElectrodeConfigurationMode.Stimulus)
            _isStimulusDetecting = false;
        else
            _isAcquisitionDetecting = false;
        _isStoppingDetection = false;
        _activeDetectionMode = null;
        _activeDetectionElectrodeIds = [];
        _activeStimulationDetectionRequest = null;
        _detectionCancellation?.Dispose();
        _detectionCancellation = null;
        _detectionTask = null;
        NotifyDetectionStateChanged();
    }

    internal EnvelopeConfirmationData? CreateEnvelopeConfirmationData()
    {
        if (!IsStimulusOnly || StimulusConfiguration.Kind != StimulusKind.EnvelopeTAcs)
            return null;

        string ChannelText(StimulationChannelRole role) => string.Join(
            "、",
            Points.Where(point => point.HasStimulationChannel && point.StimulationChannelRole == role)
                .OrderBy(point => point.StimulationPhysicalChannelId)
                .Select(point => $"{point.PositionName} / CH{point.StimulationPhysicalChannelId}")
        );

        var delay = StimulusConfiguration.Envelope?.DelayMilliseconds ?? 0;
        return new EnvelopeConfirmationData(
            FormattableString.Invariant($"{StimulusConfiguration.Targets.Single().PeakCurrent:0.##} mA"),
            FormattableString.Invariant($"{delay:0} ms ({delay / 1000d:0.###} s)"),
            ChannelText(StimulationChannelRole.FixedActive),
            ChannelText(StimulationChannelRole.Selectable)
        );
    }

    [RelayCommand(CanExecute = nameof(CanOpenConfirmation))]
    private async Task OpenConfirmationAsync()
    {
        if (!CanOpenConfirmation)
            return;
        _isConfirmationOpen = true;
        OnPropertyChanged(nameof(CanGoBack));
        OpenConfirmationCommand.NotifyCanExecuteChanged();
        try
        {
            var dialog = new ExperimentConfigurationDialogViewModel(
                RouteData.SubjectId,
                RouteData.ExperimentId,
                StimulusSummary,
                StimulusParameterSummary,
                StimulusElectrodeSummary,
                AcquisitionElectrodeSummary,
                SafetySummary,
                showAcquisition: !IsStimulusOnly,
                showStimulation: !IsAcquisitionOnly,
                envelopeConfirmation: CreateEnvelopeConfirmationData()
            );
            await _dialogService.ShowDialog(_dialogHost, dialog);
            if (!dialog.Confirmed)
                return;
            var runRoute = CreateExperimentRunRouteData();
            if (_experimentPersistence is not null && RouteData.ExperimentDatabaseId > 0)
            {
                var assignments = CreateStimulusAssignments();
                var timing = IsStimulusOnly
                    ? new ExperimentTimingTemplate(
                        ExperimentRunMode.Manual,
                        0,
                        0,
                        RouteData.ImportedTemplate?.Timing.StimulationMilliseconds ?? 600_000,
                        0,
                        1
                    )
                    : RouteData.ImportedTemplate?.Timing
                        ?? new ExperimentTimingTemplate(
                            ExperimentRunMode.Manual,
                            10_000,
                            1_000,
                            15_000,
                            1_000,
                            1
                        );
                if (IsAcquisitionOnly)
                    timing = AcquisitionOnlyConfiguration.NormalizeTiming(timing);
                var template = new ExperimentConfigurationTemplate(
                    StimulusConfiguration,
                    assignments,
                    Points
                        .Where(x => x.Role == ElectrodeRole.Acquisition)
                        .Select(x => x.Name)
                        .ToArray(),
                    IsStimulusOnly
                        ? string.Empty
                        : Points.Single(x => x.Role == ElectrodeRole.Reference).Name,
                    IsStimulusOnly
                        ? string.Empty
                        : Points.Single(x => x.Role == ElectrodeRole.Ground).Name,
                    _eegAcquisitionOptions.SampleRateHz,
                    timing,
                    RouteData.ImportedTemplate?.SourceDisplayName ?? "当前实验",
                    CreationMode: RouteData.CreationMode
                );
                var measuredAt = DateTimeOffset.UtcNow;
                var impedances = Points
                    .Where(x => x.IsStimulus || x.IsAcquisition)
                    .SelectMany(point =>
                    {
                        var values = new List<ImpedanceSnapshotValue>();
                        if (point.IsStimulus)
                            values.Add(
                                new ImpedanceSnapshotValue(
                                    point.Name,
                                    ImpedanceMeasurementKind.Stimulation,
                                    point.Impedance,
                                    point.Impedance is <= 10,
                                    measuredAt
                                )
                            );
                        if (point.IsAcquisition)
                            values.Add(
                                new ImpedanceSnapshotValue(
                                    point.Name,
                                    ImpedanceMeasurementKind.Acquisition,
                                    point.Impedance,
                                    point.Role is ElectrodeRole.Reference or ElectrodeRole.Ground
                                        || point.Impedance is <= 10,
                                    measuredAt
                                )
                            );
                        return values;
                    })
                    .ToArray();
                await _experimentPersistence.SaveConfigurationAsync(
                    RouteData.ExperimentDatabaseId,
                    template,
                    impedances
                );
            }
            IsConfigurationConfirmed = true;
            OnPropertyChanged(nameof(ConfirmationStatusText));
            if (IsStimulusOnly && StimulusConfiguration.Kind == StimulusKind.EnvelopeTAcs)
            {
                _router.Navigate(
                    new EnvelopeStimulationRouteData(
                        runRoute,
                        StimulusDetectionState == ImpedanceDetectionState.Passed
                            ? "已通过"
                            : "未通过（按配置允许继续）"
                    )
                );
            }
            else if (IsStimulusOnly)
            {
                if (_singleStimulusDialog is null)
                    throw new InvalidOperationException("单刺激实验服务不可用。");
                await _singleStimulusDialog.ShowAsync(runRoute);
            }
            else
                _router.Navigate(runRoute);
        }
        catch (Exception exception)
        {
            await ShowDetectionErrorDialogAsync("无法完成实验配置", exception.Message);
        }
        finally
        {
            _isConfirmationOpen = false;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanOpenConfirmation));
            OpenConfirmationCommand.NotifyCanExecuteChanged();
        }
    }

    public void ApplyImpedanceReadings(
        ElectrodeConfigurationMode mode,
        IReadOnlyDictionary<string, double> readings
    )
    {
        var configured = GetImpedanceDetectionPoints(mode).ToArray();
        if (mode == ElectrodeConfigurationMode.Stimulus)
            _stimulationMeasuredAt = DateTimeOffset.UtcNow;
        else
            _acquisitionMeasuredAt = DateTimeOffset.UtcNow;
        _suppressSiteChanges = true;
        if (mode == ElectrodeConfigurationMode.Stimulus)
        {
            foreach (
                var fixedPoint in Points.Where(point =>
                    point.StimulationChannelRole == StimulationChannelRole.FixedActive
                )
            )
                fixedPoint.Impedance = null;
        }
        foreach (var point in configured)
            point.Impedance = readings.TryGetValue(point.Name, out var value) ? value : null;
        _suppressSiteChanges = false;
        var passed = configured.Length > 0 && configured.All(point => point.Impedance is <= 10);
        if (mode == ElectrodeConfigurationMode.Stimulus)
            StimulusDetectionState = passed
                ? ImpedanceDetectionState.Passed
                : ImpedanceDetectionState.Failed;
        else
            AcquisitionDetectionState = passed
                ? ImpedanceDetectionState.Passed
                : ImpedanceDetectionState.Failed;
        RefreshAllState(false);
    }

    private Task ApplyImpedanceReadingsOnUiAsync(
        ElectrodeConfigurationMode mode,
        IReadOnlyDictionary<string, double> readings
    ) => RunOnUiAsync(() => ApplyImpedanceReadings(mode, readings));

    private Task RunOnUiAsync(Action action)
    {
        if (
            _uiSynchronizationContext is null
            || ReferenceEquals(SynchronizationContext.Current, _uiSynchronizationContext)
        )
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _uiSynchronizationContext.Post(
            _ =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            null
        );
        return completion.Task;
    }

    private ExperimentRunRouteData CreateExperimentRunRouteData()
    {
        var stimulusAssignments = CreateStimulusAssignments();
        var acquisitionChannels = Points
            .Where(point => point.Role == ElectrodeRole.Acquisition)
            .Select(point => point.Name)
            .ToArray();
        var reference = IsStimulusOnly
            ? string.Empty
            : Points.First(point => point.Role == ElectrodeRole.Reference).Name;
        var ground = IsStimulusOnly
            ? string.Empty
            : Points.First(point => point.Role == ElectrodeRole.Ground).Name;
        return new ExperimentRunRouteData(
            RouteData.ExperimentId,
            RouteData.SubjectId,
            ExperimentStimulusConfigurationSnapshot.From(
                RouteData.StimulusConfiguration,
                stimulusAssignments
            ),
            stimulusAssignments,
            acquisitionChannels,
            reference,
            ground,
            _eegAcquisitionOptions.SampleRateHz,
            RouteData.DeviceId,
            RouteData.ExperimentDatabaseId,
            RouteData.ScheduledAt,
            RouteData.Remarks,
            RouteData.ImportedTemplate?.Timing,
            preRunImpedance: CreatePreRunImpedanceSnapshot(),
            creationMode: RouteData.CreationMode
        );
    }

    internal PreRunImpedanceSnapshot CreatePreRunImpedanceSnapshot()
    {
        var mappings =
            _eegChannelMappings?.Load().Mappings
            ?? RouteData.ImportedTemplate?.PhysicalChannels
            ?? [];
        return new PreRunImpedanceSnapshot(
            Points
                .Where(x => x.Role == ElectrodeRole.Acquisition)
                .Select(point => new PreRunImpedanceChannel(
                    point.Name,
                    mappings
                        .FirstOrDefault(x =>
                            string.Equals(
                                x.ElectrodeId,
                                point.Name,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        ?.PhysicalChannel,
                    PreRunImpedanceSummaryCalculator.AcquisitionBand(point.Impedance)
                )),
            Points
                .Where(x =>
                    x.IsStimulus && x.StimulationChannelRole == StimulationChannelRole.Selectable
                )
                .Select(point => new PreRunImpedanceChannel(
                    point.Name,
                    point.StimulationPhysicalChannelId,
                    PreRunImpedanceSummaryCalculator.StimulationBand(point.Impedance)
                )),
            _acquisitionMeasuredAt,
            _stimulationMeasuredAt
        );
    }

    private void BuildStimulusSelectionGroups()
    {
        foreach (var target in StimulusConfiguration.Targets.OrderBy(target => target.DisplayOrder))
        {
            var selectableCount =
                StimulusConfiguration.ArrayMode == StimulusArrayMode.DualChannel
                    ? _capability.DualChannelSelectableCount
                    : target.Channels.Count(channel =>
                        channel.Role == StimulationChannelRole.Selectable
                    );
            if (
                selectableCount == 0
                && StimulusConfiguration.ArrayMode != StimulusArrayMode.DualChannel
            )
                selectableCount = _capability.HdSelectableChannelCount;

            var fixedOption = new StimulusElectrodeSelectionOptionViewModel(
                target.TargetId,
                target.DisplayOrder,
                StimulationChannelRole.FixedActive,
                1,
                StimulusConfiguration.Direction
            );
            var selectableOption = new StimulusElectrodeSelectionOptionViewModel(
                target.TargetId,
                target.DisplayOrder,
                StimulationChannelRole.Selectable,
                selectableCount,
                StimulusConfiguration.Direction
            );
            fixedOption.SelectCommand = SelectStimulusSelectionOptionCommand;
            selectableOption.SelectCommand = SelectStimulusSelectionOptionCommand;
            StimulusSelectionOptions.Add(fixedOption);
            StimulusSelectionOptions.Add(selectableOption);
            StimulusSelectionGroups.Add(
                new StimulusElectrodeSelectionGroupViewModel(
                    target,
                    fixedOption,
                    selectableOption,
                    StimulusConfiguration.ArrayMode == StimulusArrayMode.MultiTarget
                )
            );
        }
    }

    private IReadOnlyList<StimulationChannelOptionViewModel> GetAvailableStimulationChannels(
        ElectrodeSiteViewModel point
    )
    {
        if (
            point.StimulusTargetId is not { } targetId
            || point.StimulationChannelRole is not { } role
        )
            return [];

        if (StimulusConfiguration.ArrayMode != StimulusArrayMode.DualChannel)
        {
            return StimulusConfiguration
                .Targets.Where(target => target.TargetId == targetId)
                .SelectMany(target => target.Channels)
                .Where(channel => channel.Role == role)
                .Select(channel => new StimulationChannelOptionViewModel(
                    channel.PhysicalChannelId,
                    channel.Role,
                    StimulusConfiguration.Direction
                ))
                .OrderBy(option => option.PhysicalChannelId)
                .ToArray();
        }

        return role == StimulationChannelRole.FixedActive
            ? _capability
                .FixedActivePhysicalChannelIds.OrderBy(channel => channel)
                .Select(channel => new StimulationChannelOptionViewModel(
                    channel,
                    StimulationChannelRole.FixedActive,
                    StimulusConfiguration.Direction
                ))
                .ToArray()
            : _capability
                .SelectablePhysicalChannelIds.Select(
                    channel => new StimulationChannelOptionViewModel(
                        channel,
                        StimulationChannelRole.Selectable,
                        StimulusConfiguration.Direction
                    )
                )
                .ToArray();
    }

    private void TryAssignOnlyFixedStimulationChannel(ElectrodeSiteViewModel point)
    {
        if (point.StimulationChannelRole != StimulationChannelRole.FixedActive)
            return;
        var options = GetAvailableStimulationChannels(point);
        if (options.Count != 1)
            return;
        var channel = options[0];
        if (
            Points.Any(candidate =>
                !ReferenceEquals(candidate, point)
                && candidate.StimulationPhysicalChannelId == channel.PhysicalChannelId
            )
        )
            return;
        point.AssignStimulationChannel(channel);
    }

    private void SetPointRole(ElectrodeSiteViewModel point, ElectrodeRole role)
    {
        var wasStimulus = point.IsStimulus;
        _suppressSiteChanges = true;
        point.ClearStimulusAssignment();
        point.Role = role;
        point.Impedance = null;
        _suppressSiteChanges = false;
        InvalidateMode(
            wasStimulus || IsStimulusMode
                ? ElectrodeConfigurationMode.Stimulus
                : ElectrodeConfigurationMode.Acquisition
        );
        RefreshAllState();
    }

    private void OnStimulationChannelChanged(
        ElectrodeImpedanceItemViewModel row,
        StimulationChannelOptionViewModel? selectedChannel
    )
    {
        var point = row.Site;
        FocusPoint(point);
        if (selectedChannel is null || !point.IsStimulus)
            return;
        if (point.StimulationPhysicalChannelId == selectedChannel.PhysicalChannelId)
        {
            var conflictCleared = _pendingChannelSelections.Remove(point.Name);
            row.ChannelConflictText = string.Empty;
            if (conflictCleared)
                RefreshAllState(false);
            return;
        }
        var owner = Points.FirstOrDefault(candidate =>
            !ReferenceEquals(candidate, point)
            && candidate.StimulationPhysicalChannelId == selectedChannel.PhysicalChannelId
        );
        if (owner is not null)
        {
            _pendingChannelSelections[point.Name] = selectedChannel.PhysicalChannelId;
            row.ChannelConflictText =
                $"{selectedChannel.DisplayName} 已分配给 {owner.PositionName}，请选择其他通道";
            InvalidateMode(ElectrodeConfigurationMode.Stimulus);
            RefreshAllState(false);
            return;
        }
        _pendingChannelSelections.Remove(point.Name);
        row.ChannelConflictText = string.Empty;
        _suppressSiteChanges = true;
        point.AssignStimulationChannel(selectedChannel);
        point.Impedance = null;
        _suppressSiteChanges = false;
        InvalidateMode(ElectrodeConfigurationMode.Stimulus);
        RefreshAllState(false);
    }

    private void OnPointPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        // Focus changes update the head and row bindings directly. Rebuilding rows here
        // destroys the ComboBox the user is currently opening.
        if (!_suppressSiteChanges && e.PropertyName != nameof(ElectrodeSiteViewModel.IsSelected))
            RefreshAllState();
    }

    private void InvalidateMode(ElectrodeConfigurationMode mode)
    {
        _suppressSiteChanges = true;
        foreach (var point in GetConfiguredPoints(mode))
            point.Impedance = null;
        _suppressSiteChanges = false;
        if (mode == ElectrodeConfigurationMode.Stimulus)
        {
            StimulusDetectionState = ImpedanceDetectionState.NotStarted;
            _stimulationMeasuredAt = null;
            _isStimulusDetecting = false;
        }
        else
        {
            AcquisitionDetectionState = ImpedanceDetectionState.NotStarted;
            _acquisitionMeasuredAt = null;
            _isAcquisitionDetecting = false;
        }
        IsConfigurationConfirmed = false;
    }

    private void RefreshAllState(bool refreshImpedanceItems = true)
    {
        TryCommitPendingChannelSelections();
        UpdatePointAvailability();
        RefreshStimulusSelectionProgress();
        if (refreshImpedanceItems)
            RefreshImpedanceItems();
        OnPropertyChanged(nameof(IsStimulusMode));
        OnPropertyChanged(nameof(IsAcquisitionMode));
        OnPropertyChanged(nameof(StimulusChannelCurrentSummary));
        OnPropertyChanged(nameof(HasStimulusChannelCurrentSummary));
        OnPropertyChanged(nameof(ShowStimulusChannelCurrentSummary));
        OnPropertyChanged(nameof(RefGndText));
        OnPropertyChanged(nameof(SelectedStimulusElectrodeCount));
        OnPropertyChanged(nameof(StimulusSelectionProgressText));
        OnPropertyChanged(nameof(IsStimulusPositionSelectionComplete));
        OnPropertyChanged(nameof(IsStimulusChannelMappingComplete));
        OnPropertyChanged(nameof(IsStimulusConfigurationComplete));
        OnPropertyChanged(nameof(IsAcquisitionConfigurationComplete));
        OnPropertyChanged(nameof(CanDetectCurrentMode));
        OnPropertyChanged(nameof(CanToggleDetection));
        OnPropertyChanged(nameof(IsCurrentDetectionCapabilityEnabled));
        OnPropertyChanged(nameof(DetectionUnavailableReason));
        OnPropertyChanged(nameof(IsAnyDetecting));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(HasCurrentImpedanceItems));
        OnPropertyChanged(nameof(CurrentDetectionState));
        OnPropertyChanged(nameof(IsCurrentDetectionPassed));
        OnPropertyChanged(nameof(IsCurrentDetectionFailed));
        OnPropertyChanged(nameof(CurrentDetectionStatusText));
        OnPropertyChanged(nameof(IsCurrentDetecting));
        OnPropertyChanged(nameof(DetectionButtonText));
        OnPropertyChanged(nameof(DetectionErrorText));
        OnPropertyChanged(nameof(CurrentPanelTitle));
        OnPropertyChanged(nameof(SelectedPointRoleText));
        OnPropertyChanged(nameof(HasChannelConflicts));
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(CanOpenConfirmation));
        OnPropertyChanged(nameof(StimulusElectrodeSummary));
        OnPropertyChanged(nameof(AcquisitionElectrodeSummary));
        OnPropertyChanged(nameof(SafetySummary));
        ToggleDetectionCommand.NotifyCanExecuteChanged();
        OpenConfirmationCommand.NotifyCanExecuteChanged();
        NotifyConfigurationCommandsCanExecuteChanged();
    }

    private void TryCommitPendingChannelSelections()
    {
        if (_pendingChannelSelections.Count == 0)
            return;

        var pendingAssignments =
            new List<(ElectrodeSiteViewModel Point, StimulationChannelOptionViewModel Channel)>();
        foreach (var (pointName, physicalChannelId) in _pendingChannelSelections)
        {
            var point = Points.FirstOrDefault(candidate =>
                candidate.IsStimulus
                && candidate.Name.Equals(pointName, StringComparison.OrdinalIgnoreCase)
            );
            var channel = point is null
                ? null
                : GetAvailableStimulationChannels(point)
                    .FirstOrDefault(option => option.PhysicalChannelId == physicalChannelId);
            if (point is null || channel is null)
                return;
            pendingAssignments.Add((point, channel));
        }

        var intendedChannels = Points
            .Where(point => point.IsStimulus)
            .Select(point =>
                _pendingChannelSelections.TryGetValue(point.Name, out var pendingChannel)
                    ? (int?)pendingChannel
                    : point.StimulationPhysicalChannelId
            )
            .OfType<int>()
            .ToArray();
        if (intendedChannels.Distinct().Count() != intendedChannels.Length)
            return;

        _suppressSiteChanges = true;
        foreach (var (point, channel) in pendingAssignments)
        {
            point.AssignStimulationChannel(channel);
            point.Impedance = null;
        }
        _suppressSiteChanges = false;
        _pendingChannelSelections.Clear();
        foreach (var row in CurrentImpedanceItems)
            row.ChannelConflictText = string.Empty;
    }

    private void NotifyDetectionStateChanged()
    {
        OnPropertyChanged(nameof(IsAnyDetecting));
        OnPropertyChanged(nameof(IsCurrentDetecting));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(CanToggleDetection));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(DetectionButtonText));
        OnPropertyChanged(nameof(DetectionErrorText));
        OnPropertyChanged(nameof(CurrentDetectionStatusText));
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(CanOpenConfirmation));
        OnPropertyChanged(nameof(SafetySummary));
        ToggleDetectionCommand.NotifyCanExecuteChanged();
        OpenConfirmationCommand.NotifyCanExecuteChanged();
        NotifyConfigurationCommandsCanExecuteChanged();
    }

    private void NotifyConfigurationCommandsCanExecuteChanged()
    {
        SelectModeCommand.NotifyCanExecuteChanged();
        SelectStimulusSelectionOptionCommand.NotifyCanExecuteChanged();
        ToggleHeadOrientationCommand.NotifyCanExecuteChanged();
        SelectPointCommand.NotifyCanExecuteChanged();
        FocusPointCommand.NotifyCanExecuteChanged();
        SetSelectedPointRoleCommand.NotifyCanExecuteChanged();
        ClearSelectedPointCommand.NotifyCanExecuteChanged();
        ClearPointCommand.NotifyCanExecuteChanged();
    }

    private void UpdatePointAvailability()
    {
        foreach (var point in Points)
        {
            point.IsAvailable = IsStimulusMode
                ? point.IsStimulus || point.CanStimulate && !point.IsAcquisition
                : !point.IsStimulus;
        }
    }

    private void RefreshImpedanceItems()
    {
        var selected = GetConfiguredPoints(CurrentMode).ToArray();
        foreach (var item in CurrentImpedanceItems)
            item.Dispose();
        CurrentImpedanceItems.Clear();
        foreach (var point in selected)
        {
            var options = IsStimulusMode ? GetAvailableStimulationChannels(point) : [];
            var selectedChannel = point.StimulationPhysicalChannelId is { } physicalChannelId
                ? options.FirstOrDefault(option => option.PhysicalChannelId == physicalChannelId)
                : null;
            var conflictText = _pendingChannelSelections.TryGetValue(
                point.Name,
                out var pendingChannel
            )
                ? $"CH{pendingChannel} 已被其他电极使用，请选择其他通道"
                : string.Empty;
            CurrentImpedanceItems.Add(
                new ElectrodeImpedanceItemViewModel(
                    point,
                    options,
                    selectedChannel,
                    IsStimulusMode,
                    conflictText,
                    OnStimulationChannelChanged
                )
            );
        }
    }

    private void ReindexStimulusPositions()
    {
        var index = 1;
        foreach (var target in StimulusConfiguration.Targets.OrderBy(target => target.DisplayOrder))
        {
            foreach (
                var point in Points
                    .Where(point => point.StimulusTargetId == target.TargetId)
                    .OrderBy(point => point.StimulationChannelRole)
            )
            {
                point.AssignStimulusPosition(
                    target.TargetId,
                    target.DisplayOrder,
                    index++,
                    point.StimulationChannelRole!.Value,
                    StimulusConfiguration.Direction
                );
            }
        }
    }

    private void RefreshStimulusSelectionProgress()
    {
        foreach (var option in StimulusSelectionOptions)
        {
            option.AssignedCount = Points.Count(point =>
                point.StimulusTargetId == option.TargetId
                && point.StimulationChannelRole == option.Role
            );
        }
    }

    private IEnumerable<ElectrodeSiteViewModel> GetConfiguredPoints(
        ElectrodeConfigurationMode mode
    ) =>
        mode == ElectrodeConfigurationMode.Stimulus
            ? Points.Where(point => point.IsStimulus)
            : Points.Where(point => point.IsAcquisition);

    private IEnumerable<ElectrodeSiteViewModel> GetImpedanceDetectionPoints(
        ElectrodeConfigurationMode mode
    ) =>
        mode == ElectrodeConfigurationMode.Stimulus
            ? Points.Where(point =>
                point.IsStimulus
                && point.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            : Points.Where(point => point.Role == ElectrodeRole.Acquisition);

    private IEnumerable<string> GetStimulusChannelCurrentGroups()
    {
        foreach (var target in StimulusConfiguration.Targets.OrderBy(target => target.DisplayOrder))
        {
            IEnumerable<(
                int PhysicalChannelId,
                StimulationChannelRole Role,
                double Current
            )> channels =
                StimulusConfiguration.ArrayMode == StimulusArrayMode.DualChannel
                    ? Points
                        .Where(point =>
                            point.StimulusTargetId == target.TargetId
                            && point.StimulationPhysicalChannelId.HasValue
                            && point.StimulationChannelRole.HasValue
                        )
                        .Select(point =>
                            (
                                point.StimulationPhysicalChannelId!.Value,
                                point.StimulationChannelRole!.Value,
                                target.PeakCurrent
                            )
                        )
                    : target.Channels.Select(channel =>
                        (channel.PhysicalChannelId, channel.Role, channel.Current)
                    );
            var channelText = string.Join(
                " · ",
                channels
                    .OrderBy(channel => channel.Role)
                    .ThenBy(channel => channel.PhysicalChannelId)
                    .Select(channel => $"CH{channel.PhysicalChannelId} {channel.Current:0.00} mA")
            );
            if (string.IsNullOrWhiteSpace(channelText))
                continue;

            yield return StimulusConfiguration.Targets.Count > 1
                ? $"靶点{target.DisplayOrder}：{channelText}"
                : channelText;
        }
    }

    private string JoinPointNames(ElectrodeRole role)
    {
        var names = Points.Where(point => point.Role == role).Select(point => point.Name).ToArray();
        return names.Length == 0 ? "-" : string.Join("、", names);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ++_detectionSessionVersion;
        _detectionCancellation?.Cancel();
        if (_activeDetectionMode is { } mode)
        {
            var electrodeIds = _activeDetectionElectrodeIds;
            var stimulationRequest = _activeStimulationDetectionRequest;
            _ = StopDetectionOnDisposeAsync(mode, electrodeIds, stimulationRequest);
        }
        foreach (var point in Points)
            point.PropertyChanged -= OnPointPropertyChanged;
        foreach (var item in CurrentImpedanceItems)
            item.Dispose();
        _detectionCancellation?.Dispose();
        _detectionCancellation = null;
    }

    private async Task StopDetectionOnDisposeAsync(
        ElectrodeConfigurationMode mode,
        IReadOnlyList<string> electrodeIds,
        StimulationImpedanceDetectionRequest? stimulationRequest
    )
    {
        try
        {
            if (mode == ElectrodeConfigurationMode.Stimulus)
                await _impedanceDetectionService.StopStimulationAsync(
                    RouteData.DeviceId,
                    stimulationRequest
                        ?? throw new InvalidOperationException(
                            "The active stimulation detection request is unavailable."
                        )
                );
            else
                await _impedanceDetectionService.StopEegAsync(RouteData.DeviceId, electrodeIds);
        }
        catch { }
    }

    private StimulationImpedanceDetectionRequest GetActiveStimulationDetectionRequest() =>
        _activeStimulationDetectionRequest
        ?? throw new InvalidOperationException(
            "The active stimulation detection request is unavailable."
        );

    private static ObservableCollection<ElectrodeSiteViewModel> CreatePoints(
        StimulusCapabilityProfile capability,
        IReadOnlyList<ElectrodePositionDefinition> positions
    )
    {
        const double referenceCenterX = 286d;
        const double referenceCenterY = 287d;
        const double mapCenterX = 426d;
        const double mapCenterY = 450d;
        const double horizontalScale = 1.124d;
        const double verticalScale = 1.35d;
        const double pointRadius = 22d;
        return new ObservableCollection<ElectrodeSiteViewModel>(
            positions.Select(position => new ElectrodeSiteViewModel(
                position.Id,
                mapCenterX
                    + (position.ReferenceX - referenceCenterX) * horizontalScale
                    - pointRadius,
                mapCenterY + (position.ReferenceY - referenceCenterY) * verticalScale - pointRadius,
                capability.CanStimulate(position.Id),
                position.DefaultRole,
                position.Position
            ))
        );
    }
}
