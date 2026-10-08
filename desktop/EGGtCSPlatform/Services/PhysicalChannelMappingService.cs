using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed record EegPhysicalChannelMapping(string ElectrodeId, int? PhysicalChannel);

public sealed record EegPhysicalChannelMappingSnapshot(
    int PhysicalChannelCount,
    IReadOnlyList<EegPhysicalChannelMapping> Mappings,
    IReadOnlyList<string> ValidationErrors
)
{
    public bool IsValid => ValidationErrors.Count == 0;
}

public interface IEegPhysicalChannelMappingService
{
    EegPhysicalChannelMappingSnapshot Load();

    void Save(int physicalChannelCount, IReadOnlyList<EegPhysicalChannelMapping> mappings);

    string StoragePath { get; }
}

public sealed class EegPhysicalChannelMappingService : IEegPhysicalChannelMappingService
{
    public const string SectionName = "EegAcquisitionChannels";
    public const int SupportedPhysicalChannelCount = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IReadOnlySet<string> _knownElectrodeIds;

    public EegPhysicalChannelMappingService()
        : this(ElectrodePositionCatalog.Default) { }

    public EegPhysicalChannelMappingService(IElectrodePositionCatalog electrodeCatalog)
        : this(AppPaths.UserConfigurationPath, electrodeCatalog) { }

    public EegPhysicalChannelMappingService(string storagePath)
        : this(storagePath, ElectrodePositionCatalog.Default) { }

    public EegPhysicalChannelMappingService(
        string storagePath,
        IElectrodePositionCatalog electrodeCatalog
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        ArgumentNullException.ThrowIfNull(electrodeCatalog);
        StoragePath = Path.GetFullPath(storagePath);
        _knownElectrodeIds = new HashSet<string>(
            electrodeCatalog.ElectrodeIds,
            StringComparer.OrdinalIgnoreCase
        );
    }

    public string StoragePath { get; }

    public EegPhysicalChannelMappingSnapshot Load()
    {
        if (!File.Exists(StoragePath))
            return EmptySnapshot();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(StoragePath)) as JsonObject;
            if (root is null)
                return InvalidSnapshot("用户配置文件根节点必须是 JSON 对象。");
            if (root[SectionName] is null)
                return EmptySnapshot();

            EegAcquisitionChannelsDocument? document;
            try
            {
                document = root[SectionName]!.Deserialize<EegAcquisitionChannelsDocument>(
                    JsonOptions
                );
            }
            catch (JsonException exception)
            {
                return InvalidSnapshot($"{SectionName} 结构无法解析：{exception.Message}");
            }
            if (document is null)
                return InvalidSnapshot($"{SectionName} 配置不能为空。");

