using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SpeechExperimentTests
{
    [Fact]
    public async Task BackLocksDuringStartAndRecoversOnlyAfterFailedFirstStartOrExperimentEnd()
    {
        var service = new NavigationTestService();
        var router = new Router();
        using var page = new EnvelopeStimulationPageViewModel(
            Route(), router,
            () => new(new TinyCorpus(), new ControllableSpeechAudioPlayer()),
            experiments: service);
        Assert.True(page.CanGoBack);
        Assert.True(page.BackCommand.CanExecute(null));
        var notifications = 0;
        var commandNotifications = 0;
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(page.CanGoBack))
                notifications++;
        };
        page.BackCommand.CanExecuteChanged += (_, _) => commandNotifications++;
        var starting = page.StartCommand.ExecuteAsync(null);
        Assert.False(starting.IsCompleted);
        Assert.False(page.CanGoBack);
        Assert.False(page.BackCommand.CanExecute(null));
        page.BackCommand.Execute(null);
        Assert.Equal(0, router.BackCount);
        Assert.True(notifications > 0);
        Assert.True(commandNotifications > 0);
        service.SaveGate.SetException(new IOException("checkpoint failed"));
        await starting;
        Assert.True(page.CanGoBack);
        Assert.True(page.BackCommand.CanExecute(null));

        service.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SaveGate.SetResult();
        await page.StartCommand.ExecuteAsync(null);
        Assert.False(page.CanGoBack);
        service.FailEnd = true;
        Assert.False(await page.FinishAsync(true));
        Assert.False(page.CanGoBack);
        Assert.False(page.BackCommand.CanExecute(null));
        service.FailEnd = false;
        Assert.True(await page.FinishAsync(true));
        Assert.True(page.CanGoBack);
        Assert.True(page.BackCommand.CanExecute(null));
        page.BackCommand.Execute(null);
        Assert.Equal(1, router.BackCount);
    }

    private sealed class NavigationTestService : ISpeechExperimentService
    {
        public TaskCompletionSource SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailEnd;
        public Task SaveRoundAsync(Guid experimentId, SpeechExperimentSource source,
            SpeechTrainingRound round, CancellationToken token = default) => SaveGate.Task;
        public Task EndAsync(Guid experimentId, ExperimentRunStatus status,
            CancellationToken token = default, SpeechExperimentSource? source = null) =>
            FailEnd ? Task.FromException(new IOException("end failed")) : Task.CompletedTask;
        public Task<SpeechExperimentResult> LoadAsync(Guid experimentId,
            CancellationToken token = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ThreeRoundsRemainInOneExperimentAndPersistAcrossReload()
    {
        using var fixture = await Fixture.Create();
        var router = new Router();
        using var page = new EnvelopeStimulationPageViewModel(
            fixture.Route,
            router,
            () => new(new TinyCorpus(), new ControllableSpeechAudioPlayer()),
            experiments: fixture.Service
        );
        page.DelayMilliseconds = 20;
        await page.StartCommand.ExecuteAsync(null);
        Assert.False(page.CanConfigure);
        page.DelayMilliseconds = 90;
        page.SpeechRate = 4;
        page.SelectedCarrier = page.CarrierOptions[1];
        Assert.Equal(20, page.DelayMilliseconds);
        Assert.Equal(0, page.SpeechRate);
        Assert.Equal(page.CarrierOptions[0], page.SelectedCarrier);
        page.VolumePercent = 30;
        Assert.Equal(30, page.VolumePercent);
        for (var i = 0; i < 2; i++)
        {
            page.AllCorrectCommand.Execute(null);
            await page.SaveNextCommand.ExecuteAsync(null);
        }
        Assert.True(page.IsEnded);
        Assert.Null(router.Result);
        Assert.False(page.CanGoBack);
        Assert.True(page.AgainCommand.CanExecute(null));
        await page.AgainCommand.ExecuteAsync(null);
        Assert.False(page.CanGoBack);
        Assert.True(page.CanConfigure);
        Assert.Equal(20, page.DelayMilliseconds);
        page.DelayMilliseconds = 80;
        Assert.True(page.StartCommand.CanExecute(null), page.StatusText);
        await page.StartCommand.ExecuteAsync(null);
        Assert.Equal(2, page.ExperimentSnapshot().Rounds.Count);
        Assert.True(!page.CanConfigure && !page.IsEnded, page.StatusText);
        page.AllWrongCommand.Execute(null);
        await page.SaveNextCommand.ExecuteAsync(null);
        Assert.True(await page.FinishAsync(false));
        Assert.Null(router.Result);
        await page.AgainCommand.ExecuteAsync(null);
        page.SelectedMode = page.ModeOptions[1];
        page.DelayMilliseconds = 120;
        Assert.True(page.StartCommand.CanExecute(null), page.StatusText);
        await page.StartCommand.ExecuteAsync(null);
        Assert.True(!page.CanConfigure && !page.IsEnded, page.StatusText);
        Assert.True(await page.FinishAsync(true));
        var result = await fixture.Service.LoadAsync(router.Result!.ExperimentId);
        Assert.Equal(3, result.Rounds.Count);
        Assert.Equal(
            new AdaptiveScoringRule(7, 4),
            result.Rounds[2].Result.Settings.AdaptiveScoring
        );
        Assert.Equal(new[] { 20d, 80d, 120d }, result.Rounds.Select(x => x.DelayMilliseconds));
        Assert.Equal(
            new[]
            {
                SpeechRoundStatus.Completed,
                SpeechRoundStatus.Interrupted,
                SpeechRoundStatus.Interrupted,
            },
            result.Rounds.Select(x => x.Status)
        );
        Assert.Equal(new[] { 2, 1, 0 }, result.Rounds.Select(x => x.Result.Sentences.Count));
        Assert.Equal(500, result.Source.Stimulation.Envelope!.DebugFrequencyHz);
        var view = new SpeechExperimentResultsPageViewModel(new(result.Id), fixture.Service);
        await view.LoadTask;
        Assert.Equal("", view.StatusText);
        Assert.Equal(3, view.TotalCount);
        Assert.Equal(1, view.CompletedCount);
        Assert.Equal(2, view.InterruptedCount);
        Assert.All(view.Rounds.Skip(1), x => Assert.False(x.CanCompare));
        await using var db = fixture.CreateDbContext();
        Assert.Single(await db.SpeechExperiments.ToArrayAsync());
        Assert.Empty(await db.ExperimentRuns.ToArrayAsync());
    }

    [Fact]
    public async Task MigrationAndRecoveryKeepCommittedDataAndAllowAggregateDeletion()
    {
        using var f = await Fixture.Create();
        var id = Guid.NewGuid();
        var round = Round(1, 40, false);
        await f.Service.SaveRoundAsync(
            id,
            SpeechExperimentSource.From(f.Route.Configuration),
            round
        );
        await f.Service.SaveRoundAsync(
            id,
            SpeechExperimentSource.From(f.Route.Configuration),
            round
        );
        await new ExperimentRecoveryService(f).RecoverAsync(DateTimeOffset.UtcNow);
        var result = await f.Service.LoadAsync(id);
        Assert.Equal(ExperimentRunStatus.RecoveredAfterCrash, result.Status);
        Assert.Equal(SpeechRoundStatus.Interrupted, Assert.Single(result.Rounds).Status);
        Assert.Single(result.Rounds[0].Result.Sentences);
        await using (var db = f.CreateDbContext())
        {
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Contains(
                "20261022090000_AddSpeechExperiments",
                await db.Database.GetAppliedMigrationsAsync()
            );
        }
        var deletion = await new ExperimentHistoryDeletionService(f).DeleteAsync([id]);
        Assert.Empty(deletion.Failures);
        await using var deleted = f.CreateDbContext();
        Assert.Empty(await deleted.SpeechTrainingRounds.ToArrayAsync());
        Assert.Empty(await deleted.SpeechExperiments.ToArrayAsync());
    }

    [Fact]
    public async Task ComparisonFiltersExportAndCopyUseRoundSnapshots()
    {
        var source = SpeechExperimentSource.From(Route().Configuration);
        var rounds = new[]
        {
            Round(1, 40, true),
            Round(2, 40, true),
            Round(3, 80, true),
            Round(4, 40, false) with
            {
                Status = SpeechRoundStatus.Interrupted,
            },
        };
        var experiment = new SpeechExperimentResult(
            Guid.NewGuid(),
            source,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ExperimentRunStatus.Completed,
            rounds
        );
        var vm = new SpeechExperimentResultsPageViewModel(new(experiment.Id, experiment));
        await vm.LoadTask;
        vm.Rounds[0].Selected = true;
        vm.Rounds[1].Selected = true;
        Assert.Equal(2, vm.ChartRows.Count);
        vm.Rounds[2].Selected = true;
        Assert.False(vm.Rounds[2].Selected);
        vm.DifferentDelay = true;
        vm.Rounds[2].Selected = true;
        Assert.Equal(3, vm.ChartRows.Count);
        vm.Rounds[3].Selected = true;
        Assert.False(vm.Rounds[3].Selected);
        vm.ViewRoundCommand.Execute(vm.Rounds[2]);
        Assert.Equal(2, vm.SelectedTab);
        Assert.Contains("80 ms", vm.DetailSummary);
        var copy = SpeechExperimentResultsPageViewModel.CreateCopyTemplate(source, rounds[2]);
        Assert.Equal(80, copy.StimulusConfiguration.Envelope!.DelayMilliseconds);
        Assert.Equal(500, copy.StimulusConfiguration.Envelope.DebugFrequencyHz);
        Assert.NotNull(copy.SpeechTrainingPreset);
        Assert.Null(StimulusParameterPolicy.ValidateExecutableTemplate(copy));
        var directory = Path.Combine(Path.GetTempPath(), "speech-export-test-" + Guid.NewGuid());
        try
        {
            var path = await SpeechExperimentExportService.ExportAsync(experiment, directory);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(3, zip.Entries.Count);
            using var reader = new StreamReader(zip.GetEntry("experiment.json")!.Open());
            var restored = SimulationJson.Read<SpeechExperimentResult>(
                await reader.ReadToEndAsync()
            );
            Assert.Equal(4, restored!.Rounds.Count);
            Assert.Equal(80, restored.Rounds[2].DelayMilliseconds);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComparisonSelectionRejectsBeforeCommitInEitherOrder(bool reverse)
    {
        var source = SpeechExperimentSource.From(Route().Configuration);
        var rounds = new[] { Round(1, 100, true), Round(2, 165, true) };
        var snapshot = new SpeechExperimentResult(Guid.NewGuid(), source, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, ExperimentRunStatus.Completed, rounds);
        var vm = new SpeechExperimentResultsPageViewModel(new(snapshot.Id, snapshot));
        await vm.LoadTask;
        var first = vm.Rounds[reverse ? 1 : 0];
        var second = vm.Rounds[reverse ? 0 : 1];
        first.ToggleCommand.Execute(null);
        Assert.True(first.CanToggle);
        Assert.False(second.CanToggle);
        Assert.False(second.ToggleCommand.CanExecute(null));
        var observed = new List<bool>();
        second.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpeechRoundRow.Selected))
                observed.Add(second.Selected);
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            second.ToggleCommand.Execute(null);
            Assert.False(second.Selected);
            Assert.Equal(1, vm.SelectedCount);
            Assert.Equal("已选 1 次训练", vm.SelectionText);
            Assert.False(vm.CompareSelectedCommand.CanExecute(null));
            Assert.Contains("延时不同", vm.StatusText);
            Assert.Equal(vm.StatusText, vm.ComparisonHint);
            Assert.Empty(vm.ChartRows);
            first.ToggleCommand.Execute(null);
            Assert.Equal(0, vm.SelectedCount);
            Assert.True(first.CanToggle);
            Assert.True(second.CanToggle);
            first.ToggleCommand.Execute(null);
            Assert.False(vm.HasStatus);
        }
        Assert.Equal(new[] { false, false }, observed);
        vm.DifferentDelay = true;
        Assert.True(second.CanToggle);
        Assert.True(second.ToggleCommand.CanExecute(null));
        second.ToggleCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(2, vm.ChartRows.Count);
        Assert.True(vm.CompareSelectedCommand.CanExecute(null));
        Assert.False(vm.HasStatus);
        vm.DifferentDelay = false;
        Assert.True(first.CanToggle);
        Assert.True(second.CanToggle);
        second.ToggleCommand.Execute(null);
        Assert.False(second.CanToggle);
        Assert.True(first.CanToggle);
    }

    [Fact]
    public async Task ComparisonSelectionRejectsOtherConditionsAndIncompleteRounds()
    {
        var first = Round(1, 100, true);
        var differentCorpus = Round(2, 100, true);
        differentCorpus = differentCorpus with
        {
            Result = differentCorpus.Result with
            {
                Settings = differentCorpus.Result.Settings with { Corpus = "OTHER" },
            },
        };
        var snapshot = new SpeechExperimentResult(Guid.NewGuid(), SpeechExperimentSource.From(Route().Configuration),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ExperimentRunStatus.Completed,
            [first, differentCorpus, Round(3, 100, false)]);
        var vm = new SpeechExperimentResultsPageViewModel(new(snapshot.Id, snapshot));
        await vm.LoadTask;
        vm.Rounds[0].ToggleCommand.Execute(null);
        Assert.False(vm.Rounds[1].CanToggle);
        Assert.False(vm.Rounds[2].CanToggle);
        vm.Rounds[1].ToggleCommand.Execute(null);
        Assert.False(vm.Rounds[1].Selected);
        Assert.Contains("语料库不同", vm.StatusText);
        Assert.False(vm.Rounds[2].ToggleCommand.CanExecute(null));
        vm.Rounds[2].Selected = true;
        Assert.False(vm.Rounds[2].Selected);
        Assert.Contains("未有效完成", vm.StatusText);
        Assert.Equal(1, vm.SelectedCount);
        Assert.Empty(vm.ChartRows);
    }

    [Fact]
    public async Task DialogCancelAndInterruptDoNotEndExperimentAndNextRoundRequiresStart()
    {
        var choice = SpeechEndChoice.Cancel;
        var router = new Router();
        using var page = new EnvelopeStimulationPageViewModel(
            Route(),
            router,
            () => new(new TinyCorpus(), new ControllableSpeechAudioPlayer()),
            showEndDialog: dialog =>
            {
                if (choice == SpeechEndChoice.Training)
                    dialog.EndTrainingCommand.Execute(null);
                else if (choice == SpeechEndChoice.Experiment)
                    dialog.EndExperimentCommand.Execute(null);
                else
                    dialog.DismissCommand.Execute(null);
                return Task.CompletedTask;
            }
        );
        await page.StartCommand.ExecuteAsync(null);
        await page.EndCommand.ExecuteAsync(null);
        Assert.False(page.IsEnded);
        Assert.False(page.CanGoBack);
        Assert.False(page.BackCommand.CanExecute(null));
        choice = SpeechEndChoice.Training;
        await page.EndCommand.ExecuteAsync(null);
        Assert.True(page.IsEnded);
        Assert.Null(router.Result);
        await page.AgainCommand.ExecuteAsync(null);
        Assert.Single(page.ExperimentSnapshot().Rounds);
        Assert.False(page.CanGoBack);
        choice = SpeechEndChoice.Experiment;
        await page.EndCommand.ExecuteAsync(null);
        Assert.True(page.CanGoBack);
        Assert.Single(router.Result!.Snapshot!.Rounds);
        Assert.False(page.AgainCommand.CanExecute(null));
    }

    [Fact]
    public async Task HistoryAggregatesSearchesExportsAndRejectsChangesToCommittedSentences()
    {
        using var f = await Fixture.Create();
        var id = Guid.NewGuid();
        var source = SpeechExperimentSource.From(f.Route.Configuration);
        var first = Round(1, 40, false);
        await f.Service.SaveRoundAsync(id, source, first);
        var changed = first with
        {
            Result = first.Result with
            {
                Sentences = [first.Result.Sentences[0] with { Correct = 0 }],
            },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Service.SaveRoundAsync(id, source, changed)
        );
        await f.Service.SaveRoundAsync(
            id,
            source,
            first with
            {
                Status = SpeechRoundStatus.Interrupted,
            }
        );
        await f.Service.SaveRoundAsync(id, source, Round(2, 80, true));
        await f.Service.EndAsync(id, ExperimentRunStatus.Completed);
        var history = new ExperimentPersistenceService(
            f,
            new CurrentOperatorContext(),
            new EegPhysicalChannelMappingService()
        );
        var all = await history.QueryHistoryAsync(
            new(
                SearchText: "speech-test",
                RunStatus: ExperimentRunStatus.Completed,
                StartDate: DateOnly.FromDateTime(DateTime.Today),
                EndDate: DateOnly.FromDateTime(DateTime.Today)
            )
        );
        var item = Assert.Single(all.Items);
        Assert.True(item.IsSpeech);
        Assert.Equal(id, Assert.Single(item.Runs).RunId);
        Assert.Null(item.Runs[0].RawFilePath);
        Assert.Empty((await history.QueryHistoryAsync(new(SearchText: "not-found"))).Items);
        Assert.Empty(
            (
                await history.QueryHistoryAsync(
                    new(RunStatus: ExperimentRunStatus.InterruptedByUser)
                )
            ).Items
        );
        var directory = Path.Combine(
            Path.GetTempPath(),
            "speech-export-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            // Speech export never invokes EEG readers, finalizers or device-dependent services.
            var export = new EegExportService(f, history, null!, null!, null!, null!);
            var files = await export.ExportAsync(
                id,
                new(false, true, false),
                directory,
                revealResult: false
            );
            using var zip = ZipFile.OpenRead(Assert.Single(files.Paths));
            Assert.Equal(3, zip.Entries.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task OutputMustConfirmStopBeforeAnotherRoundAndAlwaysReceives500Hz()
    {
        var output = new StopControlledOutput();
        var created = 0;
        ControllableSpeechAudioPlayer player = null!;
        var source = Route();
        source = source with
        {
            Configuration = SpeechResultRoutes.WithEnvelope(
                source.Configuration,
                source.Configuration.StimulusConfiguration.Envelope! with
                {
                    DebugFrequencyHz = 800,
                }
            ),
        };
        using var page = new EnvelopeStimulationPageViewModel(
            source,
            new Router(),
            () =>
            {
                created++;
                player = new();
                return new(new TinyCorpus(), player);
            },
            createOutput: _ => output
        );
        var playback = page.StartCommand.ExecuteAsync(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (player.Audio is null)
            await Task.Delay(10, timeout.Token);
        player.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(500, output.Plan!.Settings.DebugFrequencyHz);
        output.FailStop = true;
        Assert.False(await page.FinishAsync(false));
        Assert.False(page.AgainCommand.CanExecute(null));
        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Equal(1, created);
        output.FailStop = false;
        output.BlockStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = page.FinishAsync(false);
        await output.EnteredStop.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await page.FinishAsync(false));
        Assert.False(page.AgainCommand.CanExecute(null));
        output.BlockStop.TrySetResult();
        Assert.True(await ending);
        await page.AgainCommand.ExecuteAsync(null);
        await page.AgainCommand.ExecuteAsync(null);
        Assert.Equal(2, created);
        page.DelayMilliseconds = 300;
        playback = page.StartCommand.ExecuteAsync(null);
        while (player.Audio is null)
            await Task.Delay(10, timeout.Token);
        player.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(500, output.Plan!.Settings.DebugFrequencyHz);
        Assert.Equal(300, output.Plan.Settings.DelayMilliseconds);
        Assert.Equal(2, page.ExperimentSnapshot().Rounds.Count);
        Assert.True(await page.PrepareToLeaveAsync());
        Assert.Equal(SpeechRoundStatus.Interrupted, page.ExperimentSnapshot().Rounds.Last().Status);
    }

    private sealed class StopControlledOutput : ISpeechStimulationOutput
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
        public event EventHandler<string>? Faulted
        {
            add { }
            remove { }
        }
        public string? Status => null;
        public EnvelopePlaybackPlan? Plan;
        public bool FailStop;
        public TaskCompletionSource? BlockStop;
        public TaskCompletionSource EnteredStop = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task PrepareAsync(EnvelopePlaybackPlan plan, CancellationToken token)
        {
            Plan = plan;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken token) => Task.CompletedTask;

        public Task WaitForCompletionAsync(CancellationToken token) => Task.CompletedTask;

        public Task StopAsync()
        {
            if (FailStop)
                throw new IOException("stop not acknowledged");
            if (BlockStop is null)
                return Task.CompletedTask;
            EnteredStop.TrySetResult();
            return BlockStop.Task;
        }
    }

    [Fact]
    public async Task EndingBeforeStartPersistsEmptyExperimentWithoutCreatingRound()
    {
        using var f = await Fixture.Create();
        var router = new Router();
        using var page = new EnvelopeStimulationPageViewModel(
            f.Route,
            router,
            () => new(new TinyCorpus(), new ControllableSpeechAudioPlayer()),
            experiments: f.Service
        );
        Assert.True(await page.FinishAsync(true));
        Assert.True(await page.FinishAsync(true));
        var result = await f.Service.LoadAsync(router.Result!.ExperimentId);
        Assert.Empty(result.Rounds);
        Assert.Equal(ExperimentRunStatus.Completed, result.Status);
        await using var db = f.CreateDbContext();
        Assert.Single(await db.SpeechExperiments.ToListAsync());
        Assert.Empty(await db.SpeechTrainingRounds.ToListAsync());
    }

    [Fact]
    public async Task DatabaseCanBeClosedAndReopenedWithoutAudioFilesOrDevices()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "speech-db-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "speech.db");
        var id = Guid.NewGuid();
        try
        {
            using (var f = await Fixture.Create(path))
            {
                await f.Service.SaveRoundAsync(
                    id,
                    SpeechExperimentSource.From(f.Route.Configuration),
                    Round(1, 35, true)
                );
                await f.Service.EndAsync(id, ExperimentRunStatus.Completed);
            }
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            using var reopened = new Fixture(connection);
            var result = await reopened.Service.LoadAsync(id);
            Assert.Equal(35, Assert.Single(result.Rounds).DelayMilliseconds);
            Assert.Equal(6, result.Rounds[0].Result.Sentences[0].Correct);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    internal static SpeechTrainingRound Round(int number, double delay, bool complete) =>
        new(
            Guid.NewGuid(),
            number,
            delay,
            complete ? DateTimeOffset.UtcNow : null,
            complete ? SpeechRoundStatus.Completed : SpeechRoundStatus.Running,
            new(
                new("MSP", "01", AudioSettings: new()),
                DateTimeOffset.UtcNow,
                complete ? 1 : 2,
                [new("s1", 1, "今天的阳光真好", 6, 7)],
                Playbacks: []
            )
        );

    private static EnvelopeStimulationRouteData Route(long databaseId = 0)
    {
        var d = ExperimentRunRouteDataDefaults.Create();
        var config = d.StimulusConfiguration with
        {
            Kind = StimulusKind.EnvelopeTAcs,
            Targets = d.StimulusConfiguration.Targets.Take(1).ToArray(),
            Envelope = new EnvelopeParameters(1, 1, 40, false) with { IsDeviceSetup = true },
        };
        return new(
            new(
                "EXP-20261022-001",
                "SPEECH-TEST",
                config,
                d.StimulusElectrodes,
                [],
                "",
                "",
                1000,
                experimentDatabaseId: databaseId,
                creationMode: ExperimentCreationMode.StimulusOnly
            ),
            "已通过"
        );
    }

    private sealed class TinyCorpus : ISpeechCorpusProvider
    {
        private readonly FileSpeechCorpusProvider _source = new();
        public IReadOnlyList<SpeechCorpus> Corpora =>
            [
                _source.Corpora[0] with
                {
                    Sentences = _source.Corpora[0].Sentences.Take(2).ToArray(),
                },
            ];

        public AudioBuffer ReadAudio(SpeechSentence sentence) => _source.ReadAudio(sentence);
    }

    private sealed class Router : INavigationRouter
    {
        public int BackCount { get; private set; }
        public SpeechExperimentResultsRouteData? Result { get; private set; }

        public void Navigate(SpeechExperimentResultsRouteData data) => Result = data;

        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData route) { }

        public void Navigate(StimulusConfigurationRouteData route) { }

        public void Navigate(ElectrodeConfigurationRouteData route) { }

        public void Navigate(ExperimentRunRouteData route) => throw new InvalidOperationException();

        public void Navigate(ExperimentRerunRouteData route) =>
            throw new InvalidOperationException();

        public void GoBack() => BackCount++;

        public void GoHome() { }
    }

    private sealed class Fixture(SqliteConnection connection)
        : IDbContextFactory<AppDbContext>,
            IDisposable
    {
        public SpeechExperimentService Service => new(this);
        public EnvelopeStimulationRouteData Route { get; private set; } = null!;

        public AppDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        public static async Task<Fixture> Create(string? databasePath = null)
        {
            var connection = new SqliteConnection(
                databasePath is null
                    ? "Data Source=:memory:"
                    : $"Data Source={databasePath};Pooling=False"
            );
            await connection.OpenAsync();
            var f = new Fixture(connection);
            await using var db = f.CreateDbContext();
            await db.Database.MigrateAsync();
            var entity = new ExperimentEntity
            {
                ExperimentCode = "EXP-20261022-001",
                Remarks = "",
                Status = ExperimentStatus.Draft,
                ScheduledAt = DateTimeOffset.UtcNow,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                CreationMode = ExperimentCreationMode.StimulusOnly,
                Operator = new()
                {
                    Username = "speech",
                    NormalizedUsername = "SPEECH",
                    PasswordSalt = [1],
                    PasswordHash = [1],
                    PasswordIterations = 1,
                },
                Subject = new()
                {
                    SubjectCode = "SPEECH-TEST",
                    NormalizedSubjectCode = "SPEECH-TEST",
                },
            };
            db.Experiments.Add(entity);
            await db.SaveChangesAsync();
            f.Route = SpeechExperimentTests.Route(entity.Id);
            return f;
        }

        public void Dispose() => connection.Dispose();
    }
}
