using System;
using System.IO;
using EGGtCSPlatform.Persistence;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void ApplicationDataPathsUseLocalApplicationDataRoot()
    {
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.ProductName
        );

        Assert.Equal(expectedRoot, AppPaths.Root);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "appsettings.default.json"),
            AppPaths.DefaultConfigurationPath
        );
        Assert.Equal(
            Path.Combine(expectedRoot, "appsettings.json"),
            AppPaths.UserConfigurationPath
        );
        Assert.Equal(
            Path.Combine(expectedRoot, "config-backups"),
            AppPaths.ConfigurationBackupDirectory
        );
        Assert.Equal(
            Path.Combine(expectedRoot, "appsettings.DeviceBackend.backup.json"),
            AppPaths.DeviceBackendConfigurationBackupPath
        );
        Assert.Equal(Path.Combine(expectedRoot, "data"), AppPaths.DatabaseDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "data", "eggtcs.db"), AppPaths.DatabasePath);
        Assert.Equal(Path.Combine(expectedRoot, "recordings"), AppPaths.RecordingsDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "temp", "eeg"), AppPaths.TemporaryEegDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "export"), AppPaths.ExportDirectory);
    }
}
