using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class VisualTestApp : App
{
    private enum CaptureStage
    {
        WaitingForAutomaticSetup,
        SettlingAutomaticSetup,
        WaitingForTimingSetup,
        SettlingTimingSetup,
        WaitingForAcquisition,
        SettlingAcquisition,
        SettlingAmplitudeAxis,
        WaitingForManualStimulus,
        WaitingForStimulation,
        SettlingStimulation,
        SettlingStimulationDetails,
        SettlingStimulationHistory,
        WaitingForFinalAcquisition,
        WaitingForCompletion,
        SettlingCompletion,
        WaitingForResults,
        SettlingResults,
        SettlingResultDetails,
        Finished,
    }

    private ExperimentRunPageViewModel? _pageViewModel;
    private VisualExperimentRunService? _runService;
    private DispatcherTimer? _captureTimer;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _settleStartedAt;
    private CaptureStage _captureStage;
    private int _axisCaptureIndex;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        ResetArtifacts();
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _pageViewModel = ExperimentRunVisualScenario.CreateViewModel(out _runService);
        var content = new BasicView
        {
            DataContext = new BasicViewModel(
                new VisualTestNavigationRouter(),
                _pageViewModel,
                CreateConnectedContext()
            ),
        };
        var window = new Window
        {
            Title = "时序与运行 - 自动截图验收",
            Width = VisualTestOptions.Current.Scenario.Contains("minimum") ? 1200 : 1440,
            Height = VisualTestOptions.Current.Scenario.Contains("minimum") ? 760 : 900,
            MinWidth = 1200,
            MinHeight = 760,
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = content,
        };

        desktop.MainWindow = window;
        window.Closed += (_, _) => _pageViewModel?.Dispose();
        window.Opened += (_, _) => StartScenarioAndCapture(desktop, window, content);
    }

    private static DeviceSelectionContext CreateConnectedContext()
    {
        var context = new DeviceSelectionContext();
        context.SetConnectedDevice("simulator-default", "EGG/tCS Simulator", batteryPercent: 80);
        return context;
    }

    private void StartScenarioAndCapture(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window,
        Control content
    )
    {
        _startedAt = DateTimeOffset.UtcNow;
        _pageViewModel!.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        _captureStage = CaptureStage.WaitingForAutomaticSetup;

        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _captureTimer.Tick += (_, _) =>
        {
            try
            {
                AdvanceScenario(desktop, window, content);
            }
            catch (Exception exception)
            {
                _captureTimer.Stop();
                FailAndShutdown(desktop, exception.ToString());
            }
        };
        _captureTimer.Start();
    }

    private void AdvanceScenario(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window,
        Control content
    )
    {
        if (DateTimeOffset.UtcNow - _startedAt >= VisualTestOptions.Current.Timeout)
            throw new TimeoutException($"视觉验收场景在 {_captureStage} 阶段等待超时。");

        var viewModel = _pageViewModel!;
        switch (_captureStage)
        {
            case CaptureStage.WaitingForAutomaticSetup
                when viewModel.IsTimingSetup
                    && viewModel.IsAutomaticMode
                    && viewModel.IsCycleCountVisible
                    && viewModel.FooterPrimaryText == "开始自动运行"
                    && content.Bounds.Width >= 1200
                    && content.Bounds.Height >= 700:
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingAutomaticSetup;
                break;
            case CaptureStage.SettlingAutomaticSetup
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                ValidateHeaderLayout(content, viewModel);
                Capture(content, VisualTestOptions.Current.AutomaticSetupOutputPath);
                viewModel.SelectModeCommand.Execute(ExperimentRunMode.Manual);
                _captureStage = CaptureStage.WaitingForTimingSetup;
                break;
            case CaptureStage.WaitingForTimingSetup
                when viewModel.IsTimingSetup
                    && viewModel.FooterPrimaryText == "开始采集"
                    && content.Bounds.Width >= 1200
                    && content.Bounds.Height >= 700:
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingTimingSetup;
                break;
            case CaptureStage.SettlingTimingSetup
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                Capture(content, VisualTestOptions.Current.TimingSetupOutputPath);
                _captureStage = CaptureStage.WaitingForAcquisition;
                _ = viewModel.ExecuteFooterPrimaryCommand.ExecuteAsync(null);
                break;
            case CaptureStage.WaitingForAcquisition
                when viewModel.CurrentStage == ExperimentRunStage.Acquisition
                    && viewModel.HasWaveformData
                    && content.Bounds.Width >= 1200
                    && content.Bounds.Height >= 700:
                var activeChannel = viewModel.WaveformChannels[
                    Math.Min(1, viewModel.WaveformChannels.Count - 1)
                ];
                var timeSeconds = 1.37d;
                viewModel.IsFollowingLatest = false;
                viewModel.TimelineViewStart = 1.2d;
                viewModel.TimelineViewEnd = 1.55d;
                viewModel.WaveformHover.Update(activeChannel.ChannelId, timeSeconds);
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingAcquisition;
                break;
            case CaptureStage.SettlingAcquisition
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                Capture(content, VisualTestOptions.Current.AcquisitionOutputPath);
                if (
                    VisualTestOptions.Current.Scenario.Contains(
                        "waveform-axis",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    _axisCaptureIndex = 0;
                    ConfigureAxisCapture(viewModel);
                    _settleStartedAt = DateTimeOffset.UtcNow;
                    _captureStage = CaptureStage.SettlingAmplitudeAxis;
                    break;
                }
                _captureStage = CaptureStage.WaitingForManualStimulus;
                _runService!.ContinueAfterAcquisition();
                break;
            case CaptureStage.SettlingAmplitudeAxis
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                ValidateAmplitudeAxisLayout(content);
                Capture(
                    content,
                    Path.Combine(
                        Path.GetDirectoryName(VisualTestOptions.Current.AcquisitionOutputPath)!,
                        $"ExperimentRunPage.axis-{_axisCaptureIndex}-{viewModel.DisplayRangeMicrovolts}.png"
                    )
                );
                if (++_axisCaptureIndex < viewModel.DisplayRangeOptions.Count + 3)
                {
                    ConfigureAxisCapture(viewModel);
                    _settleStartedAt = DateTimeOffset.UtcNow;
                    break;
                }
                viewModel.IsAllChannelsViewOpen = false;
                viewModel.SelectedLeadLayoutOption = viewModel.LeadLayoutOptions[0];
                viewModel.DisplayRangeMicrovolts = 200m;
                _captureStage = CaptureStage.WaitingForManualStimulus;
                _runService!.ContinueAfterAcquisition();
                break;
            case CaptureStage.WaitingForManualStimulus when viewModel.IsManualStimulusReady:
                _captureStage = CaptureStage.WaitingForStimulation;
                _ = viewModel.ExecuteFooterPrimaryCommand.ExecuteAsync(null);
                break;
            case CaptureStage.WaitingForStimulation when viewModel.IsStimulating:
                viewModel.IsFollowingLatest = false;
                // Include the acquisition/stimulation boundary, as in the Figma running view.
                viewModel.TimelineViewStart = viewModel.TimelineLivePosition - 12d;
                viewModel.TimelineViewEnd = viewModel.TimelineLivePosition + 8d;
                viewModel.WaveformHover.Clear();
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingStimulation;
                break;
            case CaptureStage.SettlingStimulation
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                var stimulationOverlay = content.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.StimulationPeriodOverlay>().First();
                var card = stimulationOverlay.Child!;
                if (!card.IsVisible || Math.Abs(card.Bounds.Width - 177) > 1 || Math.Abs(card.Bounds.Height - 104) > 1)
                    throw new InvalidOperationException("The active stimulation interval must show the 177 × 104 design card.");
                Capture(content, VisualTestOptions.Current.StimulationOutputPath);
                ScrollDetailsToEnd(content, "StimulationParametersScroll");
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingStimulationDetails;
                break;
            case CaptureStage.SettlingStimulationDetails
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                Capture(
                    content,
                    Path.Combine(
                        Path.GetDirectoryName(VisualTestOptions.Current.StimulationOutputPath)!,
                        "ExperimentRunPage.stimulation-details.png"
                    )
                );
                viewModel.TimelineViewStart = 0d;
                viewModel.TimelineViewEnd = 5d;
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingStimulationHistory;
                break;
            case CaptureStage.SettlingStimulationHistory
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                var overlay = content.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.StimulationPeriodOverlay>().First();
                if (overlay.Child?.IsVisible == true)
                    throw new InvalidOperationException("The stimulation card must be hidden when its interval is outside the history viewport.");
                Capture(content, Path.Combine(Path.GetDirectoryName(VisualTestOptions.Current.StimulationOutputPath)!,
                    "ExperimentRunPage.stimulation-history.png"));
                _captureStage = CaptureStage.WaitingForFinalAcquisition;
                _runService!.ContinueAfterStimulation();
                break;
            case CaptureStage.WaitingForFinalAcquisition
                when viewModel.IsManualFinalAcquisitionReady:
                viewModel.IsFollowingLatest = true;
                _captureStage = CaptureStage.WaitingForCompletion;
                _ = viewModel.ExecuteFooterPrimaryCommand.ExecuteAsync(null);
                break;
            case CaptureStage.WaitingForCompletion when viewModel.IsCompleted:
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingCompletion;
                break;
            case CaptureStage.SettlingCompletion
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                Capture(content, VisualTestOptions.Current.CompletedOutputPath);
                _captureStage = CaptureStage.WaitingForResults;
                _ = viewModel.ExecuteFooterPrimaryCommand.ExecuteAsync(null);
                break;
            case CaptureStage.WaitingForResults when viewModel.IsResults:
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingResults;
                break;
            case CaptureStage.SettlingResults
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                Capture(content, VisualTestOptions.Current.ResultsOutputPath);
                ScrollDetailsToEnd(content, "ResultDetailsScroll");
                _settleStartedAt = DateTimeOffset.UtcNow;
                _captureStage = CaptureStage.SettlingResultDetails;
                break;
            case CaptureStage.SettlingResultDetails
                when DateTimeOffset.UtcNow - _settleStartedAt >= TimeSpan.FromMilliseconds(300):
                Capture(
                    content,
                    Path.Combine(
                        Path.GetDirectoryName(VisualTestOptions.Current.ResultsOutputPath)!,
                        "ExperimentRunPage.result-details.png"
                    )
                );
                _captureStage = CaptureStage.Finished;
                _captureTimer!.Stop();
                Environment.ExitCode = 0;
                _pageViewModel?.Dispose();
                window.Close();
                desktop.Shutdown(0);
                break;
        }
    }

    private void ConfigureAxisCapture(ExperimentRunPageViewModel viewModel)
    {
        var rangeCount = viewModel.DisplayRangeOptions.Count;
        viewModel.DisplayRangeMicrovolts =
            _axisCaptureIndex < rangeCount
                ? viewModel.DisplayRangeOptions[_axisCaptureIndex]
                : 2000m;
        if (_axisCaptureIndex >= rangeCount)
            viewModel.SelectedLeadLayoutOption = viewModel.LeadLayoutOptions[
                Math.Min(2, _axisCaptureIndex - rangeCount + 1)
            ];
        viewModel.IsAllChannelsViewOpen = _axisCaptureIndex == rangeCount + 2;
    }

    private void ValidateAmplitudeAxisLayout(Control content)
    {
        var channels = content
            .GetVisualDescendants()
            .OfType<EGGtCSPlatform.Controls.EegWaveformChannel>()
            .Where(channel => channel.IsEffectivelyVisible && channel.Bounds.Width > 0d)
            .ToArray();
        if (channels.Length == 0)
            throw new InvalidOperationException("没有可见波形通道用于刻度验收。");
        foreach (var channel in channels)
        {
            var descendants = channel.GetVisualDescendants().OfType<Control>().ToArray();
            var axis = descendants.Single(control =>
                control.GetType().Name == "WaveformAmplitudeAxis"
            );
            var plot = descendants.Single(control => control.Name == "PART_WaveformSurface");
            var axisOrigin = axis.TranslatePoint(default, channel)!.Value;
            var plotOrigin = plot.TranslatePoint(default, channel)!.Value;
            if (
                Math.Abs(plotOrigin.X - 86d) > 0.01d
                || Math.Abs(axisOrigin.X + axis.Bounds.Width - plotOrigin.X) > 0.01d
                || Math.Abs(axisOrigin.Y - plotOrigin.Y) > 0.01d
                || Math.Abs(axis.Bounds.Height - plot.Bounds.Height) > 0.01d
                || Math.Abs(plotOrigin.Y - 8d) > 0.01d
                || Math.Abs(channel.Bounds.Height - plotOrigin.Y - plot.Bounds.Height - 8d) > 0.01d
            )
                throw new InvalidOperationException("刻度区域未与原有绘图区边界对齐。");

            var expectedUnit = channel.ChannelId == _pageViewModel!.WaveformChannels[0].ChannelId;
            if (
                channel.ShowAmplitudeUnit != expectedUnit
                || !Equals(axis.GetType().GetProperty("ShowUnit")!.GetValue(axis), expectedUnit)
            )
                throw new InvalidOperationException("单位必须仅显示在第一个波形通道。");
        }
    }

    private static void Capture(Control content, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Ceiling(content.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(content.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(content);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static void ScrollDetailsToEnd(Control content, string name)
    {
        var scroll = content
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Single(x => x.Name == name);
        scroll.Offset = new Vector(0, scroll.Extent.Height);
    }

    private static void ValidateHeaderLayout(Control content, ExperimentRunPageViewModel viewModel)
    {
        var headerPanel = content
            .GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(x => x.Name == "HeaderNavigationAndStatusPanel");
        var statusItems = content
            .GetVisualDescendants()
            .OfType<ItemsControl>()
            .Single(x => x.Name == "HeaderStatusItems");
        var headerAction = content
            .GetVisualDescendants()
            .OfType<ContentControl>()
            .Single(x => x.Name == "HeaderAction");

        if (!headerPanel.GetVisualChildren().Contains(statusItems))
            throw new InvalidOperationException(
                "返回按钮和顶部信息状态组未处于同一个 StackPanel。"
            );
        var badgeTexts = viewModel.HeaderBadges.Select(x => x.DisplayText).ToArray();
        if (
            badgeTexts.Length != 3
            || badgeTexts[0] != $"患者ID：{viewModel.RouteData.SubjectId}"
            || badgeTexts[1] != $"实验ID：{viewModel.RouteData.ExperimentId}"
            || badgeTexts[2] != "采集-刺激模式"
        )
            throw new InvalidOperationException("顶部实验信息徽章的顺序或文案不正确。");

        var panelOrigin =
            headerPanel.TranslatePoint(default, content)
            ?? throw new InvalidOperationException("无法定位顶部信息状态组。");
        var actionOrigin =
            headerAction.TranslatePoint(default, content)
            ?? throw new InvalidOperationException("无法定位顶部运行模式选择器。");
        if (panelOrigin.X + headerPanel.Bounds.Width > actionOrigin.X + 0.5d)
            throw new InvalidOperationException("顶部实验信息徽章与运行模式选择器发生重叠。");

        var modeText = statusItems
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(x => x.Text == "采集-刺激模式");
        if (modeText.Bounds.Width + 0.5d < modeText.DesiredSize.Width)
            throw new InvalidOperationException("顶部实验模式徽章被截断。");
    }

    private void FailAndShutdown(IClassicDesktopStyleApplicationLifetime desktop, string message)
    {
        WriteFailure(message);
        Environment.ExitCode = 1;
        _pageViewModel?.Dispose();
        desktop.MainWindow?.Close();
        desktop.Shutdown(Environment.ExitCode);
    }

    private static void WriteFailure(string message)
    {
        var errorPath = VisualTestOptions.Current.ErrorPath;
        var directory = Path.GetDirectoryName(errorPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(errorPath, message);
    }

    private static void ResetArtifacts()
    {
        foreach (
            var path in new[]
            {
                VisualTestOptions.Current.AutomaticSetupOutputPath,
                VisualTestOptions.Current.TimingSetupOutputPath,
                VisualTestOptions.Current.AcquisitionOutputPath,
                VisualTestOptions.Current.StimulationOutputPath,
                VisualTestOptions.Current.CompletedOutputPath,
                VisualTestOptions.Current.ResultsOutputPath,
                VisualTestOptions.Current.ErrorPath,
            }
        )
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
