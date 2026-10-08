using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.ViewModels.Pages;

public enum ExperimentCreationMode
{
    StimulusOnly,
    AcquisitionOnly,
    AcquisitionAndStimulation,
}

public partial class StartExperimentPageViewModel : PageViewModel, IDisposable
{
    private readonly INavigationRouter _router;
    private readonly IExperimentPersistenceService _persistence;
    private readonly ISubjectLookupService _subjects;
    private readonly IExperimentPackageSerializer _packages;
    private readonly DialogService _dialogs;
    private readonly IDialogProvider _dialogHost;
    private readonly IDeviceSelectionContext _deviceSelection;
    private readonly ITemporaryEegCleanupService _temporaryCleanup;
    private readonly IEegRawPacketReader _rawReader;
    private readonly IEegBatchExportService? _batchExportService;
    private readonly IExperimentHistoryDeletionService? _historyDeletionService;
    private readonly Dictionary<Guid, SelectedHistoryRun> _selectedHistoryRuns = [];
    private readonly List<Guid> _selectedHistoryRunOrder = [];
    private CancellationTokenSource? _subjectSearchCancellation;
    private CancellationTokenSource? _historyQueryCancellation;
    private CancellationTokenSource? _batchExportCancellation;
    private CancellationTokenSource? _batchDeleteCancellation;
    private long? _draftId;
    private long _previewGeneration;
    private long _historyLoadGeneration;
    private int _disposed;
    private bool _suppressHistoryQuery;
    private readonly TimeSpan _initialExperimentTime = DateTime.Now.TimeOfDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewExperimentSelected))]
    private bool _isHistorySelected;

    [ObservableProperty]
    private string _subjectId = string.Empty;

    [ObservableProperty]
    private string _remarks = string.Empty;

    [ObservableProperty]
    private string _experimentId = string.Empty;

    [ObservableProperty]
    private DateTime? _selectedExperimentDate = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStimulusOnlyMode))]
    [NotifyPropertyChangedFor(nameof(IsAcquisitionOnlyMode))]
    [NotifyPropertyChangedFor(nameof(ConfigurationEntryText))]
    [NotifyPropertyChangedFor(nameof(IsAcquisitionAndStimulationMode))]
    private ExperimentCreationMode _selectedExperimentMode =
        ExperimentCreationMode.AcquisitionAndStimulation;

    [ObservableProperty]
    private bool _isExistingSubject;

    [ObservableProperty]
    private ExperimentConfigurationTemplate? _importedTemplate;

    [ObservableProperty]
    private string _historyStatusText = "正在读取实验记录…";

    [ObservableProperty]
    private string _historySearchText = string.Empty;

    [ObservableProperty]
    private HistoryStimulusFilterOption? _selectedHistoryStimulusFilter;

    [ObservableProperty]
    private HistoryRunStatusFilterOption? _selectedHistoryRunStatusFilter;

    [ObservableProperty]
    private DateTime? _historyStartDate;

    [ObservableProperty]
    private DateTime? _historyEndDate;

    [ObservableProperty]
    private int _historyPageNumber = 1;

    [ObservableProperty]
    private int _historyPageSize = 10;

    [ObservableProperty]
    private int _historyTotalCount;

    [ObservableProperty]
    private int _historyTotalPages;

    [ObservableProperty]
    private bool _isHistoryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistoryValidationError))]
    private string _historyValidationText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryInteractionEnabled))]
    [NotifyPropertyChangedFor(nameof(HistorySelectionText))]
    [NotifyCanExecuteChangedFor(nameof(BatchExportHistoryCommand))]
    private bool _isBatchExporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistorySelectionText))]
    private string _batchExportProgressText = string.Empty;

    [ObservableProperty]
    private string _batchExportStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryInteractionEnabled))]
    [NotifyPropertyChangedFor(nameof(HistorySelectionText))]
    [NotifyCanExecuteChangedFor(nameof(BatchExportHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(BatchDeleteHistoryCommand))]
    private bool _isBatchDeleting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistorySelectionText))]
    private string _batchDeleteProgressText = string.Empty;

    [ObservableProperty]
    private string _batchDeleteStatusText = string.Empty;

    public StartExperimentPageViewModel(
        INavigationRouter router,
        IExperimentPersistenceService persistence,
        ISubjectLookupService subjects,
        IExperimentPackageSerializer packages,
        DialogService dialogs,
        IDialogProvider dialogHost,
        IDeviceSelectionContext deviceSelection,
        ITemporaryEegCleanupService temporaryCleanup,
        IEegRawPacketReader rawReader,
        IExperimentConfigurationTransferContext transferContext,
        IEegBatchExportService? batchExportService = null,
        IExperimentHistoryDeletionService? historyDeletionService = null,
        ISingleStimulusExperimentDialogService? singleStimulusDialog = null
    )
        : base(ApplicationPageNames.StartExperiment, "新建实验")
    {
        _router = router;
        _singleStimulusDialog = singleStimulusDialog;
        _persistence = persistence;
        _subjects = subjects;
        _packages = packages;
        _dialogs = dialogs;
        _dialogHost = dialogHost;
        _deviceSelection = deviceSelection;
        _temporaryCleanup = temporaryCleanup;
        _rawReader = rawReader;
        _batchExportService = batchExportService;
        _historyDeletionService = historyDeletionService;
        HistoryRecords = [];
        HistoryStimulusFilters =
        [
            new("全部范式", null),
            new("tDCS", "TDcs"),
            new("tACS", "TAcs"),
            new("tRNS", "TRns"),
            new("tPCS", "TPcs"),
            new("Sham", "Sham"),
            new("包络-tACS", "EnvelopeTAcs"),
        ];
        HistoryRunStatusFilters =
        [
            new("全部状态", null),
            new("正常完成", ExperimentRunStatus.Completed),
            new("人工急停", ExperimentRunStatus.InterruptedByUser),
            new("退出中断", ExperimentRunStatus.InterruptedByExit),
            new("运行失败", ExperimentRunStatus.Failed),
            new("崩溃恢复", ExperimentRunStatus.RecoveredAfterCrash),
        ];
        HistoryPageSizes = [10, 20, 50];
        _selectedHistoryStimulusFilter = HistoryStimulusFilters[0];
        _selectedHistoryRunStatusFilter = HistoryRunStatusFilters[0];
        SubjectCandidates = [];
        ImportedTemplate = transferContext.Take();
        _ = RefreshPreviewAndHistoryAsync();
    }

    public bool IsNewExperimentSelected => !IsHistorySelected;
    public string ExperimentDateDisplay => BuildScheduledAt().ToString("yyyy-MM-dd HH:mm");
    private readonly ISingleStimulusExperimentDialogService? _singleStimulusDialog;

    public bool IsStimulusOnlyMode => SelectedExperimentMode == ExperimentCreationMode.StimulusOnly;
    public bool IsAcquisitionOnlyMode =>
        SelectedExperimentMode == ExperimentCreationMode.AcquisitionOnly;
    public string ConfigurationEntryText =>
        IsAcquisitionOnlyMode ? "进入采集电极配置" : "进入刺激方案配置";
    public bool IsAcquisitionAndStimulationMode =>
        SelectedExperimentMode == ExperimentCreationMode.AcquisitionAndStimulation;
    public bool HasSubjectCandidates => SubjectCandidates.Count > 0;
    public bool HasImportedTemplate => ImportedTemplate is not null;
    public string ImportedTemplateSource => ImportedTemplate?.SourceDisplayName ?? string.Empty;
    public string ImportedTemplateSummary =>
        ImportedTemplate is null
            ? string.Empty
            : $"{ImportedTemplate.StimulusConfiguration.Kind} · "
                + $"{ImportedTemplate.StimulusElectrodes.Count} 个刺激点位 · "
                + $"{ImportedTemplate.AcquisitionChannels.Count} 个采集点位 · "
                + $"{ImportedTemplate.Timing.Mode}";

    public ObservableCollection<string> SubjectCandidates { get; }
    public ObservableCollection<ExperimentHistoryRecordViewModel> HistoryRecords { get; }
    public IReadOnlyList<HistoryStimulusFilterOption> HistoryStimulusFilters { get; }
    public IReadOnlyList<HistoryRunStatusFilterOption> HistoryRunStatusFilters { get; }
    public IReadOnlyList<int> HistoryPageSizes { get; }
    public bool HasHistoryValidationError => !string.IsNullOrEmpty(HistoryValidationText);
    public bool HasHistoryResults => HistoryTotalCount > 0;
    public int SelectedHistoryRunCount => _selectedHistoryRuns.Count;
    public bool HasSelectedHistoryRuns => SelectedHistoryRunCount > 0;
    public bool? HistoryPageSelectionState
    {
        get
        {
            var runs = HistoryRecords.SelectMany(x => x.Runs).ToArray();
            if (runs.Length == 0 || runs.All(x => !x.IsSelected))
                return false;
            return runs.All(x => x.IsSelected) ? true : null;
        }
    }
    public bool IsHistoryInteractionEnabled => !IsBatchExporting && !IsBatchDeleting;
    public string HistorySelectionText =>
        IsBatchDeleting ? BatchDeleteProgressText
        : IsBatchExporting ? BatchExportProgressText
        : HasSelectedHistoryRuns ? $"已选择 {SelectedHistoryRunCount} 条运行记录"
        : "勾选运行记录后可批量导出或删除";
    public bool CanGoToPreviousHistoryPage =>
        !IsHistoryLoading && IsHistoryInteractionEnabled && HistoryPageNumber > 1;
    public bool CanGoToNextHistoryPage =>
        !IsHistoryLoading
        && IsHistoryInteractionEnabled
        && HistoryTotalPages > 0
        && HistoryPageNumber < HistoryTotalPages;
    public string HistoryPageDisplay =>
        HistoryTotalPages == 0 ? "第 0 / 0 页" : $"第 {HistoryPageNumber} / {HistoryTotalPages} 页";

    public void ApplyRoute(StartExperimentRouteData routeData)
    {
        if (routeData.Section == StartExperimentSection.History)
        {
            IsHistorySelected = true;
            PageTitle = "历史实验";
            _ = LoadHistoryAsync();
            return;
        }

        SelectNewExperiment();
    }

    [RelayCommand]
    private void SelectNewExperiment()
    {
        IsHistorySelected = false;
        PageTitle = "新建实验";
    }

    [RelayCommand]
    private void SelectExperimentMode(ExperimentCreationMode mode) => SelectedExperimentMode = mode;

    [RelayCommand]
    private async Task SelectHistoryAsync()
    {
        IsHistorySelected = true;
        PageTitle = "历史实验";
        await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync() => await LoadHistoryAsync();

    [RelayCommand]
    private async Task SearchHistoryAsync() => await LoadHistoryAsync(resetPage: true);

    [RelayCommand(CanExecute = nameof(CanGoToPreviousHistoryPage))]
    private async Task PreviousHistoryPageAsync()
    {
        HistoryPageNumber--;
        await LoadHistoryAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextHistoryPage))]
    private async Task NextHistoryPageAsync()
    {
        HistoryPageNumber++;
        await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task ResetHistoryFiltersAsync()
    {
        ClearHistorySelection();
        _suppressHistoryQuery = true;
        HistorySearchText = string.Empty;
        SelectedHistoryStimulusFilter = HistoryStimulusFilters[0];
        SelectedHistoryRunStatusFilter = HistoryRunStatusFilters[0];
        HistoryStartDate = null;
        HistoryEndDate = null;
        HistoryPageSize = 10;
        _suppressHistoryQuery = false;
        await LoadHistoryAsync(resetPage: true);
    }

    private bool CanToggleCurrentPageHistorySelection() =>
        IsHistoryInteractionEnabled && HistoryRecords.Any(x => x.Runs.Count > 0);

    [RelayCommand(CanExecute = nameof(CanToggleCurrentPageHistorySelection))]
    private void ToggleCurrentPageHistorySelection()
    {
        var select = HistoryPageSelectionState != true;
        foreach (var record in HistoryRecords)
            record.SetAllRunsSelected(select);
        NotifyHistorySelectionChanged();
    }

    private bool CanBatchExportHistory() =>
        HasSelectedHistoryRuns && IsHistoryInteractionEnabled && _batchExportService is not null;

    [RelayCommand(CanExecute = nameof(CanBatchExportHistory))]
    private async Task BatchExportHistoryAsync()
    {
        if (_batchExportService is null || !HasSelectedHistoryRuns)
            return;

        var selected = _selectedHistoryRunOrder
            .Where(_selectedHistoryRuns.ContainsKey)
            .Select(id => _selectedHistoryRuns[id])
            .ToArray();
        var formatDialog = new BatchExportDialogViewModel(selected.Length);
        await _dialogs.ShowDialog(_dialogHost, formatDialog);
        if (!formatDialog.Confirmed)
            return;

        var parentDirectory = await _dialogs.FolderPicker("选择批量导出目录");
        if (string.IsNullOrWhiteSpace(parentDirectory))
            return;

        var cancellation = new CancellationTokenSource();
        CancelAndDispose(Interlocked.Exchange(ref _batchExportCancellation, cancellation));
        IsBatchExporting = true;
        BatchExportStatusText = string.Empty;
        BatchDeleteStatusText = string.Empty;
        BatchExportProgressText = $"正在导出 0/{selected.Length}";
        try
        {
            var progress = new CallbackProgress<EegBatchExportProgress>(value =>
                BatchExportProgressText = $"正在导出 {value.CompletedCount}/{value.TotalCount}"
            );
            var result = await _batchExportService.ExportAsync(
                selected.Select(x => x.RunId).ToArray(),
                new EegExportSelection(
                    formatDialog.ExportEdf,
                    formatDialog.ExportCsv,
                    formatDialog.ExportExpp
                ),
                parentDirectory,
                progress,
                cancellation.Token
            );

            ApplyBatchExportResult(result);
            await ShowBatchExportResultAsync(result, selected);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            BatchExportStatusText = "批量导出失败，请查看提示。";
            await ShowErrorAsync("无法批量导出", exception.Message);
        }
        finally
        {
            if (
                ReferenceEquals(
                    Interlocked.CompareExchange(ref _batchExportCancellation, null, cancellation),
                    cancellation
                )
            )
                cancellation.Dispose();
            IsBatchExporting = false;
            NotifyHistorySelectionChanged();
        }
    }

    private bool CanBatchDeleteHistory() =>
        HasSelectedHistoryRuns
        && IsHistoryInteractionEnabled
        && _historyDeletionService is not null;

    [RelayCommand(CanExecute = nameof(CanBatchDeleteHistory))]
    private async Task BatchDeleteHistoryAsync()
    {
        if (_historyDeletionService is null || !HasSelectedHistoryRuns)
            return;

        var selected = _selectedHistoryRunOrder
            .Where(_selectedHistoryRuns.ContainsKey)
            .Select(id => _selectedHistoryRuns[id])
            .ToArray();
        var confirmation = new ConfirmDialogViewModel(DialogKind.Risk)
        {
            Title = "确认批量删除",
            Message =
                $"将永久删除选中的 {selected.Length} 条运行记录及应用管理的原始和临时数据。"
                + "若实验不再包含任何运行，实验配置也会被删除；已导出到用户目录的文件会保留。此操作不可撤销。",
            ConfirmText = "永久删除",
            CancelText = "取消",
            ShowCancelButton = true,
        };
        await _dialogs.ShowDialog(_dialogHost, confirmation);
        if (!confirmation.Confirmed)
            return;

        var cancellation = new CancellationTokenSource();
        CancelAndDispose(Interlocked.Exchange(ref _batchDeleteCancellation, cancellation));
        IsBatchDeleting = true;
        BatchDeleteStatusText = string.Empty;
        BatchExportStatusText = string.Empty;
        BatchDeleteProgressText = $"正在删除 0/{selected.Length}";
        try
        {
            var progress = new CallbackProgress<ExperimentHistoryDeletionProgress>(value =>
                BatchDeleteProgressText = $"正在删除 {value.CompletedCount}/{value.TotalCount}"
            );
            var result = await _historyDeletionService.DeleteAsync(
                selected.Select(x => x.RunId).ToArray(),
                progress,
                cancellation.Token
            );

            ApplyBatchDeleteResult(result);
            await LoadHistoryAsync();
            await ShowBatchDeleteResultAsync(result, selected);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            BatchDeleteStatusText = "批量删除失败，请查看提示。";
            await ShowErrorAsync("无法批量删除", exception.Message);
        }
        finally
        {
            if (
                ReferenceEquals(
                    Interlocked.CompareExchange(ref _batchDeleteCancellation, null, cancellation),
                    cancellation
                )
            )
                cancellation.Dispose();
            IsBatchDeleting = false;
            NotifyHistorySelectionChanged();
        }
    }

    [RelayCommand]
    private async Task GenerateSubjectIdAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = _persistence.CreateRandomSubjectCode();
            if (!await _subjects.ExistsAsync(candidate))
            {
                SubjectId = candidate;
                return;
            }
        }
        await ShowErrorAsync("无法生成被试 ID", "暂时无法生成唯一被试 ID，请重试。");
    }

    [RelayCommand]
    private void SelectSubjectCandidate(string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            SubjectId = candidate;
            IsExistingSubject = true;
        }
        CancelAndDispose(Interlocked.Exchange(ref _subjectSearchCancellation, null));
        SubjectCandidates.Clear();
        OnPropertyChanged(nameof(HasSubjectCandidates));
    }

    [RelayCommand]
    private async Task ImportPackageAsync()
    {
        var files = await _dialogs.FilePicker(
            "导入实验参数包",
            fileTypes: [new FilePickerFileType("实验参数包") { Patterns = ["*.expp"] }]
        );
        if (files.Length == 0)
            return;
        try
        {
            ImportedTemplate = await _packages.ReadAsync(files[0]);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("导入实验参数包失败", exception.Message);
        }
    }

    [RelayCommand]
    private void ClearImportedTemplate() => ImportedTemplate = null;

    [RelayCommand]
    private async Task CopyHistoryConfigurationAsync(ExperimentHistoryRecordViewModel? record)
    {
        if (record is null)
            return;
        try
        {
            var template = await _persistence.LoadTemplateAsync(record.DatabaseId);
            if (
                template.CreationMode != ExperimentCreationMode.AcquisitionOnly
                && (
                    template.StimulusConfiguration.Kind == StimulusKind.EnvelopeTAcs
                    || template.StimulusConfiguration.Envelope is not null
                )
            )
                throw new InvalidOperationException(
                    "包络-tACS 仅支持数据生成与回放，不能复制到设备实验。"
                );
            if (StimulusParameterPolicy.ValidateExecutableTemplate(template) is { } error)
                throw new InvalidOperationException(error);
            ImportedTemplate = template;
            _draftId = null;
            SelectNewExperiment();
            await RefreshPreviewAsync(++_previewGeneration);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("复制历史配置失败", exception.Message);
        }
    }

    [RelayCommand]
    private async Task ViewHistoryResultAsync(ExperimentHistoryRunViewModel? run)
    {
        if (run is null)
            return;
        try
        {
            var route = await _persistence.LoadHistoricalRunAsync(run.RunId);
            var historical =
                route.HistoricalResult
                ?? throw new InvalidOperationException("历史运行信息不完整。");
            if (route.CreationMode == ExperimentCreationMode.StimulusOnly)
            {
                if (_singleStimulusDialog is null)
                    throw new InvalidOperationException("单刺激历史服务不可用。");
                await _singleStimulusDialog.ShowAsync(route);
                return;
            }
            if (
                string.IsNullOrWhiteSpace(historical.RawFilePath)
                || !File.Exists(historical.RawFilePath)
            )
                throw new FileNotFoundException("原始数据文件不存在，无法查看历史结果。");
            var summary = await _rawReader.GetSummaryFromFileAsync(
                historical.RunId,
                historical.RawFilePath
            );
            ValidateHistoricalRecordingSummary(summary, historical.RunId);
            _router.Navigate(route);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法查看结果", exception.Message);
        }
    }

    internal static void ValidateHistoricalRecordingSummary(
        EegRecordingSummary? summary,
        Guid runId
    )
    {
        if (summary is null || summary.RecordingId != runId)
            throw new InvalidDataException("原始数据文件与实验运行不匹配。");
        if (summary.PacketCount + summary.SampleBatchCount == 0)
            throw new InvalidDataException("该旧记录未保存波形数据，无法恢复历史波形。");
    }

    [RelayCommand]
    private async Task EnterStimulusConfigurationAsync()
    {
        if (string.IsNullOrWhiteSpace(SubjectId))
        {
            await ShowErrorAsync("无法进入刺激方案配置", "请输入被试 ID。");
            return;
        }
        try
        {
            var scheduledAt = BuildScheduledAt();
            var draft = await _persistence.CreateOrUpdateDraftAsync(
                _draftId,
                scheduledAt,
                SubjectId,
                Remarks
            );
            _draftId = draft.Id;
            ExperimentId = draft.ExperimentCode;
            if (SelectedExperimentMode == ExperimentCreationMode.AcquisitionOnly)
            {
                _router.Navigate(
                    new ElectrodeConfigurationRouteData(
                        draft.ExperimentCode,
                        draft.SubjectCode,
                        AcquisitionOnlyConfiguration.StimulusPlaceholder,
                        _deviceSelection.SelectedDeviceId,
                        draft.Id,
                        scheduledAt,
                        Remarks.Trim(),
                        ImportedTemplate,
                        SelectedExperimentMode
                    )
                );
                return;
            }
            if (ImportedTemplate is { } template)
            {
                if (StimulusParameterPolicy.ValidateExecutableTemplate(template) is { } error)
                    throw new InvalidOperationException(error);
                _router.Navigate(
                    new ElectrodeConfigurationRouteData(
                        draft.ExperimentCode,
                        draft.SubjectCode,
                        template.StimulusConfiguration,
                        _deviceSelection.SelectedDeviceId,
                        draft.Id,
                        scheduledAt,
                        Remarks.Trim(),
                        template,
                        SelectedExperimentMode
                    )
                );
                return;
            }
            _router.Navigate(
                new StimulusConfigurationRouteData(
                    draft.ExperimentCode,
                    draft.SubjectCode,
                    draft.Id,
                    scheduledAt,
                    Remarks.Trim(),
                    SelectedExperimentMode
                )
            );
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法进入刺激方案配置", exception.Message);
        }
    }

    partial void OnSubjectIdChanged(string value)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        CancelAndDispose(Interlocked.Exchange(ref _subjectSearchCancellation, cancellation));
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (
                ReferenceEquals(
                    Interlocked.CompareExchange(ref _subjectSearchCancellation, null, cancellation),
                    cancellation
                )
            )
                CancelAndDispose(cancellation);
            return;
        }
        _ = SearchSubjectsAsync(value, cancellationToken);
    }

    partial void OnSelectedExperimentDateChanged(DateTime? value) => SchedulePreviewRefresh();

    partial void OnImportedTemplateChanged(ExperimentConfigurationTemplate? value)
    {
        if (value is not null)
            SelectedExperimentMode = value.CreationMode;
        OnPropertyChanged(nameof(HasImportedTemplate));
        OnPropertyChanged(nameof(ImportedTemplateSource));
        OnPropertyChanged(nameof(ImportedTemplateSummary));
    }

    partial void OnHistorySearchTextChanged(string value)
    {
        if (_suppressHistoryQuery)
            return;
        ClearHistorySelection();
        if (IsHistorySelected)
            ScheduleHistorySearch();
    }

    partial void OnSelectedHistoryStimulusFilterChanged(HistoryStimulusFilterOption? value) =>
        ReloadHistoryForFilterChange(clearSelection: true);

    partial void OnSelectedHistoryRunStatusFilterChanged(HistoryRunStatusFilterOption? value) =>
        ReloadHistoryForFilterChange(clearSelection: true);

    partial void OnHistoryStartDateChanged(DateTime? value) =>
        ReloadHistoryForFilterChange(clearSelection: true);

    partial void OnHistoryEndDateChanged(DateTime? value) =>
        ReloadHistoryForFilterChange(clearSelection: true);

    partial void OnHistoryPageSizeChanged(int value)
    {
        if (HistoryPageSizes.Contains(value))
            ReloadHistoryForFilterChange(clearSelection: false);
    }

    partial void OnHistoryPageNumberChanged(int value) => NotifyHistoryPaginationStateChanged();

    partial void OnHistoryTotalCountChanged(int value) =>
        OnPropertyChanged(nameof(HasHistoryResults));

    partial void OnHistoryTotalPagesChanged(int value) => NotifyHistoryPaginationStateChanged();

    partial void OnIsHistoryLoadingChanged(bool value) => NotifyHistoryPaginationStateChanged();

    partial void OnIsBatchExportingChanged(bool value)
    {
        NotifyHistoryPaginationStateChanged();
        NotifyHistorySelectionChanged();
    }

    partial void OnIsBatchDeletingChanged(bool value)
    {
        NotifyHistoryPaginationStateChanged();
        NotifyHistorySelectionChanged();
    }

    private async Task SearchSubjectsAsync(string value, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(200, cancellationToken);
            IReadOnlyList<string> results = string.IsNullOrWhiteSpace(value)
                ? []
                : await _subjects.SearchAsync(value, cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            var normalizedValue = value.Trim();
            IsExistingSubject = results.Any(x =>
                string.Equals(x, normalizedValue, StringComparison.OrdinalIgnoreCase)
            );
            SubjectCandidates.Clear();
            foreach (
                var result in results.Where(x =>
                    !string.Equals(x, normalizedValue, StringComparison.OrdinalIgnoreCase)
                )
            )
                SubjectCandidates.Add(result);
            OnPropertyChanged(nameof(HasSubjectCandidates));
        }
        catch (OperationCanceledException) { }
    }

    private void SchedulePreviewRefresh()
    {
        OnPropertyChanged(nameof(ExperimentDateDisplay));
        _ = RefreshPreviewAsync(++_previewGeneration);
    }

    private async Task RefreshPreviewAndHistoryAsync()
    {
        try
        {
            await _temporaryCleanup.CleanupAsync();
            await RefreshPreviewAsync(++_previewGeneration);
            await LoadHistoryAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("初始化实验信息失败", exception.Message);
        }
    }

    private async Task RefreshPreviewAsync(long generation)
    {
        try
        {
            var code = await _persistence.PreviewExperimentCodeAsync(BuildScheduledAt());
            if (generation == _previewGeneration)
                ExperimentId = code;
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("生成实验 ID 失败", exception.Message);
        }
    }

    private async Task LoadHistoryAsync(bool resetPage = false)
    {
        var generation = Interlocked.Increment(ref _historyLoadGeneration);
        var cancellation = new CancellationTokenSource();
        CancelAndDispose(Interlocked.Exchange(ref _historyQueryCancellation, cancellation));
        if (resetPage)
            HistoryPageNumber = 1;
        await LoadHistoryCoreAsync(generation, cancellation);
    }

    private async Task LoadHistoryCoreAsync(long generation, CancellationTokenSource cancellation)
    {
        try
        {
            if (!TryValidateHistoryDateRange())
            {
                ClearHistoryResults();
                HistoryStatusText = HistoryValidationText;
                return;
            }

            IsHistoryLoading = true;
            HistoryStatusText = "正在读取实验记录…";
            var result = await _persistence.QueryHistoryAsync(
                new ExperimentHistoryQuery(
                    HistorySearchText,
                    SelectedHistoryStimulusFilter?.Value,
                    ToLocalDate(HistoryStartDate),
                    ToLocalDate(HistoryEndDate),
                    SelectedHistoryRunStatusFilter?.Value,
                    HistoryPageNumber,
                    HistoryPageSize
                ),
                cancellation.Token
            );
            if (generation != Volatile.Read(ref _historyLoadGeneration))
                return;

            HistoryPageNumber = result.PageNumber;
            HistoryTotalCount = result.TotalCount;
            HistoryTotalPages = result.TotalPages;
            ReplaceHistoryRecords(result.Items);
            HistoryStatusText =
                result.TotalCount == 0
                    ? "没有符合当前条件的实验记录"
                    : $"共 {result.TotalCount} 条实验记录";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _historyLoadGeneration))
                HistoryStatusText = $"读取实验记录失败：{exception.Message}";
        }
        finally
        {
            if (generation == Volatile.Read(ref _historyLoadGeneration))
                IsHistoryLoading = false;
            if (
                ReferenceEquals(
                    Interlocked.CompareExchange(ref _historyQueryCancellation, null, cancellation),
                    cancellation
                )
            )
                cancellation.Dispose();
        }
    }

    private void ReplaceHistoryRecords(IReadOnlyList<ExperimentHistoryItem> records)
    {
        HistoryRecords.Clear();
        foreach (var record in records)
        {
            var runs = record
                .Runs.Select(run => new ExperimentHistoryRunViewModel(
                    run.RunId,
                    record.ExperimentCode,
                    FormatRunStatus(run.Status),
                    (run.StartedAtUtc ?? run.CreatedAtUtc)
                        .ToLocalTime()
                        .ToString("yyyy/MM/dd HH:mm:ss"),
                    FormatRunDuration(run.StartedAtUtc, run.EndedAtUtc),
                    FormatPacketQuality(run.PacketStatistics),
                    _selectedHistoryRuns.ContainsKey(run.RunId)
                ))
                .ToArray();
            HistoryRecords.Add(
                new ExperimentHistoryRecordViewModel(
                    record.ExperimentId,
                    record.ExperimentCode,
                    record.SubjectCode,
                    record.StimulusKind == "EnvelopeTAcs" ? "包络-tACS" : record.StimulusKind,
                    $"{record.TotalCurrentMilliAmps:0.###}mA",
                    record.ElectrodeSites.Select(x => new ElectrodePointViewModel(x)).ToArray(),
                    $"{record.RunCount}次",
                    record.ScheduledAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
                    runs,
                    HandleHistoryRunSelectionChanged
                )
            );
        }
        NotifyHistorySelectionChanged();
    }

    private void ClearHistoryResults()
    {
        HistoryRecords.Clear();
        HistoryTotalCount = 0;
        HistoryTotalPages = 0;
        HistoryPageNumber = 1;
        NotifyHistorySelectionChanged();
    }

    private void ReloadHistoryForFilterChange(bool clearSelection)
    {
        if (clearSelection && !_suppressHistoryQuery)
            ClearHistorySelection();
        if (!_suppressHistoryQuery && IsHistorySelected)
            _ = LoadHistoryAsync(resetPage: true);
    }

    private void HandleHistoryRunSelectionChanged(
        ExperimentHistoryRunViewModel run,
        bool isSelected
    )
    {
        if (isSelected)
        {
            if (
                _selectedHistoryRuns.TryAdd(
                    run.RunId,
                    new SelectedHistoryRun(run.RunId, run.ExperimentId, run.StartedAt)
                )
            )
                _selectedHistoryRunOrder.Add(run.RunId);
        }
        else
        {
            _selectedHistoryRuns.Remove(run.RunId);
            _selectedHistoryRunOrder.Remove(run.RunId);
        }
        NotifyHistorySelectionChanged();
    }

    private void ClearHistorySelection()
    {
        if (_selectedHistoryRuns.Count == 0)
            return;
        _selectedHistoryRuns.Clear();
        _selectedHistoryRunOrder.Clear();
        foreach (var record in HistoryRecords)
            record.SetAllRunsSelected(false, notifyOwner: false);
        NotifyHistorySelectionChanged();
    }

    private void RemoveSuccessfulSelections(IEnumerable<Guid> runIds)
    {
        var successfulIds = runIds.ToHashSet();
        foreach (var runId in successfulIds)
        {
            _selectedHistoryRuns.Remove(runId);
            _selectedHistoryRunOrder.Remove(runId);
        }
        foreach (var record in HistoryRecords)
            record.ClearSelectedRuns(successfulIds);
        NotifyHistorySelectionChanged();
    }

    internal void ApplyBatchExportResult(EegBatchExportResult result)
    {
        RemoveSuccessfulSelections(result.Successes.Select(x => x.RunId));
        BatchExportStatusText =
            $"批量导出完成：成功 {result.Successes.Count} 条，生成 {result.FileCount} 个文件，失败 {result.Failures.Count} 条";
    }

    internal void ApplyBatchDeleteResult(ExperimentHistoryDeletionResult result)
    {
        RemoveSuccessfulSelections(result.Successes.Select(x => x.RunId));
        BatchDeleteStatusText =
            $"批量删除完成：成功 {result.Successes.Count} 条，失败 {result.Failures.Count} 条";
    }

    private void NotifyHistorySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedHistoryRunCount));
        OnPropertyChanged(nameof(HasSelectedHistoryRuns));
        OnPropertyChanged(nameof(HistoryPageSelectionState));
        OnPropertyChanged(nameof(HistorySelectionText));
        BatchExportHistoryCommand.NotifyCanExecuteChanged();
        BatchDeleteHistoryCommand.NotifyCanExecuteChanged();
        ToggleCurrentPageHistorySelectionCommand.NotifyCanExecuteChanged();
    }

    private async Task ShowBatchExportResultAsync(
        EegBatchExportResult result,
        IReadOnlyList<SelectedHistoryRun> selected
    )
    {
        var selectedById = selected.ToDictionary(x => x.RunId);
        var failures = result
            .Failures.Select(failure =>
            {
                var item = selectedById.GetValueOrDefault(failure.RunId);
                return new BatchExportFailureViewModel(
                    item?.ExperimentId ?? failure.RunId.ToString(),
                    item?.StartedAt ?? string.Empty,
                    failure.Message
                );
            })
            .ToArray();
        await _dialogs.ShowDialog(
            _dialogHost,
            new BatchExportResultDialogViewModel(
                result.Successes.Count,
                result.FileCount,
                failures,
                result.DirectoryPath
            )
        );
    }

    private async Task ShowBatchDeleteResultAsync(
        ExperimentHistoryDeletionResult result,
        IReadOnlyList<SelectedHistoryRun> selected
    )
    {
        var selectedById = selected.ToDictionary(x => x.RunId);
        var failures = result
            .Failures.Select(failure =>
            {
                var item = selectedById.GetValueOrDefault(failure.RunId);
                return new BatchDeleteFailureViewModel(
                    item?.ExperimentId ?? failure.RunId.ToString(),
                    item?.StartedAt ?? string.Empty,
                    failure.Message
                );
            })
            .ToArray();
        await _dialogs.ShowDialog(
            _dialogHost,
            new BatchDeleteResultDialogViewModel(result.Successes.Count, failures)
        );
    }

    private void ScheduleHistorySearch()
    {
        var generation = Interlocked.Increment(ref _historyLoadGeneration);
        var cancellation = new CancellationTokenSource();
        CancelAndDispose(Interlocked.Exchange(ref _historyQueryCancellation, cancellation));
        _ = DebounceHistorySearchAsync(generation, cancellation);
    }

    private async Task DebounceHistorySearchAsync(
        long generation,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            await Task.Delay(300, cancellation.Token);
            if (generation != Volatile.Read(ref _historyLoadGeneration))
                return;
            HistoryPageNumber = 1;
            await LoadHistoryCoreAsync(generation, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (
                ReferenceEquals(
                    Interlocked.CompareExchange(ref _historyQueryCancellation, null, cancellation),
                    cancellation
                )
            )
                cancellation.Dispose();
        }
    }

    private bool TryValidateHistoryDateRange()
    {
        if (
            HistoryStartDate is not null
            && HistoryEndDate is not null
            && ToLocalDate(HistoryStartDate) > ToLocalDate(HistoryEndDate)
        )
        {
            HistoryValidationText = "开始日期不能晚于结束日期";
            return false;
        }
        HistoryValidationText = string.Empty;
        return true;
    }

    private void NotifyHistoryPaginationStateChanged()
    {
        OnPropertyChanged(nameof(CanGoToPreviousHistoryPage));
        OnPropertyChanged(nameof(CanGoToNextHistoryPage));
        OnPropertyChanged(nameof(HistoryPageDisplay));
        PreviousHistoryPageCommand.NotifyCanExecuteChanged();
        NextHistoryPageCommand.NotifyCanExecuteChanged();
    }

    private static DateOnly? ToLocalDate(DateTime? value) =>
        value is null ? null : DateOnly.FromDateTime(value.Value);

    private static string FormatRunStatus(ExperimentRunStatus status) =>
        status switch
        {
            ExperimentRunStatus.Completed => "正常完成",
            ExperimentRunStatus.InterruptedByUser => "人工急停",
            ExperimentRunStatus.InterruptedByExit => "退出中断",
            ExperimentRunStatus.Failed => "运行失败",
            ExperimentRunStatus.RecoveredAfterCrash => "崩溃恢复",
            _ => status.ToString(),
        };

    private static string FormatRunDuration(DateTimeOffset? started, DateTimeOffset? ended) =>
        started is not null && ended is not null
            ? (ended.Value - started.Value).ToString(@"hh\:mm\:ss")
            : "--:--:--";

    private static string FormatPacketQuality(EegPacketStatistics? statistics) =>
        statistics is null
            ? "暂无统计"
            : $"丢包 {statistics.LostPacketCount} · {statistics.LossRate:P2}";

    private DateTimeOffset BuildScheduledAt()
    {
        var date = (SelectedExperimentDate ?? DateTime.Today).Date;
        var local = DateTime.SpecifyKind(date + _initialExperimentTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private Task ShowErrorAsync(string title, string message) =>
        _dialogs.ShowDialog(
            _dialogHost,
            new ConfirmDialogViewModel(DialogKind.Error)
            {
                Title = title,
                Message = message,
                ConfirmText = "确认",
                ShowCancelButton = false,
            }
        );

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CancelAndDispose(Interlocked.Exchange(ref _subjectSearchCancellation, null));
        CancelAndDispose(Interlocked.Exchange(ref _historyQueryCancellation, null));
        CancelAndDispose(Interlocked.Exchange(ref _batchExportCancellation, null));
        CancelAndDispose(Interlocked.Exchange(ref _batchDeleteCancellation, null));
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }
}

