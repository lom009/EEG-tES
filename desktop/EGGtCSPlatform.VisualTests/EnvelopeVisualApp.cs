using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.VisualTree;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed partial class EnvelopeVisualApp : App
{
    private static async Task AssertWheelZoom(Control view, EnvelopeStimulationPageViewModel model, string directory, string screenshot)
    {
        var controls = view.GetVisualDescendants().OfType<Control>().ToArray();
        var voice = controls.Single(c => c.Name == "VoiceZoomRegion");
        var stimulus = controls.Single(c => c.Name == "StimulusZoomRegion");
        var scroll = controls.OfType<ScrollViewer>().Single(c => c.Name == "TrainingScroll");
        var axis = controls.OfType<EGGtCSPlatform.Controls.SpeechTrainingAmplitudeAxis>().Single();
        var plots = voice.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.WaveformPlotSurface>().Where(p => p.Name != "VoiceGrid").ToArray();
        var preview = stimulus.GetVisualDescendants().OfType<EGGtCSPlatform.Controls.SpeechStimulusPreviewControl>().Single(c => c.DrawAxes);
        var timeAxis = controls.Single(c => c.Name == "SpeechTimeAxis");
        var timeBounds = timeAxis.Bounds;
        var originalOffset = scroll.Offset;
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        bool Wheel(Control target, double delta, bool onPlot = false)
        {
            var root = TopLevel.GetTopLevel(target)!;
            var position = target.TranslatePoint(new Point(onPlot ? target.Bounds.Width * 0.7 : 65,
                Math.Min(50, target.Bounds.Height / 2)), root)!.Value;
            var hit = root.InputHitTest(position) as InputElement
                ?? throw new InvalidOperationException("滚轮位置未命中控件。");
            var args = new PointerWheelEventArgs(hit, pointer, root, position, 0,
                new PointerPointProperties(), KeyModifiers.None, new Vector(0, delta));
            hit.RaiseEvent(args);
            return args.Handled;
        }
        if (!Wheel(voice, 0.5) || !Wheel(voice, 0.5, onPlot: true)
            || Math.Abs(model.VoiceMaximumAmplitude - 0.8) > 1e-10 || Math.Abs(model.StimulusMaximumAmplitude - 2) > 1e-10)
            throw new InvalidOperationException("语音滚轮缩放方向、小数步长或独立性错误。");
        Wheel(voice, 15);
        Wheel(stimulus, 4, onPlot: true);
        await Task.Delay(50);
        if (plots.Length != 2 || plots.Any(p => p.MaximumAmplitude != model.VoiceMaximumAmplitude || p.MinimumAmplitude != 0.001)
            || axis.MaximumAmplitude != model.VoiceMaximumAmplitude
            || preview.MaximumAmplitude != model.StimulusMaximumAmplitude
            || !ReferenceEquals(preview.Preview, model.StimulusPreview)
            || preview.DurationSeconds != model.DisplayDurationSeconds
            || preview.DelayMilliseconds != model.DelayMilliseconds
            || preview.MaximumCurrent != model.PreviewCurrentScale
            || scroll.Offset != originalOffset || timeAxis.Bounds != timeBounds)
            throw new InvalidOperationException("缩放绑定、时间轴或滚动拦截错误。");
        Capture(view, directory, screenshot);
        Wheel(voice, 10000);
        Wheel(stimulus, 10000);
        if (!Wheel(voice, 1) || !Wheel(stimulus, 1)
            || model.VoiceMaximumAmplitude != 0.001 || model.StimulusMaximumAmplitude != 0.01
            || scroll.Offset != originalOffset)
            throw new InvalidOperationException("缩放下限发生穿透。");
        await Task.Delay(30);
        Capture(view, directory, screenshot + "-minimum");
        Wheel(voice, -10000);
        Wheel(stimulus, -10000);
        if (!Wheel(voice, -1) || !Wheel(stimulus, -1)
            || model.VoiceMaximumAmplitude != 10 || model.StimulusMaximumAmplitude != 20
            || scroll.Offset != originalOffset)
            throw new InvalidOperationException("缩放上限发生穿透。");
        // Restore the default range for the existing pixel/layout assertions.
        Wheel(voice, Math.Log(10) / Math.Log(1.25));
        Wheel(stimulus, Math.Log(10) / Math.Log(1.25));
        // The shared time axis remains outside both zoom regions and scrolls the page normally.
        var priorVoice = model.VoiceMaximumAmplitude;
        var priorStimulus = model.StimulusMaximumAmplitude;
        Wheel(timeAxis, -1);
        await Task.Delay(30);
        if (model.VoiceMaximumAmplitude != priorVoice || model.StimulusMaximumAmplitude != priorStimulus
            || (scroll.Extent.Height > scroll.Viewport.Height + 1 && scroll.Offset.Y <= originalOffset.Y))
            throw new InvalidOperationException("图外滚轮未正常滚动页面。");
        scroll.Offset = originalOffset;
        await Task.Delay(30);
    }

    private static async Task AssertSliderJoins(Window window, Control original, string directory)
    {
        var matrix = new WrapPanel { Background = Brushes.White, Width = 1000 };
        var sliders = new List<Slider>();
        foreach (var compact in new[] { false, true })
        foreach (var reversed in new[] { false, true })
        {
            var column = new StackPanel { Width = 230, Margin = new Thickness(10), Spacing = 8 };
            column.Children.Add(new TextBlock { Text = $"{(compact ? "Compact" : "Normal")} / reversed={reversed}" });
            foreach (var enabled in new[] { true, false })
            foreach (var value in new[] { 0d, 1d, 50d, 99d, 100d })
            {
                var slider = new Slider
                {
                    Width = 210, Minimum = 0, Maximum = 100, Value = value,
                    IsEnabled = enabled, IsDirectionReversed = reversed,
                    Foreground = Brush.Parse("#3941B6"), Background = Brush.Parse("#E2E8F2"),
                    Theme = (Avalonia.Styling.ControlTheme)Current!.FindResource("StimulusSliderTheme")!,
                };
                if (compact) slider.Classes.Add("compact");
                column.Children.Add(slider);
                sliders.Add(slider);
            }
            matrix.Children.Add(column);
        }
        window.Content = matrix;
        await Task.Delay(100);
        foreach (var slider in sliders)
        {
            var nodes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(slider).ToArray();
            var thumb = nodes.OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            var center = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, 0), slider)!.Value.X;
            if (Math.Abs(thumb.Bounds.Width - 25.3333) > 1 || thumb.ZIndex <= 0)
                throw new InvalidOperationException("滑块尺寸或遮盖顺序发生变化。");
            foreach (var name in new[] { "ActiveTrack", "InactiveTrack" })
            {
                var button = nodes.OfType<Button>().Single(b => b.Name == name);
                var fill = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(button)
                    .OfType<Border>().Single(b => b.Name == "TrackFill");
                if (button.Bounds.Width == 0)
                {
                    if (fill.IsVisible)
                        throw new InvalidOperationException("端点出现多余的轨道颜色。");
                    continue;
                }
                var left = fill.TranslatePoint(default, slider)!.Value.X;
                var right = left + fill.Bounds.Width;
                var isLeft = (name == "ActiveTrack") != slider.IsDirectionReversed;
                if (Math.Abs((isLeft ? right : left) - center) > 1
                    || left < -1 || right > slider.Bounds.Width + 1)
                    throw new InvalidOperationException($"轨道未延伸至滑块中心或越界：{name}, value={slider.Value}, reversed={slider.IsDirectionReversed}");
                var expectedHeight = slider.Classes.Contains("compact") ? (name == "ActiveTrack" ? 7 : 6) : 10;
                if (fill.Bounds.Height != expectedHeight)
                    throw new InvalidOperationException("轨道高度发生变化。");
            }
        }
        Capture(matrix, directory, "slider-joins");
        Capture(matrix, directory, "slider-joins-150dpi", 1.5);
        foreach (var scale in new[] { 1d, 1.5 })
        {
            var size = new PixelSize((int)(matrix.Bounds.Width * scale), (int)(matrix.Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            using (context.PushTransform(Matrix.CreateScale(scale, scale)))
                context.DrawRectangle(new VisualBrush(matrix) { Stretch = Stretch.Fill }, null, new Rect(matrix.Bounds.Size));
            var bytes = new byte[size.Width * size.Height * 4];
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), buffer, bytes.Length, size.Width * 4);
                System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, bytes.Length);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            foreach (var slider in sliders)
            {
                var nodes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(slider).ToArray();
                var thumb = nodes.OfType<Avalonia.Controls.Primitives.Thumb>().Single();
                var center = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), matrix)!.Value;
                foreach (var name in new[] { "ActiveTrack", "InactiveTrack" })
                {
                    var button = nodes.OfType<Button>().Single(b => b.Name == name);
                    if (button.Bounds.Width == 0) continue;
                    var isLeft = (name == "ActiveTrack") != slider.IsDirectionReversed;
                    var x = (int)((center.X + (isLeft ? -11.5 : 11.5)) * scale);
                    var y = (int)(center.Y * scale);
                    var offset = (y * size.Width + x) * 4;
                    var expected = Color.Parse(name == "ActiveTrack" ? "#3941B6" : "#E2E8F2");
                    if (Math.Abs(bytes[offset] - expected.B) > 20
                        || Math.Abs(bytes[offset + 1] - expected.G) > 20
                        || Math.Abs(bytes[offset + 2] - expected.R) > 20)
                        throw new InvalidOperationException($"滑块接缝像素不连续：{name}, value={slider.Value}, scale={scale}");
                }
            }
        }
        foreach (var slider in sliders.Where(s => s.IsEnabled && s.Value == 50))
        {
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            if (slider.Value == 50) throw new InvalidOperationException("键盘无法调整滑条。");
            slider.Value = 50;
            await Task.Delay(20);
            using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var click = slider.TranslatePoint(new Point(10, slider.Bounds.Height / 2), matrix)!.Value;
            var hit = matrix.InputHitTest(click) as InputElement;
            if (hit is null) throw new InvalidOperationException("轨道无法命中。");
            var trackButton = hit as Button ?? (hit as Visual)?.GetVisualAncestors().OfType<Button>().FirstOrDefault();
            if (trackButton is null) throw new InvalidOperationException("轨道按钮无法命中。");
            slider.Value = 50;
            await Task.Delay(20);
            var thumb = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), matrix)!.Value;
            thumb.RaiseEvent(new PointerPressedEventArgs(thumb, pointer, matrix, start, 2,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            thumb.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, thumb, pointer, matrix, start + new Vector(20, 0), 3,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            thumb.RaiseEvent(new PointerReleasedEventArgs(thumb, pointer, matrix, start + new Vector(20, 0), 4,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            if (slider.Value == 50) throw new InvalidOperationException("拖动滑块无法调整数值。");
        }
        window.Content = original;
        await Task.Delay(100);
    }

    private static void AssertVolumeLayout(Control view)
    {
        var descendants = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).ToArray();
        var slider = descendants.OfType<Slider>().Single(s => s.Name == "VolumeSlider");
        var badge = descendants.OfType<Border>().Single(b => b.Name == "PlaybackTimeBadge");
        var right = slider.TranslatePoint(new Point(slider.Bounds.Width, 0), view)!.Value.X;
        var left = badge.TranslatePoint(default, view)!.Value.X;
        if (slider.Bounds.Width < 60 || right >= left || !slider.IsEnabled)
            throw new InvalidOperationException("音量滑条未位于音频进度左侧或不可用。");
        var sliderCenter = slider.TranslatePoint(new Point(0, slider.Bounds.Height / 2), view)!.Value.Y;
        var badgeCenter = badge.TranslatePoint(new Point(0, badge.Bounds.Height / 2), view)!.Value.Y;
        if (Math.Abs(sliderCenter - badgeCenter) > 2)
            throw new InvalidOperationException("音量滑条与音频进度未对齐。");
        var capsule = descendants.OfType<Border>().Single(b => b.Name == "VolumeCapsule");
        var capsuleRight = capsule.TranslatePoint(new Point(capsule.Bounds.Width, 0), view)!.Value.X;
        if (Math.Abs(left - capsuleRight - 16) > 1 || badge.Bounds.Width != 146
            || badge.Bounds.Height != 32 || capsule.Bounds.Height != 32
            || capsule.CornerRadius != new CornerRadius(16))
            throw new InvalidOperationException("音量胶囊尺寸或间距不符合设计稿。");
        if (view.Bounds.Width >= 1400 && capsule.Bounds.Width != 335)
            throw new InvalidOperationException("标准窗口音量胶囊宽度应为 335。");
        AssertBrush(capsule.Background, "#F5F7FA");
        var mute = descendants.OfType<Button>().Single(b => b.Name == "MuteVolumeButton");
        if (descendants.OfType<Button>().Any(b => b.Name == "RestoreVolumeButton")
            || mute.TranslatePoint(new Point(20, 0), view)!.Value.X >= slider.TranslatePoint(default, view)!.Value.X)
            throw new InvalidOperationException("音量滑块左侧应仅有一个静音切换按钮。");
        foreach (var (name, height, radius) in new[] { ("ActiveTrack", 7d, 3.5), ("InactiveTrack", 6d, 3d) })
        {
            var track = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(slider)
                .OfType<Button>().Single(b => b.Name == name);
            var fill = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(track)
                .OfType<Border>().Single(b => b.Name == "TrackFill");
            if (fill.Bounds.Height != height || fill.CornerRadius != new CornerRadius(radius))
                throw new InvalidOperationException("音量紧凑轨道样式未生效。");
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var directory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(directory);
        File.Delete(VisualTestOptions.Current.ErrorPath);
        var router = new Router();
        var host = new Host();
        var capability = StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions { FixedActivePhysicalChannelIds = [1] }
        );
        var page = new StimulusConfigurationPageViewModel(
            new("EXP-ENVELOPE", "SUBJECT-001", CreationMode: ExperimentCreationMode.StimulusOnly),
            router,
            new DialogService(() => null),
            host,
            capability,
            applicationOptions: new() { NavigationAnimationsEnabled = false }
        );
        var view = new StimulusConfigurationPageView
        {
            DataContext = page,
            Background = Brush.Parse("#F2F4FA"),
        };
        var window = new Window
        {
            Width = 1440,
            Height = 768,
            WindowDecorations = WindowDecorations.None,
            Content = view,
        };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                await AssertSliderJoins(window, view, directory);
                await Task.Delay(150);
                var parameters = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
                    .OfType<EGGtCSPlatform.Controls.StimulusParameterEditor>().ToArray();
                if (parameters.Any(p => p.Title == "延时")
                    || Grid.GetColumnSpan(parameters.Single(p => p.Title == "最大电流")) != 3)
                    throw new InvalidOperationException("配置页延时未移除或最大电流未扩展整行。");
                Capture(view, directory, "envelope-default");
                page.CurrentConfiguration.Current = 1;
                await Task.Delay(80);
                Capture(view, directory, "envelope-half-current");
                page.CurrentConfiguration.DelayMilliseconds = 300;
                await Task.Delay(80);
                Capture(view, directory, "envelope-delay300");
                page.CurrentConfiguration.DelayMilliseconds = 0;
                await Task.Delay(80);
                Capture(view, directory, "envelope-delay0");
                page.CurrentConfiguration.DelayMilliseconds = 1;
                await Task.Delay(80);
                Capture(view, directory, "envelope-delay1");
                for (var delay = 300; delay >= 0; delay--)
                    page.CurrentConfiguration.DelayMilliseconds = delay;
                await Task.Delay(80);
                Capture(view, directory, "envelope-after-delay-sweep");
                window.Width = 1000;
                window.Height = 650;
                await Task.Delay(80);
                Capture(view, directory, "envelope-small");
                page.CurrentConfiguration.DelayMilliseconds = 40;
                await page.SaveConfigurationCommand.ExecuteAsync(null);
                using var electrodes = new ElectrodeConfigurationPageViewModel(
                    router.Electrodes!,
                    router,
                    new DialogService(() => null),
                    host,
                    capability,
                    impedanceDetectionOptions: new() { AllowConfirmationWhenFailed = true }
                );
                var points = new Queue<ElectrodeSiteViewModel>(
                    electrodes.Points.Where(p => p.CanStimulate).Take(2)
                );
                foreach (var option in electrodes.StimulusSelectionOptions)
                {
                    electrodes.SelectStimulusSelectionOptionCommand.Execute(option);
                    electrodes.SelectPointCommand.Execute(points.Dequeue());
                }
                var used = new HashSet<int>();
                foreach (var row in electrodes.CurrentImpedanceItems)
                    row.SelectedStimulationChannel = row.StimulationChannels.First(c =>
                        used.Add(c.PhysicalChannelId)
                    );
                electrodes.ApplyImpedanceReadings(
                    ElectrodeConfigurationMode.Stimulus,
                    electrodes
                        .Points.Where(p =>
                            p.StimulationChannelRole == StimulationChannelRole.Selectable
                        )
                        .ToDictionary(p => p.Name, _ => 24d)
                );
                if (!electrodes.CanOpenConfirmation)
                    throw new InvalidOperationException("配置未允许未通过的阻抗结果继续。");
                var confirm = electrodes.OpenConfirmationCommand.ExecuteAsync(null);
                for (var i = 0; host.DialogStack.Count == 0 && i < 100; i++)
                    await Task.Delay(10);
                await Task.Delay(40);
                var dialog = host
                    .DialogStack.OfType<ExperimentConfigurationDialogViewModel>()
                    .Single();
                var confirmationView = new ExperimentConfigurationDialogView
                {
                    DataContext = dialog,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                window.Content = confirmationView;
                await Task.Delay(100);
                Capture(confirmationView, directory, "envelope-confirmation");
                confirmationView.DataContext = new ExperimentConfigurationDialogViewModel(
                    dialog.SubjectId,
                    dialog.ExperimentId,
                    "tDCS · 单刺激",
                    "峰值 1 mA",
                    "CH1=FP2、CH2=F3",
                    "",
                    "刺激阻抗检测已完成",
                    showAcquisition: false
                );
                await Task.Delay(80);
                Capture(confirmationView, directory, "ordinary-confirmation");
                confirmationView.DataContext = dialog;
                dialog.CancelCommand.Execute(null);
                await confirm;
                if (router.Envelope is not null || electrodes.IsConfigurationConfirmed)
                    throw new InvalidOperationException("关闭确认窗口不应提交配置。");

                confirm = electrodes.OpenConfirmationCommand.ExecuteAsync(null);
                for (var i = 0; host.DialogStack.Count == 0 && i < 100; i++)
                    await Task.Delay(10);
                dialog = host.DialogStack.OfType<ExperimentConfigurationDialogViewModel>().Single();
                dialog.ConfirmCommand.Execute(null);
                await confirm;
                if (router.Envelope is null)
                    throw new InvalidOperationException("未跳转包络专属页。");
                if (!router.Envelope.DetectionStatus.Contains("未通过"))
                    throw new InvalidOperationException("检测结果展示失真。");
                var player = new ControllableSpeechAudioPlayer();
                using var training = new EnvelopeStimulationPageViewModel(
                    router.Envelope,
                    router,
                    () => new(new FileSpeechCorpusProvider(), player)
                );
                var device = new DeviceSelectionContext();
                device.SetConnectedDevice("demo", "界面演示", batteryPercent: 85);
                var shell = new BasicViewModel(router, training, device, false);
                var trainingView = new BasicView { DataContext = shell };
                window.Content = trainingView;
                window.Width = 1440;
                window.Height = 900;
                await Task.Delay(200);
                var volumeSlider = Avalonia
                    .VisualTree.VisualExtensions.GetVisualDescendants(trainingView)
                    .OfType<Slider>()
                    .Single(s => s.Name == "VolumeSlider");
                volumeSlider.Value = 35;
                if (Math.Abs(player.Volume - 0.35) > 0.001 || training.VolumeText != "35%")
                    throw new InvalidOperationException("音量滑条绑定失败。");
                await Task.Delay(80);
                AssertVolumeLayout(trainingView);
                Capture(trainingView, directory, "speech-start");
                if (VisualTestOptions.Current.Scenario.Equals("envelope-interactions", StringComparison.OrdinalIgnoreCase))
                {
                    await AssertSpeechInteractions(window, trainingView, shell, router, directory);
                    desktop.Shutdown(0);
                    return;
                }
                AssertTrainingStyles(
                    trainingView,
                    fieldsDisabled: false,
                    replayEnabled: false,
                    saveEnabled: false,
                    checkActions: false
                );
                var selectors = Avalonia
                    .VisualTree.VisualExtensions.GetVisualDescendants(trainingView)
                    .OfType<ComboBox>()
                    .Where(c => c.IsEffectivelyVisible)
                    .ToArray();
                if (selectors.Length != 5)
                    throw new InvalidOperationException("训练设置下拉框缺失。");
                var expectedCounts = new[] { 3, 4, 5, 5, 2 };
                for (var selectorIndex = 0; selectorIndex < selectors.Length; selectorIndex++)
                {
                    var selector = selectors[selectorIndex];
                    if (selector.Items.Count != expectedCounts[selectorIndex])
                        throw new InvalidOperationException("下拉选项不符合要求。");
                    selector.IsDropDownOpen = true;
                    await Task.Delay(80);
                    var popup = Avalonia
                        .Controls.Templates.TemplateExtensions.GetTemplateDescendants(selector)
                        .OfType<Avalonia.Controls.Primitives.Popup>()
                        .FirstOrDefault();
                    if (popup?.Child is Control popupContent && popupContent.Bounds.Width > 0)
                        Capture(popupContent, directory, $"speech-dropdown-{selectorIndex}");
                    selector.IsDropDownOpen = false;
                }
                var rateInput = Avalonia
                    .VisualTree.VisualExtensions.GetVisualDescendants(trainingView)
                    .OfType<NumericUpDown>()
                    .Single(c => c.Name == "SpeechRateInput");
                if (!rateInput.IsEffectivelyEnabled || training.SpeechRate != 0)
                    throw new InvalidOperationException("语速应可编辑且默认为 0（原速）。");
                var trainingControls = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(trainingView).ToArray();
                var rateSlider = trainingControls.OfType<Slider>().Single(s => s.Name == "SpeechRateSlider");
                var delaySlider = trainingControls.OfType<Slider>().Single(s => s.Name == "DelaySlider");
                var delayInput = trainingControls.OfType<NumericUpDown>().Single(s => s.Name == "DelayInput");
                if (!rateSlider.IsEffectivelyEnabled || rateSlider.Maximum != 5
                    || !ReferenceEquals(volumeSlider.Theme, rateSlider.Theme)
                    || !ReferenceEquals(volumeSlider.Theme, delaySlider.Theme))
                    throw new InvalidOperationException("训练拖动条未统一复用主题或语速不可编辑。");
                rateSlider.Value = 4;
                if (rateInput.Value != 4 || training.SpeechRate != 4)
                    throw new InvalidOperationException("语速拖动条未同步。");
                rateInput.Value = 2;
                if (rateSlider.Value != 2)
                    throw new InvalidOperationException("语速输入框未同步。");
                var mute = trainingControls.OfType<Button>().Single(b => b.Name == "MuteVolumeButton");
                mute.Command!.Execute(null);
                if (volumeSlider.Value != 0 || player.Volume != 0 || !training.IsMuted
                    || AutomationProperties.GetName(mute) != "恢复音量")
                    throw new InvalidOperationException("静音按钮未生效。");
                mute.Command!.Execute(null);
                if (volumeSlider.Value != 35 || Math.Abs(player.Volume - .35) > .001 || training.IsMuted
                    || AutomationProperties.GetName(mute) != "静音")
                    throw new InvalidOperationException("恢复音量按钮未生效。");
                delaySlider.Value = 120;
                if (delayInput.Value != 120 || training.Waveform.DelayMilliseconds != 120)
                    throw new InvalidOperationException("延时拖动条未同步到输入框或波形。");
                delayInput.Value = 75;
                if (delaySlider.Value != 75 || training.DelayMilliseconds != 75)
                    throw new InvalidOperationException("延时输入框未同步到拖动条。");
                training.DelayMilliseconds = 40;
                training.SelectedCorpus = "MHINT-P";
                training.SentenceList = "02";
                await Task.Delay(80);
                if (training.Words.Count != 10 || training.SentenceListOptions.Count != 2)
                    throw new InvalidOperationException("MHINT 句表或文字不正确。");
                Capture(trainingView, directory, "speech-mhint-practice");
                training.ListSize = 30;
                if (training.SentenceList != "01")
                    throw new InvalidOperationException("切换句表大小后未重置句表。");
                training.SentenceList = "02";
                await Task.Delay(80);
                Capture(trainingView, directory, "speech-practice-partial");
                training.SelectedCorpus = "MSP";
                if (training.ListSize != 30 || training.SentenceListOptions.Count != 4)
                    throw new InvalidOperationException("切换语料库未保留句表大小。");
                training.SentenceList = "04";
                await Task.Delay(80);
                if (!training.SentencePosition.Contains("10"))
                    throw new InvalidOperationException("末表未显示实际句数。");
                Capture(trainingView, directory, "speech-msp-partial");
                training.ListSize = 20;
                training.SentenceList = "05";
                await Task.Delay(50);
                Capture(trainingView, directory, "speech-options");
                training.SelectedCarrier = "声码器仿真";
                training.VocoderAlgorithm = "ACE";
                training.VocoderBands = 6;
                if (training.VocoderMaxima != 6 || !training.ShowVocoderMaxima)
                    throw new InvalidOperationException("声码器最大激活通道数未随频带数收敛。");
                await Task.Delay(120);
                Capture(trainingView, directory, "speech-vocoder-options");
                window.Width = 1000;
                window.Height = 650;
                await Task.Delay(100);
                Capture(trainingView, directory, "speech-vocoder-small");
                var vocoderScroll = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(trainingView)
                    .OfType<ScrollViewer>().Single(c => c.Name == "TrainingSettingsScroll");
                vocoderScroll.Offset = new Vector(0, vocoderScroll.Extent.Height);
                await Task.Delay(100);
                Capture(trainingView, directory, "speech-vocoder-small-actions");
                vocoderScroll.Offset = default;
                window.Width = 1440;
                window.Height = 900;
                training.SelectedCarrier = "原始语音";
                training.SentenceList = "01";
                await training.StartCommand.ExecuteAsync(null);
                training.SpeechRate = 3;
                if (rateSlider.IsEffectivelyEnabled || rateInput.IsEffectivelyEnabled || training.SpeechRate != 2
                    || Math.Abs(training.AudioDurationSeconds - 3.5) > 0.001)
                    throw new InvalidOperationException("训练中语速应锁定，变速后的时长应为 3.5 秒。");
                player.Advance(TimeSpan.FromSeconds(1.15));
                await Task.Delay(100);
                if (training.PlaybackText.Contains("00:00/"))
                    throw new InvalidOperationException("播放器进度未更新。");
                Capture(trainingView, directory, "speech-playing");
                await AssertWheelZoom(trainingView, training, directory, "speech-zoom");
                AssertTrainingStyles(
                    trainingView,
                    fieldsDisabled: true,
                    replayEnabled: false,
                    saveEnabled: false
                );
                var hoverControls = Avalonia
                    .VisualTree.VisualExtensions.GetVisualDescendants(trainingView)
                    .OfType<Control>()
                    .Where(c =>
                        c is ComboBox or NumericUpDown or TextBox
                        || c.Name is "ReplayButton" or "SaveNextButton"
                    )
                    .ToArray();
                foreach (var control in hoverControls)
                    ((IPseudoClasses)control.Classes).Set(":pointerover", true);
                await Task.Delay(180);
                AssertTrainingStyles(
                    trainingView,
                    fieldsDisabled: true,
                    replayEnabled: false,
                    saveEnabled: false
                );
                Capture(trainingView, directory, "speech-disabled-hover");
                foreach (var control in hoverControls)
                    ((IPseudoClasses)control.Classes).Set(":pointerover", false);
                training.Words[0].CorrectCommand.Execute(null);
                training.Words[1].WrongCommand.Execute(null);
                await Task.Delay(180);
                Capture(trainingView, directory, "speech-playing-partial-score");
                if (!training.IsPlaying || training.RatedText != "2/7")
                    throw new InvalidOperationException("播放中逐字评分未生效。");
                training.AllCorrectCommand.Execute(null);
                await Task.Delay(180);
                Capture(trainingView, directory, "speech-playing-complete-score");
                AssertTrainingStyles(
                    trainingView,
                    fieldsDisabled: true,
                    replayEnabled: false,
                    saveEnabled: true
                );
                training.ResetCommand.Execute(null);
                player.Complete();
                await Task.Delay(180);
                Capture(trainingView, directory, "speech-unrated");
                if (rateInput.IsEffectivelyEnabled || rateSlider.IsEffectivelyEnabled)
                    throw new InvalidOperationException("播放结束后训练语速应继续锁定。");
                var completedAudio = player.Audio;
                training.SpeechRate = 3;
                await Task.Delay(60);
                if (training.SpeechRate != 2 || !training.SpeechRateHint.Contains("训练中不可修改")
                    || !ReferenceEquals(completedAudio, player.Audio) || training.AudioDurationSeconds != 3.5)
                    throw new InvalidOperationException("播放结束后的语速锁定或音频保持失败。");
                Capture(trainingView, directory, "speech-rate-locked");
                AssertTrainingStyles(
                    trainingView,
                    fieldsDisabled: true,
                    replayEnabled: true,
                    saveEnabled: false
                );
                training.AllCorrectCommand.Execute(null);
                training.Words[1].WrongCommand.Execute(null);
                await Task.Delay(180);
                Capture(trainingView, directory, "speech-scoring");
                Capture(trainingView, directory, "speech-scoring-150dpi", 1.5);
                AssertTrainingStyles(
                    trainingView,
                    fieldsDisabled: true,
                    replayEnabled: true,
                    saveEnabled: true
                );
                var normalButtonSize = AssertScoreButtons(trainingView);
                window.Width = 1920;
                window.Height = 1080;
                await Task.Delay(180);
                Capture(trainingView, directory, "speech-fullhd");
                if (AssertScoreButtons(trainingView) <= normalButtonSize)
                    throw new InvalidOperationException("全屏评分按钮未随字卡放大。");
                window.Width = 1440;
                window.Height = 900;
                await Task.Delay(100);
                // Exercise the effective 960x600 viewport at 150% layout scale, without changing OS settings.
                window.Content = null;
                var scaledHost = new LayoutTransformControl
                {
                    Child = trainingView,
                    LayoutTransform = new ScaleTransform(1.5, 1.5),
                };
                window.Content = scaledHost;
                await Task.Delay(180);
                Capture(scaledHost, directory, "speech-layout-scale150");
                AssertTrainingStyles(
                    trainingView,
                    fieldsDisabled: true,
                    replayEnabled: true,
                    saveEnabled: true
                );
                var scaledScroll = Avalonia
                    .VisualTree.VisualExtensions.GetVisualDescendants(trainingView)
                    .OfType<ScrollViewer>()
                    .Single(c => c.Name == "TrainingScroll");
                scaledScroll.Offset = new Vector(0, scaledScroll.Extent.Height);
                await Task.Delay(80);
                Capture(scaledHost, directory, "speech-layout-scale150-actions");
                scaledScroll.Offset = default;
                // LayoutTransformControl assigns a transform to its child; clear it before reusing the view.
                scaledHost.LayoutTransform = new ScaleTransform(1, 1);
                await Task.Delay(80);
                window.Content = null;
                scaledHost.Child = null;
                trainingView.RenderTransform = null;
                window.Content = trainingView;
                window.Width = 1000;
                window.Height = 650;
                await Task.Delay(100);
                Capture(trainingView, directory, "speech-small");
                await AssertWheelZoom(trainingView, training, directory, "speech-small-zoom");
                AssertScoreButtons(trainingView);
                var scroll = Avalonia
                    .VisualTree.VisualExtensions.GetVisualDescendants(trainingView)
                    .OfType<ScrollViewer>()
                    .Single(c => c.Name == "TrainingScroll");
                if (scroll.Extent.Width > scroll.Viewport.Width + 1)
                    throw new InvalidOperationException("训练页存在横向溢出。");
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                await Task.Delay(80);
                AssertVolumeLayout(trainingView);
                Capture(trainingView, directory, "speech-small-actions");
                var endButton = Avalonia
                    .VisualTree.VisualExtensions.GetVisualDescendants(trainingView)
                    .OfType<Button>()
                    .Single(b => b.Name == "EndTrainingButton");
                var buttonBottom = endButton.TranslatePoint(
                    new Point(0, endButton.Bounds.Height),
                    trainingView
                );
                if (buttonBottom is null || buttonBottom.Value.Y > trainingView.Bounds.Height)
                    throw new InvalidOperationException("小窗口无法访问结束按钮。");
                scroll.Offset = default;
                window.Width = 1440;
                window.Height = 900;
                training.VocoderAlgorithm = "经典 CIS";
                training.SelectedCarrier = "声码器仿真";
                if (training.CanAdjustCarrier || training.SelectedCarrier != "原始语音")
                    throw new InvalidOperationException("播放结束后的载体未保持锁定。");
                await Task.Delay(100);
                Capture(trainingView, directory, "speech-carrier-locked");
                await training.SaveNextCommand.ExecuteAsync(null);
                if (training.CanAdjustCarrier || training.SelectedCarrier != "原始语音" || training.SpeechRate != 2)
                    throw new InvalidOperationException("下一句未沿用已锁定的载体及语速。");
                await Task.Delay(100);
                Capture(trainingView, directory, "speech-next-playing");
                training.AllWrongCommand.Execute(null);
                await training.SaveNextCommand.ExecuteAsync(null);
                await training.SaveNextCommand.ExecuteAsync(null);
                player.Advance(TimeSpan.FromSeconds(1.15));
                await Task.Delay(100);
                if (
                    !training.IsPlaying
                    || !training.PlaybackText.Contains("00:01/")
                    || training.RatedText != "0/7"
                )
                    throw new InvalidOperationException("提前保存未正确重启下一句计时或清空评分。");
                Capture(trainingView, directory, "speech-early-save-next");
                training.AllCorrectCommand.Execute(null);
                await training.SaveNextCommand.ExecuteAsync(null);
                if (!await training.FinishAsync(true))
                    throw new InvalidOperationException(training.StatusText);
                var result = new SpeechTrainingResultsPageViewModel(router.SpeechResults!);
                if (result.RatedCount != 21 || result.CorrectCount != 13)
                    throw new InvalidOperationException("真实语料汇总不正确。");
                if (result.SpeechRateText != "2 字/s" || result.Results.Any(r => r.SpeechRate != 2))
                    throw new InvalidOperationException("结果未记录训练期间锁定的语速。");
                if (result.CarrierText != "原始语音" || result.Results.Any(r => r.CarrierText != "原始语音"))
                    throw new InvalidOperationException("结果未记录训练期间锁定的载体。");
                shell.CurrentPage = result;
                await Task.Delay(150);
                Capture(trainingView, directory, "speech-results");
                window.Width = 1000;
                window.Height = 650;
                await Task.Delay(100);
                Capture(trainingView, directory, "speech-results-small");
                shell.CurrentPage = new SpeechTrainingResultsPageViewModel(
                    router.SpeechResults! with
                    {
                        Results = [],
                    }
                );
                await Task.Delay(100);
                Capture(trainingView, directory, "speech-results-empty");
                await AssertAdaptiveTraining(window, trainingView, shell, router, directory);
                await AssertSnrTraining(window, trainingView, shell, router, directory);
                await AssertFixedSnrTraining(window, trainingView, shell, router, directory);
                await AssertRirTraining(window, trainingView, shell, router, directory);
                await AssertCorpusScoringConfiguration(window, trainingView, shell, router, directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "envelope.passed.txt"),
                    "Full envelope visual regression passed: axis pixels, zoom, locked settings, playback/scoring, result snapshots, adaptive rate/SNR, fixed SNR, RIR and corpus configuration.");
                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(
                    VisualTestOptions.Current.ErrorPath,
                    exception.ToString()
                );
                Environment.ExitCode = 1;
            }
            finally
            {
                desktop.Shutdown(Environment.ExitCode);
            }
        };
        window.Show();
    }

    private static async Task AssertSnrTraining(
        Window window,
        BasicView view,
        BasicViewModel shell,
        Router router,
        string directory
    )
    {
        var player = new ControllableSpeechAudioPlayer();
        using var model = new EnvelopeStimulationPageViewModel(
            router.Envelope!,
            router,
            () => new(new FileSpeechCorpusProvider(), player)
        );
        shell.CurrentPage = model;
        window.Width = 1440;
        window.Height = 900;
        model.SelectedMode = model.ModeOptions[2];
        model.InitialSnrDb = 12;
        model.SelectedNoise = model.NoiseOptions[1];
        model.ListSize = 10;
        await Task.Delay(1000);
        var initial = view.GetVisualDescendants()
            .OfType<Control>()
            .Single(c => c.Name == "InitialSnrInput");
        var noise = view.GetVisualDescendants()
            .OfType<ComboBox>()
            .Single(c => c.Name == "NoiseSelector");
        var slider = view.GetVisualDescendants()
            .OfType<Slider>()
            .Single(c => c.Name == "SpeechRateSlider");
        if (
            !initial.IsEffectivelyVisible
            || !noise.IsEffectivelyVisible
            || slider.IsEffectivelyVisible
            || !model.StartCommand.CanExecute(null)
        )
            throw new InvalidOperationException("SNR 配置控件显示错误。");
        Capture(view, directory, "speech-snr-start");
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(400);
        Capture(view, directory, "speech-snr-small");
        var scroll = view.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Single(c => c.Name == "TrainingSettingsScroll");
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await Task.Delay(200);
        if (noise.Bounds.Width <= 0 || scroll.Extent.Width > scroll.Viewport.Width + 1)
            throw new InvalidOperationException("SNR 配置在小窗口横向溢出。");
        Capture(view, directory, "speech-snr-small-settings");
        window.Width = 1440;
        window.Height = 900;
        scroll.Offset = default;
        await model.StartCommand.ExecuteAsync(null);
        await Task.Delay(300);
        if (
            initial.IsEffectivelyEnabled
            || noise.IsEffectivelyEnabled
            || !model.SnrText.Contains("12 dB")
        )
            throw new InvalidOperationException("SNR 开始后配置未锁定。");
        Capture(view, directory, "speech-snr-playing");
        for (var i = 0; i < 10; i++)
        {
            if (i % 2 == 0)
                model.AllCorrectCommand.Execute(null);
            else
                model.AllWrongCommand.Execute(null);
            await model.SaveNextCommand.ExecuteAsync(null);
        }
        await Task.Delay(400);
        var results = new SpeechTrainingResultsPageViewModel(RoundDetail(model));
        if (!results.IsSnr || !results.HasAdaptiveValue || !results.AdaptiveValue.EndsWith(" dB"))
            throw new InvalidOperationException("SNR 结果缺少阈值。");
        shell.CurrentPage = results;
        await Task.Delay(300);
        Capture(view, directory, "speech-snr-results");
        view.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .First(c => c.Name == "DetailsToggle")
            .IsChecked = true;
        await Task.Delay(200);
        Capture(view, directory, "speech-snr-details");
        File.WriteAllText(
            Path.Combine(directory, "speech-snr.passed.txt"),
            "SNR controls, lock, small layout, playback and results passed."
        );
    }

    private static async Task AssertAdaptiveTraining(
        Window window,
        BasicView view,
        BasicViewModel shell,
        Router router,
        string directory
    )
    {
        var player = new ControllableSpeechAudioPlayer();
        using var model = new EnvelopeStimulationPageViewModel(
            router.Envelope!,
            router,
            () => new(new FileSpeechCorpusProvider(), player)
        );
        shell.CurrentPage = model;
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(400);
        var selector = view.GetVisualDescendants()
            .OfType<ComboBox>()
            .Single(c => c.Name == "ModeSelector");
        selector.SelectedIndex = 1;
        model.ListSize = 10;
        await Task.Delay(400);
        var slider = view.GetVisualDescendants()
            .OfType<Slider>()
            .Single(c => c.Name == "SpeechRateSlider");
        var autoInfo = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(c => c.Name == "AdaptiveRateInfo");
        if (
            !model.IsAdaptiveMode
            || slider.IsEffectivelyVisible
            || !autoInfo.IsEffectivelyVisible
            || !selector.IsEffectivelyEnabled
            || !model.AdaptiveRateText.Contains("1.5 倍")
        )
            throw new InvalidOperationException("自适应模式选择或自动语速显示错误。");
        selector.IsDropDownOpen = true;
        await Task.Delay(50);
        for (var i = 0; i < selector.Items.Count; i++)
            if (selector.ContainerFromIndex(i) is { } item && item.IsEnabled != (i < 5))
                throw new InvalidOperationException("未实现的模式必须禁用。");
        selector.IsDropDownOpen = false;
        Capture(view, directory, "speech-adaptive-start");
        await model.StartCommand.ExecuteAsync(null);
        await Task.Delay(400);
        if (
            selector.IsEffectivelyEnabled
            || model.CanAdjustSpeechRate
            || model.AdaptiveRateText.Contains("待准备")
        )
            throw new InvalidOperationException("自适应播放状态错误。");
        Capture(view, directory, "speech-adaptive-playing");
        model.AllCorrectCommand.Execute(null);
        await model.SaveNextCommand.ExecuteAsync(null);
        await model.SaveNextCommand.ExecuteAsync(null);
        if (!model.AdaptiveRateText.Contains("1.75 倍"))
            throw new InvalidOperationException("提前保存未按 R 更新一次。");
        player.Complete();
        await Task.Delay(400);
        if (model.CanAdjustCarrier || model.CanAdjustSpeechRate)
            throw new InvalidOperationException("完成后的自适应配置权限错误。");
        Capture(view, directory, "speech-adaptive-completed");
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(400);
        Capture(view, directory, "speech-adaptive-small");
        var scroll = view.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Single(c => c.Name == "TrainingSettingsScroll");
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await Task.Delay(400);
        Capture(view, directory, "speech-adaptive-small-actions");
        scroll.Offset = default;
        window.Width = 1440;
        window.Height = 900;
        for (var i = 1; i < 10; i++)
        {
            player.Complete();
            if (i % 2 == 0)
                model.AllCorrectCommand.Execute(null);
            else
                model.AllWrongCommand.Execute(null);
            await model.SaveNextCommand.ExecuteAsync(null);
        }
        var completed = RoundDetail(model);
        var results = new SpeechTrainingResultsPageViewModel(completed);
        if (
            !results.HasAdaptiveValue
            || results.IsMixedCarrier
            || results.Results.Count != 10
            || results.Results[0].PlaybackCompleted != false
        )
            throw new InvalidOperationException("自适应完整结果错误。");
        shell.CurrentPage = results;
        await Task.Delay(400);
        Capture(view, directory, "speech-adaptive-results");
        view.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .First(c => c.Name == "DetailsToggle")
            .IsChecked = true;
        await Task.Delay(400);
        Capture(view, directory, "speech-adaptive-details");
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(400);
        Capture(view, directory, "speech-adaptive-results-small");

        var coreRows = completed
            .Results.Select(r => new SpeechTraining.SpeechSentenceResult(
                $"MSP/{r.Number}",
                r.Number,
                r.Text,
                r.Correct,
                r.Total,
                Adaptive: r.Adaptive
            ))
            .ToArray();
        foreach (var count in new[] { 3, 7 })
        {
            var summary = AdaptiveSpeechSummary.Create(
                count == 3 ? 10 : 7,
                coreRows.Take(count).ToArray(),
                completed.Playbacks
            );
            shell.CurrentPage = new SpeechTrainingResultsPageViewModel(
                completed with
                {
                    SentenceCount = count == 3 ? 10 : 7,
                    Results = completed.Results.Take(count).ToArray(),
                    AdaptiveSummary = summary,
                }
            );
            await Task.Delay(400);
            Capture(
                view,
                directory,
                count == 3 ? "speech-adaptive-incomplete" : "speech-adaptive-insufficient"
            );
        }

        // Attempts to change the carrier after starting must not affect replays or later sentences.
        var mixedPlayer = new ControllableSpeechAudioPlayer();
        using var mixed = new EnvelopeStimulationPageViewModel(
            router.Envelope!,
            router,
            () => new(new FileSpeechCorpusProvider(), mixedPlayer)
        );
        shell.CurrentPage = mixed;
        mixed.SelectedMode = mixed.ModeOptions[1];
        mixed.ListSize = 10;
        await mixed.StartCommand.ExecuteAsync(null);
        mixedPlayer.Complete();
        mixed.VocoderBands = 1;
        mixed.SelectedCarrier = "声码器仿真";
        await mixed.ReplayCommand.ExecuteAsync(null);
        mixedPlayer.Complete();
        mixed.SelectedCarrier = "原始语音";
        for (var i = 0; i < 10; i++)
        {
            if (i % 2 == 0)
                mixed.AllCorrectCommand.Execute(null);
            else
                mixed.AllWrongCommand.Execute(null);
            await mixed.SaveNextCommand.ExecuteAsync(null);
        }
        var mixedResults = new SpeechTrainingResultsPageViewModel(RoundDetail(mixed));
        if (
            mixedResults.IsMixedCarrier
            || !mixedResults.HasAdaptiveValue
            || mixedResults.Results.Any(r => r.CarrierText != "原始语音")
        )
            throw new InvalidOperationException("重播或后续句子未保持载体锁定。");
        shell.CurrentPage = mixedResults;
        await Task.Delay(400);
        Capture(view, directory, "speech-adaptive-carrier-locked");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "speech-adaptive.passed.txt"),
            "Mode selection, early save, carrier lock through replays, complete/incomplete/insufficient summaries, details and narrow layouts passed."
        );
    }

    private static async Task AssertCorpusScoringConfiguration(
        Window window, BasicView view, BasicViewModel shell, Router router, string directory)
    {
        var root = Path.Combine(directory, "CorpusConfigurationResources");
        void Corpus(string name, string? json)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "sentences.txt"), "一二三四五六七\n");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "SpeechCorpora", "MSP", "1.wav"), Path.Combine(folder, "1.wav"), true);
            if (json is not null) File.WriteAllText(Path.Combine(folder, "corpus.json"), json);
        }
        Corpus("Configured", """{"adaptiveScoring":{"expectedCharacterCount":7,"minimumCorrect":4}}""");
        Corpus("Missing", null);
        Corpus("Malformed", "{");
        Corpus("WrongLength", """{"adaptiveScoring":{"expectedCharacterCount":10,"minimumCorrect":8}}""");
        using var model = new EnvelopeStimulationPageViewModel(router.Envelope!, router,
            () => new(new FileSpeechCorpusProvider(root), new ControllableSpeechAudioPlayer()));
        shell.CurrentPage = model;
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(400);
        var selector = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ModeSelector");
        model.SelectedMode = model.ModeOptions[1];
        var settings = view.GetVisualDescendants().OfType<ScrollViewer>().Single(c => c.Name == "TrainingSettingsScroll");
        foreach (var name in new[] { "Configured", "Missing", "Malformed", "WrongLength", "Configured" })
        {
            model.SelectedCorpus = name;
            await Task.Delay(100);
            selector.IsDropDownOpen = true;
            await Task.Delay(100);
            var adaptiveItem = selector.ContainerFromIndex(1)
                ?? throw new InvalidOperationException("Missing adaptive mode option.");
            var valid = name == "Configured";
            if (adaptiveItem.IsEnabled != valid || model.StartCommand.CanExecute(null) != valid
                || !model.IsAdaptiveMode || selector.SelectedItem?.ToString() != model.ModeOptions[1])
                throw new InvalidOperationException("语料配置可用性未同步到模式选择及开始按钮。");
            selector.IsDropDownOpen = false;
            settings.Offset = new Vector(0, settings.Extent.Height);
            await Task.Delay(400);
            Capture(view, directory, "speech-corpus-config-" + name.ToLowerInvariant());
        }
        model.SelectedCorpus = "Missing";
        model.SelectedMode = model.ModeOptions[0];
        if (!model.StartCommand.CanExecute(null))
            throw new InvalidOperationException("缺少评分配置不应禁用固定模式。");
        await model.StartCommand.ExecuteAsync(null);
        if (!model.IsPlaying) throw new InvalidOperationException("无配置语料的固定模式无法播放。");
        await Task.Delay(400);
        Capture(view, directory, "speech-corpus-config-fixed");
        await File.WriteAllTextAsync(Path.Combine(directory, "speech-corpus-config.passed.txt"),
            "Configured, missing, malformed and wrong-length corpora; mode selection, start command, narrow hints and fixed-mode playback passed.");
    }

    private static void Capture(Control control, string directory, string name, double scale = 1)
    {
        using var bitmap = new RenderTargetBitmap(
            new PixelSize(
                (int)(control.Bounds.Width * scale),
                (int)(control.Bounds.Height * scale)
            ),
            new Vector(96, 96)
        );
        // Scale the complete visual once. Per-target DPI can double-scale cached child
        // drawing operations in Avalonia 12, producing misleading cropped screenshots.
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawRectangle(new VisualBrush(control) { Stretch = Stretch.Fill }, null,
                new Rect(control.Bounds.Size));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static void AssertTrainingStyles(
        Control view,
        bool fieldsDisabled,
        bool replayEnabled,
        bool saveEnabled,
        bool checkActions = true
    )
    {
        var nodes = Avalonia
            .VisualTree.VisualExtensions.GetVisualDescendants(view)
            .OfType<Control>()
            .ToArray();
        foreach (var section in nodes.Where(c => c.Name is "CorpusSettingsSection" or "CarrierSettingsSection" or "ModeSettingsSection"))
            foreach (var label in section.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => !t.GetVisualAncestors().Any(a => a is ComboBox or NumericUpDown or Button or Slider)))
                if (!label.IsEffectivelyEnabled || label.Opacity != 1)
                    throw new InvalidOperationException($"训练说明文字进入禁用状态：{label.Text}");
        foreach (var choice in nodes.OfType<ComboBox>().Where(c => c.IsEffectivelyVisible))
        {
            var disabled = fieldsDisabled;
            if (choice.IsEffectivelyEnabled == disabled || choice.Opacity != 1)
                throw new InvalidOperationException("下拉框禁用状态不正确。");
            AssertBrush(choice.Background, disabled ? "#ECF2F8" : "#FFFFFF");
            AssertBrush(choice.BorderBrush, "#E2E8F2");
            var children = Avalonia
                .VisualTree.VisualExtensions.GetVisualDescendants(choice)
                .OfType<Control>()
                .ToArray();
            var arrow = children.OfType<Avalonia.Controls.Shapes.Path>().Single();
            AssertBrush(arrow.Fill, "#E6000000");
            foreach (
                var text in children
                    .OfType<TextBlock>()
                    .Where(t => t.Text == choice.SelectedItem?.ToString())
            )
                if (choice.SelectedItem is null)
                    throw new InvalidOperationException(
                        $"选择为空: {choice.Name}, count={choice.Items.Count}, UI={((BasicViewModel)view.DataContext!).CurrentPage}"
                    );
                else
                    AssertBrush(text.Foreground, "#232B38");
        }
        foreach (var name in new[] { "SpeechRateInput", "DelayInput" })
        {
            var input = nodes.OfType<EGGtCSPlatform.Controls.StimulusNumericInput>().Single(c => c.Name == name);
            var disabled = fieldsDisabled;
            if (input.IsEffectivelyEnabled == disabled || input.CornerRadius != new CornerRadius(6))
                throw new InvalidOperationException($"{name} 状态或圆角不正确。");
            var spinner = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(input)
                .OfType<ButtonSpinner>().Single(c => c.Name == "PART_Spinner");
            AssertBrush(spinner.Background, disabled ? "#ECF2F8" : "#FFFFFF");
            AssertBrush(spinner.BorderBrush, "#E2E8F2");
            if (spinner.CornerRadius != new CornerRadius(6))
                throw new InvalidOperationException("输入框内部仍为直角。");
        }
        var delaySlider = nodes.OfType<Slider>().Single(c => c.Name == "DelaySlider");
        if (delaySlider.IsEffectivelyEnabled == fieldsDisabled || delaySlider.Bounds.Width < 40)
            throw new InvalidOperationException("延时滑条状态或可用宽度不正确。");
        if (checkActions)
        {
            foreach (
                var (name, enabled, fill, foreground) in new[]
                {
                    ("ReplayButton", replayEnabled, "#FFFFFF", "#232B38"),
                    ("SaveNextButton", saveEnabled, "#3941B6", "#FFFFFF"),
                }
            )
            {
                var button = nodes.OfType<Button>().Single(c => c.Name == name);
                if (
                    button.IsEffectivelyEnabled != enabled
                    || button.Opacity != 1
                    || button.CornerRadius != new CornerRadius(6)
                )
                    throw new InvalidOperationException($"{name} 状态不正确。");
                AssertBrush(button.Background, enabled ? fill : "#ACB7C8");
                foreach (
                    var text in Avalonia
                        .VisualTree.VisualExtensions.GetVisualDescendants(button)
                        .OfType<TextBlock>()
                )
                    AssertBrush(text.Foreground, enabled ? foreground : "#FFFFFF");
            }
        }
        var axis = nodes.OfType<EGGtCSPlatform.Controls.SpeechTrainingTimeAxis>().Single();
        var voice = nodes.OfType<EGGtCSPlatform.Controls.WaveformPlotSurface>().First();
        var wavePanel = nodes.Single(c => c.Name == "SpeechWaveformPanel");
        var origin = axis.TranslatePoint(new Point(46, 0), view)!.Value;
        var plotOrigin = voice.TranslatePoint(default, view)!.Value;
        if (
            axis.Bounds.Height < 36
            || Math.Abs(origin.X - plotOrigin.X) > 1
            || wavePanel.ClipToBounds
        )
            throw new InvalidOperationException("共享时间轴或圆角裁切布局不正确。");
        AssertScoreButtons(view);
        AssertAxisInk(
            view,
            axis,
            Enumerable.Range(0, 9).Select(i => new Point(46 + (axis.Bounds.Width - 64) * i / 8, 8)),
            "#7288AB"
        );
        AssertAxisInk(view, axis, [new Point(46 + (axis.Bounds.Width - 64) / 16, 5)], "#7288AB");
        var amplitude = nodes
            .OfType<EGGtCSPlatform.Controls.SpeechTrainingAmplitudeAxis>()
            .Single();
        AssertAxisInk(
            view,
            amplitude,
            Enumerable
                .Range(0, 5)
                .Select(i => new Point(47, 24 + (amplitude.Bounds.Height - 36) * i / 4)),
            "#A9BAD4"
        );
        var stimulus = nodes.OfType<EGGtCSPlatform.Controls.SpeechStimulusPreviewControl>().Single(c => c.DrawAxes);
        var stimulusOrigin = stimulus.TranslatePoint(new Point(46, 24), view)!.Value;
        if (Math.Abs(stimulusOrigin.X - plotOrigin.X) > 1
            || Math.Abs((stimulus.Bounds.Width - 64) - voice.Bounds.Width) > 1)
            throw new InvalidOperationException("刺激预览与语音时间轴未对齐。");
        AssertAxisInk(
            view,
            stimulus,
            Enumerable
                .Range(0, 5)
                .Select(i => new Point(47, 24 + (stimulus.Bounds.Height - 36) * i / 4)),
            "#9AAAC2"
        );
    }

    private static double AssertScoreButtons(Control view)
    {
        var panels = Avalonia
            .VisualTree.VisualExtensions.GetVisualDescendants(view)
            .OfType<EGGtCSPlatform.Controls.SpeechScoreButtonsPanel>()
            .ToArray();
        if (panels.Length == 0)
            throw new InvalidOperationException("缺少评分按钮容器。");
        foreach (var panel in panels)
        {
            var buttons = panel.Children.OfType<Button>().ToArray();
            if (buttons.Length != 2)
                throw new InvalidOperationException("评分按钮数量不符。");
            var width = panel.Bounds.Width;
            var expected = Math.Min(Math.Max(24, width * 0.3), (width - Math.Min(6, width)) / 2);
            foreach (var button in buttons)
            {
                var bounds = button.Bounds;
                if (
                    Math.Abs(bounds.Width - bounds.Height) > 1
                    || Math.Abs(bounds.Width - expected) > 1
                    || bounds.Left < -1
                    || bounds.Right > width + 1
                    || bounds.Bottom > panel.Bounds.Height + 1
                )
                    throw new InvalidOperationException(
                        $"评分按钮比例或边界不正确：{bounds}，容器 {panel.Bounds}。"
                    );
                var icon = Avalonia
                    .VisualTree.VisualExtensions.GetVisualDescendants(button)
                    .OfType<Viewbox>()
                    .Single();
                var center = icon.TranslatePoint(
                    new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2),
                    button
                )!.Value;
                if (
                    Math.Abs(center.X - bounds.Width / 2) > 1
                    || Math.Abs(center.Y - bounds.Height / 2) > 1
                )
                    throw new InvalidOperationException("评分图标未居中。");
            }
        }
        return panels[0].Children[0].Bounds.Width;
    }

    // Sample rendered pixels, so missing axis/tick drawing cannot pass layout-only assertions.
    private static void AssertAxisInk(
        Control view,
        Control control,
        IEnumerable<Point> points,
        string color
    )
    {
        var root = (TopLevel.GetTopLevel(view) as Window)?.Content as Control ?? view;
        var width = (int)Math.Ceiling(root.Bounds.Width);
        var height = (int)Math.Ceiling(root.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(root);
        var bytes = new byte[width * height * 4];
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, bytes.Length, width * 4);
            System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, bytes.Length);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
        var expected = Color.Parse(color);
        foreach (var position in points)
        {
            var translated = control.TranslatePoint(position, root)!.Value;
            var point = translated;
            var found = false;
            for (
                var y = Math.Max(0, (int)point.Y - 1);
                y <= Math.Min(height - 1, (int)point.Y + 1);
                y++
            )
            for (var x = Math.Max(0, (int)point.X); x <= Math.Min(width - 1, (int)point.X + 1); x++)
            {
                var offset = (y * width + x) * 4;
                var alpha = bytes[offset + 3];
                if (alpha < 80)
                    continue;
                // Avalonia's Windows render target is premultiplied BGRA.
                var b = bytes[offset] * 255.0 / alpha;
                var g = bytes[offset + 1] * 255.0 / alpha;
                var r = bytes[offset + 2] * 255.0 / alpha;
                // One-DIP strokes blend with the panel or the purple zero line at fractional pixels.
                foreach (
                    var background in new[]
                    {
                        Colors.White,
                        Color.Parse("#F8FAFD"),
                        Color.Parse("#9536F3"),
                    }
                )
                    for (var coverage = 0.4; coverage <= 1.01; coverage += 0.05)
                    {
                        found |=
                            Math.Abs(r - (expected.R * coverage + background.R * (1 - coverage)))
                                < 12
                            && Math.Abs(g - (expected.G * coverage + background.G * (1 - coverage)))
                                < 12
                            && Math.Abs(b - (expected.B * coverage + background.B * (1 - coverage)))
                                < 12;
                    }
            }
            if (!found)
            {
                throw new InvalidOperationException(
                    $"{control.GetType().Name} 缺少轴线或刻度像素：{point}。"
                );
            }
        }
    }

    private static void AssertBrush(IBrush? brush, string expected)
    {
        if (brush is not ISolidColorBrush solid || solid.Color != Color.Parse(expected))
            throw new InvalidOperationException($"颜色不符：预期 {expected}，实际 {brush}。");
    }

    private sealed class Host : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private static SpeechTrainingResultsRouteData RoundDetail(EnvelopeStimulationPageViewModel model)
    {
        var experiment = model.ExperimentSnapshot();
        if (experiment.Rounds.Count == 0)
            throw new InvalidOperationException("训练未生成结果快照。");
        return SpeechResultRoutes.Detail(experiment.Source, experiment.Rounds.Last());
    }

    private sealed class Router : INavigationRouter
    {
        public ElectrodeConfigurationRouteData? Electrodes { get; private set; }
        public EnvelopeStimulationRouteData? Envelope { get; private set; }
        public SpeechTrainingResultsRouteData? SpeechResults { get; private set; }

        public void Navigate(SpeechTrainingResultsRouteData route) => SpeechResults = route;

        public void Navigate(SpeechExperimentResultsRouteData route)
        {
            var experiment = route.Snapshot ?? throw new InvalidOperationException("实验结果路由缺少快照。");
            SpeechResults = SpeechResultRoutes.Detail(experiment.Source, experiment.Rounds.Last());
        }

        public void Navigate(ElectrodeConfigurationRouteData route) => Electrodes = route;

        public void Navigate(EnvelopeStimulationRouteData route) => Envelope = route;

        public void Navigate(ExperimentRunRouteData route) =>
            throw new InvalidOperationException("错误进入普通运行页。");

        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData route) { }

        public void Navigate(StimulusConfigurationRouteData route) { }

        public void Navigate(ExperimentRerunRouteData route) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
