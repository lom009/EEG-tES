using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.Services;

public sealed class CommunicationDebugAssistantController(
    ByteTrafficLogDispatcher trafficDispatcher
) : IDisposable, IAsyncDisposable
{
    private readonly HashSet<Window> _hosts = [];
    private CommunicationDebugWindow? _window;
    private CommunicationDebugAssistantViewModel? _viewModel;
    private IDisposable? _trafficSubscription;
    private bool _disposed;

    public void Attach(Window hostWindow)
    {
        ArgumentNullException.ThrowIfNull(hostWindow);
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hosts.Add(hostWindow))
            return;
        hostWindow.Closed += OnHostClosed;
        hostWindow.AddHandler(InputElement.KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
    }

    public static bool IsOpenGesture(Key key, KeyModifiers modifiers) =>
        key == Key.D && modifiers == (KeyModifiers.Control | KeyModifiers.Shift);

    public void Open()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            if (!_window.IsVisible)
                _window.Show();
            _window.Activate();
            return;
        }

        var captureSession = new UdpTrafficCaptureSession();
        var viewModel = new CommunicationDebugAssistantViewModel(captureSession);
        IDisposable? subscription = null;
        CommunicationDebugWindow? window = null;
        try
        {
            subscription = trafficDispatcher.Attach(captureSession);
            window = new CommunicationDebugWindow { DataContext = viewModel };
            window.Closed += OnWindowClosed;
            _trafficSubscription = subscription;
            _viewModel = viewModel;
            _window = window;
            window.Show();
            window.Activate();
        }
        catch
        {
            if (window is not null)
                window.Closed -= OnWindowClosed;
            subscription?.Dispose();
            viewModel.Dispose();
            _trafficSubscription = null;
            _viewModel = null;
            _window = null;
            throw;
        }
    }

    // Synchronous callers are UI event handlers; DI uses DisposeAsync.
    public void Dispose() => DisposeOnUiThread();

    public async ValueTask DisposeAsync()
    {
        if (Dispatcher.UIThread.CheckAccess())
            DisposeOnUiThread();
        else
            await Dispatcher.UIThread.InvokeAsync(DisposeOnUiThread);
    }

    private void DisposeOnUiThread()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        foreach (var host in _hosts)
        {
            host.RemoveHandler(InputElement.KeyDownEvent, OnHostKeyDown);
            host.Closed -= OnHostClosed;
        }
        _hosts.Clear();
        var window = _window;
        if (window is not null)
            window.Closed -= OnWindowClosed;
        StopCapture();
        window?.Close();
    }

    private void OnHostClosed(object? sender, EventArgs e)
    {
        if (sender is not Window host)
            return;
        host.RemoveHandler(InputElement.KeyDownEvent, OnHostKeyDown);
        host.Closed -= OnHostClosed;
        _hosts.Remove(host);
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsOpenGesture(e.Key, e.KeyModifiers))
            return;
        e.Handled = true;
        Open();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is CommunicationDebugWindow window)
            window.Closed -= OnWindowClosed;
        StopCapture();
    }

    private void StopCapture()
    {
        // Detach first so no new packet can enter the session being torn down.
        _trafficSubscription?.Dispose();
        _trafficSubscription = null;
        _viewModel?.Dispose();
        _viewModel = null;
        _window = null;
    }
}
