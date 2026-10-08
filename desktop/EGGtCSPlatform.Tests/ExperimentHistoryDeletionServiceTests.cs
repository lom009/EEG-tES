using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ExperimentHistoryDeletionServiceTests
{
    [Fact]
    public async Task DeleteRemovesManagedArtifactsAndDatabaseGraphButPreservesExports()
    {
        var root = CreateTestDirectory();
        var recordings = Directory.CreateDirectory(Path.Combine(root, "recordings")).FullName;
        var temporary = Directory.CreateDirectory(Path.Combine(root, "temporary")).FullName;
        var exports = Directory.CreateDirectory(Path.Combine(root, "exports")).FullName;
        try
        {
            var factory = CreateFactory(Path.Combine(root, "history-delete.db"));
            var seeded = await SeedAsync(factory, recordings, temporary, exports);
            var service = new ExperimentHistoryDeletionService(factory, recordings, temporary);

            var firstResult = await service.DeleteAsync([seeded.FirstRunId]);

            Assert.Equal(seeded.FirstRunId, Assert.Single(firstResult.Successes).RunId);
            Assert.Empty(firstResult.Failures);
            Assert.False(File.Exists(seeded.RawPath));
            Assert.False(File.Exists(seeded.RawPath + ".idx"));
            Assert.False(File.Exists(seeded.TemporaryPath));
            Assert.True(File.Exists(seeded.ExportedPath));
            await using (var assertionDb = factory.CreateDbContext())
            {
                Assert.True(
                    await assertionDb.Experiments.AnyAsync(x => x.Id == seeded.ExperimentId)
                );
                Assert.False(
                    await assertionDb.ExperimentRuns.AnyAsync(x => x.Id == seeded.FirstRunId)
                );
                Assert.True(
                    await assertionDb.ExperimentRuns.AnyAsync(x => x.Id == seeded.SecondRunId)
                );
                Assert.False(
                    await assertionDb.ExperimentEvents.AnyAsync(x =>
                        x.ExperimentRunId == seeded.FirstRunId
                    )
                );
                Assert.False(
                    await assertionDb.ExperimentIncidents.AnyAsync(x =>
                        x.ExperimentRunId == seeded.FirstRunId
                    )
                );
                Assert.False(
                    await assertionDb.EegFiles.AnyAsync(x => x.ExperimentRunId == seeded.FirstRunId)
                );
            }

            var secondResult = await service.DeleteAsync([seeded.SecondRunId]);

            Assert.Single(secondResult.Successes);
            await using (var assertionDb = factory.CreateDbContext())
            {
                Assert.False(
                    await assertionDb.Experiments.AnyAsync(x => x.Id == seeded.ExperimentId)
                );
                Assert.False(
                    await assertionDb.StimulusParadigms.AnyAsync(x =>
                        x.ExperimentId == seeded.ExperimentId
                    )
                );
                Assert.True(await assertionDb.Subjects.AnyAsync());
                Assert.True(await assertionDb.Operators.AnyAsync());
                Assert.True(await assertionDb.ApplicationSessions.AnyAsync());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteContinuesAfterRejectedActiveAndUnsafeRecordsAndDeduplicatesIds()
    {
        var root = CreateTestDirectory();
        var recordings = Directory.CreateDirectory(Path.Combine(root, "recordings")).FullName;
        var temporary = Directory.CreateDirectory(Path.Combine(root, "temporary")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        try
        {
            var factory = CreateFactory(Path.Combine(root, "history-delete-failures.db"));
            var ids = await SeedFailureCasesAsync(factory, outside);
            var logger = new RecordingApplicationLogger();
            var service = new ExperimentHistoryDeletionService(
                factory,
                recordings,
                temporary,
                logger
            );
            var progress = new List<ExperimentHistoryDeletionProgress>();
            var missingRunId = Guid.NewGuid();

            var result = await service.DeleteAsync(
                [
                    ids.CompletedRunId,
                    ids.ActiveRunId,
                    ids.UnsafeRunId,
                    missingRunId,
                    ids.CompletedRunId,
                ],
                new InlineProgress<ExperimentHistoryDeletionProgress>(progress.Add)
            );

            Assert.Equal(ids.CompletedRunId, Assert.Single(result.Successes).RunId);
            Assert.Equal(3, result.Failures.Count);
            Assert.Equal(2, logger.Entries.Length);
            Assert.Equal("History.Delete.Completed", logger.Entries[1].EventName);
            Assert.Equal(ApplicationLogLevel.Error, logger.Entries[1].Level);
            Assert.Contains("success=1; failed=3", logger.Entries[1].Message);
            Assert.Contains(
                result.Failures,
                x => x.RunId == ids.ActiveRunId && x.Message.Contains("尚未结束")
            );
            Assert.Contains(
                result.Failures,
                x => x.RunId == ids.UnsafeRunId && x.Message.Contains("超出应用管理目录")
            );
            Assert.Contains(
                result.Failures,
                x => x.RunId == missingRunId && x.Message.Contains("不存在")
            );
            Assert.Equal(4, progress.Count);
            Assert.Equal(4, progress[^1].TotalCount);
            await using var assertionDb = factory.CreateDbContext();
            Assert.False(
                await assertionDb.ExperimentRuns.AnyAsync(x => x.Id == ids.CompletedRunId)
            );
            Assert.True(await assertionDb.ExperimentRuns.AnyAsync(x => x.Id == ids.ActiveRunId));
            Assert.True(await assertionDb.ExperimentRuns.AnyAsync(x => x.Id == ids.UnsafeRunId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SeededHistory> SeedAsync(
        TestContextFactory factory,
        string recordings,
        string temporary,
        string exports
    )
    {
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var graph = CreateBaseGraph("delete-success");
        var firstRun = CreateRun(graph.Experiment, graph.Session, ExperimentRunStatus.Completed);
        var secondRun = CreateRun(graph.Experiment, graph.Session, ExperimentRunStatus.Completed);
        var rawPath = Path.Combine(recordings, firstRun.Id.ToString("N") + ".eegraw");
        var temporaryPath = Path.Combine(temporary, firstRun.Id.ToString("N") + ".csv");
        var exportedPath = Path.Combine(exports, firstRun.Id.ToString("N") + ".csv");
        await File.WriteAllTextAsync(rawPath, "raw");
        await File.WriteAllTextAsync(rawPath + ".idx", "index");
        await File.WriteAllTextAsync(temporaryPath, "temporary");
        await File.WriteAllTextAsync(exportedPath, "exported");
        var rawFile = new EegFileEntity
        {
            ExperimentRun = firstRun,
            Format = EegFileFormat.Staging,
            Location = EegFileLocation.Managed,
            State = EegFileState.Available,
            Path = rawPath,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        firstRun.Files.Add(rawFile);
        firstRun.Files.Add(
            new EegFileEntity
            {
                ExperimentRun = firstRun,
                Format = EegFileFormat.Csv,
                Location = EegFileLocation.Temporary,
                State = EegFileState.Available,
                Path = temporaryPath,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        firstRun.Files.Add(
            new EegFileEntity
            {
                ExperimentRun = firstRun,
                Format = EegFileFormat.Csv,
                Location = EegFileLocation.Exported,
                State = EegFileState.Available,
                Path = exportedPath,
                SourceFile = rawFile,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        firstRun.Events.Add(
            new ExperimentEventEntity
            {
                Kind = ExperimentEventKind.Acquisition,
                Sequence = 1,
                RequestedAtUtc = DateTimeOffset.UtcNow,
                Status = ExperimentEventStatus.Completed,
            }
        );
        firstRun.Incidents.Add(
            new ExperimentIncidentEntity
            {
                Kind = ExperimentIncidentKind.FileFailure,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Source = "test",
                Message = "test",
            }
        );
        graph.Experiment.Runs.Add(firstRun);
        graph.Experiment.Runs.Add(secondRun);
        db.Experiments.Add(graph.Experiment);
        db.ApplicationSessions.Add(graph.Session);
        await db.SaveChangesAsync();
        return new SeededHistory(
            graph.Experiment.Id,
            firstRun.Id,
            secondRun.Id,
            rawPath,
            temporaryPath,
            exportedPath
        );
    }

    private static async Task<FailureCaseIds> SeedFailureCasesAsync(
        TestContextFactory factory,
        string outside
    )
    {
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var graph = CreateBaseGraph("delete-failures");
        var completed = CreateRun(graph.Experiment, graph.Session, ExperimentRunStatus.Completed);
        var active = CreateRun(graph.Experiment, graph.Session, ExperimentRunStatus.Running);
        var unsafeRun = CreateRun(graph.Experiment, graph.Session, ExperimentRunStatus.Completed);
        unsafeRun.Files.Add(
            new EegFileEntity
            {
                ExperimentRun = unsafeRun,
                Format = EegFileFormat.Staging,
                Location = EegFileLocation.Managed,
                State = EegFileState.Available,
                Path = Path.Combine(outside, "unsafe.eegraw"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        graph.Experiment.Runs.AddRange([completed, active, unsafeRun]);
        db.Experiments.Add(graph.Experiment);
        db.ApplicationSessions.Add(graph.Session);
        await db.SaveChangesAsync();
        return new FailureCaseIds(completed.Id, active.Id, unsafeRun.Id);
    }

    private static (ExperimentEntity Experiment, ApplicationSessionEntity Session) CreateBaseGraph(
        string suffix
    )
    {
        var now = DateTimeOffset.UtcNow;
        var experiment = new ExperimentEntity
        {
            ExperimentCode = $"EXP-{suffix}-{Guid.NewGuid():N}"[..32],
            ScheduledAt = now,
            Remarks = string.Empty,
            Status = ExperimentStatus.Completed,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Operator = new OperatorEntity
            {
                Username = suffix,
                NormalizedUsername = suffix.ToUpperInvariant(),
                PasswordSalt = [1],
                PasswordHash = [1],
                PasswordIterations = 1,
                CreatedAtUtc = now,
            },
            Subject = new SubjectEntity
            {
                SubjectCode = suffix,
                NormalizedSubjectCode = suffix.ToUpperInvariant(),
                CreatedAtUtc = now,
            },
            StimulusParadigm = new StimulusParadigmEntity
            {
                Kind = "TDcs",
                ArrayMode = "DualChannel",
                Direction = "Positive",
                ShamMode = "Direct",
            },
        };
        var session = new ApplicationSessionEntity
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = now,
            LastHeartbeatAtUtc = now,
            ClosedAtUtc = now,
        };
        return (experiment, session);
    }

    private static ExperimentRunEntity CreateRun(
        ExperimentEntity experiment,
        ApplicationSessionEntity session,
        ExperimentRunStatus status
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            Experiment = experiment,
            ApplicationSession = session,
            Status = status,
            RunMode = "Manual",
            SampleRateHz = 500,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = DateTimeOffset.UtcNow,
            EndedAtUtc = status == ExperimentRunStatus.Running ? null : DateTimeOffset.UtcNow,
            LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
        };

    private static TestContextFactory CreateFactory(string databasePath) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False;Foreign Keys=True")
                .Options
        );

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "eggtcs-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record SeededHistory(
        long ExperimentId,
        Guid FirstRunId,
        Guid SecondRunId,
        string RawPath,
        string TemporaryPath,
        string ExportedPath
    );

    private sealed record FailureCaseIds(Guid CompletedRunId, Guid ActiveRunId, Guid UnsafeRunId);
}
