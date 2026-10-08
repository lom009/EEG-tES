using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EegBatchExportServiceTests
{
    [Fact]
    public async Task ExportContinuesAfterFailureReportsProgressAndRevealsOnce()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            var first = Guid.NewGuid();
            var failed = Guid.NewGuid();
            var last = Guid.NewGuid();
            var exports = new StubExportService(failed);
            var reveal = new RecordingFileRevealService();
            var logger = new RecordingApplicationLogger();
            var service = new EegBatchExportService(exports, reveal, logger);
            var progress = new List<EegBatchExportProgress>();

            var result = await service.ExportAsync(
                [first, failed, last],
                new EegExportSelection(true, true, false),
                parent,
                new RecordingProgress<EegBatchExportProgress>(progress.Add)
            );

            Assert.Equal(2, result.Successes.Count);
            Assert.Equal(4, result.FileCount);
            Assert.Equal(failed, Assert.Single(result.Failures).RunId);
            Assert.Equal([first, failed, last], exports.RunIds);
            Assert.All(exports.RevealResults, Assert.False);
            Assert.Equal([1, 2, 3], progress.ConvertAll(x => x.CompletedCount));
            Assert.Equal(result.DirectoryPath, Assert.Single(reveal.Paths));
            Assert.True(Directory.Exists(result.DirectoryPath));
            Assert.StartsWith("实验记录批量导出_", Path.GetFileName(result.DirectoryPath));
            Assert.Equal(2, logger.Entries.Length);
            Assert.Equal("Export.Batch.Started", logger.Entries[0].EventName);
            Assert.Equal("Export.Batch.Completed", logger.Entries[1].EventName);
            Assert.Equal(ApplicationLogLevel.Error, logger.Entries[1].Level);
            Assert.Contains("success=2; failed=1", logger.Entries[1].Message);
            Assert.Contains(failed.ToString(), logger.Entries[1].Message);
            Assert.Equal(logger.Entries[0].CorrelationId, logger.Entries[1].CorrelationId);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void BatchDirectoryUsesSuffixWhenTimestampCollides()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 9, 4, 12, 34, 56, TimeSpan.Zero);

            var first = EegBatchExportService.CreateBatchDirectory(parent, now);
            var second = EegBatchExportService.CreateBatchDirectory(parent, now);

            Assert.Equal("实验记录批量导出_20260904_123456", Path.GetFileName(first));
            Assert.Equal("实验记录批量导出_20260904_123456_001", Path.GetFileName(second));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void FormatDialogDefaultsToEdfAndCsvAndRequiresAFormat()
    {
        var dialog = new BatchExportDialogViewModel(3);

        Assert.True(dialog.ExportEdf);
        Assert.True(dialog.ExportCsv);
        Assert.False(dialog.ExportExpp);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));

        dialog.ExportEdf = false;
        dialog.ExportCsv = false;

        Assert.False(dialog.HasSelectedFormat);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "eggtcs-batch-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StubExportService(Guid failedRunId) : IEegExportService
    {
        public List<Guid> RunIds { get; } = [];
        public List<bool> RevealResults { get; } = [];

        public Task<EegExportResult> ExportAsync(
            Guid runId,
            EegExportSelection selection,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default,
            bool revealResult = true
        )
        {
            RunIds.Add(runId);
            RevealResults.Add(revealResult);
            if (runId == failedRunId)
                throw new FileNotFoundException("原始数据文件不存在，无法导出。");
            return Task.FromResult(
                new EegExportResult([$"{runId:N}.edf", $"{runId:N}.csv"], $"{runId:N}.edf")
            );
        }
    }

    private sealed class RecordingFileRevealService : IFileRevealService
    {
        public List<string> Paths { get; } = [];

        public void Reveal(string path) => Paths.Add(path);
    }

    private sealed class RecordingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
