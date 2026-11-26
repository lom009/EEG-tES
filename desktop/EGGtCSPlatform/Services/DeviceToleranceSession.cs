using System;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

/// <summary>A page-owned, single-device session. Relative adjustments are never retried.</summary>
public sealed class DeviceToleranceSession : IToleranceSession
{
    private readonly ToleranceTestRouteData _route;
    private readonly ToleranceCurrentModel _model;
    private readonly IEggtCsDevice _device;
    private readonly TimeSpan _duration;
    private readonly IDeviceHeartbeatPauseService? _heartbeat;
    private readonly ToleranceSessionLifetime? _owner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _work = new();
    private readonly CancellationTokenSource _lifetime = new();
    private IDisposable? _pause;
    private bool _started;
    private volatile bool _ending;
    private volatile bool _needsStop;
    private bool _disposed;

    public DeviceToleranceSession(
        ToleranceTestRouteData route,
        StimulusCapabilityProfile capability,
        IEggtCsDevice device,
        ToleranceRunOptions options,
        IDeviceHeartbeatPauseService? heartbeat = null,
        ToleranceSessionLifetime? lifetime = null
    )
    {
        if (options.DurationSeconds is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "耐受度测试时长必须为 1～65535 整数秒。"
            );
        if (device.Stimulation is null || device.Tolerance is null)
            throw new InvalidOperationException("设备不支持刺激或耐受度测试。");
        _route = route;
        _model = new ToleranceCurrentModel(route, capability, protocolStep: true);
        _device = device;
        _duration = TimeSpan.FromSeconds(options.DurationSeconds);
        _heartbeat = heartbeat;
        _owner = lifetime;
        _owner?.Register(this);
    }