public sealed record HistoryStimulusFilterOption(string Label, string? Value);

public sealed record HistoryRunStatusFilterOption(string Label, ExperimentRunStatus? Value);

public partial class ExperimentHistoryRecordViewModel : ObservableObject
{
    private readonly Action<ExperimentHistoryRunViewModel, bool>? _selectionChanged;

    public ExperimentHistoryRecordViewModel(
        long databaseId,
        string experimentId,
        string patientId,
        string stimulationProtocol,
        string stimulationCurrent,
        IReadOnlyList<ElectrodePointViewModel> electrodePoints,
        string experimentCount,
        string recordedAt,
        IReadOnlyList<ExperimentHistoryRunViewModel>? runs = null,
        Action<ExperimentHistoryRunViewModel, bool>? selectionChanged = null
    )
    {
        DatabaseId = databaseId;
        ExperimentId = experimentId;
        PatientId = patientId;
        StimulationProtocol = stimulationProtocol;
        StimulationCurrent = stimulationCurrent;
        ElectrodePoints = electrodePoints;
        ExperimentCount = experimentCount;
        RecordedAt = recordedAt;
        Runs = runs ?? [];
        _selectionChanged = selectionChanged;
        foreach (var run in Runs)
            run.SelectionChanged = OnRunSelectionChanged;
    }

