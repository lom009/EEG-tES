using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed partial class EnvelopeVisualApp
{
    private static async Task AssertFixedSnrTraining(
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
        model.SelectedMode = model.ModeOptions[3];
        model.SelectedNoise = model.NoiseOptions[1];
        model.ListSize = 10;
        await Task.Delay(1000);
        var input = view.GetVisualDescendants()
            .OfType<StimulusNumericInput>()
            .Single(c => c.Name == "FixedSnrInput");
        var initial = view.GetVisualDescendants()
            .OfType<StimulusNumericInput>()
            .Single(c => c.Name == "InitialSnrInput");
        var delay = view.GetVisualDescendants()
            .OfType<StimulusNumericInput>()
            .Single(c => c.Name == "DelayInput");
        var noise = view.GetVisualDescendants()
            .OfType<ComboBox>()
            .Single(c => c.Name == "NoiseSelector");
        var slider = view.GetVisualDescendants()
            .OfType<Slider>()
            .Single(c => c.Name == "SpeechRateSlider");
        var hasScoringHint = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(c => c.Name == "AdaptiveScoringHint");
        if (
            !input.IsEffectivelyVisible
            || initial.IsEffectivelyVisible
            || slider.IsEffectivelyVisible
            || hasScoringHint
        )
            throw new InvalidOperationException("固定 SNR 控件可见性错误。");

        var box = input.GetVisualDescendants().OfType<TextBox>().Single();
        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var negative = CultureInfo.CurrentCulture.NumberFormat.NegativeSign;
        box.Focus();
        box.SelectAll();
        box.RaiseEvent(
            new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = negative }
        );
        box.RaiseEvent(
            new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Text = "10" + separator + "25",
            }
        );
        Commit(box);
        await Task.Delay(100);
        if (model.FixedSnrDb != -10m)
            throw new InvalidOperationException("固定 SNR 负数键盘输入失败。");

        // SelectedText is the same multi-character insertion route used by TextBox paste.
        box.SelectAll();
        box.SelectedText = negative + "7" + separator + "25";
        Commit(box);
        await Task.Delay(100);
        if (model.FixedSnrDb != -7m)
            throw new InvalidOperationException("固定 SNR 负数粘贴路径失败。");
        foreach (var invalid in new[] { "--2", "1-2", "1e3", negative + "7" + separator + "255" })
        {
            box.SelectAll();
            box.SelectedText = invalid;
            Commit(box);
            await Task.Delay(50);
            if (model.FixedSnrDb != -7m)
                throw new InvalidOperationException($"固定 SNR 接受了非法文本 {invalid}，当前值 {model.FixedSnrDb}。");
        }
        model.SelectedMode = model.ModeOptions[2];
        await Task.Delay(200);
        foreach (var unsigned in new[] { initial, delay })
        {
            unsigned.ApplyTemplate();
            var unsignedBox = unsigned.GetVisualDescendants().OfType<TextBox>().Single();
            var before = unsigned.Value;
            unsignedBox.SelectAll();
            unsignedBox.SelectedText = negative + "2";
            Commit(unsignedBox);
            await Task.Delay(50);
            if (unsigned.Value != before || unsignedBox.Text?.Contains(negative) == true)
                throw new InvalidOperationException("无符号参数接受了负号。");
        }
        model.SelectedMode = model.ModeOptions[3];
        await Task.Delay(200);
        box.SelectAll();
        box.SelectedText = "30";
        Commit(box);
        await Task.Delay(100);
        if (model.FixedSnrDb != 30)
            throw new InvalidOperationException("固定 SNR 被错误限制为 20 dB。");
        model.FixedSnrDb = -7m;
        noise.Focus();
        await Task.Delay(150);
        Capture(view, directory, "speech-fixed-snr-start");
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(400);
        Capture(view, directory, "speech-fixed-snr-small");
        var scroll = view.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Single(c => c.Name == "TrainingSettingsScroll");
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await Task.Delay(200);
        if (scroll.Extent.Width > scroll.Viewport.Width + 1)
            throw new InvalidOperationException("固定 SNR 配置横向溢出。");
        Capture(view, directory, "speech-fixed-snr-small-settings");
        window.Width = 1440;
        window.Height = 900;
        scroll.Offset = default;
        await model.StartCommand.ExecuteAsync(null);
        await Task.Delay(300);
        if (
            input.IsEffectivelyEnabled
            || noise.IsEffectivelyEnabled
            || model.SnrText.Contains("反转")
            || !model.SnrText.Contains("-7")
        )
            throw new InvalidOperationException("固定 SNR 播放状态错误。");
        Capture(view, directory, "speech-fixed-snr-playing");
        for (var i = 0; i < 10; i++)
        {
            if (i % 2 == 0)
                model.AllCorrectCommand.Execute(null);
            else
                model.AllWrongCommand.Execute(null);
            await model.SaveNextCommand.ExecuteAsync(null);
        }
        await Task.Delay(1000);
        var results = new SpeechTrainingResultsPageViewModel(RoundDetail(model));
        if (
            !results.IsFixedSnr
            || results.IsAdaptive
            || results.HasAdaptiveValue
            || results.AccuracyText != "50%"
        )
            throw new InvalidOperationException("固定 SNR 汇总错误。");
        shell.CurrentPage = results;
        await Task.Delay(1000);
        Capture(view, directory, "speech-fixed-snr-results");
        view.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .First(c => c.Name == "DetailsToggle")
            .IsChecked = true;
        await Task.Delay(200);
        Capture(view, directory, "speech-fixed-snr-details");
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(400);
        Capture(view, directory, "speech-fixed-snr-results-small");
        File.WriteAllText(
            Path.Combine(directory, "speech-fixed-snr.passed.txt"),
            "Signed input, paste route, unsigned regression, locked settings, small layout and accuracy results passed."
        );
    }

    private static void Commit(TextBox box) =>
        box.RaiseEvent(
            new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }
        );
}
