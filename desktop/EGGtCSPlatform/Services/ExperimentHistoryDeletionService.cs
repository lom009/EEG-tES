using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Services;

public sealed record ExperimentHistoryDeletionProgress(
    int CompletedCount,
    int TotalCount,
    Guid RunId
);

public sealed record ExperimentHistoryDeletionSuccess(Guid RunId);

public sealed record ExperimentHistoryDeletionFailure(Guid RunId, string Message);

public sealed record ExperimentHistoryDeletionResult(
    IReadOnlyList<ExperimentHistoryDeletionSuccess> Successes,
    IReadOnlyList<ExperimentHistoryDeletionFailure> Failures
);

public interface IExperimentHistoryDeletionService
{
    Task<ExperimentHistoryDeletionResult> DeleteAsync(
        IReadOnlyList<Guid> runIds,
        IProgress<ExperimentHistoryDeletionProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}

public sealed class ExperimentHistoryDeletionService : IExperimentHistoryDeletionService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly string _recordingsDirectory;
    private readonly string _temporaryEegDirectory;
    private readonly IApplicationLogger? _logger;

    public ExperimentHistoryDeletionService(
        IDbContextFactory<AppDbContext> contextFactory,
        IApplicationLogger? logger = null
    )
        : this(contextFactory, AppPaths.RecordingsDirectory, AppPaths.TemporaryEegDirectory, logger)
    { }

    internal ExperimentHistoryDeletionService(
        IDbContextFactory<AppDbContext> contextFactory,
        string recordingsDirectory,
        string temporaryEegDirectory,
        IApplicationLogger? logger = null
    )
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _recordingsDirectory = Path.GetFullPath(recordingsDirectory);
        _temporaryEegDirectory = Path.GetFullPath(temporaryEegDirectory);
    }

    public async Task<ExperimentHistoryDeletionResult> DeleteAsync(
        IReadOnlyList<Guid> runIds,
        IProgress<ExperimentHistoryDeletionProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(runIds);
        using var operation = new LoggedOperation(
            nameof(ExperimentHistoryDeletionService),
            "History.Delete",
            $"count={runIds.Distinct().Count()}; runIds={string.Join(",", runIds.Distinct())}",
            cancellationToken: cancellationToken,
            logger: _logger
        );
        try
        {
            if (runIds.Count == 0)
                throw new InvalidOperationException("请至少选择一条运行记录。");

            var orderedRunIds = runIds.Distinct().ToArray();
            var successes = new List<ExperimentHistoryDeletionSuccess>(orderedRunIds.Length);
            var failures = new List<ExperimentHistoryDeletionFailure>();

            for (var index = 0; index < orderedRunIds.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runId = orderedRunIds[index];
                try
                {
                    await DeleteRunAsync(runId, cancellationToken);
                    successes.Add(new ExperimentHistoryDeletionSuccess(runId));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add(new ExperimentHistoryDeletionFailure(runId, exception.Message));
                }

                progress?.Report(
                    new ExperimentHistoryDeletionProgress(index + 1, orderedRunIds.Length, runId)
                );
            }

            operation.Complete(
                $"success={successes.Count}; failed={failures.Count}; failures="
                    + string.Join("; ", failures.Select(x => $"{x.RunId}: {x.Message}")),
                failures.Count == 0 ? ApplicationLogLevel.Info : ApplicationLogLevel.Error
            );
            return new ExperimentHistoryDeletionResult(successes, failures);
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    private async Task DeleteRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var run =
            await db
                .ExperimentRuns.Include(x => x.Files)
                .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken)
            ?? throw new InvalidOperationException("运行记录不存在或已被删除。");
        if (run.Status is ExperimentRunStatus.Preparing or ExperimentRunStatus.Running)
            throw new InvalidOperationException("实验尚未结束，不能删除运行记录。");

        DeleteApplicationManagedFiles(run.Files, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Export rows may reference the raw-file row through SourceFileId. Remove them first
        // so the self-referencing restrictive foreign key cannot block deletion.
        db.EegFiles.RemoveRange(run.Files.Where(x => x.SourceFileId is not null));
        db.EegFiles.RemoveRange(run.Files.Where(x => x.SourceFileId is null));
        await db.SaveChangesAsync(cancellationToken);

        var hasOtherRuns = await db
            .ExperimentRuns.AsNoTracking()
            .AnyAsync(x => x.ExperimentId == run.ExperimentId && x.Id != runId, cancellationToken);
        if (hasOtherRuns)
        {
            db.ExperimentRuns.Remove(run);
        }
        else
        {
            var experiment = await db.Experiments.SingleAsync(
                x => x.Id == run.ExperimentId,
                cancellationToken
            );
            db.Experiments.Remove(experiment);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void DeleteApplicationManagedFiles(
        IReadOnlyList<EegFileEntity> files,
        CancellationToken cancellationToken
    )
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? allowedRoot = file.Location switch
            {
                EegFileLocation.Managed => _recordingsDirectory,
                EegFileLocation.Temporary => _temporaryEegDirectory,
                _ => null,
            };
            if (allowedRoot is null || string.IsNullOrWhiteSpace(file.Path))
                continue;

            var fullPath = Path.GetFullPath(file.Path);
            if (!IsWithinDirectory(fullPath, allowedRoot))
                throw new IOException("关联文件路径超出应用管理目录，已拒绝删除。");
            paths.Add(fullPath);
            if (file.Format == EegFileFormat.Staging)
                paths.Add(fullPath + ".idx");
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal
            );
    }
}