    public long DatabaseId { get; }
    public string ExperimentId { get; }
    public string PatientId { get; }
    public string StimulationProtocol { get; }
    public string StimulationCurrent { get; }
    public IReadOnlyList<ElectrodePointViewModel> ElectrodePoints { get; }
    public string ExperimentCount { get; }
    public string RecordedAt { get; }
    public IReadOnlyList<ExperimentHistoryRunViewModel> Runs { get; }
    public bool? SelectionState =>
        Runs.Count == 0 || Runs.All(x => !x.IsSelected) ? false
        : Runs.All(x => x.IsSelected) ? true
        : null;

    [ObservableProperty]
    private bool _isExpanded;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleAllRuns() => SetAllRunsSelected(SelectionState != true);

    internal void SetAllRunsSelected(bool value, bool notifyOwner = true)
    {
        foreach (var run in Runs)
            run.SetSelected(value, notifyOwner);
        OnPropertyChanged(nameof(SelectionState));
    }

    internal void ClearSelectedRuns(IReadOnlySet<Guid> runIds)
    {
        foreach (var run in Runs.Where(x => runIds.Contains(x.RunId)))
            run.SetSelected(false, notifyOwner: false);
        OnPropertyChanged(nameof(SelectionState));
    }

    private void OnRunSelectionChanged(ExperimentHistoryRunViewModel run, bool value)
    {
        _selectionChanged?.Invoke(run, value);
        OnPropertyChanged(nameof(SelectionState));
    }
}

