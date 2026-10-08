using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ApplicationRestartTests
{
    [Fact]
    public async Task RestartSafelyShutsDownLaunchesWaitingChildThenExits()
    {
        var events = new List<string>();
        var launcher = new StubLauncher(events);
        var service = new ApplicationRestartService(
            new StubShutdownCoordinator(events),
            new StubExecutableResolver(events, "test-app"),
            launcher,
            new StubLifetimeExit(events)
        );

        await service.RestartAsync();

        Assert.Equal(["shutdown:factory-reset", "resolve", "launch:test-app", "exit"], events);
        Assert.Equal(
            ApplicationRestartProtocol.CreateParentProcessArgument(Environment.ProcessId),
            Assert.Single(launcher.Arguments)
        );
    }

    [Fact]
    public async Task LaunchFailureDoesNotExitAndCanBeRetried()
    {
        var events = new List<string>();
        var launcher = new StubLauncher(events) { FailNextStart = true };
        var lifetime = new StubLifetimeExit(events);
        var service = new ApplicationRestartService(
            new StubShutdownCoordinator(events),
            new StubExecutableResolver(events, "test-app"),
            launcher,
            lifetime
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestartAsync());
        Assert.False(lifetime.Exited);

        await service.RestartAsync();

        Assert.True(lifetime.Exited);
        Assert.Equal(2, launcher.StartCount);
    }

    [Fact]
    public async Task SuccessfulRestartRequestIsIdempotent()
    {
        var events = new List<string>();
        var launcher = new StubLauncher(events);
        var lifetime = new StubLifetimeExit(events);
        var service = new ApplicationRestartService(
            new StubShutdownCoordinator(events),
            new StubExecutableResolver(events, "test-app"),
            launcher,
            lifetime
        );

        await service.RestartAsync();
        await service.RestartAsync();

        Assert.Equal(1, launcher.StartCount);
        Assert.Equal(1, lifetime.ExitCount);
    }

    [Fact]
    public void RestartProtocolExtractsOnlyItsInternalArgument()
    {
        var arguments = new[]
        {
            "--user-option",
            ApplicationRestartProtocol.CreateParentProcessArgument(1234),
            "value",
        };

        var found = ApplicationRestartProtocol.TryTakeParentProcessId(
            arguments,
            out var parentProcessId,
            out var remaining
        );

        Assert.True(found);
        Assert.Equal(1234, parentProcessId);
        Assert.Equal(["--user-option", "value"], remaining);
    }

    [Fact]
    public void RestartProtocolTreatsAlreadyExitedParentAsComplete()
    {
        Assert.True(ApplicationRestartProtocol.WaitForParentExit(int.MaxValue, TimeSpan.Zero));
    }

    private sealed class StubShutdownCoordinator(List<string> events)
        : IApplicationShutdownCoordinator
    {
        public Task ShutdownAsync(
            string source,
            Exception? exception = null,
            bool cleanupTemporaryFiles = true,
            CancellationToken cancellationToken = default
        )
        {
            events.Add($"shutdown:{source}");
            return Task.CompletedTask;
        }
    }

    private sealed class StubExecutableResolver(List<string> events, string executablePath)
        : IApplicationExecutableResolver
    {
        public string Resolve()
        {
            events.Add("resolve");
            return executablePath;
        }
    }

    private sealed class StubLauncher(List<string> events) : IApplicationProcessLauncher
    {
        public bool FailNextStart { get; set; }
        public int StartCount { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public void Start(string executablePath, IReadOnlyList<string> arguments)
        {
            StartCount++;
            Arguments = arguments;
            events.Add($"launch:{executablePath}");
            if (FailNextStart)
            {
                FailNextStart = false;
                throw new InvalidOperationException("launch failed");
            }
        }
    }

    private sealed class StubLifetimeExit(List<string> events) : IApplicationLifetimeExit
    {
        public bool Exited => ExitCount > 0;
        public int ExitCount { get; private set; }

        public void Exit()
        {
            ExitCount++;
            events.Add("exit");
        }
    }
}
