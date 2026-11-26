using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class Head3DResourceServerTests
{
    private const string Prefix = "http://127.0.0.1:12345/session/";
    private static readonly byte[] Content = [1, 2, 3, 4];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("scene.js", "text/javascript")]
    [InlineData("style.css", "text/css")]
    [InlineData("eeg-head.glb", "model/gltf-binary")]
    public async Task CompleteResourceIsWrittenAndClosed(string name, string contentType)
    {
        await using var host = new Host();
        var response = host.Enqueue(name);
        await response.Finished.Task.WaitAsync(Timeout);
        Assert.Equal(Content, ((MemoryStream)response.OutputStream).ToArray());
        Assert.Equal(Content.Length, response.ContentLength64);
        Assert.Equal(contentType, response.ContentType);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(1, response.CloseCount);
        Assert.Equal(0, response.AbortCount);
        Assert.Empty(host.Logger.Entries);
    }

    [Theory]
    [InlineData("missing.txt", "GET", false)]
    [InlineData("index.html", "POST", false)]
    [InlineData("index.html", "GET", true)]
    [InlineData("../index.html", "GET", false)]
    [InlineData("http://elsewhere/index.html", "GET", false)]
    public async Task InvalidOrMissingResourcesReturnEmpty404(string name, string method, bool missing)
    {
        await using var host = new Host(openResource: _ => missing ? null : new MemoryStream(Content));
        var response = host.Enqueue(name, method);
        await response.Finished.Task.WaitAsync(Timeout);
        Assert.Equal(404, response.StatusCode);
        Assert.Equal(0, response.ContentLength64);
        Assert.Equal(0, response.OutputStream.Length);
        Assert.Equal(1, response.CloseCount);
        Assert.Equal(0, response.AbortCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWriteIsAbortedAndNextRequestStillWorks(bool abortFails)
    {
        await using var host = new Host();
        var writeError = new IOException("client disconnected");
        var abortError = new InvalidOperationException("abort failed");
        var output = new ControlledOutput(writeError);
        var response = host.Enqueue("eeg-head.glb", response: new Response(output)
        {
            AbortError = abortFails ? abortError : null
        });
        await response.Finished.Task.WaitAsync(Timeout);
        var next = host.Enqueue("index.html");
        await next.Finished.Task.WaitAsync(Timeout);
        Assert.Equal(1, output.Length);
        Assert.Equal(0, response.CloseCount);
        Assert.Equal(1, response.AbortCount);
        Assert.Equal(Content, ((MemoryStream)next.OutputStream).ToArray());
        Assert.Contains(host.Logger.Entries, e => ReferenceEquals(e.Exception, writeError)
            && e.Message.Contains("stage=write") && e.Message.Contains("eeg-head.glb"));
        if (abortFails)
            Assert.Contains(host.Logger.Entries, e => ReferenceEquals(e.Exception, abortError)
                && e.Message.Contains("stage=response-abort"));
        Assert.Empty(host.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseFailureIsContainedAndAbortIsAttempted(bool abortFails)
    {
        await using var host = new Host();
        var error = new InvalidOperationException("Cannot close stream until all bytes are written.");
        var response = host.Enqueue("index.html", response: new Response
        {
            CloseError = error,
            AbortError = abortFails ? new IOException("abort failed") : null
        });
        await response.Finished.Task.WaitAsync(Timeout);
        var next = host.Enqueue("style.css");
        await next.Finished.Task.WaitAsync(Timeout);
        Assert.Equal(1, response.CloseCount);
        Assert.Equal(1, response.AbortCount);
        Assert.Contains(host.Logger.Entries, e => ReferenceEquals(e.Exception, error)
            && e.Message.Contains("stage=response-close"));
        Assert.Empty(host.Failures);
    }

    [Fact]
    public async Task StopWhileAcceptingCompletesAndIsIdempotent()
    {
        await using var host = new Host();
        await host.Listener.Accepting.Task.WaitAsync(Timeout);
        host.Server.Stop();
        host.Server.Stop();
        await host.Server.Completion.WaitAsync(Timeout);
        host.Server.Stop();
        Assert.True(host.Server.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, host.Listener.CloseCount);
        Assert.Empty(host.Logger.Entries);
        Assert.Empty(host.Failures);
    }

    [Fact]
    public async Task StopDuringModelDelayAbortsWithoutOpeningResource()
    {
        var delaying = Signal();
        var opened = false;
        await using var host = new Host(openResource: _ =>
        {
            opened = true;
            return new MemoryStream(Content);
        }, delay: (_, token) =>
        {
            delaying.TrySetResult();
            return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        });
        var response = host.Enqueue("eeg-head.glb");
        await delaying.Task.WaitAsync(Timeout);
        host.Server.Stop();
        await host.Server.Completion.WaitAsync(Timeout);
        Assert.False(opened);
        Assert.Equal(0, response.CloseCount);
        Assert.Equal(1, response.AbortCount);
        Assert.Empty(host.Failures);
        Assert.Empty(host.Logger.Entries);
    }

    [Fact]
    public async Task StopDuringWriteCancelsCopyAndDisposesResource()
    {
        var resource = new TrackingResource();
        await using var host = new Host(openResource: _ => resource);
        var output = new ControlledOutput();
        var response = host.Enqueue("eeg-head.glb", response: new Response(output));
        await output.Writing.Task.WaitAsync(Timeout);
        host.Server.Stop();
        await host.Server.Completion.WaitAsync(Timeout);
        Assert.True(output.Token.IsCancellationRequested);
        Assert.True(resource.Disposed);
        Assert.Equal(1, response.AbortCount);
        Assert.Equal(0, response.CloseCount);
        Assert.Empty(host.Logger.Entries);
        Assert.Empty(host.Failures);
    }

    [Fact]
    public async Task OldServerStopDoesNotAffectNewServer()
    {
        await using var old = new Host();
        await old.Listener.Accepting.Task.WaitAsync(Timeout);
        old.Server.Stop();
        await old.Server.Completion.WaitAsync(Timeout);
        await using var current = new Host();
        old.Server.Stop();
        var response = current.Enqueue("index.html");
        await response.Finished.Task.WaitAsync(Timeout);
        Assert.Equal(Content, ((MemoryStream)response.OutputStream).ToArray());
        Assert.Equal(0, current.Listener.CloseCount);
        Assert.Empty(current.Failures);
    }

    [Fact]
    public async Task UnexpectedAcceptFailureIsReportedOnceAndCompletionDoesNotFault()
    {
        await using var host = new Host();
        var error = new InvalidOperationException("listener failed");
        await host.Listener.Accepting.Task.WaitAsync(Timeout);
        host.Listener.Fail(error);
        await host.Server.Completion.WaitAsync(Timeout);
        Assert.Same(error, Assert.Single(host.Failures));
        Assert.Contains(host.Logger.Entries, e => ReferenceEquals(e.Exception, error)
            && e.Message.Contains("stage=accept"));
        Assert.True(host.Server.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, host.Listener.CloseCount);
    }

    [Fact]
    public async Task DiagnosticAndNotificationFailuresCannotFaultCompletion()
    {
        await using var host = new Host(notificationThrows: true);
        host.Logger.Throw = true;
        await host.Listener.Accepting.Task.WaitAsync(Timeout);
        host.Listener.Fail(new InvalidOperationException("listener failed"));
        await host.Server.Completion.WaitAsync(Timeout);
        Assert.Single(host.Failures);
        Assert.True(host.Server.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ListenerCloseFailureDoesNotEscapeStop()
    {
        await using var host = new Host();
        host.Listener.CloseError = new InvalidOperationException("close failed");
        await host.Listener.Accepting.Task.WaitAsync(Timeout);
        host.Server.Stop();
        await host.Server.Completion.WaitAsync(Timeout);
        Assert.Contains(host.Logger.Entries, e => ReferenceEquals(e.Exception, host.Listener.CloseError));
        Assert.Empty(host.Failures);
    }

    private sealed class Host : IAsyncDisposable
    {
        internal readonly Listener Listener = new();
        internal readonly Logger Logger = new();
        internal readonly ConcurrentQueue<Exception> Failures = new();
        internal Head3DResourceServer Server { get; }

        internal Host(Func<string, Stream?>? openResource = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null, bool notificationThrows = false)
        {
            Server = new Head3DResourceServer(Listener, Prefix,
                openResource ?? (_ => new MemoryStream(Content)),
                () => delay is null ? TimeSpan.Zero : TimeSpan.FromSeconds(20), Logger,
                (_, error) =>
                {
                    Failures.Enqueue(error);
                    if (notificationThrows) throw new InvalidOperationException("notification failed");
                }, delay);
        }

        internal Response Enqueue(string name, string method = "GET", Response? response = null)
        {
            response ??= new Response();
            Listener.Requests.Writer.TryWrite(new Head3DRequest(
                name.StartsWith("http", StringComparison.Ordinal) ? name : Prefix + name, method, response));
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            Server.Stop();
            await Server.Completion.WaitAsync(Timeout);
            Assert.True(Server.Completion.IsCompletedSuccessfully);
        }
    }

    private sealed class Listener : IHead3DListener
    {
        internal readonly Channel<Head3DRequest> Requests = Channel.CreateUnbounded<Head3DRequest>();
        internal readonly TaskCompletionSource Accepting = Signal();
        internal int CloseCount;
        internal Exception? CloseError;
        private Exception? _failure;

        public async Task<Head3DRequest> AcceptAsync()
        {
            Accepting.TrySetResult();
            try { return await Requests.Reader.ReadAsync(); }
            catch (ChannelClosedException) { throw _failure ?? new ObjectDisposedException("listener"); }
        }

        internal void Fail(Exception exception)
        {
            _failure = exception;
            Requests.Writer.TryComplete();
        }

        public void Close()
        {
            Interlocked.Increment(ref CloseCount);
            Requests.Writer.TryComplete();
            if (CloseError is not null) throw CloseError;
        }
    }

    private sealed class Response(Stream? output = null) : IHead3DResponse
    {
        public int StatusCode { get; set; } = 200;
        public string ContentType { get; set; } = "";
        public long ContentLength64 { get; set; }
        public Stream OutputStream { get; } = output ?? new MemoryStream();
        internal readonly TaskCompletionSource Finished = Signal();
        internal Exception? CloseError;
        internal Exception? AbortError;
        internal int CloseCount;
        internal int AbortCount;
        public void Close()
        {
            CloseCount++;
            if (CloseError is not null) throw CloseError;
            Finished.TrySetResult();
        }
        public void Abort()
        {
            AbortCount++;
            Finished.TrySetResult();
            if (AbortError is not null) throw AbortError;
        }
    }

    private sealed class ControlledOutput(Exception? error = null) : Stream
    {
        private readonly MemoryStream _written = new();
        internal readonly TaskCompletionSource Writing = Signal();
        internal CancellationToken Token { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written.Length;
        public override long Position { get => _written.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            _written.WriteByte(buffer.Span[0]);
            Writing.TrySetResult();
            if (error is not null) throw error;
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class TrackingResource() : MemoryStream(Content)
    {
        internal bool Disposed;
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class Logger : IApplicationLogger
    {
        internal readonly ConcurrentQueue<ApplicationLogEntry> Entries = new();
        internal bool Throw;
        public void Log(ApplicationLogEntry entry)
        {
            if (Throw) throw new InvalidOperationException("logging failed");
            Entries.Enqueue(entry);
        }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
