using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class PersistenceAndArtifactTests
{
    [Fact]
    public void EegPacketStatisticsMigrationIsRegistered()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var db = CreateFactory(Path.Combine(directory, "migrations.db"))
                .CreateDbContext();
            Assert.Contains("20260828020000_AddEegPacketStatistics", db.Database.GetMigrations());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExperimentEventTimelineOffsetsMigrationIsRegistered()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var db = CreateFactory(Path.Combine(directory, "timeline-migrations.db"))
                .CreateDbContext();
            Assert.Contains(
                "20260831061621_AddExperimentEventTimelineOffsets",
                db.Database.GetMigrations()
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HistoricalTimelinePrefersLogicalOffsetsAndLeavesLegacyUtcFallbackUnchanged()
    {
        var runStarted = DateTimeOffset.UnixEpoch;
        var logical = new ExperimentEventEntity
        {
            RequestedAtUtc = runStarted.AddSeconds(40),
            StartedAtUtc = runStarted.AddSeconds(41),
            EndedAtUtc = runStarted.AddSeconds(56),
            TimelineStartMilliseconds = 30_000,
            TimelineEndMilliseconds = 45_000,
        };
        var legacy = new ExperimentEventEntity
        {
            RequestedAtUtc = runStarted.AddSeconds(10),
            StartedAtUtc = runStarted.AddSeconds(11),
            EndedAtUtc = runStarted.AddSeconds(26),
        };

        var logicalTimeline = ExperimentPersistenceService.ResolveHistoricalEventTimeline(
            logical,
            runStarted,
            runStarted.AddMinutes(1)
        );
        var legacyTimeline = ExperimentPersistenceService.ResolveHistoricalEventTimeline(
            legacy,
            runStarted,
            runStarted.AddMinutes(1)
        );

        Assert.Equal((30d, 45d), logicalTimeline);
        Assert.Equal((11d, 26d), legacyTimeline);
    }

    [Fact]
    public void HistoricalLogicalTimelineEndRequiresEveryEventToHaveLogicalOffsets()
    {
        var logicalEvents = new[]
        {
            new ExperimentEventEntity
            {
                TimelineStartMilliseconds = 0,
                TimelineEndMilliseconds = 30_000,
            },
            new ExperimentEventEntity
            {
                TimelineStartMilliseconds = 30_000,
                TimelineEndMilliseconds = 45_000,
            },
        };
        var mixedEvents = logicalEvents
            .Append(
                new ExperimentEventEntity
                {
                    TimelineStartMilliseconds = null,
                    TimelineEndMilliseconds = null,
                }
            )
            .ToArray();

        Assert.Equal(
            45d,
            ExperimentPersistenceService.ResolveHistoricalLogicalTimelineEnd(logicalEvents)
        );
        Assert.Null(ExperimentPersistenceService.ResolveHistoricalLogicalTimelineEnd(mixedEvents));
        Assert.Null(ExperimentPersistenceService.ResolveHistoricalLogicalTimelineEnd([]));
    }

    [Fact]
    public async Task HistoryOrderingWithDateTimeOffsetRunsOnClientForSqlite()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var factory = CreateFactory(Path.Combine(directory, "history.db"));
            var runId = Guid.NewGuid();
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                var session = new ApplicationSessionEntity
                {
                    Id = Guid.NewGuid(),
                    StartedAtUtc = now,
                    LastHeartbeatAtUtc = now,
                };
                db.ExperimentRuns.Add(
                    new ExperimentRunEntity
                    {
                        Id = runId,
                        Experiment = CreateExperiment(now),
                        ApplicationSession = session,
                        Status = ExperimentRunStatus.Completed,
                        RunMode = "Manual",
                        SampleRateHz = 500,
                        CreatedAtUtc = now,
                        LastHeartbeatAtUtc = now,
                        EndedAtUtc = now,
                    }
                );
                await db.SaveChangesAsync();
            }
            var mappings = new EegPhysicalChannelMappingService(
                Path.Combine(directory, "mapping.json")
            );
            var service = new ExperimentPersistenceService(
                factory,
                new CurrentOperatorContext(),
                mappings
            );

            var history = await service.ListHistoryAsync();

            Assert.Single(history);
            Assert.Equal("EXP-20260827-001", history[0].ExperimentCode);
            Assert.Single(history[0].Runs);
            Assert.Equal(runId, history[0].Runs[0].RunId);
            Assert.Equal(ExperimentRunStatus.Completed, history[0].Runs[0].Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptionRequestIsDurableBeforeFinalRunClosure()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var factory = CreateFactory(Path.Combine(directory, "interrupt.db"));
            var sessionId = Guid.NewGuid();
            long experimentId;
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                db.ApplicationSessions.Add(
                    new ApplicationSessionEntity
                    {
                        Id = sessionId,
                        StartedAtUtc = now,
                        LastHeartbeatAtUtc = now,
                    }
                );
                var experiment = CreateExperiment(now);
                db.Experiments.Add(experiment);
                await db.SaveChangesAsync();
                experimentId = experiment.Id;
            }
            var session = new ApplicationSessionState { Id = sessionId };
            await using var coordinator = new ExperimentRunPersistenceCoordinator(factory, session);
            var runId = Guid.NewGuid();
            await coordinator.PrepareAsync(
                new ExperimentRunPreparation(
                    runId,
                    experimentId,
                    "Manual",
                    1,
                    500,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1)
                ),
                "device"
            );
            await coordinator.RequestStageAsync(ExperimentRunStage.Acquisition, 1, TimeSpan.Zero);
            await coordinator.ObserveStageAsync(
                ExperimentRunStage.Acquisition,
                1,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(500)
            );

            await coordinator.RequestInterruptionAsync("人工急停");

            await using (var assertionDb = factory.CreateDbContext())
            {
                var requested = await assertionDb.ExperimentRuns.SingleAsync(x => x.Id == runId);
                Assert.Equal(ExperimentRunStatus.Running, requested.Status);
                Assert.NotNull(requested.InterruptRequestedAtUtc);
                Assert.Equal("人工急停", requested.InterruptRequestSource);
            }
            await coordinator.FinishAsync(
                ExperimentRunStatus.InterruptedByUser,
                ExperimentIncidentKind.UserEmergencyStop,
                source: "人工急停",
                packetStatistics: new EegPacketStatistics(true, 98, 2, 4, 1, 1),
                timelineEnd: TimeSpan.FromMilliseconds(750)
            );
            await using var finalDb = factory.CreateDbContext();
            var finalRun = await finalDb
                .ExperimentRuns.Include(x => x.Events)
                .SingleAsync(x => x.Id == runId);
            Assert.Equal(ExperimentRunStatus.InterruptedByUser, finalRun.Status);
            var interruptedEvent = Assert.Single(finalRun.Events);
            Assert.Equal(ExperimentEventStatus.Interrupted, interruptedEvent.Status);
            Assert.Equal(0d, interruptedEvent.TimelineStartMilliseconds);
            Assert.Equal(750d, interruptedEvent.TimelineEndMilliseconds);
            Assert.True(finalRun.EegPacketReorderingEnabled);
            Assert.Equal(98, finalRun.EegReceivedPacketCount);
            Assert.Equal(2, finalRun.EegLostPacketCount);
            Assert.Equal(4, finalRun.EegOutOfOrderPacketCount);
            Assert.Equal(1, finalRun.EegDuplicatePacketCount);
            Assert.Equal(1, finalRun.EegLateDiscardedPacketCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NewRunPersistsLogicalStageOffsetsIndependentOfUtcDelays()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var factory = CreateFactory(Path.Combine(directory, "logical-stage-time.db"));
            var sessionId = Guid.NewGuid();
            long experimentId;
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                db.ApplicationSessions.Add(
                    new ApplicationSessionEntity
                    {
                        Id = sessionId,
                        StartedAtUtc = now,
                        LastHeartbeatAtUtc = now,
                    }
                );
                var experiment = CreateExperiment(now);
                db.Experiments.Add(experiment);
                await db.SaveChangesAsync();
                experimentId = experiment.Id;
            }

            var session = new ApplicationSessionState { Id = sessionId };
            await using var coordinator = new ExperimentRunPersistenceCoordinator(factory, session);
            var runId = Guid.NewGuid();
            await coordinator.PrepareAsync(
                new ExperimentRunPreparation(
                    runId,
                    experimentId,
                    "Automatic",
                    1,
                    500,
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(1)
                ),
                "device"
            );

            await coordinator.RequestStageAsync(ExperimentRunStage.Acquisition, 1, TimeSpan.Zero);
            await coordinator.ObserveStageAsync(
                ExperimentRunStage.Acquisition,
                1,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2)
            );
            await Task.Delay(20);
            await coordinator.RequestStageAsync(
                ExperimentRunStage.Stimulation,
                1,
                TimeSpan.FromSeconds(16)
            );
            await coordinator.ObserveStageAsync(
                ExperimentRunStage.Stimulation,
                1,
                TimeSpan.FromSeconds(16),
                TimeSpan.FromSeconds(17)
            );
            await Task.Delay(20);
            await coordinator.CompleteCurrentStageAsync(TimeSpan.FromSeconds(31));
            await coordinator.FinishAsync(
                ExperimentRunStatus.Completed,
                timelineEnd: TimeSpan.FromSeconds(31)
            );

            await using var assertionDb = factory.CreateDbContext();
            var events = await assertionDb
                .ExperimentEvents.Where(x => x.ExperimentRunId == runId)
                .OrderBy(x => x.Sequence)
                .ToArrayAsync();
            Assert.Collection(
                events,
                acquisition =>
                {
                    Assert.Equal(0d, acquisition.TimelineStartMilliseconds);
                    Assert.Equal(16_000d, acquisition.TimelineEndMilliseconds);
                },
                stimulation =>
                {
                    Assert.Equal(16_000d, stimulation.TimelineStartMilliseconds);
                    Assert.Equal(31_000d, stimulation.TimelineEndMilliseconds);
                }
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RerunCreatesAnotherRunWithoutChangingPreviousRawFileRecord()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var factory = CreateFactory(Path.Combine(directory, "rerun.db"));
            var sessionId = Guid.NewGuid();
            var previousRunId = Guid.NewGuid();
            var nextRunId = Guid.NewGuid();
            const string previousRawPath = "C:\\recordings\\previous.eegraw";
            long experimentId;
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                var session = new ApplicationSessionEntity
                {
                    Id = sessionId,
                    StartedAtUtc = now,
                    LastHeartbeatAtUtc = now,
                };
                var experiment = CreateExperiment(now);
                db.ExperimentRuns.Add(
                    new ExperimentRunEntity
                    {
                        Id = previousRunId,
                        Experiment = experiment,
                        ApplicationSession = session,
                        Status = ExperimentRunStatus.Completed,
                        RunMode = "Automatic",
                        CycleCount = 1,
                        SampleRateHz = 500,
                        CreatedAtUtc = now.AddMinutes(-2),
                        StartedAtUtc = now.AddMinutes(-2),
                        EndedAtUtc = now.AddMinutes(-1),
                        LastHeartbeatAtUtc = now.AddMinutes(-1),
                        Files =
                        [
                            new EegFileEntity
                            {
                                Format = EegFileFormat.Staging,
                                Location = EegFileLocation.Managed,
                                State = EegFileState.Available,
                                Path = previousRawPath,
                                CreatedAtUtc = now.AddMinutes(-1),
                            },
                        ],
                    }
                );
                await db.SaveChangesAsync();
                experimentId = experiment.Id;
            }

            await using var coordinator = new ExperimentRunPersistenceCoordinator(
                factory,
                new ApplicationSessionState { Id = sessionId }
            );
            await coordinator.PrepareAsync(
                new ExperimentRunPreparation(
                    nextRunId,
                    experimentId,
                    "Automatic",
                    1,
                    500,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(1)
                ),
                "device-rerun"
            );

            await using var assertionDb = factory.CreateDbContext();
            var runs = await assertionDb
                .ExperimentRuns.Include(x => x.Files)
                .Where(x => x.ExperimentId == experimentId)
                .ToListAsync();
            Assert.Equal(2, runs.Count);
            Assert.Equal(
                previousRawPath,
                Assert.Single(runs.Single(x => x.Id == previousRunId).Files).Path
            );
            Assert.Empty(runs.Single(x => x.Id == nextRunId).Files);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CrashRecoveryUsesLastDurableHeartbeatAndClosesActiveEvent()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var factory = CreateFactory(Path.Combine(directory, "recovery.db"));
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                var heartbeat = DateTimeOffset.UtcNow.AddSeconds(-3);
                var session = new ApplicationSessionEntity
                {
                    Id = Guid.NewGuid(),
                    StartedAtUtc = heartbeat.AddMinutes(-1),
                    LastHeartbeatAtUtc = heartbeat,
                };
                var run = new ExperimentRunEntity
                {
                    Id = Guid.NewGuid(),
                    ApplicationSession = session,
                    Experiment = CreateExperiment(heartbeat),
                    Status = ExperimentRunStatus.Running,
                    RunMode = "Automatic",
                    SampleRateHz = 500,
                    CreatedAtUtc = heartbeat.AddMinutes(-1),
                    LastHeartbeatAtUtc = heartbeat,
                    LastKnownElapsedMilliseconds = 34_000,
                };
                run.Events.Add(
                    new ExperimentEventEntity
                    {
                        Kind = ExperimentEventKind.Stimulation,
                        Cycle = 2,
                        Sequence = 3,
                        RequestedAtUtc = heartbeat.AddSeconds(-5),
                        StartedAtUtc = heartbeat.AddSeconds(-4),
                        TimelineStartMilliseconds = 30_000,
                        Status = ExperimentEventStatus.Running,
                    }
                );
                db.ExperimentRuns.Add(run);
                await db.SaveChangesAsync();
            }

            var recoveredAt = DateTimeOffset.UtcNow;
            await new ExperimentRecoveryService(factory).RecoverAsync(recoveredAt);

            await using var assertionDb = factory.CreateDbContext();
            var recovered = await assertionDb
                .ExperimentRuns.Include(x => x.Events)
                .Include(x => x.Incidents)
                .SingleAsync();
            Assert.Equal(ExperimentRunStatus.RecoveredAfterCrash, recovered.Status);
            Assert.Equal(recovered.LastHeartbeatAtUtc, recovered.EndedAtUtc);
            Assert.Equal(ExperimentEventStatus.Interrupted, recovered.Events.Single().Status);
            Assert.Equal(
                EventEndTimeAccuracy.LastDurableHeartbeat,
                recovered.Events.Single().EndTimeAccuracy
            );
            Assert.Equal(recovered.LastHeartbeatAtUtc, recovered.Events.Single().EndedAtUtc);
            Assert.Equal(34_000d, recovered.Events.Single().TimelineEndMilliseconds);
            Assert.Contains(
                recovered.Incidents,
                x => x.Kind == ExperimentIncidentKind.CrashRecovery
            );
            Assert.False(
                await assertionDb.ApplicationSessions.AnyAsync(x => x.ClosedAtUtc == null)
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactFinalizerWritesRawMicrovoltsToCsvAndEdfD()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            var rawPath = Path.Combine(directory, recordingId.ToString("N") + ".eegraw");
            await File.WriteAllBytesAsync(rawPath, [1]);
            var metadata = new EegRecordingMetadata(
                recordingId,
                "EXP-20260827-001",
                "00000001",
                "device",
                500,
                ["Fz"],
                DateTimeOffset.UtcNow,
                new EegDisplayFilterSettings(1, 30, 50),
                new Dictionary<int, string> { [1] = "Fz" }
            );
            var summary = new EegRecordingSummary(
                recordingId,
                rawPath,
                true,
                EegRecordingCompletionStatus.Completed,
                1,
                108,
                metadata.StartedAtUtc,
                DateTimeOffset.UtcNow,
                metadata
            );
            var packet = new EegRawPacketRecord(
                recordingId,
                1,
                metadata.StartedAtUtc,
                0,
                2.5,
                500,
                1,
                2,
                "Acquisition",
                CreateV101EegFrame(channelOneRawValue: 1200)
            );
            Assert.True(EegRecordedSampleDecoder.TryDecode(packet, out var sampleBatch));

            var result = await new EegArtifactFinalizer().FinalizeAsync(
                summary,
                _ => Yield(sampleBatch)
            );

            var csv = await File.ReadAllTextAsync(result.CsvPath);
            var expectedMicrovolts = 1200 * (2d * 4.5d / (12d * (1 << 24)) * 1_000_000d);
            Assert.Contains(
                expectedMicrovolts.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                csv
            );
            Assert.Contains("2.5,2,\"Acquisition\"", csv);
            var header = Encoding.ASCII.GetString(
                (await File.ReadAllBytesAsync(result.EdfPath))[..256]
            );
            Assert.Equal("EDF+D", header.Substring(192, 44).Trim());
            Assert.Equal(1, result.SampleCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactFinalizerWritesEmptyCsvFieldAndEdfAnnotationForMissingChannel()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            var rawPath = Path.Combine(directory, recordingId.ToString("N") + ".eegraw");
            await File.WriteAllBytesAsync(rawPath, [1]);
            var metadata = new EegRecordingMetadata(
                recordingId,
                "EXP-GAP",
                "SUB-GAP",
                "simulated",
                500,
                ["C4", "C6"],
                DateTimeOffset.UtcNow,
                new EegDisplayFilterSettings(null, null, null),
                new Dictionary<int, string> { [1] = "C4", [2] = "C6" }
            );
            var summary = new EegRecordingSummary(
                recordingId,
                rawPath,
                true,
                EegRecordingCompletionStatus.Completed,
                0,
                0,
                metadata.StartedAtUtc,
                DateTimeOffset.UtcNow,
                metadata,
                SampleBatchCount: 1
            );
            var batch = new EegRecordedSampleBatch(
                recordingId,
                1,
                metadata.StartedAtUtc,
                0,
                2.5d,
                500,
                1d / 500d,
                1,
                "Acquisition",
                [new EegRecordedChannelSamples(1, new double[] { 12.5d })]
            );

            var result = await new EegArtifactFinalizer().FinalizeAsync(summary, _ => Yield(batch));

            var csvLines = await File.ReadAllLinesAsync(result.CsvPath);
            Assert.EndsWith(",12.5,", Assert.Single(csvLines.Skip(1)), StringComparison.Ordinal);
            var edf = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(result.EdfPath));
            Assert.Contains("GAP:C6", edf, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ExperimentEntity CreateExperiment(DateTimeOffset now)
    {
        var operatorEntity = new OperatorEntity
        {
            Username = "1",
            NormalizedUsername = "1",
            PasswordSalt = [1],
            PasswordHash = [1],
            PasswordIterations = 1,
            CreatedAtUtc = now,
        };
        var subject = new SubjectEntity
        {
            SubjectCode = "00000001",
            NormalizedSubjectCode = "00000001",
            CreatedAtUtc = now,
        };
        return new ExperimentEntity
        {
            ExperimentCode = "EXP-20260827-001",
            ScheduledAt = now,
            Remarks = string.Empty,
            Status = ExperimentStatus.Running,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Operator = operatorEntity,
            Subject = subject,
        };
    }

    private static byte[] CreateV101EegFrame(int channelOneRawValue)
    {
        var payload = new byte[3 + 32 * 3 + 1];
        payload[2] = 32;
        payload[3] = (byte)(channelOneRawValue >> 16);
        payload[4] = (byte)(channelOneRawValue >> 8);
        payload[5] = (byte)channelOneRawValue;
        payload[^1] = 80;
        var frame = new byte[6 + payload.Length + 2];
        frame[0] = 0xAA;
        frame[1] = 0xBB;
        frame[2] = 1;
        var length = frame.Length;
        frame[3] = (byte)length;
        frame[4] = (byte)(length >> 8);
        frame[5] = (byte)EggtCsCommandCode.EegData;
        payload.CopyTo(frame, 6);
        frame[^2] = 0xFF;
        frame[^1] = 0xFF;
        return frame;
    }

    private static async IAsyncEnumerable<T> Yield<T>(T item)
    {
        await Task.Yield();
        yield return item;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExitDisposesRealExperimentHeartbeatOnUiContext(bool finishRun) =>
        ApplicationExitWorkflowTests.OnUiThread(async () =>
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var factory = CreateFactory(Path.Combine(directory, "exit.db"));
                var session = new ApplicationSessionState { Id = Guid.NewGuid() };
                long experimentId;
                await using (var db = factory.CreateDbContext())
                {
                    await db.Database.EnsureCreatedAsync();
                    var now = DateTimeOffset.UtcNow;
                    db.ApplicationSessions.Add(
                        new ApplicationSessionEntity
                        {
                            Id = session.Id,
                            StartedAtUtc = now,
                            LastHeartbeatAtUtc = now,
                        }
                    );
                    var experiment = CreateExperiment(now);
                    db.Experiments.Add(experiment);
                    await db.SaveChangesAsync();
                    experimentId = experiment.Id;
                }
                var coordinator = new ExperimentRunPersistenceCoordinator(factory, session);
                var runId = Guid.NewGuid();
                await coordinator.PrepareAsync(
                    new ExperimentRunPreparation(
                        runId,
                        experimentId,
                        "Manual",
                        1,
                        500,
                        TimeSpan.FromSeconds(1),
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        TimeSpan.Zero
                    ),
                    "device"
                );
                var exited = false;
                var failures = new List<Exception>();
                var workflow = new ApplicationExitWorkflow(
                    async () =>
                    {
                        if (finishRun)
                            await coordinator.FinishAsync(
                                ExperimentRunStatus.InterruptedByExit,
                                ExperimentIncidentKind.ApplicationExit,
                                source: "application-exit"
                            );
                    },
                    () => coordinator.DisposeAsync().AsTask(),
                    () => { },
                    () => exited = true,
                    failures.Add
                );
                await workflow.RunAsync();
                Assert.True(exited);
                Assert.Empty(failures);
                if (finishRun)
                {
                    await using var db = factory.CreateDbContext();
                    Assert.Equal(
                        ExperimentRunStatus.InterruptedByExit,
                        (await db.ExperimentRuns.SingleAsync(x => x.Id == runId)).Status
                    );
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

    private static TestContextFactory CreateFactory(string path) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options
        );

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "eggtcs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
