using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.Views;

public partial class CommunicationDebugWindow : Window
{
    private CommunicationDebugAssistantViewModel? _viewModel;
    private ScrollViewer? _scrollViewer;
    private bool _followLatest = true;
    private bool _scrollScheduled;

    public CommunicationDebugWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        _viewModel = DataContext as CommunicationDebugAssistantViewModel;
        if (_viewModel is not null)
            _viewModel.VisibleEntries.CollectionChanged += OnEntriesChanged;
        Dispatcher.UIThread.Post(
            () =>
            {
                _scrollViewer = TrafficList
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault();
                if (_scrollViewer is not null)
                    _scrollViewer.ScrollChanged += OnScrollChanged;
                ScheduleScrollToLatest();
            },
            DispatcherPriority.Loaded
        );
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.VisibleEntries.CollectionChanged -= OnEntriesChanged;
        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        _viewModel = null;
        _scrollViewer = null;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_followLatest)
            ScheduleScrollToLatest();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
            return;
        var remaining = viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y;
        _followLatest = remaining <= 4;
    }

    private async void OnCopyPacketClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UdpTrafficDisplayEntry entry })
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(entry.CopyText);
    }

    private void ScheduleScrollToLatest()
    {
        if (_scrollScheduled)
            return;
        _scrollScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _scrollScheduled = false;
                if (!_followLatest || _viewModel?.VisibleEntries.Count is not > 0)
                    return;
                TrafficList.ScrollIntoView(_viewModel.VisibleEntries[^1]);
            },
            DispatcherPriority.Background
        );
    }
}
