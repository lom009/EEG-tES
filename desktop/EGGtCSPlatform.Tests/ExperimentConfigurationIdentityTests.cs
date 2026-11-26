using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed partial class SingleStimulusExperimentTests
{
    private static ExperimentConfigurationTemplate FullConfiguration() =>
        Template() with
        {
            CreationMode = ExperimentCreationMode.AcquisitionAndStimulation,
            AcquisitionChannels = ["C3", "C4"],
            ReferenceChannel = "FCz",
            GroundChannel = "AFz",
            PhysicalChannels = [new("C3", 1), new("C4", 2)],
            Timing = new(ExperimentRunMode.Automatic, 1000, 100, 2000, 100, 2),
        };

    private static ExperimentRunPreparation Preparation(
        long id,
        Guid run,
        ExperimentConfigurationTemplate c
    ) =>
        new(
            run,
            id,
            c.Timing.Mode.ToString(),
            c.Timing.CycleCount,
            c.SampleRateHz,
            TimeSpan.FromMilliseconds(c.Timing.AcquisitionMilliseconds),
            TimeSpan.FromMilliseconds(c.Timing.BlankingMilliseconds),
            TimeSpan.FromMilliseconds(c.Timing.StimulationMilliseconds),
            TimeSpan.FromMilliseconds(c.Timing.RecoveryMilliseconds),
            c
        );

    [Theory]
    [InlineData("site")]
    [InlineData("role")]
    [InlineData("channel")]
    [InlineData("acquisition")]
    [InlineData("reference")]
    [InlineData("ground")]
    [InlineData("mapping")]
    [InlineData("sample")]
    [InlineData("kind")]
    [InlineData("current")]
    [InlineData("ramp")]
    [InlineData("frequency")]
    [InlineData("direction")]
    [InlineData("envelope")]
    [InlineData("mode")]
    [InlineData("cycles")]
    [InlineData("creation-mode")]
    [InlineData("channel-current")]
    [InlineData("stimulus-role")]
    [InlineData("acquisition-time")]
    [InlineData("blanking-time")]
    [InlineData("stimulation-time")]
    [InlineData("recovery-time")]
    public async Task ChangedConfigurationForksSameDateAndPreservesOriginal(string change)
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        var service = new ExperimentPersistenceService(
            fixture.Factory,
            new CurrentOperatorContext(),
            fixture.Mapping
        );
        var c = FullConfiguration();
        await service.SaveConfigurationAsync(id, c, []);
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        var firstRun = Guid.NewGuid();
        await coordinator.PrepareAsync(Preparation(id, firstRun, c), "test");
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        var changed = change switch
        {
            "site" => c with
            {
                StimulusElectrodes = c
                    .StimulusElectrodes.Select((a, i) => i == 0 ? a with { SiteId = "F3" } : a)
                    .ToArray(),
            },
            "role" => c with
            {
                ReferenceChannel = c.GroundChannel,
                GroundChannel = c.ReferenceChannel,
            },
            "channel" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with
                {
                    Targets = c
                        .StimulusConfiguration.Targets.Select(t =>
                            t with
                            {
                                Channels = t
                                    .Channels.Select(ch =>
                                        ch with
                                        {
                                            PhysicalChannelId = ch.PhysicalChannelId + 2,
                                        }
                                    )
                                    .ToArray(),
                            }
                        )
                        .ToArray(),
                },
                StimulusElectrodes = c
                    .StimulusElectrodes.Select(a =>
                        a with
                        {
                            PhysicalChannelId = a.PhysicalChannelId + 2,
                        }
                    )
                    .ToArray(),
            },
            "acquisition" => c with { AcquisitionChannels = ["C3"] },
            "reference" => c with { ReferenceChannel = "F4" },
            "ground" => c with { GroundChannel = "F4" },
            "mapping" => c with { PhysicalChannels = [new("C3", 2), new("C4", 1)] },
            "sample" => c with { SampleRateHz = 1000 },
            "kind" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with { Kind = StimulusKind.TAcs },
            },
            "current" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with
                {
                    Targets = c
                        .StimulusConfiguration.Targets.Select(t => t with { PeakCurrent = 1 })
                        .ToArray(),
                },
            },
            "ramp" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with { RampSeconds = 3 },
            },
            "frequency" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with { Frequency = 25 },
            },
            "direction" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with
                {
                    Direction = StimulusDirection.Negative,
                },
            },
            "envelope" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with
                {
                    Kind = StimulusKind.EnvelopeTAcs,
                    Envelope = new(DelayMilliseconds: 123),
                },
            },
            "creation-mode" => c with { CreationMode = ExperimentCreationMode.StimulusOnly },
            "channel-current" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with
                {
                    Targets = c
                        .StimulusConfiguration.Targets.Select(t =>
                            t with
                            {
                                Channels = t
                                    .Channels.Select(ch => ch with { Current = 1 })
                                    .ToArray(),
                            }
                        )
                        .ToArray(),
                },
            },
            "stimulus-role" => c with
            {
                StimulusConfiguration = c.StimulusConfiguration with
                {
                    Targets = c
                        .StimulusConfiguration.Targets.Select(t =>
                            t with
                            {
                                Channels = t
                                    .Channels.Select(ch =>
                                        ch with
                                        {
                                            Role =
                                                ch.Role == StimulationChannelRole.FixedActive
                                                    ? StimulationChannelRole.Selectable
                                                    : StimulationChannelRole.FixedActive,
                                        }
                                    )
                                    .ToArray(),
                            }
                        )
                        .ToArray(),
                },
                StimulusElectrodes = c
                    .StimulusElectrodes.Select(a =>
                        a with
                        {
                            Role =
                                a.Role == StimulationChannelRole.FixedActive
                                    ? StimulationChannelRole.Selectable
                                    : StimulationChannelRole.FixedActive,
                        }
                    )
                    .ToArray(),
            },
            "mode" => c with { Timing = c.Timing with { Mode = ExperimentRunMode.Manual } },
            "cycles" => c with { Timing = c.Timing with { CycleCount = 3 } },
            "acquisition-time" => c with
            {
                Timing = c.Timing with { AcquisitionMilliseconds = 2000 },
            },
            "blanking-time" => c with { Timing = c.Timing with { BlankingMilliseconds = 200 } },
            "stimulation-time" => c with
            {
                Timing = c.Timing with { StimulationMilliseconds = 3000 },
            },
            _ => c with { Timing = c.Timing with { RecoveryMilliseconds = 200 } },
        };
        var saved = await service.SaveConfigurationAsync(id, changed, []);
        Assert.True(saved.CreatedNew);
        Assert.Equal("EXP-20261012-002", saved.ExperimentCode);
        var again = await service.SaveConfigurationAsync(saved.ExperimentId, changed, []);
        Assert.Equal(saved.ExperimentId, again.ExperimentId);
        Assert.False(again.CreatedNew);
        var secondRun = Guid.NewGuid();
        var prepared = await coordinator.PrepareAsync(
            Preparation(saved.ExperimentId, secondRun, changed),
            "test"
        );
        Assert.Equal(saved.ExperimentId, prepared.ExperimentId);
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        var original = await service.LoadHistoricalRunAsync(firstRun);
        var second = await service.LoadHistoricalRunAsync(secondRun);
        Assert.Equal(c.StimulusElectrodes, original.StimulusElectrodes);
        Assert.Equal(c.Timing, original.ImportedTiming);
        Assert.Equal(changed.StimulusElectrodes, second.StimulusElectrodes);
        Assert.Equal(changed.Timing, second.ImportedTiming);
        Assert.Equal(2, (await service.ListHistoryAsync()).Count);
    }

    [Fact]
    public async Task EquivalentConfigurationReusesNumberButFinalTimingAndLegacyRunsFork()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        var service = new ExperimentPersistenceService(
            fixture.Factory,
            new CurrentOperatorContext(),
            fixture.Mapping
        );
        var c = FullConfiguration();
        await service.SaveConfigurationAsync(id, c, []);
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        var first = Guid.NewGuid();
        await coordinator.PrepareAsync(Preparation(id, first, c), "test");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveConfigurationAsync(id, c, [])
        );
        await using var competing = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            competing.PrepareAsync(Preparation(id, Guid.NewGuid(), c), "test")
        );
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        var newGuid = Guid.NewGuid();
        var equivalent = c with
        {
            SourceDisplayName = "renamed",
            AcquisitionChannels = c.AcquisitionChannels.Reverse().ToArray(),
            PhysicalChannels = c.PhysicalChannels!.Reverse().ToArray(),
            StimulusConfiguration = c.StimulusConfiguration with
            {
                Targets = c
                    .StimulusConfiguration.Targets.Select(t =>
                        t with
                        {
                            TargetId = newGuid,
                            Channels = t.Channels.Reverse().ToArray(),
                        }
                    )
                    .ToArray(),
            },
            StimulusElectrodes = c
                .StimulusElectrodes.Reverse()
                .Select(a => a with { TargetId = newGuid })
                .ToArray(),
        };
        Assert.False((await service.SaveConfigurationAsync(id, equivalent, [])).CreatedNew);
        Assert.Equal(
            id,
            (
                await coordinator.PrepareAsync(Preparation(id, Guid.NewGuid(), equivalent), "test")
            ).ExperimentId
        );
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        var changed = c with { Timing = c.Timing with { CycleCount = 4 } };
        var result = await coordinator.PrepareAsync(
            Preparation(id, Guid.NewGuid(), changed),
            "test"
        );
        Assert.Equal("EXP-20261012-002", result.ExperimentCode);
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var run = await db.ExperimentRuns.SingleAsync(r => r.Id == first);
            run.ConfigurationJson = null;
            await db.SaveChangesAsync();
        }
        Assert.NotNull(
            (await service.LoadHistoricalRunAsync(first)).HistoricalResult!.ConfigurationWarning
        );
        var legacy = await service.SaveConfigurationAsync(id, c, []);
        Assert.Equal("EXP-20261012-003", legacy.ExperimentCode);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            (await db.ExperimentRuns.SingleAsync(r => r.Id == first)).ConfigurationJson = "broken";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadHistoricalRunAsync(first));
        var count = (await service.ListHistoryAsync()).Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveConfigurationAsync(id, c with { StimulusElectrodes = [] }, [])
        );
        Assert.Equal(count, (await service.ListHistoryAsync()).Count);
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal(3, await check.Experiments.CountAsync());
    }

    [Fact]
    public async Task FinalPreparationFailureRollsBackIdentityAndRunTogether()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        var c = FullConfiguration();
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        await coordinator.PrepareAsync(Preparation(id, Guid.NewGuid(), c), "test");
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        var failedRun = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.PrepareAsync(
                Preparation(id, failedRun, c with { StimulusElectrodes = [] }),
                "test"
            )
        );
        Assert.False(coordinator.IsActive);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(1, await db.Experiments.CountAsync());
        Assert.False(await db.ExperimentRuns.AnyAsync(r => r.Id == failedRun));
    }

    [Fact]
    public async Task ForkSkipsOccupiedNumbersAndConcurrentPreparationsCannotShareAGroup()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        var c = FullConfiguration();
        await using var first = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        await first.PrepareAsync(Preparation(id, Guid.NewGuid(), c), "test");
        await first.FinishAsync(ExperimentRunStatus.Completed);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var occupied = CreateExperiment();
            occupied.ExperimentCode = "EXP-20261012-009";
            occupied.Subject = null!;
            occupied.SubjectId = (await db.Experiments.SingleAsync()).SubjectId;
            db.Experiments.Add(occupied);
            await db.SaveChangesAsync();
        }
        var service = new ExperimentPersistenceService(
            fixture.Factory,
            new CurrentOperatorContext(),
            fixture.Mapping
        );
        var changed = c with { Timing = c.Timing with { CycleCount = 5 } };
        var result = await service.SaveConfigurationAsync(id, changed, []);
        Assert.Equal("EXP-20261012-010", result.ExperimentCode);
        await using var second = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        var outcomes = await Task.WhenAll(
            new[] { first, second }.Select(async coordinator =>
            {
                try
                {
                    await coordinator.PrepareAsync(
                        Preparation(result.ExperimentId, Guid.NewGuid(), changed),
                        "test"
                    );
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })
        );
        Assert.Single(outcomes, success => success);
        if (first.IsActive)
            await first.FinishAsync(ExperimentRunStatus.Completed);
        if (second.IsActive)
            await second.FinishAsync(ExperimentRunStatus.Completed);
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Single(
            await check
                .ExperimentRuns.Where(r => r.ExperimentId == result.ExperimentId)
                .ToArrayAsync()
        );
    }

    [Fact]
    public async Task SnapshotHistoryAndExportIgnoreOverwrittenParentConfiguration()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        var c = FullConfiguration();
        var run = Guid.NewGuid();
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        await coordinator.PrepareAsync(Preparation(id, run, c), "test");
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var experiment = await db
                .Experiments.Include(e => e.StimulusParadigm)
                .Include(e => e.Electrodes)
                .SingleAsync();
            // Emulate legacy data already overwritten by a later configuration.
            experiment.CreationMode = ExperimentCreationMode.StimulusOnly;
            experiment.StimulusParadigm!.Kind = "EnvelopeTAcs";
            db.ExperimentElectrodes.RemoveRange(experiment.Electrodes);
            await db.SaveChangesAsync();
        }
        var service = new ExperimentPersistenceService(
            fixture.Factory,
            new CurrentOperatorContext(),
            fixture.Mapping
        );
        var history = await service.LoadHistoricalRunAsync(run);
        Assert.Equal(c.CreationMode, history.CreationMode);
        Assert.Equal(c.StimulusElectrodes, history.StimulusElectrodes);
        Assert.Equal("C3", history.HistoricalResult!.PhysicalChannelNames[1]);
        var packages = new CapturingConfigurationPackage();
        await using var raw = new FileEegRawPacketStore(fixture.Directory);
        var export = new EegExportService(
            fixture.Factory,
            service,
            packages,
            new NoReveal(),
            new EegArtifactFinalizer(),
            raw
        );
        await export.ExportAsync(
            run,
            new(false, false, true),
            fixture.Directory,
            revealResult: false
        );
        Assert.Equal(
            ExperimentConfigurationIdentity.Key(c),
            ExperimentConfigurationIdentity.Key(packages.Saved!)
        );
        await using (var db = fixture.Factory.CreateDbContext())
        {
            (await db.ExperimentRuns.SingleAsync()).ConfigurationJson = "{}";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            export.ExportAsync(run, new(false, false, true), fixture.Directory, revealResult: false)
        );
    }

    private sealed class CapturingConfigurationPackage : IExperimentPackageSerializer
    {
        public ExperimentConfigurationTemplate? Saved { get; private set; }

        public Task<ExperimentConfigurationTemplate> ReadAsync(
            string path,
            System.Threading.CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async Task WriteAsync(
            string path,
            ExperimentConfigurationTemplate template,
            System.Threading.CancellationToken cancellationToken = default
        )
        {
            Saved = template;
            await File.WriteAllTextAsync(path, "{}", cancellationToken);
        }
    }

    [Fact]
    public async Task ReturningToStartAllowsModeChangeAndForkedDraftKeepsOriginalDate()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        var currentOperator = new CurrentOperatorContext();
        string subject;
        DateTimeOffset scheduled;
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var experiment = await db.Experiments.Include(e => e.Subject).SingleAsync();
            subject = experiment.Subject.SubjectCode;
            experiment.Subject.NormalizedSubjectCode = SubjectLookupService.NormalizeSubject(
                subject
            );
            scheduled = experiment.ScheduledAt;
            currentOperator.Set(experiment.OperatorId, "test");
            await db.SaveChangesAsync();
        }
        var service = new ExperimentPersistenceService(
            fixture.Factory,
            currentOperator,
            fixture.Mapping
        );
        var c = FullConfiguration();
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        await coordinator.PrepareAsync(Preparation(id, Guid.NewGuid(), c), "test");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateOrUpdateDraftAsync(id, scheduled, subject, "")
        );
        await coordinator.FinishAsync(ExperimentRunStatus.Completed);
        var resumed = await service.CreateOrUpdateDraftAsync(id, scheduled, subject, "");
        Assert.Equal(id, resumed.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateOrUpdateDraftAsync(id, scheduled, subject, "changed remarks")
        );
        var fork = await service.SaveConfigurationAsync(
            resumed.Id,
            c with
            {
                CreationMode = ExperimentCreationMode.AcquisitionOnly,
            },
            []
        );
        Assert.Equal("EXP-20261012-002", fork.ExperimentCode);
        var editedDraft = await service.CreateOrUpdateDraftAsync(
            fork.ExperimentId,
            scheduled.AddDays(2),
            subject,
            "draft remarks"
        );
        Assert.Equal(fork.ExperimentCode, editedDraft.ExperimentCode);
        Assert.Equal(fork.ExperimentId, editedDraft.Id);
    }

    [Fact]
    public async Task SingleStimulusStartUpdatesIdentityOnlyAfterCommittedPreparation()
    {
        await using var fixture = new DatabaseFixture();
        var id = await fixture.InitializeAsync();
        await using var coordinator = new ExperimentRunPersistenceCoordinator(
            fixture.Factory,
            fixture.Session
        );
        var service = new RunService();
        using (
            var first = new EGGtCSPlatform.ViewModels.SingleStimulusExperimentDialogViewModel(
                Route(id),
                service,
                coordinator
            )
        )
        {
            await first.StartCommand.ExecuteAsync(null);
            Assert.Equal(id, first.Route.ExperimentDatabaseId);
            Assert.Equal("EXP-20261012-001", first.Route.ExperimentId);
        }
        // Missing application-session FK forces SaveChanges to fail after resolving a new number.
        await using (
            var invalid = new ExperimentRunPersistenceCoordinator(
                fixture.Factory,
                new ApplicationSessionState { Id = Guid.NewGuid() }
            )
        )
        {
            var blockedDevice = new RunService();
            using var failed =
                new EGGtCSPlatform.ViewModels.SingleStimulusExperimentDialogViewModel(
                    Route(id),
                    blockedDevice,
                    invalid
                );
            failed.Duration.DurationValue = 2;
            await failed.StartCommand.ExecuteAsync(null);
            Assert.Empty(blockedDevice.Requests);
            Assert.Equal(id, failed.Route.ExperimentDatabaseId);
            Assert.Equal(EGGtCSPlatform.ViewModels.SingleStimulusRunState.Failed, failed.State);
        }
        using var changed = new EGGtCSPlatform.ViewModels.SingleStimulusExperimentDialogViewModel(
            Route(id),
            service,
            coordinator
        );
        changed.Duration.DurationValue = 2;
        await changed.StartCommand.ExecuteAsync(null);
        Assert.NotEqual(id, changed.Route.ExperimentDatabaseId);
        Assert.Equal("EXP-20261012-002", changed.Route.ExperimentId);
        using var same = new EGGtCSPlatform.ViewModels.SingleStimulusExperimentDialogViewModel(
            changed.Route,
            service,
            coordinator
        );
        await same.StartCommand.ExecuteAsync(null);
        Assert.Equal(changed.Route.ExperimentDatabaseId, same.Route.ExperimentDatabaseId);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await db.Experiments.CountAsync());
        var runs = await db
            .ExperimentRuns.Where(r => r.ExperimentId == changed.Route.ExperimentDatabaseId)
            .ToArrayAsync();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, r => Assert.NotNull(r.ConfigurationJson));
    }
}
