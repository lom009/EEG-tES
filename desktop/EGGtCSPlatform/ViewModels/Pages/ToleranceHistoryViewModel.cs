using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Persistence;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed record ToleranceKindOption(string Label, StimulusKind? Kind);

public partial class ToleranceHistoryGroupViewModel(ToleranceHistoryGroup group) : ViewModelBase
{
    public Guid Id => group.Id;
    public string SubjectId => group.SubjectId;
    public string Summary => group.Summary;
    public string Sites => group.Sites;
    public int Count => group.Count;
    public ToleranceResultSnapshot Latest => group.Latest;
    public IReadOnlyList<ToleranceResultRow> Rows => group.Rows;
    public IReadOnlyList<ElectrodePointViewModel> ElectrodePoints { get; } =
        group
            .Latest.Test.Electrodes.OrderBy(e =>
                group
                    .Latest.Test.Configuration.Targets.Single(t => t.TargetId == e.TargetId)
                    .DisplayOrder
            )
            .ThenBy(e => e.PhysicalChannelId)
            .Select(e =>
            {
                var roles = StimulationElectrodeRoleResolver.Resolve(
                    group.Latest.Test.Configuration.Direction
                );
                var token = ElectrodeRoleDisplay.Token(
                    e.Role == StimulationChannelRole.FixedActive
                        ? roles.FixedActive
                        : roles.Selectable
                );
                return new ElectrodePointViewModel($"{e.SiteId}·{token}");
            })
            .ToArray();

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

public partial class ToleranceHistoryViewModel(ITolerancePersistenceService persistence)
    : ViewModelBase,
        IDisposable
{
    private CancellationTokenSource? _load;
    private bool _disposed;
    private int _pageSize = 10;

    [ObservableProperty]
    private string _subjectId = "";

    [ObservableProperty]
    private ToleranceKindOption _selectedKind = new("全部范式", null);

    [ObservableProperty]
    private DateTime? _startDate;

    [ObservableProperty]
    private DateTime? _endDate;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private int _totalCount;

    public ToleranceKindOption[] Kinds { get; } =
    [
        new("全部范式", null),
        new("tDCS", StimulusKind.TDcs),
        new("tACS", StimulusKind.TAcs),
        new("tRNS", StimulusKind.TRns),
        new("tPCS", StimulusKind.TPcs),
        new("Sham", StimulusKind.Sham),
    ];
    public IReadOnlyList<int> PageSizes { get; } = [10, 20, 50];
    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (_disposed || !PageSizes.Contains(value) || !SetProperty(ref _pageSize, value))
                return;
            Page = 1;
            NotifyPagination();
            _ = LoadAsync();
        }
    }
    public ObservableCollection<ToleranceHistoryGroupViewModel> Groups { get; } = [];
    public bool HasGroups => Groups.Count > 0;
    public string PageText =>
        $"共 {TotalCount} 组，每页 {PageSize} 组 · 第 {(TotalCount == 0 ? 0 : Page)} / {(TotalCount + PageSize - 1) / PageSize} 页";
    public bool CanPrevious => !_disposed && !IsLoading && Page > 1;
    public bool CanNext => !_disposed && !IsLoading && (long)Page * PageSize < TotalCount;

    [RelayCommand]
    private Task SearchAsync()
    {
        Page = 1;
        return LoadAsync();
    }

    [RelayCommand]
    private Task ResetAsync()
    {
        SubjectId = "";
        SelectedKind = Kinds[0];
        StartDate = null;
        EndDate = null;
        SetProperty(ref _pageSize, 10, nameof(PageSize));
        Page = 1;
        NotifyPagination();
        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousAsync()
    {
        if (!CanPrevious)
            return Task.CompletedTask;
        Page--;
        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextAsync()
    {
        if (!CanNext)
            return Task.CompletedTask;
        Page++;
        return LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_disposed)
            return;
        _load?.Cancel();
        var cancellation = new CancellationTokenSource();
        _load = cancellation;
        IsLoading = true;
        HasError = false;
        StatusText = "正在读取耐受度记录…";
        try
        {
            if (StartDate?.Date > EndDate?.Date)
                throw new ArgumentException("开始日期不能晚于结束日期。");
            var result = await persistence.QueryAsync(
                new(SubjectId, SelectedKind.Kind, StartDate, EndDate, Page, PageSize),
                cancellation.Token
            );
            if (cancellation.IsCancellationRequested || _disposed)
                return;
            Groups.Clear();
            foreach (var group in result.Groups)
                Groups.Add(new(group));
            TotalCount = result.TotalCount;
            OnPropertyChanged(nameof(HasGroups));
            StatusText = TotalCount == 0 ? "暂无耐受度测试记录" : "";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (!cancellation.IsCancellationRequested && !_disposed)
            {
                HasError = true;
                StatusText = $"读取失败：{e.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_load, cancellation))
            {
                _load = null;
                IsLoading = false;
            }
            cancellation.Dispose();
        }
    }

    partial void OnPageChanged(int value) => NotifyPagination();

    partial void OnTotalCountChanged(int value) => NotifyPagination();

    partial void OnIsLoadingChanged(bool value) => NotifyPagination();

    private void NotifyPagination()
    {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        _load?.Cancel();
        NotifyPagination();
    }
}
