using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceConnectionPageViewModelTests
{
    [Theory]
    [InlineData("eggtcs.gen1", "1.0.1", "已连接", "软件通信协议：EGG/tCS Gen1 · 1.0.1")]
    [InlineData("test.protocol", "test.2", "已连接", "软件通信协议：test.protocol · test.2")]
    [InlineData("simulator", "simulator", "已连接", "软件通信协议：模拟通信")]
    [InlineData(null, null, "已连接", "软件通信协议：未提供")]
    [InlineData("eggtcs.gen1", "1.0.1", "未连接", "软件通信协议：--")]
    public void ProtocolCardUsesOnlyItsConnectionSnapshot(
        string? id,
        string? revision,
        string state,
        string expected
    )
    {
        var info = id is null
            ? null
            : new DeviceProtocolInfo(id, revision!, ProtocolRevisionSource.SoftwareDefault);
        var item = new DeviceConnectionItemViewModel(
            "device",
            "Device",
            null,
            null,
            null,
            state,
            false,
            info
        );
        Assert.Equal(expected, item.ProtocolDisplayText);
        Assert.Contains("设备未提供协议版本上报", item.ProtocolExplanation);
        if (state != "已连接")
            Assert.Null(item.ProtocolInfo);
    }

    [Fact]
    public async Task SimulationCardClearsSnapshotAfterDisconnect()
    {
        await using var runtime = new EGGtCSPlatform.DeviceRuntime.DeviceRuntimeBuilder()
            .UseSimulation()
            .Build();
        var device = await runtime.ConnectVerifiedAsync(
            new(
                new(new("sim"), "Simulation", null, null),
                new("simulator", "local", 0),
                "simulator"
            )
        );
        var model = CreateModel(
            runtime.Devices,
            new StubConfigurationResetService(),
            new StubRestartService()
        );
        Assert.Equal("软件通信协议：模拟通信", Assert.Single(model.Devices).ProtocolDisplayText);
        await runtime.Devices.DisconnectAsync(device.Identity.DeviceId);
        model.RefreshDevices();
        Assert.Null(Assert.Single(model.Devices).ProtocolInfo);
        Assert.Equal("软件通信协议：--", Assert.Single(model.Devices).ProtocolDisplayText);
    }

    [Fact]
    public async Task SelectingManagerDeviceDoesNotPromoteItToGlobalConnectedState()
    {
        var device = new SimulatedEggtCsDevice(
            new DeviceIdentity(new DeviceId("cached-device"), "Cached device", null, null)
        );
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var selection = new DeviceSelectionContext();
        var model = new DeviceConnectionPageViewModel(
            new StubNavigationRouter(),
            manager,
            selection,
            new DeviceConnectionNetworkContext(),
            new DialogService(() => null),
            new StubDialogProvider()
        );

        model.SelectDeviceCommand.Execute(Assert.Single(model.Devices));

        Assert.NotNull(model.SelectedDevice);
        Assert.False(selection.IsConnected);
        Assert.Empty(selection.SelectedDeviceId);
    }

    [Fact]
    public async Task FactoryResetRequiresConfirmationBeforeWritingConfiguration()
    {
        await using var manager = new DeviceManager();
        var reset = new StubConfigurationResetService();
        var model = CreateModel(manager, reset, new StubRestartService());

        var dialog = model.CreateFactoryResetConfirmationDialog();

        Assert.Equal(DialogKind.Risk, dialog.Kind);
        Assert.True(dialog.ShowCancelButton);
        Assert.Equal(0, reset.CallCount);
        dialog.Show();
        await dialog.CancelCommand.ExecuteAsync(null);
        Assert.False(dialog.Confirmed);
        Assert.Equal(0, reset.CallCount);
    }

    [Fact]
    public async Task ConfirmingFactoryResetCallsServiceOnce()
    {
        await using var manager = new DeviceManager();
        var reset = new StubConfigurationResetService();
        var model = CreateModel(manager, reset, new StubRestartService());
        var dialog = model.CreateFactoryResetConfirmationDialog();
        dialog.Show();

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.True(dialog.Confirmed);
        Assert.Equal(1, reset.CallCount);
    }

    [Fact]
    public async Task FactoryResetFailureKeepsConfirmationOpenForRetry()
    {
        await using var manager = new DeviceManager();
        var reset = new StubConfigurationResetService
        {
            Exception = new IOException("write failed"),
        };
        var model = CreateModel(manager, reset, new StubRestartService());
        var dialog = model.CreateFactoryResetConfirmationDialog();
        dialog.Show();

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.True(dialog.IsDialogOpen);
        Assert.False(dialog.Confirmed);
        Assert.Equal("重试恢复", dialog.ConfirmText);
        Assert.Contains("write failed", dialog.StatusText);
    }

    [Fact]
    public async Task RequiredRestartDialogCannotBeDismissedAndSupportsRetry()
    {
        await using var manager = new DeviceManager();
        var restart = new StubRestartService
        {
            Exception = new InvalidOperationException("start failed"),
        };
        var model = CreateModel(manager, new StubConfigurationResetService(), restart);
        var dialog = model.CreateRequiredRestartDialog();
        dialog.Show();

        Assert.False(dialog.ShowCancelButton);
        Assert.False(dialog.ShowCloseButton);
        Assert.False(dialog.AllowOverlayDismiss);
        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.True(dialog.IsDialogOpen);
        Assert.Equal("重试重启", dialog.ConfirmText);
        Assert.Contains("start failed", dialog.StatusText);
        restart.Exception = null;
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.True(dialog.Confirmed);
        Assert.Equal(2, restart.CallCount);
    }

    [Fact]
    public async Task SelectingFontSizeAppliesAndUpdatesSelection()
    {
        await using var manager = new DeviceManager();
        var fontScale = new StubFontScaleService();
        var model = CreateModel(
            manager,
            new StubConfigurationResetService(),
            new StubRestartService(),
            fontScale
        );

        await model.SetFontSizeCommand.ExecuteAsync(FontSizePreset.Large);

        Assert.Equal(FontSizePreset.Large, fontScale.CurrentPreset);
        Assert.Equal(FontSizePreset.Large, model.CurrentFontSizePreset);
        Assert.True(model.IsLargeFontSize);
        Assert.Equal("当前字号：115%", model.FontSizePercentText);
        Assert.Empty(model.FontSizeError);
    }

    [Fact]
    public async Task FontSizeFailureRestoresSelectionAndShowsError()
    {
        await using var manager = new DeviceManager();
        var fontScale = new StubFontScaleService { Exception = new IOException("write failed") };
        var model = CreateModel(
            manager,
            new StubConfigurationResetService(),
            new StubRestartService(),
            fontScale
        );

        await model.SetFontSizeCommand.ExecuteAsync(FontSizePreset.ExtraLarge);

        Assert.Equal(FontSizePreset.Standard, model.CurrentFontSizePreset);
        Assert.True(model.IsStandardFontSize);
        Assert.Contains("write failed", model.FontSizeError);
        Assert.False(model.IsFontSizeSaving);
    }

    private static DeviceConnectionPageViewModel CreateModel(
        IDeviceManager manager,
        IApplicationConfigurationResetService reset,
        IApplicationRestartService restart,
        IFontScaleService? fontScale = null
    ) =>
        new(
            new StubNavigationRouter(),
            manager,
            new DeviceSelectionContext(),
            new DeviceConnectionNetworkContext(),
            new DialogService(() => null),
            new StubDialogProvider(),
            reset,
            restart,
            fontScale
        );

    private sealed class StubDialogProvider : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private sealed class StubNavigationRouter : INavigationRouter
    {
        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData routeData) { }

        public void Navigate(StimulusConfigurationRouteData routeData) { }

        public void Navigate(ElectrodeConfigurationRouteData routeData) { }

        public void Navigate(ExperimentRunRouteData routeData) { }

        public void Navigate(ExperimentRerunRouteData routeData) { }

        public void GoBack() { }

        public void GoHome() { }
    }

    private sealed class StubConfigurationResetService : IApplicationConfigurationResetService
    {
        public int CallCount { get; private set; }
        public Exception? Exception { get; set; }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }

    private sealed class StubRestartService : IApplicationRestartService
    {
        public int CallCount { get; private set; }
        public Exception? Exception { get; set; }

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }

    private sealed class StubFontScaleService : IFontScaleService
    {
        public FontSizePreset CurrentPreset { get; private set; } = FontSizePreset.Standard;
        public Exception? Exception { get; set; }

        public void Initialize() { }

        public Task ApplyAsync(FontSizePreset preset, CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
                return Task.FromException(Exception);
            CurrentPreset = preset;
            return Task.CompletedTask;
        }
    }
}
