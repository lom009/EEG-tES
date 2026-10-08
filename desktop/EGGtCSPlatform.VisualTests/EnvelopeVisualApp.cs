using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class EnvelopeVisualApp : App
{
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
                await Task.Delay(150);
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
                    dialog.SubjectId, dialog.ExperimentId, "tDCS · 单刺激",
                    "峰值 1 mA", "CH1=FP2、CH2=F3", "", "刺激阻抗检测已完成",
                    showAcquisition: false);
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
                var placeholder = new EnvelopeStimulationPageView
                {
                    DataContext = new EnvelopeStimulationPageViewModel(router.Envelope, router),
                };
                window.Content = placeholder;
                window.Width = 1440;
                window.Height = 768;
                await Task.Delay(100);
                Capture(placeholder, directory, "envelope-placeholder");
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

    private static void Capture(Control control, string directory, string name)
    {
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height),
            new Vector(96, 96)
        );
        bitmap.Render(control);
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private sealed class Host : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private sealed class Router : INavigationRouter
    {
        public ElectrodeConfigurationRouteData? Electrodes { get; private set; }
        public EnvelopeStimulationRouteData? Envelope { get; private set; }

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
