using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.Persistence;

namespace EGGtCSPlatform.Services;

public interface IDeviceConnectionProfileStore
{
    SavedDeviceConnectionProfile? Load();
    Task SaveAsync(
        SavedDeviceConnectionProfile profile,
        CancellationToken cancellationToken = default
    );
}

public sealed class DeviceConnectionProfileStore : IDeviceConnectionProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DeviceConnectionProfileStore(string? storagePath = null) =>
        StoragePath = Path.GetFullPath(storagePath ?? AppPaths.UserConfigurationPath);

    public string StoragePath { get; }

    public SavedDeviceConnectionProfile? Load()
    {
        try
        {
            if (!File.Exists(StoragePath))
                return null;
            var root = JsonNode.Parse(File.ReadAllText(StoragePath)) as JsonObject;
            return root?[DeviceAutoConnectionOptions.SectionName]?[
                nameof(DeviceAutoConnectionOptions.LastDevice)
            ]?.Deserialize<SavedDeviceConnectionProfile>(JsonOptions);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        SavedDeviceConnectionProfile profile,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var operation = new LoggedOperation(
            nameof(DeviceConnectionProfileStore),
            "Configuration.Save",
            "fields=DeviceAutoConnection.LastDevice",
            cancellationToken: cancellationToken
        );
        await AppSettingsFileGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettingsFileGate.ThrowIfWritesBlocked();
            var directory =
                Path.GetDirectoryName(StoragePath)
                ?? throw new InvalidOperationException("无法确定用户配置文件所在目录。");
            Directory.CreateDirectory(directory);
            var root = File.Exists(StoragePath)
                ? JsonNode.Parse(
                    await File.ReadAllTextAsync(StoragePath, cancellationToken)
                        .ConfigureAwait(false)
                ) as JsonObject
                    ?? throw new InvalidDataException("用户配置文件根节点必须是 JSON 对象。")
                : new JsonObject();
            var section =
                root[DeviceAutoConnectionOptions.SectionName] as JsonObject ?? new JsonObject();
            section[nameof(DeviceAutoConnectionOptions.LastDevice)] =
                JsonSerializer.SerializeToNode(profile, JsonOptions);
            root[DeviceAutoConnectionOptions.SectionName] = section;
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(StoragePath)}.{Guid.NewGuid():N}.tmp"
            );
            try
            {
                await File.WriteAllTextAsync(
                        temporaryPath,
                        root.ToJsonString(JsonOptions),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
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
        operation.Complete("fields=DeviceAutoConnection.LastDevice");
    }
}

internal static class AppSettingsFileGate
{
    private static int _writesBlocked;

    internal static SemaphoreSlim Semaphore { get; } = new(1, 1);

    internal static void BlockWritesUntilRestart() => Interlocked.Exchange(ref _writesBlocked, 1);

    internal static void ThrowIfWritesBlocked()
    {
        if (Volatile.Read(ref _writesBlocked) != 0)
            throw new InvalidOperationException("默认配置已经恢复，程序重启前不能再修改配置。");
    }
}
