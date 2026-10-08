using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SingleStimulusExperimentTests
{
    [Fact]
    public async Task EnvelopeSetupRoundTripsThroughDatabaseWithoutCreatingRun()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        var persistence = new ExperimentPersistenceService(
            fixture.Factory,
            new CurrentOperatorContext(),
            fixture.Mapping
        );
        var original = Template();
        var configuration = original with
        {
            StimulusConfiguration = original.StimulusConfiguration with
            {
                Kind = StimulusKind.EnvelopeTAcs,
                Direction = StimulusDirection.Bidirectional,
                Envelope = new EnvelopeParameters(DelayMilliseconds: 125, IsDeviceSetup: true),
            },
        };
        await persistence.SaveConfigurationAsync(id, configuration, []);
        var loaded = await persistence.LoadTemplateAsync(id);
        Assert.Equal(
            configuration.StimulusConfiguration.Envelope,
            loaded.StimulusConfiguration.Envelope
        );
        Assert.Equal(
            configuration.StimulusConfiguration.Targets[0].PeakCurrent,
            loaded.StimulusConfiguration.Targets[0].PeakCurrent
        );
        Assert.Equal(StimulusKind.EnvelopeTAcs, loaded.StimulusConfiguration.Kind);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.ExperimentRuns.ToListAsync());
    }

    [Theory]
    [InlineData(ExperimentRunMode.Manual, 1)]
    [InlineData(ExperimentRunMode.Automatic, 2)]
    public async Task AcquisitionOnlyPersistsActualStagesAndPackageRoundTrips(
        ExperimentRunMode mode,
        int cycles
    )
    {
        await using var fixture = new DatabaseFixture();
        fixture.Mapping.Save(
            32,
            ElectrodePositionCatalog
                .Default.ElectrodeIds.Select(id => new EegPhysicalChannelMapping(
                    id,
                    id == "C3" ? 1 : null
                ))
                .ToArray()
        );
        var experimentId = await fixture.InitializeAsync();
        var persistence = new ExperimentPersistenceService(
            fixture.Factory,
            new CurrentOperatorContext(),
            fixture.Mapping
        );
        var template = Template() with
        {
            CreationMode = ExperimentCreationMode.AcquisitionOnly,
            AcquisitionChannels = ["C3"],
            ReferenceChannel = "FCz",
            GroundChannel = "AFz",
            Timing = new ExperimentTimingTemplate(mode, 100, 50, 0, 0, cycles),
        };
        // Legacy stimulus data is irrelevant to a newly saved acquisition-only configuration.
        await persistence.SaveConfigurationAsync(experimentId, template, []);
        var saved = await persistence.LoadTemplateAsync(experimentId);
        Assert.Empty(saved.StimulusConfiguration.Targets);
        Assert.Empty(saved.StimulusElectrodes);
        Assert.Equal(0, saved.Timing.StimulationMilliseconds);
        Assert.Equal(0, saved.Timing.RecoveryMilliseconds);

        await using var runtime = new DeviceRuntime.DeviceRuntimeBuilder().UseSimulation().Build();
        await foreach (var candidate in runtime.Discovery.DiscoverAsync(TimeSpan.Zero))
            await runtime.ConnectVerifiedAsync(candidate);
        using var service = new DeviceExperimentRunService(runtime.Devices, fixture.Mapping);
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        var source = Route(experimentId);
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            ["C3"],
            "FCz",
            "AFz",
            500,
            DeviceSdk.DeviceId.Simulator.Value,
            experimentId,
            importedTiming: template.Timing,
            creationMode: ExperimentCreationMode.AcquisitionOnly
        );
        using var model = new ExperimentRunPageViewModel(
            route,
            new AcquisitionTestRouter(),
            service,
            timingOptions: new ExperimentRunTimingOptions
            {
                DurationMinimumMilliseconds = 1,
                DurationStepMilliseconds = 1,
            },
            persistence: coordinator
        );
        if (mode == ExperimentRunMode.Manual)
            await model.StartAcquisitionCommand.ExecuteAsync(null);
        else
            await model.StartAutomaticExperimentCommand.ExecuteAsync(null);
        Assert.True(model.IsCompleted);

        await using var db = fixture.Factory.CreateDbContext();
        var run = await db.ExperimentRuns.SingleAsync();
        Assert.Equal(ExperimentRunStatus.Completed, run.Status);
        Assert.Equal(0, run.StimulationDurationMilliseconds);
        Assert.Equal(0, run.RecoveryDurationMilliseconds);
        var events = await db.ExperimentEvents.OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal(cycles * 2, events.Count);
        Assert.Equal(
            Enumerable
                .Range(1, cycles)
                .SelectMany(_ =>
                    new[] { ExperimentEventKind.Acquisition, ExperimentEventKind.Blanking }
                ),
            events.Select(x => x.Kind)
        );
        var history = await persistence.LoadHistoricalRunAsync(run.Id);
        Assert.Equal(cycles * 2, history.HistoricalResult!.StageIntervals.Count);
        Assert.Equal(cycles * 0.15, history.HistoricalResult.StageIntervals.Last().EndSeconds, 8);
        using var replay = new ExperimentRunPageViewModel(
            history,
            new AcquisitionTestRouter(),
            service
        );
        Assert.Equal(cycles * 2, replay.TimelineSegments.Count);

        var serializer = new ExperimentPackageSerializer(
            StimulusCapabilityProfile.Default,
            ElectrodePositionCatalog.Default,
            fixture.Mapping,
            new ApplicationVersionProvider()
        );
        await using var raw = new FileEegRawPacketStore(fixture.Directory);
        var export = new EegExportService(
            fixture.Factory,
            persistence,
            serializer,
            new NoReveal(),
            new EegArtifactFinalizer(),
            raw
        );
        var result = await export.ExportAsync(
            run.Id,
            new EegExportSelection(false, false, true),
            fixture.Directory,
            revealResult: false
        );
        var imported = await serializer.ReadAsync(Assert.Single(result.Paths));
        Assert.Equal(ExperimentCreationMode.AcquisitionOnly, imported.CreationMode);
        Assert.Equal(template.Timing, imported.Timing);
        Assert.Empty(imported.StimulusElectrodes);
        Assert.Equal(new[] { "C3" }, imported.AcquisitionChannels);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(result.Paths[0]))!.AsObject();
        json.Remove("stimulus");
        var missingStimulusFile = Path.Combine(fixture.Directory, "missing-stimulus.expp");
        await File.WriteAllTextAsync(missingStimulusFile, json.ToJsonString());
        Assert.Equal(
            ExperimentCreationMode.AcquisitionOnly,
            (await serializer.ReadAsync(missingStimulusFile)).CreationMode
        );
    }

    private sealed class AcquisitionTestRouter : INavigationRouter
    {
        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData route) { }

        public void Navigate(StimulusConfigurationRouteData route) { }

        public void Navigate(ElectrodeConfigurationRouteData route) { }

        public void Navigate(ExperimentRunRouteData route) { }

        public void Navigate(ExperimentRerunRouteData route) { }

        public void GoBack() { }

        public void GoHome() { }
    }

    [Fact]
    public void DefaultsAndDurationValidationUseExistingDurationModel()
    {
        using var model = new SingleStimulusExperimentDialogViewModel(Route(), new RunService());
        Assert.Equal(10m, model.Duration.DurationValue);
        Assert.Equal("min", model.Duration.SelectedUnit!.Label);
        Assert.True(model.CanStart);
        Assert.False(model.CanEmergencyStop);
        model.Duration.SelectedUnit = model.Duration.Units[1];
        Assert.Equal(600m, model.Duration.DurationValue);
        model.Duration.DurationValue = 1;
        Assert.False(model.CanStart); // Two seven-second ramps do not fit.
        model.Duration.DurationValue = 15;
        Assert.True(model.CanStart);
        model.Duration.DurationValue = 15.5m;
        Assert.False(model.CanStart);
        model.Duration.DurationValue = 65536;
        Assert.False(model.CanStart);
    }

    [Fact]
    public async Task CompletedDialogMustCloseAndNewDialogCreatesIndependentRun()
    {
        var service = new RunService();
        using var model = new SingleStimulusExperimentDialogViewModel(Route(), service);
        model.Show();
        await model.StartCommand.ExecuteAsync(null);
        Assert.Equal("已结束", model.StatusText);
        Assert.Equal(TimeSpan.FromMinutes(10), model.Elapsed);
        Assert.True(model.IsFinished);
        Assert.False(model.CanEdit);
        Assert.False(model.CanStart);
        Assert.Equal("0分0秒", model.RemainingText);
        await model.StartCommand.ExecuteAsync(null);
        Assert.Single(service.Requests);
        model.CloseExperimentCommand.Execute(null);
        Assert.False(model.IsDialogOpen);
        using var reopened = new SingleStimulusExperimentDialogViewModel(Route(), service);
        reopened.Duration.DurationValue = 2;
        await reopened.StartCommand.ExecuteAsync(null);
        Assert.Equal(2, service.Requests.Count);
        Assert.NotEqual(service.Requests[0].RecordingId, service.Requests[1].RecordingId);
        Assert.All(
            service.Requests,
            request =>
            {
                Assert.Empty(request.ChannelIds);
                Assert.Equal(TimeSpan.Zero, request.TimelineOffset);
                Assert.NotNull(request.StimulusConfiguration);
                Assert.Equal(2, request.StimulusElectrodes!.Count);
            }
        );
        Assert.Equal(TimeSpan.FromMinutes(2), service.Requests[1].Duration);
        Assert.Equal(0, service.Acquisitions + service.Recordings + service.Automatic);
    }

    [Fact]
    public async Task RunningLocksCloseDurationAndDuplicateStartThenStopUnlocks()
    {
        var service = new RunService { Block = true };
        using var model = new SingleStimulusExperimentDialogViewModel(Route(), service);
        model.Show();
        var run = model.StartCommand.ExecuteAsync(null);
        Assert.True(model.IsRunning);
        Assert.False(model.CanClose);
        Assert.False(model.CanEdit);
        Assert.False(model.StartCommand.CanExecute(null));
        model.CloseExperimentCommand.Execute(null);
        model.OverlayDismissCommand.Execute(null);
        Assert.True(model.IsDialogOpen);
        await model.StartCommand.ExecuteAsync(null);
        Assert.Single(service.Requests);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await run;
        Assert.Equal("已紧急停止", model.StatusText);
        Assert.False(model.CanStart);
        Assert.False(model.CanEdit);
        Assert.True(model.IsFinished);
        model.CloseExperimentCommand.Execute(null);
        Assert.False(model.IsDialogOpen);
    }

    [Fact]
    public async Task FailedStopStaysLockedAndCanBeRetried()
    {
        var service = new RunService
        {
            Block = true,
            StopFailure = new IOException("disconnected"),
        };
        using var model = new SingleStimulusExperimentDialogViewModel(Route(), service);
        var run = model.StartCommand.ExecuteAsync(null);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await run;
        Assert.Equal("停止未确认", model.StatusText);
        Assert.False(model.IsFinished);
        Assert.False(model.CanClose);
        Assert.False(model.CanStart);
        Assert.True(model.CanEmergencyStop);
        service.StopFailure = null;
        await model.EmergencyStopCommand.ExecuteAsync(null);
        Assert.Equal("已紧急停止", model.StatusText);
        Assert.True(model.IsFinished);
        Assert.False(model.HasError);
        Assert.True(model.CanClose);
        Assert.Equal(2, service.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartFailureReportsErrorAndUnconfirmedStopDoesNotEnableRestart(
        bool stopFailed
    )
    {
        var service = new RunService
        {
            StartFailure = new StimulationException(
                StimulationFailureKind.StartRejected,
                "rejected",
                stopCommandRequested: true,
                stopFailure: stopFailed ? "timeout" : null
            ),
        };
        using var model = new SingleStimulusExperimentDialogViewModel(Route(), service);
        await model.StartCommand.ExecuteAsync(null);
        Assert.True(model.HasError);
        Assert.Equal(!stopFailed, model.CanStart);
        Assert.Equal(!stopFailed, model.CanClose);
        Assert.False(model.IsFinished);
        if (!stopFailed)
        {
            service.StartFailure = null;
            await model.StartCommand.ExecuteAsync(null);
            Assert.True(model.IsFinished);
            Assert.Equal(2, service.Requests.Count);
        }
    }

    [Fact]
    public async Task CountdownUsesTelemetryRoundsUpAndZeroDoesNotCompleteRun()
    {
        var service = new RunService { Block = true };
        using var model = new SingleStimulusExperimentDialogViewModel(Route(), service);
        Assert.False(model.ShowRemaining);
        var run = model.StartCommand.ExecuteAsync(null);
        Assert.True(model.ShowRemaining);
        Assert.Equal("10分0秒", model.RemainingText);
        service.Progress(-1);
        Assert.Equal("10分0秒", model.RemainingText);
        service.Progress(37.2);
        Assert.Equal("9分23秒", model.RemainingText);
        service.Progress(36);
        Assert.Equal("9分23秒", model.RemainingText);
        service.Progress(599.1);
        Assert.Equal("0分1秒", model.RemainingText);
        service.Progress(601);
        Assert.Equal("0分0秒", model.RemainingText);
        Assert.False(model.IsFinished);
        Assert.False(model.CanClose);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await run;
    }

    [Fact]
    public async Task EmergencyStopFreezesRemainingAndLateTelemetryCannotChangeResult()
    {
        var service = new RunService { Block = true };
        using var model = new SingleStimulusExperimentDialogViewModel(Route(), service);
        var run = model.StartCommand.ExecuteAsync(null);
        service.Progress(37);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await run;
        Assert.Equal("9分23秒", model.RemainingText);
        service.Progress(50);
        Assert.Equal("9分23秒", model.RemainingText);
        Assert.Equal(SingleStimulusRunState.Interrupted, model.State);
        Assert.False(model.CanStart);
    }

    [Fact]
    public async Task CountdownSupportsMaximumDeviceDurationAndSecondsUnit()
    {
        var service = new RunService { Block = true };
        using var model = new SingleStimulusExperimentDialogViewModel(Route(), service);
        model.Duration.SelectedUnit = model.Duration.Units[1];
        model.Duration.DurationValue = 65535;
        var run = model.StartCommand.ExecuteAsync(null);
        Assert.Equal("1092分15秒", model.RemainingText);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await run;
    }

    [Fact]
    public async Task TelemetrySubscriptionIsRemovedOnClose()
    {
        var service = new RunService();
        var model = new SingleStimulusExperimentDialogViewModel(Route(), service);
        Assert.Equal(1, service.Subscribers);
        await model.StartCommand.ExecuteAsync(null);
        model.Dispose();
        Assert.Equal(0, service.Subscribers);
    }

    [Fact]
    public void HistoricalDialogDoesNotNeedRawFilesOrSubscribeToDevice()
    {
        var service = new RunService();
        var history = new HistoricalExperimentRunContext(
            Guid.NewGuid(),
            string.Empty,
            ExperimentRunStatus.InterruptedByUser,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(27),
            new Dictionary<int, string>(),
            [],
            LogicalTimelineEndSeconds: 12
        );
        using var model = new SingleStimulusExperimentDialogViewModel(
            Route(history: history),
            service
        );
        Assert.True(model.IsReadOnly);
        Assert.False(model.CanStart);
        Assert.True(model.CanClose);
        Assert.Equal(TimeSpan.FromSeconds(12), model.Elapsed);
        Assert.Equal("已紧急停止", model.StatusText);
        Assert.Equal(0, service.Subscribers);
    }

    [Fact]
    public async Task MigrationKeepsOldModeAndExplicitSingleModeIsPersistedAsZero()
    {
        await using var fixture = new DatabaseFixture();
        await using var db = fixture.Factory.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20260910130000_RenameGenerationParameters");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Operators (Username,NormalizedUsername,PasswordSalt,PasswordHash,PasswordIterations,IsEnabled,CreatedAtUtc)
            VALUES ('old','old',X'01',X'01',1,1,'2026-09-01');
            INSERT INTO Subjects (SubjectCode,NormalizedSubjectCode,CreatedAtUtc) VALUES ('old','old','2026-09-01');
            INSERT INTO Experiments (ExperimentCode,ScheduledAt,Remarks,Status,CreatedAtUtc,UpdatedAtUtc,OperatorId,SubjectId)
            VALUES ('old','2026-09-01','',0,'2026-09-01','2026-09-01',1,1);
            """
        );
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var old = await db.Experiments.SingleAsync();
        Assert.Equal(ExperimentCreationMode.AcquisitionAndStimulation, old.CreationMode);
        var added = CreateExperiment();
        added.CreationMode = ExperimentCreationMode.StimulusOnly;
        db.Experiments.Add(added);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(
            ExperimentCreationMode.StimulusOnly,
            (await db.Experiments.SingleAsync(x => x.Id == added.Id)).CreationMode
        );
    }

    [Fact]
    public async Task TwoRunsPersistTimingAndHistoryWithoutEegFilesAndPackageRoundTrips()
    {
        await using var fixture = new DatabaseFixture();
        var experimentId = await fixture.InitializeAsync();
        var persistence = new ExperimentPersistenceService(
            fixture.Factory,
            new CurrentOperatorContext(),
            fixture.Mapping
        );
        await persistence.SaveConfigurationAsync(experimentId, Template(), []);
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        var service = new RunService();
        using (
            var model = new SingleStimulusExperimentDialogViewModel(
                Route(experimentId),
                service,
                coordinator
            )
        )
        {
            await model.StartCommand.ExecuteAsync(null);
            model.CloseExperimentCommand.Execute(null);
        }
        await persistence.SaveConfigurationAsync(experimentId, Template(), []);
        using (
            var model = new SingleStimulusExperimentDialogViewModel(
                Route(experimentId),
                service,
                coordinator
            )
        )
        {
            model.Duration.DurationValue = 2;
            service.Block = true;
            var run = model.StartCommand.ExecuteAsync(null);
            await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.EmergencyStopCommand.ExecuteAsync(null);
            await run;
        }
        await using var db = fixture.Factory.CreateDbContext();
        var runs = await db.ExperimentRuns.ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.Single(runs, run => run.Status == ExperimentRunStatus.Completed);
        Assert.Single(runs, run => run.Status == ExperimentRunStatus.InterruptedByUser);
        Assert.All(
            runs,
            run =>
            {
                Assert.Equal(0, run.AcquisitionDurationMilliseconds);
                Assert.Equal(0, run.BlankingDurationMilliseconds);
                Assert.Equal(0, run.RecoveryDurationMilliseconds);
            }
        );
        Assert.Empty(await db.EegFiles.ToListAsync());
        Assert.All(
            await db.ExperimentEvents.ToListAsync(),
            item => Assert.Equal(ExperimentEventKind.Stimulation, item.Kind)
        );
        Assert.DoesNotContain(
            await db.ExperimentElectrodes.ToListAsync(),
            item => string.IsNullOrEmpty(item.SiteId)
        );
        var changed = Template();
        await persistence.SaveConfigurationAsync(
            experimentId,
            changed with
            {
                StimulusConfiguration = changed.StimulusConfiguration with { RampSeconds = 2 },
            },
            []
        );
        var historical = await persistence.LoadHistoricalRunAsync(runs[0].Id);
        Assert.Equal(7, historical.StimulusConfiguration.RampSeconds);
        Assert.Equal(ExperimentCreationMode.StimulusOnly, historical.CreationMode);
        Assert.Empty(historical.AcquisitionChannels);
        Assert.Equal(string.Empty, historical.HistoricalResult!.RawFilePath);
        var template = await persistence.LoadTemplateAsync(experimentId);
        Assert.Equal(ExperimentCreationMode.StimulusOnly, template.CreationMode);
        var serializer = new ExperimentPackageSerializer(
            StimulusCapabilityProfile.Default with
            {
                StimulusSiteIds = template.StimulusElectrodes.Select(x => x.SiteId).ToHashSet(),
            },
            ElectrodePositionCatalog.Default,
            fixture.Mapping,
            new ApplicationVersionProvider()
        );
        var file = Path.Combine(fixture.Directory, "single.expp");
        await serializer.WriteAsync(file, template);
        var imported = await serializer.ReadAsync(file);
        Assert.Equal(ExperimentCreationMode.StimulusOnly, imported.CreationMode);
        Assert.Empty(imported.AcquisitionChannels);
        Assert.Equal(template.Timing, imported.Timing);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        json.Remove("creationMode");
        var legacy = JsonSerializer.Deserialize<ExperimentPackageV1>(
            json.ToJsonString(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }
        );
        Assert.Equal(ExperimentCreationMode.AcquisitionAndStimulation, legacy!.CreationMode);
        await using var raw = new FileEegRawPacketStore(fixture.Directory);
        var export = new EegExportService(
            fixture.Factory,
            persistence,
            serializer,
            new NoReveal(),
            new EegArtifactFinalizer(),
            raw
        );
        var first = runs.Single(r => r.Status == ExperimentRunStatus.Completed);
        var result = await export.ExportAsync(
            first.Id,
            new EegExportSelection(false, false, true),
            fixture.Directory,
            revealResult: false
        );
        var exported = await serializer.ReadAsync(Assert.Single(result.Paths));
        Assert.Equal(
            first.StimulationDurationMilliseconds,
            exported.Timing.StimulationMilliseconds
        );
        Assert.Equal(7, exported.StimulusConfiguration.RampSeconds);
        var noEeg = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            export.ExportAsync(
                first.Id,
                new EegExportSelection(true, false, false),
                fixture.Directory,
                revealResult: false
            )
        );
        Assert.Contains("没有脑电采集数据", noEeg.Message);
    }

    [Fact]
    public async Task SharedDeviceServiceCompletesSingleStimulationOnSimulator()
    {
        await using var fixture = new DatabaseFixture();
        await using var runtime = new DeviceRuntime.DeviceRuntimeBuilder().UseSimulation().Build();
        await foreach (var candidate in runtime.Discovery.DiscoverAsync(TimeSpan.Zero))
            await runtime.ConnectVerifiedAsync(candidate);
        using var service = new DeviceExperimentRunService(runtime.Devices, fixture.Mapping);
        var source = Route();
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration with
            {
                RampSeconds = 0,
            },
            source.StimulusElectrodes,
            [],
            "",
            "",
            500,
            deviceId: DeviceSdk.DeviceId.Simulator.Value,
            creationMode: ExperimentCreationMode.StimulusOnly
        );
        using var model = new SingleStimulusExperimentDialogViewModel(route, service);
        model.Duration.SelectedUnit = model.Duration.Units[1];
        model.Duration.DurationValue = 1;
        await model.StartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(model.HasError, model.ErrorText);
        Assert.Equal("已结束", model.StatusText);
        Assert.Equal(TimeSpan.FromSeconds(1), model.Elapsed);
    }

    [Fact]
    public async Task ApplicationExitCancellationPreservesExitStatus()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        var service = new RunService { Block = true };
        using var model = new SingleStimulusExperimentDialogViewModel(
            Route(id),
            service,
            coordinator
        );
        var running = model.StartCommand.ExecuteAsync(null);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.RequestInterruptionAsync("application-exit");
        await running;
        await coordinator.FinishAsync(
            ExperimentRunStatus.InterruptedByExit,
            ExperimentIncidentKind.ApplicationExit
        );
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(
            ExperimentRunStatus.InterruptedByExit,
            (await db.ExperimentRuns.SingleAsync()).Status
        );
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task FailedStopKeepsApplicationExitProtectionUntilStopIsConfirmed()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        var service = new RunService { Block = true, StopFailure = new IOException("timeout") };
        using var model = new SingleStimulusExperimentDialogViewModel(
            Route(id),
            service,
            coordinator
        );
        var running = model.StartCommand.ExecuteAsync(null);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;
        Assert.True(coordinator.IsActive);
        Assert.False(model.CanClose);
        await using (var db = fixture.Factory.CreateDbContext())
            Assert.Contains(
                await db.ExperimentIncidents.ToListAsync(),
                x => x.Kind == ExperimentIncidentKind.DeviceFailure
            );
        service.StopFailure = null;
        await model.EmergencyStopCommand.ExecuteAsync(null);
        Assert.False(coordinator.IsActive);
        Assert.False(model.CanStart);
        Assert.True(model.IsFinished);
        Assert.True(model.CanClose);
    }

    internal static ExperimentRunRouteData Route(
        long databaseId = 0,
        HistoricalExperimentRunContext? history = null
    )
    {
        var source = ExperimentRunRouteDataDefaults.Create();
        return new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            [],
            "",
            "",
            500,
            experimentDatabaseId: databaseId,
            historicalResult: history,
            creationMode: ExperimentCreationMode.StimulusOnly
        );
    }

    private static ExperimentConfigurationTemplate Template()
    {
        var route = Route();
        var s = route.StimulusConfiguration;
        var configuration = new StimulusConfigurationSnapshot(
            s.Kind,
            s.ArrayMode,
            s.Direction,
            s.ShamMode,
            s.RampSeconds,
            s.Frequency,
            s.DutyPercent,
            s.Targets.Select(t => new StimulusTargetSnapshot(
                    t.TargetId,
                    t.DisplayOrder,
                    t.PeakCurrent,
                    t.FixedActivePhysicalChannelId,
                    t.Channels.Select(c => new StimulationPhysicalChannelSnapshot(
                            c.PhysicalChannelId,
                            c.Role,
                            c.Current
                        ))
                        .ToArray()
                ))
                .ToArray()
        );
        return new ExperimentConfigurationTemplate(
            configuration,
            route.StimulusElectrodes,
            [],
            "",
            "",
            500,
            new ExperimentTimingTemplate(ExperimentRunMode.Manual, 0, 0, 600000, 0, 1),
            "test",
            CreationMode: ExperimentCreationMode.StimulusOnly
        );
    }

    private static ExperimentEntity CreateExperiment() =>
        new()
        {
            ExperimentCode = Guid.NewGuid().ToString("N"),
            ScheduledAt = DateTimeOffset.UtcNow,
            Remarks = "",
            Operator = new OperatorEntity
            {
                Username = Guid.NewGuid().ToString(),
                NormalizedUsername = Guid.NewGuid().ToString(),
                PasswordSalt = [1],
                PasswordHash = [1],
                PasswordIterations = 1,
            },
            Subject = new SubjectEntity
            {
                SubjectCode = Guid.NewGuid().ToString(),
                NormalizedSubjectCode = Guid.NewGuid().ToString(),
            },
        };

    private sealed class NoReveal : IFileRevealService
    {
        public void Reveal(string path) { }
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        public string Directory { get; } =
            Path.Combine(Path.GetTempPath(), "single-stimulus-tests", Guid.NewGuid().ToString("N"));
        public Factory Factory { get; }
        public EegPhysicalChannelMappingService Mapping { get; }
        public ApplicationSessionState Session { get; } = new() { Id = Guid.NewGuid() };

        public DatabaseFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Factory = new Factory(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(Directory, "test.db")};Pooling=False")
                    .Options
            );
            Mapping = new EegPhysicalChannelMappingService(Path.Combine(Directory, "mapping.json"));
        }

        public async Task<long> InitializeAsync()
        {
            await using var db = Factory.CreateDbContext();
            await db.Database.MigrateAsync();
            var experiment = CreateExperiment();
            db.Experiments.Add(experiment);
            db.ApplicationSessions.Add(
                new ApplicationSessionEntity
                {
                    Id = Session.Id,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                }
            );
            await db.SaveChangesAsync();
            return experiment.Id;
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    internal sealed class RunService : IExperimentRunService
    {
        private EventHandler<ExperimentRunTelemetryEventArgs>? _telemetry;
        public int Subscribers { get; private set; }
        public event EventHandler<ExperimentRunTelemetryEventArgs>? TelemetryReceived
        {
            add
            {
                _telemetry += value;
                Subscribers++;
            }
            remove
            {
                _telemetry -= value;
                Subscribers--;
            }
        }
        public List<StimulationRunRequest> Requests { get; } = [];

        public void Progress(double seconds) =>
            _telemetry?.Invoke(
                this,
                new ExperimentRunTelemetryEventArgs(
                    new ExperimentRunTelemetry(
                        ExperimentRunStage.Stimulation,
                        TimeSpan.FromSeconds(seconds),
                        TimeSpan.FromSeconds(seconds),
                        0,
                        1,
                        1,
                        2,
                        0,
                        true,
                        [],
                        []
                    )
                )
            );

        public int Acquisitions,
            Automatic,
            Recordings,
            Stops;
        public bool Block;
        public Exception? StartFailure,
            StopFailure;
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAcquisitionAsync(
            AcquisitionRunRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Acquisitions++;
            return Task.CompletedTask;
        }

        public Task StartAutomaticExperimentAsync(
            AutomaticExperimentRunRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Automatic++;
            return Task.CompletedTask;
        }

        public Task BeginRecordingAsync(
            EegRecordingMetadata metadata,
            CancellationToken cancellationToken = default
        )
        {
            Recordings++;
            return Task.CompletedTask;
        }

        public async Task StartStimulationAsync(
            StimulationRunRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            if (StartFailure is not null)
                throw StartFailure;
            if (Block)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public Task StopCurrentOperationAsync(
            string deviceId,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task EmergencyStopAsync(
            string deviceId,
            CancellationToken cancellationToken = default
        )
        {
            Stops++;
            return StopFailure is null ? Task.CompletedTask : Task.FromException(StopFailure);
        }
    }
}
