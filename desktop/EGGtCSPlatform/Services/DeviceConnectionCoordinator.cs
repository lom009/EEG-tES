using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;

namespace EGGtCSPlatform.Services;

public enum DeviceConnectionAutomationStage
{
    Idle,
    WaitingToRetry,
    Searching,
    Connecting,
    Connected,
    ManualReview,
    Stopped,
}

public sealed record DeviceConnectionCoordinatorSnapshot(
    DeviceConnectionAutomationStage Stage,
    string StatusText,
    string BusyText,
    IReadOnlyList<DeviceCandidate> Candidates,
    bool IsBusy,
    bool IsManualReview
);

public interface IDeviceConnectionCoordinator : IAsyncDisposable
{
    event EventHandler<DeviceConnectionCoordinatorSnapshot>? StateChanged;
    DeviceConnectionCoordinatorSnapshot Snapshot { get; }
    Task StartAsync();
    void EnterManualMode();
    void ExitManualMode();
    Task<IReadOnlyList<DeviceCandidate>> SearchManualAsync(
        CancellationToken cancellationToken = default
    );
    Task<bool> ConnectManualAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    );
}

public sealed record DeviceConnectionFaultNotification(DeviceIdentity Identity, Exception Exception)
{
    public IEggtCsDevice? SourceDevice { get; init; }
}

public interface IDeviceConnectionFaultNotifier
{
    event Action<DeviceConnectionFaultNotification>? Faulted;
    void Notify(DeviceIdentity identity, Exception exception);
    void Notify(DeviceIdentity identity, Exception exception, IEggtCsDevice? sourceDevice) =>
        Notify(identity, exception);
}

public sealed class DeviceConnectionFaultNotifier : IDeviceConnectionFaultNotifier
{
    public event Action<DeviceConnectionFaultNotification>? Faulted;

    public void Notify(DeviceIdentity identity, Exception exception) =>
        Faulted?.Invoke(new DeviceConnectionFaultNotification(identity, exception));

    public void Notify(DeviceIdentity identity, Exception exception, IEggtCsDevice? sourceDevice) =>
        Faulted?.Invoke(
            new DeviceConnectionFaultNotification(identity, exception)
            {
                SourceDevice = sourceDevice,
            }
        );
}

