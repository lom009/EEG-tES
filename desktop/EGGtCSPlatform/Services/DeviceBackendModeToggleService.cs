using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.Persistence;

namespace EGGtCSPlatform.Services;

public enum DeviceBackendModeToggleResult
{
    SwitchedToSimulated,
    RestoredOriginal,
}

public interface IDeviceBackendModeToggleService
{
    Task<DeviceBackendModeToggleResult> ToggleAsync(CancellationToken cancellationToken = default);
}

public sealed class DeviceBackendModeToggleService : IDeviceBackendModeToggleService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DeviceBackendModeToggleService()
        : this(AppPaths.UserConfigurationPath, AppPaths.DeviceBackendConfigurationBackupPath) { }

    public DeviceBackendModeToggleService(string configurationPath, string? backupPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ConfigurationPath = Path.GetFullPath(configurationPath);
        var directory =
            Path.GetDirectoryName(ConfigurationPath)
            ?? throw new InvalidOperationException("无法确定用户配置文件所在目录。");
        BackupPath = Path.GetFullPath(
            backupPath ?? Path.Combine(directory, "appsettings.DeviceBackend.backup.json")
        );
    }

    public string ConfigurationPath { get; }

    public string BackupPath { get; }

    public async Task<DeviceBackendModeToggleResult> ToggleAsync(
        CancellationToken cancellationToken = default
    )
    {
        DeviceBackendModeToggleResult result;
        using var operation = new LoggedOperation(
            nameof(DeviceBackendModeToggleService),
            "Configuration.Save",
            "fields=DeviceBackend",
            cancellationToken: cancellationToken
        );
        await AppSettingsFileGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettingsFileGate.ThrowIfWritesBlocked();
            result = File.Exists(BackupPath)
                ? await RestoreAsync(cancellationToken).ConfigureAwait(false)
                : await SwitchToSimulatedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AppSettingsFileGate.Semaphore.Release();
        }
        ApplicationConfigurationRuntime.SynchronizeAfterProductionWrite(ConfigurationPath);
        operation.Complete($"fields=DeviceBackend; result={result}; restartRequired=true");
        return result;
    }

    private async Task<DeviceBackendModeToggleResult> SwitchToSimulatedAsync(
        CancellationToken cancellationToken
    )
    {
        var originalBytes = await ReadRequiredBytesAsync(ConfigurationPath, cancellationToken)
            .ConfigureAwait(false);
        var currentRoot = ParseRoot(originalBytes, Path.GetFileName(ConfigurationPath));
        RequireDeviceBackend(currentRoot, Path.GetFileName(ConfigurationPath));

        currentRoot[DeviceBackendOptions.SectionName] = CreateSimulatedSection();

        var backupCreated = false;
        var configurationReplaced = false;
        try
        {
            await WriteBytesAtomicallyAsync(
                    BackupPath,
                    originalBytes,
                    overwrite: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
            backupCreated = true;
            await WriteRootAtomicallyAsync(ConfigurationPath, currentRoot, cancellationToken)
                .ConfigureAwait(false);
            configurationReplaced = true;
            return DeviceBackendModeToggleResult.SwitchedToSimulated;
        }
        finally
        {
            if (backupCreated && !configurationReplaced && File.Exists(BackupPath))
                File.Delete(BackupPath);
        }
    }

    private async Task<DeviceBackendModeToggleResult> RestoreAsync(
        CancellationToken cancellationToken
    )
    {
        var backupBytes = await ReadRequiredBytesAsync(BackupPath, cancellationToken)
            .ConfigureAwait(false);
        var backupRoot = ParseRoot(backupBytes, Path.GetFileName(BackupPath));
        var originalSection = RequireDeviceBackend(backupRoot, Path.GetFileName(BackupPath));

        var currentBytes = await ReadRequiredBytesAsync(ConfigurationPath, cancellationToken)
            .ConfigureAwait(false);
        var currentRoot = ParseRoot(currentBytes, Path.GetFileName(ConfigurationPath));
        RequireDeviceBackend(currentRoot, Path.GetFileName(ConfigurationPath));
        currentRoot[DeviceBackendOptions.SectionName] = originalSection.DeepClone();

        await WriteRootAtomicallyAsync(ConfigurationPath, currentRoot, cancellationToken)
            .ConfigureAwait(false);
        File.Delete(BackupPath);
        return DeviceBackendModeToggleResult.RestoredOriginal;
    }

    private static JsonObject CreateSimulatedSection() =>
        new()
        {
            [nameof(DeviceBackendOptions.ConnectionSource)] = "Simulated",
            [nameof(DeviceBackendOptions.Capabilities)] = new JsonObject
            {
                [nameof(DeviceCapabilitySourceOptions.Status)] = "Simulated",
                [nameof(DeviceCapabilitySourceOptions.EegAcquisition)] = "Simulated",
                [nameof(DeviceCapabilitySourceOptions.Stimulation)] = "Simulated",
                [nameof(DeviceCapabilitySourceOptions.EegImpedance)] = "Simulated",
                [nameof(DeviceCapabilitySourceOptions.StimulationImpedance)] = "Simulated",
                [nameof(DeviceCapabilitySourceOptions.Tolerance)] = "Simulated",
            },
        };

    private static JsonObject RequireDeviceBackend(JsonObject root, string sourceName) =>
        root[DeviceBackendOptions.SectionName] as JsonObject
        ?? throw new InvalidDataException($"{sourceName} 缺少有效的 DeviceBackend 配置。");

    private static JsonObject ParseRoot(ReadOnlySpan<byte> content, string sourceName)
    {
        try
        {
            return JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidDataException($"{sourceName} 根节点必须是 JSON 对象。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{sourceName} 不是有效的 JSON：{exception.Message}",
                exception
            );
        }
    }

    private static async Task<byte[]> ReadRequiredBytesAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到配置文件：{path}", path);
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteRootAtomicallyAsync(
        string path,
        JsonObject root,
        CancellationToken cancellationToken
    ) =>
        WriteBytesAtomicallyAsync(
            path,
            System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions)),
            overwrite: true,
            cancellationToken
        );

    private static async Task WriteBytesAtomicallyAsync(
        string path,
        byte[] content,
        bool overwrite,
        CancellationToken cancellationToken
    )
    {
        var directory =
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"无法确定文件目录：{path}");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"
        );
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
