using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class LoginVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var window = new LoginWindow();
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
            Directory.CreateDirectory(output);
            try
            {
                await Task.Delay(700);
                Capture("login");
                var inputs = window.GetVisualDescendants().OfType<TextBox>().ToArray();
                if (inputs.Length != 2) throw new Exception("Expected two login inputs.");
                inputs[0].Text = "13800138000";
                inputs[1].Text = "password-preview";
                inputs[1].Focus();
                await Task.Delay(200);
                Capture("login-filled");
                desktop.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(VisualTestOptions.Current.ErrorPath, ex.ToString());
                desktop.Shutdown(1);
            }
            void Capture(string name)
            {
                var content = (Control)window.Content!;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height));
                bitmap.Render(content);
                bitmap.Save(Path.Combine(output, name + ".png"));
            }
        };
        window.Show();
    }
}
