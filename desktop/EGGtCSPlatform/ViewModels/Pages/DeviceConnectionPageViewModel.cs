using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public partial class DeviceConnectionPageViewModel : PageViewModel
{
    private readonly INavigationRouter _router;
    private readonly IDeviceManager _deviceManager;
    private readonly IDeviceSelectionContext _selection;
    private readonly IDeviceConnectionNetworkContext _networkContext;
    private readonly DialogService _dialogService;
    private readonly IDialogProvider _dialogHost;
    private readonly IDeviceConnectionCoordinator? _connectionCoordinator;
    private readonly IApplicationConfigurationResetService _configurationResetService;
    private readonly IApplicationRestartService _restartService;
    private readonly IFontScaleService _fontScaleService;

    [ObservableProperty]
    private DeviceConnectionItemViewModel? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSmallFontSize))]
    [NotifyPropertyChangedFor(nameof(IsStandardFontSize))]
    [NotifyPropertyChangedFor(nameof(IsLargeFontSize))]
    [NotifyPropertyChangedFor(nameof(IsExtraLargeFontSize))]
    [NotifyPropertyChangedFor(nameof(FontSizePercentText))]
    private FontSizePreset _currentFontSizePreset;

    [ObservableProperty]
    private bool _isFontSizeSaving;

    [ObservableProperty]
    private string _fontSizeError = string.Empty;

    public DeviceConnectionPageViewModel(
        INavigationRouter router,
        IDeviceManager deviceManager,
        IDeviceSelectionContext selection,
        IDeviceConnectionNetworkContext networkContext,
        IDeviceConnectionCoordinator? connectionCoordinator,
        DialogService dialogService,
        IDialogProvider dialogHost,
        IApplicationConfigurationResetService configurationResetService,
        IApplicationRestartService restartService,
        IFontScaleService? fontScaleService = null
    )
        : base(ApplicationPageNames.DeviceConnection, "设备连接")
    {
        _router = router;
        _deviceManager = deviceManager;
        _selection = selection;
        _networkContext = networkContext;
        _connectionCoordinator = connectionCoordinator;
        _dialogService = dialogService;
        _dialogHost = dialogHost;
        _configurationResetService = configurationResetService;
        _restartService = restartService;
        _fontScaleService = fontScaleService ?? UnavailableFontScaleService.Instance;
        _currentFontSizePreset = _fontScaleService.CurrentPreset;
        Devices = [];
        RefreshDevices();
    }

    public DeviceConnectionPageViewModel(
        INavigationRouter router,
        IDeviceManager deviceManager,
        IDeviceSelectionContext selection,
        IDeviceConnectionNetworkContext networkContext,
        DialogService dialogService,
        IDialogProvider dialogHost,
        IApplicationConfigurationResetService configurationResetService,
        IApplicationRestartService restartService,
        IFontScaleService? fontScaleService = null
    )
        : this(
            router,
            deviceManager,
            selection,
            networkContext,
            null,
            dialogService,
            dialogHost,
            configurationResetService,
            restartService,
            fontScaleService
        ) { }

    public DeviceConnectionPageViewModel(
        INavigationRouter router,
        IDeviceManager deviceManager,
        IDeviceSelectionContext selection,
        IDeviceConnectionNetworkContext networkContext,
        DialogService dialogService,
        IDialogProvider dialogHost
    )
        : this(
            router,
            deviceManager,
            selection,
            networkContext,
            dialogService,
            dialogHost,
            UnavailableConfigurationResetService.Instance,
            UnavailableRestartService.Instance
        ) { }

    public DeviceConnectionPageViewModel(
        INavigationRouter router,
        IDeviceManager deviceManager,
        IDeviceSelectionContext selection,
        DialogService dialogService,
        IDialogProvider dialogHost,
        IApplicationConfigurationResetService configurationResetService,
        IApplicationRestartService restartService
    )
        : this(
            router,
            deviceManager,
            selection,
            new DeviceConnectionNetworkContext(),
            null,
            dialogService,
            dialogHost,
            configurationResetService,
            restartService
        ) { }

    public DeviceConnectionPageViewModel(
        INavigationRouter router,
        IDeviceManager deviceManager,
        IDeviceSelectionContext selection,
        DialogService dialogService,
        IDialogProvider dialogHost
    )
        : this(
            router,
            deviceManager,
            selection,
            dialogService,
            dialogHost,
            UnavailableConfigurationResetService.Instance,
            UnavailableRestartService.Instance
        ) { }

    public ObservableCollection<DeviceConnectionItemViewModel> Devices { get; }

    public IDeviceSelectionContext Connection => _selection;

    public bool IsSmallFontSize => CurrentFontSizePreset == FontSizePreset.Small;

    public bool IsStandardFontSize => CurrentFontSizePreset == FontSizePreset.Standard;

    public bool IsLargeFontSize => CurrentFontSizePreset == FontSizePreset.Large;

    public bool IsExtraLargeFontSize => CurrentFontSizePreset == FontSizePreset.ExtraLarge;

    public string FontSizePercentText =>
        $"当前字号：{FontScaleService.GetScale(CurrentFontSizePreset):P0}";

    [RelayCommand]
    private async Task SetFontSizeAsync(FontSizePreset preset)
    {
        if (IsFontSizeSaving || preset == CurrentFontSizePreset)
            return;

        var previous = CurrentFontSizePreset;
        FontSizeError = string.Empty;
        IsFontSizeSaving = true;
        CurrentFontSizePreset = preset;
        try
        {
            await _fontScaleService.ApplyAsync(preset);
        }
        catch (Exception exception)
        {
            CurrentFontSizePreset = previous;
            FontSizeError = $"字号设置保存失败：{exception.Message}";
        }
        finally
        {
            IsFontSizeSaving = false;
        }
    }

    [RelayCommand]
    private async Task OpenConfigurationAsync()
    {
        var dialog = _connectionCoordinator is null
            ? new DeviceConnectionDialogViewModel(_deviceManager, _selection, _networkContext)
            : new DeviceConnectionDialogViewModel(
                _connectionCoordinator,
                _selection,
                _networkContext
            );
        await _dialogService.ShowDialog(_dialogHost, dialog);
        RefreshDevices();
    }

    [RelayCommand]
    private void SelectDevice(DeviceConnectionItemViewModel item)
    {
        SelectedDevice = item;
    }

    [RelayCommand]
    private void OpenPhysicalChannelMapping() =>
        _router.Navigate(ApplicationPageNames.PhysicalChannelMapping);

    [RelayCommand]
    private async Task RestoreFactorySettingsAsync()
    {
        var confirmation = CreateFactoryResetConfirmationDialog();
        await _dialogService.ShowDialog(_dialogHost, confirmation);
        if (!confirmation.Confirmed)
            return;

        await _dialogService.ShowDialog(_dialogHost, CreateRequiredRestartDialog());
    }

    internal ConfirmDialogViewModel CreateFactoryResetConfirmationDialog()
    {
        var confirmation = new ConfirmDialogViewModel(DialogKind.Risk)
        {
            Title = "确认恢复出厂设置",
            Message =
                "这将清除当前用户的全部应用配置并恢复当前版本默认值。实验数据、记录文件和日志不会被删除。",
            ConfirmText = "确认恢复",
            CancelText = "取消",
            ShowCancelButton = true,
        };
        confirmation.OnConfirm = async dialog =>
        {
            try
            {
                await _configurationResetService.ResetAsync();
                return true;
            }
            catch (Exception exception)
            {
                dialog.ConfirmText = "重试恢复";
                dialog.StatusText = $"恢复失败：{exception.Message}";
                dialog.ProgressText = string.Empty;
                return false;
            }
        };
        return confirmation;
    }

    internal ConfirmDialogViewModel CreateRequiredRestartDialog()
    {
        var restart = new ConfirmDialogViewModel(DialogKind.Success)
        {
            Title = "配置已恢复",
            Message = "默认配置已完整恢复，必须立即重启程序后才能继续使用。",
            ConfirmText = "立即重启",
            ShowCancelButton = false,
            ShowCloseButton = false,
        };
        restart.OnConfirm = async dialog =>
        {
            try
            {
                await _restartService.RestartAsync();
                return true;
            }
            catch (Exception exception)
            {
                dialog.ConfirmText = "重试重启";
                dialog.StatusText = $"重启失败：{exception.Message}";
                dialog.ProgressText = string.Empty;
                return false;
            }
        };
        return restart;
    }

    internal void RefreshDevices()
    {
        Devices.Clear();
        foreach (var device in _deviceManager.Devices.OrderBy(item => item.Identity.Model))
        {
            Devices.Add(
                new DeviceConnectionItemViewModel(
                    device.Identity.DeviceId.Value,
                    device.Identity.Model,
                    device.Identity.SerialNumber,
                    device.Identity.DeviceId.Value == _selection.SelectedDeviceId
                        ? _selection.SelectedDeviceIp
                        : string.Empty,
                    device.Identity.MacAddress,
                    device.State.Connection == DeviceConnectionState.Connected
                        ? "已连接"
                        : "未连接",
                    device.Identity.DeviceId.Value == _selection.SelectedDeviceId,
                    (device as IDeviceProtocolBinding)?.ProtocolInfo
                        ?? (
                            device is EGGtCSPlatform.DeviceSdk.Simulation.SimulatedEggtCsDevice
                                ? new DeviceProtocolInfo(
                                    "simulator",
                                    "simulator",
                                    ProtocolRevisionSource.Unspecified
                                )
                                : null
                        )
                )
            );
        }
        SelectedDevice = Devices.FirstOrDefault(item =>
            item.DeviceId == _selection.SelectedDeviceId
        );
    }

    partial void OnSelectedDeviceChanged(DeviceConnectionItemViewModel? value)
    {
        if (value is null)
            return;
        foreach (var device in Devices)
            device.IsSelected = ReferenceEquals(device, value);
    }

    private sealed class UnavailableConfigurationResetService
        : IApplicationConfigurationResetService
    {
        public static UnavailableConfigurationResetService Instance { get; } = new();

        public Task ResetAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("当前宿主未配置恢复出厂设置服务。"));
    }

    private sealed class UnavailableRestartService : IApplicationRestartService
    {
        public static UnavailableRestartService Instance { get; } = new();

        public Task RestartAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("当前宿主未配置程序重启服务。"));
    }

    private sealed class UnavailableFontScaleService : IFontScaleService
    {
        public static UnavailableFontScaleService Instance { get; } = new();

        public FontSizePreset CurrentPreset => FontSizePreset.Standard;

        public void Initialize() { }

        public Task ApplyAsync(
            FontSizePreset preset,
            System.Threading.CancellationToken cancellationToken = default
        ) => Task.CompletedTask;
    }
}

