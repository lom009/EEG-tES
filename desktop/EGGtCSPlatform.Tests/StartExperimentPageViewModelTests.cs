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
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class StartExperimentPageViewModelTests
{
    [Fact]
    public async Task AcquisitionOnlySelectionSkipsStimulusConfiguration()
    {
        var router = new NullRouter();
        using var model = CreateModel(new StubPersistenceService(), router: router);
        model.SubjectId = "subject";
        model.SelectExperimentModeCommand.Execute(ExperimentCreationMode.AcquisitionOnly);
        await model.EnterStimulusConfigurationCommand.ExecuteAsync(null);
        Assert.Null(router.StimulusRoute);
        Assert.Equal(ExperimentCreationMode.AcquisitionOnly, router.ElectrodeRoute!.CreationMode);
        Assert.Empty(router.ElectrodeRoute.StimulusConfiguration.Targets);
    }

    [Fact]
    public async Task SingleStimulusSelectionIsPassedToConfigurationRoute()
    {
        var router = new NullRouter();
        using var model = CreateModel(new StubPersistenceService(), router: router);
        model.SubjectId = "subject";
        model.SelectExperimentModeCommand.Execute(ExperimentCreationMode.StimulusOnly);
        await model.EnterStimulusConfigurationCommand.ExecuteAsync(null);
        Assert.Equal(ExperimentCreationMode.StimulusOnly, router.StimulusRoute!.CreationMode);
    }

    [Fact]
    public void EmptyLegacySimulatedRecordingIsReportedAsUnrecoverable()
    {
        var runId = Guid.NewGuid();
        var metadata = new EegRecordingMetadata(
            runId,
            "EXP-OLD-SIM",
            "SUB-001",
            "simulated",
            500,
            ["C4"],
            DateTimeOffset.UtcNow,
            new EegDisplayFilterSettings(null, null, null)
        );
        var summary = new EegRecordingSummary(
            runId,
            "old-sim.eegraw",
            true,
            EegRecordingCompletionStatus.Completed,
            0,
            0,
            metadata.StartedAtUtc,
            DateTimeOffset.UtcNow,
            metadata
        );

        var exception = Assert.Throws<InvalidDataException>(() =>
            StartExperimentPageViewModel.ValidateHistoricalRecordingSummary(summary, runId)
        );

        Assert.Equal("该旧记录未保存波形数据，无法恢复历史波形。", exception.Message);
    }

    [Fact]
    public void DisposeCanBeCalledTwiceWhileSubjectSearchIsActive()
    {
        var model = CreateModel(new StubPersistenceService());
        model.SubjectId = "00000001";

        model.Dispose();
        model.Dispose();
    }

    [Fact]
    public void NewExperimentDefaultsToAcquisitionAndStimulationModeAndSupportsExclusiveSelection()
    {
        using var model = CreateModel(new StubPersistenceService());

        Assert.Equal(
            ExperimentCreationMode.AcquisitionAndStimulation,
            model.SelectedExperimentMode
        );
        Assert.False(model.IsStimulusOnlyMode);
        Assert.False(model.IsAcquisitionOnlyMode);
        Assert.True(model.IsAcquisitionAndStimulationMode);

        model.SelectExperimentModeCommand.Execute(ExperimentCreationMode.StimulusOnly);

        Assert.Equal(ExperimentCreationMode.StimulusOnly, model.SelectedExperimentMode);
        Assert.True(model.IsStimulusOnlyMode);
        Assert.False(model.IsAcquisitionOnlyMode);
        Assert.False(model.IsAcquisitionAndStimulationMode);

        model.SelectExperimentModeCommand.Execute(ExperimentCreationMode.AcquisitionOnly);

        Assert.Equal(ExperimentCreationMode.AcquisitionOnly, model.SelectedExperimentMode);
        Assert.False(model.IsStimulusOnlyMode);
        Assert.True(model.IsAcquisitionOnlyMode);
        Assert.False(model.IsAcquisitionAndStimulationMode);
    }

    [Fact]
    public async Task SelectingExperimentDateRefreshesPreviewAndPreservesInitialTimeWhenSavingDraft()
    {
        var persistence = new StubPersistenceService();
        using var model = CreateModel(persistence);
        Assert.True(persistence.LastPreviewScheduledAt.HasValue);
        var initialTime = persistence.LastPreviewScheduledAt.Value.TimeOfDay;
        var selectedDate = new DateTime(2030, 4, 18);

        model.SelectedExperimentDate = selectedDate;

        Assert.True(persistence.LastPreviewScheduledAt.HasValue);
        Assert.Equal(selectedDate, persistence.LastPreviewScheduledAt.Value.Date);
        Assert.Equal(initialTime, persistence.LastPreviewScheduledAt.Value.TimeOfDay);

        model.SubjectId = "SUB-001";
        await model.EnterStimulusConfigurationCommand.ExecuteAsync(null);

        Assert.True(persistence.LastDraftScheduledAt.HasValue);
        Assert.Equal(selectedDate, persistence.LastDraftScheduledAt.Value.Date);
        Assert.Equal(initialTime, persistence.LastDraftScheduledAt.Value.TimeOfDay);
    }

    [Fact]
    public async Task SelectingSubjectCandidateDoesNotReopenExactMatchPopup()
    {
        using var model = CreateModel(
            new StubPersistenceService(),
            new ImmediateSubjectLookupService()
        );
        model.SubjectId = "SUB";
        await Task.Delay(300);
        Assert.Contains("SUB001", model.SubjectCandidates);

        model.SelectSubjectCandidateCommand.Execute("SUB001");
        await Task.Delay(300);

        Assert.Equal("SUB001", model.SubjectId);
        Assert.True(model.IsExistingSubject);
        Assert.Empty(model.SubjectCandidates);
        Assert.False(model.HasSubjectCandidates);
    }

    [Fact]
    public async Task HistoryRouteSelectsHistoryAndLoadsTerminalRuns()
    {
        var runId = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 8, 28, 1, 2, 3, TimeSpan.Zero);
        var persistence = new StubPersistenceService
        {
            HistoryItems =
            [
                new ExperimentHistoryItem(
                    12,
                    "EXP-20260828-001",
                    "SUB-001",
                    "tDCS",
                    2,
                    ["Fz", "Cz"],
                    1,
                    startedAt,
                    [
                        new ExperimentHistoryRunItem(
                            runId,
                            ExperimentRunStatus.InterruptedByUser,
                            startedAt,
                            startedAt,
                            startedAt.AddSeconds(65),
                            "C:\\recordings\\run.eegraw"
                        ),
                    ]
                ),
            ],
        };
        var model = CreateModel(persistence);

        model.ApplyRoute(StartExperimentRouteData.History);
        await model.RefreshHistoryCommand.ExecuteAsync(null);

        Assert.True(model.IsHistorySelected);
        Assert.Equal("历史实验", model.PageTitle);
        Assert.Equal("共 1 条实验记录", model.HistoryStatusText);
        var record = Assert.Single(model.HistoryRecords);
        var run = Assert.Single(record.Runs);
        Assert.Equal(runId, run.RunId);
        Assert.Equal("人工急停", run.Status);
        Assert.Equal("00:01:05", run.Duration);
    }

    [Fact]
    public async Task HistorySupportsDebouncedSearchFiltersAndPaging()
    {
        var persistence = new StubPersistenceService { HistoryItems = CreateHistoryItems(25) };
        using var model = CreateModel(persistence);
        model.ApplyRoute(StartExperimentRouteData.History);
        await model.RefreshHistoryCommand.ExecuteAsync(null);

        Assert.Equal(25, model.HistoryTotalCount);
        Assert.Equal(3, model.HistoryTotalPages);
        Assert.Equal(10, model.HistoryRecords.Count);
        Assert.False(model.CanGoToPreviousHistoryPage);
        Assert.True(model.CanGoToNextHistoryPage);

        await model.NextHistoryPageCommand.ExecuteAsync(null);

        Assert.Equal(2, model.HistoryPageNumber);
        Assert.Equal(10, model.HistoryRecords.Count);
        Assert.True(model.CanGoToPreviousHistoryPage);

        model.HistorySearchText = "SUB-023";
        await Task.Delay(400);

        Assert.Equal(1, model.HistoryPageNumber);
        Assert.Equal(1, model.HistoryTotalCount);
        Assert.Equal("SUB-023", Assert.Single(model.HistoryRecords).PatientId);

        await model.ResetHistoryFiltersCommand.ExecuteAsync(null);
        model.HistoryPageSize = 20;
        await model.RefreshHistoryCommand.ExecuteAsync(null);

        Assert.Equal(20, model.HistoryRecords.Count);
        Assert.Equal(2, model.HistoryTotalPages);

        model.HistoryPageSize = 50;
        await model.RefreshHistoryCommand.ExecuteAsync(null);

        Assert.Equal([10, 20, 50], model.HistoryPageSizes);
        Assert.Equal(25, model.HistoryRecords.Count);
        Assert.Equal(1, model.HistoryTotalPages);

        model.SelectedHistoryStimulusFilter = model.HistoryStimulusFilters.Single(x =>
            x.Value == "TDcs"
        );
        model.SelectedHistoryRunStatusFilter = model.HistoryRunStatusFilters.Single(x =>
            x.Value == ExperimentRunStatus.InterruptedByUser
        );
        await model.RefreshHistoryCommand.ExecuteAsync(null);

        Assert.Equal(4, model.HistoryTotalCount);
        Assert.All(
            model.HistoryRecords,
            record => Assert.Equal("TDcs", record.StimulationProtocol)
        );

        model.HistoryStartDate = new DateTime(2026, 9, 10);
        model.HistoryEndDate = new DateTime(2026, 9, 1);
        await Task.Delay(50);

        Assert.True(model.HasHistoryValidationError);
        Assert.Equal("开始日期不能晚于结束日期", model.HistoryValidationText);
        Assert.Empty(model.HistoryRecords);
    }

    [Fact]
    public async Task HistoryRunSelectionIsTriStateAndPersistsAcrossPagesUntilFilterChanges()
    {
        var items = CreateHistoryItems(25).ToArray();
        var firstRun = items[^1].Runs[0];
        items[^1] = items[^1] with
        {
            RunCount = 2,
            Runs = [firstRun, firstRun with { RunId = Guid.NewGuid() }],
        };
        var persistence = new StubPersistenceService { HistoryItems = items };
        using var model = CreateModel(
            persistence,
            batchExportService: new StubBatchExportService()
        );
        model.ApplyRoute(StartExperimentRouteData.History);
        await model.RefreshHistoryCommand.ExecuteAsync(null);

        var firstRecord = model.HistoryRecords[0];
        firstRecord.ToggleAllRunsCommand.Execute(null);

        Assert.Equal(2, model.SelectedHistoryRunCount);
        Assert.True(firstRecord.SelectionState);
        Assert.True(model.BatchExportHistoryCommand.CanExecute(null));

        firstRecord.Runs[0].IsSelected = false;

        Assert.Equal(1, model.SelectedHistoryRunCount);
        Assert.Null(firstRecord.SelectionState);

        await model.NextHistoryPageCommand.ExecuteAsync(null);
        model.HistoryRecords[0].Runs[0].IsSelected = true;
        Assert.Equal(2, model.SelectedHistoryRunCount);

        await model.PreviousHistoryPageCommand.ExecuteAsync(null);
        Assert.False(model.HistoryRecords[0].Runs[0].IsSelected);
        Assert.True(model.HistoryRecords[0].Runs[1].IsSelected);

        model.HistoryPageSize = 20;
        await model.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal(2, model.SelectedHistoryRunCount);

        model.HistorySearchText = "SUB-023";

        Assert.Equal(0, model.SelectedHistoryRunCount);
        Assert.False(model.BatchExportHistoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task CurrentPageSelectAllIsTriStateAndDoesNotClearOtherPages()
    {
        var persistence = new StubPersistenceService { HistoryItems = CreateHistoryItems(25) };
        using var model = CreateModel(
            persistence,
            batchExportService: new StubBatchExportService()
        );
        model.ApplyRoute(StartExperimentRouteData.History);
        await model.RefreshHistoryCommand.ExecuteAsync(null);

        Assert.False(model.HistoryPageSelectionState);
        Assert.True(model.ToggleCurrentPageHistorySelectionCommand.CanExecute(null));

        model.ToggleCurrentPageHistorySelectionCommand.Execute(null);

        Assert.True(model.HistoryPageSelectionState);
        Assert.Equal(10, model.SelectedHistoryRunCount);
        Assert.All(model.HistoryRecords.SelectMany(x => x.Runs), x => Assert.True(x.IsSelected));

        model.HistoryRecords[0].Runs[0].IsSelected = false;
        Assert.Null(model.HistoryPageSelectionState);

        await model.NextHistoryPageCommand.ExecuteAsync(null);
        model.HistoryRecords[0].Runs[0].IsSelected = true;
        Assert.Null(model.HistoryPageSelectionState);

        await model.PreviousHistoryPageCommand.ExecuteAsync(null);
        model.ToggleCurrentPageHistorySelectionCommand.Execute(null);
        Assert.True(model.HistoryPageSelectionState);
        model.ToggleCurrentPageHistorySelectionCommand.Execute(null);

        Assert.False(model.HistoryPageSelectionState);
        Assert.Equal(1, model.SelectedHistoryRunCount);
    }

    [Fact]
    public async Task BatchExportCompletionClearsSuccessesAndKeepsFailuresSelected()
    {
        var items = CreateHistoryItems(1).ToArray();
        var firstRun = items[0].Runs[0];
        var failedRun = firstRun with { RunId = Guid.NewGuid() };
        items[0] = items[0] with { RunCount = 2, Runs = [firstRun, failedRun] };
        using var model = CreateModel(new StubPersistenceService { HistoryItems = items });
        model.ApplyRoute(StartExperimentRouteData.History);
        await model.RefreshHistoryCommand.ExecuteAsync(null);
        model.HistoryRecords[0].ToggleAllRunsCommand.Execute(null);

        model.ApplyBatchExportResult(
            new EegBatchExportResult(
                "C:\\exports\\batch",
                [
                    new EegBatchExportSuccess(
                        firstRun.RunId,
                        new EegExportResult(["first.edf", "first.csv"], "first.edf")
                    ),
                ],
                [new EegBatchExportFailure(failedRun.RunId, "missing raw file")]
            )
        );

        Assert.Equal(1, model.SelectedHistoryRunCount);
        Assert.False(model.HistoryRecords[0].Runs[0].IsSelected);
        Assert.True(model.HistoryRecords[0].Runs[1].IsSelected);
        Assert.Null(model.HistoryRecords[0].SelectionState);
        Assert.Equal(
            "批量导出完成：成功 1 条，生成 2 个文件，失败 1 条",
            model.BatchExportStatusText
        );
    }

    [Fact]
    public async Task BatchDeleteCompletionClearsSuccessesAndKeepsFailuresSelected()
    {
        var items = CreateHistoryItems(1).ToArray();
        var firstRun = items[0].Runs[0];
        var failedRun = firstRun with { RunId = Guid.NewGuid() };
        items[0] = items[0] with { RunCount = 2, Runs = [firstRun, failedRun] };
        using var model = CreateModel(
            new StubPersistenceService { HistoryItems = items },
            historyDeletionService: new StubHistoryDeletionService()
        );
        model.ApplyRoute(StartExperimentRouteData.History);
        await model.RefreshHistoryCommand.ExecuteAsync(null);
        model.HistoryRecords[0].ToggleAllRunsCommand.Execute(null);

        Assert.True(model.BatchDeleteHistoryCommand.CanExecute(null));
        model.ApplyBatchDeleteResult(
            new ExperimentHistoryDeletionResult(
                [new ExperimentHistoryDeletionSuccess(firstRun.RunId)],
                [new ExperimentHistoryDeletionFailure(failedRun.RunId, "file occupied")]
            )
        );

        Assert.Equal(1, model.SelectedHistoryRunCount);
        Assert.False(model.HistoryRecords[0].Runs[0].IsSelected);
        Assert.True(model.HistoryRecords[0].Runs[1].IsSelected);
        Assert.Null(model.HistoryRecords[0].SelectionState);
        Assert.Equal("批量删除完成：成功 1 条，失败 1 条", model.BatchDeleteStatusText);
    }

    [Fact]
    public async Task SupersededHistoryQueryCannotOverwriteLatestSearchResult()
    {
        using var model = CreateModel(new DelayedHistoryPersistenceService());
        model.ApplyRoute(StartExperimentRouteData.History);
        await model.RefreshHistoryCommand.ExecuteAsync(null);

        model.HistorySearchText = "slow";
        await Task.Delay(350);
        model.HistorySearchText = "fast";
        await Task.Delay(400);

        Assert.Equal("SUB-FAST", Assert.Single(model.HistoryRecords).PatientId);

        await Task.Delay(200);

        Assert.Equal("SUB-FAST", Assert.Single(model.HistoryRecords).PatientId);
    }

    private static IReadOnlyList<ExperimentHistoryItem> CreateHistoryItems(int count)
    {
        var first = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        return Enumerable
            .Range(1, count)
            .Select(index => new ExperimentHistoryItem(
                index,
                $"EXP-202609-{index:000}",
                $"SUB-{index:000}",
                index % 2 == 0 ? "TDcs" : "TAcs",
                2,
                ["Cz"],
                1,
                first.AddHours(index),
                [
                    new ExperimentHistoryRunItem(
                        Guid.NewGuid(),
                        index % 3 == 0
                            ? ExperimentRunStatus.InterruptedByUser
                            : ExperimentRunStatus.Completed,
                        first.AddHours(index),
                        first.AddHours(index),
                        first.AddHours(index).AddMinutes(1),
                        null
                    ),
                ]
            ))
            .ToArray();
    }

    private static StartExperimentPageViewModel CreateModel(
        IExperimentPersistenceService persistence,
        ISubjectLookupService? subjects = null,
        IEegBatchExportService? batchExportService = null,
        IExperimentHistoryDeletionService? historyDeletionService = null,
        NullRouter? router = null
    ) =>
        new(
            router ?? new NullRouter(),
            persistence,
            subjects ?? new BlockingSubjectLookupService(),
            new StubPackageSerializer(),
            new DialogService(() => null),
            new StubDialogProvider(),
            new DeviceSelectionContext(),
            new StubTemporaryCleanupService(),
            new StubRawPacketReader(),
            new ExperimentConfigurationTransferContext(),
            batchExportService,
            historyDeletionService
        );

    private sealed class StubPersistenceService : IExperimentPersistenceService
    {
        public IReadOnlyList<ExperimentHistoryItem> HistoryItems { get; init; } = [];
        public DateTimeOffset? LastPreviewScheduledAt { get; private set; }
        public DateTimeOffset? LastDraftScheduledAt { get; private set; }

        public Task<string> PreviewExperimentCodeAsync(
            DateTimeOffset scheduledAt,
            CancellationToken cancellationToken = default
        )
        {
            LastPreviewScheduledAt = scheduledAt;
            return Task.FromResult("EXP-TEST");
        }

        public Task<ExperimentDraft> CreateOrUpdateDraftAsync(
            long? draftId,
            DateTimeOffset scheduledAt,
            string subjectCode,
            string remarks,
            CancellationToken cancellationToken = default
        )
        {
            LastDraftScheduledAt = scheduledAt;
            return Task.FromResult(new ExperimentDraft(1, "EXP-TEST", subjectCode));
        }

        public Task SaveConfigurationAsync(
            long experimentId,
            ExperimentConfigurationTemplate template,
            IReadOnlyList<ImpedanceSnapshotValue> impedances,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task<ExperimentConfigurationTemplate> LoadTemplateAsync(
            long experimentId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<ExperimentHistoryItem>> ListHistoryAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(HistoryItems);

        public string CreateRandomSubjectCode() => "00000001";
    }

    private sealed class DelayedHistoryPersistenceService : IExperimentPersistenceService
    {
        public Task<string> PreviewExperimentCodeAsync(
            DateTimeOffset scheduledAt,
            CancellationToken cancellationToken = default
        ) => Task.FromResult("EXP-TEST");

        public Task<ExperimentDraft> CreateOrUpdateDraftAsync(
            long? draftId,
            DateTimeOffset scheduledAt,
            string subjectCode,
            string remarks,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ExperimentDraft(1, "EXP-TEST", subjectCode));

        public Task SaveConfigurationAsync(
            long experimentId,
            ExperimentConfigurationTemplate template,
            IReadOnlyList<ImpedanceSnapshotValue> impedances,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task<ExperimentConfigurationTemplate> LoadTemplateAsync(
            long experimentId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<ExperimentHistoryItem>> ListHistoryAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<ExperimentHistoryItem>>([]);

        public async Task<PagedResult<ExperimentHistoryItem>> QueryHistoryAsync(
            ExperimentHistoryQuery query,
            CancellationToken cancellationToken = default
        )
        {
            if (query.SearchText == "slow")
                await Task.Delay(500);
            else if (query.SearchText == "fast")
                await Task.Delay(10, cancellationToken);
            else
                return new PagedResult<ExperimentHistoryItem>([], 0, 1, query.PageSize);

            var item = CreateHistoryItems(1)[0] with
            {
                SubjectCode = query.SearchText == "fast" ? "SUB-FAST" : "SUB-SLOW",
            };
            return new PagedResult<ExperimentHistoryItem>([item], 1, 1, query.PageSize);
        }

        public string CreateRandomSubjectCode() => "00000001";
    }

    private sealed class BlockingSubjectLookupService : ISubjectLookupService
    {
        public async Task<IReadOnlyList<string>> SearchAsync(
            string query,
            int limit = 10,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task<bool> ExistsAsync(
            string subjectCode,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);
    }

    private sealed class ImmediateSubjectLookupService : ISubjectLookupService
    {
        public Task<IReadOnlyList<string>> SearchAsync(
            string query,
            int limit = 10,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<string>>(["SUB001"]);

        public Task<bool> ExistsAsync(
            string subjectCode,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(true);
    }

    private sealed class StubPackageSerializer : IExperimentPackageSerializer
    {
        public Task<ExperimentConfigurationTemplate> ReadAsync(
            string path,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task WriteAsync(
            string path,
            ExperimentConfigurationTemplate template,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class StubTemporaryCleanupService : ITemporaryEegCleanupService
    {
        public Task CleanupAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubBatchExportService : IEegBatchExportService
    {
        public Task<EegBatchExportResult> ExportAsync(
            IReadOnlyList<Guid> runIds,
            EegExportSelection selection,
            string targetParentDirectory,
            IProgress<EegBatchExportProgress>? progress = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new EegBatchExportResult(targetParentDirectory, [], []));
    }

    private sealed class StubHistoryDeletionService : IExperimentHistoryDeletionService
    {
        public Task<ExperimentHistoryDeletionResult> DeleteAsync(
            IReadOnlyList<Guid> runIds,
            IProgress<ExperimentHistoryDeletionProgress>? progress = null,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new ExperimentHistoryDeletionResult(
                    runIds.Select(x => new ExperimentHistoryDeletionSuccess(x)).ToArray(),
                    []
                )
            );
    }

    private sealed class StubRawPacketReader : IEegRawPacketReader
    {
        public ValueTask<IReadOnlyList<EegRecordingSummary>> ListAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<IReadOnlyList<EegRecordingSummary>>([]);

        public ValueTask<EegRecordingSummary?> GetSummaryAsync(
            Guid recordingId,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<EegRecordingSummary?>(null);

        public async IAsyncEnumerable<EegRawPacketRecord> ReadPacketsAsync(
            Guid recordingId,
            double? timelineStartSeconds = null,
            double? timelineEndSeconds = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DeleteAsync(
            Guid recordingId,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }

    private sealed class StubDialogProvider : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private sealed class NullRouter : INavigationRouter
    {
        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData routeData) { }

        public StimulusConfigurationRouteData? StimulusRoute;

        public void Navigate(StimulusConfigurationRouteData routeData)
        {
            StimulusRoute = routeData;
        }

        public ElectrodeConfigurationRouteData? ElectrodeRoute { get; private set; }

        public void Navigate(ElectrodeConfigurationRouteData routeData)
        {
            ElectrodeRoute = routeData;
        }

        public void Navigate(ExperimentRunRouteData routeData) { }

        public void Navigate(ExperimentRerunRouteData routeData) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
