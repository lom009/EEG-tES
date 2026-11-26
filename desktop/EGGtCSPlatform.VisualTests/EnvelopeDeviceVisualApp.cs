using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Rendering.Composition;
using System.Diagnostics;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

// Windows pixel-level fixture; optional native audio uses simulated stimulation only.
public sealed class EnvelopeDeviceVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var id = Guid.NewGuid();
        var configuration = new ExperimentStimulusConfigurationSnapshot(
            StimulusKind.EnvelopeTAcs,
            StimulusArrayMode.DualChannel,
            StimulusDirection.Bidirectional,
            ShamWaveformMode.Direct,
            0,
            40,
            50,
            [
                new(
                    id,
                    1,
                    2,
                    1,
                    [
                        new(1, StimulationChannelRole.FixedActive, 2),
                        new(2, StimulationChannelRole.Selectable, 2),
                    ]
                ),
            ],
            new EnvelopeParameters(DelayMilliseconds: 300, IsDeviceSetup: true)
        );
        var route = new EnvelopeStimulationRouteData(
            new(
                "EXP-VISUAL",
                "SUBJECT-001",
                configuration,
                [
                    new("Fp1", id, 1, StimulationChannelRole.FixedActive),
                    new("Fp2", id, 2, StimulationChannelRole.Selectable),
                ],
                [],
                "",
                "",
                1000,
                creationMode: ExperimentCreationMode.StimulusOnly
            ),
            "已通过"
        );
        var player = new ControllableSpeechAudioPlayer();
        var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        var model = new EnvelopeStimulationPageViewModel(
            route,
            new Router(),
            () => session,
            _ => new Output(),
            displayOptions: new DisplayOptions { EnvelopePlayheadEnabled = true }
        );
        var view = new EnvelopeStimulationPageView
        {
            DataContext = model,
            Background = Brushes.White,
        };
        var window = new Window
        {
            Width = 1440,
            Height = 980,
            Content = view,
            ShowActivated = false,
            Topmost = true,
        };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var directory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
            Directory.CreateDirectory(directory);
            try
            {
                if (Environment.GetEnvironmentVariable("SPEECH_CACHE_TEST") == "1")
                {
                    await VerifyDisplayCombinations(window, route, directory);
                    return;
                }
                if (Environment.GetEnvironmentVariable("SPEECH_AUDIO_DEVICE_TEST") == "only")
                {
                    await VerifyNativePlayback(window, route, directory);
                    return;
                }
                var playhead = view.GetVisualDescendants()
                    .OfType<EGGtCSPlatform.Controls.SpeechTrainingPlayhead>().Single();

                var reveals = view.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.SpeechWaveformReveal>().ToArray();
                if (reveals.Length != 2 || reveals.Any(r => !r.RealtimeEnabled))
                    throw new InvalidOperationException("实时绘制默认配置或曲线分层未生效。");
                await Task.Delay(100);
                using var voiceProbe = new PlayheadScreenProbe(reveals[0], curves: true);
                using var stimulusProbe = new PlayheadScreenProbe(reveals[1], curves: true);
                await Task.Delay(100);
                if (!voiceProbe.Frames.IsEmpty || !stimulusProbe.Frames.IsEmpty)
                    throw new InvalidOperationException("播放前曲线应完全隐藏。");
                var playing = model.StartCommand.ExecuteAsync(null);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (player.PlayCount == 0)
                    await Task.Delay(20, timeout.Token);
                await Task.Delay(60, timeout.Token);
                using var probe = new PlayheadScreenProbe(playhead);
                var renderedFrames = probe.Frames;
                player.Emit(player.PlaybackId, AudioPlaybackState.Playing, session.Duration);
                var audioEndProgress = session.Duration.TotalSeconds / model.DisplayDurationSeconds;
                while (model.PlaybackProgress <= audioEndProgress)
                    await Task.Delay(16, timeout.Token);
                if (!session.IsPlaying || session.CanReplay)
                    throw new InvalidOperationException("尾段显示不能提前完成音频状态。");
                var tailProgress = model.PlaybackProgress;
                var animationCount = playhead.AnimationSubmissionCount;
                player.Complete();
                if (model.PlaybackProgress < tailProgress)
                    throw new InvalidOperationException("音频完成通知不能重置尾段进度。");
                var audioText = model.PlaybackText;
                await playing;
                // Model a busy UI during completion. The marker must still draw on screen.
                var busyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                Thread.Sleep(150);
                var busyEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                while (model.PlaybackProgress < 1)
                    await Task.Delay(16, timeout.Token);
                // Let the dispatcher deliver the final property change to the control.
                await Task.Delay(50, timeout.Token);
                var actualFrames = renderedFrames.ToArray();
                foreach (var curveProbe in new[] { voiceProbe, stimulusProbe })
                {
                    var curveFrames = curveProbe.Frames.ToArray();
                    if (curveFrames.Length == 0)
                        throw new InvalidOperationException("实时曲线没有显示。");
                    foreach (var frame in curveFrames)
                    {
                        var markerFrame = actualFrames.MinBy(f => Math.Abs(f.Time - frame.Time));
                        if (Math.Abs(Stopwatch.GetElapsedTime(markerFrame.Time, frame.Time).TotalMilliseconds) < 20
                            && frame.Progress > markerFrame.Progress + .02)
                            throw new InvalidOperationException("曲线提前显示在进度线右侧。");
                    }
                }
                if (playhead.AnimationSubmissionCount != animationCount)
                    throw new InvalidOperationException("音频完成重新提交了进度动画。");
                File.WriteAllLines(Path.Combine(directory, "actual-rendered-progress.txt"),
                    actualFrames.Select(f => $"{f.Time},{f.Progress:R}"));
                var busyCurveFrames = stimulusProbe.Frames.Where(f => f.Time > busyStart && f.Time < busyEnd).ToArray();
                if (busyCurveFrames.Length < 2 || busyCurveFrames[^1].Progress <= busyCurveFrames[0].Progress)
                    throw new InvalidOperationException("UI 忙碌时曲线裁剪停止推进。");
                var busyFrames = actualFrames.Where(f => f.Time > busyStart && f.Time < busyEnd).ToArray();
                File.WriteAllText(Path.Combine(directory, "render-summary.txt"),
                    $"UI blocked: 150 ms{Environment.NewLine}Screen samples during block: {busyFrames.Length}{Environment.NewLine}");
                if (busyFrames.Length < 3 || busyFrames[^1].Progress <= busyFrames[0].Progress)
                    throw new InvalidOperationException($"结束阶段 UI 忙碌时竖线停绘：实际绘制 {busyFrames.Length} 帧。");
                if (actualFrames.Length == 0 || Math.Abs(actualFrames[^1].Progress - 1) > .002)
                    throw new InvalidOperationException("合成动画没有实际画到时间轴末端。");
                if (model.PlaybackProgress != 1 || model.PlaybackText != audioText
                    || !ReferenceEquals(playhead.TimelineAnimation, session.TimelineAnimation))
                    throw new InvalidOperationException("进度线必须走到时间轴末端，音频计时保持不变。");
                if (
                    model.DebugFrequencyHz != 500
                    || session.StimulusPlan?.Settings.DebugFrequencyHz != 500
                )
                    throw new InvalidOperationException("训练的调试频率必须固定为 500 Hz。");
                if (
                    view.GetVisualDescendants()
                        .OfType<Control>()
                        .Any(c => c.Name == "DebugFrequencyInput")
                )
                    throw new InvalidOperationException("调试频率控件未移除。");
                Capture(view, directory, "envelope-device-500.png");
                var replay = model.ReplayCommand.ExecuteAsync(null);
                while (player.PlayCount < 2)
                    await Task.Delay(16, timeout.Token);
                await Task.Delay(100, timeout.Token);
                await session.StopAsync();
                await replay;
                await Task.Delay(60, timeout.Token);
                renderedFrames.Clear();
                await Task.Delay(80, timeout.Token);
                var stoppedFrames = renderedFrames.ToArray();
                var stoppedProgress = model.PlaybackProgress;
                if (stoppedFrames.Length == 0 || stoppedFrames.Any(f => Math.Abs(f.Progress - stoppedProgress) > .002))
                    throw new InvalidOperationException("停止后渲染线程仍在推进竖线。");
                // A solid calibration curve makes the clip boundary measurable to two pixels.
                foreach (var reveal in reveals)
                    reveal.Child = new Border { Background = new SolidColorBrush(Color.Parse("#78C5FB")) };
                await Task.Delay(100);
                async Task VerifyBoundary(double expected)
                {
                    foreach (var reveal in reveals)
                    {
                        using var boundary = new PlayheadScreenProbe(reveal, curves: true);
                        await Task.Delay(100);
                        var samples = boundary.Frames.ToArray();
                        var pixelWidth = reveal.PointToScreen(new Point(reveal.Bounds.Width, 0)).X
                            - reveal.PointToScreen(default).X;
                        if (expected == 0 ? samples.Length != 0
                            : samples.Length == 0 || Math.Abs(samples[^1].Progress - expected) * (pixelWidth - 1) > 2)
                            throw new InvalidOperationException("曲线裁剪边界偏差超过两个物理像素。");
                    }
                }
                await VerifyBoundary(stoppedProgress);
                window.Width -= 120;
                await Task.Delay(150);
                await VerifyBoundary(stoppedProgress);
                foreach (var reveal in reveals)
                    reveal.RealtimeEnabled = false;
                await Task.Delay(100);
                await VerifyBoundary(1);
                foreach (var reveal in reveals)
                {
                    reveal.RealtimeEnabled = true;
                    reveal.Progress = 0;
                }
                await Task.Delay(100);
                await VerifyBoundary(0);
                window.Content = null;
                await Task.Delay(60, timeout.Token);
                renderedFrames.Clear();
                await Task.Delay(80, timeout.Token);
                if (!renderedFrames.IsEmpty)
                    throw new InvalidOperationException("离开页面后仍在绘制竖线。");
                await model.PrepareToLeaveAsync();
                model.Dispose();
                await VerifyDisplayCombinations(window, route, directory);
                if (Environment.GetEnvironmentVariable("SPEECH_AUDIO_DEVICE_TEST") == "1")
                {
                    await VerifyNativePlayback(window, route, directory);
                }
            }
            catch (Exception e)
            {
                File.WriteAllText(VisualTestOptions.Current.ErrorPath, e.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                desktop.Shutdown(Environment.ExitCode);
            }
        };
        window.Show();
    }

    private static async Task VerifyDisplayCombinations(Window window,
        EnvelopeStimulationRouteData route, string directory)
    {
        foreach (var (realtime, playheadEnabled) in new[] { (true, false), (true, true), (false, false), (false, true) })
        {
            var player = new ControllableSpeechAudioPlayer();
            var corpus = new CacheTestCorpus();
            using var session = new SpeechTrainingSession(corpus, player);
            // The first combination exercises the actual defaults without supplying options.
            using var model = new EnvelopeStimulationPageViewModel(route, new Router(), () => session,
                _ => new Output(), displayOptions: realtime && !playheadEnabled ? null : new DisplayOptions
                {
                    EnvelopeWaveformRealtimeEnabled = realtime,
                    EnvelopePlayheadEnabled = playheadEnabled,
                });
            var view = new EnvelopeStimulationPageView { DataContext = model, Background = Brushes.White };
            window.Content = view;
            await Task.Delay(150);
            var marker = view.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.SpeechTrainingPlayhead>().Single();
            var reveals = view.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.SpeechWaveformReveal>().ToArray();
            if (marker.IsVisible || model.ShowPlaybackPlayhead || reveals.Any(r => r.RealtimeEnabled != realtime))
                throw new InvalidOperationException("无波形时竖线应隐藏，实时波形开关应独立生效。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var playing = model.StartCommand.ExecuteAsync(null);
            while (session.TimelineAnimation is null)
                await Task.Delay(10, timeout.Token);
            await Task.Delay(100, timeout.Token);
            if (marker.IsVisible != playheadEnabled || model.ShowPlaybackPlayhead != playheadEnabled)
                throw new InvalidOperationException("竖线显示配置未生效。");
            using var voice = new PlayheadScreenProbe(reveals[0], curves: true);
            using var stimulus = new PlayheadScreenProbe(reveals[1], curves: true);
            await Task.Delay(100, timeout.Token);
            var originalCurve = reveals[1].Child;
            reveals[1].Child = new Border { Background = new SolidColorBrush(Color.Parse("#78C5FB")) };
            await Task.Delay(100, timeout.Token);
            var before = stimulus.Frames.LastOrDefault().Progress;
            Thread.Sleep(150);
            var after = stimulus.Frames.LastOrDefault().Progress;
            reveals[1].Child = originalCurve;
            if (realtime && (after <= before || after >= 1))
                throw new InvalidOperationException($"UI 忙碌时波形未独立展开：playhead={playheadEnabled}, before={before}, after={after}。");
            if (!realtime && (voice.Frames.IsEmpty || voice.Frames.Last().Progress < .8))
                throw new InvalidOperationException("关闭实时绘制后未显示完整语音曲线。");
            while (model.PlaybackProgress < 1)
                await Task.Delay(16, timeout.Token);
            player.Complete();
            await playing.WaitAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            if (voice.Frames.IsEmpty || stimulus.Frames.IsEmpty || stimulus.Frames.Last().Progress < .95)
                throw new InvalidOperationException("波形未独立展开到尾段末端。");
            Capture(view, directory, $"display-realtime-{realtime}-playhead-{playheadEnabled}.png");

            // Compare actual compositor output, not RenderTargetBitmap (which bypasses its cache).
            using var voicePixels = new PlayheadScreenProbe(reveals[0], curves: true, capturePixels: true);
            using var stimulusPixels = new PlayheadScreenProbe(reveals[1], curves: true, capturePixels: true);
            await Task.Delay(100);
            for (var sentence = 2; sentence <= 3; sentence++)
            {
                var oldVoice = voicePixels.LastPixels!;
                var oldStimulus = stimulusPixels.LastPixels!;
                var oldAudio = player.Audio;
                var oldSamples = model.VoiceSamples;
                var oldPreview = model.StimulusPreview;
                model.AllCorrectCommand.Execute(null);
                corpus.BlockNextRead();
                corpus.FailNextRead = sentence == 3;
                var next = model.SaveNextCommand.ExecuteAsync(null);
                try
                {
                    await corpus.Entered.Task.WaitAsync(timeout.Token);
                    await Task.Delay(100);
                    voice.Frames.Clear();
                    stimulus.Frames.Clear();
                    await Task.Delay(100);
                    if (model.VoiceSamples.Count != 0 || model.StimulusPreview is not null
                        || !voice.Frames.IsEmpty || !stimulus.Frames.IsEmpty)
                        throw new InvalidOperationException("准备下一句时旧曲线没有清空。");
                }
                finally { corpus.Release.TrySetResult(); }
                if (sentence == 3)
                {
                    await next.WaitAsync(timeout.Token);
                    await Task.Delay(100);
                    voice.Frames.Clear();
                    stimulus.Frames.Clear();
                    await Task.Delay(100);
                    if (session.Error is null || model.VoiceSamples.Count != 0 || model.StimulusPreview is not null
                        || !voice.Frames.IsEmpty || !stimulus.Frames.IsEmpty)
                        throw new InvalidOperationException("准备失败后仍显示旧曲线。");
                    next = model.ReplayCommand.ExecuteAsync(null);
                }
                while (player.PlayCount < sentence)
                    await Task.Delay(10, timeout.Token);
                if (ReferenceEquals(oldAudio, player.Audio) || ReferenceEquals(oldSamples, model.VoiceSamples)
                    || ReferenceEquals(oldPreview, model.StimulusPreview))
                    throw new InvalidOperationException("下一句未发布新音频或波形数据。");
                if (oldAudio!.Duration != player.Audio!.Duration)
                    throw new InvalidOperationException("缓存回归必须使用相同时长的不同音频。");
                while (model.PlaybackProgress < 1)
                    await Task.Delay(16, timeout.Token);
                player.Complete();
                await next.WaitAsync(timeout.Token);
                await Task.Delay(150);
                if (voice.Frames.IsEmpty || stimulus.Frames.IsEmpty)
                    throw new InvalidOperationException("新句数据准备完成后未绘制曲线。");
                if (oldVoice.SequenceEqual(voicePixels.LastPixels!) || oldStimulus.SequenceEqual(stimulusPixels.LastPixels!))
                    throw new InvalidOperationException($"下一句数据已更新，但屏幕仍显示上一句缓存：sentence={sentence}, realtime={realtime}。");
            }

            var beforeZoomVoice = voicePixels.LastPixels!;
            var beforeZoomStimulus = stimulusPixels.LastPixels!;
            model.ZoomVoice(1);
            model.ZoomStimulus(1);
            var zoomDeadline = Stopwatch.StartNew();
            while ((beforeZoomVoice.SequenceEqual(voicePixels.LastPixels!) || beforeZoomStimulus.SequenceEqual(stimulusPixels.LastPixels!))
                && zoomDeadline.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(16, timeout.Token);
            if (beforeZoomVoice.SequenceEqual(voicePixels.LastPixels!) || beforeZoomStimulus.SequenceEqual(stimulusPixels.LastPixels!))
                throw new InvalidOperationException($"纵轴缩放未刷新曲线缓存：voice={beforeZoomVoice.SequenceEqual(voicePixels.LastPixels!)}, stimulus={beforeZoomStimulus.SequenceEqual(stimulusPixels.LastPixels!)}, cache amplitudes={string.Join(',', view.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.SpeechWaveformCache>().Select(c => c.Amplitude))}。");

            var caches = view.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.SpeechWaveformCache>().ToArray();
            var cacheModes = caches.Select(c => c.CacheMode).ToArray();
            var replay = model.ReplayCommand.ExecuteAsync(null);
            if (model.PlaybackProgress != 0)
                throw new InvalidOperationException("重播没有重置显示进度。");
            while (player.PlayCount < 4)
                await Task.Delay(10, timeout.Token);
            await Task.Delay(150, timeout.Token);
            if (caches.Where((c, i) => !ReferenceEquals(c.CacheMode, cacheModes[i])).Any())
                throw new InvalidOperationException("同句重播不应重建静态曲线缓存。");
            await session.StopAsync();
            await replay.WaitAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            var frozen = model.PlaybackProgress;
            // Calibrate the clipping edge independently of gaps in the real waveform.
            foreach (var reveal in reveals)
                reveal.Child = new Border { Background = new SolidColorBrush(Color.Parse("#78C5FB")) };
            await Task.Delay(150, timeout.Token);
            stimulus.Frames.Clear();
            await Task.Delay(150, timeout.Token);
            var samples = stimulus.Frames.ToArray();
            var width = reveals[1].PointToScreen(new Point(reveals[1].Bounds.Width, 0)).X
                - reveals[1].PointToScreen(default).X;
            var expected = realtime ? frozen : 1;
            if (model.PlaybackProgress != frozen || samples.Length == 0
                || samples.Any(f => Math.Abs(f.Progress - expected) * (width - 1) > 2))
                throw new InvalidOperationException("停止后波形未按配置冻结或完整显示。");
            await model.PrepareToLeaveAsync();
            window.Content = null;
        }
        File.WriteAllText(Path.Combine(directory, "display-combinations-passed.txt"),
            "All four combinations passed: three equal-duration sentences with different audio/data/screen pixels, loading/failure clears curves, retry, zoom refreshes caches, replay reuses caches, UI blocking, tail completion and stop.");
    }

    private sealed class CacheTestCorpus : ISpeechCorpusProvider
    {
        private readonly FileSpeechCorpusProvider _inner = new();
        public IReadOnlyList<SpeechCorpus> Corpora => _inner.Corpora;
        private bool _block;
        public bool FailNextRead { get; set; }
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNextRead()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _block = true;
        }
        public AudioBuffer ReadAudio(SpeechSentence sentence)
        {
            if (_block)
            {
                _block = false;
                Entered.TrySetResult();
                if (!Release.Task.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Cache test did not release audio preparation.");
            }
            if (FailNextRead)
            {
                FailNextRead = false;
                throw new InvalidDataException("Simulated audio preparation failure.");
            }
            // Same duration and scale, with the energy peak at a different position each sentence.
            const int sampleRate = 16000;
            var bytes = new byte[sampleRate * 2 * 2];
            var center = .35 + (sentence.Number % 3) * .55;
            for (var i = 0; i < sampleRate * 2; i++)
            {
                var seconds = i / (double)sampleRate;
                var amplitude = .2 + .15 * Math.Cos(4 * Math.PI * seconds)
                    + .5 * Math.Exp(-Math.Pow((seconds - center) / .2, 2));
                var sample = (short)(short.MaxValue * amplitude * Math.Sin(2 * Math.PI * 220 * seconds));
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), sample);
            }
            return new(sampleRate, 1, bytes);
        }
    }

    private static async Task VerifyNativePlayback(Window window,
        EnvelopeStimulationRouteData route, string directory)
    {
        await using var runtime = new EGGtCSPlatform.DeviceRuntime.DeviceRuntimeBuilder().UseSimulation().Build();
        EGGtCSPlatform.DeviceSdk.DeviceCandidate? candidate = null;
        await foreach (var found in runtime.Discovery.DiscoverAsync(TimeSpan.Zero))
            candidate = found;
        var device = await runtime.ConnectVerifiedAsync(candidate!);
        using var nativeSession = new SpeechTrainingSession(new FileSpeechCorpusProvider(),
            new EGGtCSPlatform.Audio.Sdl.SdlAudioPlayer());
        using var nativeModel = new EnvelopeStimulationPageViewModel(route, new Router(),
            () => nativeSession, _ => new DeviceEnvelopeOutput(runtime.Devices, device.Identity.DeviceId.Value),
            displayOptions: new DisplayOptions { EnvelopePlayheadEnabled = true });
        nativeModel.DelayMilliseconds = 247;
        nativeSession.Volume = 0;
        var connection = new DeviceSelectionContext();
        connection.SetConnectedDevice(device.Identity.DeviceId.Value, "EGG/tCS Simulator", batteryPercent: 100);
        var mainModel = new MainViewModel(new PageFactory((_, _) => throw new NotSupportedException()), connection);
        mainModel.CurrentView = new EGGtCSPlatform.ViewModels.BasicViewModel(new Router(), nativeModel, connection, false);
        var shell = new MainView
        {
            DataContext = mainModel,
        };
        var cadenceHost = new Border { Width = 1, Height = 1, IsHitTestVisible = false };
        var content = new Grid();
        content.Children.Add(shell);
        content.Children.Add(cadenceHost);
        window.Content = content;
        window.Width = 1920;
        window.Height = 1080;
        window.WindowState = WindowState.Maximized;
        await Task.Delay(500);
        var cadence = new RenderCadence();
        var cadenceVisual = ElementComposition.GetElementVisual(cadenceHost)!.Compositor.CreateCustomVisual(cadence);
        cadenceVisual.Size = new(1, 1);
        ElementComposition.SetElementChildVisual(cadenceHost, cadenceVisual);
        var marker = shell.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.SpeechTrainingPlayhead>().Single();

        var completionPhase = 0;
        var completionProperties = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        nativeSession.Changed += (_, _) =>
        {
            if (nativeSession.Duration > TimeSpan.Zero && nativeSession.Position == nativeSession.Duration)
                Interlocked.Exchange(ref completionPhase, 1);
        };
        nativeModel.PropertyChanged += (_, e) =>
        {
            if (Volatile.Read(ref completionPhase) != 0)
                completionProperties.Enqueue(e.PropertyName);
        };

        try
        {
            var playing = nativeModel.StartCommand.ExecuteAsync(null);
            using var started = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (nativeSession.TimelineAnimation is null)
                await Task.Delay(16, started.Token);
            await Task.Delay(60, started.Token);
            cadenceVisual.SendHandlerMessage(true);
            using var probe = new PlayheadScreenProbe(marker);
            var samples = probe.Frames;
            await playing.WaitAsync(TimeSpan.FromSeconds(20));
            if (nativeSession.Error is { } error)
                throw new InvalidOperationException(error);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (nativeModel.PlaybackProgress < 1)
                await Task.Delay(16, timeout.Token);
            await Task.Delay(100);
            cadenceVisual.SendHandlerMessage(false);
            var ticks = cadence.Frames.ToArray();
            if (ticks.Length < 2)
                throw new InvalidOperationException("未采集到足够的合成器帧，无法统计渲染帧率。");
            var gaps = ticks.Zip(ticks.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).Order().ToArray();
            File.WriteAllText(Path.Combine(directory, "render-cadence.txt"),
                $"Render loop: {(ticks.Length - 1) / Stopwatch.GetElapsedTime(ticks[0], ticks[^1]).TotalSeconds:F1} fps; median={gaps[gaps.Length / 2]:F2} ms; p95={gaps[(int)(gaps.Length * .95)]:F2} ms");
            var frames = samples.ToArray();
            var unrelatedProperties = new[] { "VoiceSamples", "VoiceEnvelope", "StimulusPreview", "SelectedCorpus", "SelectedMode", "CanUseAdaptiveMode" };
            if (completionProperties.Any(p => unrelatedProperties.Contains(p)))
                throw new InvalidOperationException("播放完成仍然触发了波形或设置的整页刷新。");
            File.WriteAllLines(Path.Combine(directory, "native-rendered-progress.txt"),
                frames.Select(f => $"{f.Time},{f.Progress:R}"));
            if (frames.Length == 0 || Math.Abs(frames[^1].Progress - 1) > .002)
                throw new InvalidOperationException("屏幕上的竖线没有到达末端。");
            // Repeated pixels are not new frames: measure the time between visible
            // movements so a stationary marker followed by a jump cannot pass.
            var tail = frames.Where((f, i) => f.Progress >= .85
                && (i == 0 || f.Progress != frames[i - 1].Progress)).ToArray();
            var largestStep = tail.Zip(tail.Skip(1), (a, b) => b.Progress - a.Progress).DefaultIfEmpty().Max();
            var largestGapMs = tail.Zip(tail.Skip(1), (a, b) => a.Progress < 1
                ? System.Diagnostics.Stopwatch.GetElapsedTime(a.Time, b.Time).TotalMilliseconds
                : 0).DefaultIfEmpty().Max();
            File.WriteAllText(Path.Combine(directory, "native-render-summary.txt"),
                $"Last 15% frames: {tail.Length}; largest step: {largestStep:P3}; largest frame gap: {largestGapMs:F3} ms");
            if (tail.Length < 3 || largestStep > .03 || largestGapMs > 50)
                throw new InvalidOperationException($"实际音频尾段出现跳帧：最大进度跨度 {largestStep:P3}，最大绘制间隔 {largestGapMs:F3} ms。");
        }
        finally
        {
            cadenceVisual.SendHandlerMessage(false);
            ElementComposition.SetElementChildVisual(cadenceHost, null);
            await nativeModel.PrepareToLeaveAsync();
        }
    }

    private sealed class RenderCadence : CompositionCustomVisualHandler
    {
        private bool _running;
        public System.Collections.Concurrent.ConcurrentQueue<long> Frames { get; } = new();
        public override void OnMessage(object message)
        {
            _running = message is true;
            if (_running)
                RegisterForNextAnimationFrameUpdate();
        }
        public override void OnAnimationFrameUpdate()
        {
            if (!_running)
                return;
            Frames.Enqueue(Stopwatch.GetTimestamp());
            RegisterForNextAnimationFrameUpdate();
        }
        public override void OnRender(ImmediateDrawingContext context) { }
    }

    private static void Capture(Control view, string directory, string name)
    {
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height)
        );
        bitmap.Render(view);
        bitmap.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private sealed class Output : ISpeechStimulationOutput
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
        public string? Status => "模拟输出";

        public Task PrepareAsync(EnvelopePlaybackPlan plan, CancellationToken token) =>
            Task.CompletedTask;

        public Task StartAsync(CancellationToken token) => Task.CompletedTask;

        public Task WaitForCompletionAsync(CancellationToken token) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class Router : INavigationRouter
    {
        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData route) { }

        public void Navigate(StimulusConfigurationRouteData route) { }

        public void Navigate(ElectrodeConfigurationRouteData route) { }

        public void Navigate(ExperimentRunRouteData route) { }

        public void Navigate(ExperimentRerunRouteData route) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
