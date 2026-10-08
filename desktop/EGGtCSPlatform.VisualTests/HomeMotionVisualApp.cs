using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed class HomeMotionVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(output);
        File.Delete(Path.Combine(output, "home.error.txt"));
        var context = new DeviceSelectionContext();
        var router = new Router();
        var view = new IndexView { DataContext = new IndexViewModel(router, context) };
        var window = new Window { Title = "首页静态背景验收", Width = 1440, Height = 900, Content = view };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                var motion = view.FindControl<HomeMotionView>("MotionHome")!;
                for (var i = 0; i < 300 && !motion.IsReady; i++) await Task.Delay(100);
                if (!motion.IsReady) throw new Exception(motion.LastError ?? "Home did not load");
                var web = (NativeWebView)motion.Content!;
                await Task.Delay(600);
                foreach (var id in new[] { "new", "history", "device", "tolerance" })
                {
                    await web.InvokeScript($"document.querySelector('[data-action={id}]').click()");
                    await Task.Delay(100);
                }
                if (router.Count != 4) throw new Exception("Expected four native navigation commands.");
                context.SetConnectedDevice("visual-test", "visual-test");
                await Task.Delay(700);
                var connected = await web.InvokeScript("document.querySelector('[data-action=device] small').textContent");
                if (connected?.Contains("已连接") != true) throw new Exception("Device state did not synchronize: " + connected);
                var background = await web.InvokeScript("String(document.querySelector('.home-background-image').complete && document.querySelector('.home-background-image').naturalWidth > 0)");
                if (background != "true") throw new Exception("Original static background did not load: " + background);
                var videoCount = await web.InvokeScript("String(document.querySelectorAll('video').length)");
                var videoRequests = await web.InvokeScript("String(performance.getEntriesByType('resource').filter(entry => entry.name.includes('.mp4')).length)");
                if (videoCount != "0" || videoRequests != "0") throw new Exception("Home still loads a background video.");
                if (typeof(HomeMotionView).Assembly.GetManifestResourceNames().Any(name => name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && name.Contains(".Home.")))
                    throw new Exception("Home video remains embedded in the application.");
                window.Content = null;
                await Task.Delay(300);
                if (motion.IsReady || motion.PreviewUrl != null)
                    throw new Exception("Home resources did not stop on detach");
                File.WriteAllText(Path.Combine(output, "home.passed.txt"),
                    "PASS: original static background loads; no video element, MP4 request or embedded home video; four cards invoke native routes; device state updates; home resources stop on detach.");
                desktop.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "home.error.txt"), ex.ToString());
                desktop.Shutdown(1);
            }
        };
        window.Show();
    }
    private sealed class Router : INavigationRouter
    {
        public int Count;
        public void Navigate(ApplicationPageNames route) => Count++;
        public void Navigate(StartExperimentRouteData route) => Count++;
        public void Navigate(StimulusConfigurationRouteData route) { }
        public void Navigate(ElectrodeConfigurationRouteData route) { }
        public void Navigate(ExperimentRunRouteData route) { }
        public void Navigate(ExperimentRerunRouteData route) { }
        public void GoBack() { }
        public void GoHome() { }
    }
}
