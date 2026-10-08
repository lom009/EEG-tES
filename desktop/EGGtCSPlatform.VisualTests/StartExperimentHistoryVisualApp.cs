using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class StartExperimentHistoryVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        try
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var outputDirectory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
            Directory.CreateDirectory(outputDirectory);
            File.Delete(VisualTestOptions.Current.ErrorPath);
            var persistence = new VisualPersistenceService();
            var dialogs = new DialogService(() => null);
            var dialogHost = new VisualDialogProvider();
            var connection = new DeviceSelectionContext();
            var viewModel = new StartExperimentPageViewModel(
                new VisualTestNavigationRouter(),
                persistence,
                new VisualSubjectLookupService(),
                new VisualPackageSerializer(),
                dialogs,
                dialogHost,
                connection,
                new VisualCleanupService(),
                new VisualRawPacketReader(),
                new ExperimentConfigurationTransferContext(),
                new VisualBatchExportService()
            );
            var content = new BasicView
            {
                DataContext = new BasicViewModel(
                    new VisualTestNavigationRouter(),
                    viewModel,
                    connection
                ),
            };
            var window = new Window
            {
                Title = "新建实验 - 历史记录副页视觉验收",
                Width = 1440,
                Height = 900,
                MinWidth = 1200,
                MinHeight = 760,
                WindowDecorations = WindowDecorations.None,
                Content = content,
            };
            desktop.MainWindow = window;
            window.Closed += (_, _) => viewModel.Dispose();
            window.Opened += async (_, _) =>
            {
                try
                {
                    await Task.Delay(100);
                    if (
                        viewModel.SelectedExperimentMode
                            != ExperimentCreationMode.AcquisitionAndStimulation
                        || !viewModel.IsAcquisitionAndStimulationMode
                    )
                    {
                        throw new InvalidOperationException("新建实验未默认选择采集-刺激模式。");
                    }
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.new-default.png")
                    );

                    var modeButtons = content
                        .GetVisualDescendants()
                        .OfType<RadioButton>()
                        .Where(button => button.GroupName == "ExperimentCreationMode")
                        .OrderBy(button => button.Bounds.X)
                        .ToArray();
                    if (
                        modeButtons.Length != 3
                        || modeButtons[0].CornerRadius != new CornerRadius(4, 0, 0, 4)
                        || modeButtons[1].CornerRadius != new CornerRadius(0)
                        || modeButtons[2].CornerRadius != new CornerRadius(0, 4, 4, 0)
                    )
                    {
                        throw new InvalidOperationException(
                            "新建实验模式按钮圆角未按左、中、右位置配置。"
                        );
                    }

                    viewModel.SelectExperimentModeCommand.Execute(
                        ExperimentCreationMode.StimulusOnly
                    );
                    if (
                        !viewModel.IsStimulusOnlyMode
                        || viewModel.IsAcquisitionOnlyMode
                        || viewModel.IsAcquisitionAndStimulationMode
                    )
                    {
                        throw new InvalidOperationException("新建实验模式单选状态未正确切换。");
                    }
                    await Task.Delay(50);
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.new-stimulus-mode.png")
                    );

                    var datePicker =
                        content
                            .GetVisualDescendants()
                            .OfType<DateSelectionPicker>()
                            .FirstOrDefault(picker => picker.Name == "ExperimentDatePicker")
                        ?? throw new InvalidOperationException("未找到新建实验日期选择控件。");
                    var dateTextBox =
                        datePicker
                            .GetVisualDescendants()
                            .OfType<TextBox>()
                            .FirstOrDefault(textBox => textBox.Name == "PART_TextBox")
                        ?? throw new InvalidOperationException(
                            "未找到日期选择控件的文本显示区域。"
                        );
                    if (!dateTextBox.IsReadOnly)
                        throw new InvalidOperationException("日期选择控件仍允许手动编辑文本。");
                    var innerDatePicker =
                        datePicker
                            .GetVisualDescendants()
                            .OfType<CalendarDatePicker>()
                            .FirstOrDefault(picker => picker.Name == "PART_DatePicker")
                        ?? throw new InvalidOperationException("未找到日期选择组件的日历选择器。");
                    var calendarOpened = false;
                    innerDatePicker.CalendarOpened += (_, _) => calendarOpened = true;
                    innerDatePicker.IsDropDownOpen = true;
                    await Task.Delay(120);
                    if (!calendarOpened || !innerDatePicker.IsDropDownOpen)
                        throw new InvalidOperationException("点击式日期日历未能展开。");
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.new-date-open.png")
                    );
                    innerDatePicker.IsDropDownOpen = false;

                    viewModel.SelectExperimentModeCommand.Execute(
                        ExperimentCreationMode.AcquisitionAndStimulation
                    );
                    viewModel.ApplyRoute(StartExperimentRouteData.History);
                    await Task.Delay(50);
                    var historyDatePickers = content
                        .GetVisualDescendants()
                        .OfType<DateSelectionPicker>()
                        .Where(picker =>
                            picker.Name is "HistoryStartDatePicker" or "HistoryEndDatePicker"
                        )
                        .ToArray();
                    if (
                        historyDatePickers.Length != 2
                        || historyDatePickers.Any(picker =>
                            picker
                                .GetVisualDescendants()
                                .OfType<TextBox>()
                                .FirstOrDefault(textBox => textBox.Name == "PART_TextBox")
                                is not { IsReadOnly: true }
                        )
                    )
                    {
                        throw new InvalidOperationException(
                            "历史记录日期筛选未复用只读日期选择组件。"
                        );
                    }
                    await viewModel.RefreshHistoryCommand.ExecuteAsync(null);
                    EnsurePage(viewModel, 23, 3, 1, 10, "初始分页");
                    await Task.Delay(100);
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.history-page-1.png")
                    );

                    await viewModel.NextHistoryPageCommand.ExecuteAsync(null);
                    EnsurePage(viewModel, 23, 3, 2, 10, "第二页");
                    await Task.Delay(100);
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.history-page-2.png")
                    );

                    viewModel.HistoryPageSize = 20;
                    await viewModel.RefreshHistoryCommand.ExecuteAsync(null);
                    EnsurePage(viewModel, 23, 2, 1, 20, "每页二十条");
                    await Task.Delay(100);
                    Capture(
                        content,
                        Path.Combine(
                            outputDirectory,
                            "StartExperimentPage.history-page-size-20.png"
                        )
                    );

                    viewModel.HistorySearchText = "SUB-00018";
                    await Task.Delay(400);
                    EnsurePage(viewModel, 1, 1, 1, 1, "搜索筛选");
                    await Task.Delay(100);
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.history-filtered.png")
                    );

                    await viewModel.ResetHistoryFiltersCommand.ExecuteAsync(null);
                    EnsurePage(viewModel, 23, 3, 1, 10, "重置筛选");
                    viewModel.HistoryRecords[0].ToggleAllRunsCommand.Execute(null);
                    if (viewModel.SelectedHistoryRunCount != 1)
                        throw new InvalidOperationException("批量导出选择状态未正确显示。");
                    window.Width = 1200;
                    window.Height = 760;
                    await Task.Delay(120);
                    Capture(
                        content,
                        Path.Combine(
                            outputDirectory,
                            "StartExperimentPage.history-minimum-size.png"
                        )
                    );
                    window.Width = 1440;
                    window.Height = 900;
                    await Task.Delay(120);

                    var record = viewModel.HistoryRecords[0];
                    var expander = await WaitForExpanderAsync(content);

                    await Task.Delay(100);
                    var collapsedHeight = expander.Bounds.Height;
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.history-collapsed.png")
                    );
                    EnsureHeight(collapsedHeight, 0, 0.5, "收起状态");

                    record.ToggleExpandedCommand.Execute(null);
                    await Task.Delay(120);
                    var expandingHeight = expander.Bounds.Height;
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.history-expanding.png")
                    );

                    await Task.Delay(220);
                    var expandedHeight = expander.Bounds.Height;
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.history-expanded.png")
                    );
                    EnsureIntermediateHeight(expandingHeight, expandedHeight, "展开动画");

                    record.ToggleExpandedCommand.Execute(null);
                    await Task.Delay(120);
                    var collapsingHeight = expander.Bounds.Height;
                    Capture(
                        content,
                        Path.Combine(outputDirectory, "StartExperimentPage.history-collapsing.png")
                    );
                    EnsureIntermediateHeight(collapsingHeight, expandedHeight, "收回动画");

                    await Task.Delay(220);
                    var finalCollapsedHeight = expander.Bounds.Height;
                    Capture(
                        content,
                        Path.Combine(
                            outputDirectory,
                            "StartExperimentPage.history-collapsed-final.png"
                        )
                    );
                    EnsureHeight(finalCollapsedHeight, 0, 0.5, "最终收起状态");
                    await File.WriteAllTextAsync(
                        Path.Combine(
                            outputDirectory,
                            "StartExperimentPage.history-animation-metrics.txt"
                        ),
                        $"collapsed={collapsedHeight:F2}{Environment.NewLine}"
                            + $"expanding={expandingHeight:F2}{Environment.NewLine}"
                            + $"expanded={expandedHeight:F2}{Environment.NewLine}"
                            + $"collapsing={collapsingHeight:F2}{Environment.NewLine}"
                            + $"collapsed-final={finalCollapsedHeight:F2}{Environment.NewLine}"
                    );
                    Environment.ExitCode = 0;
                }
                catch (Exception exception)
                {
                    await WriteErrorAsync(exception);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    window.Close();
                    desktop.Shutdown(Environment.ExitCode);
                }
            };
        }
        catch (Exception exception)
        {
            WriteErrorAsync(exception).GetAwaiter().GetResult();
            Environment.ExitCode = 1;
            desktop.Shutdown(1);
        }
    }

    private static async Task WriteErrorAsync(Exception exception)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!);
        await File.WriteAllTextAsync(VisualTestOptions.Current.ErrorPath, exception.ToString());
    }

    private static async Task<AnimatedExpander> WaitForExpanderAsync(Control content)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < VisualTestOptions.Current.Timeout)
        {
            if (
                content.GetVisualDescendants().OfType<AnimatedExpander>().FirstOrDefault() is
                { } expander
            )
            {
                return expander;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("等待运行记录动画控件进入视觉树超时。");
    }

    private static void EnsureIntermediateHeight(double actual, double expanded, string stage)
    {
        if (expanded <= 0.5 || actual <= 0.5 || actual >= expanded - 0.5)
        {
            throw new InvalidOperationException(
                $"{stage}没有逐帧占用空间：中间高度 {actual:F2}，展开高度 {expanded:F2}。"
            );
        }
    }

    private static void EnsureHeight(double actual, double minimum, double maximum, string stage)
    {
        if (actual < minimum || actual > maximum)
            throw new InvalidOperationException($"{stage}高度异常：{actual:F2}。");
    }

    private static void EnsurePage(
        StartExperimentPageViewModel viewModel,
        int totalCount,
        int totalPages,
        int pageNumber,
        int itemCount,
        string stage
    )
    {
        if (
            viewModel.HistoryTotalCount != totalCount
            || viewModel.HistoryTotalPages != totalPages
            || viewModel.HistoryPageNumber != pageNumber
            || viewModel.HistoryRecords.Count != itemCount
        )
        {
            throw new InvalidOperationException(
                $"{stage}异常：总数 {viewModel.HistoryTotalCount}，总页数 {viewModel.HistoryTotalPages}，"
                    + $"当前页 {viewModel.HistoryPageNumber}，当前条数 {viewModel.HistoryRecords.Count}。"
            );
        }
    }

    private static void Capture(Control content, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var width = Math.Max(1, (int)Math.Ceiling(content.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(content.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(content);
        using var stream = File.Create(outputPath);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private sealed class VisualPersistenceService : IExperimentPersistenceService
    {
        private static readonly DateTimeOffset StartedAt = new(
            2026,
            8,
            28,
            9,
            30,
            0,
            TimeSpan.FromHours(8)
        );

        public Task<string> PreviewExperimentCodeAsync(
            DateTimeOffset scheduledAt,
            CancellationToken cancellationToken = default
        ) => Task.FromResult("EXP-20260828-003");

        public Task<ExperimentDraft> CreateOrUpdateDraftAsync(
            long? draftId,
            DateTimeOffset scheduledAt,
            string subjectCode,
            string remarks,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ExperimentDraft(1, "EXP-20260828-003", subjectCode));

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
        ) =>
            Task.FromResult<IReadOnlyList<ExperimentHistoryItem>>(
                Enumerable
                    .Range(1, 23)
                    .Select(index =>
                    {
                        var startedAt = StartedAt.AddDays(-(index - 1));
                        var kind = (index % 5) switch
                        {
                            0 => "Sham",
                            1 => "TDcs",
                            2 => "TAcs",
                            3 => "TRns",
                            _ => "TPcs",
                        };
                        var status =
                            index % 3 == 0
                                ? ExperimentRunStatus.InterruptedByUser
                                : ExperimentRunStatus.Completed;
                        return CreateHistory(
                            index,
                            $"EXP-{startedAt:yyyyMMdd}-{index:000}",
                            $"SUB-{index:00000}",
                            kind,
                            status,
                            startedAt
                        );
                    })
                    .ToArray()
            );

        public string CreateRandomSubjectCode() => "00000001";

        private static ExperimentHistoryItem CreateHistory(
            long id,
            string code,
            string subject,
            string kind,
            ExperimentRunStatus status,
            DateTimeOffset startedAt
        ) =>
            new(
                id,
                code,
                subject,
                kind,
                2,
                ["Fz", "Cz", "CP4"],
                1,
                startedAt,
                [
                    new ExperimentHistoryRunItem(
                        Guid.NewGuid(),
                        status,
                        startedAt,
                        startedAt,
                        startedAt.AddMinutes(2).AddSeconds(15),
                        $"C:\\recordings\\{id}.eegraw"
                    ),
                ]
            );
    }

    private sealed class VisualSubjectLookupService : ISubjectLookupService
    {
        public Task<IReadOnlyList<string>> SearchAsync(
            string query,
            int limit = 10,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> ExistsAsync(
            string subjectCode,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);
    }

    private sealed class VisualPackageSerializer : IExperimentPackageSerializer
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

    private sealed class VisualCleanupService : ITemporaryEegCleanupService
    {
        public Task CleanupAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class VisualBatchExportService : IEegBatchExportService
    {
        public Task<EegBatchExportResult> ExportAsync(
            IReadOnlyList<Guid> runIds,
            EegExportSelection selection,
            string targetParentDirectory,
            IProgress<EegBatchExportProgress>? progress = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new EegBatchExportResult(targetParentDirectory, [], []));
    }

    private sealed class VisualRawPacketReader : IEegRawPacketReader
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

    private sealed class VisualDialogProvider : IDialogProvider
    {
        public System.Collections.ObjectModel.ObservableCollection<DialogViewModel> DialogStack { get; } =
        [];
    }
}