public sealed class DeviceConnectionCoordinator(
    DeviceAutoConnectionOptions options,
    IDeviceManager deviceManager,
    IDeviceSelectionContext selection,
    IDeviceConnectionNetworkContext networkContext,
    IConfiguredDeviceDiscovery discovery,
    IDeviceConnectionProfileStore profileStore,
    IDeviceConnectionFaultNotifier? faultNotifier = null,
    DeviceBackendOptions? backendOptions = null,
    EGGtCSPlatform.DeviceRuntime.IDeviceRuntime? runtime = null,
    IApplicationLogger? logger = null
) : IDeviceConnectionCoordinator
{
    private IApplicationLogger Log => logger ?? ApplicationLog.Current;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        byte
    > _reportedFailures = new();

    private void ReportFailure(string eventName, string id, Exception exception)
    {
        var first = _reportedFailures.TryAdd(
            eventName + ":" + id + ":" + exception.GetType().Name,
            0
        );
        Log.Write(
            first ? ApplicationLogLevel.Warning : ApplicationLogLevel.Debug,
            nameof(DeviceConnectionCoordinator),
            eventName,
            "连接或搜索失败",
            id,
            exception
        );
    }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _connectionFaultSignal = new(0, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _connectAttempt;
    private Task? _runner;
    private bool _manualMode;
    private bool _faultSubscribed;
    private bool _disposed;
    private int _selectedConnectionFaulted;
    private DeviceConnectionCoordinatorSnapshot _snapshot = new(
        DeviceConnectionAutomationStage.Idle,
        "等待设备连接",
        string.Empty,
        [],
        false,
        false
    );

    public event EventHandler<DeviceConnectionCoordinatorSnapshot>? StateChanged;

    public DeviceConnectionCoordinatorSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
                return _snapshot;
        }
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateGate)
        {
            if (!_faultSubscribed && faultNotifier is not null)
            {
                faultNotifier.Faulted += OnConnectionFaulted;
                _faultSubscribed = true;
            }
            if (!options.Enabled)
            {
                Publish(
                    DeviceConnectionAutomationStage.Idle,
                    "自动连接已关闭",
                    string.Empty,
                    [],
                    false
                );
                return Task.CompletedTask;
            }
            _runner ??= Task.Run(() => RunAsync(_lifetime.Token));
        }
        return Task.CompletedTask;
    }

    public void EnterManualMode()
    {
        _manualMode = true;
        Interlocked.Exchange(ref _connectAttempt, null)?.Cancel();
        var current = Snapshot;
        Publish(
            current.Stage == DeviceConnectionAutomationStage.Searching
                ? current.Stage
                : DeviceConnectionAutomationStage.ManualReview,
            current.StatusText,
            current.BusyText,
            current.Candidates,
            current.IsBusy
        );
    }

    public void ExitManualMode()
    {
        _manualMode = false;
        var current = Snapshot;
        Publish(
            current.Stage,
            current.StatusText,
            current.BusyText,
            current.Candidates,
            current.IsBusy
        );
    }

    public async Task<IReadOnlyList<DeviceCandidate>> SearchManualAsync(
        CancellationToken cancellationToken = default
    )
    {
        EnterManualMode();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = new List<DeviceCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mode in GetEnabledModes())
            {
                await DiscoverModeAsync(mode, candidates, seen, cancellationToken)
                    .ConfigureAwait(false);
            }
            Publish(
                DeviceConnectionAutomationStage.ManualReview,
                candidates.Count == 0
                    ? "未发现可连接设备，请检查配置后重试"
                    : $"已发现 {candidates.Count} 台设备",
                string.Empty,
                candidates,
                false
            );
            return candidates;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> ConnectManualAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    )
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryConnectAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var trySavedEndpoint = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await WaitForAutomaticControlAsync(cancellationToken).ConfigureAwait(false);
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var connected = false;
                try
                {
                    var profile = profileStore.Load() ?? options.LastDevice;
                    if (
                        trySavedEndpoint
                        && profile is not null
                        && MatchesConnectionMode(ToCandidate(profile))
                    )
                    {
                        connected = await TryConnectAsync(ToCandidate(profile), cancellationToken)
                            .ConfigureAwait(false);
                        trySavedEndpoint = false;
                    }
                    if (!connected && !_manualMode)
                    {
                        foreach (var mode in GetEnabledModes())
                        {
                            var candidates = new List<DeviceCandidate>();
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            await DiscoverModeAsync(mode, candidates, seen, cancellationToken)
                                .ConfigureAwait(false);
                            if (_manualMode)
                                break;
                            foreach (var candidate in Prioritize(candidates, profile))
                            {
                                if (
                                    await TryConnectAsync(candidate, cancellationToken)
                                        .ConfigureAwait(false)
                                )
                                {
                                    connected = true;
                                    break;
                                }
                            }
                            if (connected)
                                break;
                        }
                    }
                }
                finally
                {
                    _operationGate.Release();
                }

                if (_manualMode)
                {
                    var current = Snapshot;
                    Publish(
                        DeviceConnectionAutomationStage.ManualReview,
                        current.Candidates.Count == 0
                            ? "自动搜索已完成，未发现设备"
                            : $"已发现 {current.Candidates.Count} 台设备",
                        string.Empty,
                        current.Candidates,
                        false
                    );
                    continue;
                }
                if (connected)
                {
                    while (
                        selection.IsConnected
                        && Volatile.Read(ref _selectedConnectionFaulted) == 0
                        && !cancellationToken.IsCancellationRequested
                    )
                        await _connectionFaultSignal
                            .WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
                            .ConfigureAwait(false);
                    trySavedEndpoint = true;
                    continue;
                }

                Publish(
                    DeviceConnectionAutomationStage.WaitingToRetry,
                    $"本轮未连接到设备，{options.RetryInterval.TotalSeconds:0.###} 秒后重试",
                    "等待自动重试…",
                    Snapshot.Candidates,
                    false
                );
                await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ReportFailure("Device.AutoConnectFailed", "automatic", exception);
                Publish(
                    DeviceConnectionAutomationStage.WaitingToRetry,
                    $"自动连接失败：{exception.Message}",
                    "等待自动重试…",
                    Snapshot.Candidates,
                    false
                );
                await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        Publish(DeviceConnectionAutomationStage.Stopped, "自动连接已停止", string.Empty, [], false);
    }

    private async Task DiscoverModeAsync(
        DeviceDiscoveryModeDefinition mode,
        List<DeviceCandidate> candidates,
        HashSet<string> seen,
        CancellationToken cancellationToken
    )
    {
        Publish(
            DeviceConnectionAutomationStage.Searching,
            $"正在进行{mode.DisplayName}搜索…",
            $"正在进行{mode.DisplayName}搜索…",
            candidates,
            true
        );
        try
        {
            await foreach (
                var candidate in discovery
                    .DiscoverAsync(mode, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                if (seen.Add(candidate.Identity.DeviceId.Value))
                {
                    candidates.Add(candidate);
                    Publish(
                        DeviceConnectionAutomationStage.Searching,
                        $"{mode.DisplayName}已发现 {candidates.Count} 台设备",
                        $"正在进行{mode.DisplayName}搜索…",
                        candidates,
                        true
                    );
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure("Device.DiscoveryFailed", mode.DisplayName, exception);
            Publish(
                DeviceConnectionAutomationStage.Searching,
                $"{mode.DisplayName}不可用：{exception.Message}",
                string.Empty,
                candidates,
                false
            );
        }
    }

    private IReadOnlyList<DeviceDiscoveryModeDefinition> GetEnabledModes() =>
        backendOptions?.ConnectionSource == DeviceConnectionSource.Simulated
            ?
            [
                new DeviceDiscoveryModeDefinition(
                    DeviceDiscoveryModeKind.Simulator,
                    "模拟设备",
                    1,
                    TimeSpan.FromMilliseconds(1)
                ),
            ]
            : options.GetEnabledModes();

    private async Task<bool> TryConnectAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        if (!MatchesConnectionMode(candidate))
            return false;
        if (_manualMode && Snapshot.Stage != DeviceConnectionAutomationStage.ManualReview)
            return false;
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _connectAttempt, attempt)?.Dispose();
        Publish(
            DeviceConnectionAutomationStage.Connecting,
            $"正在连接 {candidate.Identity.Model}…",
            "正在连接并验证设备…",
            [candidate],
            true
        );
        IEggtCsDevice? connectedDevice = null;
        Log.Write(
            _manualMode ? ApplicationLogLevel.Info : ApplicationLogLevel.Debug,
            nameof(DeviceConnectionCoordinator),
            "Device.ConnectAttempt",
            "正在连接并验证设备",
            candidate.Identity.DeviceId.Value
        );
        try
        {
            var previousId = selection.SelectedDeviceId;
            if (!string.IsNullOrWhiteSpace(previousId))
            {
                await deviceManager
                    .RemoveAsync(new DeviceId(previousId), attempt.Token)
                    .ConfigureAwait(false);
                Log.Write(
                    ApplicationLogLevel.Info,
                    nameof(DeviceConnectionCoordinator),
                    "Device.Disconnected",
                    "切换连接，已移除原设备",
                    previousId
                );
            }
            selection.ClearConnection("正在连接设备…");
            if (deviceManager.TryGet(candidate.Identity.DeviceId, out _))
                await deviceManager
                    .RemoveAsync(candidate.Identity.DeviceId, attempt.Token)
                    .ConfigureAwait(false);
            int batteryPercent;
            if (runtime is not null)
            {
                connectedDevice = await runtime
                    .ConnectVerifiedAsync(candidate, attempt.Token)
                    .ConfigureAwait(false);
                batteryPercent = connectedDevice.State.BatteryPercent ?? 0;
            }
            else
            {
                // Compatibility for callers that supply a standalone manager.
                connectedDevice = await deviceManager
                    .ConnectAsync(candidate, attempt.Token)
                    .ConfigureAwait(false);
                var status = await connectedDevice
                    .ReadStatusAsync(attempt.Token)
                    .ConfigureAwait(false);
                if (
                    status.Status != DeviceCommandStatus.Success
                    || connectedDevice.State.Connection != DeviceConnectionState.Connected
                )
                    throw new InvalidOperationException($"设备状态验证失败：{status.Status}");
                batteryPercent = status.BatteryPercent;
            }
            var simulated = candidate.Endpoint.Scheme.Equals(
                "simulator",
                StringComparison.OrdinalIgnoreCase
            );
            if (!simulated)
                networkContext.Configure(
                    candidate.Endpoint.Port,
                    networkContext.Snapshot.LocalPort
                );
            selection.SetConnectedDevice(
                connectedDevice.Identity.DeviceId.Value,
                connectedDevice.Identity.Model,
                candidate.Endpoint.Address,
                connectedDevice.Identity.MacAddress,
                batteryPercent
            );
            Volatile.Write(ref _selectedConnectionFaulted, 0);
            _reportedFailures.Clear();
            Log.Write(
                ApplicationLogLevel.Info,
                nameof(DeviceConnectionCoordinator),
                "Device.Connected",
                "设备连接成功",
                connectedDevice.Identity.DeviceId.Value
            );
            try
            {
                if (!simulated)
                {
                    var network = networkContext.Snapshot;
                    await profileStore
                        .SaveAsync(
                            new SavedDeviceConnectionProfile(
                                connectedDevice.Identity.DeviceId.Value,
                                connectedDevice.Identity.Model,
                                connectedDevice.Identity.SerialNumber,
                                connectedDevice.Identity.MacAddress,
                                candidate.Endpoint.Scheme,
                                candidate.Endpoint.Address,
                                candidate.Endpoint.Port,
                                runtime is not null
                                && connectedDevice is IDeviceProtocolBinding binding
                                    ? binding.ProtocolInfo.ActiveRevision
                                    : candidate.ProtocolVersion,
                                network.LocalAddress,
                                network.LocalPort
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                Publish(
                    DeviceConnectionAutomationStage.Connected,
                    $"已连接，但保存最近设备失败：{exception.Message}",
                    string.Empty,
                    [candidate],
                    false
                );
                return true;
            }
            Publish(
                DeviceConnectionAutomationStage.Connected,
                $"已连接：{connectedDevice.Identity.Model}",
                string.Empty,
                [candidate],
                false
            );
            return true;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            Log.Write(
                ApplicationLogLevel.Debug,
                nameof(DeviceConnectionCoordinator),
                "Device.ConnectCanceled",
                "连接已取消",
                candidate.Identity.DeviceId.Value
            );
            if (connectedDevice is not null)
                await TryRemoveAsync(connectedDevice.Identity.DeviceId).ConfigureAwait(false);
            if (_manualMode)
                Publish(
                    DeviceConnectionAutomationStage.ManualReview,
                    "自动连接已取消，请选择设备后手动连接",
                    string.Empty,
                    [candidate],
                    false
                );
            return false;
        }
        catch (Exception exception)
        {
            if (connectedDevice is not null)
                await TryRemoveAsync(connectedDevice.Identity.DeviceId).ConfigureAwait(false);
            ReportFailure("Device.ConnectFailed", candidate.Identity.DeviceId.Value, exception);
            var message = exception
                is EGGtCSPlatform.DeviceRuntime.DeviceConnectionValidationException validation
                ? $"设备状态验证失败：{validation.Status}"
                : exception.Message;
            selection.ClearConnection($"设备连接失败：{message}");
            Publish(
                _manualMode
                    ? DeviceConnectionAutomationStage.ManualReview
                    : DeviceConnectionAutomationStage.Connecting,
                $"连接失败：{message}",
                string.Empty,
                [candidate],
                false
            );
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _connectAttempt, null, attempt);
            attempt.Dispose();
        }
    }

    private async Task WaitForAutomaticControlAsync(CancellationToken cancellationToken)
    {
        while (_manualMode)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<DeviceCandidate> Prioritize(
        IEnumerable<DeviceCandidate> candidates,
        SavedDeviceConnectionProfile? profile
    ) =>
        candidates.OrderBy(candidate =>
            profile is not null
            && string.Equals(
                candidate.Identity.DeviceId.Value,
                profile.DeviceId,
                StringComparison.OrdinalIgnoreCase
            )
                ? 0
            : profile is not null
            && !string.IsNullOrWhiteSpace(profile.MacAddress)
            && string.Equals(
                candidate.Identity.MacAddress,
                profile.MacAddress,
                StringComparison.OrdinalIgnoreCase
            )
                ? 1
            : 2
        );

    private bool MatchesConnectionMode(DeviceCandidate candidate)
    {
        if (backendOptions is null)
            return true; // Standalone compatibility callers choose their own connectors.
        var simulated = candidate.Endpoint.Scheme.Equals(
            "simulator",
            StringComparison.OrdinalIgnoreCase
        );
        return backendOptions.ConnectionSource == DeviceConnectionSource.Simulated
            ? simulated
                && (
                    candidate.ConnectionEndpoint is null
                    || candidate.ConnectionEndpoint.Scheme.Equals(
                        "simulator",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                && candidate.ProtocolId is null or "simulator"
            : !simulated;
    }

    private static DeviceCandidate ToCandidate(SavedDeviceConnectionProfile profile) =>
        new(
            new DeviceIdentity(
                new DeviceId(profile.DeviceId),
                profile.Model,
                profile.SerialNumber,
                profile.MacAddress
            ),
            new TransportEndpoint(profile.Scheme, profile.Address, profile.DevicePort),
            profile.ProtocolVersion
        )
        {
            ProtocolId = profile.Scheme.Equals("simulator", StringComparison.OrdinalIgnoreCase)
                ? "simulator"
                : Protocol.EggtCs.Gen1.EggtCsProtocolModule.Id,
            RevisionSource = profile.Scheme.Equals("simulator", StringComparison.OrdinalIgnoreCase)
                ? ProtocolRevisionSource.Unspecified
                : ProtocolRevisionSource.CompatibilityAssumption,
        };

    private async Task TryRemoveAsync(DeviceId deviceId)
    {
        try
        {
            await deviceManager.RemoveAsync(deviceId).ConfigureAwait(false);
        }
        catch { }
    }

    private void Publish(
        DeviceConnectionAutomationStage stage,
        string status,
        string busy,
        IReadOnlyList<DeviceCandidate> candidates,
        bool isBusy
    )
    {
        DeviceConnectionCoordinatorSnapshot snapshot;
        lock (_stateGate)
        {
            snapshot = new DeviceConnectionCoordinatorSnapshot(
                stage,
                status,
                busy,
                candidates.ToArray(),
                isBusy,
                _manualMode
            );
            _snapshot = snapshot;
        }
        StateChanged?.Invoke(this, snapshot);
    }

    private void OnConnectionFaulted(DeviceConnectionFaultNotification notification)
    {
        if (notification.SourceDevice is { } source)
        {
            if (
                !deviceManager.TryGet(source.Identity.DeviceId, out var current)
                || !ReferenceEquals(source, current)
            )
                return;
            _ = TryRemoveCurrentAsync(source);
        }
        else
            _ = TryRemoveAsync(notification.Identity.DeviceId);
        if (
            !string.Equals(
                selection.SelectedDeviceId,
                notification.Identity.DeviceId.Value,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return;

        if (Interlocked.Exchange(ref _selectedConnectionFaulted, 1) == 0)
            Log.Write(
                ApplicationLogLevel.Warning,
                nameof(DeviceConnectionCoordinator),
                "Device.ConnectionLost",
                "设备连接异常断开",
                notification.Identity.DeviceId.Value,
                notification.Exception
            );
        try
        {
            _connectionFaultSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending notification already wakes the coordinator.
        }
    }

    private async Task TryRemoveCurrentAsync(IEggtCsDevice source)
    {
        try
        {
            await deviceManager.RemoveIfCurrentAsync(source).ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        Interlocked.Exchange(ref _connectAttempt, null)?.Cancel();
        if (_runner is not null)
        {
            try
            {
                await _runner.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
        if (_faultSubscribed && faultNotifier is not null)
            faultNotifier.Faulted -= OnConnectionFaulted;
        _connectionFaultSignal.Dispose();
        _operationGate.Dispose();
        _lifetime.Dispose();
    }
}
