using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class AcquisitionOnlyVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var router = new VisualTestNavigationRouter();
        var connection = new DeviceSelectionContext();
        connection.SetConnectedDevice("simulator-default", "EGG/tCS Simulator");
        var host = new DialogHost();
        var electrode = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData(
                "EXP-ACQUISITION",
                "SUBJECT-001",
                AcquisitionOnlyConfiguration.StimulusPlaceholder,
                CreationMode: ExperimentCreationMode.AcquisitionOnly
            ),
            router,
            new DialogService(() => null),
            host
        );
        electrode.SelectPointCommand.Execute(electrode.Points.Single(x => x.Name == "C3"));
        electrode.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Acquisition,
            electrode
                .Points.Where(x => x.Role == ElectrodeRole.Acquisition)
                .ToDictionary(x => x.Name, _ => 8d)
        );
        var basic = new BasicViewModel(
            router, electrode, connection, isNavigationAnimationEnabled: false);
        var content = new BasicView { DataContext = basic };
        var window = new Window
        {
            Width = 1440,
            Height = 900,
            Content = content,
            WindowDecorations = WindowDecorations.None,
            Title = "单采集模式视觉验收",
        };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            using var service = new VisualExperimentRunService();
            ExperimentRunPageViewModel? run = null;
            try
            {
                var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
                Directory.CreateDirectory(output);
                File.Delete(VisualTestOptions.Current.ErrorPath);
                await Capture(content, Path.Combine(output, "AcquisitionOnly.electrodes.png"));
                VerifyModeHeader(content, "单采集模式", 0);
                using (
                    var stimulus = new ElectrodeConfigurationPageViewModel(
                        ElectrodeConfigurationRouteDataDefaults.Create() with
                        {
                            CreationMode = ExperimentCreationMode.StimulusOnly,
                        },
                        router,
                        new DialogService(() => null),
                        host
                    )
                )
                {
                    basic.CurrentPage = stimulus;
                    await Capture(content, Path.Combine(output, "StimulusOnly.electrodes.png"));
                    VerifyModeHeader(content, "单刺激模式", 0);
                }
                using (
                    var combined = new ElectrodeConfigurationPageViewModel(
                        ElectrodeConfigurationRouteDataDefaults.Create(),
                        router,
                        new DialogService(() => null),
                        host
                    )
                )
                {
                    basic.CurrentPage = combined;
                    await Capture(content, Path.Combine(output, "Combined.electrodes.png"));
                    VerifyModeHeader(content, null, 2);
                    combined.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
                    if (!combined.IsAcquisitionMode)
                        throw new InvalidOperationException("组合模式无法切换至采集");
                    combined.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);
                    if (!combined.IsStimulusMode)
                        throw new InvalidOperationException("组合模式无法切换至刺激");
                }
                basic.CurrentPage = electrode;
                if (!electrode.CanOpenConfirmation || !electrode.IsAcquisitionMode)
                    throw new InvalidOperationException("采集配置不能独立确认");
                var confirmation = electrode.OpenConfirmationCommand.ExecuteAsync(null);
                for (
                    var attempt = 0;
                    attempt < 100 && !host.DialogStack.Any(x => x.IsDialogOpen);
                    attempt++
                )
                    await Task.Delay(10);
                var dialog = host
                    .DialogStack.OfType<ExperimentConfigurationDialogViewModel>()
                    .Single();
                if (dialog.ShowStimulation || !dialog.ShowAcquisition)
                    throw new InvalidOperationException("确认弹窗显示了错误配置");
                dialog.CancelCommand.Execute(null);
                await confirmation;

                var source = ExperimentRunRouteDataDefaults.Create();
                var route = new ExperimentRunRouteData(
                    "EXP-ACQUISITION",
                    "SUBJECT-001",
                    source.StimulusConfiguration,
                    [],
                    ["C3"],
                    "FCz",
                    "AFz",
                    500,
                    creationMode: ExperimentCreationMode.AcquisitionOnly
                );
                run = new ExperimentRunPageViewModel(
                    route,
                    router,
                    service,
                    new ImmediateVisualClock(),
                    deviceSelection: connection
                );
                run.AcquisitionStage.DurationValue = 30;
                basic.CurrentPage = run;
                await Capture(content, Path.Combine(output, "AcquisitionOnly.manual.png"));
                if (
                    content
                        .GetVisualDescendants()
                        .OfType<ExperimentStageControl>()
                        .Count(x => x.IsEffectivelyVisible) != 1
                )
                    throw new InvalidOperationException("单采集时序没有恰好一个采集阶段");
                if (
                    content
                        .GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Any(x => x.IsEffectivelyVisible && x.Text is "刺激参数配置" or "刺激配置")
                )
                    throw new InvalidOperationException("单采集仍显示刺激配置");
                if (content.GetVisualDescendants().OfType<RunModeSelector>()
                    .Any(x => x.IsEffectivelyVisible))
                    throw new InvalidOperationException("单采集仍显示模式切换按钮");
                if (content.GetVisualDescendants().OfType<TextBlock>()
                    .Any(x => x.IsEffectivelyVisible && x.Text == "手动"))
                    throw new InvalidOperationException("单采集仍显示手动文本");
                run.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
                run.CycleCount = 2;
                if (!run.IsManualMode || run.CycleCount != 1 || run.TimelineSegments.Count != 1)
                    throw new InvalidOperationException("单采集允许自动或循环运行");
                var running = run.StartAcquisitionCommand.ExecuteAsync(null);
                await Capture(content, Path.Combine(output, "AcquisitionOnly.running.png"));
                VerifyNoModeHeader(content);
                service.ContinueAfterAcquisition();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                if (!run.IsCompleted)
                    throw new InvalidOperationException("采集后未立即结束");
                await Capture(content, Path.Combine(output, "AcquisitionOnly.completed.png"));
                VerifyNoModeHeader(content);
                VerifyCompletionButtons(content);
                window.Width = 1024;
                window.Height = 640;
                await Capture(content, Path.Combine(output, "AcquisitionOnly.completed-small.png"));
                VerifyCompletionButtons(content);
                window.Width = 1440;
                window.Height = 900;
                await run.RerunExperimentCommand.ExecuteAsync(null);
                run.ExecuteFooterPrimaryCommand.Execute(null);
                await Capture(content, Path.Combine(output, "AcquisitionOnly.results.png"));
                using var combinedRun = new ExperimentRunPageViewModel(
                    source, router, service, new ImmediateVisualClock(), deviceSelection: connection);
                combinedRun.AcquisitionStage.DurationValue = 30;
                combinedRun.StimulationStage.DurationValue = 30;
                basic.CurrentPage = combinedRun;
                await combinedRun.StartAcquisitionCommand.ExecuteAsync(null);
                service.ContinueAfterStimulation();
                await combinedRun.StartStimulationCommand.ExecuteAsync(null);
                await combinedRun.ExecuteFooterPrimaryCommand.ExecuteAsync(null);
                await Capture(content, Path.Combine(output, "Combined.completed.png"));
                VerifyCompletionButtons(content);
                window.Width = 1024;
                window.Height = 640;
                await Capture(content, Path.Combine(output, "Combined.completed-small.png"));
                VerifyCompletionButtons(content);
                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            {
                File.WriteAllText(VisualTestOptions.Current.ErrorPath, exception.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                run?.Dispose();
                electrode.Dispose();
                desktop.Shutdown(Environment.ExitCode);
            }
        };
        window.Show();
    }

    private static void VerifyNoModeHeader(Control content)
    {
        if (content.GetVisualDescendants().OfType<RunModeSelector>().Any(x => x.IsEffectivelyVisible)
            || content.GetVisualDescendants().OfType<TextBlock>()
                .Any(x => x.IsEffectivelyVisible && x.Text == "手动"))
            throw new InvalidOperationException("单采集仍显示模式区域");
    }

    private static void VerifyCompletionButtons(Control content)
    {
        var labels = content.GetVisualDescendants().OfType<TextBlock>()
            .Where(x => x.IsEffectivelyVisible && x.Text is "结束实验" or "再次运行此次实验")
            .ToArray();
        if (labels.Length != 2)
            throw new InvalidOperationException("完成页未显示两个操作按钮");
        foreach (var label in labels)
        {
            var button = label.GetVisualAncestors().OfType<Button>().First();
            if (!button.IsEnabled || button.Bounds.Height != 32
                || label.DesiredSize.Width > label.Bounds.Width + 1)
                throw new InvalidOperationException("完成页按钮不可用或文字被截断");
        }
    }

    private static async Task Capture(Control view, string path)
    {
        await Task.Delay(350);
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height),
            new Vector(96, 96)
        );
        bitmap.Render(view);
        using var stream = File.Create(path);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static void VerifyModeHeader(Control content, string? title, int expectedButtons)
    {
        var page = content
            .GetVisualDescendants()
            .OfType<EGGtCSPlatform.Views.Pages.ElectrodeConfigurationPageView>()
            .Single();
        var buttons = page.GetVisualDescendants()
            .OfType<RadioButton>()
            .Count(x => x.IsEffectivelyVisible && x.GroupName == "ElectrodeConfigurationMode");
        if (buttons != expectedButtons)
            throw new InvalidOperationException("点位面板模式按钮数量不正确");
        if (title is null)
            return;
        var label = page.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(x => x.IsEffectivelyVisible && x.Text == title);
        if (
            label.HorizontalAlignment != Avalonia.Layout.HorizontalAlignment.Left
            || label.Bounds.X != 0
            || label.Parent is Button
        )
            throw new InvalidOperationException("单模式标题未以普通文字靠左显示");
    }

    private sealed class DialogHost : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }
}
