using System.ComponentModel;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Interfaces;

public interface IDeviceSelectionContext : INotifyPropertyChanged
{
    string SelectedDeviceId { get; }

    string SelectedDeviceName { get; }

    string SelectedDeviceIp { get; }

    string SelectedDeviceMacAddress { get; }

    DeviceConnectionState ConnectionState { get; }

    bool IsConnected { get; }

    string ConnectionStatusText { get; }

    int? BatteryPercent { get; }

    string BatteryPercentText { get; }

    string StatusMessage { get; set; }

    void SetConnectedDevice(
        string deviceId,
        string deviceName,
        string? deviceIp = null,
        string? macAddress = null,
        int? batteryPercent = null
    );

    void UpdateBattery(int? batteryPercent);

    void ClearConnection(string? statusMessage = null);
}
