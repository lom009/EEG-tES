using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed class DeviceImpedanceDetectionService(
    IDeviceManager deviceManager,
    IEegPhysicalChannelMappingService eegChannelMappings,
    IDeviceHeartbeatPauseService? heartbeatPause = null
) : IImpedanceDetectionService
{
    private readonly object _heartbeatPauseGate = new();
    private readonly Dictionary<
        (DeviceId DeviceId, ImpedanceDetectionKind Kind),
        HeartbeatPauseRegistration
    > _heartbeatPauses = [];

    public static TimeSpan EegDataTimeout { get; } = TimeSpan.FromMilliseconds(2500);

    public async IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var device = GetDevice(deviceId);
        var capability =
            device.Impedance
            ?? throw new NotSupportedException(
                $"Device {deviceId} does not support impedance detection."
            );
        var channels = ResolveEegChannels(device, electrodeIds);
        var physicalChannels = channels.Values.ToHashSet();
        var usesSimulation = UsesSimulatedImpedance(device, ImpedanceDetectionKind.Eeg);
        await foreach (
            var readings in WatchAsync(
                    device,
                    () =>
                        StartWithHeartbeatPausedAsync(
                            device,
                            ImpedanceDetectionKind.Eeg,
                            () => capability.StartEegAsync(physicalChannels, cancellationToken)
                        ),
                    channels.ToDictionary(item => item.Value, item => item.Key),
                    ImpedanceDetectionKind.Eeg,
                    usesSimulation,
                    usesSimulation ? null : EegDataTimeout,
                    usesSimulation
                        ? null
                        : token =>
                            StopAndResumeHeartbeatAsync(
                                device,
                                ImpedanceDetectionKind.Eeg,
                                () => capability.StopEegAsync(physicalChannels, token)
                            ),
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
            yield return readings;
    }

    public Task<IReadOnlyDictionary<string, double>> StartEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        CancellationToken cancellationToken = default
    ) => RunEegAsync(deviceId, electrodeIds, start: true, cancellationToken);

    public async Task StopEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        CancellationToken cancellationToken = default
    )
    {
        _ = await RunEegAsync(deviceId, electrodeIds, start: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var device = GetDevice(deviceId);
        var capability =
            device.Impedance
            ?? throw new NotSupportedException(
                $"Device {deviceId} does not support impedance detection."
            );
        var configuration = CreateStimulationConfiguration(device, request);
        await foreach (
            var readings in WatchAsync(
                    device,
                    () =>
                        StartWithHeartbeatPausedAsync(
                            device,
                            ImpedanceDetectionKind.Stimulation,
                            () => capability.StartStimulationAsync(configuration, cancellationToken)
                        ),
                    CreateStimulationElectrodeMap(request.Assignments),
                    ImpedanceDetectionKind.Stimulation,
                    UsesSimulatedImpedance(device, ImpedanceDetectionKind.Stimulation),
                    null,
                    null,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
            yield return readings;
    }

    public Task<IReadOnlyDictionary<string, double>> StartStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        CancellationToken cancellationToken = default
    ) => RunStimulationAsync(deviceId, request, start: true, cancellationToken);

    public async Task StopStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _ = await RunStimulationAsync(deviceId, request, start: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, double>> RunEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        bool start,
        CancellationToken cancellationToken
    )
    {
        var device = GetDevice(deviceId);
        var capability =
            device.Impedance
            ?? throw new NotSupportedException(
                $"Device {deviceId} does not support impedance detection."
            );
        var channels = ResolveEegChannels(device, electrodeIds);
        if (!start)
        {
            var stopped = await StopAndResumeHeartbeatAsync(
                    device,
                    ImpedanceDetectionKind.Eeg,
                    () => capability.StopEegAsync(channels.Values.ToHashSet(), cancellationToken)
                )
                .ConfigureAwait(false);
            EnsureSuccess(stopped, "停止 EEG 阻抗检测");
            return new Dictionary<string, double>();
        }
        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        await using var subscription = device.SubscribeEvents();
        await subscription.Ready.ConfigureAwait(false);
        await using var enumerator = subscription
            .ReadEventsAsync(eventCancellation.Token)
            .GetAsyncEnumerator(eventCancellation.Token);
        var nextEvent = enumerator.MoveNextAsync().AsTask();
        var eventNotBefore = DateTimeOffset.UtcNow;
        try
        {
            EnsureSuccess(
                await StartWithHeartbeatPausedAsync(
                        device,
                        ImpedanceDetectionKind.Eeg,
                        () =>
                            capability.StartEegAsync(channels.Values.ToHashSet(), cancellationToken)
                    )
                    .ConfigureAwait(false),
                "启动 EEG 阻抗检测"
            );
        }
        catch
        {
            eventCancellation.Cancel();
            await ObservePendingEventAsync(nextEvent).ConfigureAwait(false);
            throw;
        }
        try
        {
            return await ReadResultAsync(
                        enumerator,
                        nextEvent,
                        channels.ToDictionary(item => item.Value, item => item.Key),
                        ImpedanceDetectionKind.Eeg,
                        eventNotBefore,
                        UsesSimulatedImpedance(device, ImpedanceDetectionKind.Eeg)
                            ? null
                            : EegDataTimeout,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                ?? throw new OperationCanceledException(cancellationToken);
        }
        catch (EegImpedanceDataTimeoutException exception)
        {
            var stopCommandRequested = false;
            try
            {
                stopCommandRequested = true;
                _ = await StopAndResumeHeartbeatAsync(
                        device,
                        ImpedanceDetectionKind.Eeg,
                        () =>
                            capability.StopEegAsync(
                                channels.Values.ToHashSet(),
                                CancellationToken.None
                            )
                    )
                    .ConfigureAwait(false);
            }
            catch
            {
                // Keep the data-timeout cause even if the best-effort stop is rejected.
            }
            if (stopCommandRequested)
            {
                throw new EegImpedanceDataTimeoutException(
                    exception.Timeout,
                    stopCommandRequested: true
                );
            }
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, double>> RunStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        bool start,
        CancellationToken cancellationToken
    )
    {
        var device = GetDevice(deviceId);
        var capability =
            device.Impedance
            ?? throw new NotSupportedException(
                $"Device {deviceId} does not support impedance detection."
            );
        var configuration = CreateStimulationConfiguration(device, request);
        if (!start)
        {
            EnsureSuccess(
                await StopAndResumeHeartbeatAsync(
                        device,
                        ImpedanceDetectionKind.Stimulation,
                        () => capability.StopStimulationAsync(configuration, cancellationToken)
                    )
                    .ConfigureAwait(false),
                "停止刺激阻抗检测"
            );
            return new Dictionary<string, double>();
        }
        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        await using var subscription = device.SubscribeEvents();
        await subscription.Ready.ConfigureAwait(false);
        await using var enumerator = subscription
            .ReadEventsAsync(eventCancellation.Token)
            .GetAsyncEnumerator(eventCancellation.Token);
        var nextEvent = enumerator.MoveNextAsync().AsTask();
        var eventNotBefore = DateTimeOffset.UtcNow;
        try
        {
            EnsureSuccess(
                await StartWithHeartbeatPausedAsync(
                        device,
                        ImpedanceDetectionKind.Stimulation,
                        () => capability.StartStimulationAsync(configuration, cancellationToken)
                    )
                    .ConfigureAwait(false),
                "启动刺激阻抗检测"
            );
        }
        catch
        {
            eventCancellation.Cancel();
            await ObservePendingEventAsync(nextEvent).ConfigureAwait(false);
            throw;
        }
        return await ReadResultAsync(
                    enumerator,
                    nextEvent,
                    CreateStimulationElectrodeMap(request.Assignments),
                    ImpedanceDetectionKind.Stimulation,
                    eventNotBefore,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new OperationCanceledException(cancellationToken);
    }

    private static IReadOnlyDictionary<int, string> CreateStimulationElectrodeMap(
        IReadOnlyList<StimulusElectrodeAssignment> assignments
    ) =>
        assignments
            .Where(assignment => assignment.Role == StimulationChannelRole.Selectable)
            .ToDictionary(
                assignment => assignment.PhysicalChannelId,
                assignment => assignment.SiteId
            );

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchAsync(
        IEggtCsDevice device,
        Func<Task<DeviceCommandResult>> start,
        IReadOnlyDictionary<int, string> electrodeByChannel,
        ImpedanceDetectionKind kind,
        bool usesSimulation,
        TimeSpan? dataTimeout,
        Func<CancellationToken, Task<DeviceCommandResult>>? stopOnTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        await using var subscription = device.SubscribeEvents();
        await subscription.Ready.ConfigureAwait(false);
        await using var enumerator = subscription
            .ReadEventsAsync(eventCancellation.Token)
            .GetAsyncEnumerator(eventCancellation.Token);
        Task<bool>? pendingEvent = null;
        try
        {
            if (usesSimulation)
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                        .ConfigureAwait(false);
                    pendingEvent = enumerator.MoveNextAsync().AsTask();
                    var eventNotBefore = DateTimeOffset.UtcNow;
                    EnsureSuccess(
                        await start().ConfigureAwait(false),
                        kind == ImpedanceDetectionKind.Stimulation
                            ? "启动刺激阻抗检测"
                            : "启动 EEG 阻抗检测"
                    );
                    var readings = await ReadResultAsync(
                            enumerator,
                            pendingEvent,
                            electrodeByChannel,
                            kind,
                            eventNotBefore,
                            cancellationToken: cancellationToken
                        )
                        .ConfigureAwait(false);
                    pendingEvent = null;
                    if (readings is null)
                        yield break;
                    yield return readings;
                }
            }

            pendingEvent = enumerator.MoveNextAsync().AsTask();
            var physicalEventNotBefore = DateTimeOffset.UtcNow;
            EnsureSuccess(
                await start().ConfigureAwait(false),
                kind == ImpedanceDetectionKind.Stimulation
                    ? "启动刺激阻抗检测"
                    : "启动 EEG 阻抗检测"
            );
            while (true)
            {
                IReadOnlyDictionary<string, double>? readings;
                try
                {
                    readings = await ReadResultAsync(
                            enumerator,
                            pendingEvent,
                            electrodeByChannel,
                            kind,
                            physicalEventNotBefore,
                            dataTimeout,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                catch (EegImpedanceDataTimeoutException exception)
                {
                    var stopCommandRequested = false;
                    if (stopOnTimeout is not null)
                    {
                        try
                        {
                            stopCommandRequested = true;
                            _ = await stopOnTimeout(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Keep the data-timeout cause even if the best-effort stop is rejected.
                        }
                    }
                    if (stopCommandRequested)
                    {
                        throw new EegImpedanceDataTimeoutException(
                            exception.Timeout,
                            stopCommandRequested: true
                        );
                    }
                    throw;
                }
                if (readings is null)
                    yield break;
                pendingEvent = null;
                yield return readings;
                pendingEvent = enumerator.MoveNextAsync().AsTask();
            }
        }
        finally
        {
            eventCancellation.Cancel();
            if (pendingEvent is not null)
                await ObservePendingEventAsync(pendingEvent).ConfigureAwait(false);
        }
    }

    private static async Task ObservePendingEventAsync(Task<bool> pendingEvent)
    {
        try
        {
            _ = await pendingEvent.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Cleanup must not replace the command or stream error that triggered cancellation.
        }
    }

    private static async Task<IReadOnlyDictionary<string, double>?> ReadResultAsync(
        IAsyncEnumerator<DeviceEventEnvelope> enumerator,
        Task<bool> nextEvent,
        IReadOnlyDictionary<int, string> electrodeByChannel,
        ImpedanceDetectionKind kind,
        DateTimeOffset eventNotBefore,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        using var timeoutCancellation = timeout is { } value
            ? new CancellationTokenSource(value)
            : null;
        using var linkedCancellation = timeoutCancellation is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token
            );
        while (true)
        {
            bool hasEvent;
            try
            {
                hasEvent = linkedCancellation is null
                    ? await nextEvent.ConfigureAwait(false)
                    : await nextEvent.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (timeoutCancellation?.IsCancellationRequested == true
                    && !cancellationToken.IsCancellationRequested
                )
            {
                throw new EegImpedanceDataTimeoutException(timeout!.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            if (!hasEvent)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;
                throw new InvalidOperationException(
                    "The device event stream ended before impedance data arrived."
                );
            }
            var readings =
                enumerator.Current.Timestamp >= eventNotBefore
                    ? TryMapReadings(enumerator.Current.Event, electrodeByChannel, kind)
                    : null;
            if (readings is { Count: > 0 })
                return readings;
            nextEvent = enumerator.MoveNextAsync().AsTask();
        }
    }

    private static IReadOnlyDictionary<string, double>? TryMapReadings(
        DeviceEvent deviceEvent,
        IReadOnlyDictionary<int, string> electrodeByChannel,
        ImpedanceDetectionKind kind
    )
    {
        var readings = deviceEvent switch
        {
            EegImpedanceReceivedEvent eeg when kind == ImpedanceDetectionKind.Eeg => eeg.Readings,
            StimulationImpedanceReceivedEvent stimulation
                when kind == ImpedanceDetectionKind.Stimulation => stimulation.Readings,
            _ => null,
        };
        return readings
            ?.Where(reading =>
                electrodeByChannel.ContainsKey(reading.PhysicalChannel)
                && (kind != ImpedanceDetectionKind.Eeg || reading.Band != ImpedanceBand.Disabled)
            )
            .ToDictionary(
                reading => electrodeByChannel[reading.PhysicalChannel],
                reading => ToKiloOhms(reading.Band),
                StringComparer.OrdinalIgnoreCase
            );
    }

    private IEggtCsDevice GetDevice(string deviceId) =>
        deviceManager.TryGet(new DeviceId(deviceId), out var device)
        && device is not null
        && device.State.Connection == DeviceConnectionState.Connected
            ? device
            : throw new InvalidOperationException($"Device '{deviceId}' is not connected.");

    private IReadOnlyDictionary<string, int> ResolveEegChannels(
        IEggtCsDevice device,
        IReadOnlyList<string> electrodeIds
    )
    {
        var mappingSnapshot = eegChannelMappings.Load();
        if (!mappingSnapshot.IsValid)
            throw new ImpedanceDetectionConfigurationException(
                $"EEG采集物理通道配置无效：{string.Join("；", mappingSnapshot.ValidationErrors)}"
            );
        var configured = mappingSnapshot
            .Mappings.Where(mapping => mapping.PhysicalChannel is not null)
            .ToDictionary(
                mapping => mapping.ElectrodeId,
                mapping => mapping.PhysicalChannel!.Value,
                StringComparer.OrdinalIgnoreCase
            );
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedChannels = new HashSet<int>();
        foreach (var electrodeId in electrodeIds)
        {
            if (configured.TryGetValue(electrodeId, out var channel))
            {
                result[electrodeId] = channel;
                if (!usedChannels.Add(channel))
                    throw new ImpedanceDetectionConfigurationException(
                        $"EEG 物理通道 CH{channel} 被重复映射，请检查电极物理通道配置。"
                    );
            }
            else if (UsesSimulatedImpedance(device, ImpedanceDetectionKind.Eeg))
            {
                var simulatedChannel = Enumerable
                    .Range(1, device.Capabilities.MaximumEegChannels)
                    .FirstOrDefault(candidate => !usedChannels.Contains(candidate));
                if (simulatedChannel == 0)
                    throw new InvalidOperationException(
                        "No simulated EEG physical channel is available."
                    );
                result[electrodeId] = simulatedChannel;
                usedChannels.Add(simulatedChannel);
            }
            else
                throw new ImpedanceDetectionConfigurationException(
                    $"EEG 电极“{electrodeId}”未配置物理采集通道，请先完成电极物理通道映射。"
                );
        }
        return result;
    }

    private static StimulationConfiguration CreateStimulationConfiguration(
        IEggtCsDevice device,
        StimulationImpedanceDetectionRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = DeviceStimulationConfigurationMapper.CreateForImpedance(
            device,
            request.Configuration,
            request.Assignments
        );
        if (!UsesSimulatedImpedance(device, ImpedanceDetectionKind.Stimulation))
        {
            if (configuration.TargetGroups.Count != 1)
                throw new ImpedanceDetectionConfigurationException(
                    "当前设备仅支持单靶点刺激阻抗检测，请调整刺激阵列后重试。"
                );
            var selectableCount = configuration
                .TargetGroups[0]
                .Channels.Count(channel => channel.Role == DeviceStimulationChannelRole.Selectable);
            if (selectableCount is < 1 or > 4)
                throw new ImpedanceDetectionConfigurationException(
                    "当前设备要求配置 1 至 4 个自选刺激通道。"
                );
        }
        return configuration;
    }

    private static bool UsesSimulatedImpedance(IEggtCsDevice device, ImpedanceDetectionKind kind)
    {
        var capability =
            kind == ImpedanceDetectionKind.Stimulation
                ? DeviceCapabilityKind.StimulationImpedance
                : DeviceCapabilityKind.EegImpedance;
        return device.UsesCapabilitySource(capability, DeviceCapabilitySource.Simulated);
    }

    private async Task<DeviceCommandResult> StartWithHeartbeatPausedAsync(
        IEggtCsDevice device,
        ImpedanceDetectionKind kind,
        Func<Task<DeviceCommandResult>> start
    )
    {
        var registration = PauseHeartbeat(device, kind);
        try
        {
            var result = await start().ConfigureAwait(false);
            if (!result.IsSuccess)
                ResumeHeartbeat(registration);
            return result;
        }
        catch
        {
            ResumeHeartbeat(registration);
            throw;
        }
    }

    private async Task<DeviceCommandResult> StopAndResumeHeartbeatAsync(
        IEggtCsDevice device,
        ImpedanceDetectionKind kind,
        Func<Task<DeviceCommandResult>> stop
    )
    {
        try
        {
            return await stop().ConfigureAwait(false);
        }
        finally
        {
            ResumeHeartbeat(device.Identity.DeviceId, kind);
        }
    }

    private HeartbeatPauseRegistration? PauseHeartbeat(
        IEggtCsDevice device,
        ImpedanceDetectionKind kind
    )
    {
        if (heartbeatPause is null || UsesSimulatedImpedance(device, kind))
            return null;

        var key = (device.Identity.DeviceId, kind);
        lock (_heartbeatPauseGate)
        {
            if (_heartbeatPauses.ContainsKey(key))
                return null;

            var registration = new HeartbeatPauseRegistration(
                key,
                heartbeatPause.Pause(device.Identity.DeviceId)
            );
            _heartbeatPauses.Add(key, registration);
            return registration;
        }
    }

    private void ResumeHeartbeat(HeartbeatPauseRegistration? registration)
    {
        if (registration is null)
            return;

        IDisposable? pause = null;
        lock (_heartbeatPauseGate)
        {
            if (
                _heartbeatPauses.TryGetValue(registration.Key, out var current)
                && ReferenceEquals(current, registration)
            )
            {
                _heartbeatPauses.Remove(registration.Key);
                pause = registration.Pause;
            }
        }
        pause?.Dispose();
    }

    private void ResumeHeartbeat(DeviceId deviceId, ImpedanceDetectionKind kind)
    {
        IDisposable? pause = null;
        lock (_heartbeatPauseGate)
        {
            if (_heartbeatPauses.Remove((deviceId, kind), out var registration))
                pause = registration.Pause;
        }
        pause?.Dispose();
    }

    private sealed record HeartbeatPauseRegistration(
        (DeviceId DeviceId, ImpedanceDetectionKind Kind) Key,
        IDisposable Pause
    );

    private static void EnsureSuccess(DeviceCommandResult result, string operation)
    {
        if (!result.IsSuccess)
            throw new DeviceCommandRejectedException(operation, result);
    }

    private static double ToKiloOhms(ImpedanceBand band) =>
        band switch
        {
            ImpedanceBand.Disabled => 0d,
            ImpedanceBand.UpTo10KOhms or ImpedanceBand.Normal => 8d,
            ImpedanceBand.UpTo20KOhms => 15d,
            ImpedanceBand.UpTo30KOhms => 25d,
            ImpedanceBand.UpTo40KOhms => 35d,
            ImpedanceBand.Above40KOhms or ImpedanceBand.Abnormal => 45d,
            _ => throw new ArgumentOutOfRangeException(nameof(band)),
        };
}
