using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.Crash;

public sealed class GlobalExceptionHandler(
    IEnumerable<IGlobalExceptionLogger> loggers,
    IApplicationShutdownCoordinator shutdownCoordinator
) : IDisposable
{
    private readonly IReadOnlyList<IGlobalExceptionLogger> _loggers = loggers.ToArray();
    private int _handlingException;
    private bool _attached;

    public void Attach()
    {
        if (_attached)
            return;

        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        _attached = true;
    }

    public void Dispose()
    {
        if (!_attached)
            return;

        Dispatcher.UIThread.UnhandledException -= OnUiUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        _attached = false;
    }

    private void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = HandleOnceAsync(e.Exception, "UI 线程", isTerminating: true);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        _ = HandleOnceAsync(e.Exception, "未观察的后台任务", isTerminating: true);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception)
            return;

        var handling = HandleOnceAsync(exception, "进程未处理异常", e.IsTerminating);
        if (!Dispatcher.UIThread.CheckAccess())
            handling.GetAwaiter().GetResult();
    }

    private async Task HandleOnceAsync(Exception exception, string source, bool isTerminating)
    {
        if (Interlocked.CompareExchange(ref _handlingException, 1, 0) != 0)
            return;

        var context = new GlobalExceptionContext(source, DateTimeOffset.Now, isTerminating);
        try
        {
            await shutdownCoordinator
                .ShutdownAsync(source, exception, cleanupTemporaryFiles: true)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch
        {
            // 收口失败仍继续写崩溃日志；下次启动会按最后心跳恢复。
        }
        await WriteLogsAsync(exception, context).ConfigureAwait(false);
        await ShowFatalErrorAsync(exception, context).ConfigureAwait(false);
    }

    private async Task WriteLogsAsync(Exception exception, GlobalExceptionContext context)
    {
        foreach (var logger in _loggers)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await logger
                    .LogAsync(exception, context, timeout.Token)
                    .AsTask()
                    .WaitAsync(timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 日志记录失败不能阻止错误提示和安全退出。
            }
        }
    }

    private static Task ShowFatalErrorAsync(Exception exception, GlobalExceptionContext context)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    var window = new ErrorWindow
                    {
                        DataContext = new ErrorViewModel
                        {
                            Title = "程序发生未处理异常",
                            Description = CreateDescription(exception, context),
                            ConfirmText = "确认并退出",
                        },
                    };
                    window.Closed += (_, _) =>
                    {
                        completion.TrySetResult();
                        ShutdownApplication();
                    };
                    window.Show();
                    window.Activate();
                }
                catch
                {
                    completion.TrySetResult();
                    ShutdownApplication();
                }
            },
            DispatcherPriority.Send
        );
        return completion.Task;
    }

    private static string CreateDescription(Exception exception, GlobalExceptionContext context) =>
        $"异常来源：{context.Source}\r\n"
        + $"发生时间：{context.OccurredAt:yyyy-MM-dd HH:mm:ss}\r\n\r\n"
        + $"{exception.GetType().Name}: {exception.Message}\r\n\r\n"
        + $"{exception.StackTrace ?? "无堆栈信息"}\r\n\r\n"
        + "请确认此提示，程序随后将退出。";

    private static void ShutdownApplication()
    {
        if (Application.Current is App app && app.RequestFatalExit())
            return;
        if (
            Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
        )
        {
            desktop.Shutdown(1);
            return;
        }

        Environment.Exit(1);
    }
}
