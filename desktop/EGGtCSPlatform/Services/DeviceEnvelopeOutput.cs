using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.Services;

public sealed class DeviceEnvelopeOutput(
    IDeviceManager manager,
    string deviceId,
    IDeviceHeartbeatPauseService? heartbeat = null,
    TimeSpan? stopTimeout = null,
    TimeProvider? timeProvider = null,
    StimulationCurrentPolicy? currentPolicy = null
) : ISpeechStimulationOutput
{
    private readonly SemaphoreSlim _gate = new(1);

    // Keep uncertainty across page recreation; reconnect must replace the faulted device instance.
    private static readonly ConditionalWeakTable<
        IDeviceManager,
        ConcurrentDictionary<string, IEggtCsDevice>
    > Faults = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private IEggtCsDevice? _device;
    private EnvelopePlaybackPlan? _plan;
    private IDisposable? _pause;
    private CancellationTokenSource? _watchCancellation;
    private CancellationTokenSource? _faultCancellation;
    private Task _watch = Task.CompletedTask;
    private bool _mayBeRunning;
    private bool _prepared;
    private bool _uncertain;
    private long _started;
    public string? Status { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler<string>? Faulted;

    public static EnvelopePlaybackSettings Settings(ExperimentRunRouteData route)
    {
        var source = route.StimulusConfiguration;
        var target = source.Targets.Single();
        var assignments = route.StimulusElectrodes;
        var ids = assignments.Select(a => a.PhysicalChannelId).ToArray();
        if (
            ids.Length != 2
            || ids.Distinct().Count() != 2
            || ids.Any(id => id is < 1 or > 8)
            || assignments.Any(a => a.TargetId != target.TargetId)
            || target.Channels.Count != 2
            || target.Channels.Any(c =>
                !assignments.Any(a =>
                    a.PhysicalChannelId == c.PhysicalChannelId && a.Role == c.Role
                )
            )
        )
            throw new ArgumentException("包络刺激需要两个与配置一致的物理通道。");
        var setup = source.Envelope ?? throw new ArgumentException("缺少包络配置。");
        if (
            !setup.IsDeviceSetup
            || setup.DebugFrequencyHz is < 200 or > 1000
            || assignments.Count(a => a.Role == ViewModels.Pages.StimulationChannelRole.FixedActive)
                != 1
            || assignments.Count(a => a.Role == ViewModels.Pages.StimulationChannelRole.Selectable)
                != 1
        )
            throw new ArgumentException("包络设备参数或通道角色无效。");
        if (
            !double.IsFinite(setup.DelayMilliseconds)
            || setup.DelayMilliseconds != Math.Truncate(setup.DelayMilliseconds)
            || setup.DelayMilliseconds is < 0 or > 300
        )
            throw new ArgumentException("包络启动延时必须为 0～300 ms 的整数。");
        var settings = new EnvelopePlaybackSettings(
            (decimal)target.PeakCurrent,
            (ushort)setup.DelayMilliseconds,
            checked((ushort)setup.DebugFrequencyHz),
            (byte)
                assignments
                    .Where(a => a.Role == ViewModels.Pages.StimulationChannelRole.Selectable)
                    .Aggregate(0, (mask, a) => mask | (1 << (a.PhysicalChannelId - 1)))
        );
        settings.Validate();
        return settings;
    }

    public async Task PrepareAsync(EnvelopePlaybackPlan plan, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            _prepared = false;
            if (
                !(currentPolicy ?? new StimulationCurrentPolicy(new())).IsValidMilliAmps(
                    (double)plan.Settings.MaximumCurrent
                )
            )
                throw new ArgumentException("最大电流超出当前刺激电流配置范围或步进。");
            if (
                !manager.TryGet(new DeviceId(deviceId), out var device)
                || device is null
                || device.State.Connection != DeviceConnectionState.Connected
            )
                throw new InvalidOperationException("设备未连接，不能开始包络刺激。");
            var source = device.CapabilitySource(DeviceCapabilityKind.Stimulation);
            if (source == DeviceCapabilitySource.Disabled)
                throw new NotSupportedException("设备未启用包络刺激能力。");
            var capability =
                device.EnvelopeStimulation
                ?? throw new NotSupportedException("设备未启用包络刺激能力。");
            // Protocol bindings describe physical commands, not simulated capability support.
            if (
                source == DeviceCapabilitySource.Real
                && device is IDeviceProtocolBinding binding
                && (
                    !binding.SupportsCommand(typeof(ConfigureEnvelopeStimulationRequest))
                    || !binding.SupportsCommand(typeof(ControlEnvelopeStimulationRequest))
                )
            )
                throw new NotSupportedException("当前协议不支持包络刺激，请使用 V102。");
            var faults = Faults.GetOrCreateValue(manager);
            var recovery = faults.TryGetValue(deviceId, out var faultedDevice);
            if (recovery && ReferenceEquals(device, faultedDevice))
                throw new InvalidOperationException("设备状态未确认，请重新连接设备后重播。");
            var state = device.State;
            var confirmStop =
                recovery
                || source == DeviceCapabilitySource.Real
                    && state.Operation == DeviceOperationState.Unknown;
            Log(
                "Preflight",
                $"connection={state.Connection}; operation={state.Operation}; source={source}; updatedAt={state.UpdatedAt:O}; confirmStop={confirmStop}"
            );
            // Recovery may stop a previously uncertain stimulation, but never interrupts acquisition or impedance checks.
            if (
                !recovery
                || state.Operation
                    is not (DeviceOperationState.Stimulating or DeviceOperationState.Stopping)
            )
                RequireReady(state, confirmStop);
            if (
                Enumerable
                    .Range(1, 8)
                    .Any(id =>
                        (plan.Settings.ChannelMask & (1 << (id - 1))) != 0
                        && id > device.Capabilities.StimulationPhysicalChannelCount
                    )
            )
                throw new ArgumentException("包络物理通道超出设备范围。");
            _device = device;
            _plan = plan;
            _uncertain = false;
            _pause =
                source == DeviceCapabilitySource.Real
                    ? heartbeat?.Pause(device.Identity.DeviceId)
                    : null;
            _watchCancellation = new();
            _faultCancellation = new();
            var subscription = device.SubscribeEvents();
            _watch = WatchAsync(subscription, _watchCancellation.Token);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                _faultCancellation.Token
            );
            token = execution.Token;
            await subscription.Ready.WaitAsync(token).ConfigureAwait(false);
            if (confirmStop)
            {
                await ConfirmStoppedAsync(capability, recovery, token).ConfigureAwait(false);
                faults.TryRemove(deviceId, out _);
                _mayBeRunning = false;
            }
            token.ThrowIfCancellationRequested();
            RequireReady(device.State);
            SetStatus("正在下发包络参数");
            Log(
                "Configure",
                $"frequency={plan.Settings.DebugFrequencyHz}; delay={plan.Settings.DelayMilliseconds}; "
                    + $"mask={plan.Settings.ChannelMask}; rate=100; count={plan.Samples.Length}; "
                    + $"maximumCurrent={plan.Settings.MaximumCurrent}; peak={plan.Samples.Max() * .033m}; "
                    + $"sha256={Convert.ToHexString(SHA256.HashData(plan.Samples.AsSpan()))}"
            );
            var result = await capability
                .ConfigureAsync(
                    new(
                        plan.Settings.DelayMilliseconds,
                        plan.Settings.ChannelMask,
                        EnvelopePlaybackPlan.SampleRateHz,
                        plan.Settings.DebugFrequencyHz,
                        plan.Samples.ToArray()
                    ),
                    token
                )
                .WaitAsync(token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            LogResult("Configure", result);
            if (!result.IsSuccess)
                throw Rejected("包络参数配置", result);
            _prepared = true;
        }
        catch (Exception e)
        {
            if (
                e
                is EGGtCSPlatform.Communication.Core.RequestTimeoutException
                    or OperationCanceledException
            )
                MarkUncertain();
            SetStatus($"包络参数配置失败：{e.Message}");
            Log("ConfigureFailed", e.Message);
            if (!_mayBeRunning)
                await ReleaseMonitoringAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConfirmStoppedAsync(
        IEnvelopeStimulationCapability capability,
        bool recovery,
        CancellationToken token
    )
    {
        SetStatus(recovery ? "重新连接后确认包络刺激已停止" : "正在确认包络刺激已停止");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(stopTimeout ?? TimeSpan.FromMilliseconds(1500));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            var result = await capability
                .StopAsync(timeout.Token)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            LogResult("ConfirmStop", result);
            timeout.Token.ThrowIfCancellationRequested();
            RequireStop(result);
            if (_uncertain)
                throw new InvalidOperationException("设备断连或故障，停止状态未确认。");
            RequireReady(_device!.State);
        }
        catch (Exception e)
        {
            MarkUncertain();
            Log("ConfirmStopFailed", e.Message);
            throw new InvalidOperationException(
                $"设备停止未确认，请重新连接设备后重播。{e.Message}",
                e
            );
        }
    }

    private void RequireReady(DeviceStateSnapshot state, bool allowUnknown = false)
    {
        if (state.Connection != DeviceConnectionState.Connected)
        {
            Log(
                "StateRejected",
                $"connection={state.Connection}; operation={state.Operation}; updatedAt={state.UpdatedAt:O}"
            );
            throw new InvalidOperationException("设备未连接，不能配置包络刺激。");
        }
        var reason = state.Operation switch
        {
            DeviceOperationState.Ready => null,
            DeviceOperationState.Unknown when allowUnknown => null,
            DeviceOperationState.Unknown => "设备运行状态未确认，不能配置包络刺激。",
            DeviceOperationState.Faulted => "设备处于故障状态，不能配置包络刺激。",
            DeviceOperationState.Acquiring => "设备正在采集脑电，不能配置包络刺激。",
            DeviceOperationState.Stimulating => "设备正在刺激，不能配置包络刺激。",
            DeviceOperationState.Stopping => "设备正在停止刺激，不能配置包络刺激。",
            DeviceOperationState.ImpedanceChecking => "设备正在检测阻抗，不能配置包络刺激。",
            _ => "设备运行状态未确认，不能配置包络刺激。",
        };
        if (reason is not null)
        {
            Log(
                "StateRejected",
                $"connection={state.Connection}; operation={state.Operation}; updatedAt={state.UpdatedAt:O}"
            );
            throw new InvalidOperationException(reason);
        }
    }

    public async Task StartAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!_prepared || _uncertain || _mayBeRunning)
                throw new InvalidOperationException("包络配置尚未成功或设备状态未确认，不能启动。");
            _prepared = false;
            SetStatus("等待包络刺激启动响应");
            _mayBeRunning = true;
            var result = await _device!
                .EnvelopeStimulation!.StartAsync(token)
                .WaitAsync(token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            LogResult("Start", result);
            if (!result.IsSuccess)
                throw Rejected("包络刺激启动", result);
            _started = _clock.GetTimestamp();
            SetStatus("包络刺激已启动，播放音频");
        }
        catch (Exception e)
        {
            MarkUncertain();
            SetStatus($"包络刺激启动失败：{e.Message}");
            Log("StartFailed", e.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WaitForCompletionAsync(CancellationToken token)
    {
        SetStatus("音频已结束，等待包络刺激结束");
        var remaining = _plan!.Duration - _clock.GetElapsedTime(_started);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, _clock, token).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _prepared = false;
            if (_mayBeRunning)
            {
                SetStatus("等待包络刺激停止确认");
                using var timeout = new CancellationTokenSource(
                    stopTimeout ?? TimeSpan.FromMilliseconds(1500)
                );
                try
                {
                    var result = await _device!
                        .EnvelopeStimulation!.StopAsync(timeout.Token)
                        .WaitAsync(timeout.Token)
                        .ConfigureAwait(false);
                    LogResult("Stop", result);
                    RequireStop(result);
                    _mayBeRunning = false;
                    SetStatus(
                        _uncertain ? "设备状态未确认，请重新连接设备后重播" : "包络刺激已停止"
                    );
                }
                catch (Exception e)
                {
                    MarkUncertain();
                    SetStatus($"设备停止未确认：{e.Message}");
                    Log("StopFailed", e.Message);
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                await ReleaseMonitoringAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task ReleaseMonitoringAsync()
    {
        _watchCancellation?.Cancel();
        try
        {
            await _watch.ConfigureAwait(false);
        }
        finally
        {
            _watchCancellation?.Dispose();
            _watchCancellation = null;
            _faultCancellation?.Dispose();
            _faultCancellation = null;
            _pause?.Dispose();
            _pause = null;
        }
    }

    private async Task WatchAsync(DeviceEventSubscription subscription, CancellationToken token)
    {
        await using (subscription)
        {
            try
            {
                await foreach (
                    var item in subscription.ReadEventsAsync(token).ConfigureAwait(false)
                )
                    if (
                        item.Event is DeviceStateChangedEvent changed
                        && (
                            changed.State.Connection
                                is DeviceConnectionState.Disconnected
                                    or DeviceConnectionState.Faulted
                            || changed.State.Operation == DeviceOperationState.Faulted
                        )
                    )
                    {
                        MarkUncertain();
                        _faultCancellation?.Cancel();
                        Faulted?.Invoke(
                            this,
                            "设备断连或故障，当前刺激状态未确认，请重新连接设备。"
                        );
                        return;
                    }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                MarkUncertain();
                _faultCancellation?.Cancel();
                Faulted?.Invoke(this, $"设备事件监听失败：{e.Message}");
            }
        }
    }

    private static Exception Rejected(string stage, DeviceCommandResult result) =>
        new InvalidOperationException(
            $"{stage}被拒绝：{result.Status}（响应码 0x{result.RawResultCode:X2}）。"
        );

    private static void RequireStop(DeviceCommandResult result)
    {
        if (!result.IsSuccess && result.Status != DeviceCommandStatus.AlreadyStopped)
            throw Rejected("包络刺激停止", result);
    }

    private void SetStatus(string value)
    {
        Status = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void MarkUncertain()
    {
        _uncertain = true;
        if (_device is not null)
            Faults.GetOrCreateValue(manager)[deviceId] = _device;
    }

    private void LogResult(string stage, DeviceCommandResult result) =>
        Log(stage, $"status={result.Status}; raw={result.RawResultCode}");

    private void Log(string stage, string value) =>
        ApplicationLog.Write(
            ApplicationLogLevel.Info,
            nameof(DeviceEnvelopeOutput),
            "Envelope." + stage,
            $"device={deviceId}; {value}",
            _plan?.PlaybackId.ToString()
        );
}

/// <summary>Stops audio and device output before application services disconnect devices.</summary>
public sealed class SpeechOutputLifetime
{
    private readonly List<(
        WeakReference<SpeechTrainingSession> Session,
        WeakReference<Func<Task>>? Finalize
    )> _sessions = [];

    public void Register(SpeechTrainingSession session, Func<Task>? finalize = null)
    {
        lock (_sessions)
            _sessions.Add((new(session), finalize is null ? null : new(finalize)));
    }

    public async Task StopAllAsync()
    {
        (
            WeakReference<SpeechTrainingSession> Session,
            WeakReference<Func<Task>>? Finalize
        )[] sessions;
        lock (_sessions)
            sessions = _sessions.ToArray();
        foreach (var reference in sessions)
            if (reference.Session.TryGetTarget(out var session))
                try
                {
                    if (
                        reference.Finalize is not null
                        && reference.Finalize.TryGetTarget(out var finalize)
                    )
                        await finalize().ConfigureAwait(false);
                    else
                        await session.StopAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    ApplicationLog.Write(
                        ApplicationLogLevel.Error,
                        nameof(SpeechOutputLifetime),
                        "Envelope.ExitStopFailed",
                        "设备停止未确认",
                        exception: e
                    );
                }
    }
}
