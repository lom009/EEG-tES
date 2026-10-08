using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using EGGtCSPlatform.Persistence;
using Velopack.Locators;

namespace EGGtCSPlatform.Services;

public interface IApplicationRestartService
{
    Task RestartAsync(CancellationToken cancellationToken = default);
}

public static class ApplicationRestartProtocol
{
    public const string ParentProcessArgumentPrefix = "--eggtcs-restart-after-pid=";
    public static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(30);

    public static bool TryTakeParentProcessId(
        IReadOnlyList<string> arguments,
        out int parentProcessId,
        out string[] remainingArguments
    )
    {
        parentProcessId = 0;
        var remaining = new List<string>(arguments.Count);
        var found = false;
        foreach (var argument in arguments)
        {
            if (
                !found
                && argument.StartsWith(ParentProcessArgumentPrefix, StringComparison.Ordinal)
                && int.TryParse(argument[ParentProcessArgumentPrefix.Length..], out var parsed)
                && parsed > 0
            )
            {
                parentProcessId = parsed;
                found = true;
                continue;
            }
            remaining.Add(argument);
        }

        remainingArguments = remaining.ToArray();
        return found;
    }

    public static bool WaitForParentExit(int parentProcessId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(parentProcessId);
            return process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public static string CreateParentProcessArgument(int parentProcessId) =>
        $"{ParentProcessArgumentPrefix}{parentProcessId}";
}

internal interface IApplicationExecutableResolver
{
    string Resolve();
}

internal interface IApplicationProcessLauncher
{
    void Start(string executablePath, IReadOnlyList<string> arguments);
}

internal interface IApplicationLifetimeExit
{
    void Exit();
}

internal sealed class CurrentApplicationExecutableResolver : IApplicationExecutableResolver
{
    public string Resolve()
    {
        if (OperatingSystem.IsLinux())
        {
            if (
                VelopackLocator.IsCurrentSet
                && VelopackLocator.Current is LinuxVelopackLocator linuxLocator
                && !string.IsNullOrWhiteSpace(linuxLocator.AppImagePath)
                && File.Exists(linuxLocator.AppImagePath)
            )
            {
                return linuxLocator.AppImagePath;
            }

            var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(appImagePath) && File.Exists(appImagePath))
                return Path.GetFullPath(appImagePath);
        }

        return Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序的可执行文件路径。");
    }
}

internal sealed class ApplicationProcessLauncher : IApplicationProcessLauncher
{
    public void Start(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        _ =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("操作系统未能启动新的程序进程。");
    }
}

internal sealed class AvaloniaApplicationLifetimeExit : IApplicationLifetimeExit
{
    public void Exit()
    {
        if (
            Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
        )
        {
            throw new InvalidOperationException("当前应用生命周期不支持程序重启。");
        }
        if (Application.Current is App app)
            app.RequestExit();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.TryShutdown());
    }
}

public sealed class ApplicationRestartService : IApplicationRestartService
{
    private readonly IApplicationShutdownCoordinator _shutdownCoordinator;
    private readonly IApplicationExecutableResolver _executableResolver;
    private readonly IApplicationProcessLauncher _processLauncher;
    private readonly IApplicationLifetimeExit _lifetimeExit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _restartStarted;

    public ApplicationRestartService(IApplicationShutdownCoordinator shutdownCoordinator)
        : this(
            shutdownCoordinator,
            new CurrentApplicationExecutableResolver(),
            new ApplicationProcessLauncher(),
            new AvaloniaApplicationLifetimeExit()
        ) { }

    internal ApplicationRestartService(
        IApplicationShutdownCoordinator shutdownCoordinator,
        IApplicationExecutableResolver executableResolver,
        IApplicationProcessLauncher processLauncher,
        IApplicationLifetimeExit lifetimeExit
    )
    {
        _shutdownCoordinator = shutdownCoordinator;
        _executableResolver = executableResolver;
        _processLauncher = processLauncher;
        _lifetimeExit = lifetimeExit;
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_restartStarted)
                return;

            await _shutdownCoordinator
                .ShutdownAsync("factory-reset", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var executablePath = _executableResolver.Resolve();
            _processLauncher.Start(
                executablePath,
                [ApplicationRestartProtocol.CreateParentProcessArgument(Environment.ProcessId)]
            );
            _restartStarted = true;
            _lifetimeExit.Exit();
        }
        finally
        {
            _gate.Release();
        }
    }
}
