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
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.ViewModels.Pages;

public partial class StimulusConfigurationPageViewModel : PageViewModel
{
    private StimulusConfigurationRouteData _routeData;
    private ExperimentConfigurationTemplate? _lastConfirmedConfiguration;
    private readonly INavigationRouter _router;
    private readonly DialogService _dialogService;
    private readonly IDialogProvider _dialogHost;
    private readonly IDeviceSelectionContext _deviceSelection;
    private readonly IReadOnlyDictionary<
        StimulusKind,
        StimulusModeConfigurationViewModel
    > _configurations;

    [ObservableProperty]
    private StimulusModeConfigurationViewModel _currentConfiguration;

    [ObservableProperty]
    private StimulusConfigurationSnapshot? _lastSavedConfiguration;

    [ObservableProperty]
    private bool _isConfigurationSaved;

    [ObservableProperty]
    private int _selectedModeIndex;

    [ObservableProperty]
    private bool _isModeTransitionReversed;

    public StimulusConfigurationPageViewModel(
        StimulusConfigurationRouteData routeData,
        INavigationRouter router,
        DialogService dialogService,
        IDialogProvider dialogHost,
        StimulusCapabilityProfile? capability = null,
        IDeviceSelectionContext? deviceSelection = null,
        ApplicationBehaviorOptions? applicationOptions = null
    )
        : base(ApplicationPageNames.StimulusConfiguration, "刺激参数配置")
    {
        _routeData = routeData;
        _router = router;
        _dialogService = dialogService;
        _dialogHost = dialogHost;
        _deviceSelection = deviceSelection ?? new DeviceSelectionContext();
        IsNavigationAnimationEnabled = applicationOptions?.NavigationAnimationsEnabled ?? true;
        _headerBadges =
        [
            new PageHeaderBadgeViewModel("患者ID", routeData.SubjectId),
            new PageHeaderBadgeViewModel("实验ID", routeData.ExperimentId),
            PageHeaderBadgeViewModel.CreateExperimentMode(routeData.CreationMode),
        ];

        if (routeData.Tolerance is not null)
            _headerBadges = [new("被试ID", routeData.SubjectId), new("流程", "耐受度测试")];

        _configurations = Enum.GetValues<StimulusKind>()
            .ToDictionary(
                kind => kind,
                kind => new StimulusModeConfigurationViewModel(
                    kind,
                    capability,
                    routeData.Tolerance is not null
                )
            );
        foreach (var configuration in _configurations.Values)
            configuration.ConfigurationChanged += OnConfigurationChanged;

        _currentConfiguration = _configurations[StimulusKind.TDcs];
        Modes =
        [
            new(StimulusKind.TDcs, "tDCS", "恒定直流刺激", SelectMode),
            new(StimulusKind.TAcs, "tACS", "正弦交流刺激", SelectMode),
            new(StimulusKind.TRns, "tRNS", "随机噪声刺激", SelectMode),
            new(StimulusKind.TPcs, "tPCS", "脉冲电流刺激", SelectMode),
            new(StimulusKind.Sham, "Sham", "研究对照伪刺激", SelectMode),
        ];
        if (
            routeData.CreationMode == ExperimentCreationMode.StimulusOnly
            && routeData.Tolerance is null
        )
        {
            Modes.Insert(
                0,
                new(StimulusKind.EnvelopeTAcs, "包络-tACS", "包络交流刺激", SelectMode)
            );
            _currentConfiguration = _configurations[StimulusKind.EnvelopeTAcs];
        }
        UpdateSelectedMode();
    }

    private IReadOnlyList<PageHeaderBadgeViewModel> _headerBadges = [];
    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges => _headerBadges;

    public override void UpdateExperimentIdentity(
        long previousId,
        long id,
        string code,
        EGGtCSPlatform.MainApp.ExperimentConfigurationTemplate? configuration = null
    )
    {
        if (_routeData.ExperimentDatabaseId != previousId)
            return;
        _routeData = _routeData with { ExperimentDatabaseId = id, ExperimentId = code };
        _lastConfirmedConfiguration = configuration ?? _lastConfirmedConfiguration;
        _headerBadges =
        [
            new("患者ID", _routeData.SubjectId),
            new("实验ID", code),
            PageHeaderBadgeViewModel.CreateExperimentMode(_routeData.CreationMode),
        ];
        OnPropertyChanged(nameof(HeaderBadges));
    }

    public ObservableCollection<StimulusModeNavigationItemViewModel> Modes { get; }

