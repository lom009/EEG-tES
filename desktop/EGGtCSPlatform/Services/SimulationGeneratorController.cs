using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.Services;

public sealed class SimulationGeneratorController(
    SimulationGenerationService generator,
    ICurrentOperatorContext currentOperator,
    IEegPhysicalChannelMappingService mappings,
    StimulusCapabilityProfile capability,
    MainViewModel main
) : IDisposable, IAsyncDisposable
{
    private bool _disposed;
    private SimulationGeneratorWindow? _window;
    private Window? _host;

    public static bool IsOpenGesture(Key key, KeyModifiers modifiers) =>
        key == Key.G && modifiers == (KeyModifiers.Control | KeyModifiers.Shift);

    public void Attach(Window host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host is not null)
        {
            _host.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _host.Closed -= OnHostClosed;
        }
        _host = host;
        host.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        host.Closed += OnHostClosed;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_disposed || !IsOpenGesture(e.Key, e.KeyModifiers) || !currentOperator.IsAuthenticated)
            return;
        e.Handled = true;
        if (_window is null)
        {
            var vm = new SimulationGeneratorViewModel(generator, mappings, capability);
            vm.Generated += OnGenerated;
            _window = new SimulationGeneratorWindow { DataContext = vm };
            _window.Closed += (_, _) =>
            {
                vm.Generated -= OnGenerated;
                _window = null;
            };
            _window.Show(_host!);
        }
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void OnGenerated(object? sender, EventArgs e)
    {
        if (main.CurrentView is BasicViewModel { CurrentPage: StartExperimentPageViewModel page })
            await page.RefreshHistoryCommand.ExecuteAsync(null);
    }

    private void OnHostClosed(object? sender, EventArgs e) => Dispose();

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
        if (_host is not null)
        {
            _host.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _host.Closed -= OnHostClosed;
            _host = null;
        }
        var window = _window;
        if (window?.DataContext is SimulationGeneratorViewModel vm)
        {
            vm.Generated -= OnGenerated;
            vm.Cancel();
        }
        window?.Close();
        _window = null;
    }
}
