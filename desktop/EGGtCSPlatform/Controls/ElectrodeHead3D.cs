using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

// The native page owns all assignments. JavaScript only renders snapshots and requests selection.
public sealed class ElectrodeHead3D : UserControl
{
    private NativeWebView? _web;
    private Head3DResourceServer? _server;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private string? _lastSnapshot;
    private bool _ready, _sending;
    private bool _presented;
    private Grid? _surface;
    private StackPanel? _loading;
    private string _prefix = "";
    private CancellationTokenSource? _startupCancellation;
    // Test-host timing controls; production uses immediate serving and a 30-second deadline.
    internal TimeSpan ModelLoadDelay { get; set; }
    internal TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public ElectrodeConfigurationPageViewModel? Page { get; set; }
    public string? LastError { get; private set; }
    public bool IsSceneReady => _ready;
    public bool IsScenePresented => _presented;
    private bool IsModalBlocked => NativeContentModalState.GetIsBlocked(this);

    public ElectrodeHead3D()
    {
        _timer.Tick += async (_, _) => await SendState();
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != NativeContentModalState.IsBlockedProperty) return;

        // Native HWNDs render above Avalonia's adorner layer. Keep the browser attached
        // (and its camera/model alive), but hide its native window until the stack is empty.
        if (_web is not null) _web.IsVisible = !IsModalBlocked;
        _lastSnapshot = null;
        if (!IsModalBlocked) _ = SendState();
    }

    private void Start()
    {
        if (_web is not null) return;
        LastError = null;
        _loading = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new RunLoadingSpinner { Width = 32, Height = 32, SpinnerBrush = Brush.Parse("#4148C5"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                new TextBlock { Text = "正在加载 3D 头模…", Foreground = Brush.Parse("#4148C5") }
            }
        };
        _surface = new Grid();
        _surface.Children.Add(_loading);
        Content = _surface;
        if (Page is { CanShowHead3D: false } invalidPage)
        {
            ShowError(invalidPage.Head3DConfigurationError);
            return;
        }
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            _prefix = $"http://127.0.0.1:{port}/{Guid.NewGuid():N}/";
            _server = Head3DResourceServer.Start(port, _prefix, () => ModelLoadDelay,
                (server, error) => Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_server, server)) ShowError(error.Message);
                }));
            _web = new NativeWebView { Background = Brushes.Transparent, IsVisible = !IsModalBlocked };
            var instance = _web;
            _web.WebMessageReceived += (_, e) =>
            {
                if (ReferenceEquals(_web, instance)) Receive(e.Body);
            };
            _surface.Children.Add(_web);
            _web.Source = new Uri(_prefix + "index.html");
            _timer.Start();
            _startupCancellation = new CancellationTokenSource();
            _ = CheckStartup(instance, _startupCancellation.Token);
        }
        catch (Exception e) { ShowError(e.Message); }
    }

    private async Task CheckStartup(NativeWebView instance, CancellationToken cancellation)
    {
        try { await Task.Delay(StartupTimeout, cancellation); }
        catch (OperationCanceledException) { return; }
        if (ReferenceEquals(_web, instance) && !_presented)
            ShowError("3D 头模加载超时。");
    }

    private void Receive(string? body)
    {
        try
        {
            using var json = JsonDocument.Parse(body ?? "{}");
            var message = json.RootElement;
            if (!message.TryGetProperty("type", out var typeValue)) return;
            var type = typeValue.GetString();
            if (type == "ready") { _ready = true; _lastSnapshot = null; _ = SendState(); return; }
            if (type == "scenePresented" && _ready)
            {
                _presented = true;
                _startupCancellation?.Cancel();
                if (_loading is not null) _surface?.Children.Remove(_loading);
                _loading = null;
                return;
            }
            if (type == "error") { ShowError(message.TryGetProperty("message", out var error) ? error.GetString() ?? "模型加载失败" : "模型加载失败"); return; }
            if (type != "select" || !_presented || IsModalBlocked || Page is not { CanEditConfiguration: true } page) return;
            if (!message.TryGetProperty("id", out var idValue)) return;
            var id = idValue.GetString();
            var point = page.Points.FirstOrDefault(p => string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase));
            if (point is { IsAvailable: true } && page.SelectPointCommand.CanExecute(point))
                page.SelectPointCommand.Execute(point);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private async Task SendState()
    {
        if (!_ready || !IsVisible || IsModalBlocked || _sending || _web is null || Page is not { } page) return;
        if (!page.CanShowHead3D) { ShowError(page.Head3DConfigurationError); return; }
        var checkingIds = page.IsCurrentDetecting
            ? page.Points.Where(p => page.IsStimulusMode
                ? p.IsStimulus && p.StimulationChannelRole == StimulationChannelRole.Selectable
                : p.Role == ElectrodeRole.Acquisition)
                .Select(p => p.Name).ToArray()
            : Array.Empty<string>();
        var snapshot = JsonSerializer.Serialize(new { stimulationMode = page.IsStimulusMode, checkingIds, points = page.Points.Select(p => new
        {
            id = p.Name, label = p.UiDisplayName, focused = !p.IsDimmed && p.IsSelected, assigned = !p.IsDimmed && (p.IsStimulus || p.IsAcquisition),
            dimmed = p.IsDimmed, shared = p.IsShared, showSharedDivider = p.ShowSharedDivider, acquisitionLabel = p.AcquisitionLabel, tooltip = p.UiToolTipText,
            stimulationColor = p.StimulationColor, acquisitionColor = p.AcquisitionColor,
            stimulationForeground = p.StimulationForeground, acquisitionForeground = p.AcquisitionForeground,
            foreground = ElectrodeSiteViewModel.ForegroundFor(p.IsDimmed ? ImpedanceQuality.Unknown : p.DisplayQuality),
            position = new[] { p.Position3D!.X, p.Position3D.Y, p.Position3D.Z },
            normal = p.Normal3D is { } normal ? new[] { normal.X, normal.Y, normal.Z } : null,
            available = p.IsAvailable && page.CanEditConfiguration,
            color = p.IsDimmed ? "#F8FBFF" : p.IsStimulus || p.IsAcquisition ? ElectrodeSiteViewModel.ColorFor(p.DisplayQuality) : "#F8FBFF"
        }) });
        if (snapshot == _lastSnapshot) return;
        var instance = _web;
        _sending = true;
        try { await instance.InvokeScript($"window.applyHostState?.({snapshot})"); if (ReferenceEquals(_web, instance)) _lastSnapshot = snapshot; }
        catch (Exception e) { if (ReferenceEquals(_web, instance)) LastError = e.Message; }
        finally { if (ReferenceEquals(_web, instance)) _sending = false; }
    }

    public async void ResetView()
    {
        if (_web is null || !_presented || IsModalBlocked) return;
        try { await _web.InvokeScript("window.resetHeadView?.()"); } catch (Exception e) { LastError = e.Message; }
    }
    private void ShowError(string message)
    {
        Stop();
        LastError = message;
        Content = new TextBlock { Text = "3D 头模加载失败，可切回 2D 继续配置。\n" + message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    }
    private void Stop()
    {
        if (_web is not null) _web.IsVisible = false;
        _web = null; // Reject queued browser callbacks before releasing its visual host.
        _startupCancellation?.Cancel();
        _startupCancellation?.Dispose();
        _startupCancellation = null;
        var server = _server;
        _server = null; // Ignore fault notifications queued by the outgoing server.
        server?.Stop();
        _timer.Stop();
        _sending = false;
        _ready = false; _lastSnapshot = null; Content = null; _web = null;
        _presented = false; _loading = null; _surface = null;
    }
    public void Exit() => Stop();
}