public partial class ExperimentHistoryRunViewModel : ObservableObject
{
    private bool _suppressSelectionNotification;

    public ExperimentHistoryRunViewModel(
        Guid runId,
        string experimentId,
        string status,
        string startedAt,
        string duration,
        string packetQuality = "暂无统计",
        bool isSelected = false
    )
    {
        RunId = runId;
        ExperimentId = experimentId;
        Status = status;
        StartedAt = startedAt;
        Duration = duration;
        PacketQuality = packetQuality;
        _isSelected = isSelected;
    }

    public Guid RunId { get; }
    public string ExperimentId { get; }
    public string Status { get; }
    public string StartedAt { get; }
    public string Duration { get; }
    public string PacketQuality { get; }
    internal Action<ExperimentHistoryRunViewModel, bool>? SelectionChanged { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    internal void SetSelected(bool value, bool notifyOwner)
    {
        _suppressSelectionNotification = !notifyOwner;
        try
        {
            IsSelected = value;
        }
        finally
        {
            _suppressSelectionNotification = false;
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_suppressSelectionNotification)
            SelectionChanged?.Invoke(this, value);
    }
}

public sealed class ElectrodePointViewModel(string name)
{
    public string Name { get; } = name;
}

internal sealed record SelectedHistoryRun(Guid RunId, string ExperimentId, string StartedAt);

internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
