using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
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

public sealed class SingleStimulusVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var source = ExperimentRunRouteDataDefaults.Create();
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            [],
            "",
            "",
            500,
            creationMode: ExperimentCreationMode.StimulusOnly
        );
        var service = new VisualRunService();
        var model = new SingleStimulusExperimentDialogViewModel(route, service);
        var view = new SingleStimulusExperimentDialogView
        {
            DataContext = model,
            Background = Brush.Parse("#526078"),
        };
        var window = new Window
        {
            Width = 560,
            Height = 450,
            Content = view,
            Background = Brush.Parse("#526078"),
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = "单刺激实验视觉验收",
        };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
                Directory.CreateDirectory(output);
                await Capture(view, Path.Combine(output, "SingleStimulus.ready.png"));
                VerifyActions(view, completion: false, disabledText: "紧急停止");
                var running = model.StartCommand.ExecuteAsync(null);
                if (!model.IsRunning || model.CanClose || model.CanEdit)
                    throw new InvalidOperationException("运行锁定失败");
                service.Progress(37);
                await Capture(view, Path.Combine(output, "SingleStimulus.running.png"));
                VerifyActions(view, completion: false, disabledText: "开始实验");
                if (model.RemainingText != "9分23秒")
                    throw new InvalidOperationException("倒计时错误");
                service.Completion.TrySetResult();
                await running;
                if (!model.IsFinished || model.StatusText != "已结束")
                    throw new InvalidOperationException("完成状态错误");
                await Capture(view, Path.Combine(output, "SingleStimulus.completed.png"));
                VerifyActions(view, completion: true);
                model.CloseExperimentCommand.Execute(null);
                service.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var stopModel = new SingleStimulusExperimentDialogViewModel(route, service);
                view.DataContext = stopModel;
                var stopped = stopModel.StartCommand.ExecuteAsync(null);
                service.Progress(37);
                service.StopFailure = new IOException("设备未响应停止指令");
                await stopModel.EmergencyStopCommand.ExecuteAsync(null);
                await stopped;
                await Capture(view, Path.Combine(output, "SingleStimulus.stop-failed.png"));
                VerifyActions(view, completion: false, disabledText: "开始实验");
                if (stopModel.CanClose || !stopModel.CanEmergencyStop)
                    throw new InvalidOperationException("停止失败未锁定");
                service.StopFailure = null;
                await stopModel.EmergencyStopCommand.ExecuteAsync(null);
                await Capture(view, Path.Combine(output, "SingleStimulus.stopped.png"));
                VerifyActions(view, completion: true);
                if (stopModel.RemainingText != "9分23秒")
                    throw new InvalidOperationException("急停后倒计时变化");
                if (stopModel.HasError)
                    throw new InvalidOperationException("急停重试成功后残留错误提示");
                using var longRun = new SingleStimulusExperimentDialogViewModel(route, service);
                view.DataContext = longRun;
                longRun.Duration.SelectedUnit = longRun.Duration.Units[1];
                longRun.Duration.DurationValue = 65535;
                var longRunning = longRun.StartCommand.ExecuteAsync(null);
                await Capture(view, Path.Combine(output, "SingleStimulus.long-duration.png"));
                await longRun.EmergencyStopCommand.ExecuteAsync(null);
                await longRunning;
                var history = new ExperimentRunRouteData(
                    route.ExperimentId,
                    route.SubjectId,
                    route.StimulusConfiguration,
                    route.StimulusElectrodes,
                    [],
                    "",
                    "",
                    500,
                    historicalResult: new HistoricalExperimentRunContext(
                        Guid.NewGuid(),
                        "",
                        ExperimentRunStatus.InterruptedByUser,
                        DateTimeOffset.UtcNow.AddMinutes(-2),
                        DateTimeOffset.UtcNow,
                        new Dictionary<int, string>(),
                        [],
                        LogicalTimelineEndSeconds: 120
                    ),
                    creationMode: ExperimentCreationMode.StimulusOnly
                );
                using var historical = new SingleStimulusExperimentDialogViewModel(
                    history,
                    service
                );
                view.DataContext = historical;
                await Capture(view, Path.Combine(output, "SingleStimulus.history.png"));
                view.DataContext = model;
                var card = view.GetVisualDescendants().OfType<StimulusOverviewCard>().Single();
                foreach (var kind in Enum.GetValues<StimulusKind>())
                {
                    card.Descriptor = model.Waveform with { Kind = kind };
                    await Capture(
                        view,
                        Path.Combine(output, $"SingleStimulus.waveform-{kind}.png")
                    );
                }
                foreach (var ramp in new[] { 0d, 7d, 30d })
                {
                    card.Descriptor = model.Waveform with
                    {
                        Kind = StimulusKind.TDcs,
                        RampSeconds = ramp,
                        Direction = StimulusDirection.Negative,
                    };
                    await Capture(
                        view,
                        Path.Combine(output, $"SingleStimulus.tdcs-negative-ramp-{ramp}.png")
                    );
                }
                await VerifyConfirmationFlow();
                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!
                );
                await File.WriteAllTextAsync(
                    VisualTestOptions.Current.ErrorPath,
                    exception.ToString()
                );
                Environment.ExitCode = 1;
            }
            finally
            {
                model.Dispose();
                window.Close();
                desktop.Shutdown(Environment.ExitCode);
            }
        };
        window.Show();
    }

    private static void VerifyActions(Control view, bool completion, string? disabledText = null)
    {
        var actions = view.GetVisualDescendants().OfType<ExperimentRunActions>().Single();
        var visibleButtons = actions
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible)
            .ToArray();
        if (visibleButtons.Length != (completion ? 1 : 2))
            throw new InvalidOperationException("底部按钮未按状态切换");
        if (completion && visibleButtons[0].Bounds.Width < actions.Bounds.Width - 1)
            throw new InvalidOperationException("结束按钮未占满整行");
        foreach (
            var label in actions
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible)
        )
        {
            if (
                label.Foreground is not ISolidColorBrush { Color: var color }
                || color != Colors.White
            )
                throw new InvalidOperationException($"按钮文字不是白色：{label.Text}");
            if (label.Text == disabledText && label.IsEffectivelyEnabled)
                throw new InvalidOperationException($"按钮未禁用：{label.Text}");
        }
        var card = view.GetVisualDescendants().OfType<StimulusOverviewCard>().Single();
        if (card.ClipToBounds)
            throw new InvalidOperationException("刺激卡片仍裁剪阴影");
    }

    private static async Task VerifyConfirmationFlow()
    {
        var capability = StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions { FixedActivePhysicalChannelIds = [1, 8] }
        );
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var host = new DialogHost();
        var singleDialog = new DialogRecorder();
        var persistence = new TestPersistence();
        using var page = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData(
                "single",
                "subject",
                stimulus.CreateSnapshot(),
                ExperimentDatabaseId: 1,
                CreationMode: ExperimentCreationMode.StimulusOnly
            ),
            new VisualTestNavigationRouter(),
            new DialogService(() => null),
            host,
            capability: capability,
            experimentPersistence: persistence,
            singleStimulusDialog: singleDialog
        );
        var points = new Queue<ElectrodeSiteViewModel>(
            page.Points.Where(p => p.CanStimulate).Take(page.RequiredStimulusElectrodeCount)
        );
        foreach (var option in page.StimulusSelectionOptions)
        {
            page.SelectStimulusSelectionOptionCommand.Execute(option);
            for (var i = 0; i < option.RequiredCount; i++)
                page.SelectPointCommand.Execute(points.Dequeue());
        }
        var channels = new HashSet<int>();
        foreach (var row in page.CurrentImpedanceItems)
            row.SelectedStimulationChannel = row.StimulationChannels.First(c =>
                channels.Add(c.PhysicalChannelId)
            );
        page.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Stimulus,
            page.Points.Where(p => p.StimulationChannelRole == StimulationChannelRole.Selectable)
                .ToDictionary(p => p.Name, _ => 8d)
        );
        if (!page.CanOpenConfirmation)
            throw new InvalidOperationException("单刺激检测后不能确认");
        var cancel = page.OpenConfirmationCommand.ExecuteAsync(null);
        var confirmation = await WaitForConfirmation(host);
        if (confirmation.ShowAcquisition)
            throw new InvalidOperationException("单刺激仍展示采集信息");
        confirmation.CancelCommand.Execute(null);
        await cancel;
        if (singleDialog.Count != 0)
            throw new InvalidOperationException("取消仍打开实验");
        var confirm = page.OpenConfirmationCommand.ExecuteAsync(null);
        confirmation = await WaitForConfirmation(host);
        if (page.CanGoBack || page.OpenConfirmationCommand.CanExecute(null))
            throw new InvalidOperationException("确认未锁定");
        confirmation.ConfirmCommand.Execute(null);
        await confirm;
        if (singleDialog.Count != 1 || !page.CanGoBack)
            throw new InvalidOperationException("弹窗次数或返回状态错误");
        if (
            singleDialog.Route!.CreationMode != ExperimentCreationMode.StimulusOnly
            || singleDialog.Route.AcquisitionChannels.Count != 0
        )
            throw new InvalidOperationException("模式传递错误");
        if (
            persistence.Saves != 1
            || persistence.Template?.CreationMode != ExperimentCreationMode.StimulusOnly
        )
            throw new InvalidOperationException("配置未正确保存");
        persistence.Fail = true;
        var failed = page.OpenConfirmationCommand.ExecuteAsync(null);
        (await WaitForConfirmation(host)).ConfirmCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (
            !host.DialogStack.OfType<ConfirmDialogViewModel>().Any(d => d.IsDialogOpen)
            && DateTime.UtcNow < deadline
        )
            await Task.Delay(10);
        var error = host.DialogStack.OfType<ConfirmDialogViewModel>().Single(d => d.IsDialogOpen);
        if (!error.Message.Contains("save failed"))
            throw new InvalidOperationException("未显示保存失败原因");
        error.ConfirmCommand.Execute(null);
        await failed;
        if (singleDialog.Count != 1)
            throw new InvalidOperationException("保存失败仍打开实验");
    }

    private static async Task<ExperimentConfigurationDialogViewModel> WaitForConfirmation(
        DialogHost host
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (
                host
                    .DialogStack.OfType<ExperimentConfigurationDialogViewModel>()
                    .FirstOrDefault(d => d.IsDialogOpen) is
                { } dialog
            )
                return dialog;
            await Task.Delay(10);
        }
        throw new TimeoutException("配置确认弹窗未出现");
    }

    private static async Task Capture(Control view, string path)
    {
        await Task.Delay(150);
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height),
            new Vector(96, 96)
        );
        bitmap.Render(view);
        using var stream = File.Create(path);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private sealed class DialogHost : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private sealed class DialogRecorder : ISingleStimulusExperimentDialogService
    {
        public int Count;
        public ExperimentRunRouteData? Route;

        public Task ShowAsync(ExperimentRunRouteData route)
        {
            Count++;
            Route = route;
            return Task.CompletedTask;
        }
    }

    private sealed class TestPersistence : IExperimentPersistenceService
    {
        public bool Fail;
        public int Saves;
        public ExperimentConfigurationTemplate? Template;

        public Task<string> PreviewExperimentCodeAsync(
            DateTimeOffset scheduledAt,
            CancellationToken cancellationToken = default
        ) => Task.FromResult("single");

        public Task<ExperimentDraft> CreateOrUpdateDraftAsync(
            long? draftId,
            DateTimeOffset scheduledAt,
            string subjectCode,
            string remarks,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task SaveConfigurationAsync(
            long experimentId,
            ExperimentConfigurationTemplate template,
            IReadOnlyList<ImpedanceSnapshotValue> impedances,
            CancellationToken cancellationToken = default
        )
        {
            if (Fail)
                throw new IOException("save failed");
            Saves++;
            Template = template;
            return Task.CompletedTask;
        }

        public Task<ExperimentConfigurationTemplate> LoadTemplateAsync(
            long experimentId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<ExperimentHistoryItem>> ListHistoryAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<ExperimentHistoryItem>>([]);

        public string CreateRandomSubjectCode() => "subject";
    }

    private sealed class VisualRunService : IExperimentRunService
    {
        public event EventHandler<ExperimentRunTelemetryEventArgs>? TelemetryReceived;
        public Exception? StopFailure;

        public void Progress(double seconds) =>
            TelemetryReceived?.Invoke(
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

        public TaskCompletionSource Completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task StartStimulationAsync(
            StimulationRunRequest request,
            CancellationToken cancellationToken = default
        ) => Completion.Task.WaitAsync(cancellationToken);

        public Task StartAcquisitionAsync(
            AcquisitionRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("不应采集");

        public Task StartAutomaticExperimentAsync(
            AutomaticExperimentRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("不应自动运行");

        public Task StopCurrentOperationAsync(
            string deviceId,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task EmergencyStopAsync(
            string deviceId,
            CancellationToken cancellationToken = default
        ) => StopFailure is null ? Task.CompletedTask : Task.FromException(StopFailure);
    }
}