            var mappings = document.ElectrodeMappings ?? [];
            var errors = Validate(
                document.PhysicalChannelCount,
                mappings,
                requireCompleteCatalog: false
            );
            return new EegPhysicalChannelMappingSnapshot(
                SupportedPhysicalChannelCount,
                mappings.ToArray(),
                errors
            );
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return InvalidSnapshot($"用户配置文件读取失败：{exception.Message}");
        }
    }

    public void Save(int physicalChannelCount, IReadOnlyList<EegPhysicalChannelMapping> mappings)
    {
        using var operation = new LoggedOperation(
            nameof(EegPhysicalChannelMappingService),
            "Configuration.Save",
            "fields=EegAcquisitionChannels"
        );
        ArgumentNullException.ThrowIfNull(mappings);
        var errors = Validate(physicalChannelCount, mappings, requireCompleteCatalog: true);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("；", errors));

        AppSettingsFileGate.Semaphore.Wait();
        try
        {
            AppSettingsFileGate.ThrowIfWritesBlocked();
            var directory =
                Path.GetDirectoryName(StoragePath)
                ?? throw new InvalidOperationException("无法确定用户配置文件所在目录。");
            Directory.CreateDirectory(directory);
            var root = File.Exists(StoragePath)
                ? JsonNode.Parse(File.ReadAllText(StoragePath)) as JsonObject
                    ?? throw new InvalidDataException("用户配置文件根节点必须是 JSON 对象。")
                : new JsonObject();
            root[SectionName] = JsonSerializer.SerializeToNode(
                new EegAcquisitionChannelsDocument(
                    SupportedPhysicalChannelCount,
                    mappings
                        .Select(mapping => new EegPhysicalChannelMapping(
                            mapping.ElectrodeId,
                            mapping.PhysicalChannel
                        ))
                        .ToArray()
                ),
                JsonOptions
            );

            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(StoragePath)}.{Guid.NewGuid():N}.tmp"
            );
            try
            {
                File.WriteAllText(temporaryPath, root.ToJsonString(JsonOptions));
                File.Move(temporaryPath, StoragePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            AppSettingsFileGate.Semaphore.Release();
        }
        ApplicationConfigurationRuntime.SynchronizeAfterProductionWrite(StoragePath);
        operation.Complete("fields=EegAcquisitionChannels");
    }

    private IReadOnlyList<string> Validate(
        int physicalChannelCount,
        IReadOnlyList<EegPhysicalChannelMapping> mappings,
        bool requireCompleteCatalog
    )
    {
        var errors = new List<string>();
        if (physicalChannelCount != SupportedPhysicalChannelCount)
            errors.Add(
                $"PhysicalChannelCount 必须为 {SupportedPhysicalChannelCount}（当前 {physicalChannelCount}）。"
            );

        var duplicateElectrodes = mappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ElectrodeId))
            .GroupBy(mapping => mapping.ElectrodeId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicateElectrodes.Length > 0)
            errors.Add($"电极位置重复：{string.Join("、", duplicateElectrodes)}。");

        var unknownElectrodes = mappings
            .Where(mapping =>
                string.IsNullOrWhiteSpace(mapping.ElectrodeId)
                || !_knownElectrodeIds.Contains(mapping.ElectrodeId)
            )
            .Select(mapping =>
                string.IsNullOrWhiteSpace(mapping.ElectrodeId) ? "<空>" : mapping.ElectrodeId
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownElectrodes.Length > 0)
            errors.Add($"存在未知电极位置：{string.Join("、", unknownElectrodes)}。");

        var outOfRange = mappings
            .Where(mapping => mapping.PhysicalChannel is < 1 or > SupportedPhysicalChannelCount)
            .Select(mapping => $"{mapping.ElectrodeId}={mapping.PhysicalChannel}")
            .ToArray();
        if (outOfRange.Length > 0)
            errors.Add(
                $"物理通道必须在 1～{SupportedPhysicalChannelCount}：{string.Join("、", outOfRange)}。"
            );

        var duplicateChannels = mappings
            .Where(mapping => mapping.PhysicalChannel is >= 1 and <= SupportedPhysicalChannelCount)
            .GroupBy(mapping => mapping.PhysicalChannel!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(channel => channel)
            .ToArray();
        if (duplicateChannels.Length > 0)
            errors.Add($"物理通道重复分配：{string.Join("、", duplicateChannels)}。");

        if (requireCompleteCatalog)
        {
            var configuredIds = new HashSet<string>(
                mappings.Select(mapping => mapping.ElectrodeId),
                StringComparer.OrdinalIgnoreCase
            );
            var missing = _knownElectrodeIds
                .Where(id => !configuredIds.Contains(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length > 0)
                errors.Add($"必须保存全部电极位置，缺少：{string.Join("、", missing)}。");
        }
        return errors;
    }

    private static EegPhysicalChannelMappingSnapshot EmptySnapshot() =>
        new(SupportedPhysicalChannelCount, [], []);

    private static EegPhysicalChannelMappingSnapshot InvalidSnapshot(string error) =>
        new(SupportedPhysicalChannelCount, [], [error]);

    private sealed record EegAcquisitionChannelsDocument(
        int PhysicalChannelCount,
        IReadOnlyList<EegPhysicalChannelMapping>? ElectrodeMappings
    );
}
