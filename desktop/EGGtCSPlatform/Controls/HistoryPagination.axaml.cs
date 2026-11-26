using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace EGGtCSPlatform.Controls;

/// <summary>Shared presentation only; the owning page performs all queries.</summary>
public partial class HistoryPagination : UserControl
{
    public static readonly StyledProperty<int> PageNumberProperty = AvaloniaProperty.Register<
        HistoryPagination,
        int
    >(nameof(PageNumber), 1);
    public static readonly StyledProperty<int> PageSizeProperty = AvaloniaProperty.Register<
        HistoryPagination,
        int
    >(nameof(PageSize), 10, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IEnumerable<int>?> PageSizesProperty =
        AvaloniaProperty.Register<HistoryPagination, IEnumerable<int>?>(nameof(PageSizes));
    public static readonly StyledProperty<int> TotalCountProperty = AvaloniaProperty.Register<
        HistoryPagination,
        int
    >(nameof(TotalCount));
    public static readonly StyledProperty<bool> IsBusyProperty = AvaloniaProperty.Register<
        HistoryPagination,
        bool
    >(nameof(IsBusy));
    public static readonly StyledProperty<string> UnitProperty = AvaloniaProperty.Register<
        HistoryPagination,
        string
    >(nameof(Unit), "条");
    public static readonly StyledProperty<ICommand?> PreviousCommandProperty =
        AvaloniaProperty.Register<HistoryPagination, ICommand?>(nameof(PreviousCommand));
    public static readonly StyledProperty<ICommand?> NextCommandProperty =
        AvaloniaProperty.Register<HistoryPagination, ICommand?>(nameof(NextCommand));
    public static readonly DirectProperty<HistoryPagination, string> PageDisplayProperty =
        AvaloniaProperty.RegisterDirect<HistoryPagination, string>(
            nameof(PageDisplay),
            c => c.PageDisplay
        );
    public static readonly DirectProperty<HistoryPagination, int> DisplayPageNumberProperty =
        AvaloniaProperty.RegisterDirect<HistoryPagination, int>(
            nameof(DisplayPageNumber),
            c => c.DisplayPageNumber
        );
    public static readonly DirectProperty<HistoryPagination, bool> CanPreviousProperty =
        AvaloniaProperty.RegisterDirect<HistoryPagination, bool>(
            nameof(CanPrevious),
            c => c.CanPrevious
        );
    public static readonly DirectProperty<HistoryPagination, bool> CanNextProperty =
        AvaloniaProperty.RegisterDirect<HistoryPagination, bool>(nameof(CanNext), c => c.CanNext);

    public int PageNumber
    {
        get => GetValue(PageNumberProperty);
        set => SetValue(PageNumberProperty, value);
    }
    public int PageSize
    {
        get => GetValue(PageSizeProperty);
        set => SetValue(PageSizeProperty, value);
    }
    public IEnumerable<int>? PageSizes
    {
        get => GetValue(PageSizesProperty);
        set => SetValue(PageSizesProperty, value);
    }
    public int TotalCount
    {
        get => GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }
    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }
    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }
    public ICommand? PreviousCommand
    {
        get => GetValue(PreviousCommandProperty);
        set => SetValue(PreviousCommandProperty, value);
    }
    public ICommand? NextCommand
    {
        get => GetValue(NextCommandProperty);
        set => SetValue(NextCommandProperty, value);
    }
    private int _displayPageNumber;
    private string _pageDisplay = "";
    private bool _canPrevious,
        _canNext;
    public int DisplayPageNumber => _displayPageNumber;
    public string PageDisplay => _pageDisplay;
    public bool CanPrevious => _canPrevious;
    public bool CanNext => _canNext;

    public HistoryPagination()
    {
        InitializeComponent();
        UpdatePagination();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (
            change.Property == PageNumberProperty
            || change.Property == PageSizeProperty
            || change.Property == TotalCountProperty
            || change.Property == IsBusyProperty
            || change.Property == UnitProperty
        )
            UpdatePagination();
    }

    private void UpdatePagination()
    {
        var totalPages = (int)Math.Ceiling((double)Math.Max(0, TotalCount) / Math.Max(1, PageSize));
        SetAndRaise(
            DisplayPageNumberProperty,
            ref _displayPageNumber,
            TotalCount == 0 ? 0 : PageNumber
        );
        SetAndRaise(
            PageDisplayProperty,
            ref _pageDisplay,
            $"共 {TotalCount} {Unit} · 第 {DisplayPageNumber} / {totalPages} 页"
        );
        SetAndRaise(
            CanPreviousProperty,
            ref _canPrevious,
            !IsBusy && TotalCount > 0 && PageNumber > 1
        );
        SetAndRaise(CanNextProperty, ref _canNext, !IsBusy && PageNumber < totalPages);
    }
}
