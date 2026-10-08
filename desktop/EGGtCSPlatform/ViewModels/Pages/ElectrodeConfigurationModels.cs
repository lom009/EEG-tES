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
        ResolvedRole is ResolvedElectrodeRole.Anode or ResolvedElectrodeRole.FixedActive;

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

    public string Label => Site.StimulationPositionLabel;

    public string RoleText => Site.RoleText;

    public bool RequiresImpedanceDetection =>
        Site.IsStimulus
            ? Site.StimulationChannelRole != StimulationChannelRole.FixedActive
            : Site.Role == ElectrodeRole.Acquisition;

    public string ImpedanceText => RequiresImpedanceDetection ? Site.ImpedanceText : "-";

    public string QualityText => RequiresImpedanceDetection ? Site.QualityText : "无需检测";

    public ImpedanceQuality Quality =>
        RequiresImpedanceDetection ? Site.Quality : ImpedanceQuality.Unknown;

    public ResolvedElectrodeRole? ResolvedStimulationRole => Site.ResolvedStimulationRole;

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
    private double? _impedance;

    public ElectrodeSiteViewModel(
        string name,
        double x,
        double y,
        bool canStimulate,
        ElectrodeRole role = ElectrodeRole.None,
        string? positionName = null
    )
    {
        Name = name;
        PositionName = string.IsNullOrWhiteSpace(positionName) ? name : positionName;
        X = x;
        Y = y;
        CanStimulate = canStimulate;
        _role = role;
    }

    public string Name { get; }

    public string PositionName { get; }

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
        set => SetProperty(ref _isAvailable, value);
    }

    public double? Impedance
    {
        get => _impedance;
        set
        {
            if (!SetProperty(ref _impedance, value))
                return;
            OnPropertyChanged(nameof(ImpedanceText));
            OnPropertyChanged(nameof(Quality));
            OnPropertyChanged(nameof(QualityText));
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    public bool IsStimulus => StimulusTargetId.HasValue;

    public bool HasStimulationChannel =>
        StimulationPhysicalChannelId.HasValue && StimulationChannelRole.HasValue;

    public bool IsAcquisition =>
        !IsStimulus
        && Role is ElectrodeRole.Acquisition or ElectrodeRole.Reference or ElectrodeRole.Ground;

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
        Role = ElectrodeRole.None;
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

    public string QualityText =>
        Quality switch
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

    private static string GetImpedanceRangeText(ImpedanceQuality quality) =>
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
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(IsStimulus));
        OnPropertyChanged(nameof(HasStimulationChannel));
        OnPropertyChanged(nameof(IsAcquisition));
        OnPropertyChanged(nameof(ToolTipText));
    }
}
