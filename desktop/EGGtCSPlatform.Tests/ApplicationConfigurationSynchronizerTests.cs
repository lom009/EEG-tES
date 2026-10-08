using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EGGtCSPlatform.Bootstrap;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ApplicationConfigurationSynchronizerTests
{
    [Fact]
    public void MissingUserConfigurationCopiesCurrentDefault()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());

        var result = fixture.Synchronize();

        Assert.True(File.Exists(fixture.UserPath));
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(File.ReadAllText(fixture.DefaultPath)),
                JsonNode.Parse(File.ReadAllText(fixture.UserPath))
            )
        );
        Assert.Equal("https://updates.example/v1", result.SourceBaseAddress);
        Assert.Empty(result.ResetPaths);
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public void ExistingValuesSurviveWhileNewPropertiesAreAddedAndRemovedPropertiesDisappear()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        fixture.WriteUser(
            JsonNode
                .Parse(
                    """
                    {
                      "_meta": { "schemaVersion": 1 },
                      "Application": { "SingleInstance": false, "Removed": true },
                      "ApplicationUpdate": {
                        "AutoCheckOnStartup": false,
                        "SourceBaseAddress": "https://user.invalid"
                      },
                      "Nested": {
                        "Name": "user",
                        "Removed": 12
                      },
                      "ObjectItems": [
                        { "Id": "custom", "Enabled": false, "Removed": "old" }
                      ],
                      "PrimitiveItems": [9, 8],
                      "RemovedSection": { "Value": 1 }
                    }
                    """
                )!
                .AsObject()
        );

        fixture.Synchronize();
        var root = fixture.ReadUser();

        Assert.False(root["Application"]!["SingleInstance"]!.GetValue<bool>());
        Assert.Null(root["Application"]!["Removed"]);
        Assert.True(root["Application"]!["Added"]!.GetValue<bool>());
        Assert.False(root["ApplicationUpdate"]!["AutoCheckOnStartup"]!.GetValue<bool>());
        Assert.Equal(
            "https://updates.example/v1",
            root["ApplicationUpdate"]!["SourceBaseAddress"]!.GetValue<string>()
        );
        Assert.Equal("user", root["Nested"]!["Name"]!.GetValue<string>());
        Assert.Equal(5, root["Nested"]!["Added"]!.GetValue<int>());
        Assert.Null(root["Nested"]!["Removed"]);
        Assert.Null(root["RemovedSection"]);

        var objectItem = root["ObjectItems"]!.AsArray().Single()!.AsObject();
        Assert.Equal("custom", objectItem["Id"]!.GetValue<string>());
        Assert.False(objectItem["Enabled"]!.GetValue<bool>());
        Assert.Equal("new", objectItem["NewProperty"]!.GetValue<string>());
        Assert.Null(objectItem["Removed"]);
        Assert.Equal(
            [9, 8],
            root["PrimitiveItems"]!.AsArray().Select(item => item!.GetValue<int>())
        );
        Assert.Single(fixture.Backups());
    }

    [Fact]
    public void ReorderedObjectArrayUsesMatchingDefaultItemStructure()
    {
        using var fixture = new ConfigurationFixture();
        var defaults = CreateDefault();
        defaults["ObjectItems"] = new JsonArray
        {
            new JsonObject
            {
                ["Id"] = "A",
                ["Value"] = "default-a",
                ["Added"] = "from-a",
            },
            new JsonObject
            {
                ["Id"] = "B",
                ["Value"] = "default-b",
                ["Added"] = "from-b",
            },
        };
        fixture.WriteDefault(defaults);
        var user = CreateDefault();
        user["ObjectItems"] = new JsonArray
        {
            new JsonObject
            {
                ["Id"] = "B",
                ["Value"] = "user-b",
                ["Removed"] = true,
            },
            new JsonObject { ["Id"] = "A", ["Value"] = "user-a" },
        };
        fixture.WriteUser(user);

        fixture.Synchronize();

        var items = fixture.ReadUser()["ObjectItems"]!.AsArray();
        Assert.Equal("B", items[0]!["Id"]!.GetValue<string>());
        Assert.Equal("user-b", items[0]!["Value"]!.GetValue<string>());
        Assert.Equal("from-b", items[0]!["Added"]!.GetValue<string>());
        Assert.Null(items[0]!["Removed"]);
        Assert.Equal("A", items[1]!["Id"]!.GetValue<string>());
        Assert.Equal("from-a", items[1]!["Added"]!.GetValue<string>());
    }

    [Fact]
    public void NullObjectArrayItemIsResetToCurrentDefaultStructure()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        var user = CreateDefault();
        user["ObjectItems"] = new JsonArray { null };
        fixture.WriteUser(user);

        var result = fixture.Synchronize();

        Assert.Equal("default", fixture.ReadUser()["ObjectItems"]![0]!["Id"]!.GetValue<string>());
        Assert.Contains("ObjectItems[]", result.ResetPaths);
    }

    [Fact]
    public void IncompatibleValueUsesDefaultAndPreservesOriginalInBackup()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        var user = CreateDefault();
        user["Nested"]!["Added"] = "not-a-number";
        fixture.WriteUser(user);

        var result = fixture.Synchronize();

        Assert.Equal(5, fixture.ReadUser()["Nested"]!["Added"]!.GetValue<int>());
        Assert.Contains("Nested:Added", result.ResetPaths);
        var backup = Assert.Single(fixture.Backups());
        Assert.Equal(
            "not-a-number",
            JsonNode.Parse(File.ReadAllText(backup.FullName))!["Nested"]![
                "Added"
            ]!.GetValue<string>()
        );
    }

    [Fact]
    public void FutureUserSchemaIsRejectedWithoutChangingFile()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        var future = CreateDefault();
        future["_meta"]!["schemaVersion"] = 2;
        fixture.WriteUser(future);
        var before = File.ReadAllBytes(fixture.UserPath);

        var exception = Assert.Throws<ApplicationConfigurationException>(() =>
            fixture.Synchronize()
        );

        Assert.Contains("高于当前程序支持", exception.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.UserPath));
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public void LegacyConfigurationWithoutMetadataMigratesToSchemaOne()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        var legacy = CreateDefault();
        legacy.Remove("_meta");
        legacy["Application"]!["SingleInstance"] = false;
        fixture.WriteUser(legacy);

        fixture.Synchronize();

        Assert.Equal(1, fixture.ReadUser()["_meta"]!["schemaVersion"]!.GetValue<int>());
        Assert.False(fixture.ReadUser()["Application"]!["SingleInstance"]!.GetValue<bool>());
        Assert.Single(fixture.Backups());
    }

    [Fact]
    public void ExplicitMigrationPreservesRenamedValue()
    {
        using var fixture = new ConfigurationFixture();
        var currentDefault = CreateDefault(schemaVersion: 2);
        currentDefault["Nested"]!["NewName"] = "default";
        currentDefault["Nested"]!.AsObject().Remove("Name");
        fixture.WriteDefault(currentDefault);
        var oldUser = CreateDefault();
        oldUser["Nested"]!["Name"] = "preserved";
        fixture.WriteUser(oldUser);
        var synchronizer = new ApplicationConfigurationSynchronizer(
            [new RenameNestedNameMigration()],
            currentSchemaVersion: 2
        );

        fixture.Synchronize(synchronizer);

        var nested = fixture.ReadUser()["Nested"]!.AsObject();
        Assert.Equal("preserved", nested["NewName"]!.GetValue<string>());
        Assert.Null(nested["Name"]);
        Assert.Equal(2, fixture.ReadUser()["_meta"]!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void FailedMigrationKeepsOriginalAndCreatesBackupFirst()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault(schemaVersion: 2));
        fixture.WriteUser(CreateDefault());
        var before = File.ReadAllBytes(fixture.UserPath);
        var synchronizer = new ApplicationConfigurationSynchronizer(
            [new ThrowingMigration()],
            currentSchemaVersion: 2
        );

        Assert.Throws<ApplicationConfigurationException>(() => fixture.Synchronize(synchronizer));

        Assert.Equal(before, File.ReadAllBytes(fixture.UserPath));
        Assert.Equal(before, File.ReadAllBytes(Assert.Single(fixture.Backups()).FullName));
    }

    [Fact]
    public void EffectiveConfigurationUsesUserValuesButForcesDefaultUpdateSource()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        var user = CreateDefault();
        user["Application"]!["SingleInstance"] = false;
        user["ApplicationUpdate"]!["AutoCheckOnStartup"] = false;
        user["ApplicationUpdate"]!["SourceBaseAddress"] = "https://user.invalid";
        fixture.WriteUser(user);

        var configuration = Bootstrapper.BuildConfiguration(
            fixture.Paths,
            fixture.CreateSynchronizer()
        );

        Assert.False(configuration.GetValue<bool>("Application:SingleInstance"));
        Assert.False(configuration.GetValue<bool>("ApplicationUpdate:AutoCheckOnStartup"));
        Assert.Equal(
            "https://updates.example/v1",
            configuration["ApplicationUpdate:SourceBaseAddress"]
        );
        Assert.Equal(
            "https://updates.example/v1",
            fixture.ReadUser()["ApplicationUpdate"]!["SourceBaseAddress"]!.GetValue<string>()
        );
    }

    [Fact]
    public void UnchangedConfigurationIsNotRewrittenOrBackedUp()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        fixture.WriteUser(CreateDefault());
        var before = File.ReadAllBytes(fixture.UserPath);

        fixture.Synchronize();

        Assert.Equal(before, File.ReadAllBytes(fixture.UserPath));
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public void SuccessfulSynchronizationRetainsOnlyFiveNewestBackups()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        var user = CreateDefault();
        user["Obsolete"] = true;
        fixture.WriteUser(user);
        Directory.CreateDirectory(fixture.BackupDirectory);
        for (var index = 0; index < 7; index++)
        {
            File.WriteAllText(
                Path.Combine(
                    fixture.BackupDirectory,
                    $"appsettings.2020010{index}T0000000Z.old.json"
                ),
                "{}"
            );
        }

        fixture.Synchronize();

        Assert.Equal(
            ApplicationConfigurationSynchronizer.RetainedBackupCount,
            fixture.Backups().Length
        );
    }

    [Fact]
    public async Task ConcurrentSynchronizationProducesOneValidCompleteConfiguration()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        var user = CreateDefault();
        user["Obsolete"] = true;
        fixture.WriteUser(user);

        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => fixture.Synchronize()))
        );

        Assert.True(JsonNode.DeepEquals(CreateDefault(), fixture.ReadUser()));
        Assert.InRange(
            fixture.Backups().Length,
            1,
            ApplicationConfigurationSynchronizer.RetainedBackupCount
        );
    }

    [Fact]
    public void LegacyInstallConfigurationSeedsFirstAppDataConfiguration()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        var legacy = CreateDefault();
        legacy.Remove("_meta");
        legacy["Application"]!["SingleInstance"] = false;
        File.WriteAllText(fixture.LegacyPath, legacy.ToJsonString());

        fixture.Synchronize();

        Assert.False(fixture.ReadUser()["Application"]!["SingleInstance"]!.GetValue<bool>());
        Assert.Equal(1, fixture.ReadUser()["_meta"]!["schemaVersion"]!.GetValue<int>());
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public void CorruptUserConfigurationIsRejectedWithoutReplacement()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteDefault(CreateDefault());
        File.WriteAllText(fixture.UserPath, "not-json");
        var before = File.ReadAllBytes(fixture.UserPath);

        Assert.Throws<ApplicationConfigurationException>(() => fixture.Synchronize());

        Assert.Equal(before, File.ReadAllBytes(fixture.UserPath));
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public void MissingDefaultConfigurationIsRejectedWithoutChangingUserFile()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteUser(CreateDefault());
        var before = File.ReadAllBytes(fixture.UserPath);

        var exception = Assert.Throws<ApplicationConfigurationException>(() =>
            fixture.Synchronize()
        );

        Assert.Equal(fixture.DefaultPath, exception.ConfigurationPath);
        Assert.Equal(before, File.ReadAllBytes(fixture.UserPath));
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public async Task RuntimeWritersChangeOnlyAppDataConfiguration()
    {
        using var fixture = new ConfigurationFixture();
        var defaults = CreateDefault();
        defaults[DeviceAutoConnectionOptions.SectionName] = new JsonObject
        {
            [nameof(DeviceAutoConnectionOptions.LastDevice)] = null,
        };
        defaults[EegPhysicalChannelMappingService.SectionName] = new JsonObject
        {
            ["PhysicalChannelCount"] = 32,
            ["ElectrodeMappings"] = new JsonArray
            {
                new JsonObject { ["ElectrodeId"] = "A", ["PhysicalChannel"] = 1 },
                new JsonObject { ["ElectrodeId"] = "B", ["PhysicalChannel"] = null },
            },
        };
        defaults[DeviceBackendOptions.SectionName] = CreateRealBackend();
        fixture.WriteDefault(defaults);
        fixture.Synchronize();
        var defaultBytes = File.ReadAllBytes(fixture.DefaultPath);

        var profile = new SavedDeviceConnectionProfile(
            "device-1",
            "model",
            "serial",
            "mac",
            "udp",
            "192.168.1.2",
            30307,
            "1.0.1",
            "192.168.1.3",
            30302
        );
        await new DeviceConnectionProfileStore(fixture.UserPath).SaveAsync(profile);
        var catalog = new ElectrodePositionCatalog(
            new ElectrodePositionOptions
            {
                Positions =
                [
                    new ElectrodePositionDefinition("A", "A", 1, 1),
                    new ElectrodePositionDefinition("B", "B", 2, 2),
                ],
            }
        );
        new EegPhysicalChannelMappingService(fixture.UserPath, catalog).Save(
            32,
            [new EegPhysicalChannelMapping("A", 2), new EegPhysicalChannelMapping("B", 1)]
        );
        await new DeviceBackendModeToggleService(fixture.UserPath).ToggleAsync();

        Assert.Equal(defaultBytes, File.ReadAllBytes(fixture.DefaultPath));
        var user = fixture.ReadUser();
        Assert.Equal(
            "device-1",
            user[DeviceAutoConnectionOptions.SectionName]![
                nameof(DeviceAutoConnectionOptions.LastDevice)
            ]!["DeviceId"]!.GetValue<string>()
        );
        Assert.Equal(
            2,
            user[EegPhysicalChannelMappingService.SectionName]!["ElectrodeMappings"]![0]![
                "PhysicalChannel"
            ]!.GetValue<int>()
        );
        Assert.Equal(
            "Simulated",
            user[DeviceBackendOptions.SectionName]![
                nameof(DeviceBackendOptions.ConnectionSource)
            ]!.GetValue<string>()
        );
    }

    [Fact]
    public void CurrentDefaultConfigurationSynchronizesAndPassesAllStartupValidation()
    {
        using var fixture = new ConfigurationFixture();
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "appsettings.default.json"),
            fixture.DefaultPath,
            overwrite: true
        );
        var configuration = Bootstrapper.BuildConfiguration(
            fixture.Paths,
            fixture.CreateSynchronizer()
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        services.AddSingleton<IEegPhysicalChannelMappingService>(
            provider => new EegPhysicalChannelMappingService(
                fixture.UserPath,
                provider.GetRequiredService<IElectrodePositionCatalog>()
            )
        );
        using var provider = services.BuildServiceProvider();

        Bootstrapper.ValidateConfiguration(provider);
    }

    [Fact]
    public void StartupSnapshotRestoresOriginalWhenFinalValidationFails()
    {
        using var fixture = new ConfigurationFixture();
        var defaults = CreateDefault();
        defaults["ApplicationUpdate"]!["SourceBaseAddress"] = "ftp://invalid.example";
        fixture.WriteDefault(defaults);
        var original = CreateDefault();
        original["Obsolete"] = true;
        fixture.WriteUser(original);
        var before = File.ReadAllBytes(fixture.UserPath);
        var snapshot = UserConfigurationFileSnapshot.Capture(fixture.UserPath);
        var configuration = Bootstrapper.BuildConfiguration(
            fixture.Paths,
            fixture.CreateSynchronizer()
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.ThrowsAny<Exception>(() => Bootstrapper.ValidateConfiguration(provider));
        snapshot.RestoreIfChanged();

        Assert.Equal(before, File.ReadAllBytes(fixture.UserPath));
    }

    [Fact]
    public void StartupSnapshotRemovesNewFileWhenFirstRunValidationFails()
    {
        using var fixture = new ConfigurationFixture();
        var defaults = CreateDefault();
        defaults["ApplicationUpdate"]!["SourceBaseAddress"] = "ftp://invalid.example";
        fixture.WriteDefault(defaults);
        var snapshot = UserConfigurationFileSnapshot.Capture(fixture.UserPath);

        fixture.Synchronize();
        snapshot.RestoreIfChanged();

        Assert.False(File.Exists(fixture.UserPath));
    }

    [Fact]
    public void SchemaOneLoggingStructureSynchronizesWithoutLosingEnabledOrSizeAndOnlyBacksUpOnce()
    {
        using var fixture = new ConfigurationFixture();
        var defaults = CreateDefault();
        defaults["SerialLog"] = new JsonObject
        {
            ["Enabled"] = true,
            ["MaxFileSizeBytes"] = 10485760,
            ["RetentionDays"] = 45,
            ["MinimumLevel"] = "Warning",
        };
        fixture.WriteDefault(defaults);
        var old = CreateDefault();
        old["SerialLog"] = new JsonObject
        {
            ["Enabled"] = false,
            ["MaxFileSizeBytes"] = 123456,
            ["RetainedFileCount"] = 7,
        };
        fixture.WriteUser(old);
        fixture.Synchronize();
        fixture.Synchronize();
        var logging = fixture.ReadUser()["SerialLog"]!;
        Assert.False(logging["Enabled"]!.GetValue<bool>());
        Assert.Equal(123456, logging["MaxFileSizeBytes"]!.GetValue<int>());
        Assert.Equal(45, logging["RetentionDays"]!.GetValue<int>());
        Assert.Equal("Warning", logging["MinimumLevel"]!.GetValue<string>());
        Assert.Null(logging["RetainedFileCount"]);
        Assert.Equal(1, fixture.ReadUser()["_meta"]!["schemaVersion"]!.GetValue<int>());
        Assert.Single(fixture.Backups());
    }

    private static JsonObject CreateDefault(int schemaVersion = 1) =>
        JsonNode
            .Parse(
                $$"""
                {
                  "_meta": { "schemaVersion": {{schemaVersion}} },
                  "Application": { "SingleInstance": true, "Added": true },
                  "ApplicationUpdate": {
                    "AutoCheckOnStartup": true,
                    "SourceBaseAddress": "https://updates.example/v1"
                  },
                  "Nested": { "Name": "default", "Added": 5 },
                  "ObjectItems": [
                    { "Id": "default", "Enabled": true, "NewProperty": "new" }
                  ],
                  "PrimitiveItems": [1, 2, 3]
                }
                """
            )!
            .AsObject();

    private static JsonObject CreateRealBackend() =>
        JsonNode
            .Parse(
                """
                {
                  "ConnectionSource": "Real",
                  "Capabilities": {
                    "Status": "Real",
                    "EegAcquisition": "Real",
                    "Stimulation": "Real",
                    "EegImpedance": "Real",
                    "StimulationImpedance": "Real",
                    "Tolerance": "Simulated"
                  }
                }
                """
            )!
            .AsObject();

    private sealed class RenameNestedNameMigration : IUserConfigurationMigration
    {
        public int FromVersion => 1;

        public int ToVersion => 2;

        public void Apply(JsonObject configuration)
        {
            var nested = configuration["Nested"]!.AsObject();
            nested["NewName"] = nested["Name"]?.DeepClone();
            nested.Remove("Name");
        }
    }

    private sealed class ThrowingMigration : IUserConfigurationMigration
    {
        public int FromVersion => 1;

        public int ToVersion => 2;

        public void Apply(JsonObject configuration) =>
            throw new InvalidOperationException("migration failed");
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"eggtcs-configuration-{Guid.NewGuid():N}"
        );

        public ConfigurationFixture()
        {
            Directory.CreateDirectory(_directory);
            DefaultPath = Path.Combine(_directory, "appsettings.default.json");
            UserPath = Path.Combine(_directory, "user", "appsettings.json");
            BackupDirectory = Path.Combine(_directory, "user", "config-backups");
            LegacyPath = Path.Combine(_directory, "appsettings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
            Paths = new ApplicationConfigurationPaths(
                DefaultPath,
                UserPath,
                BackupDirectory,
                LegacyPath
            );
        }

        public string DefaultPath { get; }

        public string UserPath { get; }

        public string BackupDirectory { get; }

        public string LegacyPath { get; }

        public ApplicationConfigurationPaths Paths { get; }

        public void WriteDefault(JsonObject root) =>
            File.WriteAllText(DefaultPath, root.ToJsonString());

        public void WriteUser(JsonObject root) => File.WriteAllText(UserPath, root.ToJsonString());

        public JsonObject ReadUser() => JsonNode.Parse(File.ReadAllText(UserPath))!.AsObject();

        public FileInfo[] Backups() =>
            Directory.Exists(BackupDirectory)
                ? new DirectoryInfo(BackupDirectory).GetFiles("appsettings.*.json")
                : [];

        public ApplicationConfigurationSynchronizer CreateSynchronizer() => new();

        public ApplicationConfigurationSynchronizationResult Synchronize(
            ApplicationConfigurationSynchronizer? synchronizer = null
        ) => (synchronizer ?? CreateSynchronizer()).Synchronize(Paths);

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
