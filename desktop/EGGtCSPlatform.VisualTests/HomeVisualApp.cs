using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed class HomeVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var connection = new DeviceSelectionContext();
        var view = new IndexView { DataContext = new IndexViewModel(new VisualTestNavigationRouter(), connection) };
        var window = new Window { Width = 1440, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var directory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
            Directory.CreateDirectory(directory);
            try
            {
                foreach (var (width, height) in new[] { (1440, 900), (1000, 650) })
                {
                    window.Width = width;
                    window.Height = height;
                    await Task.Delay(300);
                    using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
                    bitmap.Render(view);
                    bitmap.Save(Path.Combine(directory, $"home-{width}.png"), PngBitmapEncoderOptions.Default);
                }
                desktop.Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(VisualTestOptions.Current.ErrorPath, error.ToString());
                desktop.Shutdown(1);
            }
        };
        window.Show();
    }
}
