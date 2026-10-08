using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EGGtCSPlatform.Services;

public enum ApplicationLogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}

public sealed record ApplicationLogEntry(
    DateTimeOffset Timestamp,
    ApplicationLogLevel Level,
    string Source,
    string EventName,
    string Message,
    string? CorrelationId = null,
    Exception? Exception = null
);

public interface IApplicationLogger
{
    void Log(ApplicationLogEntry entry);
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public static class ApplicationLoggerExtensions
{
    public static void Write(
        this IApplicationLogger logger,
        ApplicationLogLevel level,
        string source,
        string eventName,
        string message,
        string? correlationId = null,
        Exception? exception = null
    ) =>
        logger.Log(
            new(DateTimeOffset.Now, level, source, eventName, message, correlationId, exception)
        );
}

public sealed class NullApplicationLogger : IApplicationLogger
{
    public static NullApplicationLogger Instance { get; } = new();

    public void Log(ApplicationLogEntry entry) { }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

// The desktop owns the logger, not DI: disposal diagnostics must still work.
public static class ApplicationLog
{
    private static IApplicationLogger _current = NullApplicationLogger.Instance;
    private static readonly object ExitGate = new();
    private static Task? _stopTask;
    public static IApplicationLogger Current => Volatile.Read(ref _current);

    public static void SetLogger(IApplicationLogger logger)
    {
        lock (ExitGate)
        {
            Volatile.Write(ref _current, logger);
            _stopTask = null;
        }
    }

    public static void Write(
        ApplicationLogLevel level,
        string source,
        string eventName,
        string message,
        string? correlationId = null,
        Exception? exception = null
    ) => Current.Write(level, source, eventName, message, correlationId, exception);

    public static Task StopAsync()
    {
        lock (ExitGate)
            return _stopTask ??= StopCoreAsync(Current);
    }

    private static async Task StopCoreAsync(IApplicationLogger logger)
    {
        logger.Write(
            ApplicationLogLevel.Info,
            "Application",
            "Application.Exit",
            "退出收尾已结束，正在关闭日志"
        );
        try
        {
            if (logger is IAsyncDisposable disposable)
                await disposable
                    .DisposeAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            else
                await logger.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Log shutdown failed: {exception}");
        }
    }

    public static async Task FlushBeforeExitAsync()
    {
        try
        {
            await Current.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Log flush failed: {exception}");
        }
    }
}

// Record one start and one outcome; callers can add a failure's exception or success summary.
internal sealed class LoggedOperation : IDisposable
{
    private readonly IApplicationLogger _logger;
    private readonly string _source,
        _event,
        _id;
    private readonly CancellationToken _cancellation;
    private bool _finished;

    public LoggedOperation(
        string source,
        string eventName,
        string message,
        string? correlationId = null,
        CancellationToken cancellationToken = default,
        IApplicationLogger? logger = null
    )
    {
        _logger = logger ?? ApplicationLog.Current;
        _source = source;
        _event = eventName;
        _id = correlationId ?? Guid.NewGuid().ToString("N");
        _cancellation = cancellationToken;
        _logger.Write(ApplicationLogLevel.Info, _source, _event + ".Started", message, _id);
    }

    public void Complete(string message, ApplicationLogLevel level = ApplicationLogLevel.Info)
    {
        if (_finished)
            return;
        _finished = true;
        _logger.Write(level, _source, _event + ".Completed", message, _id);
    }

    public void Fail(Exception exception)
    {
        if (_finished)
            return;
        _finished = true;
        var canceled = exception is OperationCanceledException;
        _logger.Write(
            canceled ? ApplicationLogLevel.Info : ApplicationLogLevel.Error,
            _source,
            _event + (canceled ? ".Canceled" : ".Failed"),
            canceled ? "操作已取消" : "操作失败",
            _id,
            canceled ? null : exception
        );
    }

    public void Dispose()
    {
        if (!_finished)
            _logger.Write(
                _cancellation.IsCancellationRequested
                    ? ApplicationLogLevel.Info
                    : ApplicationLogLevel.Error,
                _source,
                _event + (_cancellation.IsCancellationRequested ? ".Canceled" : ".Failed"),
                _cancellation.IsCancellationRequested ? "操作已取消" : "操作未能完成",
                _id
            );
    }
}
