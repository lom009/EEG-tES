using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using EGGtCSPlatform.Views.Pages;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.VisualTests;

public sealed class DeviceConnectionVisualApp : App
{
    private DeviceManager? _deviceManager;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var connection = new DeviceSelectionContext();
        var useLiveDevice = string.Equals(
            VisualTestOptions.Current.Scenario,
            "device-connection-live",
            StringComparison.OrdinalIgnoreCase
        );
        var network = useLiveDevice
            ? new DeviceConnectionNetworkContext(
                Environment.GetEnvironmentVariable("EGGTCS_UDP_BROADCAST_ADDRESS")
                    ?? "255.255.255.255",
                Environment.GetEnvironmentVariable("EGGTCS_UDP_LOCAL_ADDRESS") ?? "0.0.0.0"
            )
            : new DeviceConnectionNetworkContext();
        var backendOptions = new DeviceBackendOptions();
        if (!useLiveDevice)
        {
            backendOptions.ConnectionSource = DeviceConnectionSource.Simulated;
            backendOptions.Capabilities.Status = DeviceCapabilitySource.Simulated;
            backendOptions.Capabilities.EegAcquisition = DeviceCapabilitySource.Simulated;
            backendOptions.Capabilities.Stimulation = DeviceCapabilitySource.Simulated;
            backendOptions.Capabilities.EegImpedance = DeviceCapabilitySource.Simulated;
            backendOptions.Capabilities.StimulationImpedance = DeviceCapabilitySource.Simulated;
            backendOptions.Capabilities.Tolerance = DeviceCapabilitySource.Simulated;
        }
        var backend = new ConfigurableDeviceBackend(
            backendOptions,
            network,
            new DeviceHeartbeatPauseService(),
            new DeviceHeartbeatOptions { Enabled = false },
            NullByteTrafficLogger.Instance
        );
        _deviceManager = new DeviceManager(discoveries: [backend], connectors: [backend]);
        var dialogHost = new VisualDialogProvider();
        var fontScale = new FontScaleService(
            Options.Create(
                new DisplayOptions
                {
                    FontSizePreset = VisualTestOptions.Current.Scenario.Contains(
                        "extra-large",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? FontSizePreset.ExtraLarge
                        : FontSizePreset.Standard,
                }
            )
        );
        fontScale.Initialize();
        Window? window = null;
        var dialogService = new DialogService(() => window);
        var pageViewModel = new DeviceConnectionPageViewModel(
            new VisualTestNavigationRouter(),
            _deviceManager,
            connection,
            network,
            dialogService,
            dialogHost,
            new UnavailableConfigurationResetService(),
            new UnavailableRestartService(),
            fontScale
        );
        var page = CreatePage(pageViewModel, connection);
        var dialogViewModel = new DeviceConnectionDialogViewModel(
            _deviceManager,
            connection,
            network
        );
        dialogViewModel.Show();
        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(120, 35, 43, 56)),
            Children =
            {
                new DeviceConnectionDialogView
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    DataContext = dialogViewModel,
                },
            },
        };
        var root = new Grid { Children = { page, overlay } };
        window = new Window
        {
            Title = "设备连接 - 自动截图验收",
            Width = 1400,
            Height = 900,
            MinWidth = 1200,
            MinHeight = 760,
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = root,
        };

        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(400);
                await dialogViewModel.SearchCommand.ExecuteAsync(null);
                if (dialogViewModel.SelectedCandidate is null)
                    throw new InvalidOperationException(dialogViewModel.StatusText);
                await Task.Delay(200);
                Capture(root, VisualTestOptions.Current.DeviceConnectionDialogOutputPath);
                await dialogViewModel.ConnectCommand.ExecuteAsync(null);
                if (!connection.IsConnected)
                    throw new InvalidOperationException(dialogViewModel.StatusText);
                await Task.Delay(300);
                root.Children[0] = CreatePage(
                    new DeviceConnectionPageViewModel(
                        new VisualTestNavigationRouter(),
                        _deviceManager,
                        connection,
                        network,
                        dialogService,
                        dialogHost,
                        new UnavailableConfigurationResetService(),
                        new UnavailableRestartService(),
                        fontScale
                    ),
                    connection
                );
                overlay.IsVisible = false;
                await Task.Delay(300);
                Capture(root, VisualTestOptions.Current.DeviceConnectionConnectedOutputPath);
                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!
                );
                await File.WriteAllTextAsync(
                    VisualTestOptions.Current.ErrorPath,
                    exception.ToString()
                );
                Environment.ExitCode = 1;
            }
            finally
            {
                if (_deviceManager is not null)
                    await _deviceManager.DisposeAsync();
                window.Close();
                desktop.Shutdown(Environment.ExitCode);
            }
        };
    }

    private static BasicView CreatePage(
        DeviceConnectionPageViewModel pageViewModel,
        IDeviceSelectionContext connection
    ) =>
        new()
        {
            DataContext = new BasicViewModel(
                new VisualTestNavigationRouter(),
                pageViewModel,
                connection
            ),
        };

    private static void Capture(Control content, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Ceiling(content.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(content.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(content);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private sealed class VisualDialogProvider : IDialogProvider
    {
        public System.Collections.ObjectModel.ObservableCollection<DialogViewModel> DialogStack { get; } =
        [];
    }

    private sealed class UnavailableConfigurationResetService
        : EGGtCSPlatform.Configuration.IApplicationConfigurationResetService
    {
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnavailableRestartService : IApplicationRestartService
    {
        public Task RestartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
