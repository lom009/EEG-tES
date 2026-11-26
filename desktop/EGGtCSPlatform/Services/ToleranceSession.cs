using System;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Services;

public interface IToleranceSession : IDisposable
{
    double Current { get; }
    double Minimum { get; }
    double Maximum { get; }
    double Step { get; }
    StimulusConfigurationSnapshot Configuration { get; }
    bool IsSimulated { get; }
    bool IsRunning { get; }
    bool HasConfirmedCurrent { get; }
    string Status { get; }
    event Action? Changed;
    Task StartAsync();
    Task AdjustAsync(int direction);
    Task ResetAsync();
    Task StopAsync();
}

public interface IToleranceSessionFactory
{
    IToleranceSession Create(ToleranceTestRouteData route);
}

public sealed class ToleranceRunOptions
{
    public const string SectionName = "ToleranceRun";
    public int DurationSeconds { get; set; } = 600;
}

public sealed class ToleranceSessionFactory(
    StimulusCapabilityProfile capability,
    DeviceBackendOptions? backend = null,
    IDeviceSelectionContext? selection = null,
    IDeviceManager? devices = null,
    IOptions<ToleranceRunOptions>? options = null,
    IDeviceHeartbeatPauseService? heartbeat = null,
    ToleranceSessionLifetime? lifetime = null
) : IToleranceSessionFactory
{
    public IToleranceSession Create(ToleranceTestRouteData route)
    {
        var source =
            backend?.GetSource(DeviceCapabilityKind.Tolerance) ?? DeviceCapabilitySource.Simulated;
        if (source == DeviceCapabilitySource.Simulated)
            return new ToleranceSimulationSession(route, capability);
        if (source == DeviceCapabilitySource.Disabled)
            throw new InvalidOperationException("耐受度测试能力已禁用。");
        if (backend?.GetSource(DeviceCapabilityKind.Stimulation) != DeviceCapabilitySource.Real)
            throw new InvalidOperationException("真实耐受度测试要求刺激能力也设置为真实模式。");
        if (
            selection?.IsConnected != true
            || devices is null
            || !devices.TryGet(new DeviceId(selection.SelectedDeviceId), out var device)
            || device is null
            || device.State.Connection != DeviceConnectionState.Connected
        )
            throw new InvalidOperationException("尚未连接设备，无法启动真实耐受度测试。");
        if (
            !device.UsesCapabilitySource(
                DeviceCapabilityKind.Tolerance,
                DeviceCapabilitySource.Real
            )
            || !device.UsesCapabilitySource(
                DeviceCapabilityKind.Stimulation,
                DeviceCapabilitySource.Real
            )
        )
            throw new InvalidOperationException("当前设备的耐受度或刺激能力不是实际通信模式。");
        return new DeviceToleranceSession(
            route,
            capability,
            device,
            options?.Value ?? new ToleranceRunOptions(),
            heartbeat,
            lifetime
        );
    }
}

/// <summary>In-memory simulation never accesses a device.</summary>
public sealed class ToleranceSimulationSession(
    ToleranceTestRouteData route,
    StimulusCapabilityProfile capability
) : ToleranceCurrentModel(route, capability), IToleranceSession
{
    public bool IsSimulated => true;
    public bool IsRunning { get; private set; } = true;
    public bool HasConfirmedCurrent => true;
    public string Status => "";
    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task StartAsync() => Task.CompletedTask;

    public Task AdjustAsync(int direction)
    {
        Adjust(direction);
        return Task.CompletedTask;
    }

    public Task ResetAsync()
    {
        Reset();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }
}