public partial class DeviceConnectionItemViewModel(
    string deviceId,
    string displayName,
    string? serialNumber,
    string? ipAddress,
    string? macAddress,
    string connectionState,
    bool isSelected,
    DeviceProtocolInfo? protocolInfo = null
) : ObservableObject
{
    public string DeviceId { get; } = deviceId;

    public string DisplayName { get; } = displayName;

    public string SerialNumber { get; } = serialNumber ?? "--";

    public string IpAddress { get; } = string.IsNullOrWhiteSpace(ipAddress) ? "--" : ipAddress;

    public string MacAddress { get; } = string.IsNullOrWhiteSpace(macAddress) ? "--" : macAddress;

    public string ConnectionState { get; } = connectionState;

    public DeviceProtocolInfo? ProtocolInfo { get; } =
        connectionState == "已连接" ? protocolInfo : null;

    public string ProtocolDisplayText =>
        "软件通信协议："
        + (
            ConnectionState != "已连接"
                ? "--"
                : ProtocolInfo switch
                {
                    { ProtocolId: "simulator" } => "模拟通信",
                    { ProtocolId: "eggtcs.gen1" } info => $"EGG/tCS Gen1 · {info.ActiveRevision}",
                    { } info => $"{info.ProtocolId} · {info.ActiveRevision}",
                    _ => "未提供",
                }
        );

    public string ProtocolExplanation => "表示软件采用的协议定义；设备未提供协议版本上报。";

    [ObservableProperty]
    private bool _isSelected = isSelected;
}
