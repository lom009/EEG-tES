using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class EegMappingVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var outputDirectory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        var configurationPath = Path.Combine(
            outputDirectory,
            "PhysicalChannelMappingPage.appsettings.json"
        );
        var screenshotPath = Path.Combine(
            outputDirectory,
            "PhysicalChannelMappingPage.reloaded.png"
        );
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(configurationPath, new JsonObject().ToJsonString());
        var service = new EegPhysicalChannelMappingService(configurationPath);
        service.Save(
            EegPhysicalChannelMappingService.SupportedPhysicalChannelCount,
            ElectrodePositionCatalog
                .All.Select(position => new EegPhysicalChannelMapping(position.Id, null))
                .ToArray()
        );

        var firstPage = new PhysicalChannelMappingPageViewModel(service);
        firstPage.Mappings.Single(mapping => mapping.ElectrodeId == "FP1").SelectedOption =
            firstPage.ChannelOptions.Single(option => option.Value == 1);
        firstPage.Mappings.Single(mapping => mapping.ElectrodeId == "O2").SelectedOption =
            firstPage.ChannelOptions.Single(option => option.Value == 32);
        firstPage.SaveCommand.Execute(null);
        if (!firstPage.IsSaved)
            throw new InvalidOperationException(firstPage.StatusText);

        var reloadedPage = new PhysicalChannelMappingPageViewModel(service);
        if (
            reloadedPage.Mappings.Single(mapping => mapping.ElectrodeId == "FP1").PhysicalChannel
                != 1
            || reloadedPage.Mappings.Single(mapping => mapping.ElectrodeId == "O2").PhysicalChannel
                != 32
        )
            throw new InvalidOperationException("EEG 映射保存后未能在新页面实例中回显。");
        var connection = new DeviceSelectionContext();
        connection.SetConnectedDevice("simulator-default", "EGG/tCS Simulator");
        var content = new BasicView
        {
            DataContext = new BasicViewModel(
                new VisualTestNavigationRouter(),
                reloadedPage,
                connection
            ),
        };
        var window = new Window
        {
            Title = "EEG采集物理通道映射 - 自动回显验收",
            Width = 1440,
            Height = 900,
            MinWidth = 1200,
            MinHeight = 760,
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = content,
        };

        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(500);
                Capture(content, screenshotPath);
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
                window.Close();
                desktop.Shutdown(Environment.ExitCode);
            }
        };
    }

    private static void Capture(Control content, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Ceiling(content.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(content.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(content);
        using var stream = File.Create(outputPath);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }
}
