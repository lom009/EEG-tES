using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceEnvelopeOutputTests
{
    private static EnvelopePlaybackPlan Plan(int frequency = 800)
    {
        var corpus = new FileSpeechCorpusProvider();
        var prepared = new TrainingAudioPreparationService(corpus).Prepare(
            new(
                corpus.Corpora[0].Sentences[0],
                new(),
                new AudioBuffer(16000, 1, new byte[640]),
                1,
                1729,
                null,
                null,
                0
            )
        );
        return EnvelopePlaybackPlan.Create(
            123,
            SpeechWaveformData.Prepare(prepared),
            new(2, 0, (ushort)frequency, 5)
        );
    }

    [Fact]
    public async Task FrozenPlanIsSentWithIndependentEnvelopeCommands()
    {
        var device = new Device();
        var manager = new Manager(device);
        var output = new DeviceEnvelopeOutput(manager, "device");
        var plan = Plan();
        await output.PrepareAsync(plan, default);
        await output.StartAsync(default);
        await output.WaitForCompletionAsync(default);
        device.Envelope.StopResult = new(DeviceCommandStatus.AlreadyStopped) { RawResultCode = 3 };
        await output.StopAsync();
        Assert.Equal(new[] { "configure", "start", "stop" }, device.Envelope.Calls);
        var configuration = device.Envelope.Configuration!;
        Assert.Equal(plan.Samples.ToArray(), configuration.Samples.ToArray());
        Assert.Equal(800, configuration.DebugFrequencyHz);
        Assert.Equal(100, configuration.SampleRateHz);
        Assert.Equal(5, configuration.ChannelMask);
        var profile = new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults());
        var frame = profile.Encode(new ConfigureEnvelopeStimulationRequest(configuration), 1);
        Assert.Equal(0x4A, frame.Command);
        Assert.Equal(configuration.Samples.ToArray(), frame.Payload.Span[9..].ToArray());
    }

    [Theory]
    [InlineData(DeviceCommandStatus.Success)]
    [InlineData(DeviceCommandStatus.AlreadyStopped)]
    public async Task UnknownRealDeviceConfirmsStopBeforeConfiguration(DeviceCommandStatus result)
    {
        var device = new Device { Operation = DeviceOperationState.Unknown };
        device.Envelope.StopResult = new(result);
        var pause = new DeviceHeartbeatPauseService();
        var output = new DeviceEnvelopeOutput(new Manager(device), "device", pause);
        await output.PrepareAsync(Plan(), default);
        Assert.Equal(new[] { "stop", "configure" }, device.Envelope.Calls);
        Assert.True(pause.IsPaused(device.Identity.DeviceId));
        await output.StopAsync();
        Assert.False(pause.IsPaused(device.Identity.DeviceId));
    }

    [Theory]
    [InlineData(DeviceCommandStatus.NotConfigured, 1)]
    [InlineData(DeviceCommandStatus.AlreadyRunning, 2)]
    [InlineData(DeviceCommandStatus.Failed, 255)]
    public async Task UnknownStopRejectionLatchesAndReleasesMonitoring(
        DeviceCommandStatus result,
        byte raw
    )
    {
        var device = new Device { Operation = DeviceOperationState.Unknown };
        device.Envelope.StopResult = new(result) { RawResultCode = raw };
        var manager = new Manager(device);
        var pause = new DeviceHeartbeatPauseService();
        var output = new DeviceEnvelopeOutput(manager, "device", pause);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            output.PrepareAsync(Plan(), default)
        );
        Assert.Contains("停止未确认", error.Message);
        Assert.Contains($"0x{raw:X2}", error.Message);
        Assert.False(pause.IsPaused(device.Identity.DeviceId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeviceEnvelopeOutput(manager, "device").PrepareAsync(Plan(), default)
        );
        Assert.Equal(new[] { "stop" }, device.Envelope.Calls);
        manager.Current = new Device();
        await output.PrepareAsync(Plan(), default);
        Assert.Equal(new[] { "stop", "configure" }, manager.Current.Envelope.Calls);
        await output.StopAsync();
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    public async Task InterruptedInitialStopNeverConfiguresEvenAfterLateSuccess(string interruption)
    {
        var device = new Device { Operation = DeviceOperationState.Unknown };
        var pending = new TaskCompletionSource<DeviceCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        device.Envelope.PendingStop = pending.Task;
        var manager = new Manager(device);
        var pause = new DeviceHeartbeatPauseService();
        var output = new DeviceEnvelopeOutput(
            manager,
            "device",
            pause,
            TimeSpan.FromMilliseconds(interruption == "timeout" ? 30 : 2000)
        );
        using var cancellation = new CancellationTokenSource();
        var prepare = output.PrepareAsync(Plan(), cancellation.Token);
        Assert.Contains("stop", device.Envelope.Calls);
        if (interruption == "cancel")
            cancellation.Cancel();
        if (interruption == "disconnect")
            device.Events.Writer.TryWrite(
                new(
                    new("device"),
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    new DeviceStateChangedEvent(
                        new(
                            DeviceConnectionState.Disconnected,
                            DeviceOperationState.Faulted,
                            100,
                            DateTimeOffset.UtcNow
                        )
                    ),
                    default
                )
            );
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            prepare.WaitAsync(TimeSpan.FromSeconds(5))
        );
        Assert.Contains("停止未确认", error.Message);
        Assert.False(pause.IsPaused(device.Identity.DeviceId));
        pending.SetResult(DeviceCommandResult.Success);
        using var lateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (device.Operation != DeviceOperationState.Ready)
            await Task.Delay(10, lateTimeout.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeviceEnvelopeOutput(manager, "device").PrepareAsync(Plan(), default)
        );
        Assert.Equal(new[] { "stop" }, device.Envelope.Calls);
    }

    [Theory]
    [InlineData(DeviceOperationState.Acquiring, "采集脑电")]
    [InlineData(DeviceOperationState.Stimulating, "正在刺激")]
    [InlineData(DeviceOperationState.Stopping, "正在停止")]
    [InlineData(DeviceOperationState.ImpedanceChecking, "检测阻抗")]
    [InlineData(DeviceOperationState.Faulted, "故障")]
    public async Task KnownUnavailableStateHasSpecificMessageAndNoTraffic(
        DeviceOperationState state,
        string message
    )
    {
        var device = new Device { Operation = state };
        var output = new DeviceEnvelopeOutput(new Manager(device), "device");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            output.PrepareAsync(Plan(), default)
        );
        Assert.Contains(message, error.Message);
        Assert.DoesNotContain("设备正忙", error.Message);
        Assert.Empty(device.Envelope.Calls);
    }

    [Fact]
    public async Task UnknownSimulatorDoesNotPretendToBeBusyOrSendStop()
    {
        var device = new Device
        {
            Operation = DeviceOperationState.Unknown,
            Source = DeviceCapabilitySource.Simulated,
        };
        var output = new DeviceEnvelopeOutput(new Manager(device), "device");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            output.PrepareAsync(Plan(), default)
        );
        Assert.Contains("状态未确认", error.Message);
        Assert.DoesNotContain("设备正忙", error.Message);
        Assert.Empty(device.Envelope.Calls);
    }

    [Theory]
    [InlineData(DeviceCommandStatus.NotConfigured)]
    [InlineData(DeviceCommandStatus.AlreadyRunning)]
    [InlineData(DeviceCommandStatus.AlreadyStopped)]
    [InlineData(DeviceCommandStatus.Failed)]
    public async Task RejectedStartIsStoppedAndCannotBeRetriedOnSameConnection(
        DeviceCommandStatus status
    )
    {
        var device = new Device();
        device.Envelope.StartResult = new(status) { RawResultCode = 2 };
        var manager = new Manager(device);
        var output = new DeviceEnvelopeOutput(manager, "device");
        var plan = Plan();
        await output.PrepareAsync(plan, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartAsync(default));
        await output.StopAsync();
        var recreated = new DeviceEnvelopeOutput(manager, "device");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recreated.PrepareAsync(plan, default)
        );
        Assert.Equal(1, device.Envelope.Calls.Count(x => x == "start"));
        manager.Current = new Device(); // Reconnect: verify stop before accepting configuration.
        await recreated.PrepareAsync(plan, default);
        Assert.Equal(new[] { "stop", "configure" }, manager.Current.Envelope.Calls);
        await recreated.StopAsync();
    }

    [Fact]
    public async Task ConfigurationTimeoutLatchesAcrossPagesWithoutStarting()
    {
        var device = new Device();
        device.Envelope.ConfigureError = new RequestTimeoutException(
            typeof(ConfigureEnvelopeStimulationRequest),
            TimeSpan.FromMilliseconds(1)
        );
        var manager = new Manager(device);
        var output = new DeviceEnvelopeOutput(manager, "device");
        await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            output.PrepareAsync(Plan(), default)
        );
        await output.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeviceEnvelopeOutput(manager, "device").PrepareAsync(Plan(), default)
        );
        Assert.Equal(new[] { "configure" }, device.Envelope.Calls);
    }

    [Fact]
    public async Task UnsupportedDisabledAndBusyDevicesNeverSend()
    {
        foreach (
            var device in new[]
            {
                new Device { Supported = false },
                new Device { Enabled = false },
                new Device { Operation = DeviceOperationState.Stimulating },
            }
        )
        {
            var output = new DeviceEnvelopeOutput(new Manager(device), "device");
            await Assert.ThrowsAnyAsync<Exception>(() => output.PrepareAsync(Plan(), default));
            await output.StopAsync();
            Assert.Empty(device.Envelope.Calls);
        }
    }

    [Fact]
    public async Task StopFailureIsVisibleAndBlocksReconfiguration()
    {
        var device = new Device();
        var output = new DeviceEnvelopeOutput(new Manager(device), "device");
        await output.PrepareAsync(Plan(), default);
        await output.StartAsync(default);
        device.Envelope.StopResult = new(DeviceCommandStatus.NotConfigured) { RawResultCode = 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StopAsync());
        Assert.Contains("停止未确认", output.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            output.PrepareAsync(Plan(), default)
        );
    }

    [Theory]
    [InlineData(DeviceCommandStatus.NotConfigured)]
    [InlineData(DeviceCommandStatus.AlreadyRunning)]
    [InlineData(DeviceCommandStatus.AlreadyStopped)]
    [InlineData(DeviceCommandStatus.Failed)]
    public async Task RejectedConfigurationNeverStarts(DeviceCommandStatus status)
    {
        var device = new Device();
        device.Envelope.ConfigureResult = new(status) { RawResultCode = 9 };
        var output = new DeviceEnvelopeOutput(new Manager(device), "device");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            output.PrepareAsync(Plan(), default)
        );
        Assert.Contains("0x09", error.Message);
        await output.StopAsync();
        Assert.Equal(new[] { "configure" }, device.Envelope.Calls);
    }

    [Fact]
    public async Task CancelledStartUsesFreshStopTokenAndLateAckDoesNotClearFault()
    {
        var device = new Device();
        var pending = new TaskCompletionSource<DeviceCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        device.Envelope.PendingStart = pending.Task;
        var manager = new Manager(device);
        var output = new DeviceEnvelopeOutput(manager, "device");
        await output.PrepareAsync(Plan(), default);
        using var cancelled = new CancellationTokenSource();
        var start = output.StartAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await output.StopAsync();
        Assert.True(device.Envelope.StopTokenCanBeCancelled);
        Assert.False(device.Envelope.StopTokenWasCancelled);
        pending.SetResult(DeviceCommandResult.Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            output.PrepareAsync(Plan(), default)
        );
        Assert.Equal(new[] { "configure", "start", "stop" }, device.Envelope.Calls);
    }

    [Fact]
    public async Task DeviceDisconnectNotifiesAndLatchesTheConnection()
    {
        var device = new Device();
        var output = new DeviceEnvelopeOutput(new Manager(device), "device");
        var fault = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        output.Faulted += (_, message) => fault.TrySetResult(message);
        await output.PrepareAsync(Plan(), default);
        await output.StartAsync(default);
        device.Events.Writer.TryWrite(
            new(
                new("device"),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new DeviceStateChangedEvent(
                    new(
                        DeviceConnectionState.Disconnected,
                        DeviceOperationState.Faulted,
                        100,
                        DateTimeOffset.UtcNow
                    )
                ),
                default
            )
        );
        Assert.Contains("断连", await fault.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await output.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            output.PrepareAsync(Plan(), default)
        );
    }

    [Fact]
    public async Task CleanupTimeoutIsBoundedEvenIfCapabilityIgnoresCancellation()
    {
        var device = new Device();
        var output = new DeviceEnvelopeOutput(
            new Manager(device),
            "device",
            stopTimeout: TimeSpan.FromMilliseconds(20)
        );
        await output.PrepareAsync(Plan(), default);
        await output.StartAsync(default);
        var pending = new TaskCompletionSource<DeviceCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        device.Envelope.PendingStop = pending.Task;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            output.StopAsync().WaitAsync(TimeSpan.FromSeconds(5))
        );
        pending.SetResult(DeviceCommandResult.Success);
        Assert.Contains("停止未确认", output.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            output.PrepareAsync(Plan(), default)
        );
    }

    private sealed class Envelope : IEnvelopeStimulationCapability
    {
        public Action? Stopped { get; set; }
        public List<string> Calls { get; } = [];
        public EnvelopeStimulationConfiguration? Configuration { get; private set; }
        public DeviceCommandResult StartResult { get; set; } = DeviceCommandResult.Success;
        public DeviceCommandResult ConfigureResult { get; set; } = DeviceCommandResult.Success;
        public Task<DeviceCommandResult>? PendingStart { get; set; }
        public Task<DeviceCommandResult>? PendingStop { get; set; }
        public bool StopTokenCanBeCancelled { get; private set; }
        public bool StopTokenWasCancelled { get; private set; }
        public DeviceCommandResult StopResult { get; set; } = DeviceCommandResult.Success;
        public Exception? ConfigureError { get; set; }

        public Task<DeviceCommandResult> ConfigureAsync(
            EnvelopeStimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add("configure");
            Configuration = configuration;
            return ConfigureError is null
                ? Task.FromResult(ConfigureResult)
                : Task.FromException<DeviceCommandResult>(ConfigureError);
        }

        public Task<DeviceCommandResult> StartAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            return PendingStart ?? Task.FromResult(StartResult);
        }

        public async Task<DeviceCommandResult> StopAsync(
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add("stop");
            StopTokenCanBeCancelled = cancellationToken.CanBeCanceled;
            StopTokenWasCancelled = cancellationToken.IsCancellationRequested;
            var result = await (PendingStop ?? Task.FromResult(StopResult));
            if (result.IsSuccess || result.Status == DeviceCommandStatus.AlreadyStopped)
                Stopped?.Invoke();
            return result;
        }
    }

    private sealed class Device
        : IEggtCsDevice,
            IDeviceProtocolBinding,
            IDeviceCapabilitySourceProfile
    {
        public Device() => Envelope.Stopped = () => Operation = DeviceOperationState.Ready;

        public DeviceCapabilitySource Source { get; init; } = DeviceCapabilitySource.Real;

        public DeviceCapabilitySource GetCapabilitySource(DeviceCapabilityKind capability) =>
            Source;

        public Envelope Envelope { get; } = new();
        public Channel<DeviceEventEnvelope> Events { get; } =
            Channel.CreateUnbounded<DeviceEventEnvelope>();
        public bool Enabled { get; init; } = true;
        public bool Supported { get; init; } = true;
        public DeviceOperationState Operation { get; set; } = DeviceOperationState.Ready;
        public string ProtocolId => "test";
        public string ProtocolRevision => Supported ? "1.0.2" : "1.0.1";

        public bool SupportsCommand(Type commandType) => Supported;

        public DeviceIdentity Identity { get; } = new(new("device"), "test", null, null);
        public DeviceCapabilities Capabilities => DeviceCapabilities.Simulator;
        public DeviceStateSnapshot State =>
            new(DeviceConnectionState.Connected, Operation, 100, DateTimeOffset.UtcNow);
        public IEnvelopeStimulationCapability? EnvelopeStimulation => Enabled ? Envelope : null;
        public IEegAcquisitionCapability? EegAcquisition => null;
        public IStimulationCapability? Stimulation => null;
        public IImpedanceCapability? Impedance => null;
        public IToleranceCapability? Tolerance => null;

        public Task<DeviceStatusResponse> ReadStatusAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new DeviceStatusResponse(DeviceCommandStatus.Success, Operation, 100));

        public async IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await foreach (var item in Events.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Manager(Device current) : IDeviceManager
    {
        public Device Current { get; set; } = current;
        public IReadOnlyCollection<IEggtCsDevice> Devices => [Current];

        public bool TryGet(DeviceId deviceId, out IEggtCsDevice? device)
        {
            device = Current;
            return true;
        }

        public Task<IEggtCsDevice> ConnectAsync(
            DeviceCandidate candidate,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IEggtCsDevice>(Current);

        public Task DisconnectAsync(
            DeviceId deviceId,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task RemoveAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            TimeSpan window,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
