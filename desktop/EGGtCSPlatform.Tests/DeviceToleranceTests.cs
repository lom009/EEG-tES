using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceToleranceTests
{
    [Fact]
    public async Task HdStartupConfiguresMaximumWithoutUsingZeroSnapshot()
    {
        var device = new Device();
        using var session = new DeviceToleranceSession(
            ToleranceTests.Route(returns: 4),
            ToleranceTests.Capability,
            device,
            new()
        );
        await session.StartAsync();
        var group = Assert.Single(device.Configuration!.TargetGroups);
        Assert.Equal(2m, group.FixedActiveChannel.CurrentMilliAmps);
        Assert.Equal(
            new[] { .52m, .52m, .48m, .48m },
            group
                .Channels.Where(c => c.Role == DeviceStimulationChannelRole.Selectable)
                .Select(c => c.CurrentMilliAmps)
        );
        Assert.All(session.Configuration.Targets[0].Channels, c => Assert.Equal(0, c.Current));
        await session.StopAsync();
    }

    [Fact]
    public async Task ResetTimeoutDoesNotRetryOrChangeConfirmedCurrent()
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.Success, .12m));
        await session.AdjustAsync(1);
        var count = device.Calls.Count;
        device.Adjust = _ => Task.FromException<CurrentAdjustmentResponse>(new TimeoutException());
        await Assert.ThrowsAsync<TimeoutException>(() => session.ResetAsync());
        Assert.Equal(count + 1, device.Calls.Count);
        Assert.Equal("decrease", device.Calls.Last());
        Assert.Equal(.12, session.Current);
        await session.StopAsync();
    }

    [Fact]
    public async Task StartupWaitsForZeroConfirmationBeforeEnablingPage()
    {
        var pending = new TaskCompletionSource<CurrentAdjustmentResponse>();
        var device = new Device { Adjust = _ => pending.Task };
        using var session = Session(device);
        using var page = new ToleranceTestPageViewModel(
            ToleranceTests.Route(),
            new Factory(session),
            new Store()
        );
        var start = page.ResumeAsync();
        Assert.Equal(new[] { "configure", "start:tolerance", "decrease" }, device.Calls);
        Assert.All(device.Configuration!.Channels, c => Assert.Equal(2m, c.CurrentMilliAmps));
        Assert.False(page.CanAdjust);
        Assert.False(page.CanSave);
        Assert.False(session.HasConfirmedCurrent);
        pending.SetResult(new(DeviceCommandStatus.MinimumReached, 0m));
        await start;
        Assert.True(page.CanIncrease);
        Assert.False(page.CanDecrease);
        Assert.True(page.CanSave);
        Assert.Equal(0, page.Current);
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.Success, .04m));
        await page.IncreaseCommand.ExecuteAsync(null);
        Assert.Equal(.04, page.Current);
        await session.StopAsync();
    }

    [Theory]
    [InlineData(DeviceCommandStatus.Success, 0.04)]
    [InlineData(DeviceCommandStatus.MinimumReached, 0.04)]
    [InlineData(DeviceCommandStatus.InvalidParameter, 0)]
    [InlineData(DeviceCommandStatus.NotConfigured, 0)]
    public async Task InvalidInitialConfirmationStopsWithoutEnablingAdjustment(
        DeviceCommandStatus status,
        double current
    )
    {
        var device = new Device
        {
            Adjust = _ => Task.FromResult(new CurrentAdjustmentResponse(status, (decimal)current)),
        };
        using var session = Session(device);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        Assert.False(session.IsRunning);
        Assert.False(session.HasConfirmedCurrent);
        Assert.Equal(new[] { "configure", "start:tolerance", "decrease", "stop" }, device.Calls);
    }

    [Fact]
    public async Task InitialConfirmationTimeoutStopsWithoutRetry()
    {
        var device = new Device
        {
            Adjust = _ => Task.FromException<CurrentAdjustmentResponse>(new TimeoutException()),
        };
        using var session = Session(device);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        Assert.False(session.IsRunning);
        Assert.False(session.HasConfirmedCurrent);
        Assert.Equal(new[] { "configure", "start:tolerance", "decrease", "stop" }, device.Calls);
    }

    [Fact]
    public async Task ResetWaitsForEachReplyAndStopsExactlyAtZero()
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.Success, .12m));
        await session.AdjustAsync(1);
        var replies = new[]
        {
            new TaskCompletionSource<CurrentAdjustmentResponse>(),
            new TaskCompletionSource<CurrentAdjustmentResponse>(),
            new TaskCompletionSource<CurrentAdjustmentResponse>(),
        };
        var sent = 0;
        device.Adjust = direction =>
        {
            Assert.Equal(CurrentAdjustment.Decrease, direction);
            return replies[sent++].Task;
        };
        var reset = session.ResetAsync();
        Assert.Equal(1, sent);
        Assert.False(reset.IsCompleted);
        replies[0].SetResult(new(DeviceCommandStatus.Success, .08m));
        Assert.Equal(2, sent);
        Assert.Equal(.08, session.Current);
        replies[1].SetResult(new(DeviceCommandStatus.Success, .04m));
        Assert.Equal(3, sent);
        replies[2].SetResult(new(DeviceCommandStatus.MinimumReached, 0m));
        await reset;
        Assert.Equal(3, sent);
        Assert.Equal(0, session.Current);
        await session.ResetAsync();
        Assert.Equal(3, sent);
        await session.StopAsync();
    }

    private static DeviceToleranceSession Session(Device device, int seconds = 600) =>
        new(
            ToleranceTests.Route(),
            ToleranceTests.Capability,
            device,
            new() { DurationSeconds = seconds }
        );

    [Fact]
    public async Task StartsOnceAndUpdatesOnlyAfterAcknowledgement()
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        await session.StartAsync();
        Assert.Equal(new[] { "configure", "start:tolerance", "decrease" }, device.Calls);
        Assert.True(session.HasConfirmedCurrent);
        var pending = new TaskCompletionSource<CurrentAdjustmentResponse>();
        device.Adjust = _ => pending.Task;
        var adjustment = session.AdjustAsync(1);
        Assert.Equal(0, session.Current);
        pending.SetResult(new(DeviceCommandStatus.Success, .12m));
        await adjustment;
        Assert.Equal(.12, session.Current);
        Assert.True(session.HasConfirmedCurrent);
        Assert.Equal(.12, session.Configuration.Targets[0].PeakCurrent);
        await session.StopAsync();
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task TimeoutPreservesValueWithoutRetryAndResetStopsOnNoProgress()
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        device.Adjust = _ => Task.FromException<CurrentAdjustmentResponse>(new TimeoutException());
        await Assert.ThrowsAsync<TimeoutException>(() => session.AdjustAsync(1));
        Assert.Equal(0, session.Current);
        Assert.Equal(1, device.Calls.Count(c => c == "increase"));
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.Success, .16m));
        await session.AdjustAsync(1);
        var values = new Queue<decimal>([.12m, .08m, .04m, 0m]);
        device.Adjust = _ =>
            Task.FromResult(
                new CurrentAdjustmentResponse(DeviceCommandStatus.Success, values.Dequeue())
            );
        await session.ResetAsync();
        Assert.Equal(0, session.Current);
        Assert.Empty(values);
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.Success, .12m));
        await session.AdjustAsync(1);
        var count = device.Calls.Count;
        await session.ResetAsync();
        Assert.Equal(count + 1, device.Calls.Count);
        Assert.Contains("未继续下降", session.Status);
        await session.StopAsync();
    }

    [Theory]
    [InlineData(DeviceCommandStatus.InvalidParameter)]
    [InlineData(DeviceCommandStatus.NotConfigured)]
    [InlineData(DeviceCommandStatus.Failed)]
    public async Task RejectedReplyPreservesConfirmedValue(DeviceCommandStatus status)
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        device.Adjust = _ => Task.FromResult(new CurrentAdjustmentResponse(status, 1m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AdjustAsync(1));
        Assert.Equal(0, session.Current);
        Assert.True(session.HasConfirmedCurrent);
        if (status == DeviceCommandStatus.NotConfigured)
            Assert.False(session.IsRunning);
        await session.StopAsync();
    }

    [Fact]
    public async Task BoundsUseReportedCurrentAndInvalidCurrentIsNotApplied()
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.MaximumReached, 2m));
        await session.AdjustAsync(1);
        Assert.Equal(2, session.Current);
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.Success, 2.04m));
        await Assert.ThrowsAsync<ArgumentException>(() => session.AdjustAsync(-1));
        Assert.Equal(2, session.Current);
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.MinimumReached, 0m));
        await session.AdjustAsync(-1);
        Assert.Equal(0, session.Current);
        await session.StopAsync();
    }

    [Fact]
    public async Task ExitDrainsRequestDiscardsLateReplyAndCanRetryStop()
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        var pending = new TaskCompletionSource<CurrentAdjustmentResponse>();
        device.Adjust = _ => pending.Task;
        var adjust = session.AdjustAsync(1);
        var stop = session.StopAsync();
        Assert.DoesNotContain("stop", device.Calls);
        device.StopResult = new(DeviceCommandStatus.Failed);
        pending.SetResult(new(DeviceCommandStatus.Success, .08m));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adjust);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stop);
        Assert.Equal(0, session.Current);
        device.StopResult = DeviceCommandResult.Success;
        await session.StopAsync();
        Assert.Equal(2, device.Calls.Count(c => c == "stop"));
    }

    [Fact]
    public async Task StartupTimeoutAttemptsStopAndDoesNotEnableAdjustment()
    {
        var device = new Device { StartError = new TimeoutException() };
        using var session = Session(device);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        Assert.Equal(new[] { "configure", "start:tolerance", "stop" }, device.Calls);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task DeviceCompletionEndsSession()
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        var ended = new TaskCompletionSource();
        session.Changed += () =>
        {
            if (!session.IsRunning)
                ended.TrySetResult();
        };
        device.Emit(new StimulationCompletedEvent());
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopAsync();
        Assert.DoesNotContain("stop", device.Calls);
    }

    [Fact]
    public async Task DeadlineStopsOnceWithoutRestarting()
    {
        var device = new Device();
        using var session = Session(device, 1);
        await session.StartAsync();
        await device.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.False(session.IsRunning);
        Assert.Single(device.Calls.Where(c => c.StartsWith("start:")));
    }

    [Fact]
    public async Task PageDisablesOtherCommandsWhileWaitingAndBlocksExitOnStopFailure()
    {
        var device = new Device();
        using var session = Session(device);
        using var page = new ToleranceTestPageViewModel(
            ToleranceTests.Route(),
            new Factory(session),
            new Store()
        );
        Assert.False(page.CanAdjust);
        Assert.False(page.CanSave);
        await page.ResumeAsync();
        await page.ResumeAsync();
        var pending = new TaskCompletionSource<CurrentAdjustmentResponse>();
        device.Adjust = _ => pending.Task;
        var adjust = page.IncreaseCommand.ExecuteAsync(null);
        Assert.False(page.DecreaseCommand.CanExecute(null));
        Assert.False(page.ResetCommand.CanExecute(null));
        Assert.False(page.SaveCommand.CanExecute(null));
        pending.SetResult(new(DeviceCommandStatus.Success, .08m));
        await adjust;
        Assert.True(page.CanSave);
        device.StopResult = new(DeviceCommandStatus.Failed);
        Assert.False(await page.PrepareToLeaveAsync());
        device.StopResult = DeviceCommandResult.Success;
        Assert.True(await page.PrepareToLeaveAsync());
    }

    [Fact]
    public void FactoryKeepsSimulationIndependentAndRejectsInvalidRealConfiguration()
    {
        using var simulated = new ToleranceSessionFactory(ToleranceTests.Capability).Create(
            ToleranceTests.Route()
        );
        Assert.True(simulated.IsSimulated);
        var options = new DeviceBackendOptions();
        options.Capabilities.Tolerance = DeviceCapabilitySource.Disabled;
        Assert.Throws<InvalidOperationException>(() =>
            new ToleranceSessionFactory(ToleranceTests.Capability, options).Create(
                ToleranceTests.Route()
            )
        );
        options.Capabilities.Tolerance = DeviceCapabilitySource.Real;
        options.Capabilities.Stimulation = DeviceCapabilitySource.Simulated;
        Assert.Throws<InvalidOperationException>(() =>
            new ToleranceSessionFactory(ToleranceTests.Capability, options).Create(
                ToleranceTests.Route()
            )
        );
        options.Capabilities.Stimulation = DeviceCapabilitySource.Real;
        Assert.Throws<InvalidOperationException>(() =>
            new ToleranceSessionFactory(ToleranceTests.Capability, options).Create(
                ToleranceTests.Route()
            )
        );
    }

    [Fact]
    public async Task ConcurrentAdjustmentsAreSerializedAndResetCancellationSendsNoMoreSteps()
    {
        var device = new Device();
        using var session = Session(device);
        await session.StartAsync();
        var first = new TaskCompletionSource<CurrentAdjustmentResponse>();
        device.Adjust = _ => first.Task;
        var increase = session.AdjustAsync(1);
        var decrease = session.AdjustAsync(-1);
        Assert.Equal(1, device.Calls.Count(c => c == "increase"));
        Assert.Equal(1, device.Calls.Count(c => c == "decrease"));
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.Success, 0m));
        first.SetResult(new(DeviceCommandStatus.Success, .08m));
        await Task.WhenAll(increase, decrease);
        Assert.Equal(0, session.Current);
        device.Adjust = _ =>
            Task.FromResult(new CurrentAdjustmentResponse(DeviceCommandStatus.Success, .16m));
        await session.AdjustAsync(1);
        var resetReply = new TaskCompletionSource<CurrentAdjustmentResponse>();
        device.Adjust = _ => resetReply.Task;
        var reset = session.ResetAsync();
        var stop = session.StopAsync();
        var count = device.Calls.Count;
        resetReply.SetResult(new(DeviceCommandStatus.Success, .12m));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
        await stop;
        Assert.Equal(count + 1, device.Calls.Count);
        Assert.Equal("stop", device.Calls.Last());
        Assert.Equal(.16, session.Current);
    }

    [Fact]
    public async Task DisconnectDisablesAdjustmentAndRetainsShutdownStopObligation()
    {
        var device = new Device();
        var lifetime = new ToleranceSessionLifetime();
        using var session = new DeviceToleranceSession(
            ToleranceTests.Route(),
            ToleranceTests.Capability,
            device,
            new(),
            lifetime: lifetime
        );
        await session.StartAsync();
        var ended = new TaskCompletionSource();
        session.Changed += () =>
        {
            if (!session.IsRunning)
                ended.TrySetResult();
        };
        device.Emit(
            new DeviceStateChangedEvent(
                new(
                    DeviceConnectionState.Disconnected,
                    DeviceOperationState.Unknown,
                    null,
                    DateTimeOffset.UtcNow
                )
            )
        );
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("断开", session.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AdjustAsync(1));
        await lifetime.StopAllAsync();
        Assert.Contains("stop", device.Calls);
        var count = device.Calls.Count;
        await lifetime.StopAllAsync();
        Assert.Equal(count, device.Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidDurationIsRejectedBeforeSending(int seconds)
    {
        var device = new Device();
        Assert.Throws<ArgumentOutOfRangeException>(() => Session(device, seconds));
        Assert.Empty(device.Calls);
    }

    private sealed class Factory(IToleranceSession session) : IToleranceSessionFactory
    {
        public IToleranceSession Create(ToleranceTestRouteData route) => session;
    }

    private sealed class Store : ITolerancePersistenceService
    {
        public Task<ToleranceResultSnapshot> SaveAsync(
            ToleranceTestRouteData route,
            double current,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<ToleranceHistoryPage> QueryAsync(
            ToleranceHistoryQuery query,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class Device : IEggtCsDevice, IStimulationCapability, IToleranceCapability
    {
        private readonly Channel<DeviceEventEnvelope> _events =
            Channel.CreateUnbounded<DeviceEventEnvelope>();
        public List<string> Calls { get; } = [];
        public TaskCompletionSource Stopped { get; } = new();
        public Func<CurrentAdjustment, Task<CurrentAdjustmentResponse>> Adjust { get; set; } =
            _ =>
                Task.FromResult(
                    new CurrentAdjustmentResponse(DeviceCommandStatus.MinimumReached, 0m)
                );
        public StimulationConfiguration? Configuration { get; private set; }
        public DeviceCommandResult StopResult { get; set; } = DeviceCommandResult.Success;
        public Exception? StartError { get; set; }
        public DeviceIdentity Identity { get; } = new(new("test"), "test", null, null);
        public DeviceCapabilities Capabilities =>
            DeviceCapabilities.Simulator with
            {
                StimulationPhysicalChannelCount = 16,
            };
        public DeviceStateSnapshot State =>
            new(
                DeviceConnectionState.Connected,
                DeviceOperationState.Ready,
                100,
                DateTimeOffset.UtcNow
            );
        public IEegAcquisitionCapability? EegAcquisition => null;
        public IStimulationCapability Stimulation => this;
        public IImpedanceCapability? Impedance => null;
        public IToleranceCapability Tolerance => this;

        public Task<DeviceStatusResponse> ReadStatusAsync(
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new DeviceStatusResponse(
                    DeviceCommandStatus.Success,
                    DeviceOperationState.Ready,
                    100
                )
            );

        public IAsyncEnumerable<DeviceEventEnvelope> ReadEventsAsync(
            CancellationToken cancellationToken = default
        ) => _events.Reader.ReadAllAsync(cancellationToken);

        public void Emit(DeviceEvent item) =>
            _events.Writer.TryWrite(
                new(
                    Identity.DeviceId,
                    Guid.Empty,
                    DateTimeOffset.UtcNow,
                    item,
                    EventDeliveryClass.Control
                )
            );

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<DeviceCommandResult> ConfigureAsync(
            StimulationConfiguration configuration,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add("configure");
            Configuration = configuration;
            return Task.FromResult(DeviceCommandResult.Success);
        }

        public Task<DeviceCommandResult> StartAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add($"start:{duration.TotalSeconds}");
            return StartError is null
                ? Task.FromResult(DeviceCommandResult.Success)
                : Task.FromException<DeviceCommandResult>(StartError);
        }

        public Task<DeviceCommandResult> StartAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start:tolerance");
            return StartError is null
                ? Task.FromResult(DeviceCommandResult.Success)
                : Task.FromException<DeviceCommandResult>(StartError);
        }

        public Task<DeviceCommandResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            Stopped.TrySetResult();
            return Task.FromResult(StopResult);
        }

        public Task<CurrentAdjustmentResponse> AdjustCurrentAsync(
            CurrentAdjustment adjustment,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(adjustment == CurrentAdjustment.Increase ? "increase" : "decrease");
            return Adjust(adjustment);
        }
    }
}
