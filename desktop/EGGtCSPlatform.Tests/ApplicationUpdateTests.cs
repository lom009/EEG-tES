using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ApplicationUpdateTests
{
    [Fact]
    public void DefaultSourceBaseAddressMatchesTheHttpServerRoot()
    {
        Assert.Equal("http://127.0.0.1:8080", new ApplicationUpdateOptions().SourceBaseAddress);
    }

    [Fact]
    public void OptionsRequireValidBaseAddressOnlyWhenStartupCheckIsEnabled()
    {
        Assert.True(
            new ApplicationUpdateOptions
            {
                AutoCheckOnStartup = false,
                SourceBaseAddress = string.Empty,
            }.IsValid()
        );
        Assert.False(
            new ApplicationUpdateOptions
            {
                AutoCheckOnStartup = true,
                SourceBaseAddress = "  ",
            }.IsValid()
        );
    }

    [Fact]
    public void SourceResolverCombinesHttpBaseAddressAndTarget()
    {
        Assert.Equal(
            "http://127.0.0.1:8080/win/x64",
            ApplicationUpdateSource.Resolve("http://127.0.0.1:8080", "win/x64")
        );
        Assert.Equal(
            "https://updates.example.test/app/osx/arm64",
            ApplicationUpdateSource.Resolve("https://updates.example.test/app/", "/osx/arm64/")
        );
    }

    [Theory]
    [InlineData("win-x86", "win/x86")]
    [InlineData("win-x64", "win/x64")]
    [InlineData("win-arm64", "win/arm64")]
    [InlineData("linux-x64", "linux/x64")]
    [InlineData("linux-arm64", "linux/arm64")]
    [InlineData("osx-x64", "osx/x64")]
    [InlineData("osx-arm64", "osx/arm64")]
    public void UpdateTargetMapsSupportedRuntimeIdentifiers(
        string runtimeIdentifier,
        string expected
    )
    {
        Assert.Equal(expected, ApplicationUpdateTarget.GetFeedPath(runtimeIdentifier));
    }

    [Fact]
    public void CurrentUpdateTargetUsesTheActualRuntimeIdentifier()
    {
        Assert.Equal(
            RuntimeInformation.RuntimeIdentifier,
            ApplicationUpdateTarget.CurrentRuntimeIdentifier
        );
        Assert.Equal(
            ApplicationUpdateTarget.GetFeedPath(RuntimeInformation.RuntimeIdentifier),
            ApplicationUpdateTarget.CurrentFeedPath
        );
        Assert.DoesNotContain(
            "development",
            ApplicationUpdateTarget.CurrentFeedPath,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("../feed")]
    [InlineData("file:///tmp/feed")]
    [InlineData("http://localhost/updates?channel=win")]
    [InlineData("https://localhost/updates#latest")]
    public void OptionsRejectInvalidHttpBaseAddresses(string sourceBaseAddress)
    {
        Assert.False(
            new ApplicationUpdateOptions
            {
                AutoCheckOnStartup = true,
                SourceBaseAddress = sourceBaseAddress,
            }.IsValid()
        );
    }

    [Fact]
    public void SourceResolverRejectsTraversalAndUnknownRuntimeIdentifiers()
    {
        Assert.Throws<ArgumentException>(() =>
            ApplicationUpdateSource.Resolve("http://localhost:8080/updates", "../win/x64")
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ApplicationUpdateTarget.GetFeedPath("linux-x86")
        );
    }

    [Fact]
    public async Task DownloadReportsClampedProgressThenShutsDownAndApplies()
    {
        var update = new AvailableApplicationUpdate("1.1.4", "1.1.5");
        var shutdown = new StubShutdownCoordinator();
        ApplicationUpdateViewModel model = null!;
        var client = new StubUpdateClient
        {
            DownloadAction = (_, progress, _) =>
            {
                Assert.True(model.Busy);
                Assert.True(model.IsDownloading);
                Assert.True(model.ShowProgress);
                progress(-10);
                progress(45);
                progress(120);
                return Task.CompletedTask;
            },
            ApplyAction = _ => Assert.Equal(1, shutdown.CallCount),
        };
        model = new ApplicationUpdateViewModel(client, shutdown);
        model.ShowAvailableUpdate(update);

        await model.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(100, model.Progress);
        Assert.Equal(1, shutdown.CallCount);
        Assert.Equal("application-update", shutdown.LastSource);
        Assert.Equal(1, client.ApplyCount);
    }

    [Fact]
    public async Task FailedDownloadCanRetryTheSameUpdate()
    {
        var attempts = 0;
        var client = new StubUpdateClient
        {
            DownloadAction = (_, progress, _) =>
            {
                attempts++;
                if (attempts == 1)
                    throw new IOException("feed unavailable");
                progress(100);
                return Task.CompletedTask;
            },
        };
        var shutdown = new StubShutdownCoordinator();
        var model = new ApplicationUpdateViewModel(client, shutdown);
        model.ShowAvailableUpdate(new AvailableApplicationUpdate("1.1.4", "1.1.5"));

        await model.PrimaryCommand.ExecuteAsync(null);

        Assert.True(model.HasError);
        Assert.Equal("重试", model.PrimaryButtonText);
        Assert.Equal(0, shutdown.CallCount);
        Assert.Equal(0, client.ApplyCount);

        await model.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(2, attempts);
        Assert.Equal(1, shutdown.CallCount);
        Assert.Equal(1, client.ApplyCount);
    }

    [Fact]
    public async Task FailedCheckCanRetryAndShowAvailableUpdate()
    {
        var client = new StubUpdateClient
        {
            CheckResult = new AvailableApplicationUpdate("1.1.4", "1.1.5"),
        };
        var model = new ApplicationUpdateViewModel(client, new StubShutdownCoordinator());
        model.ShowCheckFailure(new IOException("missing feed"));

        await model.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal("发现新版本", model.Title);
        Assert.Equal("立即更新", model.PrimaryButtonText);
        Assert.False(model.HasError);
        Assert.Equal(1, client.CheckCount);
    }

    [Fact]
    public async Task DialogCannotBeDismissedWhileDownloadIsRunning()
    {
        var downloadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var finishDownload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var client = new StubUpdateClient
        {
            DownloadAction = async (_, _, _) =>
            {
                downloadStarted.SetResult();
                await finishDownload.Task;
            },
        };
        var model = new ApplicationUpdateViewModel(client, new StubShutdownCoordinator());
        model.ShowAvailableUpdate(new AvailableApplicationUpdate("1.1.4", "1.1.5"));
        var closeRequests = 0;
        model.CloseRequested += () => closeRequests++;

        var updateTask = model.PrimaryCommand.ExecuteAsync(null);
        await downloadStarted.Task;
        model.LaterCommand.Execute(null);

        Assert.True(model.Busy);
        Assert.False(model.CanClose);
        Assert.Equal(0, closeRequests);

        finishDownload.SetResult();
        await updateTask;
    }

    [Fact]
    public async Task DownloadCanBeCanceledAndRetriedWithoutApplyingCanceledUpdate()
    {
        var downloadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        CancellationToken capturedToken = default;
        var attempts = 0;
        var client = new StubUpdateClient
        {
            DownloadAction = async (_, progress, cancellationToken) =>
            {
                attempts++;
                capturedToken = cancellationToken;
                if (attempts == 1)
                {
                    downloadStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                progress(100);
            },
        };
        var shutdown = new StubShutdownCoordinator();
        var model = new ApplicationUpdateViewModel(client, shutdown);
        model.ShowAvailableUpdate(new AvailableApplicationUpdate("1.1.4", "1.1.5"));

        var canceledUpdateTask = model.PrimaryCommand.ExecuteAsync(null);
        await downloadStarted.Task;

        Assert.True(model.IsDownloading);
        Assert.True(model.CanCancelDownload);
        model.CancelDownloadCommand.Execute(null);

        Assert.True(capturedToken.IsCancellationRequested);
        Assert.False(model.CanCancelDownload);
        Assert.False(model.CancelDownloadCommand.CanExecute(null));
        Assert.Equal("正在取消下载…", model.StatusText);
        await canceledUpdateTask;

        Assert.False(model.Busy);
        Assert.False(model.IsDownloading);
        Assert.False(model.HasError);
        Assert.False(model.ShowProgress);
        Assert.Equal(0, model.Progress);
        Assert.Equal("立即更新", model.PrimaryButtonText);
        Assert.Equal("下载已取消，可重新下载。", model.StatusText);
        Assert.True(model.ShowSecondaryButton);
        Assert.Equal(0, shutdown.CallCount);
        Assert.Equal(0, client.ApplyCount);

        await model.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(2, attempts);
        Assert.Equal(1, shutdown.CallCount);
        Assert.Equal(1, client.ApplyCount);
    }

    [Fact]
    public async Task DownloadCannotBeCanceledAfterInstallationPreparationStarts()
    {
        var shutdownStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var finishShutdown = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var shutdown = new StubShutdownCoordinator
        {
            ShutdownAction = async () =>
            {
                shutdownStarted.SetResult();
                await finishShutdown.Task;
            },
        };
        var client = new StubUpdateClient();
        var model = new ApplicationUpdateViewModel(client, shutdown);
        model.ShowAvailableUpdate(new AvailableApplicationUpdate("1.1.4", "1.1.5"));

        var updateTask = model.PrimaryCommand.ExecuteAsync(null);
        await shutdownStarted.Task;

        Assert.True(model.Busy);
        Assert.False(model.IsDownloading);
        Assert.False(model.CanCancelDownload);
        Assert.False(model.CancelDownloadCommand.CanExecute(null));

        finishShutdown.SetResult();
        await updateTask;

        Assert.Equal(1, client.ApplyCount);
    }

    private sealed class StubUpdateClient : IApplicationUpdateClient
    {
        public bool IsInstalled { get; set; } = true;

        public AvailableApplicationUpdate? CheckResult { get; set; }

        public Func<
            AvailableApplicationUpdate,
            Action<int>,
            CancellationToken,
            Task
        > DownloadAction { get; set; } = (_, _, _) => Task.CompletedTask;

        public Action<AvailableApplicationUpdate>? ApplyAction { get; set; }

        public int CheckCount { get; private set; }

        public int ApplyCount { get; private set; }

        public Task<AvailableApplicationUpdate?> CheckForUpdatesAsync()
        {
            CheckCount++;
            return Task.FromResult(CheckResult);
        }

        public Task DownloadAsync(
            AvailableApplicationUpdate update,
            Action<int> progress,
            CancellationToken cancellationToken = default
        ) => DownloadAction(update, progress, cancellationToken);

        public void ApplyAndRestart(AvailableApplicationUpdate update)
        {
            ApplyCount++;
            ApplyAction?.Invoke(update);
        }
    }

    private sealed class StubShutdownCoordinator : IApplicationShutdownCoordinator
    {
        public Func<Task>? ShutdownAction { get; set; }

        public int CallCount { get; private set; }

        public string? LastSource { get; private set; }

        public async Task ShutdownAsync(
            string source,
            Exception? exception = null,
            bool cleanupTemporaryFiles = true,
            CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            LastSource = source;
            if (ShutdownAction is not null)
                await ShutdownAction();
        }
    }
}
