using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Dialog;
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

public sealed class ToleranceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ZeroAndLowCurrentSnapshotsCanBeSavedAndRead(int returns)
    {
        using var database = await Database.Create();
        var route = Route(returns: returns);
        using var model = new ToleranceCurrentModel(route, Capability, protocolStep: true);
        Assert.Equal(0, model.Current);
        Assert.All(model.Configuration.Targets[0].Channels, c => Assert.Equal(0, c.Current));
        Assert.Equal(2, model.MaximumConfiguration.Targets[0].PeakCurrent);
        var maximumReturns = model
            .MaximumConfiguration.Targets[0]
            .Channels.Where(c => c.Role == StimulationChannelRole.Selectable)
            .ToArray();
        Assert.Equal(2, maximumReturns.Sum(c => c.Current), 8);
        if (returns == 4)
            Assert.Equal(new[] { .52, .52, .48, .48 }, maximumReturns.Select(c => c.Current));
        foreach (var current in new[] { 0d, .04, .08, .12 })
        {
            model.SetCurrent(current);
            Assert.Equal(
                current,
                model
                    .Configuration.Targets[0]
                    .Channels.Where(c => c.Role == StimulationChannelRole.Selectable)
                    .Sum(c => c.Current),
                8
            );
            var saved = await database.Service.SaveAsync(
                route,
                new ToleranceSaveData(current, false, model.Configuration)
            );
            Assert.Equal(current, saved.Current);
        }
        var results = Assert.Single((await database.Service.QueryAsync(new())).Groups).Results;
        Assert.Equal(4, results.Count);
        Assert.Contains(
            results,
            r =>
                r.Current == 0
                && r.AppliedConfiguration.Targets[0].Channels.All(c => c.Current == 0)
        );
    }

    internal static StimulusCapabilityProfile Capability =>
        StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions
            {
                FixedActivePhysicalChannelIds = [1, 8],
                MultiTargetEnabled = true,
                PhysicalChannelCount = 16,
                AllowedElectrodeSiteIds = Enumerable.Range(1, 16).Select(i => $"S{i}").ToArray(),
            }
        );

    [Theory]
    [InlineData(StimulusKind.TDcs)]
    [InlineData(StimulusKind.TAcs)]
    [InlineData(StimulusKind.TRns)]
    [InlineData(StimulusKind.TPcs)]
    [InlineData(StimulusKind.Sham)]
    public void ConfigurationLocksToleranceMaximumAndPreservesMultiTargetDefaults(StimulusKind kind)
    {
        var model = new StimulusModeConfigurationViewModel(kind, Capability, lockCurrent: true);
        Assert.False(model.CanEditCurrent);
        Assert.Equal(2, model.Current);
        model.Current = 1;
        Assert.Equal(2, model.Current);
        foreach (var array in model.ArrayOptions)
        {
            model.SelectedArrayOption = array;
            var expected = model.ArrayMode == StimulusArrayMode.MultiTarget ? .16 : 2;
            Assert.Equal(expected, model.Current);
            foreach (var target in model.Targets)
            {
                target.PeakCurrent = 2;
                Assert.Equal(expected, target.PeakCurrent);
                foreach (var channel in target.Channels)
                {
                    var before = channel.Current;
                    channel.Current = 2;
                    Assert.Equal(before, channel.Current);
                    Assert.False(channel.CanEditCurrent);
                }
            }
            if (model.ArrayMode == StimulusArrayMode.MultiTarget)
            {
                model.AddTargetCommand.Execute(null);
                Assert.Equal(.16, model.Targets[1].PeakCurrent);
                Assert.False(model.Targets[1].CanEditCurrent);
            }
        }
    }

    [Fact]
    public async Task ConfigurationExcludesEnvelopeAndPropagatesContext()
    {
        var router = new Router();
        var context = new ToleranceTestContext("SUB", "note");
        var model = new StimulusConfigurationPageViewModel(
            new(
                "耐受度",
                "SUB",
                CreationMode: ExperimentCreationMode.StimulusOnly,
                Tolerance: context
            ),
            router,
            new DialogService(() => null),
            router,
            Capability
        );
        Assert.Equal(5, model.Modes.Count);
        Assert.DoesNotContain(model.Modes, m => m.Kind == StimulusKind.EnvelopeTAcs);
        await model.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.Equal(context, router.Electrodes!.Tolerance);
        Assert.Equal(0, router.Electrodes.ExperimentDatabaseId);
        Assert.Equal(ExperimentCreationMode.StimulusOnly, router.Electrodes.CreationMode);
        using var electrodes = new ElectrodeConfigurationPageViewModel(
            router.Electrodes,
            router,
            new DialogService(() => null),
            router,
            Capability
        );
        Assert.True(electrodes.IsStimulusOnly);
        Assert.DoesNotContain(
            electrodes.Points,
            p =>
                p.Role
                    is ElectrodeRole.Acquisition
                        or ElectrodeRole.Ground
                        or ElectrodeRole.Reference
        );
    }

    [Theory]
    [InlineData(1, 1, 0, 2)]
    [InlineData(1, 4, 0, 2)]
    [InlineData(2, 4, .16, 1)]
    public void SessionBalancesChannelsClampsAndResets(
        int targets,
        int returns,
        double minimum,
        double maximum
    )
    {
        using var session = new ToleranceSimulationSession(Route(targets, returns), Capability);
        Assert.Equal(minimum, session.Current);
        Assert.Equal(maximum, session.Maximum);
        for (int i = 0; i < 55; i++)
        {
            session.Adjust(1);
            Assert.All(
                session.Configuration.Targets,
                t =>
                {
                    Assert.Equal(session.Current, t.PeakCurrent);
                    Assert.Equal(
                        t.PeakCurrent,
                        t.Channels.Where(c => c.Role == StimulationChannelRole.Selectable)
                            .Sum(c => c.Current),
                        8
                    );
                    Assert.All(
                        t.Channels,
                        c =>
                            Assert.True(
                                c.Current == 0
                                    || Capability.CurrentPolicy.IsValidMilliAmps(c.Current)
                            )
                    );
                }
            );
        }
        Assert.Equal(maximum, session.Current);
        session.Reset();
        Assert.Equal(minimum, session.Current);
        session.Adjust(-50);
        Assert.Equal(minimum, session.Current);
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Adjust(1));
    }

    [Fact]
    public async Task RealResultKeepsConfirmedCurrentConfigurationAndMode()
    {
        using var database = await Database.Create();
        var route = Route();
        using var model = new ToleranceCurrentModel(route, Capability, protocolStep: true);
        model.SetCurrent(.12);
        var data = new ToleranceSaveData(model.Current, false, model.Configuration);
        var saved = await database.Service.SaveAsync(route, data);
        Assert.False(saved.IsSimulated);
        Assert.Equal(.12, saved.Current);
        var loaded = Assert.Single(
            Assert.Single((await database.Service.QueryAsync(new())).Groups).Results
        );
        Assert.False(loaded.IsSimulated);
        Assert.Equal(.12, loaded.AppliedConfiguration.Targets[0].PeakCurrent);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Service.SaveAsync(route, data with { Current = .16 })
        );
    }

    [Fact]
    public async Task MigrationGroupingSnapshotsFilteringAndRestart()
    {
        using var f = await Database.Create();
        var route = Route();
        var first = await f.Service.SaveAsync(route, .08);
        // A new configuration flow has new GUIDs and notes but is the same group.
        var second = await f.Service.SaveAsync(
            Route() with
            {
                Context = new(" subject ", "第二次"),
            },
            .12
        );
        var same = await f.Service.SaveAsync(route, .12);
        Assert.NotEqual(second.Id, same.Id);
        var history = await f.Service.QueryAsync(new());
        var group = Assert.Single(history.Groups);
        Assert.Equal(3, group.Count);
        Assert.Equal(.08, group.Results.Single(r => r.Id == first.Id).Current);
        Assert.All(group.Results, r => Assert.True(r.IsSimulated));
        Assert.Equal(2, group.Latest.AppliedConfiguration.Targets[0].Channels.Count);
        await using (var db = f.CreateDbContext())
        {
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Experiments.ToListAsync());
            Assert.Single(await db.Subjects.ToListAsync());
            Assert.Equal(3, await db.Set<ToleranceSnapshotEntity>().CountAsync());
            var old = await db.Set<ToleranceSnapshotEntity>().SingleAsync(r => r.Id == first.Id);
            old.SavedAtUtcTicks = DateTimeOffset.Now.AddDays(-2).UtcTicks;
            await db.SaveChangesAsync();
        }
        var today = await f.Service.QueryAsync(
            new(StartDate: DateTime.Today, EndDate: DateTime.Today)
        );
        Assert.Equal(2, Assert.Single(today.Groups).Count);
        var yesterday = await f.Service.QueryAsync(new(EndDate: DateTime.Today.AddDays(-1)));
        Assert.Single(Assert.Single(yesterday.Groups).Results);
        Assert.Empty((await f.Service.QueryAsync(new(Kind: StimulusKind.TAcs))).Groups);
        Assert.Empty((await f.Service.QueryAsync(new(SubjectId: "missing"))).Groups);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            f.Service.QueryAsync(
                new(StartDate: DateTime.Today, EndDate: DateTime.Today.AddDays(-1))
            )
        );
        for (var i = 0; i < 11; i++)
            await f.Service.SaveAsync(
                route with
                {
                    Configuration = route.Configuration with { RampSeconds = i + 1 },
                },
                .04
            );
        await f.Service.SaveAsync(route with { Context = new("another", "") }, .04);
        var restarted = new TolerancePersistenceService(f, f.Operator, Capability);
        var page = await restarted.QueryAsync(new(Page: 2));
        Assert.Equal(13, page.TotalCount);
        Assert.Equal(3, page.Groups.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.SaveAsync(route, .05));
        await using var reopened = f.CreateDbContext();
        Assert.Equal(15, await reopened.Set<ToleranceSnapshotEntity>().CountAsync());
    }

    [Fact]
    public async Task SaveFailurePreservesCurrentAndRetryAddsOnlySuccessfulSnapshot()
    {
        var storage = new ControlledStorage();
        using var model = new ToleranceTestPageViewModel(
            Route(),
            new ToleranceSessionFactory(Capability),
            storage
        );
        model.IncreaseCommand.Execute(null);
        var save = model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.CanAdjust);
        Assert.False(await model.PrepareToLeaveAsync());
        storage.Completion.SetException(new IOException("test"));
        await save;
        Assert.Empty(model.Records);
        Assert.Equal(.04, model.Current);
        Assert.Contains("保存失败", model.StatusText);
        storage.Completion = new();
        save = model.SaveCommand.ExecuteAsync(null);
        storage.Completion.SetResult(
            new(
                Guid.NewGuid(),
                DateTimeOffset.Now,
                1,
                "operator",
                .08,
                true,
                Route(),
                Route().Configuration
            )
        );
        await save;
        Assert.Single(model.Records);
        model.ResetCommand.Execute(null);
        Assert.Equal(0, model.Current);
        Assert.Single(model.Records);
        Assert.True(await model.PrepareToLeaveAsync());
        model.OnNavigatedFrom();
        Assert.False(model.SaveCommand.CanExecute(null));
        model.Resume();
        Assert.True(model.SaveCommand.CanExecute(null));
        Assert.Equal(0, model.Current);
        Assert.Empty(model.Records);
        model.Dispose();
        model.Resume();
        Assert.False(model.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SubjectDialogValidatesAndGeneratesWithoutWritingDrafts()
    {
        using var f = await Database.Create();
        var persistence = new ExperimentPersistenceService(f, f.Operator, null!);
        var model = new ToleranceSubjectDialogViewModel(persistence, new SubjectLookupService(f));
        model.ConfirmCommand.Execute(null);
        Assert.False(model.Confirmed);
        Assert.NotEmpty(model.ErrorText);
        await model.GenerateCommand.ExecuteAsync(null);
        Assert.Matches("^[0-9]{8}$", model.SubjectId);
        model.Remarks = " note ";
        model.ConfirmCommand.Execute(null);
        Assert.True(model.Confirmed);
        Assert.Equal("note", model.Remarks);
        await using var db = f.CreateDbContext();
        Assert.Empty(await db.Subjects.ToListAsync());
        Assert.Empty(await db.Experiments.ToListAsync());
    }

    internal static ToleranceTestRouteData Route(int targets = 1, int returns = 1)
    {
        var items = Enumerable
            .Range(1, targets)
            .Select(n => new StimulusTargetSnapshot(Guid.NewGuid(), n, .04, (n - 1) * 7 + 1, []))
            .ToArray();
        var electrodes = items
            .SelectMany(t =>
                Enumerable
                    .Range(0, returns + 1)
                    .Select(i => new StimulusElectrodeAssignment(
                        $"S{(t.DisplayOrder - 1) * 7 + i + 1}",
                        t.TargetId,
                        (t.DisplayOrder - 1) * 7 + i + 1,
                        i == 0
                            ? StimulationChannelRole.FixedActive
                            : StimulationChannelRole.Selectable
                    ))
            )
            .ToArray();
        return new(
            new("subject", "note"),
            new(
                StimulusKind.TDcs,
                targets > 1 ? StimulusArrayMode.MultiTarget
                    : returns == 1 ? StimulusArrayMode.DualChannel
                    : StimulusArrayMode.Hd,
                StimulusDirection.Positive,
                ShamWaveformMode.Direct,
                15,
                40,
                79,
                items
            ),
            electrodes,
            electrodes
                .Select(e => new ImpedanceSnapshotValue(
                    e.SiteId,
                    ImpedanceMeasurementKind.Stimulation,
                    5,
                    true,
                    DateTimeOffset.UtcNow
                ))
                .ToArray()
        );
    }

    private sealed class ControlledStorage : ITolerancePersistenceService
    {
        public TaskCompletionSource<ToleranceResultSnapshot> Completion = new();

        public Task<ToleranceResultSnapshot> SaveAsync(
            ToleranceTestRouteData route,
            double current,
            CancellationToken cancellationToken = default
        ) => Completion.Task;

        public Task<ToleranceHistoryPage> QueryAsync(
            ToleranceHistoryQuery query,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ToleranceHistoryPage([], 0));
    }

    private sealed class Database : IDbContextFactory<AppDbContext>, IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"tolerance-{Guid.NewGuid():N}.db"
        );
        public CurrentOperatorContext Operator { get; } = new();
        public ITolerancePersistenceService Service =>
            new TolerancePersistenceService(this, Operator, Capability);

        public AppDbContext CreateDbContext() =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={_path};Pooling=False")
                    .Options
            );

        public static async Task<Database> Create()
        {
            var f = new Database();
            await using var db = f.CreateDbContext();
            await db.GetService<IMigrator>().MigrateAsync("20261022090000_AddSpeechExperiments");
            var op = new OperatorEntity
            {
                Username = "test",
                NormalizedUsername = "TEST",
                PasswordSalt = [1],
                PasswordHash = [1],
                PasswordIterations = 1,
            };
            db.Operators.Add(op);
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            f.Operator.Set(op.Id, op.Username);
            return f;
        }

        public void Dispose() => File.Delete(_path);
    }

    private sealed class Router : INavigationRouter, IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
        public ElectrodeConfigurationRouteData? Electrodes;

        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData route) { }

        public void Navigate(StimulusConfigurationRouteData route) { }

        public void Navigate(ElectrodeConfigurationRouteData route) => Electrodes = route;

        public void Navigate(ExperimentRunRouteData route) { }

        public void Navigate(ExperimentRerunRouteData route) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
