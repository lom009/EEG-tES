using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Services;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Persistence;

public interface ITemporaryEegCleanupService
{
    Task CleanupAsync(CancellationToken cancellationToken = default);
}

public interface IManagedEegMigrationService
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
}

public sealed class ManagedEegMigrationService(
    IDbContextFactory<AppDbContext> contextFactory,
    IEegRawPacketReader rawReader,
    IEegFileRegistry fileRegistry
) : IManagedEegMigrationService
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.RecordingsDirectory);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var terminalRunIds = await db
            .ExperimentRuns.AsNoTracking()
            .Where(x =>
                x.Status != ExperimentRunStatus.Preparing && x.Status != ExperimentRunStatus.Running
            )
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var registeredRawFiles = await db
            .EegFiles.AsNoTracking()
            .Where(x =>
                x.Format == EegFileFormat.Staging
                && x.Location == EegFileLocation.Managed
                && x.State == EegFileState.Available
            )
            .Select(x => new { x.ExperimentRunId, x.Path })
            .ToListAsync(cancellationToken);
        var runsWithExistingRegisteredRawFiles = registeredRawFiles
            .Where(x => File.Exists(x.Path))
            .Select(x => x.ExperimentRunId)
            .ToHashSet();

        foreach (var runId in terminalRunIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (runsWithExistingRegisteredRawFiles.Contains(runId))
                continue;
            var canonical = Path.Combine(AppPaths.RecordingsDirectory, $"{runId:N}.eegraw");
            var source = FindCandidate(runId, canonical);
            if (source is null)
                continue;
            try
            {
                var summary = await rawReader.GetSummaryFromFileAsync(
                    runId,
                    source,
                    cancellationToken
                );
                if (summary is null || summary.RecordingId != runId)
                    continue;
                var destination = source.EndsWith(".eegraw.tmp", StringComparison.OrdinalIgnoreCase)
                    ? canonical + ".tmp"
                    : canonical;
                if (
                    !Path.GetFullPath(source)
                        .Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)
                )
                {
                    if (File.Exists(destination))
                        continue;
                    File.Move(source, destination);
                    var sourceIndex = GetIndexPath(source);
                    var targetIndex = GetIndexPath(destination);
                    if (File.Exists(sourceIndex) && !File.Exists(targetIndex))
                        File.Move(sourceIndex, targetIndex);
                }
                await foreach (
                    var _ in rawReader.ReadPacketsFromFileAsync(
                        runId,
                        destination,
                        0d,
                        0d,
                        cancellationToken
                    )
                )
                    break;
                await fileRegistry.RegisterRawAsync(runId, destination, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                // A single stale or corrupt legacy file must not block application startup.
            }
        }
    }

    private static string? FindCandidate(Guid runId, string canonical)
    {
        var stem = runId.ToString("N");
        var candidates = new[]
        {
            canonical,
            Path.Combine(AppPaths.RecordingsDirectory, $"{stem}.eegraw.tmp"),
            Path.Combine(AppPaths.TemporaryEegDirectory, $"{stem}.eegraw"),
            Path.Combine(AppPaths.TemporaryEegDirectory, $"{stem}.eegraw.tmp"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetIndexPath(string rawPath) =>
        rawPath.EndsWith(".eegraw.tmp", StringComparison.OrdinalIgnoreCase)
            ? rawPath[..^4] + ".idx.tmp"
            : rawPath + ".idx";
}

public sealed class TemporaryEegCleanupService(IDbContextFactory<AppDbContext> contextFactory)
    : ITemporaryEegCleanupService
{
    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.TemporaryEegDirectory);
        var root =
            Path.GetFullPath(AppPaths.TemporaryEegDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                File.Delete(fullPath);
            }
            catch (IOException)
            {
                // 正在由当前进程收尾的文件留待下次启动清理。
            }
            catch (UnauthorizedAccessException)
            {
                // 单个文件失败不能阻止其余元数据同步。
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var temporaryFiles = await db
            .EegFiles.Where(x =>
                x.Location == EegFileLocation.Temporary && x.State != EegFileState.Cleaned
            )
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var file in temporaryFiles)
        {
            if (!File.Exists(file.Path))
            {
                file.State = EegFileState.Cleaned;
                file.CleanedAtUtc = now;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public interface IApplicationShutdownCoordinator
{
    Task ShutdownAsync(
        string source,
        Exception? exception = null,
        bool cleanupTemporaryFiles = true,
        CancellationToken cancellationToken = default
    );
}

public sealed class ApplicationShutdownCoordinator(
    IExperimentRunPersistenceCoordinator persistence,
    IExperimentRunService runService,
    ITemporaryEegCleanupService cleanup,
    IDbContextFactory<AppDbContext> contextFactory,
    ApplicationSessionState applicationSession
) : IApplicationShutdownCoordinator
{
    private readonly object _sync = new();
    private Task? _shutdownTask;

    public Task ShutdownAsync(
        string source,
        Exception? exception = null,
        bool cleanupTemporaryFiles = true,
        CancellationToken cancellationToken = default
    )
    {
        lock (_sync)
        {
            return _shutdownTask ??= ShutdownCoreAsync(
                source,
                exception,
                cleanupTemporaryFiles,
                cancellationToken
            );
        }
    }

    private async Task ShutdownCoreAsync(
        string source,
        Exception? exception,
        bool cleanupTemporaryFiles,
        CancellationToken outerCancellation
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var cancellationToken = timeout.Token;
        bool? deviceStopped = null;
        string? deviceStopError = null;
        var activeRunId = persistence.RunId;
        var failures = 0;
        using var operation = new LoggedOperation(
            nameof(ApplicationShutdownCoordinator),
            "Application.Cleanup",
            $"source={source}",
            applicationSession.Id.ToString(),
            outerCancellation
        );
        void ReportFailure(string step, Exception error)
        {
            failures++;
            ApplicationLog.Write(
                ApplicationLogLevel.Error,
                nameof(ApplicationShutdownCoordinator),
                "Application.CleanupStepFailed",
                $"step={step}; source={source}",
                activeRunId.ToString(),
                error
            );
        }

        if (persistence.IsActive)
        {
            try
            {
                await persistence.RequestInterruptionAsync(source, cancellationToken);
            }
            catch (Exception error)
            {
                ReportFailure("request-interruption", error);
                // 仍必须继续停止设备；未提交的状态由下次启动恢复。
            }
            try
            {
                await runService.StopCurrentOperationAsync(persistence.DeviceId, cancellationToken);
                deviceStopped = true;
            }
            catch (Exception stopException)
            {
                deviceStopped = false;
                deviceStopError = stopException.Message;
                ReportFailure("device-stop", stopException);
            }

            EegRecordingCompletionResult? recording = null;
            try
            {
                recording = await runService.CompleteRecordingAsync(
                    activeRunId,
                    exception is null
                        ? EegRecordingCompletionStatus.Canceled
                        : EegRecordingCompletionStatus.Failed,
                    cancellationToken
                );
            }
            catch (Exception recordingException)
            {
                ReportFailure("recording-finalization", recordingException);
                // 保留可恢复的原始文件；未完成的运行由下次启动恢复。
            }

            try
            {
                await persistence.FinishAsync(
                    exception is null
                        ? ExperimentRunStatus.InterruptedByExit
                        : ExperimentRunStatus.Failed,
                    exception is null
                        ? ExperimentIncidentKind.ApplicationExit
                        : ExperimentIncidentKind.UnhandledException,
                    exception,
                    source,
                    deviceStopped,
                    deviceStopError,
                    cancellationToken,
                    recording?.PacketStatistics,
                    dataEndExclusiveSeconds: recording?.DataEndExclusiveSeconds
                );
            }
            catch (Exception error)
            {
                ReportFailure("experiment-finalization", error);
                // 下次启动会按最后心跳恢复；退出路径不能被二次故障卡住。
            }
        }

        if (!persistence.IsActive)
        {
            try
            {
                await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                await db
                    .ApplicationSessions.Where(x =>
                        x.Id == applicationSession.Id && x.ClosedAtUtc == null
                    )
                    .ExecuteUpdateAsync(
                        setters =>
                            setters
                                .SetProperty(x => x.LastHeartbeatAtUtc, now)
                                .SetProperty(x => x.ClosedAtUtc, now),
                        cancellationToken
                    );
            }
            catch (Exception error)
            {
                ReportFailure("session-close", error);
                // Session 未关闭时，下次启动会执行崩溃恢复。
            }
        }

        if (cleanupTemporaryFiles)
        {
            try
            {
                await cleanup.CleanupAsync(cancellationToken);
            }
            catch (Exception error)
            {
                ReportFailure("temporary-files", error);
            }
        }
        operation.Complete(
            $"source={source}; failedSteps={failures}; deviceStopped={deviceStopped}",
            failures == 0 ? ApplicationLogLevel.Info : ApplicationLogLevel.Error
        );
    }
}

public interface IFileRevealService
{
    void Reveal(string path);
}

public sealed class WindowsFileRevealService : IFileRevealService
{
    public void Reveal(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var fullPath = Path.GetFullPath(path);
        var isFile = File.Exists(fullPath);
        if (!isFile && !Directory.Exists(fullPath))
            return;
        Process.Start(
            new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = isFile ? $"/select,\"{fullPath}\"" : $"\"{fullPath}\"",
                UseShellExecute = true,
            }
        );
    }
}
