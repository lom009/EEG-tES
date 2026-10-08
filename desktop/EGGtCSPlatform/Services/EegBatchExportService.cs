using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;

namespace EGGtCSPlatform.Services;

public sealed record EegBatchExportProgress(int CompletedCount, int TotalCount, Guid RunId);

// Suppress per-record events only inside a batch, independently of Explorer reveal preferences.
internal sealed class BatchExportLogScope : IDisposable
{
    private static readonly AsyncLocal<int> Depth = new();
    public static bool IsActive => Depth.Value > 0;

    public BatchExportLogScope() => Depth.Value++;

    public void Dispose() => Depth.Value--;
}

public sealed record EegBatchExportSuccess(Guid RunId, EegExportResult Result);

public sealed record EegBatchExportFailure(Guid RunId, string Message);

public sealed record EegBatchExportResult(
    string DirectoryPath,
    IReadOnlyList<EegBatchExportSuccess> Successes,
    IReadOnlyList<EegBatchExportFailure> Failures
)
{
    public int FileCount => Successes.Sum(x => x.Result.Paths.Count);
}

public interface IEegBatchExportService
{
    Task<EegBatchExportResult> ExportAsync(
        IReadOnlyList<Guid> runIds,
        EegExportSelection selection,
        string targetParentDirectory,
        IProgress<EegBatchExportProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}

public sealed class EegBatchExportService(
    IEegExportService exports,
    IFileRevealService fileReveal,
    IApplicationLogger? logger = null
) : IEegBatchExportService
{
    private const string BatchDirectoryPrefix = "实验记录批量导出";

    public async Task<EegBatchExportResult> ExportAsync(
        IReadOnlyList<Guid> runIds,
        EegExportSelection selection,
        string targetParentDirectory,
        IProgress<EegBatchExportProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(runIds);
        using var operation = new LoggedOperation(
            nameof(EegBatchExportService),
            "Export.Batch",
            $"count={runIds.Distinct().Count()}; runIds={string.Join(",", runIds.Distinct())}",
            cancellationToken: cancellationToken,
            logger: logger
        );
        using var batchScope = new BatchExportLogScope();
        try
        {
            if (runIds.Count == 0)
                throw new InvalidOperationException("请至少选择一条运行记录。");
            if (!selection.Edf && !selection.Csv && !selection.ExperimentPackage)
                throw new InvalidOperationException("请至少选择一种导出格式。");
            if (string.IsNullOrWhiteSpace(targetParentDirectory))
                throw new ArgumentException("请选择导出目录。", nameof(targetParentDirectory));

            var orderedRunIds = runIds.Distinct().ToArray();
            var directory = CreateBatchDirectory(targetParentDirectory, DateTimeOffset.Now);
            var successes = new List<EegBatchExportSuccess>(orderedRunIds.Length);
            var failures = new List<EegBatchExportFailure>();

            for (var index = 0; index < orderedRunIds.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runId = orderedRunIds[index];
                try
                {
                    var result = await exports.ExportAsync(
                        runId,
                        selection,
                        directory,
                        cancellationToken,
                        revealResult: false
                    );
                    successes.Add(new EegBatchExportSuccess(runId, result));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add(new EegBatchExportFailure(runId, exception.Message));
                }

                progress?.Report(
                    new EegBatchExportProgress(index + 1, orderedRunIds.Length, runId)
                );
            }

            if (successes.Count > 0)
            {
                try
                {
                    fileReveal.Reveal(directory);
                }
                catch { }
            }

            operation.Complete(
                $"success={successes.Count}; failed={failures.Count}; failures="
                    + string.Join("; ", failures.Select(x => $"{x.RunId}: {x.Message}")),
                failures.Count == 0 ? ApplicationLogLevel.Info : ApplicationLogLevel.Error
            );
            return new EegBatchExportResult(directory, successes, failures);
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    internal static string CreateBatchDirectory(string parentDirectory, DateTimeOffset now)
    {
        Directory.CreateDirectory(parentDirectory);
        var fullParentDirectory = Path.GetFullPath(parentDirectory);
        var baseName = $"{BatchDirectoryPrefix}_{now:yyyyMMdd_HHmmss}";
        for (var index = 0; index <= 9999; index++)
        {
            var name = index == 0 ? baseName : $"{baseName}_{index:000}";
            var candidate = Path.Combine(fullParentDirectory, name);
            if (Directory.Exists(candidate))
                continue;
            Directory.CreateDirectory(candidate);
            return candidate;
        }

        throw new IOException("目标目录中同名批量导出文件夹过多。");
    }
}
