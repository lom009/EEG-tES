using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

// Renders the local home visuals; all navigation and capability checks stay in IndexViewModel.
public sealed class HomeMotionView : UserControl
{
    private NativeWebView? _web;
    private HttpListener? _server;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _prefix = "";
    private string? _lastState;
    private bool _ready, _sending;
    public string? LastError { get; private set; }
    public bool IsReady => _ready;
    public string? PreviewUrl => _server is null ? null : _prefix + "index.html";
    public event EventHandler? Failed;

    public HomeMotionView()
    {
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
        _timer.Tick += async (_, _) => await SendState();
    }

    private void Start()
    {
        if (_web is not null) return;
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _prefix = $"http://127.0.0.1:{port}/{Guid.NewGuid():N}/";
            _server = new HttpListener();
            _server.Prefixes.Add($"http://127.0.0.1:{port}/");
            _server.Start();
            _ = Serve(_server, _prefix);
            _web = new NativeWebView();
            _web.WebMessageReceived += (_, e) => Receive(e.Body);
            Content = _web;
            _web.Source = new Uri(_prefix + "index.html");
            _timer.Start();
            _ = CheckStartup(_web);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task CheckStartup(NativeWebView instance)
    {
        await Task.Delay(30000);
        if (ReferenceEquals(_web, instance) && !_ready) Fail("首页视图加载超时");
    }

    private static async Task Serve(HttpListener server, string prefix)
    {
        while (server.IsListening)
        {
            HttpListenerContext context;
            try { context = await server.GetContextAsync(); }
            catch (Exception) when (!server.IsListening) { break; }
            _ = Respond(context, prefix);
        }
    }

    private static async Task Respond(HttpListenerContext context, string prefix)
    {
        try
        {
            var url = context.Request.Url?.AbsoluteUri ?? "";
            var name = url.StartsWith(prefix, StringComparison.Ordinal) ? url[prefix.Length..] : "";
            if (context.Request.HttpMethod is not ("GET" or "HEAD") || name.Contains('/') || name.Contains(".."))
            { context.Response.StatusCode = 404; return; }
            // Share the original native static background with the interactive card view.
            using var stream = name == "home-background.svg"
                ? AssetLoader.Open(new Uri("avares://EGGtCSPlatform/Assets/Images/index-background.svg"))
                : typeof(HomeMotionView).Assembly.GetManifestResourceStream("EGGtCSPlatform.WebAssets.Home." + name);
            if (stream is null) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = Path.GetExtension(name) switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript",
                ".css" => "text/css", ".svg" => "image/svg+xml",
                ".png" => "image/png", _ => "application/octet-stream"
            };
            context.Response.Headers["Accept-Ranges"] = "bytes";
            long start = 0, end = stream.Length - 1;
            var range = context.Request.Headers["Range"];
            if (range is not null)
            {
                var parts = range.StartsWith("bytes=", StringComparison.Ordinal) ? range[6..].Split('-') : [];
                if (parts.Length != 2 || !long.TryParse(parts[0], out start) ||
                    (parts[1].Length > 0 && !long.TryParse(parts[1], out end)) ||
                    start < 0 || start >= stream.Length || end < start)
                {
                    context.Response.StatusCode = 416;
                    context.Response.Headers["Content-Range"] = $"bytes */{stream.Length}";
                    return;
                }
                end = Math.Min(end, stream.Length - 1);
                context.Response.StatusCode = 206;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{stream.Length}";
            }
            var remaining = end - start + 1;
            context.Response.ContentLength64 = remaining;
            if (context.Request.HttpMethod == "HEAD") return;
            stream.Position = start;
            var buffer = new byte[65536];
            while (remaining > 0)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)));
                if (count == 0) break;
                await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, count));
                remaining -= count;
            }
        }
        catch (Exception) { /* Navigation can cancel an in-flight media request. */ }
        finally { context.Response.Close(); }
    }

    private void Receive(string? body)
    {
        try
        {
            using var json = JsonDocument.Parse(body ?? "{}");
            var message = json.RootElement;
            var type = message.GetProperty("type").GetString();
            if (type == "ready") { _ready = true; _lastState = null; return; }
            if (type != "action" || DataContext is not IndexViewModel page) return;
            ICommand? command = message.GetProperty("action").GetString() switch
            {
                "new" => page.StartExperimentCommand, "history" => page.ShowExperimentRecordsCommand,
                "device" => page.OpenDeviceConnectionCommand, "tolerance" => page.StartToleranceTestCommand,
                _ => null
            };
            if (command?.CanExecute(null) == true) command.Execute(null);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private async Task SendState()
    {
        if (!_ready || _sending || _web is null || DataContext is not IndexViewModel page) return;
        var state = JsonSerializer.Serialize(new { cards = new object[] {
            new { id = "new", enabled = page.CanStartExperiment, subtitle = page.StartExperimentSubtitle, reason = page.StartExperimentUnavailableReason },
            new { id = "history", enabled = true, subtitle = "查看历史实验记录" },
            new { id = "device", enabled = true, subtitle = page.DeviceConnection.ConnectionStatusText, connected = page.DeviceConnection.IsConnected },
            new { id = "tolerance", enabled = page.CanStartToleranceTest, subtitle = page.ToleranceSubtitle, reason = page.ToleranceUnavailableReason }
        } });
        if (state == _lastState) return;
        _sending = true;
        try { await _web.InvokeScript("window.updateHome(" + state + ")"); _lastState = state; }
        catch (Exception ex) { LastError = ex.Message; }
        finally { _sending = false; }
    }

    private void Fail(string error)
    {
        LastError = error;
        Stop();
        IsVisible = false;
        Failed?.Invoke(this, EventArgs.Empty);
    }

    private void Stop()
    {
        _timer.Stop();
        _server?.Close(); _server = null;
        Content = null; _web = null;
        _ready = false; _sending = false; _lastState = null;
    }
}
