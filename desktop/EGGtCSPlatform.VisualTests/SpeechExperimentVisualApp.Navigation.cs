using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed partial class SpeechExperimentVisualApp
{
    private sealed class VisualDialogProvider : EGGtCSPlatform.Interfaces.IDialogProvider
    {
        public System.Collections.ObjectModel.ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private static async Task VerifyTrainingNavigation(
        Window window, BasicView content, BasicViewModel originalShell,
        EnvelopeStimulationPageViewModel training,
        VisualTestNavigationRouter router, string output)
    {
        var stimulus = new StimulusConfigurationPageViewModel(
            new StimulusConfigurationRouteData("LAYOUT", "SUBJECT"), router,
            new DialogService(() => null), new VisualDialogProvider());
        var electrode = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData("LAYOUT", "SUBJECT",
                stimulus.CurrentConfiguration.CreateSnapshot()),
            router, new DialogService(() => null), new VisualDialogProvider(),
            electrodeCatalog: VisualElectrodeCatalog.Create());
        var samples = new List<string>();
        void VerifyNavigationButtons()
        {
            var back = content.FindControl<StackPanel>("HeaderNavigationAndStatusPanel")!
                .GetVisualDescendants().OfType<Button>().First();
            var logo = content.FindControl<Button>("HomeLogoButton")!;
            if (back.IsEffectivelyEnabled != training.CanGoBack
                || logo.IsEffectivelyEnabled != training.CanGoBack)
                throw new InvalidOperationException("Back and home buttons do not match the training navigation lock.");
            if (!training.CanGoBack)
                return;
            foreach (var button in new[] { back, logo })
            {
                var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), content)!.Value;
                var hit = content.InputHitTest(center) as Visual;
                if (hit != button && (hit is null || !hit.GetVisualAncestors().Contains(button)))
                    throw new InvalidOperationException("Training page blocks an enabled navigation button.");
            }
        }
        foreach (var animation in new[] { false, true })
        foreach (var size in new[] { new Size(1440, 900), new Size(1024, 720) })
        {
            window.Width = size.Width;
            window.Height = size.Height;
            var shell = new BasicViewModel(router, training, new DeviceSelectionContext(), animation);
            content.DataContext = shell;
            await Task.Delay(400);
            var host = content.FindControl<TransitioningContentControl>("PageContentHost")!;
            var hostBounds = host.Bounds;
            var label = $"{size.Width}-{animation}";

            void Sample(string phase)
            {
                if (host.Bounds != hostBounds)
                    throw new InvalidOperationException($"{label}/{phase}: navigation host resized.");
                foreach (var view in host.GetVisualDescendants().OfType<UserControl>()
                    .Where(v => v is EnvelopeStimulationPageView or ElectrodeConfigurationPageView
                        or StimulusConfigurationPageView))
                {
                    var inset = view is StimulusConfigurationPageView ? 44 : 0;
                    var y = view.TranslatePoint(default, host)!.Value.Y;
                    if (Math.Abs(y - inset) > 1 || Math.Abs(view.Bounds.Height - (hostBounds.Height - inset)) > 1)
                        throw new InvalidOperationException($"{label}/{phase}: {view.GetType().Name} jumped: y={y}, size={view.Bounds.Size}.");
                    if (view is not EnvelopeStimulationPageView speech)
                        continue;
                    var panel = speech.FindControl<Border>("TrainingSettingsPanel")!;
                    var audio = speech.FindControl<ScrollViewer>("TrainingScroll")!;
                    var panelY = panel.TranslatePoint(default, host)!.Value.Y;
                    var audioY = audio.TranslatePoint(default, host)!.Value.Y;
                    if (Math.Abs(panelY) > 1 || Math.Abs(audioY - 44) > 1
                        || Math.Abs(panel.Bounds.Bottom - audio.Bounds.Bottom) > 1
                        || Math.Abs(audio.Bounds.Height - (hostBounds.Height - 68)) > 1)
                        throw new InvalidOperationException($"{label}/{phase}: training panel geometry changed.");
                }
                samples.Add($"{label}/{phase}: host={host.Bounds}");
            }

            Sample("settled");
            var speechView = content.GetVisualDescendants().OfType<EnvelopeStimulationPageView>().Single();
            var settings = speechView.FindControl<ScrollViewer>("TrainingSettingsScroll")!;
            if (settings.Viewport.Height <= 0)
                throw new InvalidOperationException("Training settings have no scroll viewport.");
            settings.Offset = new Vector(0, settings.Extent.Height);
            await Task.Delay(50);
            if (settings.Extent.Height > settings.Viewport.Height + 1 && settings.Offset.Y <= 0)
                throw new InvalidOperationException("Training settings cannot scroll.");
            var end = speechView.FindControl<Button>("EndTrainingButton")!;
            var endY = end.TranslatePoint(default, content)!.Value.Y;
            if (endY < 0 || endY + end.Bounds.Height > content.Bounds.Height)
                throw new InvalidOperationException("Training end button is clipped.");
            VerifyNavigationButtons();
            Capture(content, output, $"speech-navigation-{label}");
            settings.Offset = default;

            foreach (var other in new PageViewModel[] { electrode, stimulus })
            foreach (var frames in new[] { 8, 2, 2 })
            {
                shell.IsTransitionReversed = true;
                shell.CurrentPage = other;
                for (var frame = 0; frame < frames; frame++)
                {
                    await Task.Delay(50);
                    Sample($"out-{other.GetType().Name}-{frame}");
                }
                shell.IsTransitionReversed = false;
                shell.CurrentPage = training;
                for (var frame = 0; frame < frames; frame++)
                {
                    await Task.Delay(50);
                    Sample($"in-{other.GetType().Name}-{frame}");
                }
            }
            await Task.Delay(400);
            Sample("finished");
        }
        window.Width = 1440;
        window.Height = 900;
        content.DataContext = originalShell;
        await Task.Delay(400);
        if (!await training.FinishAsync(true) || !training.CanGoBack)
            throw new InvalidOperationException("Finishing the experiment did not release the navigation lock.");
        await Task.Delay(100);
        VerifyNavigationButtons();
        File.WriteAllLines(Path.Combine(output, "speech-navigation.passed.txt"), samples);
    }
}
