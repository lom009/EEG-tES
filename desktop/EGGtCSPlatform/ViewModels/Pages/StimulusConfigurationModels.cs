using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public enum StimulusKind
{
    TDcs,
    TAcs,
    TRns,
    TPcs,
    Sham,
    EnvelopeTAcs,
}

public enum StimulusArrayMode
{
    DualChannel,
    Hd,
    MultiTarget,
}

public enum StimulusDirection
{
    Positive,
    Negative,
    Bidirectional,
}

public enum ShamWaveformMode
{
    Direct,
    Alternating,
}

public enum StimulationChannelRole
{
    FixedActive,
    Selectable,
}

public enum ResolvedElectrodeRole
{
    Anode,
    Cathode,
    FixedActive,
    Stimulating,
}

public sealed record ResolvedStimulationRoles(
    ResolvedElectrodeRole FixedActive,
    ResolvedElectrodeRole Selectable
)
{
    public string Description =>
        FixedActive switch
        {
            ResolvedElectrodeRole.Cathode =>
                "固定刺激通道电极 = 阴极 (-)，自选刺激通道电极 = 阳极 (+)",
            ResolvedElectrodeRole.Anode =>
                "固定刺激通道电极 = 阳极 (+)，自选刺激通道电极 = 阴极 (-)",
            _ => "双向刺激：区分固定刺激电极与自选刺激电极，不定义固定阴阳极",
        };
}

public static class StimulationElectrodeRoleResolver
{
    public static ResolvedStimulationRoles Resolve(StimulusDirection direction) =>
        direction switch
        {
            StimulusDirection.Positive => new(
                ResolvedElectrodeRole.Cathode,
                ResolvedElectrodeRole.Anode
            ),
            StimulusDirection.Negative => new(
                ResolvedElectrodeRole.Anode,
                ResolvedElectrodeRole.Cathode
            ),
            _ => new(ResolvedElectrodeRole.FixedActive, ResolvedElectrodeRole.Stimulating),
        };

    public static string GetLabel(StimulusDirection direction, StimulationChannelRole role)
    {
        var resolved = Resolve(direction);
        return (
            role == StimulationChannelRole.FixedActive ? resolved.FixedActive : resolved.Selectable
        ) switch
        {
            ResolvedElectrodeRole.Anode => "阳极",
            ResolvedElectrodeRole.Cathode => "阴极",
            ResolvedElectrodeRole.FixedActive => "固定刺激电极",
            _ => "自选刺激电极",
        };
    }

    public static string GetChannelRoleLabel(StimulationChannelRole role) =>
        role == StimulationChannelRole.FixedActive ? "固定刺激通道" : "自选刺激通道";
}

public sealed record StimulusCapabilityProfile(
    IReadOnlySet<string> StimulusSiteIds,
    double MaximumTotalPeakCurrent,
    int StimulationPhysicalChannelCount,
    IReadOnlySet<int> FixedActivePhysicalChannelIds,
    int MaximumSelectableChannelCount,
    int DualChannelSelectableCount,
    int HdSelectableChannelCount,
    bool MultiTargetEnabled,
    StimulationCurrentPolicy CurrentPolicy
)
{
    public static StimulusCapabilityProfile FromOptions(
        StimulationChannelOptions options,
        StimulationCurrentPolicy? currentPolicy = null
    )
    {
        currentPolicy ??= new StimulationCurrentPolicy(new StimulationCurrentOptions());
        return new StimulusCapabilityProfile(
            new HashSet<string>(options.AllowedElectrodeSiteIds, StringComparer.OrdinalIgnoreCase),
            currentPolicy.MaximumMilliAmps,
            options.PhysicalChannelCount,
            new HashSet<int>(options.FixedActivePhysicalChannelIds),
            options.MaximumSelectableChannelCount,
            options.DualChannelSelectableCount,
            options.HdSelectableChannelCount,
            options.MultiTargetEnabled,
            currentPolicy
        );
    }

    public static StimulusCapabilityProfile Default { get; } =
        FromOptions(new StimulationChannelOptions());

    public bool CanStimulate(string siteId) => StimulusSiteIds.Contains(siteId);

    public bool IsFixedActiveChannel(int physicalChannelId) =>
        FixedActivePhysicalChannelIds.Contains(physicalChannelId);

    public IReadOnlyList<int> SelectablePhysicalChannelIds =>
        Enumerable
            .Range(1, StimulationPhysicalChannelCount)
            .Where(channel => !FixedActivePhysicalChannelIds.Contains(channel))
            .ToArray();
}

internal static class StimulusCurrentMath
{
    public const double BalanceTolerance = 0.000001d;

    public static bool AreEqual(double left, double right) =>
        Math.Abs(left - right) <= BalanceTolerance;
}

