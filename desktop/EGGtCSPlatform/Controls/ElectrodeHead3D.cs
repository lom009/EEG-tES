using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

// The native page owns all assignments. JavaScript only renders snapshots and requests selection.
public sealed class ElectrodeHead3D : UserControl
{
    private NativeWebView? _web;
    private HttpListener? _server;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private string? _lastSnapshot;
    private bool _ready, _sending;
    private string _prefix = "";
    public ElectrodeConfigurationPageViewModel? Page { get; set; }
    public string? LastError { get; private set; }
    public bool IsSceneReady => _ready;

    public ElectrodeHead3D()
    {
        _timer.Tick += async (_, _) => await SendState();
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
    }

    private void Start()
    {
        if (_web is not null) return;
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            _prefix = $"http://127.0.0.1:{port}/{Guid.NewGuid():N}/";
            _server = new HttpListener();
            _server.Prefixes.Add($"http://127.0.0.1:{port}/"); _server.Start();
            _ = Serve(_server);
            _web = new NativeWebView { Background = Brushes.Transparent };
            _web.WebMessageReceived += (_, e) => Receive(e.Body);
            Content = _web;
            _web.Source = new Uri(_prefix + "index.html");
            _timer.Start();
        }
        catch (Exception e) { ShowError(e.Message); }
    }

    private async Task Serve(HttpListener server)
    {
        while (server.IsListening)
        {
            HttpListenerContext context;
            try { context = await server.GetContextAsync(); }
            catch (Exception) when (!server.IsListening) { break; }
            try
            {
                var url = context.Request.Url?.AbsoluteUri ?? "";
                var name = url.StartsWith(_prefix, StringComparison.Ordinal) ? url[_prefix.Length..] : "";
                if (context.Request.HttpMethod != "GET" || name is not ("index.html" or "scene.js" or "style.css" or "eeg-head-34.glb" or "points-34.json"))
                { context.Response.StatusCode = 404; continue; }
                using var stream = typeof(ElectrodeHead3D).Assembly.GetManifestResourceStream("EGGtCSPlatform.WebAssets.Head3D." + name);
                if (stream is null) { context.Response.StatusCode = 404; continue; }
                context.Response.ContentType = Path.GetExtension(name) switch
                { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript", ".css" => "text/css", ".json" => "application/json", _ => "model/gltf-binary" };
                context.Response.ContentLength64 = stream.Length;
                await stream.CopyToAsync(context.Response.OutputStream);
            }
            catch (Exception) { /* Closing the view cancels in-flight model requests. */ }
            finally { context.Response.Close(); }
        }
    }

    private void Receive(string? body)
    {
        try
        {
            using var json = JsonDocument.Parse(body ?? "{}");
            var message = json.RootElement;
            var type = message.GetProperty("type").GetString();
            if (type == "ready") { _ready = true; _lastSnapshot = null; return; }
            if (type == "error") { ShowError(message.GetProperty("message").GetString() ?? "模型加载失败"); return; }
            if (type != "select" || Page is not { CanEditConfiguration: true } page) return;
            var id = message.GetProperty("id").GetString();
            var point = page.Points.FirstOrDefault(p => p.PositionName == id);
            if (point is { IsAvailable: true } && page.SelectPointCommand.CanExecute(point))
                page.SelectPointCommand.Execute(point);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private async Task SendState()
    {
        if (!_ready || !IsVisible || _sending || _web is null || Page is not { } page) return;
        var checkingIds = page.IsCurrentDetecting
            ? page.Points.Where(p => page.IsStimulusMode
                ? p.IsStimulus && p.StimulationChannelRole == StimulationChannelRole.Selectable
                : p.Role == ElectrodeRole.Acquisition)
                .Select(p => p.PositionName).ToArray()
            : Array.Empty<string>();
        var snapshot = JsonSerializer.Serialize(new { stimulationMode = page.IsStimulusMode, checkingIds, points = page.Points.Select(p => new
        {
            id = p.PositionName, label = p.DisplayName, focused = p.IsSelected, assigned = p.IsStimulus || p.IsAcquisition,
            available = p.IsAvailable && page.CanEditConfiguration,
            color = p.Quality switch
            {
                ImpedanceQuality.Excellent => "#38A169", ImpedanceQuality.Good => "#2F86FF",
                ImpedanceQuality.Medium => "#F3A619", ImpedanceQuality.Poor => "#FD5B38",
                ImpedanceQuality.Bad => "#E91919", _ => p.IsStimulus || p.IsAcquisition ? "#2F86FF" : "#F8FBFF"
            }
        }) });
        if (snapshot == _lastSnapshot) return;
        _sending = true;
        try { await _web.InvokeScript($"window.applyHostState?.({snapshot})"); _lastSnapshot = snapshot; }
        catch (Exception e) { LastError = e.Message; }
        finally { _sending = false; }
    }

    public async void ResetView()
    {
        if (_web is null || !_ready) return;
        try { await _web.InvokeScript("window.resetHeadView?.()"); } catch (Exception e) { LastError = e.Message; }
    }
    private void ShowError(string message)
    {
        LastError = message; _ready = false;
        Content = new TextBlock { Text = "3D 头模加载失败，可切回 2D 继续配置。\n" + message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    }
    private void Stop()
    {
        _timer.Stop(); _server?.Close(); _server = null;
        _ready = false; _lastSnapshot = null; Content = null; _web = null;
    }
}
