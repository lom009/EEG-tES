using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels;

public partial class DeviceConnectionDialogViewModel : DialogViewModel
{
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(3);
    private const string DefaultDevicePort = "30307";
    private readonly IDeviceManager? _deviceManager;
    private readonly IDeviceConnectionCoordinator? _coordinator;
    private readonly IDeviceSelectionContext _connection;
    private readonly IDeviceConnectionNetworkContext _networkContext;
    private string _deviceIp = string.Empty;

    [ObservableProperty]
    private string _devicePort = DefaultDevicePort;

    private readonly string _localPort;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private DeviceConnectionCandidateViewModel? _selectedCandidate;

    public ObservableCollection<DeviceConnectionCandidateViewModel> DiscoveredDevices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _statusColor = "#6B7D99";

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(ConnectionStateText))]
    private DeviceConnectionState _connectionState;

    public DeviceConnectionDialogViewModel(
        IDeviceManager deviceManager,
        IDeviceSelectionContext connection,
        IDeviceConnectionNetworkContext networkContext,
        Func<int>? localPortProvider = null
    )
    {
        _deviceManager = deviceManager;
        _connection = connection;
        _networkContext = networkContext;
        _connectionState = connection.ConnectionState;
        _localPort = (localPortProvider?.Invoke() ?? FindAvailableUdpPort()).ToString(
            CultureInfo.InvariantCulture
        );
    }

    public DeviceConnectionDialogViewModel(
        IDeviceConnectionCoordinator coordinator,
        IDeviceSelectionContext connection,
        IDeviceConnectionNetworkContext networkContext
    )
    {
        _coordinator = coordinator;
        _connection = connection;
        _networkContext = networkContext;
        _connectionState = connection.ConnectionState;
        var network = networkContext.Snapshot;
        _localPort = network.LocalPort.ToString(CultureInfo.InvariantCulture);
        _devicePort = network.DevicePort.ToString(CultureInfo.InvariantCulture);
        coordinator.StateChanged += OnCoordinatorStateChanged;
        coordinator.EnterManualMode();
        ApplyCoordinatorSnapshot(coordinator.Snapshot);
    }

    public DeviceConnectionDialogViewModel(
        IDeviceManager deviceManager,
        IDeviceSelectionContext connection,
        Func<int>? localPortProvider = null
    )
        : this(
            deviceManager,
            connection,
            new EGGtCSPlatform.Services.DeviceConnectionNetworkContext(),
            localPortProvider
        ) { }

    public string DeviceIp => _deviceIp;

    public string LocalPort => _localPort;

    public bool CanInteract => !IsBusy;

    public bool CanConnect => CanInteract && SelectedCandidate is not null;

    public bool IsConnected => ConnectionState == DeviceConnectionState.Connected;

    public bool IsDisconnected => !IsConnected;

    public string ConnectionStateText => IsConnected ? "已连接" : "未连接";

    public override bool AllowOverlayDismiss => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task SearchAsync()
    {
        if (IsBusy)
            return;

        if (_coordinator is not null)
        {
            await _coordinator.SearchManualAsync();
            return;
        }

        if (!TryValidatePort(DevicePort))
        {
            StatusColor = "#E91919";
            StatusText = "设备端口必须是 1～65535 的整数";
            return;
        }
        if (!TryValidatePort(LocalPort))
        {
            StatusColor = "#E91919";
            StatusText = "本机端口必须是 1～65535 的整数";
            return;
        }

        _networkContext.Configure(
            int.Parse(DevicePort, CultureInfo.InvariantCulture),
            int.Parse(LocalPort, CultureInfo.InvariantCulture)
        );

        IsBusy = true;
        BusyText = "正在搜索设备…";
        StatusText = string.Empty;
        StatusColor = "#6B7D99";
        try
        {
            var selectedDeviceId = SelectedCandidate?.DeviceId ?? _connection.SelectedDeviceId;
            var candidates = await DiscoverCandidatesAsync();
            DiscoveredDevices.Clear();
            foreach (var candidate in candidates)
                DiscoveredDevices.Add(new DeviceConnectionCandidateViewModel(candidate));
            SelectedCandidate =
                DiscoveredDevices.FirstOrDefault(item =>
                    string.Equals(
                        item.DeviceId,
                        selectedDeviceId,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) ?? DiscoveredDevices.FirstOrDefault();
            StatusColor = SelectedCandidate is null ? "#E91919" : "#5A4BD8";
            StatusText = SelectedCandidate is null
                ? "未发现可连接设备，请检查配置后重试"
                : $"已发现 {DiscoveredDevices.Count} 台设备，MAC：{SelectedCandidate.MacAddress}";
        }
        catch (Exception exception)
        {
            DiscoveredDevices.Clear();
            SelectedCandidate = null;
            StatusColor = "#E91919";
            StatusText = $"搜索失败：{exception.Message}";
        }
        finally
        {
            BusyText = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsBusy)
            return;

        if (!TryValidateSettings(out var validationMessage))
        {
            StatusColor = "#E91919";
            StatusText = validationMessage;
            return;
        }

        if (_coordinator is not null)
        {
            var selected =
                SelectedCandidate?.Candidate
                ?? throw new InvalidOperationException("请先搜索并选择设备");
            var candidate = selected with
            {
                Endpoint = selected.Endpoint with
                {
                    Address = DeviceIp,
                    Port = int.Parse(DevicePort, CultureInfo.InvariantCulture),
                },
            };
            _networkContext.Configure(
                candidate.Endpoint.Port,
                int.Parse(LocalPort, CultureInfo.InvariantCulture)
            );
            if (await _coordinator.ConnectManualAsync(candidate))
            {
                ConnectionState = DeviceConnectionState.Connected;
                CloseManualMode();
                Close();
            }
            return;
        }

        IsBusy = true;
        BusyText = "正在连接并验证设备…";
        StatusText = string.Empty;
        StatusColor = "#6B7D99";
        IEggtCsDevice? connectedDevice = null;
        try
        {
            var previousDeviceId = _connection.SelectedDeviceId;
            if (!string.IsNullOrWhiteSpace(previousDeviceId))
                await _deviceManager!.RemoveAsync(new DeviceId(previousDeviceId));
            _connection.ClearConnection("正在连接设备…");
            ConnectionState = DeviceConnectionState.Connecting;

            var selectedCandidate =
                SelectedCandidate?.Candidate
                ?? throw new InvalidOperationException("请先搜索并选择设备");
            _networkContext.Configure(
                int.Parse(DevicePort, CultureInfo.InvariantCulture),
                int.Parse(LocalPort, CultureInfo.InvariantCulture)
            );
            var candidate = selectedCandidate with
            {
                Endpoint = selectedCandidate.Endpoint with
                {
                    Address = DeviceIp,
                    Port = int.Parse(DevicePort, CultureInfo.InvariantCulture),
                },
            };
            if (_deviceManager!.TryGet(candidate.Identity.DeviceId, out _))
                await _deviceManager.RemoveAsync(candidate.Identity.DeviceId);
            connectedDevice = await _deviceManager.ConnectAsync(candidate);
            var status = await connectedDevice.ReadStatusAsync();
            if (
                status.Status != DeviceCommandStatus.Success
                || connectedDevice.State.Connection != DeviceConnectionState.Connected
            )
            {
                throw new InvalidOperationException($"设备状态验证失败：{status.Status}");
            }

            _connection.SetConnectedDevice(
                connectedDevice.Identity.DeviceId.Value,
                connectedDevice.Identity.Model,
                candidate.Endpoint.Address,
                connectedDevice.Identity.MacAddress,
                status.BatteryPercent
            );
            ConnectionState = DeviceConnectionState.Connected;
            StatusColor = "#258A5B";
            StatusText = $"已连接：{connectedDevice.Identity.Model}";
            Close();
        }
        catch (Exception exception)
        {
            if (connectedDevice is not null)
                await TryRemoveFailedDeviceAsync(connectedDevice.Identity.DeviceId);
            _connection.ClearConnection("设备连接失败，请重新进入配置连接");
            ConnectionState = DeviceConnectionState.Faulted;
            StatusColor = "#E91919";
            StatusText = $"连接失败：{exception.Message}";
        }
        finally
        {
            BusyText = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Cancel()
    {
        CloseManualMode();
        Close();
    }

    protected override void OnOverlayDismiss()
    {
        CloseManualMode();
        base.OnOverlayDismiss();
    }

    partial void OnSelectedCandidateChanged(DeviceConnectionCandidateViewModel? value)
    {
        if (value is null)
            return;
        ApplyCandidateEndpoint(value.Candidate);
        StatusColor = "#5A4BD8";
        StatusText = $"MAC：{value.MacAddress}";
    }

    private async Task<IReadOnlyList<DeviceCandidate>> DiscoverCandidatesAsync()
    {
        var result = new List<DeviceCandidate>();
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var candidate in _deviceManager!.DiscoverAsync(DiscoveryWindow))
        {
            if (deviceIds.Add(candidate.Identity.DeviceId.Value))
                result.Add(candidate);
        }
        return result;
    }

    private void ApplyCandidateEndpoint(DeviceCandidate candidate)
    {
        SetProperty(ref _deviceIp, candidate.Endpoint.Address, nameof(DeviceIp));
        if (candidate.Endpoint.Port is >= 1 and <= ushort.MaxValue)
            DevicePort = candidate.Endpoint.Port.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryValidateSettings(out string message)
    {
        if (!TryValidateIpv4(DeviceIp))
        {
            message = "设备 IP 必须是有效的 IPv4 地址";
            return false;
        }
        if (!TryValidatePort(DevicePort))
        {
            message = "设备端口必须是 1～65535 的整数";
            return false;
        }
        if (!TryValidatePort(LocalPort))
        {
            message = "本机端口必须是 1～65535 的整数";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryValidateIpv4(string value) =>
        IPAddress.TryParse(value, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork;

    private static bool TryValidatePort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is >= 1 and <= ushort.MaxValue;

    private static int FindAvailableUdpPort()
    {
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private async Task TryRemoveFailedDeviceAsync(DeviceId deviceId)
    {
        try
        {
            await _deviceManager!.RemoveAsync(deviceId);
        }
        catch
        {
            // Preserve the original connection failure in the dialog.
        }
    }

    private void OnCoordinatorStateChanged(
        object? sender,
        DeviceConnectionCoordinatorSnapshot snapshot
    )
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyCoordinatorSnapshot(snapshot);
        else
            Dispatcher.UIThread.Post(() => ApplyCoordinatorSnapshot(snapshot));
    }

    private void ApplyCoordinatorSnapshot(DeviceConnectionCoordinatorSnapshot snapshot)
    {
        IsBusy = snapshot.IsBusy;
        BusyText = snapshot.BusyText;
        StatusText = snapshot.StatusText;
        StatusColor =
            snapshot.Stage == DeviceConnectionAutomationStage.Connected ? "#258A5B"
            : snapshot.Candidates.Count > 0 ? "#5A4BD8"
            : "#6B7D99";
        var selectedId = SelectedCandidate?.DeviceId ?? _connection.SelectedDeviceId;
        DiscoveredDevices.Clear();
        foreach (var candidate in snapshot.Candidates)
            DiscoveredDevices.Add(new DeviceConnectionCandidateViewModel(candidate));
        SelectedCandidate =
            DiscoveredDevices.FirstOrDefault(item =>
                string.Equals(item.DeviceId, selectedId, StringComparison.OrdinalIgnoreCase)
            ) ?? DiscoveredDevices.FirstOrDefault();
        ConnectionState = _connection.ConnectionState;
    }

    private void CloseManualMode()
    {
        if (_coordinator is null)
            return;
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.ExitManualMode();
    }
}

public sealed class DeviceConnectionCandidateViewModel
{
    internal DeviceConnectionCandidateViewModel(DeviceCandidate candidate) => Candidate = candidate;

    internal DeviceCandidate Candidate { get; }

    public string DeviceId => Candidate.Identity.DeviceId.Value;

    public string DisplayName => Candidate.Identity.Model;

    public string IpAddress => Candidate.Endpoint.Address;

    public string MacAddress =>
        string.IsNullOrWhiteSpace(Candidate.Identity.MacAddress)
            ? "--"
            : Candidate.Identity.MacAddress;

    public string DisplayText => $"{Candidate.Identity.Model}  ·  {IpAddress}";
}