public sealed class StimulusOptionViewModel(string label, object value) : ObservableObject
{
    public string Label { get; } = label;

    public object Value { get; } = value;

    public bool IsVisible => true;
}

public sealed class StimulusModeNavigationItemViewModel : ObservableObject
{
    private bool _isSelected;

    public StimulusModeNavigationItemViewModel(
        StimulusKind kind,
        string title,
        string subtitle,
        Action<StimulusKind> select
    )
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        SelectCommand = new RelayCommand(() => select(Kind));
    }

    public StimulusKind Kind { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class StimulationPhysicalChannelViewModel : ObservableObject
{
    private readonly StimulationCurrentPolicy _currentPolicy;
    private bool _enabled;
    private bool _isSelectedFixed;
    private double _current;

    public StimulationPhysicalChannelViewModel(
        int physicalChannelId,
        bool isFixedActiveCandidate,
        StimulationCurrentPolicy currentPolicy
    )
    {
        PhysicalChannelId = physicalChannelId;
        IsFixedActiveCandidate = isFixedActiveCandidate;
        _currentPolicy = currentPolicy;
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? CurrentChanged;

    public int PhysicalChannelId { get; }

    public string DisplayName => $"CH{PhysicalChannelId}";

    public bool IsFixedActiveCandidate { get; }

    public bool IsSelectableChannel => !IsFixedActiveCandidate;

    public bool IsSelectedFixed
    {
        get => _isSelectedFixed;
        private set
        {
            if (!SetProperty(ref _isSelectedFixed, value))
                return;
            OnPropertyChanged(nameof(RoleText));
            OnPropertyChanged(nameof(CanEditCurrent));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (IsFixedActiveCandidate || !SetProperty(ref _enabled, value))
                return;
            OnPropertyChanged(nameof(CanEditCurrent));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanToggle => IsSelectableChannel;

    public bool CanEditCurrent => Enabled && !IsSelectedFixed;

    public double MinimumCurrent => _currentPolicy.MinimumMilliAmps;

    public double MaximumCurrent => _currentPolicy.MaximumMilliAmps;

    public double CurrentIncrement => _currentPolicy.StepMilliAmps;

    public string RoleText =>
        IsFixedActiveCandidate
            ? IsSelectedFixed
                ? "当前固定刺激"
                : "固定刺激候选"
            : "自选刺激通道";

    public double Current
    {
        get => _current;
        set
        {
            if (!CanEditCurrent || !SetProperty(ref _current, _currentPolicy.Normalize(value)))
                return;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void SetSelectedFixed(bool selected, double peakCurrent)
    {
        IsSelectedFixed = selected;
        SetEnabledSilently(selected);
        if (selected)
            SetCurrentSilently(peakCurrent);
    }

    internal void SetEnabledSilently(bool enabled)
    {
        if (!SetProperty(ref _enabled, enabled, nameof(Enabled)))
            return;
        OnPropertyChanged(nameof(CanEditCurrent));
    }

    internal void SetCurrentSilently(double current) =>
        SetProperty(ref _current, _currentPolicy.Normalize(current), nameof(Current));
}

public sealed class StimulusTargetConfigurationViewModel : ObservableObject
{
    private readonly StimulusCapabilityProfile _capability;
    private readonly Action<StimulusTargetConfigurationViewModel> _removeTarget;
    private double _peakCurrent;
    private int _displayOrder;
    private bool _canRemoveTarget;

    public StimulusTargetConfigurationViewModel(
        Guid targetId,
        int displayOrder,
        double peakCurrent,
        StimulusArrayMode arrayMode,
        StimulusCapabilityProfile capability,
        Action<StimulusTargetConfigurationViewModel> removeTarget
    )
    {
        TargetId = targetId;
        _displayOrder = displayOrder;
        _peakCurrent = capability.CurrentPolicy.Normalize(peakCurrent);
        ArrayMode = arrayMode;
        _capability = capability;
        _removeTarget = removeTarget;
        Channels = new ObservableCollection<StimulationPhysicalChannelViewModel>(
            Enumerable
                .Range(1, capability.StimulationPhysicalChannelCount)
                .Select(channel => new StimulationPhysicalChannelViewModel(
                    channel,
                    capability.IsFixedActiveChannel(channel),
                    capability.CurrentPolicy
                ))
        );
        foreach (var channel in Channels)
        {
            channel.SelectionChanged += OnChannelSelectionChanged;
            channel.CurrentChanged += OnChannelCurrentChanged;
        }

        SelectFixedChannelCommand = new RelayCommand<StimulationPhysicalChannelViewModel?>(
            SelectFixedChannel
        );
        RemoveTargetCommand = new RelayCommand(() => _removeTarget(this), () => CanRemoveTarget);
        if (ArrayMode != StimulusArrayMode.DualChannel)
            InitializeHdChannels();
    }

    public event EventHandler? ConfigurationChanged;

    public Guid TargetId { get; }

    public StimulusArrayMode ArrayMode { get; }

    public ObservableCollection<StimulationPhysicalChannelViewModel> Channels { get; }

    public RelayCommand<StimulationPhysicalChannelViewModel?> SelectFixedChannelCommand { get; }

    public RelayCommand RemoveTargetCommand { get; }

    public int DisplayOrder
    {
        get => _displayOrder;
        internal set
        {
            if (SetProperty(ref _displayOrder, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string DisplayName => $"靶点 {DisplayOrder}";

    public double PeakCurrent
    {
        get => _peakCurrent;
        set
        {
            if (!SetProperty(ref _peakCurrent, _capability.CurrentPolicy.Normalize(value)))
                return;
            SelectedFixedChannel?.SetCurrentSilently(_peakCurrent);
            RedistributeSelectableCurrents();
            NotifyValidationChanged();
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int RequiredSelectableChannelCount =>
        ArrayMode == StimulusArrayMode.DualChannel
            ? _capability.DualChannelSelectableCount
            : _capability.HdSelectableChannelCount;

    public StimulationPhysicalChannelViewModel? SelectedFixedChannel =>
        Channels.FirstOrDefault(channel => channel.IsSelectedFixed);

    public int? SelectedFixedPhysicalChannelId => SelectedFixedChannel?.PhysicalChannelId;

    public IReadOnlyList<StimulationPhysicalChannelViewModel> EnabledSelectableChannels =>
        Channels
            .Where(channel => channel.IsSelectableChannel && channel.Enabled)
            .OrderBy(channel => channel.PhysicalChannelId)
            .ToArray();

    public int EnabledSelectableChannelCount => EnabledSelectableChannels.Count;

    public double SelectableCurrentTotal =>
        Math.Round(
            EnabledSelectableChannels.Sum(channel => channel.Current),
            6,
            MidpointRounding.AwayFromZero
        );

    public bool IsBalanced =>
        ArrayMode == StimulusArrayMode.DualChannel
        || StimulusCurrentMath.AreEqual(SelectableCurrentTotal, PeakCurrent);

    public bool CanDistributeSelectableCurrents =>
        ArrayMode == StimulusArrayMode.DualChannel
        || EnabledSelectableChannelCount == 0
        || _capability.CurrentPolicy.ToRaw(PeakCurrent)
            >= EnabledSelectableChannelCount * _capability.CurrentPolicy.MinimumRaw;

    public bool HasRequiredChannelSelection =>
        ArrayMode == StimulusArrayMode.DualChannel
            ? _capability.FixedActivePhysicalChannelIds.Count > 0
                && _capability.SelectablePhysicalChannelIds.Count >= RequiredSelectableChannelCount
            : SelectedFixedChannel is not null
                && EnabledSelectableChannelCount == RequiredSelectableChannelCount;

    public bool IsValid =>
        HasRequiredChannelSelection && CanDistributeSelectableCurrents && IsBalanced;

    public string BalanceText =>
        ArrayMode == StimulusArrayMode.DualChannel
            ? $"固定与自选刺激通道电流均为 {PeakCurrent:0.00} mA"
        : !CanDistributeSelectableCurrents
            ? $"峰值 {PeakCurrent:0.00} mA 小于 {EnabledSelectableChannelCount} 个通道所需的最小合计电流 "
                + $"{EnabledSelectableChannelCount * _capability.CurrentPolicy.MinimumMilliAmps:0.00} mA"
        : IsBalanced
            ? $"自选刺激通道合计 {SelectableCurrentTotal:0.00} mA = 峰值 {PeakCurrent:0.00} mA"
        : $"自选刺激通道合计 {SelectableCurrentTotal:0.00} mA，须等于峰值 {PeakCurrent:0.00} mA";

    public bool CanRemoveTarget
    {
        get => _canRemoveTarget;
        internal set
        {
            if (SetProperty(ref _canRemoveTarget, value))
                RemoveTargetCommand.NotifyCanExecuteChanged();
        }
    }

    public StimulusTargetSnapshot CreateSnapshot()
    {
        IReadOnlyList<StimulationPhysicalChannelSnapshot> channels =
            ArrayMode == StimulusArrayMode.DualChannel
                ? []
                : Channels
                    .Where(channel => channel.Enabled)
                    .Select(channel => new StimulationPhysicalChannelSnapshot(
                        channel.PhysicalChannelId,
                        channel.IsSelectedFixed
                            ? StimulationChannelRole.FixedActive
                            : StimulationChannelRole.Selectable,
                        channel.Current
                    ))
                    .ToArray();
        return new StimulusTargetSnapshot(
            TargetId,
            DisplayOrder,
            PeakCurrent,
            SelectedFixedPhysicalChannelId,
            channels
        );
    }

    private void InitializeHdChannels()
    {
        var fixedChannel = Channels.FirstOrDefault(channel => channel.IsFixedActiveCandidate);
        if (fixedChannel is not null)
            SelectFixedChannel(fixedChannel);
        foreach (
            var channel in Channels
                .Where(channel => channel.IsSelectableChannel)
                .Take(RequiredSelectableChannelCount)
        )
            channel.SetEnabledSilently(true);
        RedistributeSelectableCurrents();
        NotifyValidationChanged();
    }

    private void SelectFixedChannel(StimulationPhysicalChannelViewModel? selected)
    {
        if (selected is not { IsFixedActiveCandidate: true })
            return;
        foreach (var channel in Channels.Where(channel => channel.IsFixedActiveCandidate))
            channel.SetSelectedFixed(ReferenceEquals(channel, selected), PeakCurrent);
        NotifyValidationChanged();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnChannelSelectionChanged(object? sender, EventArgs e)
    {
        if (
            sender is StimulationPhysicalChannelViewModel { Enabled: true } changed
            && EnabledSelectableChannelCount > RequiredSelectableChannelCount
        )
            changed.SetEnabledSilently(false);
        RedistributeSelectableCurrents();
        NotifyValidationChanged();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnChannelCurrentChanged(object? sender, EventArgs e)
    {
        NotifyValidationChanged();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RedistributeSelectableCurrents()
    {
        var enabled = EnabledSelectableChannels;
        if (enabled.Count == 0)
            return;
        var policy = _capability.CurrentPolicy;
        var peakRaw = policy.ToRaw(PeakCurrent);
        var minimumTotalRaw = enabled.Count * policy.MinimumRaw;
        if (peakRaw < minimumTotalRaw)
        {
            foreach (var channel in enabled)
                channel.SetCurrentSilently(policy.MinimumMilliAmps);
            return;
        }

        var distributableSteps = (peakRaw - minimumTotalRaw) / policy.StepRaw;
        var baseSteps = distributableSteps / enabled.Count;
        var remainder = distributableSteps % enabled.Count;
        for (var index = 0; index < enabled.Count; index++)
        {
            var raw =
                policy.MinimumRaw
                + baseSteps * policy.StepRaw
                + (index < remainder ? policy.StepRaw : 0);
            enabled[index].SetCurrentSilently(policy.ToMilliAmps(raw));
        }
    }

    private void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(SelectedFixedChannel));
        OnPropertyChanged(nameof(SelectedFixedPhysicalChannelId));
        OnPropertyChanged(nameof(EnabledSelectableChannels));
        OnPropertyChanged(nameof(EnabledSelectableChannelCount));
        OnPropertyChanged(nameof(SelectableCurrentTotal));
        OnPropertyChanged(nameof(IsBalanced));
        OnPropertyChanged(nameof(CanDistributeSelectableCurrents));
        OnPropertyChanged(nameof(HasRequiredChannelSelection));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(BalanceText));
    }
}

public sealed class StimulusModeConfigurationViewModel : ObservableObject
{
    private readonly StimulusCapabilityProfile _capability;
    private StimulusOptionViewModel? _selectedArrayOption;
    private StimulusOptionViewModel? _selectedDirectionOption;
    private StimulusOptionViewModel? _selectedShamModeOption;
    private double _rampSeconds;
    private double _frequency;
    private double _dutyPercent;
    private double _delayMilliseconds = 40;

    public StimulusModeConfigurationViewModel(
        StimulusKind kind,
        StimulusCapabilityProfile? capability = null
    )
    {
        Kind = kind;
        _capability = capability ?? StimulusCapabilityProfile.Default;
        ArrayOptions =
        [
            new("双通道", StimulusArrayMode.DualChannel),
            new("HD", StimulusArrayMode.Hd),
        ];
        if (_capability.MultiTargetEnabled)
            ArrayOptions.Add(new StimulusOptionViewModel("多靶点", StimulusArrayMode.MultiTarget));
        DirectionOptions = kind switch
        {
            StimulusKind.TDcs or StimulusKind.Sham =>
            [
                new("正向", StimulusDirection.Positive),
                new("反向", StimulusDirection.Negative),
            ],
            StimulusKind.TRns => [new("双向", StimulusDirection.Bidirectional)],
            _ =>
            [
                new("正向", StimulusDirection.Positive),
                new("双向", StimulusDirection.Bidirectional),
            ],
        };
        ShamModeOptions =
        [
            new("直流模式", ShamWaveformMode.Direct),
            new("交流模式", ShamWaveformMode.Alternating),
        ];
        _selectedArrayOption = ArrayOptions[0];
        _selectedDirectionOption = DirectionOptions[0];
        _selectedShamModeOption = ShamModeOptions[0];
        (_rampSeconds, _frequency, _dutyPercent) = kind switch
        {
            StimulusKind.TPcs => (7d, 80d, 79d),
            StimulusKind.TDcs => (15d, 40d, 79d),
            StimulusKind.Sham => (15d, 40d, 79d),
            _ => (7d, 40d, 79d),
        };
        Targets = [];
        AddTargetCommand = new RelayCommand(AddTarget, CanAddTarget);
        RebuildTargets();
    }

    public event EventHandler? ConfigurationChanged;

    public StimulusKind Kind { get; }

    public ObservableCollection<StimulusOptionViewModel> ArrayOptions { get; }

    public ObservableCollection<StimulusOptionViewModel> DirectionOptions { get; }

    public ObservableCollection<StimulusOptionViewModel> ShamModeOptions { get; }

    public ObservableCollection<StimulusTargetConfigurationViewModel> Targets { get; }

    public RelayCommand AddTargetCommand { get; }

    public string DisplayName =>
        Kind switch
        {
            StimulusKind.TDcs => "tDCS",
            StimulusKind.TAcs => "tACS",
            StimulusKind.TRns => "tRNS",
            StimulusKind.TPcs => "tPCS",
            StimulusKind.Sham => "Sham",
            StimulusKind.EnvelopeTAcs => "包络-tACS",
            _ => Kind.ToString(),
        };

    public StimulusOptionViewModel? SelectedArrayOption
    {
        get => _selectedArrayOption;
        set
        {
            if (
                IsEnvelope
                || value?.Value is not StimulusArrayMode
                || !SetProperty(ref _selectedArrayOption, value)
            )
                return;
            RebuildTargets();
            NotifyConfigurationChanged();
        }
    }

    public StimulusOptionViewModel? SelectedDirectionOption
    {
        get => _selectedDirectionOption;
        set
        {
            if (
                value?.Value is not StimulusDirection
                || !SetProperty(ref _selectedDirectionOption, value)
            )
                return;
            NotifyConfigurationChanged();
        }
    }

    public StimulusOptionViewModel? SelectedShamModeOption
    {
        get => _selectedShamModeOption;
        set
        {
            if (
                value?.Value is not ShamWaveformMode
                || !SetProperty(ref _selectedShamModeOption, value)
            )
                return;
            _selectedDirectionOption =
                ShamMode == ShamWaveformMode.Alternating
                    ? new StimulusOptionViewModel("双向", StimulusDirection.Bidirectional)
                    : DirectionOptions[0];
            OnPropertyChanged(nameof(SelectedDirectionOption));
            NotifyConfigurationChanged();
        }
    }

    public StimulusArrayMode ArrayMode =>
        SelectedArrayOption?.Value is StimulusArrayMode value
            ? value
            : StimulusArrayMode.DualChannel;

    public StimulusDirection Direction =>
        Kind is StimulusKind.TRns or StimulusKind.EnvelopeTAcs
        || Kind == StimulusKind.Sham && ShamMode == ShamWaveformMode.Alternating
            ? StimulusDirection.Bidirectional
        : SelectedDirectionOption?.Value is StimulusDirection value ? value
        : StimulusDirection.Positive;

    public ShamWaveformMode ShamMode =>
        SelectedShamModeOption?.Value is ShamWaveformMode value ? value : ShamWaveformMode.Direct;

    public double Current
    {
        get => Targets.FirstOrDefault()?.PeakCurrent ?? 0;
        set
        {
            if (Targets.FirstOrDefault() is not { } target)
                return;
            target.PeakCurrent = value;
            OnPropertyChanged();
        }
    }

    public double RampSeconds
    {
        get => _rampSeconds;
        set
        {
            var normalized = Math.Round(
                Math.Clamp(value, ParameterPolicy.Ramp.Minimum, ParameterPolicy.Ramp.Maximum),
                ParameterPolicy.Ramp.DecimalPlaces,
                MidpointRounding.AwayFromZero
            );
            if (SetProperty(ref _rampSeconds, normalized))
                NotifyConfigurationChanged();
        }
    }

    public double Frequency
    {
        get => _frequency;
        set
        {
            var normalized =
                Math.Round(
                    Math.Clamp(value, ParameterPolicy.Frequency.Minimum, MaximumFrequency) * 10d,
                    MidpointRounding.AwayFromZero
                ) / 10d;
            if (SetProperty(ref _frequency, normalized))
                NotifyConfigurationChanged();
        }
    }

    public double DutyPercent
    {
        get => _dutyPercent;
        set
        {
            var normalized = Math.Round(
                Math.Clamp(
                    value,
                    StimulusParameterPolicy.Duty.Minimum,
                    StimulusParameterPolicy.Duty.Maximum
                ),
                MidpointRounding.AwayFromZero
            );
            if (SetProperty(ref _dutyPercent, normalized))
                NotifyConfigurationChanged();
        }
    }

    public bool IsEnvelope => Kind == StimulusKind.EnvelopeTAcs;

    public double ParameterRowSpacing => IsEnvelope ? 0 : 16;

    public double DelayMilliseconds
    {
        get => _delayMilliseconds;
        set
        {
            if (!double.IsFinite(value))
                return;
            if (
                SetProperty(
                    ref _delayMilliseconds,
                    Math.Round(Math.Clamp(value, 0, 300), MidpointRounding.AwayFromZero)
                )
            )
                NotifyConfigurationChanged();
        }
    }

    public bool ShowSelectionSection => !IsEnvelope;

    public string SelectionSectionTitle => "方向与类型";

    public bool ShowArraySelector => true;

    public bool ShowShamModeSelector => Kind == StimulusKind.Sham;

    public bool ShowDirectionSelector =>
        Kind != StimulusKind.TRns
        && !(Kind == StimulusKind.Sham && ShamMode == ShamWaveformMode.Alternating);

    public StimulusParameterPolicy ParameterPolicy => StimulusParameterPolicy.For(Kind, ShamMode);
    public StimulusParameterRange DutyRange => StimulusParameterPolicy.Duty;

    public bool ShowRampParameter => !IsEnvelope && ParameterPolicy.ShowRamp;

    public bool ShowFrequencyParameter => !IsEnvelope && ParameterPolicy.ShowFrequency;

    public bool ShowDutyPercentParameter => ParameterPolicy.ShowDutyPercent;

    public double MaximumFrequency => ParameterPolicy.Frequency.Maximum;

    public bool ShowGlobalCurrent => ArrayMode != StimulusArrayMode.MultiTarget;

    public bool ShowPhysicalChannelAllocation => ArrayMode == StimulusArrayMode.Hd;

    public bool ShowMultiTargetEditor =>
        ArrayMode == StimulusArrayMode.MultiTarget && _capability.MultiTargetEnabled;

    public string CurrentTitle => IsEnvelope ? "最大电流" : "峰值电流";

    public double MinimumCurrent => _capability.CurrentPolicy.MinimumMilliAmps;

    public double MaximumCurrent => _capability.CurrentPolicy.MaximumMilliAmps;

    public double CurrentIncrement => _capability.CurrentPolicy.StepMilliAmps;

    public string CurrentInfo =>
        IsEnvelope ? string.Empty
        : Kind == StimulusKind.TAcs ? "该用户近期耐受度测试值：-"
        : "该用户近期耐受度测试值：2mA";

    public ObservableCollection<StimulationPhysicalChannelViewModel> PhysicalChannels =>
        Targets.First().Channels;

    public RelayCommand<StimulationPhysicalChannelViewModel?> SelectFixedChannelCommand =>
        Targets.First().SelectFixedChannelCommand;

    public int EnabledSelectableChannelCount =>
        Targets.FirstOrDefault()?.EnabledSelectableChannelCount ?? 0;

    public double SelectableCurrentTotal => Targets.FirstOrDefault()?.SelectableCurrentTotal ?? 0d;

    public bool IsPeakAboveSelectableTotal =>
        Current > SelectableCurrentTotal + StimulusCurrentMath.BalanceTolerance;

    public bool IsPeakBelowSelectableTotal =>
        Current < SelectableCurrentTotal - StimulusCurrentMath.BalanceTolerance;

    public int RequiredSelectableChannelCount =>
        ArrayMode == StimulusArrayMode.Hd || ArrayMode == StimulusArrayMode.MultiTarget
            ? _capability.HdSelectableChannelCount
            : _capability.DualChannelSelectableCount;

    public bool HasFixedChannelConfiguration => _capability.FixedActivePhysicalChannelIds.Count > 0;

    public double TotalPeakCurrent => Targets.Sum(target => target.PeakCurrent);

    public int RequiredElectrodeCount => Targets.Sum(_ => 1 + RequiredSelectableChannelCount);

    public bool IsTotalCurrentOverLimit =>
        TotalPeakCurrent > _capability.MaximumTotalPeakCurrent + 0.0001d;

    public bool IsSiteCapacityExceeded =>
        RequiredElectrodeCount > _capability.StimulusSiteIds.Count;

    public bool HasCrossTargetChannelConflicts
    {
        get
        {
            if (Targets.Count <= 1)
                return false;
            var enabledPhysicalChannels = Targets
                .SelectMany(target => target.Channels)
                .Where(channel => channel.Enabled)
                .Select(channel => channel.PhysicalChannelId)
                .ToArray();
            return enabledPhysicalChannels.Distinct().Count() != enabledPhysicalChannels.Length;
        }
    }

    public bool IsAllocationValid =>
        HasFixedChannelConfiguration
        && Targets.Count > 0
        && Targets.All(target => target.IsValid)
        && !HasCrossTargetChannelConflicts
        && !IsTotalCurrentOverLimit
        && !IsSiteCapacityExceeded;

    public bool HasAllocationError => !IsAllocationValid;

    public string AllocationStatusText
    {
        get
        {
            if (!HasFixedChannelConfiguration)
                return "未配置固定刺激物理通道，请先在 AppData 的 appsettings.json 中填写 FixedActivePhysicalChannelIds";
            if (IsSiteCapacityExceeded)
                return $"需要 {RequiredElectrodeCount} 个刺激点位，当前仅开放 {_capability.StimulusSiteIds.Count} 个";
            if (IsTotalCurrentOverLimit)
                return $"峰值电流合计 {TotalPeakCurrent:0.00} mA，超过 {_capability.MaximumTotalPeakCurrent:0.00} mA";
            if (HasCrossTargetChannelConflicts)
                return "多个靶点组之间不得重复使用同一刺激物理通道";
            var invalid = Targets.FirstOrDefault(target => !target.IsValid);
            if (invalid is not null)
            {
                if (ArrayMode == StimulusArrayMode.Hd && invalid.SelectedFixedChannel is null)
                    return "请选择一个固定刺激通道";
                if (
                    ArrayMode == StimulusArrayMode.Hd
                    && invalid.EnabledSelectableChannelCount != RequiredSelectableChannelCount
                )
                    return $"自选刺激通道必须选择 {RequiredSelectableChannelCount} 个（当前 {invalid.EnabledSelectableChannelCount}/{RequiredSelectableChannelCount}）";
                return invalid.BalanceText;
            }
            if (
                ArrayMode == StimulusArrayMode.Hd
                && Targets[0].SelectedFixedChannel is { } fixedChannel
            )
                return $"固定刺激 {fixedChannel.DisplayName} · 自选刺激通道 {EnabledSelectableChannelCount}/{RequiredSelectableChannelCount} · {Targets[0].BalanceText}";
            return $"峰值电流 {TotalPeakCurrent:0.00} mA";
        }
    }

    public string? CurrentStatusText =>
        HasAllocationError ? AllocationStatusText
        : ShowPhysicalChannelAllocation ? $"已分配 {SelectableCurrentTotal:0.0} mA"
        : null;

    public bool CurrentHasError => HasAllocationError;

    public string WaveformHint =>
        ShowMultiTargetEditor ? "波形形态示意，幅值取最大峰值电流" : string.Empty;

    public WaveformDescriptor Waveform => CreateWaveformDescriptor();

    public ResolvedStimulationRoles ResolvedRoles =>
        StimulationElectrodeRoleResolver.Resolve(Direction);

    public WaveformDescriptor CreateWaveformDescriptor() =>
        new(
            Kind,
            ArrayMode,
            Direction,
            ShamMode,
            IsEnvelope
                ? Current
                : Math.Max(
                    0.05d,
                    Targets.Count == 0 ? 0 : Targets.Max(target => target.PeakCurrent)
                ),
            RampSeconds,
            Frequency,
            DutyPercent,
            DelayMilliseconds
        );

    public StimulusConfigurationSnapshot CreateSnapshot() =>
        new(
            Kind,
            ArrayMode,
            Direction,
            ShamMode,
            RampSeconds,
            Frequency,
            DutyPercent,
            Targets.Select(target => target.CreateSnapshot()).ToArray(),
            IsEnvelope
                ? new EnvelopeParameters(DelayMilliseconds: DelayMilliseconds, IsDeviceSetup: true)
                : null
        );

    private bool CanAddTarget() =>
        ArrayMode == StimulusArrayMode.MultiTarget && _capability.MultiTargetEnabled;

    private void AddTarget()
    {
        if (!CanAddTarget())
            return;
        AddTargetCore(_capability.CurrentPolicy.Normalize(0.5d));
        RefreshTargetOrder();
        NotifyTargetStateChanged();
    }

    private void RemoveTarget(StimulusTargetConfigurationViewModel target)
    {
        if (Targets.Count <= 1 || !Targets.Remove(target))
            return;
        target.ConfigurationChanged -= OnTargetConfigurationChanged;
        RefreshTargetOrder();
        NotifyTargetStateChanged();
    }

    private void RebuildTargets()
    {
        foreach (var target in Targets)
            target.ConfigurationChanged -= OnTargetConfigurationChanged;
        Targets.Clear();
        AddTargetCore(
            ArrayMode == StimulusArrayMode.MultiTarget
                ? _capability.CurrentPolicy.Normalize(0.5d)
                : DefaultCurrentForKind()
        );
        RefreshTargetOrder();
        NotifyTargetStateChanged(false);
    }

    private void AddTargetCore(double current)
    {
        var target = new StimulusTargetConfigurationViewModel(
            Guid.NewGuid(),
            Targets.Count + 1,
            current,
            ArrayMode,
            _capability,
            RemoveTarget
        );
        target.ConfigurationChanged += OnTargetConfigurationChanged;
        Targets.Add(target);
    }

    private double DefaultCurrentForKind() =>
        _capability.CurrentPolicy.Normalize(
            Kind switch
            {
                StimulusKind.TDcs or StimulusKind.TAcs or StimulusKind.EnvelopeTAcs => 2d,
                _ => 1.8d,
            }
        );

    private void RefreshTargetOrder()
    {
        for (var index = 0; index < Targets.Count; index++)
            Targets[index].DisplayOrder = index + 1;
    }

    private void OnTargetConfigurationChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Current));
        NotifyTargetStateChanged();
    }

    private void NotifyTargetStateChanged(bool raiseConfigurationChanged = true)
    {
        foreach (var target in Targets)
            target.CanRemoveTarget = Targets.Count > 1;
        AddTargetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PhysicalChannels));
        OnPropertyChanged(nameof(SelectFixedChannelCommand));
        OnPropertyChanged(nameof(EnabledSelectableChannelCount));
        OnPropertyChanged(nameof(SelectableCurrentTotal));
        OnPropertyChanged(nameof(IsPeakAboveSelectableTotal));
        OnPropertyChanged(nameof(IsPeakBelowSelectableTotal));
        OnPropertyChanged(nameof(RequiredSelectableChannelCount));
        OnPropertyChanged(nameof(HasFixedChannelConfiguration));
        OnPropertyChanged(nameof(TotalPeakCurrent));
        OnPropertyChanged(nameof(RequiredElectrodeCount));
        OnPropertyChanged(nameof(IsTotalCurrentOverLimit));
        OnPropertyChanged(nameof(IsSiteCapacityExceeded));
        OnPropertyChanged(nameof(HasCrossTargetChannelConflicts));
        OnPropertyChanged(nameof(IsAllocationValid));
        OnPropertyChanged(nameof(HasAllocationError));
        OnPropertyChanged(nameof(AllocationStatusText));
        OnPropertyChanged(nameof(CurrentStatusText));
        OnPropertyChanged(nameof(CurrentHasError));
        OnPropertyChanged(nameof(Waveform));
        if (raiseConfigurationChanged)
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyConfigurationChanged()
    {
        OnPropertyChanged(nameof(ArrayMode));
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(ShamMode));
        OnPropertyChanged(nameof(ParameterPolicy));
        OnPropertyChanged(nameof(MaximumFrequency));
        OnPropertyChanged(nameof(ShowDutyPercentParameter));
        OnPropertyChanged(nameof(ShowDirectionSelector));
        OnPropertyChanged(nameof(ShowRampParameter));
        OnPropertyChanged(nameof(ShowFrequencyParameter));
        OnPropertyChanged(nameof(ShowGlobalCurrent));
        OnPropertyChanged(nameof(ShowPhysicalChannelAllocation));
        OnPropertyChanged(nameof(ShowMultiTargetEditor));
        OnPropertyChanged(nameof(ResolvedRoles));
        OnPropertyChanged(nameof(WaveformHint));
        OnPropertyChanged(nameof(Waveform));
        NotifyTargetStateChanged(false);
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record WaveformDescriptor(
    StimulusKind Kind,
    StimulusArrayMode ArrayMode,
    StimulusDirection Direction,
    ShamWaveformMode ShamMode,
    double Current,
    double RampSeconds,
    double Frequency,
    double DutyPercent,
    double DelayMilliseconds = 40
);

public sealed record StimulationPhysicalChannelSnapshot(
    int PhysicalChannelId,
    StimulationChannelRole Role,
    double Current
);

public sealed record StimulusTargetSnapshot(
    Guid TargetId,
    int DisplayOrder,
    double PeakCurrent,
    int? FixedActivePhysicalChannelId,
    IReadOnlyList<StimulationPhysicalChannelSnapshot> Channels
);

public sealed record StimulusElectrodeAssignment(
    string SiteId,
    Guid TargetId,
    int PhysicalChannelId,
    StimulationChannelRole Role
);

public sealed record StimulusConfigurationSnapshot(
    StimulusKind Kind,
    StimulusArrayMode ArrayMode,
    StimulusDirection Direction,
    ShamWaveformMode ShamMode,
    double RampSeconds,
    double Frequency,
    double DutyPercent,
    IReadOnlyList<StimulusTargetSnapshot> Targets,
    EnvelopeParameters? Envelope = null
);
