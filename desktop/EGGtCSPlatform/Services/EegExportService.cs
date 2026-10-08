using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Services;

public sealed record EegExportSelection(bool Edf, bool Csv, bool ExperimentPackage);

public sealed record EegExportResult(IReadOnlyList<string> Paths, string RevealedPath);

public interface IEegExportService
{
    Task<EegExportResult> ExportAsync(
        Guid runId,
        EegExportSelection selection,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default,
        bool revealResult = true
    );
}

public sealed class EegExportService(
    IDbContextFactory<AppDbContext> contextFactory,
    IExperimentPersistenceService experiments,
    IExperimentPackageSerializer packages,
    IFileRevealService fileReveal,
    IEegArtifactFinalizer artifactFinalizer,
    IEegRawPacketReader rawReader,
    IApplicationLogger? logger = null
) : IEegExportService
{
    public async Task<EegExportResult> ExportAsync(
        Guid runId,
        EegExportSelection selection,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default,
        bool revealResult = true
    )
    {
        // Batch calls suppress per-run logging and produce one summary at their own boundary.
        using var operation = BatchExportLogScope.IsActive
            ? null
            : new LoggedOperation(
                nameof(EegExportService),
                "Export.Single",
                "导出实验记录",
                runId.ToString(),
                cancellationToken,
                logger
            );
        try
        {
            if (!selection.Edf && !selection.Csv && !selection.ExperimentPackage)
                throw new InvalidOperationException("请至少选择一种导出格式。");
            targetDirectory ??= AppPaths.ExportDirectory;
            Directory.CreateDirectory(targetDirectory);
            var destinationRoot = Path.GetFullPath(targetDirectory);

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var run = await db
                .ExperimentRuns.AsNoTracking()
                .Include(x => x.Experiment)
                    .ThenInclude(x => x.StimulusParadigm)
                        .ThenInclude(x => x!.Targets)
                .SingleAsync(x => x.Id == runId, cancellationToken);
            if (
                selection.ExperimentPackage
                && run.Experiment.CreationMode
                    != ViewModels.Pages.ExperimentCreationMode.AcquisitionOnly
                && (
                    run.Experiment.StimulusParadigm?.Kind == "EnvelopeTAcs"
                    || run.Experiment.StimulusParadigm?.EnvelopeJson is not null
                )
            )
                throw new InvalidOperationException(
                    "包络-tACS 不能导出为设备配置，请仅选择 EEG / CSV 数据格式。"
                );
            if (
                run.Experiment.CreationMode == ViewModels.Pages.ExperimentCreationMode.StimulusOnly
                && (selection.Edf || selection.Csv)
            )
                throw new InvalidOperationException(
                    "单刺激实验没有脑电采集数据，请仅选择实验配置格式导出。"
                );
            // SQLite cannot translate ORDER BY for DateTimeOffset. Keep the selective
            // predicates in SQL, then choose the newest managed recording in memory.
            var rawFiles = await db
                .EegFiles.AsNoTracking()
                .Where(x =>
                    x.ExperimentRunId == runId
                    && x.Format == EegFileFormat.Staging
                    && x.Location == EegFileLocation.Managed
                    && x.State == EegFileState.Available
                )
                .ToListAsync(cancellationToken);
            var rawFile = rawFiles.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            EegRecordingSummary? summary = null;
            if (selection.Edf || selection.Csv)
            {
                if (rawFile is null || !File.Exists(rawFile.Path))
                    throw new FileNotFoundException(
                        "原始数据文件不存在，无法导出。",
                        rawFile?.Path
                    );
                summary =
                    await rawReader.GetSummaryFromFileAsync(runId, rawFile!.Path, cancellationToken)
                    ?? throw new FileNotFoundException(
                        "原始数据文件不存在，无法导出。",
                        rawFile.Path
                    );
            }

            var exportTime = DateTimeOffset.Now;
            var totalCurrent =
                run.Experiment.StimulusParadigm?.Targets.Sum(x => x.PeakCurrentMilliAmps) ?? 0d;
            var baseStem = SanitizeFileName(
                $"{run.Experiment.ScheduledAt.ToLocalTime():yyyyMMdd}_{exportTime:yyyyMMdd_HHmmss}_"
                    + $"{run.Experiment.ExperimentCode}_{run.Experiment.StimulusParadigm?.Kind ?? "Unknown"}_"
                    + $"{totalCurrent.ToString("0.###", CultureInfo.InvariantCulture)}mA"
            );
            var extensions = new List<string>();
            if (selection.Edf || selection.Csv)
            {
                extensions.Add(".edf");
                extensions.Add(".csv");
            }
            if (selection.ExperimentPackage)
                extensions.Add(".expp");
            var stem = ReserveGroupStem(destinationRoot, baseStem, extensions);
            var created = new List<(string Path, EegFileFormat Format, long? SourceId)>();
            var attemptedPaths = new List<string>();

            try
            {
                if (selection.Edf || selection.Csv)
                {
                    var outputStem = Path.Combine(destinationRoot, stem);
                    attemptedPaths.Add(outputStem + ".edf");
                    attemptedPaths.Add(outputStem + ".csv");
                    var artifacts = await artifactFinalizer.FinalizeAsync(
                        summary!,
                        token =>
                            rawReader.ReadSamplesFromFileAsync(
                                runId,
                                rawFile!.Path,
                                cancellationToken: token
                            ),
                        cancellationToken,
                        outputStem
                    );
                    if (selection.Edf)
                        created.Add((artifacts.EdfPath, EegFileFormat.EdfPlus, rawFile!.Id));
                    else
                        File.Delete(artifacts.EdfPath);
                    if (selection.Csv)
                        created.Add((artifacts.CsvPath, EegFileFormat.Csv, rawFile!.Id));
                    else
                        File.Delete(artifacts.CsvPath);
                }
                if (selection.ExperimentPackage)
                {
                    var destination = Path.Combine(destinationRoot, stem + ".expp");
                    attemptedPaths.Add(destination);
                    var template = run.ConfigurationJson is { } configurationJson
                        ? JsonSerializer.Deserialize<MainApp.ExperimentConfigurationTemplate>(
                            configurationJson
                        ) ?? throw new InvalidDataException("运行配置快照无效。")
                        : await experiments.LoadTemplateAsync(run.ExperimentId, cancellationToken);
                    template = template with
                    {
                        Timing = new MainApp.ExperimentTimingTemplate(
                            Enum.Parse<ViewModels.Pages.ExperimentRunMode>(run.RunMode),
                            run.AcquisitionDurationMilliseconds,
                            run.BlankingDurationMilliseconds,
                            run.StimulationDurationMilliseconds,
                            run.RecoveryDurationMilliseconds,
                            run.CycleCount
                        ),
                    };
                    await packages.WriteAsync(destination, template, cancellationToken);
                    created.Add((destination, EegFileFormat.ExperimentPackage, null));
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var file in created)
                {
                    db.EegFiles.Add(
                        new EegFileEntity
                        {
                            ExperimentRunId = runId,
                            Format = file.Format,
                            Location = EegFileLocation.Exported,
                            State = EegFileState.Available,
                            Path = file.Path,
                            SourceFileId = file.SourceId,
                            SizeBytes = new FileInfo(file.Path).Length,
                            CreatedAtUtc = now,
                        }
                    );
                }
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                foreach (var path in attemptedPaths)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch { }
                }
                throw;
            }

            var reveal =
                created.FirstOrDefault(x => x.Format == EegFileFormat.EdfPlus).Path
                ?? created.FirstOrDefault(x => x.Format == EegFileFormat.Csv).Path
                ?? created.First(x => x.Format == EegFileFormat.ExperimentPackage).Path;
            if (revealResult)
            {
                try
                {
                    fileReveal.Reveal(reveal);
                }
                catch { }
            }
            operation?.Complete($"files={created.Count}");
            return new EegExportResult(created.Select(x => x.Path).ToArray(), reveal);
        }
        catch (Exception exception)
        {
            operation?.Fail(exception);
            throw;
        }
    }

    private static string ReserveGroupStem(
        string directory,
        string baseStem,
        IReadOnlyList<string> extensions
    )
    {
        for (var index = 0; index <= 9999; index++)
        {
            var candidate = index == 0 ? baseStem : $"{baseStem}_{index:000}";
            if (
                extensions.All(extension =>
                    !File.Exists(Path.Combine(directory, candidate + extension))
                )
            )
                return candidate;
        }
        throw new IOException("目标目录中同名导出文件过多。");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.ToString();
    }
}
