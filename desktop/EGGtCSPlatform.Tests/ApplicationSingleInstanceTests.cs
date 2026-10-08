using System;
using System.Collections.Generic;
using System.IO;
using EGGtCSPlatform.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ApplicationSingleInstanceTests
{
    [Fact]
    public void SingleInstanceDefaultsToEnabled()
    {
        var options = new ApplicationBehaviorOptions();

        Assert.True(options.SingleInstance);
    }

    [Fact]
    public void PreviousCrashDialogDefaultsToDisabled()
    {
        var options = new ApplicationBehaviorOptions();

        Assert.False(options.ShowPreviousCrashOnStartup);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void SingleInstanceBindsFromConfiguration(string configuredValue, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Application:SingleInstance"] = configuredValue }
            )
            .Build();

        var options = configuration
            .GetSection(ApplicationBehaviorOptions.SectionName)
            .Get<ApplicationBehaviorOptions>();

        Assert.NotNull(options);
        Assert.Equal(expected, options.SingleInstance);
    }

    [Fact]
    public void OnlyOneLeaseCanHoldTheLockFileUntilItIsReleased()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"eggtcs-instance-test-{Guid.NewGuid():N}"
        );
        var lockFilePath = Path.Combine(directory, "application.instance.lock");
        try
        {
            Assert.True(ApplicationSingleInstanceLease.TryAcquireFile(lockFilePath, out var first));
            Assert.NotNull(first);
            Assert.False(
                ApplicationSingleInstanceLease.TryAcquireFile(lockFilePath, out var second)
            );
            Assert.Null(second);

            first!.Dispose();
            Assert.True(
                ApplicationSingleInstanceLease.TryAcquireFile(lockFilePath, out var afterRelease)
            );
            afterRelease!.Dispose();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CurrentUserMutexNameIsStable()
    {
        Assert.Equal(
            ApplicationSingleInstanceLease.CurrentUserMutexName,
            ApplicationSingleInstanceLease.CurrentUserMutexName
        );
        Assert.StartsWith(
            "EGGtCSPlatform.SingleInstance.",
            ApplicationSingleInstanceLease.CurrentUserMutexName
        );
    }

    [Fact]
    public void CurrentUserLockFileUsesLocalApplicationData()
    {
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationSingleInstanceLease.CurrentUserLockFilePath
        );
    }
}
