using System;
using CommunityToolkit.Mvvm.ComponentModel;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;

namespace EGGtCSPlatform.Services;

public partial class DeviceSelectionContext : ObservableObject, IDeviceSelectionContext
{
    public const string DefaultDisconnectedMessage = "尚未连接设备，请进入配置完成连接";

    [ObservableProperty]
    private string _selectedDeviceId = string.Empty;

    [ObservableProperty]
    private string _selectedDeviceName = string.Empty;

    [ObservableProperty]
    private string _selectedDeviceIp = string.Empty;

    [ObservableProperty]
    private string _selectedDeviceMacAddress = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;

    [ObservableProperty]
    private string _statusMessage = DefaultDisconnectedMessage;

    private int? _batteryPercent;

    public bool IsConnected => ConnectionState == DeviceConnectionState.Connected;

    public string ConnectionStatusText => IsConnected ? "已连接" : "未连接";

    public int? BatteryPercent
    {
        get => _batteryPercent;
        private set
        {
            if (!SetProperty(ref _batteryPercent, value))
                return;
            OnPropertyChanged(nameof(BatteryPercentText));
        }
    }

    public string BatteryPercentText => BatteryPercent is { } value ? $"{value}%" : "--%";

    public void SetConnectedDevice(
        string deviceId,
        string deviceName,
        string? deviceIp = null,
        string? macAddress = null,
        int? batteryPercent = null
    )
    {
        SelectedDeviceId = deviceId;
        SelectedDeviceName = deviceName;
        SelectedDeviceIp = deviceIp ?? string.Empty;
        SelectedDeviceMacAddress = macAddress ?? string.Empty;
        UpdateBattery(batteryPercent);
        ConnectionState = DeviceConnectionState.Connected;
        StatusMessage = $"当前设备：{deviceName}";
    }

    public void UpdateBattery(int? batteryPercent)
    {
        BatteryPercent = batteryPercent is { } value ? Math.Clamp(value, 0, 100) : null;
    }

    public void ClearConnection(string? statusMessage = null)
    {
        SelectedDeviceId = string.Empty;
        SelectedDeviceName = string.Empty;
        SelectedDeviceIp = string.Empty;
        SelectedDeviceMacAddress = string.Empty;
        UpdateBattery(null);
        ConnectionState = DeviceConnectionState.Disconnected;
        StatusMessage = string.IsNullOrWhiteSpace(statusMessage)
            ? DefaultDisconnectedMessage
            : statusMessage;
    }
}
