using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Services;

public sealed class DeviceExperimentRunService : IExperimentRunService, IDisposable
{
    private enum OperationKind
    {
        Acquisition,
        Stimulation,
    }

    private sealed record PendingEegPacket(
        DeviceEventEnvelope Envelope,
        EegDataPacketReceivedEvent Packet
    );

    private sealed class OperationContext(
        IEggtCsDevice device,
        OperationKind kind,
        TimeSpan timelineOffset,
        IReadOnlyDictionary<int, string> channelNames,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int sampleRateHz,
        bool isPhysicalAcquisition,
        EegAcquisitionOptions acquisitionOptions,
        EegPacketStatisticsAccumulator packetStatistics,
        int currentCycle = 1,
        int totalCycles = 1,
        Guid recordingId = default
    )
    {
        public IEggtCsDevice Device { get; } = device;
        public TaskCompletionSource<Exception> Failure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _dataGate = new();
        private readonly object _progressGate = new();
        private readonly object _stopGate = new();
        private TaskCompletionSource _dataPulse = NewSignal();
        private TaskCompletionSource _progressPulse = NewSignal();
        private DateTimeOffset? _lastDataReceivedAt;
        private int _dataPacketCount;
        private DateTimeOffset? _lastProgressReceivedAt;
        private Task<DeviceCommandResult>? _stopTask;
        private int _acceptingPackets = 1;

        public OperationKind Kind { get; } = kind;
        public TimeSpan TimelineOffset { get; } = timelineOffset;
        public IReadOnlyDictionary<int, string> ChannelNames { get; } = channelNames;
        public DateTimeOffset StartedAt { get; private set; } = startedAt;
        public DateTimeOffset AcquisitionCommandSentAtUtc => StartedAt;
        public TimeSpan Duration { get; } = duration;
        public int SampleRateHz { get; } = sampleRateHz;
        public EegSampleTimeline SampleTimeline { get; } = new(sampleRateHz);
        public EegPacketStatisticsAccumulator PacketStatistics { get; } = packetStatistics;
        public EegPacketReorderBuffer<PendingEegPacket> PacketReorderer { get; } =
            new(
                isPhysicalAcquisition && acquisitionOptions.EnablePacketReordering,
                acquisitionOptions.PacketReorderTimeout,
                acquisitionOptions.PacketReorderWindowPackets,
                packetStatistics
            );
        public SemaphoreSlim PacketProcessingGate { get; } = new(1, 1);
        public bool IsPhysicalAcquisition { get; } = isPhysicalAcquisition;
        public int CurrentCycle { get; } = currentCycle;
        public int TotalCycles { get; } = totalCycles;
        public Guid RecordingId { get; } = recordingId;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DeviceProgressEvent? LastProgress { get; set; }
        public DeviceProgressEvent? LastDeviceProgress { get; set; }
        public DateTimeOffset? CompletionReceivedAt { get; set; }

        public DateTimeOffset WindowEndsAt => StartedAt + Duration;

        public bool IsAcceptingPackets => Volatile.Read(ref _acceptingPackets) != 0;

        public void StopAcceptingPackets() => Interlocked.Exchange(ref _acceptingPackets, 0);

        public void MarkAcquisitionCommandSent(DateTimeOffset sentAtUtc)
        {
            if (Kind != OperationKind.Acquisition)
                throw new InvalidOperationException(
                    "Only an EEG acquisition has an acquisition command timestamp."
                );
            StartedAt = sentAtUtc;
        }

        public (DateTimeOffset? LastDataReceivedAt, int PacketCount, Task Pulse) GetDataState()
        {
            lock (_dataGate)
                return (_lastDataReceivedAt, _dataPacketCount, _dataPulse.Task);
        }

        public void MarkDataReceived(DateTimeOffset receivedAt)
        {
            TaskCompletionSource pulse;
            lock (_dataGate)
            {
                _dataPacketCount++;
                if (_lastDataReceivedAt is null || receivedAt > _lastDataReceivedAt)
                    _lastDataReceivedAt = receivedAt;
                pulse = _dataPulse;
                _dataPulse = NewSignal();
            }
            pulse.TrySetResult();
        }

        public (DateTimeOffset? LastProgressReceivedAt, Task Pulse) GetProgressState()
        {
            lock (_progressGate)
                return (_lastProgressReceivedAt, _progressPulse.Task);
        }

        public void MarkProgressReceived(DateTimeOffset receivedAt)
        {
            TaskCompletionSource pulse;
            lock (_progressGate)
            {
                if (_lastProgressReceivedAt is null || receivedAt > _lastProgressReceivedAt)
                    _lastProgressReceivedAt = receivedAt;
                pulse = _progressPulse;
                _progressPulse = NewSignal();
            }
            pulse.TrySetResult();
        }

        public Task<DeviceCommandResult> StopOnceAsync(Func<Task<DeviceCommandResult>> stop)
        {
            lock (_stopGate)
                return _stopTask ??= stop();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IDeviceManager _deviceManager;
    private readonly IEegPhysicalChannelMappingService _eegChannelMappings;
    private readonly IExperimentRunClock _clock;
    private readonly EegAcquisitionOptions _eegAcquisitionOptions;
    private readonly StimulationRunOptions _stimulationRunOptions;
    private readonly IDeviceHeartbeatPauseService? _heartbeatPause;
    private readonly IEegRawPacketRecorder? _rawPacketRecorder;
    private readonly ConcurrentDictionary<DeviceId, OperationContext> _operations = new();
    private readonly ConcurrentDictionary<DeviceId, OperationContext> _closingAcquisitions = new();

    private sealed class EventPump(IEggtCsDevice device)
    {
        public IEggtCsDevice Device { get; } = device;
        public TaskCompletionSource Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<DeviceId, EventPump> _eventPumps = new();
    private readonly object _eventPumpGate = new();
    private readonly ConcurrentDictionary<
        DeviceId,
        CancellationTokenSource
    > _automaticCancellations = new();
    private readonly ConcurrentDictionary<Guid, long> _recordingSequences = new();
    private readonly ConcurrentDictionary<Guid, EegPacketStatisticsAccumulator> _packetStatistics =
        new();
    private readonly SemaphoreSlim _rawRecordingLifecycleGate = new(1, 1);
    private readonly object _recordingCompletionGate = new();
    private readonly Dictionary<Guid, Task<EegRecordingCompletionResult>> _recordingCompletions =
    [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _disposed;

    public DeviceExperimentRunService(
        IDeviceManager deviceManager,
        IEegPhysicalChannelMappingService eegChannelMappings,
        IExperimentRunClock? clock = null,
        IOptions<EegAcquisitionOptions>? eegAcquisitionOptions = null,
        IOptions<StimulationRunOptions>? stimulationRunOptions = null,
        IDeviceHeartbeatPauseService? heartbeatPause = null,
        IEegRawPacketRecorder? rawPacketRecorder = null
    )
    {
        _deviceManager = deviceManager;
        _eegChannelMappings = eegChannelMappings;
        _clock = clock ?? new SystemExperimentRunClock();
        _eegAcquisitionOptions = eegAcquisitionOptions?.Value ?? new EegAcquisitionOptions();
        _stimulationRunOptions = stimulationRunOptions?.Value ?? new StimulationRunOptions();
        _heartbeatPause = heartbeatPause;
        _rawPacketRecorder = rawPacketRecorder;
        if (_rawPacketRecorder is not null)
            _rawPacketRecorder.RecordingFailed += OnRawRecordingFailed;
    }

    public event EventHandler<ExperimentRunTelemetryEventArgs>? TelemetryReceived;

    public async Task BeginRecordingAsync(
        EegRecordingMetadata metadata,
        CancellationToken cancellationToken = default
    )
    {
        await _rawRecordingLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rawPacketRecorder is not null)
                await _rawPacketRecorder
                    .BeginAsync(metadata, cancellationToken)
                    .ConfigureAwait(false);
            _recordingSequences[metadata.RecordingId] = 0;
            _packetStatistics[metadata.RecordingId] = new EegPacketStatisticsAccumulator(
                _eegAcquisitionOptions.EnablePacketReordering
            );
        }
        finally
        {
            _rawRecordingLifecycleGate.Release();
        }
    }

    public async Task RecordDisplayFilterChangeAsync(
        EegFilterChangeRecord change,
        CancellationToken cancellationToken = default
    )
    {
        await _rawRecordingLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (
                _rawPacketRecorder is not null
                && _recordingSequences.ContainsKey(change.RecordingId)
            )
                await _rawPacketRecorder
                    .AppendFilterChangeAsync(change, cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            _rawRecordingLifecycleGate.Release();
        }
    }

    public Task<EegRecordingCompletionResult> CompleteRecordingAsync(
        Guid recordingId,
        EegRecordingCompletionStatus status,
        CancellationToken cancellationToken = default
    )
    {
        Task<EegRecordingCompletionResult> completion;
        lock (_recordingCompletionGate)
        {
            if (!_recordingCompletions.TryGetValue(recordingId, out completion!))
            {
                completion = CompleteRecordingCoreAsync(recordingId, status);
                _recordingCompletions.Add(recordingId, completion);
            }
        }
        // Cancellation stops the caller's wait, not the writer's drain/finalization.
        return completion.WaitAsync(cancellationToken);
    }

    private async Task<EegRecordingCompletionResult> CompleteRecordingCoreAsync(
        Guid recordingId,
        EegRecordingCompletionStatus status
    )
    {
        if (recordingId == Guid.Empty)
            return new(EegPacketStatistics.Empty(_eegAcquisitionOptions.EnablePacketReordering));
        foreach (var pair in _operations.ToArray())
        {
            var active = pair.Value;
            if (
                active.Kind != OperationKind.Acquisition
                || active.RecordingId != recordingId
                || !RemoveOperation(active)
            )
                continue;
            active.StopAcceptingPackets();
            await FlushAllEegPacketsAsync(active).ConfigureAwait(false);
        }
        foreach (var pair in _closingAcquisitions.ToArray())
        {
            var closing = pair.Value;
            if (
                closing.RecordingId != recordingId
                || !(
                    (ICollection<KeyValuePair<DeviceId, OperationContext>>)_closingAcquisitions
                ).Remove(pair)
            )
                continue;
            closing.StopAcceptingPackets();
            await FlushAllEegPacketsAsync(closing).ConfigureAwait(false);
        }
        var statistics = _packetStatistics.TryGetValue(recordingId, out var accumulator)
            ? accumulator.Snapshot()
            : EegPacketStatistics.Empty(_eegAcquisitionOptions.EnablePacketReordering);
        await _rawRecordingLifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Stop admitting packets through a closing acquisition before placing the completion
            // marker in the writer queue. Packets already admitted hold this gate and are therefore
            // guaranteed to be queued before completion.
            EegRecordingSummary? summary = null;
            if (_rawPacketRecorder is not null && _recordingSequences.ContainsKey(recordingId))
                summary = await _rawPacketRecorder
                    .CompleteAsync(recordingId, status)
                    .ConfigureAwait(false);
            return new(statistics, summary?.DataEndExclusiveSeconds);
        }
        finally
        {
            _recordingSequences.TryRemove(recordingId, out _);
            _packetStatistics.TryRemove(recordingId, out _);
            _rawRecordingLifecycleGate.Release();
        }
    }

    public async Task StartAcquisitionAsync(
        AcquisitionRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var device = GetDevice(request.DeviceId);
        var capability =
            device.EegAcquisition
            ?? throw new NotSupportedException(
                $"Device {request.DeviceId} does not support EEG acquisition."
            );
        var channels = ResolveChannels(device, request.ChannelIds);
        using var heartbeatPause = device.UsesCapabilitySource(
            DeviceCapabilityKind.EegAcquisition,
            DeviceCapabilitySource.Real
        )
            ? _heartbeatPause?.Pause(device.Identity.DeviceId)
            : null;
        var context = BeginOperation(
            device,
            OperationKind.Acquisition,
            request.TimelineOffset,
            channels.ToDictionary(item => item.Value, item => item.Key),
            request.Duration,
            request.SampleRateHz,
            request.CurrentCycle,
            request.TotalCycles,
            request.RecordingId
        );
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var progressTask = Task.CompletedTask;
        var completedNormally = false;
        var startAcknowledged = false;
        try
        {
            await EnsureEventPumpAsync(device, context, operationCancellation.Token)
                .ConfigureAwait(false);
            context.MarkAcquisitionCommandSent(_clock.UtcNow);
            var startTask = capability.StartAsync(
                request.Duration,
                channels.Values.ToHashSet(),
                request.SampleRateHz,
                operationCancellation.Token
            );
            progressTask = RunLocalAcquisitionProgressAsync(
                device.Identity.DeviceId,
                context,
                operationCancellation.Token
            );
            DeviceCommandResult result;
            try
            {
                await WaitForOperationAsync(context, startTask, operationCancellation.Token)
                    .ConfigureAwait(false);
                result = await startTask.ConfigureAwait(false);
            }
            catch (RequestTimeoutException exception)
            {
                throw new EegAcquisitionException(
                    EegAcquisitionFailureKind.StartResponseTimeout,
                    $"已发送 EEG 开始采集指令，但在 {FormatDuration(exception.Timeout)} 内未收到成功响应。",
                    exception.Timeout,
                    innerException: exception
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new EegAcquisitionException(
                    EegAcquisitionFailureKind.StartFailed,
                    $"EEG 采集启动通信失败：{exception.Message}",
                    innerException: exception
                );
            }
            if (!result.IsSuccess)
            {
                throw new EegAcquisitionException(
                    EegAcquisitionFailureKind.StartRejected,
                    $"设备拒绝开始 EEG 采集：{result.Status}。{result.Message}"
                );
            }

            startAcknowledged = true;
            var acquisitionWindow = WaitForAcquisitionWindowAsync(
                device,
                context,
                operationCancellation.Token
            );
            await WaitForOperationAsync(context, acquisitionWindow, operationCancellation.Token)
                .ConfigureAwait(false);
            completedNormally = await acquisitionWindow.ConfigureAwait(false);
        }
        catch (EegAcquisitionException exception)
        {
            operationCancellation.Cancel();
            throw await AttachStopResultAsync(capability, context, exception).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested
            )
        {
            operationCancellation.Cancel();
            var failure = new EegAcquisitionException(
                EegAcquisitionFailureKind.StartFailed,
                startAcknowledged
                    ? $"EEG 采集过程中发生通信错误：{exception.Message}"
                    : $"EEG 采集启动通信失败：{exception.Message}",
                innerException: exception
            );
            throw await AttachStopResultAsync(capability, context, failure).ConfigureAwait(false);
        }
        finally
        {
            operationCancellation.Cancel();
            try
            {
                if (!completedNormally)
                    context.StopAcceptingPackets();
                await ObserveProgressTaskAsync(progressTask).ConfigureAwait(false);
                if (startAcknowledged)
                    await FlushAllEegPacketsAsync(context).ConfigureAwait(false);
                if (
                    completedNormally
                    && context.IsAcceptingPackets
                    && _deviceManager.TryGet(device.Identity.DeviceId, out var current)
                    && ReferenceEquals(current, device)
                )
                    PreserveClosingAcquisition(device.Identity.DeviceId, context);
            }
            finally
            {
                RemoveOperation(context);
            }
        }
    }

    public async Task StartStimulationAsync(
        StimulationRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var device = GetDevice(request.DeviceId);
        var capability =
            device.Stimulation
            ?? throw new NotSupportedException(
                $"Device {request.DeviceId} does not support stimulation."
            );
        if (
            device.UsesCapabilitySource(
                DeviceCapabilityKind.Stimulation,
                DeviceCapabilitySource.Real
            )
            && (
                request.Duration <= TimeSpan.Zero
                || request.Duration.TotalSeconds > ushort.MaxValue
                || request.Duration.TotalSeconds != Math.Truncate(request.Duration.TotalSeconds)
            )
        )
        {
            throw new StimulationException(
                StimulationFailureKind.StartFailed,
                "真实设备刺激时长必须为 1～65535 的整数秒。"
            );
        }
        using var heartbeatPause = device.UsesCapabilitySource(
            DeviceCapabilityKind.Stimulation,
            DeviceCapabilitySource.Real
        )
            ? _heartbeatPause?.Pause(device.Identity.DeviceId)
            : null;
        try
        {
            var configuration = CreateStimulationConfiguration(device, request);
            var configurationResult = await capability
                .ConfigureAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
            if (!configurationResult.IsSuccess)
            {
                throw new StimulationException(
                    StimulationFailureKind.ConfigurationFailed,
                    $"设备拒绝下发刺激参数：{configurationResult.Status}。{configurationResult.Message}"
                );
            }
        }
        catch (StimulationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new StimulationException(
                StimulationFailureKind.ConfigurationFailed,
                $"刺激参数下发失败：{exception.Message}",
                innerException: exception
            );
        }

        var context = BeginOperation(
            device,
            OperationKind.Stimulation,
            request.TimelineOffset,
            new Dictionary<int, string>(),
            request.Duration,
            request.SampleRateHz,
            request.CurrentCycle,
            request.TotalCycles,
            request.RecordingId
        );
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var progressTask = Task.CompletedTask;
        var startCommandSent = false;
        var startAcknowledged = false;
        try
        {
            await EnsureEventPumpAsync(device, context, operationCancellation.Token)
                .ConfigureAwait(false);
            startCommandSent = true;
            var startTask = capability.StartAsync(request.Duration, operationCancellation.Token);
            if (
                device.UsesCapabilitySource(
                    DeviceCapabilityKind.Stimulation,
                    DeviceCapabilitySource.Real
                )
            )
            {
                progressTask = RunLocalStimulationProgressAsync(
                    device.Identity.DeviceId,
                    context,
                    operationCancellation.Token
                );
            }

            DeviceCommandResult result;
            try
            {
                await WaitForOperationAsync(context, startTask, operationCancellation.Token)
                    .ConfigureAwait(false);
                result = await startTask.ConfigureAwait(false);
            }
            catch (RequestTimeoutException exception)
            {
                throw new StimulationException(
                    StimulationFailureKind.StartResponseTimeout,
                    $"已发送刺激启动指令，但在 {FormatDuration(exception.Timeout)} 内未收到成功响应。",
                    exception.Timeout,
                    innerException: exception
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new StimulationException(
                    StimulationFailureKind.StartFailed,
                    $"刺激启动通信失败：{exception.Message}",
                    innerException: exception
                );
            }
            if (!result.IsSuccess)
            {
                throw new StimulationException(
                    StimulationFailureKind.StartRejected,
                    $"设备拒绝开始刺激：{result.Status}。{result.Message}"
                );
            }

            startAcknowledged = true;
            if (
                device.UsesCapabilitySource(
                    DeviceCapabilityKind.Stimulation,
                    DeviceCapabilitySource.Real
                )
            )
            {
                await WaitForOperationAsync(
                        context,
                        WaitForPhysicalStimulationAsync(
                            context,
                            _clock.UtcNow,
                            operationCancellation.Token
                        ),
                        operationCancellation.Token
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                await WaitForOperationAsync(
                        context,
                        context.Completion.Task,
                        operationCancellation.Token
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (StimulationException exception)
        {
            operationCancellation.Cancel();
            throw startCommandSent
                ? await AttachStimulationStopResultAsync(capability, context, exception)
                    .ConfigureAwait(false)
                : exception;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            if (startCommandSent)
                await StopStimulationOnceAsync(context, capability, _lifetimeCancellation.Token)
                    .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            operationCancellation.Cancel();
            var failure = new StimulationException(
                StimulationFailureKind.StartFailed,
                startAcknowledged
                    ? $"刺激过程中发生通信错误：{exception.Message}"
                    : $"刺激启动通信失败：{exception.Message}",
                innerException: exception
            );
            throw startCommandSent
                ? await AttachStimulationStopResultAsync(capability, context, failure)
                    .ConfigureAwait(false)
                : failure;
        }
        finally
        {
            operationCancellation.Cancel();
            try
            {
                await ObserveProgressTaskAsync(progressTask).ConfigureAwait(false);
            }
            finally
            {
                RemoveOperation(context);
            }
        }
    }

    public async Task StartAutomaticExperimentAsync(
        AutomaticExperimentRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var device = GetDevice(request.DeviceId);
        _ =
            device.EegAcquisition
            ?? throw new NotSupportedException(
                $"Device {request.DeviceId} does not support EEG acquisition."
            );
        var acquisitionOnly = request.CreationMode == ExperimentCreationMode.AcquisitionOnly;
        if (!acquisitionOnly)
            _ =
                device.Stimulation
                ?? throw new NotSupportedException(
                    $"Device {request.DeviceId} does not support stimulation."
                );
        if (request.CycleCount is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(request.CycleCount));
        using var automaticCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        if (!_automaticCancellations.TryAdd(device.Identity.DeviceId, automaticCancellation))
            throw new InvalidOperationException(
                $"Device {request.DeviceId} already has an automatic experiment."
            );
        var cycleDuration =
            request.AcquisitionDuration
            + request.BlankingDuration
            + (
                acquisitionOnly
                    ? TimeSpan.Zero
                    : request.StimulationDuration + request.RecoveryDuration
            );
        var currentCycle = 1;
        try
        {
            while (request.CycleCount == 0 || currentCycle <= request.CycleCount)
            {
                automaticCancellation.Token.ThrowIfCancellationRequested();
                var cycleOffset = TimeSpan.FromTicks(cycleDuration.Ticks * (currentCycle - 1L));
                var totalCycles = request.CycleCount;
                await StartAcquisitionAsync(
                        new AcquisitionRunRequest(
                            request.AcquisitionDuration,
                            request.ChannelIds,
                            request.SampleRateHz,
                            request.DeviceId,
                            cycleOffset,
                            currentCycle,
                            totalCycles,
                            request.RecordingId
                        ),
                        automaticCancellation.Token
                    )
                    .ConfigureAwait(false);
                var blankingOffset = cycleOffset + request.AcquisitionDuration;
                await RunLocalAutomaticStageAsync(
                        device.Identity.DeviceId,
                        ExperimentRunStage.Blanking,
                        request.BlankingDuration,
                        blankingOffset,
                        currentCycle,
                        totalCycles,
                        automaticCancellation.Token
                    )
                    .ConfigureAwait(false);
                if (acquisitionOnly)
                {
                    currentCycle++;
                    continue;
                }
                var stimulationOffset = blankingOffset + request.BlankingDuration;
                await StartStimulationAsync(
                        new StimulationRunRequest(
                            request.StimulationDuration,
                            request.TargetCurrentMilliAmps,
                            stimulationOffset,
                            request.ChannelIds,
                            request.SampleRateHz,
                            request.StimulusConfiguration,
                            request.StimulusElectrodes,
                            request.DeviceId,
                            currentCycle,
                            totalCycles,
                            request.RecordingId
                        ),
                        automaticCancellation.Token
                    )
                    .ConfigureAwait(false);
                var recoveryOffset = stimulationOffset + request.StimulationDuration;
                await RunLocalAutomaticStageAsync(
                        device.Identity.DeviceId,
                        ExperimentRunStage.Recovery,
                        request.RecoveryDuration,
                        recoveryOffset,
                        currentCycle,
                        totalCycles,
                        automaticCancellation.Token
                    )
                    .ConfigureAwait(false);
                currentCycle++;
            }
        }
        catch (OperationCanceledException)
            when (automaticCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            ) { }
        finally
        {
            (
                (ICollection<KeyValuePair<DeviceId, CancellationTokenSource>>)
                    _automaticCancellations
            ).Remove(new(device.Identity.DeviceId, automaticCancellation));
        }
    }

    public async Task StopCurrentOperationAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        var device = GetDevice(deviceId);
        var context = GetOperation(device);
        var stimulationIsActive =
            device.State.Operation
                is DeviceOperationState.Stimulating
                    or DeviceOperationState.Stopping
            || context?.Kind == OperationKind.Stimulation;
        if (stimulationIsActive && device.Stimulation is not null)
        {
            var result = await StopStimulationOnceAsync(
                    context,
                    device.Stimulation,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!IsStimulationStopAccepted(result))
                EnsureSuccess(result, "stop stimulation");
            CompleteOperation(context);
            return;
        }

        switch (device.State.Operation)
        {
            case DeviceOperationState.Acquiring when device.EegAcquisition is not null:
                EnsureSuccess(
                    await StopAcquisitionOnceAsync(
                            context,
                            device.EegAcquisition,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                    "stop acquisition"
                );
                break;
        }
        CompleteOperation(context);
    }

    public async Task EmergencyStopAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        var device = GetDevice(deviceId);
        var context = GetOperation(device);
        if (
            _automaticCancellations.TryGetValue(
                device.Identity.DeviceId,
                out var automaticCancellation
            )
        )
            automaticCancellation.Cancel();
        var stimulationIsActive =
            device.State.Operation
                is DeviceOperationState.Stimulating
                    or DeviceOperationState.Stopping
            || context?.Kind == OperationKind.Stimulation;
        if (stimulationIsActive && device.Stimulation is not null)
        {
            var result = await StopStimulationOnceAsync(
                    context,
                    device.Stimulation,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!IsStimulationStopAccepted(result))
                EnsureSuccess(result, "emergency stop stimulation");
            CompleteOperation(context);
            return;
        }

        var acquisitionIsActive =
            device.State.Operation == DeviceOperationState.Acquiring
            || context?.Kind == OperationKind.Acquisition;
        if (acquisitionIsActive && device.EegAcquisition is not null)
        {
            EnsureSuccess(
                await StopAcquisitionOnceAsync(context, device.EegAcquisition, cancellationToken)
                    .ConfigureAwait(false),
                "stop acquisition"
            );
        }
        CompleteOperation(context);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_rawPacketRecorder is not null)
            _rawPacketRecorder.RecordingFailed -= OnRawRecordingFailed;
        _lifetimeCancellation.Cancel();
        foreach (var cancellation in _automaticCancellations.Values)
            cancellation.Cancel();
        _automaticCancellations.Clear();
        foreach (var context in _operations.Values)
            context.Completion.TrySetCanceled();
        _operations.Clear();
        _closingAcquisitions.Clear();
        _lifetimeCancellation.Dispose();
    }

    private OperationContext BeginOperation(
        IEggtCsDevice device,
        OperationKind kind,
        TimeSpan timelineOffset,
        IReadOnlyDictionary<int, string> channelNames,
        TimeSpan duration,
        int sampleRateHz,
        int currentCycle = 1,
        int totalCycles = 1,
        Guid recordingId = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (kind == OperationKind.Acquisition)
            _closingAcquisitions.TryRemove(device.Identity.DeviceId, out _);
        var context = new OperationContext(
            device,
            kind,
            timelineOffset,
            channelNames,
            _clock.UtcNow,
            duration,
            sampleRateHz,
            kind == OperationKind.Acquisition
                && device.UsesCapabilitySource(
                    DeviceCapabilityKind.EegAcquisition,
                    DeviceCapabilitySource.Real
                ),
            _eegAcquisitionOptions,
            recordingId != Guid.Empty
                ? _packetStatistics.GetOrAdd(
                    recordingId,
                    _ => new EegPacketStatisticsAccumulator(
                        _eegAcquisitionOptions.EnablePacketReordering
                    )
                )
                : new EegPacketStatisticsAccumulator(
                    kind == OperationKind.Acquisition
                        && device.UsesCapabilitySource(
                            DeviceCapabilityKind.EegAcquisition,
                            DeviceCapabilitySource.Real
                        )
                        && _eegAcquisitionOptions.EnablePacketReordering
                ),
            currentCycle,
            totalCycles,
            recordingId
        );
        if (!_operations.TryAdd(device.Identity.DeviceId, context))
            throw new InvalidOperationException(
                $"Device {device.Identity.DeviceId} already has an active operation."
            );
        return context;
    }

    private async Task EnsureEventPumpAsync(
        IEggtCsDevice device,
        OperationContext context,
        CancellationToken cancellationToken
    )
    {
        var deviceId = device.Identity.DeviceId;
        EventPump pump;
        lock (_eventPumpGate)
        {
            if (
                _eventPumps.TryGetValue(deviceId, out var existing)
                && ReferenceEquals(existing.Device, device)
            )
                pump = existing;
            else
            {
                pump = new(device);
                _eventPumps[deviceId] = pump;
                _ = PumpEventsAsync(pump, _lifetimeCancellation.Token);
            }
        }
        await WaitForOperationAsync(context, pump.Ready.Task, cancellationToken)
            .ConfigureAwait(false);
        if (pump.Ended.Task.IsCompletedSuccessfully)
            throw await pump.Ended.Task.ConfigureAwait(false);
        if (device.State.Connection != DeviceConnectionState.Connected)
            throw new DeviceDisconnectedException(Guid.Empty);
    }

    private async Task PumpEventsAsync(EventPump pump, CancellationToken cancellationToken)
    {
        var device = pump.Device;
        Exception terminal = new DeviceDisconnectedException(Guid.Empty);
        try
        {
            await using var subscription = device.SubscribeEvents(
                new() { DataPolicy = DeviceDataDeliveryPolicy.Reliable }
            );
            await subscription.Ready.ConfigureAwait(false);
            pump.Ready.TrySetResult();
            await foreach (
                var envelope in subscription
                    .ReadEventsAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                _operations.TryGetValue(envelope.DeviceId, out var activeContext);
                if (activeContext is not null && !ReferenceEquals(activeContext.Device, device))
                    activeContext = null;
                switch (envelope.Event)
                {
                    case DeviceStateChangedEvent changed
                        when changed.State.Connection != DeviceConnectionState.Connected
                            || changed.State.Operation == DeviceOperationState.Faulted:
                        activeContext?.Failure.TrySetResult(
                            changed.State.Fault is { Length: > 0 } fault
                                ? new TransportCommunicationException(fault)
                                : new DeviceDisconnectedException(envelope.SessionId)
                        );
                        break;
                    case DeviceProgressEvent progress when activeContext is not null:
                        if (activeContext.Kind == OperationKind.Acquisition)
                        {
                            // Acquisition progress is driven exclusively by the local high-resolution clock.
                            // Device progress and EEG packets must never race it or regress the UI timeline.
                            activeContext.LastDeviceProgress = progress;
                            break;
                        }
                        if (
                            activeContext.Kind == OperationKind.Stimulation
                            && device.UsesCapabilitySource(
                                DeviceCapabilityKind.Stimulation,
                                DeviceCapabilitySource.Real
                            )
                        )
                        {
                            activeContext.MarkProgressReceived(envelope.Timestamp);
                            break;
                        }
                        activeContext.LastProgress = progress;
                        Publish(envelope.DeviceId, CreateTelemetry(activeContext, progress, []));
                        break;
                    case EegSamplesReceivedEvent samples
                        when activeContext?.Kind == OperationKind.Acquisition
                            && activeContext.IsAcceptingPackets:
                        await RecordSimulatedSamplesAsync(envelope, samples, activeContext)
                            .ConfigureAwait(false);
                        var batches = samples
                            .Channels.Select(channel => new WaveformChannelBatch(
                                activeContext.ChannelNames.TryGetValue(
                                    channel.PhysicalChannel,
                                    out var name
                                )
                                    ? name
                                    : $"CH{channel.PhysicalChannel}",
                                channel.StartTimeSeconds
                                    + activeContext.TimelineOffset.TotalSeconds,
                                channel.SampleIntervalSeconds,
                                channel.Samples
                            ))
                            .ToArray();
                        var latest =
                            activeContext.LastProgress
                            ?? new DeviceProgressEvent(
                                DeviceOperationState.Acquiring,
                                TimeSpan.Zero,
                                TimeSpan.Zero,
                                0d
                            );
                        Publish(
                            envelope.DeviceId,
                            CreateTelemetry(activeContext, latest, batches) with
                            {
                                IsWaveformOnly = true,
                            }
                        );
                        break;
                    case EegDataPacketReceivedEvent packet:
                        var acquisitionContext =
                            activeContext?.Kind == OperationKind.Acquisition
                                ? activeContext
                                : GetClosingAcquisition(envelope.DeviceId);
                        if (
                            acquisitionContext is not null
                            && ReferenceEquals(acquisitionContext.Device, device)
                        )
                        {
                            // A valid physical frame is sufficient to keep the stream watchdog alive.
                            // Reordering may deliberately hold it for a short period before publication.
                            acquisitionContext.MarkDataReceived(envelope.Timestamp);
                            await AcceptEegPacketAsync(envelope, packet, acquisitionContext)
                                .ConfigureAwait(false);
                        }
                        break;
                    case AcquisitionCompletedEvent
                        when activeContext?.Kind == OperationKind.Acquisition:
                        activeContext.CompletionReceivedAt ??= envelope.Timestamp;
                        if (!activeContext.IsPhysicalAcquisition)
                            activeContext.Completion.TrySetResult();
                        break;
                    case StimulationCompletedEvent
                        when activeContext?.Kind == OperationKind.Stimulation:
                        activeContext.CompletionReceivedAt ??= envelope.Timestamp;
                        activeContext.Completion.TrySetResult();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            terminal = new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            terminal = exception;
        }
        finally
        {
            pump.Ended.TrySetResult(terminal);
            if (
                _operations.TryGetValue(device.Identity.DeviceId, out var context)
                && ReferenceEquals(context.Device, device)
            )
                context.Failure.TrySetResult(terminal);
            pump.Ready.TrySetResult();
        }
    }

    private static async Task WaitForOperationAsync(
        OperationContext context,
        Task operation,
        CancellationToken cancellationToken
    )
    {
        // Always observe an abandoned request/watchdog; failure must not wait for the acquisition duration.
        _ = operation.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        await Task.WhenAny(operation, context.Failure.Task)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (context.Failure.Task.IsCompletedSuccessfully)
            throw await context.Failure.Task.ConfigureAwait(false);
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool RemoveOperation(OperationContext context) =>
        ((ICollection<KeyValuePair<DeviceId, OperationContext>>)_operations).Remove(
            new(context.Device.Identity.DeviceId, context)
        );

    private async Task AcceptEegPacketAsync(
        DeviceEventEnvelope envelope,
        EegDataPacketReceivedEvent packet,
        OperationContext context
    )
    {
        var scheduleTimeoutFlush = false;
        await context.PacketProcessingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!context.IsAcceptingPackets)
                return;
            var ready = context.PacketReorderer.Accept(
                new EegSequencedPacket<PendingEegPacket>(
                    packet.PacketIndex,
                    packet.SampleCount,
                    envelope.Timestamp,
                    new PendingEegPacket(envelope, packet)
                )
            );
            await ProcessReadyEegPacketsAsync(context, ready).ConfigureAwait(false);
            scheduleTimeoutFlush = context.PacketReorderer.HasPendingGap;
        }
        finally
        {
            context.PacketProcessingGate.Release();
        }
        if (scheduleTimeoutFlush)
            _ = FlushEegPacketsAfterTimeoutAsync(context);
    }

    private async Task ProcessEegPacketAsync(
        DeviceEventEnvelope envelope,
        EegDataPacketReceivedEvent packet,
        OperationContext context,
        int missingPacketsBefore
    )
    {
        if (missingPacketsBefore > 0)
            context.SampleTimeline.AdvanceMissingPackets(missingPacketsBefore, packet.SampleCount);
        var packetBatches = EegPacketTimelineMapper.CreateBatches(
            packet,
            context.ChannelNames,
            context.SampleRateHz,
            context.TimelineOffset,
            context.Duration,
            context.SampleTimeline
        );
        if (
            _rawPacketRecorder is not null
            && context.RecordingId != Guid.Empty
            && !envelope.RawPacketData.IsEmpty
        )
        {
            var timelineStart =
                context.TimelineOffset.TotalSeconds
                + TimeSpan
                    .FromTicks(context.SampleTimeline.LastPacketFirstSampleTicks)
                    .TotalSeconds;
            try
            {
                await _rawRecordingLifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_recordingSequences.ContainsKey(context.RecordingId))
                    {
                        await _rawPacketRecorder
                            .AppendAsync(
                                new EegRawPacketRecord(
                                    context.RecordingId,
                                    NextRawPacketSequence(context.RecordingId),
                                    envelope.Timestamp,
                                    envelope.ReceivedTimestamp,
                                    timelineStart,
                                    context.SampleRateHz,
                                    packet.SampleCount,
                                    context.CurrentCycle,
                                    ExperimentRunStage.Acquisition.ToString(),
                                    envelope.RawPacketData
                                )
                            )
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    _rawRecordingLifecycleGate.Release();
                }
            }
            catch (Exception exception)
            {
                throw new EegAcquisitionException(
                    EegAcquisitionFailureKind.RawRecordingFailed,
                    $"原始数据记录失败：{exception.Message}",
                    innerException: exception
                );
            }
        }
        if (packetBatches.Count == 0)
            return;

        var packetElapsed = envelope.Timestamp - context.StartedAt;
        if (packetElapsed < TimeSpan.Zero)
            packetElapsed = TimeSpan.Zero;
        var packetProgress = CreateLocalAcquisitionProgress(context, packetElapsed);
        context.LastProgress = packetProgress;
        Publish(
            envelope.DeviceId,
            CreateTelemetry(context, packetProgress, packetBatches) with
            {
                IsWaveformOnly = true,
                LatestPacketReceivedAtUtc = envelope.Timestamp,
                LatestPacketReceivedTimestamp = envelope.ReceivedTimestamp,
                LocalProcessingLatency =
                    envelope.ReceivedTimestamp == 0
                        ? null
                        : Stopwatch.GetElapsedTime(envelope.ReceivedTimestamp),
                DecodeLatency = envelope.DecodeLatency,
                PacketStatistics = context.PacketStatistics.Snapshot(),
            }
        );
    }

    private async Task ProcessReadyEegPacketsAsync(
        OperationContext context,
        IReadOnlyList<EegReorderedPacket<PendingEegPacket>> ready
    )
    {
        foreach (var item in ready)
        {
            await ProcessEegPacketAsync(
                    item.Value.Envelope,
                    item.Value.Packet,
                    context,
                    item.MissingPacketsBefore
                )
                .ConfigureAwait(false);
        }
    }

    private async Task FlushEegPacketsAfterTimeoutAsync(OperationContext context)
    {
        try
        {
            await Task.Delay(
                    _eegAcquisitionOptions.PacketReorderTimeout,
                    _lifetimeCancellation.Token
                )
                .ConfigureAwait(false);
            await context
                .PacketProcessingGate.WaitAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            var scheduleAnother = false;
            try
            {
                var ready = context.PacketReorderer.FlushExpired(DateTimeOffset.UtcNow);
                await ProcessReadyEegPacketsAsync(context, ready).ConfigureAwait(false);
                scheduleAnother = context.PacketReorderer.HasPendingGap;
            }
            finally
            {
                context.PacketProcessingGate.Release();
            }
            if (scheduleAnother)
                _ = FlushEegPacketsAfterTimeoutAsync(context);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            context.Failure.TrySetResult(exception);
        }
    }

    private async Task FlushAllEegPacketsAsync(OperationContext context)
    {
        await context.PacketProcessingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ready = context.PacketReorderer.Complete();
            await ProcessReadyEegPacketsAsync(context, ready).ConfigureAwait(false);
        }
        finally
        {
            context.PacketProcessingGate.Release();
        }
    }

    private void OnRawRecordingFailed(object? sender, EegRawRecordingFailedEventArgs eventArgs)
    {
        var failure = new EegAcquisitionException(
            EegAcquisitionFailureKind.RawRecordingFailed,
            $"原始数据记录失败：{eventArgs.Exception.Message}",
            innerException: eventArgs.Exception
        );
        foreach (var context in _operations.Values)
        {
            if (context.RecordingId == eventArgs.RecordingId)
                context.Failure.TrySetResult(failure);
        }
    }

    private long NextRawPacketSequence(Guid recordingId) =>
        _recordingSequences.AddOrUpdate(recordingId, 1, static (_, sequence) => sequence + 1);

    private async Task RecordSimulatedSamplesAsync(
        DeviceEventEnvelope envelope,
        EegSamplesReceivedEvent samples,
        OperationContext context
    )
    {
        if (samples.Channels.Count == 0)
            return;
        context.PacketStatistics.RecordReceived();
        if (_rawPacketRecorder is null || context.RecordingId == Guid.Empty)
            return;
        try
        {
            await _rawRecordingLifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_recordingSequences.ContainsKey(context.RecordingId))
                    return;
                var first = samples.Channels[0];
                await _rawPacketRecorder
                    .AppendSamplesAsync(
                        new EegRecordedSampleBatch(
                            context.RecordingId,
                            NextRawPacketSequence(context.RecordingId),
                            envelope.Timestamp,
                            envelope.ReceivedTimestamp,
                            context.TimelineOffset.TotalSeconds + first.StartTimeSeconds,
                            context.SampleRateHz,
                            first.SampleIntervalSeconds,
                            context.CurrentCycle,
                            ExperimentRunStage.Acquisition.ToString(),
                            samples
                                .Channels.Select(channel => new EegRecordedChannelSamples(
                                    channel.PhysicalChannel,
                                    channel.Samples
                                ))
                                .ToArray()
                        )
                    )
                    .ConfigureAwait(false);
            }
            finally
            {
                _rawRecordingLifecycleGate.Release();
            }
        }
        catch (Exception exception)
        {
            throw new EegAcquisitionException(
                EegAcquisitionFailureKind.RawRecordingFailed,
                $"原始数据记录失败：{exception.Message}",
                innerException: exception
            );
        }
    }

    private static ExperimentRunTelemetry CreateTelemetry(
        OperationContext context,
        DeviceProgressEvent progress,
        IReadOnlyList<WaveformChannelBatch> batches
    )
    {
        var stage = progress.Operation switch
        {
            DeviceOperationState.Acquiring => ExperimentRunStage.Acquisition,
            DeviceOperationState.Stimulating => ExperimentRunStage.Stimulation,
            _ => ExperimentRunStage.Standby,
        };
        return new ExperimentRunTelemetry(
            stage,
            context.TimelineOffset + progress.Elapsed,
            progress.Elapsed,
            progress.Progress,
            context.CurrentCycle,
            context.TotalCycles,
            (double)progress.CurrentMilliAmps,
            progress.AverageImpedanceKiloOhms,
            true,
            [],
            batches,
            PacketStatistics: context.PacketStatistics.Snapshot()
        );
    }

    private void Publish(DeviceId deviceId, ExperimentRunTelemetry telemetry) =>
        TelemetryReceived?.Invoke(
            this,
            new ExperimentRunTelemetryEventArgs(telemetry) { DeviceId = deviceId.Value }
        );

    private IEggtCsDevice GetDevice(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = new DeviceId(deviceId);
        return
            _deviceManager.TryGet(id, out var device)
            && device is not null
            && device.State.Connection == DeviceConnectionState.Connected
            ? device
            : throw new InvalidOperationException($"Device '{deviceId}' is not connected.");
    }

    private IReadOnlyDictionary<string, int> ResolveChannels(
        IEggtCsDevice device,
        IReadOnlyList<string> channelIds
    )
    {
        var mappingSnapshot = _eegChannelMappings.Load();
        if (!mappingSnapshot.IsValid)
            throw new InvalidOperationException(
                $"EEG采集物理通道配置无效：{string.Join("；", mappingSnapshot.ValidationErrors)}"
            );
        var configured = mappingSnapshot
            .Mappings.Where(item => item.PhysicalChannel is not null)
            .ToDictionary(
                item => item.ElectrodeId,
                item => item.PhysicalChannel!.Value,
                StringComparer.OrdinalIgnoreCase
            );
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedChannels = new HashSet<int>();
        foreach (var channelId in channelIds)
        {
            if (configured.TryGetValue(channelId, out var physicalChannel))
            {
                result[channelId] = physicalChannel;
                usedChannels.Add(physicalChannel);
            }
            else if (
                device.UsesCapabilitySource(
                    DeviceCapabilityKind.EegAcquisition,
                    DeviceCapabilitySource.Simulated
                )
            )
            {
                var simulatedChannel = Enumerable
                    .Range(1, device.Capabilities.MaximumEegChannels)
                    .FirstOrDefault(candidate => !usedChannels.Contains(candidate));
                if (simulatedChannel == 0)
                    throw new InvalidOperationException(
                        "No simulated physical channel is available."
                    );
                result[channelId] = simulatedChannel;
                usedChannels.Add(simulatedChannel);
            }
            else
                throw new InvalidOperationException(
                    $"Electrode '{channelId}' has no physical channel mapping."
                );
        }
        if (result.Values.Distinct().Count() != result.Count)
            throw new InvalidOperationException(
                "Multiple electrodes resolve to the same physical channel."
            );
        return result;
    }

    private StimulationConfiguration CreateStimulationConfiguration(
        IEggtCsDevice device,
        StimulationRunRequest request
    ) =>
        CreateStimulationConfiguration(
            device,
            request.TargetCurrentMilliAmps,
            request.StimulusConfiguration,
            request.StimulusElectrodes
        );

    private StimulationConfiguration CreateStimulationConfiguration(
        IEggtCsDevice device,
        double fallbackCurrent,
        ExperimentStimulusConfigurationSnapshot? source,
        IReadOnlyList<StimulusElectrodeAssignment>? assignments
    )
    {
        if (source is null)
        {
            if (
                device.UsesCapabilitySource(
                    DeviceCapabilityKind.Stimulation,
                    DeviceCapabilitySource.Real
                )
            )
                throw new InvalidOperationException(
                    "真实刺激必须包含已确认的完整刺激配置和电极分配。"
                );
            return DeviceStimulationConfigurationMapper.CreateFallback(device, fallbackCurrent);
        }
        if (assignments is null)
            throw new InvalidOperationException(
                "Stimulation electrode assignments are required with a configuration snapshot."
            );
        return DeviceStimulationConfigurationMapper.Create(device, source, assignments);
    }

    private async Task RunLocalAcquisitionProgressAsync(
        DeviceId deviceId,
        OperationContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (
                !cancellationToken.IsCancellationRequested
                && _clock.UtcNow - context.StartedAt < context.Duration
            )
            {
                var progress = CreateLocalAcquisitionProgress(
                    context,
                    _clock.UtcNow - context.StartedAt
                );
                context.LastProgress = progress;
                Publish(deviceId, CreateTelemetry(context, progress, []));
                await _clock
                    .DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                var completed = CreateLocalAcquisitionProgress(context, context.Duration);
                context.LastProgress = completed;
                Publish(deviceId, CreateTelemetry(context, completed, []));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunLocalStimulationProgressAsync(
        DeviceId deviceId,
        OperationContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (
                !cancellationToken.IsCancellationRequested
                && _clock.UtcNow - context.StartedAt < context.Duration
            )
            {
                var progress = CreateLocalStimulationProgress(
                    context,
                    _clock.UtcNow - context.StartedAt
                );
                context.LastProgress = progress;
                Publish(deviceId, CreateTelemetry(context, progress, []));
                await _clock
                    .DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                var completed = CreateLocalStimulationProgress(context, context.Duration);
                context.LastProgress = completed;
                Publish(deviceId, CreateTelemetry(context, completed, []));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunLocalAutomaticStageAsync(
        DeviceId deviceId,
        ExperimentRunStage stage,
        TimeSpan duration,
        TimeSpan timelineOffset,
        int currentCycle,
        int totalCycles,
        CancellationToken cancellationToken
    )
    {
        var startedAt = _clock.UtcNow;
        while (_clock.UtcNow - startedAt < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = _clock.UtcNow - startedAt;
            if (elapsed > duration)
                elapsed = duration;
            Publish(
                deviceId,
                new ExperimentRunTelemetry(
                    stage,
                    timelineOffset + elapsed,
                    elapsed,
                    duration <= TimeSpan.Zero
                        ? 1d
                        : Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, 0d, 1d),
                    currentCycle,
                    totalCycles,
                    0d,
                    0d,
                    true,
                    [],
                    []
                )
            );
            await _clock
                .DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken)
                .ConfigureAwait(false);
        }
        Publish(
            deviceId,
            new ExperimentRunTelemetry(
                stage,
                timelineOffset + duration,
                duration,
                1d,
                currentCycle,
                totalCycles,
                0d,
                0d,
                true,
                [],
                []
            )
        );
    }

    private static DeviceProgressEvent CreateLocalAcquisitionProgress(
        OperationContext context,
        TimeSpan elapsed
    )
    {
        var boundedElapsed =
            elapsed < TimeSpan.Zero ? TimeSpan.Zero
            : elapsed > context.Duration ? context.Duration
            : elapsed;
        var deviceProgress = context.LastDeviceProgress;
        return new DeviceProgressEvent(
            DeviceOperationState.Acquiring,
            boundedElapsed,
            context.Duration - boundedElapsed,
            context.Duration <= TimeSpan.Zero
                ? 1d
                : Math.Clamp(boundedElapsed.TotalSeconds / context.Duration.TotalSeconds, 0d, 1d),
            context.CurrentCycle,
            context.TotalCycles,
            deviceProgress?.CurrentMilliAmps ?? 0m,
            deviceProgress?.BatteryPercent,
            deviceProgress?.AverageImpedanceKiloOhms ?? 0d
        );
    }

    private static DeviceProgressEvent CreateLocalStimulationProgress(
        OperationContext context,
        TimeSpan elapsed
    )
    {
        var boundedElapsed =
            elapsed < TimeSpan.Zero ? TimeSpan.Zero
            : elapsed > context.Duration ? context.Duration
            : elapsed;
        return new DeviceProgressEvent(
            DeviceOperationState.Stimulating,
            boundedElapsed,
            context.Duration - boundedElapsed,
            context.Duration <= TimeSpan.Zero
                ? 1d
                : Math.Clamp(boundedElapsed.TotalSeconds / context.Duration.TotalSeconds, 0d, 1d),
            context.CurrentCycle,
            context.TotalCycles
        );
    }

    private async Task<bool> WaitForAcquisitionWindowAsync(
        IEggtCsDevice device,
        OperationContext context,
        CancellationToken cancellationToken
    )
    {
        if (!context.IsPhysicalAcquisition)
        {
            var duration = _clock.DelayAsync(context.Duration, cancellationToken);
            await context.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await duration.ConfigureAwait(false);
            return true;
        }

        var remainingDuration = context.WindowEndsAt - _clock.UtcNow;
        if (remainingDuration < TimeSpan.Zero)
            remainingDuration = TimeSpan.Zero;
        var durationTask = _clock.DelayAsync(remainingDuration, cancellationToken);
        var watchdogTask = MonitorEegDataAsync(context, _clock.UtcNow, cancellationToken);
        var durationCompleted = false;
        var watchdogCompleted = false;
        while (!durationCompleted || !watchdogCompleted)
        {
            var pending = new List<Task>();
            if (!context.IsPhysicalAcquisition || !context.Completion.Task.IsCompletedSuccessfully)
                pending.Add(context.Completion.Task);
            if (!durationCompleted)
                pending.Add(durationTask);
            if (!watchdogCompleted)
                pending.Add(watchdogTask);
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            if (completed == context.Completion.Task)
            {
                await context.Completion.Task.ConfigureAwait(false);
                if (!context.IsPhysicalAcquisition)
                    return false;
                // A physical 0x9C is informational only. Even if another path
                // completed this shared signal, the front-end acquisition window
                // and data watchdog remain authoritative.
                continue;
            }
            if (completed == durationTask)
            {
                await durationTask.ConfigureAwait(false);
                durationCompleted = true;
            }
            if (completed == watchdogTask)
            {
                await watchdogTask.ConfigureAwait(false);
                watchdogCompleted = true;
            }
        }
        return true;
    }

    private async Task MonitorEegDataAsync(
        OperationContext context,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _clock.UtcNow;
            var (lastDataReceivedAt, packetCount, pulse) = context.GetDataState();
            if (lastDataReceivedAt is not null && now >= context.WindowEndsAt)
                return;

            var deadline = lastDataReceivedAt is null
                ? acknowledgedAt + _eegAcquisitionOptions.DataPacketTimeout
                : lastDataReceivedAt.Value + _eegAcquisitionOptions.DataPacketTimeout;
            if (lastDataReceivedAt is not null && deadline > context.WindowEndsAt)
                deadline = context.WindowEndsAt;
            if (now >= deadline)
            {
                if (lastDataReceivedAt is not null && deadline >= context.WindowEndsAt)
                    return;
                var message =
                    packetCount == 0
                        ? $"设备已确认开始采集，但在 {FormatDuration(_eegAcquisitionOptions.DataPacketTimeout)} 内未收到首个有效 EEG 数据包。"
                    : context.CompletionReceivedAt is not null
                        ? $"设备在前端采集时间结束前上报了采集完成；本次已收到 {packetCount} 个 EEG 数据包，但随后连续 {FormatDuration(_eegAcquisitionOptions.DataPacketTimeout)} 未再收到数据。"
                    : $"本次已收到 {packetCount} 个 EEG 数据包，但随后连续 {FormatDuration(_eegAcquisitionOptions.DataPacketTimeout)} 未再收到数据，EEG 数据流已中断。";
                throw new EegAcquisitionException(
                    EegAcquisitionFailureKind.DataPacketTimeout,
                    message,
                    _eegAcquisitionOptions.DataPacketTimeout
                );
            }

            var delay = _clock.DelayAsync(deadline - now, cancellationToken);
            var completed = await Task.WhenAny(delay, pulse).ConfigureAwait(false);
            if (completed == delay)
                await delay.ConfigureAwait(false);
        }
    }

    private async Task WaitForPhysicalStimulationAsync(
        OperationContext context,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken
    )
    {
        var remainingDuration = context.WindowEndsAt - _clock.UtcNow;
        if (remainingDuration < TimeSpan.Zero)
            remainingDuration = TimeSpan.Zero;
        var durationTask = _clock.DelayAsync(remainingDuration, cancellationToken);
        var watchdogTask = MonitorStimulationProgressAsync(
            context,
            acknowledgedAt,
            cancellationToken
        );
        var watchdogCompleted = false;

        while (true)
        {
            var pending = new List<Task> { context.Completion.Task, durationTask };
            if (!watchdogCompleted)
                pending.Add(watchdogTask);
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            if (completed == context.Completion.Task)
            {
                await context.Completion.Task.ConfigureAwait(false);
                return;
            }
            if (completed == watchdogTask)
            {
                await watchdogTask.ConfigureAwait(false);
                watchdogCompleted = true;
            }
            if (completed == durationTask || durationTask.IsCompleted)
            {
                await durationTask.ConfigureAwait(false);
                if (!watchdogCompleted)
                    await watchdogTask.ConfigureAwait(false);
                break;
            }
        }

        if (context.Completion.Task.IsCompleted)
        {
            await context.Completion.Task.ConfigureAwait(false);
            return;
        }

        var graceTask = _clock.DelayAsync(
            _stimulationRunOptions.CompletionEventGracePeriod,
            cancellationToken
        );
        var completion = await Task.WhenAny(context.Completion.Task, graceTask)
            .ConfigureAwait(false);
        if (completion == context.Completion.Task)
        {
            await context.Completion.Task.ConfigureAwait(false);
            return;
        }

        await graceTask.ConfigureAwait(false);
        throw new StimulationException(
            StimulationFailureKind.CompletionEventTimeout,
            $"刺激时间已结束，但在 {FormatDuration(_stimulationRunOptions.CompletionEventGracePeriod)} 内未收到设备完成事件。",
            _stimulationRunOptions.CompletionEventGracePeriod
        );
    }

    private async Task MonitorStimulationProgressAsync(
        OperationContext context,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _clock.UtcNow;
            var (lastProgressReceivedAt, pulse) = context.GetProgressState();
            if (now >= context.WindowEndsAt)
                return;

            var deadline = lastProgressReceivedAt is null
                ? acknowledgedAt + _stimulationRunOptions.ProgressPacketTimeout
                : lastProgressReceivedAt.Value + _stimulationRunOptions.ProgressPacketTimeout;
            if (deadline > context.WindowEndsAt)
                deadline = context.WindowEndsAt;
            if (now >= deadline)
            {
                if (deadline >= context.WindowEndsAt)
                    return;
                throw new StimulationException(
                    StimulationFailureKind.ProgressPacketTimeout,
                    $"设备已确认开始刺激，但连续 {FormatDuration(_stimulationRunOptions.ProgressPacketTimeout)} 未收到有效刺激状态数据包。",
                    _stimulationRunOptions.ProgressPacketTimeout
                );
            }

            var delay = _clock.DelayAsync(deadline - now, cancellationToken);
            var completed = await Task.WhenAny(delay, pulse).ConfigureAwait(false);
            if (completed == delay)
                await delay.ConfigureAwait(false);
        }
    }

    private async Task<EegAcquisitionException> AttachStopResultAsync(
        IEegAcquisitionCapability capability,
        OperationContext context,
        EegAcquisitionException failure
    )
    {
        if (context.CompletionReceivedAt is not null)
            return failure.WithStopResult(false, null);
        try
        {
            var result = await context
                .StopOnceAsync(() => capability.StopAsync(_lifetimeCancellation.Token))
                .ConfigureAwait(false);
            return result.IsSuccess
                ? failure.WithStopResult(true, null)
                : failure.WithStopResult(true, $"{result.Status}。{result.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return failure.WithStopResult(true, exception.Message);
        }
        catch (OperationCanceledException exception)
        {
            return failure.WithStopResult(true, exception.Message);
        }
    }

    private async Task<StimulationException> AttachStimulationStopResultAsync(
        IStimulationCapability capability,
        OperationContext context,
        StimulationException failure
    )
    {
        if (context.CompletionReceivedAt is not null)
            return failure.WithStopResult(false, null);
        try
        {
            var result = await StopStimulationOnceAsync(
                    context,
                    capability,
                    _lifetimeCancellation.Token
                )
                .ConfigureAwait(false);
            return IsStimulationStopAccepted(result)
                ? failure.WithStopResult(true, null)
                : failure.WithStopResult(true, $"{result.Status}。{result.Message}");
        }
        catch (Exception exception)
        {
            return failure.WithStopResult(true, exception.Message);
        }
    }

    private static Task<DeviceCommandResult> StopAcquisitionOnceAsync(
        OperationContext? context,
        IEegAcquisitionCapability capability,
        CancellationToken cancellationToken
    ) =>
        context?.Kind == OperationKind.Acquisition
            ? context.StopOnceAsync(() => capability.StopAsync(cancellationToken))
            : capability.StopAsync(cancellationToken);

    private static Task<DeviceCommandResult> StopStimulationOnceAsync(
        OperationContext? context,
        IStimulationCapability capability,
        CancellationToken cancellationToken
    ) =>
        context?.Kind == OperationKind.Stimulation
            ? context.StopOnceAsync(() => capability.StopAsync(cancellationToken))
            : capability.StopAsync(cancellationToken);

    private static bool IsStimulationStopAccepted(DeviceCommandResult result) =>
        result.IsSuccess
        || result.Status
            is DeviceCommandStatus.AlreadyStopped
                or DeviceCommandStatus.CurrentRampingDown;

    private void PreserveClosingAcquisition(DeviceId deviceId, OperationContext context)
    {
        _closingAcquisitions[deviceId] = context;
        _ = RemoveClosingAcquisitionAfterTimeoutAsync(deviceId, context);
    }

    private OperationContext? GetClosingAcquisition(DeviceId deviceId)
    {
        if (!_closingAcquisitions.TryGetValue(deviceId, out var context))
            return null;
        if (_clock.UtcNow - context.WindowEndsAt <= _eegAcquisitionOptions.DataPacketTimeout)
            return context;
        ((ICollection<KeyValuePair<DeviceId, OperationContext>>)_closingAcquisitions).Remove(
            new(deviceId, context)
        );
        return null;
    }

    private async Task RemoveClosingAcquisitionAfterTimeoutAsync(
        DeviceId deviceId,
        OperationContext context
    )
    {
        try
        {
            await _clock
                .DelayAsync(_eegAcquisitionOptions.DataPacketTimeout, _lifetimeCancellation.Token)
                .ConfigureAwait(false);
            ((ICollection<KeyValuePair<DeviceId, OperationContext>>)_closingAcquisitions).Remove(
                new(deviceId, context)
            );
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
    }

    private static string FormatDuration(TimeSpan duration) => $"{duration.TotalSeconds:0.###} 秒";

    private static async Task ObserveProgressTaskAsync(Task progressTask)
    {
        try
        {
            await progressTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private OperationContext? GetOperation(IEggtCsDevice device) =>
        _operations.TryGetValue(device.Identity.DeviceId, out var context)
        && ReferenceEquals(context.Device, device)
            ? context
            : null;

    private void CompleteOperation(OperationContext? context)
    {
        if (context is not null && RemoveOperation(context))
            context.Completion.TrySetResult();
    }

    private static void EnsureSuccess(DeviceCommandResult result, string operation)
    {
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Device rejected {operation}: {result.Status}. {result.Message}"
            );
    }
}
