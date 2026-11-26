using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Views.Pages;

public partial class ToleranceTestPageView : UserControl
{
    private readonly HashSet<Key> _pressedKeys = [];
    private Window? _window;

    public ToleranceTestPageView()
    {
        InitializeComponent();
        Focusable = true;
        AddHandler(KeyDownEvent, OnAdjustmentKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ToleranceTestPageViewModel model)
            model.Resume();
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.AddHandler(
                KeyUpEvent,
                OnAdjustmentKeyUp,
                RoutingStrategies.Tunnel,
                handledEventsToo: true
            );
            _window.Deactivated += OnWindowDeactivated;
        }
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_window is { IsActive: true } && IsEffectivelyVisible)
                    Focus();
            },
            DispatcherPriority.Loaded
        );
    }

    private void OnAdjustmentKeyDown(object? sender, KeyEventArgs e)
    {
        if (_window is null || !IsEffectivelyVisible)
            return;
        if (e.Key is not (Key.Up or Key.Right or Key.Down or Key.Left))
            return;
        var firstPress = _pressedKeys.Add(e.Key);
        if (e.KeyModifiers != KeyModifiers.None)
            return;
        e.Handled = true;
        if (!firstPress || DataContext is not ToleranceTestPageViewModel model)
            return;
        var command = e.Key is Key.Up or Key.Right ? model.IncreaseCommand : model.DecreaseCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OnAdjustmentKeyUp(object? sender, KeyEventArgs e) => _pressedKeys.Remove(e.Key);

    private void OnWindowDeactivated(object? sender, EventArgs e) => _pressedKeys.Clear();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.RemoveHandler(KeyUpEvent, OnAdjustmentKeyUp);
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }
        _pressedKeys.Clear();
        base.OnDetachedFromVisualTree(e);
    }
}
