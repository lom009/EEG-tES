using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ApplicationExitWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceDisconnectLogsReflectContainerDisposalOutcome(bool fail)
    {
        var selection = new DeviceSelectionContext();
        selection.SetConnectedDevice("device-1", "test");
        var services = new ServiceCollection()
            .AddSingleton<EGGtCSPlatform.Interfaces.IDeviceSelectionContext>(selection)
            .AddSingleton(_ => new DisposalService(fail))
            .BuildServiceProvider();
        _ = services.GetRequiredService<DisposalService>();
        var logger = new RecordingApplicationLogger();
        if (fail)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                App.DisposeServicesAsync(services, logger)
            );
        else
            await App.DisposeServicesAsync(services, logger);
        Assert.Equal(2, logger.Entries.Length);
        Assert.Equal("Device.DisconnectRequested", logger.Entries[0].EventName);
        Assert.Equal(
            fail ? "Device.DisconnectFailed" : "Device.Disconnected",
            logger.Entries[1].EventName
        );
        Assert.All(logger.Entries, entry => Assert.Equal("device-1", entry.CorrelationId));
    }

    private sealed class DisposalService(bool fail) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            fail
                ? ValueTask.FromException(new InvalidOperationException("dispose failed"))
                : ValueTask.CompletedTask;
    }

    [Fact]
    public Task LoggingRemainsAvailableThroughDisposalAndFlushesOnceBeforeExit() =>
        OnUiThread(async () =>
        {
            var events = new List<string>();
            var logger = new RecordingApplicationLogger();
            var workflow = new ApplicationExitWorkflow(
                () => Task.CompletedTask,
                () => throw new InvalidOperationException("dispose failed"),
                () => events.Add("lease"),
                () => events.Add("exit"),
                exception =>
                    logger.Write(
                        ApplicationLogLevel.Error,
                        "test",
                        "CleanupFailed",
                        "cleanup",
                        exception: exception
                    ),
                async () =>
                {
                    Assert.Equal("CleanupFailed", Assert.Single(logger.Entries).EventName);
                    logger.Write(ApplicationLogLevel.Info, "test", "Exit", "finished");
                    await logger.FlushAsync();
                    events.Add("flush");
                }
            );
            var first = workflow.RunAsync();
            Assert.Same(first, workflow.RunAsync());
            await first;
            Assert.Equal(new[] { "lease", "flush", "exit" }, events);
            Assert.Equal(2, logger.Entries.Length);
        });

    [Fact]
    public Task DisposalCanResumeOnUiContextAndLeaseIsReleasedOnOriginalThread() =>
        OnUiThread(async () =>
        {
            var threadId = Environment.CurrentManagedThreadId;
            var events = new List<string>();
            var services = new ServiceCollection()
                .AddSingleton(_ => new HeartbeatService(events))
                .BuildServiceProvider();
            var heartbeat = services.GetRequiredService<HeartbeatService>();
            var workflow = new ApplicationExitWorkflow(
                async () =>
                {
                    await Task.Yield();
                    events.Add("shutdown");
                },
                () => services.DisposeAsync().AsTask(),
                () =>
                {
                    Assert.Equal(threadId, Environment.CurrentManagedThreadId);
                    events.Add("lease");
                },
                () => events.Add("exit"),
                exception =>
                    throw new InvalidOperationException("Unexpected cleanup failure", exception)
            );

            var first = workflow.RunAsync();
            Assert.True(workflow.IsStarted);
            Assert.False(workflow.IsComplete);
            Assert.Same(first, workflow.RunAsync());
            await first;
            Assert.True(heartbeat.Completed);
            Assert.True(workflow.IsComplete);
            Assert.Same(first, workflow.RunAsync());
            Assert.Equal(new[] { "shutdown", "heartbeat", "dispose", "lease", "exit" }, events);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CleanupFailureOrCancellationStillDisposesAndExits(bool canceled) =>
        OnUiThread(async () =>
        {
            var events = new List<string>();
            var failures = new List<Exception>();
            var workflow = new ApplicationExitWorkflow(
                async () =>
                {
                    await Task.Yield();
                    if (canceled)
                        throw new OperationCanceledException();
                    throw new InvalidOperationException("stop failed");
                },
                async () =>
                {
                    await Task.Yield();
                    events.Add("dispose");
                    throw new Exception("dispose failed");
                },
                () => events.Add("lease"),
                () => events.Add("exit"),
                failures.Add
            );
            await workflow.RunAsync();
            Assert.Equal(2, failures.Count);
            Assert.Equal(new[] { "dispose", "lease", "exit" }, events);
        });

    internal static async Task OnUiThread(Func<Task> test)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var task = test();
                while (!task.IsCompleted)
                    context.RunOne();
                task.GetAwaiter().GetResult();
                finished.SetResult();
            }
            catch (Exception exception)
            {
                finished.SetException(exception);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue =
            new();

        public override void Post(SendOrPostCallback callback, object? state) =>
            _queue.Add((callback, state));

        public void RunOne()
        {
            if (!_queue.TryTake(out var work, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("UI cleanup stopped making progress.");
            work.Callback(work.State);
        }

        public void Dispose() => _queue.Dispose();
    }

    private sealed class HeartbeatService : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly List<string> _events;
        private readonly Task _heartbeat;
        public bool Completed { get; private set; }

        public HeartbeatService(List<string> events)
        {
            _events = events;
            _heartbeat = RunAsync();
        }

        private async Task RunAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            try
            {
                while (await timer.WaitForNextTickAsync(_cancellation.Token)) { }
            }
            catch (OperationCanceledException) { }
            Completed = true;
            _events.Add("heartbeat");
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            await _heartbeat;
            _events.Add("dispose");
            _cancellation.Dispose();
        }
    }
}
