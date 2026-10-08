using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed partial class ExperimentRunPageViewModelTests
{
    [Theory]
    [InlineData(3, false, false)]
    [InlineData(8, true, false)]
    [InlineData(3, true, true)]
    public async Task ExitDuringSimulationPreservesHistoryAndHover(
        int seconds,
        bool automatic,
        bool secondCycle
    )
    {
        await using var fixture = await ExitFixture.CreateAsync();
        await using var runtime = new DeviceRuntime.DeviceRuntimeBuilder().UseSimulation().Build();
        await foreach (var candidate in runtime.Discovery.DiscoverAsync(TimeSpan.Zero))
            await runtime.ConnectVerifiedAsync(candidate);
        await using var store = new FileEegRawPacketStore(
            fixture.Directory,
            fileRegistry: new EegFileRegistry(fixture.Factory)
        );
        using var service = new DeviceExperimentRunService(
            runtime.Devices,
            new TestChannelMappingService(),
            rawPacketRecorder: store
        );
        using var model = new ExperimentRunPageViewModel(
            fixture.Route,
            new FakeRouter(),
            service,
            timingOptions: secondCycle
                ? new ExperimentRunTimingOptions
                {
                    DurationStepMilliseconds = 100,
                    DurationMinimumMilliseconds = 100,
                    BlankingDurationMilliseconds = 100,
                    RecoveryDurationMilliseconds = 100,
                }
                : null,
            eegChannelMappings: new TestChannelMappingService(),
            persistence: fixture.Coordinator
        );
        model.SelectModeCommand.Execute(
            automatic ? ExperimentRunMode.Automatic : ExperimentRunMode.Manual
        );
        SetSeconds(model.Stages[0], 30);
        SetSeconds(model.Stages[2], 15);
        if (secondCycle)
        {
            model.CycleCount = 2;
            SetSeconds(model.Stages[0], 2);
            SetSeconds(model.Stages[1], 0.1);
            SetSeconds(model.Stages[2], 0.2);
            SetSeconds(model.Stages[3], 0.1);
        }
        Assert.True(
            automatic
                ? model.StartAutomaticExperimentCommand.CanExecute(null)
                : model.StartAcquisitionCommand.CanExecute(null)
        );
        var running = automatic
            ? model.StartAutomaticExperimentCommand.ExecuteAsync(null)
            : model.StartAcquisitionCommand.ExecuteAsync(null);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 10));
            while (model.Elapsed.TotalSeconds < seconds || !model.HasWaveformData)
                await Task.Delay(20, timeout.Token);
            var runId = fixture.Coordinator.RunId;
            var shutdown = new ApplicationShutdownCoordinator(
                fixture.Coordinator,
                service,
                new NoCleanup(),
                fixture.Factory,
                fixture.Session
            );
            var exit = shutdown.ShutdownAsync("application-exit", cleanupTemporaryFiles: false);
            Assert.Same(
                exit,
                shutdown.ShutdownAsync("application-exit", cleanupTemporaryFiles: false)
            );
            await exit.WaitAsync(TimeSpan.FromSeconds(10));
            await running.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Single(
                fixture.Logger.Entries.Where(entry => entry.EventName == "Experiment.Started")
            );
            Assert.Single(
                fixture.Logger.Entries.Where(entry =>
                    entry.EventName == "Experiment.InterruptedByExit"
                    && entry.CorrelationId == runId.ToString()
                )
            );

            var completion = await service.CompleteRecordingAsync(
                runId,
                EegRecordingCompletionStatus.Canceled
            );
            var repeated = await service.CompleteRecordingAsync(
                runId,
                EegRecordingCompletionStatus.Completed
            );
            Assert.Same(completion, repeated);
            Assert.True(completion.DataEndExclusiveSeconds > seconds - 0.5);
            await using (var db = fixture.Factory.CreateDbContext())
            {
                var saved = await db
                    .ExperimentRuns.Include(x => x.Events)
                    .SingleAsync(x => x.Id == runId);
                Assert.Equal(ExperimentRunStatus.InterruptedByExit, saved.Status);
                var acquisition = saved.Events.OrderBy(x => x.Sequence).Last();
                Assert.Equal(secondCycle ? 5 : 1, saved.Events.Count);
                Assert.Equal(ExperimentEventKind.Acquisition, acquisition.Kind);
                if (secondCycle)
                    Assert.Equal(
                        2000d,
                        saved.Events.OrderBy(x => x.Sequence).First().TimelineEndMilliseconds
                    );
                Assert.Equal(ExperimentEventStatus.Interrupted, acquisition.Status);
                Assert.InRange(
                    acquisition.TimelineEndMilliseconds!.Value,
                    (seconds - 0.5) * 1000,
                    (seconds + 3) * 1000
                );
                Assert.True(
                    acquisition.TimelineEndMilliseconds >= completion.DataEndExclusiveSeconds * 1000
                );
            }
            Assert.NotEqual(ExperimentRunPageState.Completed, model.PageState);

            // Re-open via the real persistence route and raw-file reader, with fresh history caches.
            var persistence = new ExperimentPersistenceService(
                fixture.Factory,
                new CurrentOperatorContext(),
                new TestChannelMappingService()
            );
            var historicalRoute = await persistence.LoadHistoricalRunAsync(runId);
            using var history = new ExperimentRunPageViewModel(
                historicalRoute,
                new FakeRouter(),
                service,
                historyWindowProvider: new EegHistoryWindowProvider(store)
            );
            await WaitUntilAsync(() => history.HasWaveformData);
            Assert.True(history.TimelineMaximum >= completion.DataEndExclusiveSeconds);
            Assert.True(history.TimelineMaximum > 1d);
            var channel = history.WaveformChannels.First();
            foreach (
                var time in new[]
                {
                    channel.Samples[0].TimeSeconds,
                    seconds / 2d,
                    channel.Samples[^1].TimeSeconds,
                }
            )
            {
                var interval = Assert.Single(
                    history.TimelineSegments.Where(x =>
                        x.Stage == ExperimentRunStage.Acquisition
                        && time >= x.StartSeconds
                        && time < x.EndSeconds
                    )
                );
                Assert.InRange(time, interval.StartSeconds, interval.EndSeconds);
                Assert.True(
                    channel.TryResolveHoverSample(
                        time,
                        interval.StartSeconds,
                        interval.EndSeconds,
                        out var point
                    )
                );
                Assert.True(double.IsFinite(point.Value));
            }
            var last = history.TimelineSegments.Last();
            Assert.False(
                channel.TryResolveHoverSample(
                    last.EndSeconds + 1,
                    last.StartSeconds,
                    last.EndSeconds,
                    out _
                )
            );
        }
        finally
        {
            if (fixture.Coordinator.IsActive)
                await new ApplicationShutdownCoordinator(
                    fixture.Coordinator,
                    service,
                    new NoCleanup(),
                    fixture.Factory,
                    fixture.Session
                ).ShutdownAsync("test-cleanup", cleanupTemporaryFiles: false);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ProgressHeartbeatIsMonotonicAndRecoveryUsesItsLogicalTime()
    {
        await using var fixture = await ExitFixture.CreateAsync();
        var coordinator = fixture.Coordinator;
        var id = Guid.NewGuid();
        await coordinator.PrepareAsync(
            new(
                id,
                fixture.Route.ExperimentDatabaseId,
                "Manual",
                1,
                500,
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero
            ),
            "device"
        );
        await coordinator.RequestStageAsync(ExperimentRunStage.Acquisition, 1, TimeSpan.Zero);
        await coordinator.ObserveStageAsync(
            ExperimentRunStage.Acquisition,
            1,
            TimeSpan.Zero,
            TimeSpan.Zero
        );
        coordinator.ReportProgress(id, ExperimentRunStage.Acquisition, 1, TimeSpan.FromSeconds(8));
        coordinator.ReportProgress(id, ExperimentRunStage.Acquisition, 1, TimeSpan.FromSeconds(2));
        coordinator.ReportProgress(
            Guid.NewGuid(),
            ExperimentRunStage.Acquisition,
            1,
            TimeSpan.FromSeconds(99)
        );
        coordinator.ReportProgress(id, ExperimentRunStage.Stimulation, 1, TimeSpan.FromSeconds(99));
        coordinator.ReportProgress(id, ExperimentRunStage.Acquisition, 2, TimeSpan.FromSeconds(99));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var db = fixture.Factory.CreateDbContext();
            var run = await db
                .ExperimentRuns.Include(x => x.Events)
                .SingleAsync(x => x.Id == id, timeout.Token);
            Assert.Single(run.Events);
            if (run.LastKnownElapsedMilliseconds == 8000)
                break;
            await Task.Delay(20, timeout.Token);
        }
        // Stop the heartbeat without closing the run, as when a process disappears.
        await coordinator.DisposeAsync();
        fixture.CoordinatorDisposed = true;
        await new ExperimentRecoveryService(fixture.Factory).RecoverAsync(DateTimeOffset.UtcNow);
        await using var recoveredDb = fixture.Factory.CreateDbContext();
        var recovered = await recoveredDb.ExperimentRuns.Include(x => x.Events).SingleAsync();
        Assert.Equal(ExperimentRunStatus.RecoveredAfterCrash, recovered.Status);
        Assert.Equal(8000d, Assert.Single(recovered.Events).TimelineEndMilliseconds);
    }

    [Theory]
    [InlineData(ExperimentRunStage.Acquisition)]
    [InlineData(ExperimentRunStage.Blanking)]
    [InlineData(ExperimentRunStage.Stimulation)]
    [InlineData(ExperimentRunStage.Recovery)]
    public async Task ExitClosesOnlyCurrentStageAndRejectsLateCompletion(ExperimentRunStage stage)
    {
        await using var fixture = await ExitFixture.CreateAsync();
        var coordinator = fixture.Coordinator;
        var id = Guid.NewGuid();
        await coordinator.PrepareAsync(
            new(
                id,
                fixture.Route.ExperimentDatabaseId,
                "Automatic",
                2,
                500,
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero
            ),
            "device"
        );
        await coordinator.RequestStageAsync(ExperimentRunStage.Acquisition, 1, TimeSpan.Zero);
        await coordinator.CompleteCurrentStageAsync(TimeSpan.FromSeconds(10));
        // A late observation of the closed stage must not reopen it.
        await coordinator.ObserveStageAsync(
            ExperimentRunStage.Acquisition,
            1,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(9)
        );
        await coordinator.RequestStageAsync(stage, 2, TimeSpan.FromSeconds(20));
        coordinator.ReportProgress(id, stage, 2, TimeSpan.FromSeconds(24));
        coordinator.ReportProgress(id, ExperimentRunStage.Acquisition, 1, TimeSpan.FromSeconds(99));
        await coordinator.RequestInterruptionAsync("application-exit");
        Assert.True(coordinator.RunCancellationToken.IsCancellationRequested);
        coordinator.ReportProgress(id, stage, 2, TimeSpan.FromSeconds(99));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.CompleteCurrentStageAsync(TimeSpan.FromSeconds(50))
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.FinishAsync(ExperimentRunStatus.Completed)
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.RequestStageAsync(ExperimentRunStage.Recovery, 2, TimeSpan.FromSeconds(50))
        );
        await coordinator.FinishAsync(
            ExperimentRunStatus.InterruptedByExit,
            timelineEnd: TimeSpan.FromSeconds(23),
            dataEndExclusiveSeconds: stage == ExperimentRunStage.Acquisition ? 24.5 : 10
        );
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        await using var db = fixture.Factory.CreateDbContext();
        var run = await db.ExperimentRuns.Include(x => x.Events).SingleAsync();
        Assert.Equal(ExperimentRunStatus.InterruptedByExit, run.Status);
        var events = run.Events.OrderBy(x => x.Sequence).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(10000d, events[0].TimelineEndMilliseconds);
        Assert.Equal(
            stage == ExperimentRunStage.Acquisition ? 24500d : 24000d,
            events[1].TimelineEndMilliseconds
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrTimedOutFinalizationKeepsProgressForClosureOrRecovery(bool timeout)
    {
        await using var fixture = await ExitFixture.CreateAsync();
        var coordinator = fixture.Coordinator;
        var id = Guid.NewGuid();
        await coordinator.PrepareAsync(
            new(
                id,
                fixture.Route.ExperimentDatabaseId,
                "Manual",
                1,
                500,
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero
            ),
            "device"
        );
        await coordinator.RequestStageAsync(ExperimentRunStage.Acquisition, 1, TimeSpan.Zero);
        coordinator.ReportProgress(id, ExperimentRunStage.Acquisition, 1, TimeSpan.FromSeconds(4));
        var pending = new TaskCompletionSource<EegRecordingCompletionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var service = new FakeRunService { PendingRecordingCompletion = pending.Task };
        if (!timeout)
            pending.SetException(new IOException("recording write failed"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await new ApplicationShutdownCoordinator(
            coordinator,
            service,
            new NoCleanup(),
            fixture.Factory,
            fixture.Session
        ).ShutdownAsync(
            "application-exit",
            cleanupTemporaryFiles: false,
            cancellationToken: timeout ? cancellation.Token : CancellationToken.None
        );
        if (timeout)
        {
            Assert.True(coordinator.IsActive);
            await coordinator.DisposeAsync();
            fixture.CoordinatorDisposed = true;
            await new ExperimentRecoveryService(fixture.Factory).RecoverAsync(
                DateTimeOffset.UtcNow
            );
            pending.TrySetResult(new(EegPacketStatistics.Empty()));
        }
        await using var db = fixture.Factory.CreateDbContext();
        var run = await db.ExperimentRuns.Include(x => x.Events).SingleAsync();
        Assert.Equal(
            timeout
                ? ExperimentRunStatus.RecoveredAfterCrash
                : ExperimentRunStatus.InterruptedByExit,
            run.Status
        );
        Assert.Equal(4000d, Assert.Single(run.Events).TimelineEndMilliseconds);
    }

    private sealed class NoCleanup : ITemporaryEegCleanupService
    {
        public Task CleanupAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ExitFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={path};Pooling=False")
                    .Options
            );
    }

    private sealed class ExitFixture : IAsyncDisposable
    {
        public RecordingApplicationLogger Logger { get; } = new();
        public string Directory { get; } =
            Path.Combine(Path.GetTempPath(), "eggtcs-exit-tests", Guid.NewGuid().ToString("N"));
        public ExitFactory Factory { get; private set; } = null!;
        public ApplicationSessionState Session { get; } = new() { Id = Guid.NewGuid() };
        public ExperimentRunPersistenceCoordinator Coordinator { get; private set; } = null!;
        public ExperimentRunRouteData Route { get; private set; } = null!;
        public bool CoordinatorDisposed { get; set; }

        public static async Task<ExitFixture> CreateAsync()
        {
            var fixture = new ExitFixture();
            System.IO.Directory.CreateDirectory(fixture.Directory);
            fixture.Factory = new ExitFactory(Path.Combine(fixture.Directory, "exit.db"));
            var source = CreateRoute(rampSeconds: 0);
            await using var db = fixture.Factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            db.ApplicationSessions.Add(
                new()
                {
                    Id = fixture.Session.Id,
                    StartedAtUtc = now,
                    LastHeartbeatAtUtc = now,
                }
            );
            var experiment = new ExperimentEntity
            {
                ExperimentCode = source.ExperimentId,
                ScheduledAt = now,
                Remarks = "",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Operator = new()
                {
                    Username = "exit",
                    NormalizedUsername = "EXIT",
                    PasswordHash = [1],
                    PasswordSalt = [1],
                    PasswordIterations = 1,
                    CreatedAtUtc = now,
                },
                Subject = new()
                {
                    SubjectCode = source.SubjectId,
                    NormalizedSubjectCode = source.SubjectId,
                    CreatedAtUtc = now,
                },
                StimulusParadigm = new()
                {
                    Kind = "TDcs",
                    ArrayMode = "DualChannel",
                    Direction = "Positive",
                    ShamMode = "Direct",
                    Targets =
                    [
                        new()
                        {
                            DisplayOrder = 1,
                            PeakCurrentMilliAmps = 2,
                            Channels =
                            [
                                new()
                                {
                                    SiteId = "Fz",
                                    PhysicalChannelId = 1,
                                    Role = "FixedActive",
                                    CurrentMilliAmps = 2,
                                },
                                new()
                                {
                                    SiteId = "CP4",
                                    PhysicalChannelId = 2,
                                    Role = "Selectable",
                                    CurrentMilliAmps = 2,
                                },
                            ],
                        },
                    ],
                },
                Electrodes =
                [
                    new()
                    {
                        SiteId = "F3",
                        AcquisitionUsage = ElectrodeUsage.Acquisition,
                        AcquisitionPhysicalChannelId = 3,
                    },
                    new()
                    {
                        SiteId = "Cz",
                        AcquisitionUsage = ElectrodeUsage.Acquisition,
                        AcquisitionPhysicalChannelId = 25,
                    },
                    new() { SiteId = "FCz", AcquisitionUsage = ElectrodeUsage.Reference },
                    new() { SiteId = "AFz", AcquisitionUsage = ElectrodeUsage.Ground },
                ],
            };
            db.Experiments.Add(experiment);
            await db.SaveChangesAsync();
            fixture.Route = new(
                source.ExperimentId,
                source.SubjectId,
                source.StimulusConfiguration,
                source.StimulusElectrodes,
                source.AcquisitionChannels,
                source.ReferenceChannel,
                source.GroundChannel,
                500,
                experimentDatabaseId: experiment.Id
            );
            fixture.Coordinator = new(fixture.Factory, fixture.Session, fixture.Logger);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            if (!CoordinatorDisposed)
                await Coordinator.DisposeAsync();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
