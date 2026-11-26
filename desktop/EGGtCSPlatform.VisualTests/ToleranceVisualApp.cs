using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using EGGtCSPlatform.Views.Pages;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.VisualTests;

public sealed class ToleranceVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new Window
        {
            Width = 1440,
            Height = 900,
            WindowDecorations = WindowDecorations.None,
            Background = Brush.Parse("#F0F3FA"),
            Title = "耐受度测试视觉验收",
        };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var directory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
            Directory.CreateDirectory(directory);
            try
            {
                var dbFactory = new Factory(
                    Path.Combine(directory, $"tolerance-visual-{Guid.NewGuid():N}.db")
                );
                var op = new CurrentOperatorContext();
                await using (var db = dbFactory.CreateDbContext())
                {
                    await db.Database.MigrateAsync();
                    var entity = new OperatorEntity
                    {
                        Username = "visual",
                        NormalizedUsername = "VISUAL",
                        PasswordSalt = [1],
                        PasswordHash = [1],
                        PasswordIterations = 1,
                    };
                    db.Add(entity);
                    await db.SaveChangesAsync();
                    op.Set(entity.Id, entity.Username);
                }
                var capability = StimulusCapabilityProfile.FromOptions(
                    new StimulationChannelOptions { FixedActivePhysicalChannelIds = [1] }
                );
                var sessions = new ToleranceSessionFactory(capability);
                var persistence = new TolerancePersistenceService(dbFactory, op, capability);
                var ordinary = new ExperimentPersistenceService(dbFactory, op, null!);
                var host = new Host();
                var dialog = new ToleranceSubjectDialogViewModel(
                    ordinary,
                    new SubjectLookupService(dbFactory)
                );
                window.Content = new ToleranceSubjectDialogView
                {
                    DataContext = dialog,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                await Capture("subject", 1000, 650);
                var subjectView = (ToleranceSubjectDialogView)window.Content;
                var subjectInput = subjectView.FindControl<TextBox>("SubjectIdInput")!;
                var remarksInput = subjectView.FindControl<TextBox>("RemarksInput")!;
                CheckInput(subjectInput, 4);
                CheckInput(remarksInput, 4);
                var subjectFontFamily = subjectInput.FontFamily.ToString();
                var subjectFontSize = subjectInput.FontSize;
                dialog.ConfirmCommand.Execute(null);
                if (dialog.Confirmed || string.IsNullOrWhiteSpace(dialog.ErrorText))
                    throw new Exception("空被试 ID 未拦截。");
                subjectInput.Focus();
                subjectInput.RaiseEvent(
                    new TextInputEventArgs
                    {
                        RoutedEvent = InputElement.TextInputEvent,
                        Text = "SUB-KEYBOARD",
                    }
                );
                if (dialog.SubjectId != "SUB-KEYBOARD")
                    throw new Exception("被试 ID 键盘输入绑定失败。");
                await Capture("subject-focused", 1000, 650);
                CheckFocusedInput(subjectInput);
                // Tab goes through the random-ID button before reaching remarks.
                subjectInput.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab }
                );
                (window.FocusManager!.GetFocusedElement() as InputElement)!.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab }
                );
                if (!remarksInput.IsFocused)
                    throw new Exception("被试弹窗 Tab 顺序错误。");
                remarksInput.RaiseEvent(
                    new TextInputEventArgs
                    {
                        RoutedEvent = InputElement.TextInputEvent,
                        Text = "键盘备注",
                    }
                );
                if (dialog.Remarks != "键盘备注")
                    throw new Exception("备注键盘输入绑定失败。");
                CheckFocusedInput(remarksInput);
                await dialog.GenerateCommand.ExecuteAsync(null);
                if (string.IsNullOrEmpty(dialog.SubjectId))
                    throw new Exception("随机被试 ID 生成失败");
                dialog.SubjectId = "EXP-20260707-001";
                dialog.Remarks = "模拟调流验收";
                dialog.ConfirmCommand.Execute(null);
                if (!dialog.Confirmed)
                    throw new Exception("被试弹窗未确认");
                var context = new ToleranceTestContext(dialog.SubjectId, dialog.Remarks);
                var configuration = new StimulusConfigurationPageViewModel(
                    new(
                        "耐受度测试",
                        context.SubjectId,
                        CreationMode: ExperimentCreationMode.StimulusOnly,
                        Tolerance: context
                    ),
                    host,
                    new DialogService(() => window),
                    host,
                    capability
                );
                var shell = new BasicView
                {
                    DataContext = new BasicViewModel(
                        host,
                        configuration,
                        new DeviceSelectionContext(),
                        false
                    ),
                };
                window.Content = shell;
                await Capture("configuration", 1440, 900);
                if (
                    configuration.Modes.Count != 5
                    || configuration.CurrentConfiguration.CanEditCurrent
                )
                    throw new Exception("范式/电流限制失败");
                await configuration.SaveConfigurationCommand.ExecuteAsync(null);
                var stimulus = configuration.CurrentConfiguration.CreateSnapshot() with { Direction = StimulusDirection.Negative };
                var targetId = stimulus.Targets[0].TargetId;
                StimulusElectrodeAssignment[] assignments =
                [
                    new("FC5", targetId, 1, StimulationChannelRole.FixedActive),
                    new("P3", targetId, 2, StimulationChannelRole.Selectable),
                ];
                var template = new ExperimentConfigurationTemplate(
                    stimulus,
                    assignments,
                    [],
                    "",
                    "",
                    500,
                    new(ExperimentRunMode.Manual, 0, 0, 600_000, 0, 1),
                    "耐受度验收",
                    CreationMode: ExperimentCreationMode.StimulusOnly
                );
                using var electrode = new ElectrodeConfigurationPageViewModel(
                    host.Electrodes! with
                    {
                        ImportedTemplate = template,
                    },
                    host,
                    new DialogService(() => window),
                    host,
                    capability,
                    new PreviewImpedanceDetectionService(),
                    new ImpedanceDetectionOptions { AllowConfirmationWhenFailed = false }
                );
                ((BasicViewModel)shell.DataContext!).CurrentPage = electrode;
                await electrode.ToggleDetectionCommand.ExecuteAsync(null);
                // Preview readings converge; wait for the existing detection pipeline to stop itself.
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (electrode.IsAnyDetecting && DateTime.UtcNow < deadline)
                    await Task.Delay(100);
                if (!electrode.CanOpenConfirmation)
                {
                    await electrode.ToggleDetectionCommand.ExecuteAsync(null);
                    deadline = DateTime.UtcNow.AddSeconds(10);
                    while (electrode.IsAnyDetecting && DateTime.UtcNow < deadline)
                        await Task.Delay(100);
                }
                if (!electrode.CanOpenConfirmation)
                    throw new Exception("刺激阻抗未通过");
                await Capture("electrodes", 1440, 900);
                var confirming = electrode.OpenConfirmationCommand.ExecuteAsync(null);
                deadline = DateTime.UtcNow.AddSeconds(5);
                while (
                    (host.DialogStack.Count == 0 || !host.DialogStack.Last().IsDialogOpen)
                    && DateTime.UtcNow < deadline
                )
                    await Task.Delay(20);
                window.Content = new ExperimentConfigurationDialogView { DataContext = host.DialogStack.Last() };
                await Capture("configuration-confirmation", 1440, 900);
                window.Content = shell;
                (
                    (ExperimentConfigurationDialogViewModel)host.DialogStack.Last()
                ).ConfirmCommand.Execute(null);
                await confirming;
                if (host.Tolerance is null || host.StartedOrdinaryRun)
                    throw new Exception("耐受度导航错误");
                using var model = new ToleranceTestPageViewModel(
                    host.Tolerance,
                    sessions,
                    persistence
                );
                ((BasicViewModel)shell.DataContext!).CurrentPage = model;
                var emptyBounds = new Dictionary<int, (Size Page, Size Adjustment, Size Records)>();
                foreach (var size in new[] { (1440, 900), (1000, 650) })
                {
                    await Capture("empty", size.Item1, size.Item2);
                    emptyBounds[size.Item1] = PanelSizes();
                }
                var debug = DebugView();
                if (!debug.IsKeyboardFocusWithin)
                    throw new Exception("进入耐受度页后未获得键盘焦点。");
                var logo = shell.FindControl<Button>("HomeLogoButton")!;
                if (!logo.IsEffectivelyEnabled || logo.Bounds.Size != new Size(182, 36))
                    throw new Exception("Logo 可用状态或尺寸错误。");
                logo.Command!.Execute(null);
                if (host.HomeRequests != 1)
                    throw new Exception("Logo 未路由至首页。");
                void RaiseKey(
                    InputElement source,
                    Avalonia.Interactivity.RoutedEvent<KeyEventArgs> routedEvent,
                    Key key,
                    KeyModifiers modifiers = KeyModifiers.None
                ) =>
                    source.RaiseEvent(
                        new KeyEventArgs
                        {
                            RoutedEvent = routedEvent,
                            Key = key,
                            KeyModifiers = modifiers,
                        }
                    );
                void Press(Key key, KeyModifiers modifiers = KeyModifiers.None)
                {
                    RaiseKey(debug, InputElement.KeyDownEvent, key, modifiers);
                    RaiseKey(debug, InputElement.KeyUpEvent, key, modifiers);
                }
                foreach (var key in new[] { Avalonia.Input.Key.Up, Avalonia.Input.Key.Right })
                {
                    var before = model.Current;
                    RaiseKey(debug, InputElement.KeyDownEvent, key);
                    RaiseKey(debug, InputElement.KeyDownEvent, key);
                    if (Math.Abs(model.Current - before - .04) > .00001)
                        throw new Exception("方向键长按重复调流或增量错误。");
                    RaiseKey(debug, InputElement.KeyUpEvent, key);
                }
                Press(Avalonia.Input.Key.Down);
                Press(Avalonia.Input.Key.Left);
                if (model.Current != .04)
                    throw new Exception("方向键减小电流失败。");
                foreach (
                    var modifier in new[]
                    {
                        KeyModifiers.Control,
                        KeyModifiers.Shift,
                        KeyModifiers.Alt,
                        KeyModifiers.Meta,
                    }
                )
                    Press(Avalonia.Input.Key.Up, modifier);
                if (model.Current != .04)
                    throw new Exception("组合键错误触发调流。");
                for (var i = 0; i < 100; i++)
                    Press(Avalonia.Input.Key.Right);
                if (model.CanIncrease)
                    throw new Exception("方向键未到达上限。");
                for (var i = 0; i < 100; i++)
                    Press(Avalonia.Input.Key.Left);
                if (model.Current != .04)
                    throw new Exception("方向键下限错误。");
                model.IsSaving = true;
                Press(Avalonia.Input.Key.Up);
                logo.Command.Execute(null);
                if (model.Current != .04 || logo.IsEffectivelyEnabled || host.HomeRequests != 1)
                    throw new Exception("保存时键盘或 Logo 未锁定。");
                model.IsSaving = false;
                if (!logo.IsEffectivelyEnabled)
                    throw new Exception("保存后 Logo 未恢复。");
                var focusedSave = debug.FindControl<Button>("SaveResultButton")!;
                focusedSave.Focus();
                RaiseKey(focusedSave, InputElement.KeyDownEvent, Avalonia.Input.Key.Up);
                RaiseKey(focusedSave, InputElement.KeyUpEvent, Avalonia.Input.Key.Up);
                if (model.Current != .08)
                    throw new Exception("子控件获得焦点时方向键失效。");
                RaiseKey(debug, InputElement.KeyDownEvent, Avalonia.Input.Key.Up);
                var currentBeforeDialog = model.Current;
                var dialogInput = new TextBox { Text = "弹窗焦点验证" };
                var focusDialog = new Window
                {
                    Width = 320,
                    Height = 160,
                    Content = dialogInput,
                };
                var dialogClosed = focusDialog.ShowDialog(window);
                try
                {
                    await Task.Delay(100);
                    dialogInput.Focus();
                    RaiseKey(dialogInput, InputElement.KeyDownEvent, Avalonia.Input.Key.Right);
                    RaiseKey(dialogInput, InputElement.KeyUpEvent, Avalonia.Input.Key.Right);
                    if (model.Current != currentBeforeDialog)
                        throw new Exception("弹窗获得焦点时仍在调流。");
                }
                finally
                {
                    focusDialog.Close();
                }
                await dialogClosed;
                window.Activate();
                await Task.Delay(100);
                debug.Focus();
                Press(Avalonia.Input.Key.Up);
                if (Math.Abs(model.Current - currentBeforeDialog - .04) > .00001)
                    throw new Exception("窗口失焦后按键状态未清理。");
                model.ResetCommand.Execute(null);
                await Task.Delay(100);
                var saveButton = debug.FindControl<Button>("SaveResultButton")!;
                var savePositionBefore = saveButton.TranslatePoint(default, window);
                for (var i = 0; i < 3; i++)
                {
                    model.IncreaseCommand.Execute(null);
                    await model.SaveCommand.ExecuteAsync(null);
                    await Task.Delay(100);
                    if (saveButton.TranslatePoint(default, window) != savePositionBefore)
                        throw new Exception("保存后调流按钮发生位移。");
                }
                if (model.Records.Count != 3)
                    throw new Exception(model.StatusText);
                model.ResetCommand.Execute(null);
                if (model.Current != .04 || model.Records.Count != 3)
                    throw new Exception("重置错误");
                foreach (var size in new[] { (1440, 900), (1000, 650) })
                {
                    await Capture("records", size.Item1, size.Item2);
                    if (PanelSizes() != emptyBounds[size.Item1])
                        throw new Exception("3 条记录改变了页面或面板大小。");
                }
                // Stress the view without adding synthetic records to the history test database.
                using var manyRecords = new ToleranceTestPageViewModel(
                    host.Tolerance,
                    sessions,
                    new SnapshotPreviewPersistence(model.Records[0])
                );
                ((BasicViewModel)shell.DataContext!).CurrentPage = manyRecords;
                for (var i = 0; i < 99; i++)
                    await manyRecords.SaveCommand.ExecuteAsync(null);
                await Capture("records-99", 1000, 650);
                var detachedCurrent = model.Current;
                Press(Avalonia.Input.Key.Up);
                if (model.Current != detachedCurrent)
                    throw new Exception("已离开的页面仍接受调流快捷键。");
                var stressView = DebugView();
                var recordsScroll = stressView.FindControl<ScrollViewer>("RecordsScroll")!;
                recordsScroll.Offset = new Vector(0, 100);
                await Task.Delay(50);
                var previousOffset = recordsScroll.Offset;
                await manyRecords.SaveCommand.ExecuteAsync(null);
                await Task.Delay(50);
                if (manyRecords.Records.Count != 100 || recordsScroll.Offset != previousOffset)
                    throw new Exception("追加记录导致滚动位置跳动。");
                foreach (var size in new[] { (1440, 900), (1000, 650) })
                {
                    recordsScroll.Offset = default;
                    await Capture("records-100", size.Item1, size.Item2);
                    if (PanelSizes() != emptyBounds[size.Item1])
                        throw new Exception("100 条记录改变了页面或面板大小。");
                    if (
                        recordsScroll.Extent.Height <= recordsScroll.Viewport.Height
                        || recordsScroll.HorizontalScrollBarVisibility
                            != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                    )
                        throw new Exception("记录列表没有独立的垂直滚动范围。");
                    var title = stressView.FindControl<TextBlock>("RecordsTitle")!;
                    var recordsHeader = stressView.FindControl<Border>("RecordsHeader")!;
                    var titlePosition = title.TranslatePoint(default, window);
                    var headerPosition = recordsHeader.TranslatePoint(default, window);
                    Wheel(recordsScroll);
                    await Task.Delay(50);
                    if (recordsScroll.Offset.Y <= 0)
                        throw new Exception("记录列表滚轮未生效。");
                    recordsScroll.ScrollToEnd();
                    await Capture("records-100-bottom", size.Item1, size.Item2);
                    if (
                        Math.Abs(
                            recordsScroll.Offset.Y
                                - (recordsScroll.Extent.Height - recordsScroll.Viewport.Height)
                        ) > 1
                        || title.TranslatePoint(default, window) != titlePosition
                        || recordsHeader.TranslatePoint(default, window) != headerPosition
                    )
                        throw new Exception("记录不能滚动到底部或表头发生移动。");
                    recordsScroll.ScrollToHome();
                    var adjustmentScroll = stressView.FindControl<ScrollViewer>(
                        "AdjustmentScroll"
                    )!;
                    adjustmentScroll.ScrollToHome();
                    await Task.Delay(50);
                    Wheel(adjustmentScroll);
                    await Task.Delay(50);
                    if (adjustmentScroll.Extent.Height > adjustmentScroll.Viewport.Height + 1 && adjustmentScroll.Offset.Y <= 0)
                        throw new Exception("小窗口调流区滚轮未生效。");
                    var save = stressView.FindControl<Button>("SaveResultButton")!;
                    save.BringIntoView();
                    await Capture("adjustment-scrolled", size.Item1, size.Item2);
                    var savePosition = save.TranslatePoint(default, adjustmentScroll)!.Value;
                    if (
                        savePosition.Y < 0
                        || savePosition.Y + save.Bounds.Height > adjustmentScroll.Bounds.Height + 1
                        || recordsScroll.Offset.Y != 0
                    )
                        throw new Exception("保存按钮不可达或记录列表无法返回顶部。");
                }
                ToleranceTestPageView DebugView() =>
                    shell.GetVisualDescendants().OfType<ToleranceTestPageView>().Single();
                (Size, Size, Size) PanelSizes()
                {
                    var view = DebugView();
                    return (
                        view.Bounds.Size,
                        view.FindControl<Border>("AdjustmentPanel")!.Bounds.Size,
                        view.FindControl<Border>("RecordsPanel")!.Bounds.Size
                    );
                }
                using var historyPage = new StartExperimentPageViewModel(
                    host,
                    ordinary,
                    new SubjectLookupService(dbFactory),
                    null!,
                    new DialogService(() => window),
                    host,
                    new DeviceSelectionContext(),
                    new NoCleanup(),
                    null!,
                    new ExperimentConfigurationTransferContext(),
                    tolerancePersistence: persistence
                );
                ((BasicViewModel)shell.DataContext!).CurrentPage = historyPage;
                await Capture("new-experiment-inputs", 1000, 650);
                var ordinaryInputs = shell
                    .GetVisualDescendants()
                    .OfType<TextBox>()
                    .Where(input => input.Classes.Contains("experiment-input"))
                    .ToArray();
                if (ordinaryInputs.Length != 2)
                    throw new Exception("新建实验输入框缺失。");
                foreach (var input in ordinaryInputs)
                {
                    CheckInput(input);
                    input.Focus();
                    CheckFocusedInput(input);
                    if (
                        input.FontFamily.ToString() != subjectFontFamily
                        || input.FontSize != subjectFontSize
                    )
                        throw new Exception("输入框字体不一致。");
                }
                await Capture("new-experiment-input-focused", 1000, 650);
                await historyPage.SelectToleranceHistoryCommand.ExecuteAsync(null);
                var history = historyPage.ToleranceHistory!;
                if (history.Groups.Count != 1 || history.Groups[0].Count != 3)
                    throw new Exception("分组历史错误");
                ((BasicViewModel)shell.DataContext!).CurrentPage = historyPage;
                window.Content = shell;
                await Capture("history-collapsed", 1440, 900);
                var historyView = shell
                    .GetVisualDescendants()
                    .OfType<ToleranceHistoryView>()
                    .Single();
                var datePickers = historyView
                    .GetVisualDescendants()
                    .OfType<DateSelectionPicker>()
                    .ToArray();
                if (
                    datePickers.Length != 2
                    || datePickers.Any(picker =>
                        picker
                            .GetVisualDescendants()
                            .OfType<TextBox>()
                            .FirstOrDefault(t => t.Name == "PART_TextBox")
                            is not { IsReadOnly: true }
                    )
                )
                    throw new Exception("耐受度未复用只读日期组件。");
                if (historyView.GetVisualDescendants().OfType<Expander>().Any())
                    throw new Exception("仍在使用默认展开器。");
                history.Groups[0].ToggleExpandedCommand.Execute(null);
                foreach (var size in new[] { (1440, 900), (1000, 650) })
                    await Capture("history-expanded", size.Item1, size.Item2);
                var animated = historyView
                    .GetVisualDescendants()
                    .OfType<AnimatedExpander>()
                    .First();
                if (animated.Bounds.Height <= 0)
                    throw new Exception("测试记录未展开。");
                var header = historyView.FindControl<Grid>("ToleranceTableHeader")!;
                var groupGrid = historyView
                    .GetVisualDescendants()
                    .OfType<Grid>()
                    .First(g => g.Classes.Contains("tolerance-group-columns"));
                for (var column = 0; column < header.Children.Count; column++)
                    if (
                        Math.Abs(
                            header.Children[column].Bounds.X - groupGrid.Children[column].Bounds.X
                        ) > 2
                    )
                        throw new Exception($"耐受度第 {column + 1} 列未对齐。");
                history.Groups[0].ToggleExpandedCommand.Execute(null);
                await Task.Delay(350);
                if (animated.Bounds.Height > .5)
                    throw new Exception("测试记录未收起。");
                for (var i = 0; i < 22; i++)
                {
                    var additional = host.Tolerance with
                    {
                        Context = new($"SUB-ADDITIONAL-{i:D3}", "分页验证"),
                    };
                    if (i == 21)
                    {
                        var id = additional.Configuration.Targets[0].TargetId;
                        additional = additional with
                        {
                            Context = new(
                                "SUB-VERY-LONG-SUBJECT-IDENTIFIER-20261030-ABCDEFGHIJKLMNOPQRSTUV",
                                string.Concat(
                                    Enumerable.Repeat(
                                        "较长备注用于检查展开记录的自动换行与完整显示。",
                                        8
                                    )
                                )
                            ),
                            Configuration = additional.Configuration with
                            {
                                ArrayMode = StimulusArrayMode.Hd,
                            },
                            Electrodes =
                            [
                                new("FC5", id, 1, StimulationChannelRole.FixedActive),
                                new("P3", id, 2, StimulationChannelRole.Selectable),
                                new("F3", id, 3, StimulationChannelRole.Selectable),
                                new("Cz", id, 4, StimulationChannelRole.Selectable),
                                new("T7", id, 5, StimulationChannelRole.Selectable),
                            ],
                        };
                    }
                    await persistence.SaveAsync(additional, i == 21 ? .16 : .04);
                }
                await history.LoadAsync();
                await Capture("history-many", 1440, 900);
                var pager = historyView.FindControl<HistoryPagination>("TolerancePaginationBar")!;
                if (pager.TotalCount != 23 || pager.Unit != "组" || history.Groups.Count != 10)
                    throw new Exception("配置组分页错误。");
                history.Groups[0].ToggleExpandedCommand.Execute(null);
                await Capture("history-long-content", 1440, 900);
                await Capture("history-long-content", 1000, 650);
                var tableScroll = historyView.FindControl<ScrollViewer>("ToleranceTableScroll")!;
                if (tableScroll.Extent.Width <= tableScroll.Viewport.Width)
                    throw new Exception("窄窗口表格缺少横向滚动。");
                tableScroll.Offset = new Vector(tableScroll.Extent.Width, 0);
                await Capture("history-horizontal-scroll", 1000, 650);
                tableScroll.Offset = default;
                pager.FindControl<ComboBox>("PageSizeSelector")!.SelectedItem = 20;
                await WaitForHistory();
                if (history.PageSize != 20 || history.Groups.Count != 20 || history.Page != 1)
                    throw new Exception("每页20组切换失败。");
                await ((IAsyncRelayCommand)pager.NextCommand!).ExecuteAsync(null);
                if (history.Groups.Count != 3 || history.Page != 2 || pager.CanNext)
                    throw new Exception("末页边界错误。");
                await Capture("history-page-two", 1440, 900);
                pager.FindControl<ComboBox>("PageSizeSelector")!.SelectedItem = 50;
                await WaitForHistory();
                if (history.Groups.Count != 23 || history.Page != 1)
                    throw new Exception("每页50组切换失败。");
                datePickers[0].SelectedDate = DateTime.Today;
                datePickers[1].SelectedDate = DateTime.Today;
                var search = historyView.FindControl<TextBox>("ToleranceSearchBox")!;
                search.Text = context.SubjectId;
                await ((IAsyncRelayCommand)search.KeyBindings.Single().Command!).ExecuteAsync(null);
                if (
                    history.Groups.Count != 1
                    || history.Groups[0].Count != 3
                    || history.StartDate != DateTime.Today
                    || history.EndDate != DateTime.Today
                )
                    throw new Exception("日期/回车搜索绑定错误。");
                search.Text = "NO-MATCH";
                await history.SearchCommand.ExecuteAsync(null);
                await Capture("history-empty", 1440, 900);
                history.StartDate = DateTime.Today.AddDays(1);
                await history.SearchCommand.ExecuteAsync(null);
                if (!history.HasError)
                    throw new Exception("日期错误未显示。");
                await Capture("history-error", 1440, 900);
                await history.ResetCommand.ExecuteAsync(null);
                if (
                    history.PageSize != 10
                    || history.StartDate is not null
                    || history.EndDate is not null
                    || history.TotalCount != 23
                )
                    throw new Exception("重置失败。");
                async Task WaitForHistory()
                {
                    var timeout = DateTime.UtcNow.AddSeconds(5);
                    while (history.IsLoading && DateTime.UtcNow < timeout)
                        await Task.Delay(20);
                    if (history.IsLoading || history.HasError)
                        throw new Exception(history.StatusText);
                }
                File.WriteAllText(
                    Path.Combine(directory, "Tolerance.passed.txt"),
                    "subject, configuration, stimulation impedance, route, current, save, reset, grouped history: passed"
                );
                desktop.Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(VisualTestOptions.Current.ErrorPath, error.ToString());
                desktop.Shutdown(1);
            }
            async Task Capture(string name, int width, int height)
            {
                window.Width = width;
                window.Height = height;
                await Task.Delay(350);
                var label = name switch
                {
                    "configuration" => "进入电极配置与检测",
                    "electrodes" => "查看并确认实验配置",
                    "configuration-confirmation" => "确认参数配置",
                    "empty" => "保存本次耐受度数据",
                    _ => null,
                };
                if (label != null && width == 1440)
                {
                    var button = window.GetVisualDescendants().OfType<TextBlock>()
                        .First(t => t.IsEffectivelyVisible && t.Text == label)
                        .FindAncestorOfType<Button>()!;
                    foreach (var scale in new[] { 1, 2 })
                    {
                        using var sample = new RenderTargetBitmap(
                            PixelSize.FromSize(button.Bounds.Size, scale), new Vector(96 * scale, 96 * scale)
                        );
                        sample.Render(button);
                        sample.Save(Path.Combine(directory, $"{name}-button@{scale}x.png"), PngBitmapEncoderOptions.Default);
                    }
                }
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
                bitmap.Render(window);
                bitmap.Save(
                    Path.Combine(directory, $"Tolerance.{name}-{width}.png"),
                    PngBitmapEncoderOptions.Default
                );
            }
        };
        window.Show();
    }

    private sealed class NoCleanup : ITemporaryEegCleanupService
    {
        public Task CleanupAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static void CheckInput(TextBox input, int cornerRadius = 0)
    {
        if (
            input.Height != 32
            || input.Padding != new Thickness(12, 4)
            || input.BorderThickness != new Thickness(1)
            || input.CornerRadius != new CornerRadius(cornerRadius)
            || input.BorderBrush?.ToString() != "#ffd9dee6"
        )
            throw new Exception("输入框未应用共享新建实验样式。");
    }

    private static void Wheel(ScrollViewer scroll)
    {
        var root = TopLevel.GetTopLevel(scroll)!;
        var position = scroll
            .TranslatePoint(new Point(40, Math.Min(30, scroll.Bounds.Height / 2)), root)!
            .Value;
        var hit =
            root.InputHitTest(position) as InputElement ?? throw new Exception("滚轮未命中列表。");
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        hit.RaiseEvent(
            new PointerWheelEventArgs(
                hit,
                pointer,
                root,
                position,
                0,
                new PointerPointProperties(),
                KeyModifiers.None,
                new Vector(0, -3)
            )
        );
    }

    private static void CheckFocusedInput(TextBox input)
    {
        var border = input
            .GetVisualDescendants()
            .OfType<Border>()
            .Single(b => b.Name == "PART_BorderElement");
        if (!input.IsFocused || border.BorderBrush?.ToString() != "#ff3941b6")
            throw new Exception("输入框聚焦样式错误。");
    }

    private sealed class SnapshotPreviewPersistence(ToleranceResultSnapshot seed)
        : ITolerancePersistenceService
    {
        public Task<ToleranceResultSnapshot> SaveAsync(
            ToleranceTestRouteData route,
            double current,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(seed with { Id = Guid.NewGuid(), Test = route, Current = current });

        public Task<ToleranceHistoryPage> QueryAsync(
            ToleranceHistoryQuery query,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={path};Pooling=False")
                    .Options
            );
    }

    private sealed class Host : INavigationRouter, IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
        public ElectrodeConfigurationRouteData? Electrodes;
        public ToleranceTestRouteData? Tolerance;
        public bool StartedOrdinaryRun;
        public int HomeRequests;

        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData route) { }

        public void Navigate(StimulusConfigurationRouteData route) { }

        public void Navigate(ElectrodeConfigurationRouteData route) => Electrodes = route;

        public void Navigate(ToleranceTestRouteData route) => Tolerance = route;

        public void Navigate(ExperimentRunRouteData route) => StartedOrdinaryRun = true;

        public void Navigate(ExperimentRerunRouteData route) => StartedOrdinaryRun = true;

        public void GoBack() { }

        public void GoHome() => HomeRequests++;
    }
}
