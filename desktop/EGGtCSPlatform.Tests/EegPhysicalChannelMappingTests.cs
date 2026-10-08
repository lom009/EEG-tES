using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EegPhysicalChannelMappingTests
{
    [Fact]
    public void ConfiguredCatalogControlsPageCountOrderLabelsAndCoordinates()
    {
        using var fixture = new TemporaryAppSettings();
        fixture.WriteRoot(new JsonObject());
        var catalog = new ElectrodePositionCatalog(
            new ElectrodePositionOptions
            {
                Positions =
                [
                    new ElectrodePositionDefinition("A", "位置 A", 286, 287),
                    new ElectrodePositionDefinition("B", "位置 B", 300, 300),
                ],
            }
        );
        var service = new EegPhysicalChannelMappingService(fixture.Path, catalog);

        var page = new PhysicalChannelMappingPageViewModel(service, catalog);

        Assert.Equal(["A", "B"], page.Mappings.Select(item => item.ElectrodeId));
        Assert.Equal(["位置 A", "位置 B"], page.Mappings.Select(item => item.PositionName));
        Assert.Equal(2, page.Points.Count);
        Assert.Equal("位置 A", page.Points[0].PositionName);
        Assert.Equal(404d, page.Points[0].X, 6);
        Assert.Equal(428d, page.Points[0].Y, 6);
    }

    [Fact]
    public void DefaultAppSettingsMapsExistingElectrodesFromConnectorWiringDiagram()
    {
        var repositoryRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        var configurationPath = System.IO.Path.Combine(
            repositoryRoot,
            "EGGtCSPlatform",
            "appsettings.default.json"
        );
        var snapshot = new EegPhysicalChannelMappingService(configurationPath).Load();
        Assert.True(snapshot.IsValid, string.Join("；", snapshot.ValidationErrors));
        Assert.Equal(ElectrodePositionCatalog.All.Count, snapshot.Mappings.Count);
        Assert.DoesNotContain(snapshot.Mappings, mapping => mapping.ElectrodeId == "FC1");
        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["FP1"] = 1,
            ["FP2"] = 2,
            ["F3"] = 3,
            ["F4"] = 4,
            ["F7"] = 5,
            ["F8"] = 6,
            ["Fz"] = 7,
            ["O2"] = 8,
            ["FC3"] = 9,
            ["C5"] = 10,
            ["FC5"] = 11,
            ["FC6"] = 12,
            ["FT7"] = 13,
            ["FT8"] = 14,
            ["C3"] = 15,
            ["C4"] = 16,
            ["C6"] = 17,
            ["Cz"] = 18,
            ["CP3"] = 19,
            ["CP4"] = 20,
            ["CP5"] = 21,
            ["CP6"] = 22,
            ["T7"] = 23,
            ["T8"] = 24,
            ["TP7"] = 25,
            ["TP8"] = 26,
            ["P3"] = 27,
            ["P4"] = 28,
            ["P7"] = 29,
            ["P8"] = 30,
            ["Pz"] = 31,
            ["O1"] = 32,
        };

        Assert.Equal(
            expected.Count,
            snapshot.Mappings.Count(mapping => mapping.PhysicalChannel.HasValue)
        );
        foreach (var (electrodeId, physicalChannel) in expected)
        {
            Assert.Equal(
                physicalChannel,
                snapshot
                    .Mappings.Single(mapping => mapping.ElectrodeId == electrodeId)
                    .PhysicalChannel
            );
        }
        Assert.All(
            snapshot.Mappings.Where(mapping => !expected.ContainsKey(mapping.ElectrodeId)),
            mapping => Assert.Null(mapping.PhysicalChannel)
        );
    }

    [Fact]
    public void SavePreservesOtherSettingsAndPageReloadsAllMappings()
    {
        using var fixture = new TemporaryAppSettings();
        fixture.WriteRoot(
            new JsonObject
            {
                ["DeviceHeartbeat"] = new JsonObject { ["Enabled"] = true },
                ["StimulationChannels"] = new JsonObject { ["PhysicalChannelCount"] = 8 },
            }
        );
        var service = new EegPhysicalChannelMappingService(fixture.Path);
        var mappings = CreateCompleteMappings()
            .Select(mapping =>
                mapping.ElectrodeId switch
                {
                    "FP1" => mapping with { PhysicalChannel = 1 },
                    "O2" => mapping with { PhysicalChannel = 32 },
                    _ => mapping,
                }
            )
            .ToArray();

        service.Save(32, mappings);

        var root = JsonNode.Parse(File.ReadAllText(fixture.Path))!.AsObject();
        Assert.True(root["DeviceHeartbeat"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(8, root["StimulationChannels"]!["PhysicalChannelCount"]!.GetValue<int>());
        var snapshot = service.Load();
        Assert.True(snapshot.IsValid);
        Assert.Equal(ElectrodePositionCatalog.All.Count, snapshot.Mappings.Count);
        Assert.Equal(
            1,
            snapshot.Mappings.Single(mapping => mapping.ElectrodeId == "FP1").PhysicalChannel
        );
        Assert.Equal(
            32,
            snapshot.Mappings.Single(mapping => mapping.ElectrodeId == "O2").PhysicalChannel
        );

        var page = new PhysicalChannelMappingPageViewModel(service);
        Assert.Equal(33, page.ChannelOptions.Count);
        Assert.Null(page.Mappings.Single(mapping => mapping.ElectrodeId == "F3").PhysicalChannel);
        Assert.Equal(
            1,
            page.Mappings.Single(mapping => mapping.ElectrodeId == "FP1").PhysicalChannel
        );
        Assert.Equal(
            32,
            page.Mappings.Single(mapping => mapping.ElectrodeId == "O2").PhysicalChannel
        );

        var fp2 = page.Mappings.Single(mapping => mapping.ElectrodeId == "FP2");
        fp2.SelectedOption = page.ChannelOptions.Single(option => option.Value == 1);
        Assert.True(page.HasDuplicatePhysicalChannels);
        Assert.False(page.CanSave);
        fp2.SelectedOption = page.ChannelOptions.Single(option => option.Value == 2);
        Assert.True(page.CanSave);
        page.SaveCommand.Execute(null);

        var reopened = new PhysicalChannelMappingPageViewModel(service);
        Assert.Equal(
            2,
            reopened.Mappings.Single(mapping => mapping.ElectrodeId == "FP2").PhysicalChannel
        );
        Assert.False(reopened.IsSaved);
        Assert.Equal(ElectrodePositionCatalog.All.Count, service.Load().Mappings.Count);
    }

    [Fact]
    public async Task DeviceServicesReloadSavedFileAndBlockItImmediatelyWhenItBecomesInvalid()
    {
        using var fixture = new TemporaryAppSettings();
        fixture.WriteRoot(new JsonObject());
        var mappings = CreateCompleteMappings()
            .Select(mapping =>
                mapping.ElectrodeId switch
                {
                    "F3" => mapping with { PhysicalChannel = 3 },
                    "C3" => mapping with { PhysicalChannel = 7 },
                    _ => mapping,
                }
            )
            .ToArray();
        var mappingService = new EegPhysicalChannelMappingService(fixture.Path);
        mappingService.Save(32, mappings);
        var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var impedance = new DeviceImpedanceDetectionService(manager, mappingService);
        using var experiment = new DeviceExperimentRunService(manager, mappingService);

        var readings = await impedance.StartEegAsync(DeviceId.Simulator.Value, ["F3", "C3"]);
        await impedance.StopEegAsync(DeviceId.Simulator.Value, ["F3", "C3"]);
        await experiment.StartAcquisitionAsync(
            new AcquisitionRunRequest(
                TimeSpan.FromMilliseconds(20),
                ["F3", "C3"],
                500,
                DeviceId.Simulator.Value
            )
        );
        Assert.Equal(2, readings.Count);

        fixture.WriteConfiguration(
            32,
            [new EegPhysicalChannelMapping("F3", 3), new EegPhysicalChannelMapping("C3", 3)]
        );

        var impedanceError = await Assert.ThrowsAsync<ImpedanceDetectionConfigurationException>(
            () =>
                impedance.StartEegAsync(DeviceId.Simulator.Value, ["F3", "C3"])
        );
        var experimentError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            experiment.StartAcquisitionAsync(
                new AcquisitionRunRequest(
                    TimeSpan.FromMilliseconds(20),
                    ["F3", "C3"],
                    500,
                    DeviceId.Simulator.Value
                )
            )
        );
        Assert.Contains(
            "EEG采集物理通道配置无效",
            impedanceError.Message,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "EEG采集物理通道配置无效",
            experimentError.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void InvalidManualConfigurationReportsEveryErrorAndCanBeNormalizedByPage()
    {
        using var fixture = new TemporaryAppSettings();
        fixture.WriteConfiguration(
            physicalChannelCount: 31,
            mappings:
            [
                new EegPhysicalChannelMapping("FP1", 1),
                new EegPhysicalChannelMapping("FP1", 2),
                new EegPhysicalChannelMapping("FP2", 1),
                new EegPhysicalChannelMapping("F3", 0),
                new EegPhysicalChannelMapping("UNKNOWN", 3),
            ]
        );
        var service = new EegPhysicalChannelMappingService(fixture.Path);

        var invalid = service.Load();

        Assert.False(invalid.IsValid);
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Contains("PhysicalChannelCount", StringComparison.Ordinal)
        );
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Contains("电极位置重复", StringComparison.Ordinal)
        );
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Contains("未知电极", StringComparison.Ordinal)
        );
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Contains("物理通道必须", StringComparison.Ordinal)
        );
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Contains("物理通道重复", StringComparison.Ordinal)
        );

        var page = new PhysicalChannelMappingPageViewModel(service);
        Assert.Contains("PhysicalChannelCount", page.ValidationText, StringComparison.Ordinal);
        Assert.Null(page.Mappings.Single(mapping => mapping.ElectrodeId == "F3").PhysicalChannel);
        Assert.True(page.CanSave);
        page.SaveCommand.Execute(null);

        var normalized = service.Load();
        Assert.True(normalized.IsValid);
        Assert.Equal(32, normalized.PhysicalChannelCount);
        Assert.Equal(ElectrodePositionCatalog.All.Count, normalized.Mappings.Count);
        Assert.DoesNotContain(normalized.Mappings, mapping => mapping.ElectrodeId == "UNKNOWN");
    }

    [Fact]
    public void SaveFailureKeepsOriginalFileAndPageShowsReason()
    {
        using var fixture = new TemporaryAppSettings();
        const string malformed = "{ not-json";
        File.WriteAllText(fixture.Path, malformed);
        var service = new EegPhysicalChannelMappingService(fixture.Path);

        Assert.ThrowsAny<Exception>(() => service.Save(32, CreateCompleteMappings()));
        Assert.Equal(malformed, File.ReadAllText(fixture.Path));

        var page = new PhysicalChannelMappingPageViewModel(new SaveFailureMappingService());
        page.SaveCommand.Execute(null);
        Assert.False(page.IsSaved);
        Assert.Contains("只读", page.StatusText, StringComparison.Ordinal);
    }

    private static EegPhysicalChannelMapping[] CreateCompleteMappings() =>
        ElectrodePositionCatalog
            .All.Select(position => new EegPhysicalChannelMapping(position.Id, null))
            .ToArray();

    private sealed class SaveFailureMappingService : IEegPhysicalChannelMappingService
    {
        public EegPhysicalChannelMappingSnapshot Load() => new(32, [], []);

        public void Save(
            int physicalChannelCount,
            IReadOnlyList<EegPhysicalChannelMapping> mappings
        ) => throw new UnauthorizedAccessException("配置文件只读");

        public string StoragePath => "read-only-appsettings.json";
    }

    private sealed class TemporaryAppSettings : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"eggtcs-eeg-mapping-{Guid.NewGuid():N}"
        );

        public TemporaryAppSettings()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "appsettings.json");
        }

        public string Path { get; }

        public void WriteRoot(JsonObject root) =>
            File.WriteAllText(
                Path,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            );

        public void WriteConfiguration(
            int physicalChannelCount,
            IReadOnlyList<EegPhysicalChannelMapping> mappings
        )
        {
            var section = JsonSerializer.SerializeToNode(
                new { PhysicalChannelCount = physicalChannelCount, ElectrodeMappings = mappings }
            );
            WriteRoot(new JsonObject { [EegPhysicalChannelMappingService.SectionName] = section });
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
