using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.Controls;

internal interface IHead3DResponse
{
    int StatusCode { set; }
    string ContentType { set; }
    long ContentLength64 { set; }
    Stream OutputStream { get; }
    void Close();
    void Abort();
}

internal sealed record Head3DRequest(string Url, string Method, IHead3DResponse Response);

internal interface IHead3DListener
{
    Task<Head3DRequest> AcceptAsync();
    void Close();
}

// Owns one listener lifetime. Completion always observes transport and cleanup failures.
internal sealed class Head3DResourceServer
{
    private readonly IHead3DListener _listener;
    private readonly string _prefix;
    private readonly Func<string, Stream?> _openResource;
    private readonly Func<TimeSpan> _modelLoadDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IApplicationLogger _logger;
    private readonly Action<Head3DResourceServer, Exception> _failed;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    private bool _stopped;

    internal Task Completion { get; }

    internal Head3DResourceServer(
        IHead3DListener listener, string prefix, Func<string, Stream?> openResource,
        Func<TimeSpan> modelLoadDelay, IApplicationLogger logger,
        Action<Head3DResourceServer, Exception> failed,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _listener = listener;
        _prefix = prefix;
        _openResource = openResource;
        _modelLoadDelay = modelLoadDelay;
        _logger = logger;
        _failed = failed;
        _delay = delay ?? Task.Delay;
        _token = _cancellation.Token;
        Completion = Task.Run(RunAsync);
    }

    internal static Head3DResourceServer Start(int port, string prefix,
        Func<TimeSpan> modelLoadDelay, Action<Head3DResourceServer, Exception> failed)
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return new Head3DResourceServer(new Listener(listener), prefix,
                name => typeof(Head3DResourceServer).Assembly.GetManifestResourceStream(
                    "EGGtCSPlatform.WebAssets.Head3D." + name),
                modelLoadDelay, ApplicationLog.Current, failed);
        }
        catch
        {
            try { listener.Close(); } catch { /* Preserve the startup failure. */ }
            throw;
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            try { _cancellation.Cancel(); }
            catch (Exception e) { Report("cancel", _prefix, e); }
            try { _listener.Close(); }
            catch (Exception e) { Report("listener-close", _prefix, e); }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_token.IsCancellationRequested)
            {
                var request = await _listener.AcceptAsync().ConfigureAwait(false);
                await RespondAsync(request).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (IsShutdownException(e)) { }
        catch (Exception e)
        {
            Report("accept", _prefix, e);
            try { _failed(this, e); }
            catch (Exception notificationError) { Report("notify", _prefix, notificationError); }
        }
        finally
        {
            Stop();
            lock (_gate) _cancellation.Dispose();
        }
    }

    private async Task RespondAsync(Head3DRequest request)
    {
        var complete = false;
        var stage = "prepare";
        try
        {
            _token.ThrowIfCancellationRequested();
            var name = request.Url.StartsWith(_prefix, StringComparison.Ordinal)
                ? request.Url[_prefix.Length..] : "";
            if (request.Method != "GET" || name is not ("index.html" or "scene.js" or "style.css" or "eeg-head.glb"))
            {
                request.Response.StatusCode = 404;
                request.Response.ContentLength64 = 0;
                complete = true;
                return;
            }
            var delay = _modelLoadDelay();
            if (name == "eeg-head.glb" && delay > TimeSpan.Zero)
                await _delay(delay, _token).ConfigureAwait(false);
            using (var stream = _openResource(name))
            {
                if (stream is null)
                {
                    request.Response.StatusCode = 404;
                    request.Response.ContentLength64 = 0;
                }
                else
                {
                    request.Response.ContentType = Path.GetExtension(name) switch
                    {
                        ".html" => "text/html; charset=utf-8",
                        ".js" => "text/javascript",
                        ".css" => "text/css",
                        _ => "model/gltf-binary"
                    };
                    request.Response.ContentLength64 = stream.Length;
                    stage = "write";
                    await stream.CopyToAsync(request.Response.OutputStream, _token).ConfigureAwait(false);
                }
            }
            complete = true;
        }
        catch (Exception e) when (IsShutdownException(e)) { }
        catch (Exception e) { Report(stage, request.Url, e); }
        finally
        {
            if (complete)
            {
                try { request.Response.Close(); }
                catch (Exception e)
                {
                    Report("response-close", request.Url, e);
                    Abort(request);
                }
            }
            else Abort(request);
        }
    }

    private void Abort(Head3DRequest request)
    {
        try { request.Response.Abort(); }
        catch (Exception e) { Report("response-abort", request.Url, e); }
    }

    private bool IsShutdownException(Exception e) => _token.IsCancellationRequested
        && e is OperationCanceledException or ObjectDisposedException or HttpListenerException or IOException;

    private void Report(string stage, string resource, Exception exception)
    {
        if (IsShutdownException(exception)) return;
        try
        {
            _logger.Write(ApplicationLogLevel.Warning, nameof(Head3DResourceServer),
                "HEAD3D_RESOURCE_ERROR", $"stage={stage}; resource={resource}", exception: exception);
        }
        catch { /* Diagnostics must not fault the service or its cleanup. */ }
    }

    private sealed class Listener(HttpListener listener) : IHead3DListener
    {
        public async Task<Head3DRequest> AcceptAsync()
        {
            var context = await listener.GetContextAsync().ConfigureAwait(false);
            return new(context.Request.Url?.AbsoluteUri ?? "", context.Request.HttpMethod,
                new Response(context.Response));
        }
        public void Close() => listener.Close();
    }

    private sealed class Response(HttpListenerResponse response) : IHead3DResponse
    {
        public int StatusCode { set => response.StatusCode = value; }
        public string ContentType { set => response.ContentType = value; }
        public long ContentLength64 { set => response.ContentLength64 = value; }
        public Stream OutputStream => response.OutputStream;
        public void Close() => response.Close();
        public void Abort() => response.Abort();
    }
}
