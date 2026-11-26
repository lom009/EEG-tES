using Avalonia;
using Avalonia.Controls;
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
    private static async Task AssertRirTraining(
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
        model.SelectedMode = model.ModeOptions[4];
        model.ListSize = 10;
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(1000);
        await model.GenerateRirCommand.ExecuteAsync(null);
        if (model.RirPreview is null || model.ShowAdaptiveScoringHint || model.IsFixedMode)
            throw new InvalidOperationException("RIR 预览或模式状态错误。");
        var scroll = view.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Single(c => c.Name == "TrainingSettingsScroll");
        model.SelectedCarrier = model.CarrierOptions[1];
        await Task.Delay(200);
        var carrierSection = view.GetVisualDescendants().OfType<StackPanel>()
            .Single(c => c.Name == "CarrierSettingsSection");
        var modeSection = view.GetVisualDescendants().OfType<StackPanel>()
            .Single(c => c.Name == "ModeSettingsSection");
        if (!carrierSection.GetVisualDescendants().OfType<ComboBox>()
                .Any(c => c.Name == "VocoderAlgorithmSelector")
            || carrierSection.Bounds.Bottom >= modeSection.Bounds.Top)
            throw new InvalidOperationException("载体参数未连续分组在测试模式之前。");
        carrierSection.BringIntoView();
        await Task.Delay(250);
        Capture(view, directory, "speech-rir-carrier-group");
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(250);
        carrierSection.BringIntoView();
        await Task.Delay(250);
        Capture(view, directory, "speech-rir-carrier-group-small");
        if (scroll.Extent.Width > scroll.Viewport.Width + 1)
            throw new InvalidOperationException("载体设置横向溢出。");
        model.SelectedCarrier = model.CarrierOptions[0];
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(250);
        view.GetVisualDescendants().OfType<StimulusNumericInput>()
            .Single(c => c.Name == "RirRoomXInput").BringIntoView();
        await Task.Delay(250);
        Capture(view, directory, "speech-rir-settings");
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await Task.Delay(250);
        Capture(view, directory, "speech-rir-preview");
        model.RirAddNoise = true;
        model.RirNoise = model.NoiseOptions[1];
        await Task.Delay(150);
        if (model.RirPreview is not null)
            throw new InvalidOperationException("RIR 参数变更后预览未失效。");
        var input = view.GetVisualDescendants()
            .OfType<StimulusNumericInput>()
            .Single(c => c.Name == "RirSnrInput");
        var box = input.GetVisualDescendants().OfType<TextBox>().Single();
        box.SelectAll();
        box.SelectedText = "-7.25";
        Commit(box);
        await Task.Delay(100);
        if (input.Increment != 1 || model.RirSnrDb != -7m)
            throw new InvalidOperationException("RIR 负 SNR 未按现有 1 dB 步长提交。");
        await model.GenerateRirCommand.ExecuteAsync(null);
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(350);
        view.GetVisualDescendants().OfType<StimulusNumericInput>()
            .Single(c => c.Name == "RirRoomXInput").BringIntoView();
        await Task.Delay(250);
        Capture(view, directory, "speech-rir-small-fields");
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await Task.Delay(250);
        if (scroll.Extent.Width > scroll.Viewport.Width + 1)
            throw new InvalidOperationException("RIR 设置横向溢出。");
        Capture(view, directory, "speech-rir-small");
        window.Width = 1440;
        window.Height = 900;
        await model.StartCommand.ExecuteAsync(null);
        await Task.Delay(300);
        if (input.IsEffectivelyEnabled || model.RirPreview is null)
            throw new InvalidOperationException("RIR 开始后未锁定配置。");
        Capture(view, directory, "speech-rir-playing");
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
            !results.IsFixedRir
            || results.IsAdaptive
            || results.HasAdaptiveValue
            || results.AccuracyText != "50%"
        )
            throw new InvalidOperationException("RIR 汇总错误。");
        shell.CurrentPage = results;
        await Task.Delay(1000);
        Capture(view, directory, "speech-rir-results");
        view.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .First(c => c.Name == "DetailsToggle")
            .IsChecked = true;
        await Task.Delay(200);
        Capture(view, directory, "speech-rir-details");
        window.Width = 1000;
        window.Height = 650;
        await Task.Delay(350);
        Capture(view, directory, "speech-rir-results-small");
        File.WriteAllText(
            Path.Combine(directory, "speech-rir.passed.txt"),
            "RIR generation, invalidation, signed noise input, locked playback, complete results and small layouts passed."
        );
    }
}
