using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed partial class EnvelopeVisualApp
{
    private static async Task AssertSpeechInteractions(
        Window window,
        BasicView view,
        BasicViewModel shell,
        Router router,
        string directory
    )
    {
        foreach (var modeIndex in new[] { 0, 3, 4 })
        {
            var player = new ControllableSpeechAudioPlayer();
            using var model = new EnvelopeStimulationPageViewModel(
                router.Envelope!,
                router,
                () => new(new FileSpeechCorpusProvider(), player)
            );
            var host = view.FindControl<TransitioningContentControl>("PageContentHost")!;
            var transition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnTransitionCompleted(object? sender, TransitionCompletedEventArgs args)
            {
                if (ReferenceEquals(args.To, model) && args.HasRunToCompletion)
                    transition.TrySetResult();
            }
            host.TransitionCompleted += OnTransitionCompleted;
            try
            {
                shell.CurrentPage = model;
                await transition.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                host.TransitionCompleted -= OnTransitionCompleted;
            }
            model.SelectedMode = model.ModeOptions[modeIndex];
            if (modeIndex == 0)
                model.SelectedCarrier = "声码器仿真";
            // The transition host can retain an inactive presenter. Inspect only this page.
            var page = view.GetVisualDescendants()
                .OfType<EnvelopeStimulationPageView>()
                .Single(p => ReferenceEquals(p.DataContext, model));
            if (modeIndex == 4)
                await model.GenerateRirCommand.ExecuteAsync(null);
            model.VolumePercent = 35;
            var sections = page.GetVisualDescendants()
                .OfType<Control>()
                .Where(c =>
                    c.Name
                        is "CorpusSettingsSection"
                            or "CarrierSettingsSection"
                            or "ModeSettingsSection"
                )
                .ToArray();
            var labels = sections
                .SelectMany(s => s.GetVisualDescendants().OfType<TextBlock>())
                .Where(t =>
                    !t.GetVisualAncestors()
                        .Any(a => a is ComboBox or NumericUpDown or Button or Slider)
                )
                .Distinct()
                .Select(t => (Label: t, Color: t.Foreground?.ToString(), t.Opacity))
                .ToArray();
            if (labels.Length < 20)
                throw new InvalidOperationException("训练说明文字覆盖不足。");
            var fields = sections
                .SelectMany(s => s.GetVisualDescendants().OfType<Control>())
                .Where(c => c is ComboBox or StimulusNumericInput or Slider or CheckBox)
                .Distinct()
                .ToArray();
            var mute = page.GetVisualDescendants()
                .OfType<Button>()
                .Single(b => b.Name == "MuteVolumeButton");
            var volume = page.GetVisualDescendants()
                .OfType<Slider>()
                .Single(s => s.Name == "VolumeSlider");
            if (
                page.GetVisualDescendants()
                    .OfType<Button>()
                    .Any(b => b.Name == "RestoreVolumeButton")
            )
                throw new InvalidOperationException("音量区仍有第二个按钮。");
            foreach (var size in new[] { (1440, 900), (1000, 650) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                await Task.Delay(200);
                AssertVolumeLayout(page);
                Capture(view, directory, $"interactions-{modeIndex}-ready-{size.Item1}");
            }
            await model.StartCommand.ExecuteAsync(null);
            await Task.Delay(200);
            if (!model.IsPlaying || model.CanConfigure || fields.Any(c => c.IsEffectivelyEnabled))
                throw new InvalidOperationException(
                    $"训练模式 {modeIndex} 的输入控件未锁定：{model.StatusText}"
                );
            foreach (var label in labels)
                if (
                    !label.Label.IsEffectivelyEnabled
                    || label.Label.Opacity != label.Opacity
                    || label.Label.Foreground?.ToString() != label.Color
                )
                    throw new InvalidOperationException(
                        $"字段说明样式发生改变：{label.Label.Text}"
                    );
            volume.Value = 35;
            mute.Command!.Execute(null);
            if (
                !model.IsMuted
                || volume.Value != 0
                || player.Volume != 0
                || AutomationProperties.GetName(mute) != "恢复音量"
            )
                throw new InvalidOperationException("播放中静音状态未同步。");
            Capture(view, directory, $"interactions-{modeIndex}-muted");
            mute.Command.Execute(null);
            if (model.IsMuted || volume.Value != 35 || AutomationProperties.GetName(mute) != "静音")
                throw new InvalidOperationException("播放中恢复音量失败。");
            volume.Value = 0;
            if (!model.IsMuted)
                throw new InvalidOperationException("滑块零音量未同步静音图标。");
            volume.Value = 22;
            if (model.IsMuted)
                throw new InvalidOperationException("滑块非零音量未同步扬声器图标。");
            foreach (var size in new[] { (1440, 900), (1000, 650) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                await Task.Delay(200);
                var scroll = page.GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .Single(s => s.Name == "TrainingSettingsScroll");
                scroll.ScrollToEnd();
                await Task.Delay(100);
                Capture(view, directory, $"interactions-{modeIndex}-playing-{size.Item1}");
            }
            if (!await model.PrepareToLeaveAsync())
                throw new InvalidOperationException("交互验收退出清理失败。");
        }
        await File.WriteAllTextAsync(
            Path.Combine(directory, "speech-interactions.passed.txt"),
            "Single mute toggle, volume restore/slider sync, fixed-vocoder/SNR/RIR field locks and unchanged labels; 1440x900 and 1000x650: passed."
        );
    }
}
