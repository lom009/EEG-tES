using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EGGtCSPlatform.Configuration;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ApplicationConfigurationResetTests
{
    [Fact]
    public async Task ResetCopiesDefaultBytesExactlyBacksUpUserAndRemovesModeBackup()
    {
        using var fixture = new ResetFixture();
        var defaultBytes = Encoding.UTF8.GetBytes(
            """
            {
              "_meta": { "schemaVersion": 1 },
              "ApplicationUpdate": { "SourceBaseAddress": "https://updates.example/new" },
              "Value": "默认值"
            }
            """
        );
        var userBytes = Encoding.UTF8.GetBytes(
            """
            { "_meta": { "schemaVersion": 1 }, "Value": "用户值" }
            """
        );
        File.WriteAllBytes(fixture.DefaultPath, defaultBytes);
        File.WriteAllBytes(fixture.UserPath, userBytes);
        File.WriteAllText(fixture.ModeBackupPath, "old mode");

        await fixture.Service.ResetAsync();

        Assert.Equal(defaultBytes, File.ReadAllBytes(fixture.UserPath));
        Assert.Equal(userBytes, File.ReadAllBytes(Assert.Single(fixture.Backups()).FullName));
        Assert.False(File.Exists(fixture.ModeBackupPath));
    }

    [Fact]
    public async Task ResetOfAlreadyDefaultConfigurationDoesNotCreateRedundantBackup()
    {
        using var fixture = new ResetFixture();
        var content = ResetFixture.ValidDefaultBytes();
        File.WriteAllBytes(fixture.DefaultPath, content);
        File.WriteAllBytes(fixture.UserPath, content);
        File.WriteAllText(fixture.ModeBackupPath, "old mode");

        await fixture.Service.ResetAsync();

        Assert.Equal(content, File.ReadAllBytes(fixture.UserPath));
        Assert.Empty(fixture.Backups());
        Assert.False(File.Exists(fixture.ModeBackupPath));
    }

    [Fact]
    public async Task ResetRetainsOnlyFiveNewestConfigurationBackups()
    {
        using var fixture = new ResetFixture();
        File.WriteAllBytes(fixture.DefaultPath, ResetFixture.ValidDefaultBytes());
        File.WriteAllText(fixture.UserPath, "{\"_meta\":{\"schemaVersion\":1},\"Old\":true}");
        Directory.CreateDirectory(fixture.BackupDirectory);
        for (var index = 0; index < 7; index++)
        {
            File.WriteAllText(
                Path.Combine(
                    fixture.BackupDirectory,
                    $"appsettings.2020010{index}T0000000000000Z.old.json"
                ),
                "{}"
            );
        }

        await fixture.Service.ResetAsync();

        Assert.Equal(
            ApplicationConfigurationSynchronizer.RetainedBackupCount,
            fixture.Backups().Length
        );
    }

    [Fact]
    public async Task ConcurrentResetRequestsProduceOneExactConfiguration()
    {
        using var fixture = new ResetFixture();
        var defaultBytes = ResetFixture.ValidDefaultBytes();
        File.WriteAllBytes(fixture.DefaultPath, defaultBytes);
        File.WriteAllText(fixture.UserPath, "{\"_meta\":{\"schemaVersion\":1},\"Old\":true}");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Service.ResetAsync()));

        Assert.Equal(defaultBytes, File.ReadAllBytes(fixture.UserPath));
        Assert.Single(fixture.Backups());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("wrong-schema")]
    public async Task InvalidDefaultPreservesExistingUserConfiguration(string caseName)
    {
        using var fixture = new ResetFixture();
        var userBytes = Encoding.UTF8.GetBytes(
            "{\"_meta\":{\"schemaVersion\":1},\"Value\":\"keep\"}"
        );
        File.WriteAllBytes(fixture.UserPath, userBytes);
        if (caseName == "invalid-json")
            File.WriteAllText(fixture.DefaultPath, "not-json");
        else if (caseName == "wrong-schema")
            File.WriteAllText(fixture.DefaultPath, "{\"_meta\":{\"schemaVersion\":2}}");

        await Assert.ThrowsAsync<ApplicationConfigurationException>(() =>
            fixture.Service.ResetAsync()
        );

        Assert.Equal(userBytes, File.ReadAllBytes(fixture.UserPath));
        Assert.Empty(fixture.Backups());
    }

    private sealed class ResetFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"eggtcs-reset-{Guid.NewGuid():N}"
        );

        public ResetFixture()
        {
            Directory.CreateDirectory(_directory);
            DefaultPath = Path.Combine(_directory, "appsettings.default.json");
            UserPath = Path.Combine(_directory, "user", "appsettings.json");
            BackupDirectory = Path.Combine(_directory, "user", "config-backups");
            ModeBackupPath = Path.Combine(
                _directory,
                "user",
                "appsettings.DeviceBackend.backup.json"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
            Service = new ApplicationConfigurationResetService(
                DefaultPath,
                UserPath,
                BackupDirectory,
                ModeBackupPath
            );
        }

        public string DefaultPath { get; }
        public string UserPath { get; }
        public string BackupDirectory { get; }
        public string ModeBackupPath { get; }
        public ApplicationConfigurationResetService Service { get; }

        public FileInfo[] Backups() =>
            Directory.Exists(BackupDirectory)
                ? new DirectoryInfo(BackupDirectory).GetFiles("appsettings.*.json")
                : [];

        public static byte[] ValidDefaultBytes() =>
            Encoding.UTF8.GetBytes(
                "{\"_meta\":{\"schemaVersion\":1},\"ApplicationUpdate\":{\"SourceBaseAddress\":\"https://updates.example\"}}"
            );

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
