using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class HistoricalResultsVisualApp : App
{
    private DispatcherTimer? _timer;
    private DateTimeOffset _startedAt;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var source = ExperimentRunRouteDataDefaults.Create();
        var stimulus = VisualTestOptions.Current.Scenario.EndsWith(
            "-tacs",
            StringComparison.OrdinalIgnoreCase
        )
            ? source.StimulusConfiguration with
            {
                Kind = StimulusKind.TAcs,
                Frequency = 100,
            }
            : source.StimulusConfiguration;
        var runId = Guid.NewGuid();
        var channelNames = source
            .AcquisitionChannels.Select((name, index) => (name, channel: index + 1))
            .ToDictionary(x => x.channel, x => x.name);
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            stimulus,
            source.StimulusElectrodes,
            source.AcquisitionChannels,
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            historicalResult: new HistoricalExperimentRunContext(
                runId,
                $"C:\\recordings\\{runId:N}.eegraw",
                ExperimentRunStatus.Completed,
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow,
                channelNames,
                [
                    new(ExperimentRunStage.Acquisition, 1, 0, 30),
                    new(ExperimentRunStage.Blanking, 1, 30, 40),
                    new(ExperimentRunStage.Stimulation, 1, 40, 90),
                    new(ExperimentRunStage.Recovery, 1, 90, 100),
                    new(ExperimentRunStage.Acquisition, 2, 100, 120),
                ]
            )
        );
        var viewModel = new ExperimentRunPageViewModel(
            route,
            new VisualTestNavigationRouter(),
            new VisualExperimentRunService(),
            historyWindowProvider: new HistoricalVisualWindowProvider()
        );
        var content = new BasicView
        {
            DataContext = new BasicViewModel(
                new VisualTestNavigationRouter(),
                viewModel,
                new DeviceSelectionContext()
            ),
        };
        var window = new Window
        {
            Title = "历史实验结果 - 自动截图验收",
            Width = 1440,
            Height = 900,
            MinWidth = 1200,
            MinHeight = 760,
            WindowDecorations = WindowDecorations.None,
            Content = content,
        };
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.MainWindow = window;
        window.Closed += (_, _) => viewModel.Dispose();
        window.Opened += (_, _) =>
        {
            _startedAt = DateTimeOffset.UtcNow;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (_, _) =>
            {
                if (DateTimeOffset.UtcNow - _startedAt > VisualTestOptions.Current.Timeout)
                {
                    _timer.Stop();
                    desktop.Shutdown(1);
                    return;
                }
                if (
                    !viewModel.IsResults
                    || (
                        !VisualTestOptions.Current.Scenario.EndsWith("-empty")
                        && !viewModel.HasWaveformData
                    )
                    || viewModel.IsHistoricalWaveformLoading
                    || DateTimeOffset.UtcNow - _startedAt < TimeSpan.FromSeconds(1)
                    || content.Bounds.Width < 1200
                )
                    return;
                _timer.Stop();
                if (viewModel.ShowWaveformStartHint || viewModel.ShowHistoricalWaveformError)
                {
                    desktop.Shutdown(1);
                    return;
                }
                var path = VisualTestOptions.Current.HistoricalResultsOutputPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bitmap = new RenderTargetBitmap(
                    new PixelSize(
                        Math.Max(1, (int)Math.Ceiling(content.Bounds.Width)),
                        Math.Max(1, (int)Math.Ceiling(content.Bounds.Height))
                    )
                );
                bitmap.Render(content);
                using var stream = File.Create(path);
                bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                desktop.Shutdown(0);
            };
            _timer.Start();
        };
    }

    private sealed class HistoricalVisualWindowProvider : IEegHistoryWindowProvider
    {
        public Task<EegHistoryWindowResult> LoadAsync(
            EegHistoryWindowRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var count = Math.Max(2, Math.Min(request.MaximumPointsPerChannel, 600));
            var channels = request
                .ChannelNames.Values.Select(
                    (channel, channelIndex) =>
                        new EegHistoryChannelWindow(
                            channel,
                            Enumerable
                                .Range(0, count)
                                .Select(index =>
                                {
                                    var fraction = index / (double)(count - 1);
                                    var time =
                                        request.StartTimeSeconds
                                        + fraction
                                            * (request.EndTimeSeconds - request.StartTimeSeconds);
                                    return new TimedWaveformPoint(
                                        time,
                                        42d * Math.Sin(time * (4.5d + channelIndex * 0.2d)),
                                        index == 0
                                    );
                                })
                                .ToArray()
                        )
                )
                .ToArray();
            return Task.FromResult(
                new EegHistoryWindowResult(
                    request.RecordingId,
                    request.StartTimeSeconds,
                    request.EndTimeSeconds,
                    request.FilterSettings,
                    request.DataVersion,
                    VisualTestOptions.Current.Scenario.EndsWith("-empty") ? [] : channels
                )
            );
        }

        public void InvalidateRecording(Guid recordingId) { }
    }
}
