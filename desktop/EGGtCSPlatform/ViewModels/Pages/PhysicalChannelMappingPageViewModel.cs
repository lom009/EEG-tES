using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed record PhysicalChannelOption(int? Value, string Label);

public sealed class PhysicalChannelMappingItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private PhysicalChannelOption _selectedOption;

    public PhysicalChannelMappingItemViewModel(
        ElectrodePositionDefinition position,
        IReadOnlyList<PhysicalChannelOption> options,
        int? physicalChannel,
        Action changed
    )
    {
        Position = position;
        Options = options;
        _selectedOption = options.First(item => item.Value == physicalChannel);
        _changed = changed;
    }

    public ElectrodePositionDefinition Position { get; }

    public string ElectrodeId => Position.Id;

    public string PositionName => Position.Position;

    public IReadOnlyList<PhysicalChannelOption> Options { get; }

    public PhysicalChannelOption SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (SetProperty(ref _selectedOption, value))
                _changed();
        }
    }

    public int? PhysicalChannel => SelectedOption.Value;
}

public partial class PhysicalChannelMappingPageViewModel : PageViewModel
{
    private readonly IEegPhysicalChannelMappingService _mappingService;
    private readonly IElectrodePositionCatalog _electrodeCatalog;
    private IReadOnlyList<string> _loadValidationErrors = [];
    private bool _isSynchronizingSelection;

    [ObservableProperty]
    private PhysicalChannelMappingItemViewModel? _selectedMapping;

    [ObservableProperty]
    private ElectrodeSiteViewModel? _selectedPoint;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isSaved;

    public PhysicalChannelMappingPageViewModel(
        IEegPhysicalChannelMappingService mappingService,
        IElectrodePositionCatalog? electrodeCatalog = null
    )
        : base(ApplicationPageNames.PhysicalChannelMapping, "EEG采集电极物理通道映射")
    {
        _mappingService = mappingService;
        _electrodeCatalog = electrodeCatalog ?? ElectrodePositionCatalog.Default;
        var loadedSnapshot = mappingService.Load();
        PhysicalChannelCount = loadedSnapshot.PhysicalChannelCount;
        _loadValidationErrors = loadedSnapshot.ValidationErrors;
        ChannelOptions =
        [
            new(null, "未映射"),
            .. Enumerable
                .Range(1, PhysicalChannelCount)
                .Select(index => new PhysicalChannelOption(index, index.ToString())),
        ];

        IReadOnlyDictionary<string, int?> loaded = new Dictionary<string, int?>();
        loaded = loadedSnapshot
            .Mappings.Where(item => !string.IsNullOrWhiteSpace(item.ElectrodeId))
            .GroupBy(item => item.ElectrodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().PhysicalChannel,
                StringComparer.OrdinalIgnoreCase
            );
        if (_loadValidationErrors.Count > 0)
            StatusText =
                "配置文件存在非法项；当前页面已将无法使用的值显示为未映射，保存后将写回规范化配置。";

        Mappings = new ObservableCollection<PhysicalChannelMappingItemViewModel>(
            _electrodeCatalog.Positions.Select(position => new PhysicalChannelMappingItemViewModel(
                position,
                ChannelOptions,
                loaded.TryGetValue(position.Id, out var channel)
                && channel is >= 1
                && channel <= PhysicalChannelCount
                    ? channel
                    : null,
                OnMappingChanged
            ))
        );
        Points = CreatePoints(_electrodeCatalog.Positions);
        SelectPointCommand = new RelayCommand<ElectrodeSiteViewModel?>(SelectPoint);
        SaveCommand = new RelayCommand(Save, () => CanSave);
        SelectedMapping = Mappings.FirstOrDefault();
    }

    public IReadOnlyList<PhysicalChannelOption> ChannelOptions { get; }

    public int PhysicalChannelCount { get; }

    public string ChannelRangeText =>
        $"EEG采集物理通道：1～{PhysicalChannelCount}（与刺激通道独立）";

    public ObservableCollection<PhysicalChannelMappingItemViewModel> Mappings { get; }

    public ObservableCollection<ElectrodeSiteViewModel> Points { get; }

    public RelayCommand<ElectrodeSiteViewModel?> SelectPointCommand { get; }

    public RelayCommand SaveCommand { get; }

    public bool HasDuplicatePhysicalChannels =>
        Mappings
            .Where(item => item.PhysicalChannel.HasValue)
            .GroupBy(item => item.PhysicalChannel)
            .Any(group => group.Count() > 1);

    public bool CanSave => !HasDuplicatePhysicalChannels;

    public string ValidationText
    {
        get
        {
            if (HasDuplicatePhysicalChannels)
                return "同一个物理通道不能分配给多个电极位置，请检查重复配置。";
            if (_loadValidationErrors.Count > 0)
                return string.Join(" ", _loadValidationErrors);
            return $"物理通道范围为1～{PhysicalChannelCount}，允许点位保持未映射。";
        }
    }

    partial void OnSelectedMappingChanged(PhysicalChannelMappingItemViewModel? value)
    {
        if (_isSynchronizingSelection)
            return;
        _isSynchronizingSelection = true;
        SelectedPoint = value is null
            ? null
            : Points.FirstOrDefault(item => item.Name == value.ElectrodeId);
        foreach (var point in Points)
            point.IsSelected = ReferenceEquals(point, SelectedPoint);
        _isSynchronizingSelection = false;
    }

    private void SelectPoint(ElectrodeSiteViewModel? point)
    {
        if (point is null)
            return;
        _isSynchronizingSelection = true;
        SelectedPoint = point;
        foreach (var item in Points)
            item.IsSelected = ReferenceEquals(item, point);
        SelectedMapping = Mappings.FirstOrDefault(item => item.ElectrodeId == point.Name);
        _isSynchronizingSelection = false;
    }

    private void OnMappingChanged()
    {
        IsSaved = false;
        StatusText = string.Empty;
        OnPropertyChanged(nameof(HasDuplicatePhysicalChannels));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ValidationText));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Save()
    {
        if (!CanSave)
            return;
        try
        {
            _mappingService.Save(
                PhysicalChannelCount,
                Mappings
                    .Select(item => new EegPhysicalChannelMapping(
                        item.ElectrodeId,
                        item.PhysicalChannel
                    ))
                    .ToArray()
            );
            _loadValidationErrors = [];
            IsSaved = true;
            StatusText = "EEG采集物理通道映射已保存";
            OnPropertyChanged(nameof(ValidationText));
        }
        catch (Exception exception)
        {
            IsSaved = false;
            StatusText = $"保存失败：{exception.Message}";
        }
    }

    private static ObservableCollection<ElectrodeSiteViewModel> CreatePoints(
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
                true,
                positionName: position.Position
            ))
        );
    }
}