    public double Current => _model.Current;
    public double Minimum => _model.Minimum;
    public double Maximum => _model.Maximum;
    public double Step => .04;
    public StimulusConfigurationSnapshot Configuration => _model.Configuration;
    public bool IsSimulated => false;
    public bool IsRunning { get; private set; }
    public bool HasConfirmedCurrent { get; private set; }
    public string Status { get; private set; } = "尚未启动，等待设备确认零电流";
    public event Action? Changed;

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_started || _ending || _disposed)
                return;
            _started = true;
            if (
                _device.State.Connection != DeviceConnectionState.Connected
                || _device.State.Operation != DeviceOperationState.Ready
            )
                throw new InvalidOperationException("设备未就绪，无法启动耐受度测试。");
            _pause = _heartbeat?.Pause(_device.Identity.DeviceId);
            var subscription = _device.SubscribeEvents();
            _ = WatchAsync(subscription);
            await subscription.Ready.WaitAsync(_work.Token);
            var configuration = DeviceStimulationConfigurationMapper.Create(
                _device,
                ExperimentStimulusConfigurationSnapshot.From(
                    _model.MaximumConfiguration,
                    _route.Electrodes
                ),
                _route.Electrodes
            );
            var configured = await _device.Stimulation!.ConfigureAsync(configuration);
            if (!configured.IsSuccess)
                throw new InvalidOperationException($"配置刺激失败：{configured.Status}");
            _work.Token.ThrowIfCancellationRequested();
            // Set before sending: an absent acknowledgement does not prove the device stayed stopped.
            _needsStop = true;
            var result = await _device.Tolerance!.StartAsync();
            if (!result.IsSuccess)
                throw new InvalidOperationException($"启动刺激失败：{result.Status}");
            _work.Token.ThrowIfCancellationRequested();
            Status = "刺激已启动，等待设备确认零电流";
            var initial = await _device.Tolerance.AdjustCurrentAsync(CurrentAdjustment.Decrease);
            _work.Token.ThrowIfCancellationRequested();
            if (
                initial.Status
                    is not (DeviceCommandStatus.Success or DeviceCommandStatus.MinimumReached)
                || initial.CurrentMilliAmps != 0m
            )
                throw new InvalidOperationException(
                    $"设备未确认零电流：{initial.Status}，{initial.CurrentMilliAmps:0.00} mA"
                );
            _model.SetCurrent(0);
            HasConfirmedCurrent = true;
            IsRunning = true;
            Status = "刺激已启动，设备已确认电流为 0.00 mA";
            _ = FinishAtDeadlineAsync();
        }
        catch (Exception exception)
        {
            _ending = true;
            IsRunning = false;
            Status = $"启动失败：{exception.Message}";
            try
            {
                await StopDeviceAsync();
            }
            catch (Exception stopError)
            {
                Status += $"；停止失败：{stopError.Message}，请重试退出";
            }
            if (!_needsStop)
                ReleaseMonitoring();
            throw new InvalidOperationException(Status, exception);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task AdjustAsync(int direction)
    {
        if (direction is not (1 or -1))
            throw new ArgumentOutOfRangeException(nameof(direction));
        await _gate.WaitAsync(_work.Token);
        try
        {
            await AdjustCoreAsync(direction);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private async Task<bool> AdjustCoreAsync(int direction)
    {
        _work.Token.ThrowIfCancellationRequested();
        if (!IsRunning || _ending)
            throw new InvalidOperationException("耐受度测试未运行。");
        if (direction > 0 ? Current >= Maximum : Current <= Minimum)
            return false;
        var response = await _device.Tolerance!.AdjustCurrentAsync(
            direction > 0 ? CurrentAdjustment.Increase : CurrentAdjustment.Decrease
        );
        _work.Token.ThrowIfCancellationRequested();
        if (_ending)
            return false;
        Status = response.Status switch
        {
            DeviceCommandStatus.Success => "电流已由设备确认",
            DeviceCommandStatus.InvalidParameter => "设备拒绝调流：数据无效",
            DeviceCommandStatus.MaximumReached => "电流已达到设备最大值",
            DeviceCommandStatus.MinimumReached => "电流已达到设备最小值",
            DeviceCommandStatus.NotConfigured => "设备未启动，请退出后重新进入测试",
            _ => "设备返回未知调流状态",
        };
        if (response.Status == DeviceCommandStatus.NotConfigured)
            IsRunning = false;
        if (
            response.Status
            is not (
                DeviceCommandStatus.Success
                or DeviceCommandStatus.MaximumReached
                or DeviceCommandStatus.MinimumReached
            )
        )
            throw new InvalidOperationException(Status);
        var before = Current;
        _model.SetCurrent((double)response.CurrentMilliAmps);
        HasConfirmedCurrent = true;
        Changed?.Invoke();
        return response.Status == DeviceCommandStatus.Success
            && (direction > 0 ? Current > before : Current < before);
    }

    public async Task ResetAsync()
    {
        await _gate.WaitAsync(_work.Token);
        try
        {
            while (Current > Minimum)
            {
                if (!await AdjustCoreAsync(-1))
                {
                    if (Current > Minimum && Status == "电流已由设备确认")
                        Status = "设备电流未继续下降，已停止重置";
                    break;
                }
            }
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        _ending = true;
        IsRunning = false;
        _work.Cancel();
        Changed?.Invoke();
        // Let any bounded SDK request finish before sending stop; discard its late result.
        await _gate.WaitAsync();
        try
        {
            await StopDeviceAsync();
            Status = "耐受度测试已结束";
            ReleaseMonitoring();
        }
        catch (Exception exception)
        {
            Status = $"停止失败：{exception.Message}，请重试退出";
            throw;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private async Task StopDeviceAsync()
    {
        if (!_needsStop)
            return;
        using var pause = _heartbeat?.Pause(_device.Identity.DeviceId);
        var result = await _device.Stimulation!.StopAsync();
        if (!result.IsSuccess && result.Status != DeviceCommandStatus.AlreadyStopped)
            throw new InvalidOperationException($"设备未确认停止：{result.Status}");
        _needsStop = false;
    }

    private async Task FinishAtDeadlineAsync()
    {
        try
        {
            await Task.Delay(_duration, _lifetime.Token);
            await StopAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Changed?.Invoke();
        } // StopAsync exposes the failure for retry on exit.
    }

    private async Task WatchAsync(DeviceEventSubscription subscription)
    {
        try
        {
            await foreach (var item in subscription.ReadEventsAsync(_lifetime.Token))
            {
                if (item.Event is StimulationCompletedEvent)
                {
                    EndFromDevice("设备已完成刺激");
                    return;
                }
                if (
                    item.Event is DeviceStateChangedEvent fault
                    && fault.State.Operation == DeviceOperationState.Faulted
                )
                    throw new InvalidOperationException(fault.State.Fault ?? "设备发生故障");
                if (
                    item.Event is DeviceStateChangedEvent state
                    && state.State.Connection != DeviceConnectionState.Connected
                )
                {
                    // Keep stop obligation: a lost connection is not confirmation of physical stop.
                    _ending = true;
                    IsRunning = false;
                    _work.Cancel();
                    Status = "设备连接已断开，无法确认停止；请恢复连接后处理";
                    ReleaseMonitoring();
                    Changed?.Invoke();
                    return;
                }
            }
            if (!_lifetime.IsCancellationRequested)
                throw new InvalidOperationException("设备事件流已结束");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Status = $"设备事件接收失败：{exception.Message}";
            try
            {
                await StopAsync();
            }
            catch (Exception) { }
            Changed?.Invoke();
        }
        finally
        {
            await subscription.DisposeAsync();
        }
    }

    private void EndFromDevice(string status)
    {
        _ending = true;
        IsRunning = false;
        _needsStop = false;
        _work.Cancel();
        Status = status;
        ReleaseMonitoring();
        Changed?.Invoke();
    }

    private void ReleaseMonitoring()
    {
        _lifetime.Cancel();
        Interlocked.Exchange(ref _pause, null)?.Dispose();
        if (!_needsStop)
            _owner?.Unregister(this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ = DisposeAsync();
    }

    private async Task DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch (Exception) { }
        finally
        {
            ReleaseMonitoring();
            _model.Dispose();
        }
    }
}