    public bool IsNavigationAnimationEnabled { get; }

    public double SelectionIndicatorOffset => SelectedModeIndex * 62d;

    public bool CanSave => CurrentConfiguration.IsAllocationValid;

    public WaveformDescriptor Waveform => CurrentConfiguration.CreateWaveformDescriptor();

    partial void OnCurrentConfigurationChanged(StimulusModeConfigurationViewModel value)
    {
        IsConfigurationSaved = false;
        UpdateSelectedMode();
        NotifyCurrentStateChanged();
    }

    partial void OnSelectedModeIndexChanged(int value) =>
        OnPropertyChanged(nameof(SelectionIndicatorOffset));

    [RelayCommand]
    private async Task SaveConfigurationAsync()
    {
        var configuration = CurrentConfiguration;
        var parameterError = StimulusParameterPolicy.Validate(
            configuration.Kind,
            configuration.ShamMode,
            configuration.Frequency,
            configuration.RampSeconds,
            configuration.DutyPercent,
            !configuration.IsEnvelope
        );
        if (
            configuration.IsEnvelope
            && _routeData.CreationMode != ExperimentCreationMode.StimulusOnly
        )
            parameterError = "包络-tACS 仅支持单刺激模式。";
        if (!CurrentConfiguration.IsAllocationValid || parameterError is not null)
        {
            var dialog = new ConfirmDialogViewModel(DialogKind.Error)
            {
                Title = "配置校验未通过",
                Message = parameterError ?? CurrentConfiguration.AllocationStatusText,
                ConfirmText = "知道了",
                ShowCancelButton = false,
            };
            await _dialogService.ShowDialog(_dialogHost, dialog);
            return;
        }

        LastSavedConfiguration = CurrentConfiguration.CreateSnapshot();
        IsConfigurationSaved = true;
        _router.Navigate(
            new ElectrodeConfigurationRouteData(
                _routeData.ExperimentId,
                _routeData.SubjectId,
                LastSavedConfiguration,
                _deviceSelection.SelectedDeviceId,
                _routeData.ExperimentDatabaseId,
                _routeData.ScheduledAt,
                _routeData.Remarks,
                ImportedTemplate: RestoreElectrodeConfiguration(LastSavedConfiguration),
                CreationMode: _routeData.CreationMode,
                Tolerance: _routeData.Tolerance
            )
        );
    }

    private ExperimentConfigurationTemplate? RestoreElectrodeConfiguration(
        StimulusConfigurationSnapshot stimulus
    )
    {
        if (_lastConfirmedConfiguration is not { } saved)
            return null;
        // Target IDs are temporary. Preserve compatible assignments by target order and channel.
        var assignments = (
            saved.StimulusConfiguration.ArrayMode == stimulus.ArrayMode
                ? saved.StimulusElectrodes
                : []
        )
            .Select(assignment =>
            {
                var previous = saved.StimulusConfiguration.Targets.Single(t =>
                    t.TargetId == assignment.TargetId
                );
                var target = stimulus.Targets.SingleOrDefault(t =>
                    t.DisplayOrder == previous.DisplayOrder
                );
                return
                    target is not null
                    && (
                        target.Channels.Count == 0
                        || target.Channels.Any(c =>
                            c.PhysicalChannelId == assignment.PhysicalChannelId
                            && c.Role == assignment.Role
                        )
                    )
                    ? assignment with
                    {
                        TargetId = target.TargetId,
                    }
                    : null;
            })
            .OfType<StimulusElectrodeAssignment>()
            .ToArray();
        return saved with { StimulusConfiguration = stimulus, StimulusElectrodes = assignments };
    }

    private void SelectMode(StimulusKind kind)
    {
        var selectedIndex = Modes
            .Select((mode, index) => (mode, index))
            .First(item => item.mode.Kind == kind)
            .index;
        if (selectedIndex == SelectedModeIndex)
            return;

        IsModeTransitionReversed = selectedIndex < SelectedModeIndex;
        SelectedModeIndex = selectedIndex;
        CurrentConfiguration = _configurations[kind];
    }

    private void UpdateSelectedMode()
    {
        if (Modes is null)
            return;

        foreach (var mode in Modes)
            mode.IsSelected = mode.Kind == CurrentConfiguration.Kind;
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, CurrentConfiguration))
            return;

        IsConfigurationSaved = false;
        NotifyCurrentStateChanged();
    }

    private void NotifyCurrentStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(Waveform));
        SaveConfigurationCommand.NotifyCanExecuteChanged();
    }
}
