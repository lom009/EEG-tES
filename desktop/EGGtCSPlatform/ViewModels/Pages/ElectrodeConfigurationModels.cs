using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EGGtCSPlatform.ViewModels.Pages;

public enum ElectrodeConfigurationMode
{
    Stimulus,
    Acquisition,
}

public enum ElectrodeHeadOrientation
{
    Front,
    Back,
}

public enum ElectrodeRole
{
    None,
    Acquisition,
    Reference,
    Ground,
}

public enum ImpedanceQuality
{
    Unknown,
    Excellent,
    Good,
    Medium,
    Poor,
    Bad,
}

public enum ImpedanceDetectionState
{
    NotStarted,
    Failed,
    Passed,
}

public sealed class StimulusElectrodeSelectionOptionViewModel(
    Guid targetId,
    int targetDisplayOrder,
    StimulationChannelRole role,
    int requiredCount,
    StimulusDirection direction
) : ObservableObject
{
    private bool _isSelected;
    private int _assignedCount;

    public Guid TargetId { get; } = targetId;

    public int TargetDisplayOrder { get; } = targetDisplayOrder;

    public StimulationChannelRole Role { get; } = role;

    public int RequiredCount { get; } = requiredCount;

    public ResolvedElectrodeRole ResolvedRole { get; } =
        role == StimulationChannelRole.FixedActive
            ? StimulationElectrodeRoleResolver.Resolve(direction).FixedActive
            : StimulationElectrodeRoleResolver.Resolve(direction).Selectable;

    public string Label { get; } = StimulationElectrodeRoleResolver.GetLabel(direction, role);

    public string UiLabel => ElectrodeRoleDisplay.Label(ResolvedRole);

    public ICommand? SelectCommand { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int AssignedCount
    {
        get => _assignedCount;
        set
        {
            if (SetProperty(ref _assignedCount, value))
                OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{AssignedCount}/{RequiredCount}";

    public bool UsesBlueColor =>
        ElectrodeRoleDisplay.IsAnode(ResolvedRole);

    public string RoleSymbol => ResolvedRole switch
    {
        ResolvedElectrodeRole.Anode => "+",
        ResolvedElectrodeRole.Cathode => "−",
        _ => "~",
    };

    public bool UsesRedColor => !UsesBlueColor;

    public int LayoutOrder => UsesBlueColor ? 0 : 1;
}

public sealed class StimulusElectrodeSelectionGroupViewModel(
    StimulusTargetSnapshot target,
    StimulusElectrodeSelectionOptionViewModel fixedOption,
    StimulusElectrodeSelectionOptionViewModel selectableOption,
    bool showDisplayName = true
)
{
    public Guid TargetId => target.TargetId;

    public string DisplayName => $"靶点 {target.DisplayOrder}";

    public bool ShowDisplayName { get; } = showDisplayName;

    public ObservableCollection<StimulusElectrodeSelectionOptionViewModel> Options { get; } =
        new(new[] { fixedOption, selectableOption }.OrderBy(option => option.LayoutOrder));
}

public sealed record StimulationChannelOptionViewModel(
    int PhysicalChannelId,
    StimulationChannelRole Role,
    StimulusDirection Direction
)
{
    public string Label =>
        $"CH{PhysicalChannelId} · {StimulationElectrodeRoleResolver.GetLabel(Direction, Role)}";

    public string DisplayName => $"CH{PhysicalChannelId}";

    public string UiLabel => $"CH{PhysicalChannelId} · {ElectrodeRoleDisplay.Label(Direction, Role)}";
}

public sealed class ElectrodeImpedanceItemViewModel : ObservableObject, IDisposable
{
    private readonly Action<
        ElectrodeImpedanceItemViewModel,
        StimulationChannelOptionViewModel?
    >? _channelChanged;
    private StimulationChannelOptionViewModel? _selectedStimulationChannel;
    private string _channelConflictText;

    public ElectrodeImpedanceItemViewModel(
        ElectrodeSiteViewModel site,
        IReadOnlyList<StimulationChannelOptionViewModel> stimulationChannels,
        StimulationChannelOptionViewModel? selectedStimulationChannel,
        bool showStimulationChannel,
        string? channelConflictText,
        Action<ElectrodeImpedanceItemViewModel, StimulationChannelOptionViewModel?>? channelChanged
    )
    {
        Site = site;
        StimulationChannels = stimulationChannels;
        _selectedStimulationChannel = selectedStimulationChannel;
        ShowStimulationChannel = showStimulationChannel;
        _channelConflictText = channelConflictText ?? string.Empty;
        _channelChanged = channelChanged;
        Site.PropertyChanged += OnSitePropertyChanged;
    }

    public ElectrodeSiteViewModel Site { get; }

    public bool IsSelected => Site.IsSelected;

    public string Label => ShowStimulationChannel ? Site.StimulationPositionLabel : Site.PositionName;

    public string RoleText => ShowStimulationChannel ? Site.RoleText : Site.AcquisitionRoleText;

    public string UiLabel => ShowStimulationChannel ? Site.UiPositionLabel : Site.PositionName;

    public string UiRoleText => ShowStimulationChannel ? Site.UiRoleText : Site.AcquisitionRoleText;

    public bool RequiresImpedanceDetection =>
        ShowStimulationChannel
            ? Site.StimulationChannelRole != StimulationChannelRole.FixedActive
            : Site.Role == ElectrodeRole.Acquisition;

    private double? ModeImpedance => ShowStimulationChannel ? Site.StimulationImpedance : Site.AcquisitionImpedance;

    public string ImpedanceText => RequiresImpedanceDetection ? ElectrodeSiteViewModel.GetImpedanceRangeText(Quality) : "-";

    public string QualityText => RequiresImpedanceDetection ? ElectrodeSiteViewModel.QualityLabel(Quality) : "无需检测";

    public ImpedanceQuality Quality =>
        RequiresImpedanceDetection ? ElectrodeSiteViewModel.FromImpedance(ModeImpedance) : ImpedanceQuality.Unknown;

    public ResolvedElectrodeRole? ResolvedStimulationRole => ShowStimulationChannel ? Site.ResolvedStimulationRole : null;

    public IReadOnlyList<StimulationChannelOptionViewModel> StimulationChannels { get; }

    public bool ShowStimulationChannel { get; }

    public string ChannelConflictText
    {
        get => _channelConflictText;
        set
        {
            if (SetProperty(ref _channelConflictText, value))
                OnPropertyChanged(nameof(HasChannelConflict));
        }
    }

    public bool HasChannelConflict => !string.IsNullOrWhiteSpace(ChannelConflictText);

    public StimulationChannelOptionViewModel? SelectedStimulationChannel
    {
        get => _selectedStimulationChannel;
        set
        {
            if (!SetProperty(ref _selectedStimulationChannel, value))
                return;
            _channelChanged?.Invoke(this, value);
        }
    }

    public void Dispose() => Site.PropertyChanged -= OnSitePropertyChanged;

    private void OnSitePropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        OnPropertyChanged(nameof(IsSelected));
        if (e.PropertyName == nameof(ElectrodeSiteViewModel.StimulationPhysicalChannelId))
            SetProperty(ref _selectedStimulationChannel,
                StimulationChannels.FirstOrDefault(c => c.PhysicalChannelId == Site.StimulationPhysicalChannelId),
                nameof(SelectedStimulationChannel));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(UiLabel));
        OnPropertyChanged(nameof(UiRoleText));
        OnPropertyChanged(nameof(RequiresImpedanceDetection));
        OnPropertyChanged(nameof(ImpedanceText));
        OnPropertyChanged(nameof(QualityText));
        OnPropertyChanged(nameof(Quality));
        OnPropertyChanged(nameof(ResolvedStimulationRole));
    }
}

public sealed class ElectrodeSiteViewModel : ObservableObject
{
    private ElectrodeRole _role;
    private Guid? _stimulusTargetId;
    private int? _targetDisplayOrder;
    private int? _stimulusPositionIndex;
    private int? _stimulationPhysicalChannelId;
    private StimulationChannelRole? _stimulationChannelRole;
    private ResolvedElectrodeRole? _resolvedStimulationRole;
    private string? _stimulationRoleText;
    private bool _isSelected;
    private bool _isAvailable;
    private double? _stimulationImpedance;
    private double? _acquisitionImpedance;

    public ElectrodeSiteViewModel(
        string name,
        double x,
        double y,
        bool canStimulate,
        ElectrodeRole role = ElectrodeRole.None,
        string? positionName = null,
        ElectrodeVector3? position3D = null,
        ElectrodeVector3? normal3D = null
    )
    {
        Name = name;
        PositionName = string.IsNullOrWhiteSpace(positionName) ? name : positionName;
        X = x;
        Y = y;
        Position3D = position3D;
        Normal3D = normal3D;
        CanStimulate = canStimulate;
        _role = role;
    }

    public string Name { get; }

    public string PositionName { get; }

    public ElectrodeVector3? Position3D { get; }
    public ElectrodeVector3? Normal3D { get; }

    public double X { get; }

    public double Y { get; }

    public bool CanStimulate { get; }

    public ElectrodeRole Role
    {
        get => _role;
        set
        {
            if (SetProperty(ref _role, value))
                NotifyAssignmentDisplayChanged();
        }
    }

    public Guid? StimulusTargetId
    {
        get => _stimulusTargetId;
        private set => SetProperty(ref _stimulusTargetId, value);
    }

    public int? TargetDisplayOrder
    {
        get => _targetDisplayOrder;
        private set => SetProperty(ref _targetDisplayOrder, value);
    }

    public int? StimulationPhysicalChannelId
    {
        get => _stimulationPhysicalChannelId;
        private set => SetProperty(ref _stimulationPhysicalChannelId, value);
    }

    public StimulationChannelRole? StimulationChannelRole
    {
        get => _stimulationChannelRole;
        private set
        {
            if (SetProperty(ref _stimulationChannelRole, value))
                NotifyAssignmentDisplayChanged();
        }
    }

    public ResolvedElectrodeRole? ResolvedStimulationRole
    {
        get => _resolvedStimulationRole;
        private set
        {
            if (SetProperty(ref _resolvedStimulationRole, value))
                NotifyAssignmentDisplayChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetProperty(ref _isAvailable, value))
                OnPropertyChanged(nameof(IsDimmed));
        }
    }

    public bool IsDimmed => !CanStimulate && !IsAvailable;

    public double? StimulationImpedance
    {
        get => _stimulationImpedance;
        set { if (SetProperty(ref _stimulationImpedance, value)) NotifyImpedanceChanged(); }
    }

    public double? AcquisitionImpedance
    {
        get => _acquisitionImpedance;
        set { if (SetProperty(ref _acquisitionImpedance, value)) NotifyImpedanceChanged(); }
    }

    // Compatibility projection for single-role consumers; mode-aware code uses Get/SetImpedance.
    public double? Impedance
    {
        get => IsStimulus ? StimulationImpedance : AcquisitionImpedance;
        set => SetImpedance(IsStimulus ? ElectrodeConfigurationMode.Stimulus : ElectrodeConfigurationMode.Acquisition, value);
    }

    public double? GetImpedance(ElectrodeConfigurationMode mode) =>
        mode == ElectrodeConfigurationMode.Stimulus ? StimulationImpedance : AcquisitionImpedance;

    public void SetImpedance(ElectrodeConfigurationMode mode, double? value)
    {
        if (mode == ElectrodeConfigurationMode.Stimulus) StimulationImpedance = value;
        else AcquisitionImpedance = value;
    }

    private void NotifyImpedanceChanged()
    {
        foreach (var property in new[] { nameof(Impedance), nameof(ImpedanceText), nameof(Quality),
            nameof(QualityText), nameof(ToolTipText), nameof(UiToolTipText), nameof(ShowSharedDivider),
            nameof(StimulationDisplayQuality), nameof(AcquisitionDisplayQuality), nameof(DisplayQuality),
            nameof(StimulationColor), nameof(AcquisitionColor), nameof(StimulationForeground), nameof(AcquisitionForeground) })
            OnPropertyChanged(property);
    }

    public bool IsShared => IsStimulus && IsAcquisition;
    public bool ShowSharedDivider => IsShared && StimulationImpedance is null && AcquisitionImpedance is null;
    public string AcquisitionLabel => Role switch { ElectrodeRole.Reference => "REF", ElectrodeRole.Ground => "GND", _ => "采" };
    public string AcquisitionRoleText => Role switch { ElectrodeRole.Reference => "参考电极 REF", ElectrodeRole.Ground => "地电极 GND", ElectrodeRole.Acquisition => "采集电极", _ => "未配置" };
    private bool _hasStimulationDetectionResult;
    private bool _hasAcquisitionDetectionResult;

    public void SetDetectionResultAvailable(ElectrodeConfigurationMode mode, bool available)
    {
        ref var state = ref (mode == ElectrodeConfigurationMode.Stimulus
            ? ref _hasStimulationDetectionResult : ref _hasAcquisitionDetectionResult);
        if (state == available) return;
        state = available;
        NotifyImpedanceChanged();
    }

    // Presentation only: no synthetic readings are added for electrodes that need no test.
    public ImpedanceQuality StimulationDisplayQuality => _hasStimulationDetectionResult && IsStimulus
        && StimulationChannelRole == global::EGGtCSPlatform.ViewModels.Pages.StimulationChannelRole.FixedActive
            ? ImpedanceQuality.Excellent : FromImpedance(StimulationImpedance);
    public ImpedanceQuality AcquisitionDisplayQuality => _hasAcquisitionDetectionResult
        && Role is ElectrodeRole.Reference or ElectrodeRole.Ground
        ? ImpedanceQuality.Excellent : FromImpedance(AcquisitionImpedance);
    public ImpedanceQuality DisplayQuality => IsStimulus ? StimulationDisplayQuality : AcquisitionDisplayQuality;
    public string StimulationColor => ColorFor(StimulationDisplayQuality);
    public string AcquisitionColor => ColorFor(AcquisitionDisplayQuality);
    public string StimulationForeground => ForegroundFor(StimulationDisplayQuality);
    public string AcquisitionForeground => ForegroundFor(AcquisitionDisplayQuality);

    public static string ColorFor(ImpedanceQuality quality) => quality switch
    {
        ImpedanceQuality.Excellent => "#38A169", ImpedanceQuality.Good => "#2F86FF",
        ImpedanceQuality.Medium => "#F3A619", ImpedanceQuality.Poor => "#FD5B38",
        ImpedanceQuality.Bad => "#E91919", _ => "#E9EBFF"
    };

    // Colored result/no-test regions use white text; pending regions retain the role color.
    public static string ForegroundFor(ImpedanceQuality quality) => quality switch
    {
        ImpedanceQuality.Unknown => "#3941B6", _ => "#FFFFFF"
    };

    public string DetectionDescription(ElectrodeConfigurationMode mode)
    {
        var required = mode == ElectrodeConfigurationMode.Stimulus
            ? StimulationChannelRole == global::EGGtCSPlatform.ViewModels.Pages.StimulationChannelRole.Selectable
            : Role == ElectrodeRole.Acquisition;
        var value = GetImpedance(mode);
        return !required ? "无需检测" : value is null ? "未检测" : $"{GetImpedanceRangeText(FromImpedance(value))} · {QualityLabel(FromImpedance(value))}";
    }

    public bool IsStimulus => StimulusTargetId.HasValue;

    public bool HasStimulationChannel =>
        StimulationPhysicalChannelId.HasValue && StimulationChannelRole.HasValue;

    public bool IsAcquisition =>
        Role is ElectrodeRole.Acquisition or ElectrodeRole.Reference or ElectrodeRole.Ground;

    public string StimulationPositionLabel => ResolvedStimulationRole switch
    {
        ResolvedElectrodeRole.Anode when IsStimulus => $"{PositionName}·A",
        ResolvedElectrodeRole.Cathode when IsStimulus => $"{PositionName}·C",
        _ => PositionName,
    };

    public string DisplayName =>
        IsStimulus
            ? ResolvedStimulationRole is ResolvedElectrodeRole.Anode or ResolvedElectrodeRole.Cathode
                ? StimulationPositionLabel
                : $"{PositionName}\n{StimulusRoleToken}"
            : Role switch
            {
                ElectrodeRole.Reference => $"{PositionName}\nREF",
                ElectrodeRole.Ground => $"{PositionName}\nGND",
                _ => PositionName,
            };

    public string RoleText =>
        IsStimulus
            ? _stimulationRoleText ?? "待分配刺激物理通道"
            : Role switch
            {
                ElectrodeRole.Acquisition => "采集电极",
                ElectrodeRole.Reference => "参考电极 REF",
                ElectrodeRole.Ground => "地电极 GND",
                _ => "未配置",
            };

    public string UiRoleText => IsStimulus && ResolvedStimulationRole is { } resolved
        ? ElectrodeRoleDisplay.Label(resolved) : RoleText;

    public string UiPositionLabel => IsStimulus && ResolvedStimulationRole is { } resolved
        ? $"{PositionName}·{ElectrodeRoleDisplay.Token(resolved)}" : StimulationPositionLabel;

    public string UiDisplayName => IsStimulus && ResolvedStimulationRole is not null
        ? UiPositionLabel : DisplayName;

    public string UiToolTipText => IsShared
        ? $"{Name} · {UiRoleText} · {DetectionDescription(ElectrodeConfigurationMode.Stimulus)}\n{AcquisitionRoleText} · {DetectionDescription(ElectrodeConfigurationMode.Acquisition)}"
        : $"{Name} · {UiRoleText} · {DetectionDescription(IsStimulus ? ElectrodeConfigurationMode.Stimulus : ElectrodeConfigurationMode.Acquisition)}";

    public void AssignStimulusPosition(
        Guid targetId,
        int targetDisplayOrder,
        int positionIndex,
        StimulationChannelRole channelRole,
        StimulusDirection direction
    )
    {
        StimulusTargetId = targetId;
        TargetDisplayOrder = targetDisplayOrder;
        _stimulusPositionIndex = positionIndex;
        StimulationChannelRole = channelRole;
        ResolvedStimulationRole =
            channelRole
            == global::EGGtCSPlatform.ViewModels.Pages.StimulationChannelRole.FixedActive
                ? StimulationElectrodeRoleResolver.Resolve(direction).FixedActive
                : StimulationElectrodeRoleResolver.Resolve(direction).Selectable;
        _stimulationRoleText = StimulationElectrodeRoleResolver.GetLabel(direction, channelRole);
        NotifyAssignmentDisplayChanged();
    }

    public void AssignStimulationChannel(StimulationChannelOptionViewModel option)
    {
        if (StimulationChannelRole != option.Role)
            throw new InvalidOperationException("刺激物理通道角色必须与已选择的电极角色一致。");
        StimulationPhysicalChannelId = option.PhysicalChannelId;
        NotifyAssignmentDisplayChanged();
    }

    public void ClearStimulationChannel()
    {
        StimulationPhysicalChannelId = null;
        StimulationChannelRole = null;
        ResolvedStimulationRole = null;
        _stimulationRoleText = null;
        NotifyAssignmentDisplayChanged();
    }

    public void ClearStimulusAssignment()
    {
        StimulusTargetId = null;
        TargetDisplayOrder = null;
        _stimulusPositionIndex = null;
        ClearStimulationChannel();
    }

    public StimulusElectrodeAssignment? CreateAssignment() =>
        IsStimulus
        && StimulusTargetId is { } targetId
        && StimulationPhysicalChannelId is { } physicalChannelId
        && StimulationChannelRole is { } channelRole
            ? new StimulusElectrodeAssignment(Name, targetId, physicalChannelId, channelRole)
            : null;

    public string ImpedanceText => Impedance is null ? "- kΩ" : GetImpedanceRangeText(Quality);

    public ImpedanceQuality Quality => FromImpedance(Impedance);

    public string QualityText => QualityLabel(Quality);

    public static string QualityLabel(ImpedanceQuality quality) =>
        quality switch
        {
            ImpedanceQuality.Excellent => "优",
            ImpedanceQuality.Good => "良",
            ImpedanceQuality.Medium => "中",
            ImpedanceQuality.Poor => "差",
            ImpedanceQuality.Bad => "不良",
            _ => "未检测",
        };

    public string ToolTipText =>
        Impedance is null
            ? $"{Name} · {RoleText} · 未检测"
            : $"{Name} · {RoleText} · {GetImpedanceRangeText(Quality)} · {QualityText}";

    public static ImpedanceQuality FromImpedance(double? value) =>
        value switch
        {
            null => ImpedanceQuality.Unknown,
            double number when !double.IsFinite(number) || number < 0 => ImpedanceQuality.Unknown,
            <= 10 => ImpedanceQuality.Excellent,
            <= 20 => ImpedanceQuality.Good,
            <= 30 => ImpedanceQuality.Medium,
            <= 40 => ImpedanceQuality.Poor,
            _ => ImpedanceQuality.Bad,
        };

    public static string GetImpedanceRangeText(ImpedanceQuality quality) =>
        quality switch
        {
            ImpedanceQuality.Excellent => "≤10 kΩ",
            ImpedanceQuality.Good => "≤20 kΩ",
            ImpedanceQuality.Medium => "≤30 kΩ",
            ImpedanceQuality.Poor => "≤40 kΩ",
            ImpedanceQuality.Bad => ">40 kΩ",
            _ => "- kΩ",
        };

    private string StimulusRoleToken =>
        ResolvedStimulationRole switch
        {
            ResolvedElectrodeRole.Anode => "A",
            ResolvedElectrodeRole.Cathode => "C",
            ResolvedElectrodeRole.FixedActive => "固",
            ResolvedElectrodeRole.Stimulating => "选",
            _ => $"E{_stimulusPositionIndex}",
        };

    private void NotifyAssignmentDisplayChanged()
    {
        NotifyImpedanceChanged();
        OnPropertyChanged(nameof(IsShared));
        OnPropertyChanged(nameof(AcquisitionLabel));
        OnPropertyChanged(nameof(AcquisitionRoleText));
        OnPropertyChanged(nameof(UiRoleText));
        OnPropertyChanged(nameof(UiPositionLabel));
        OnPropertyChanged(nameof(UiDisplayName));
        OnPropertyChanged(nameof(UiToolTipText));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(IsStimulus));
        OnPropertyChanged(nameof(HasStimulationChannel));
        OnPropertyChanged(nameof(IsAcquisition));
        OnPropertyChanged(nameof(ToolTipText));
    }
}
