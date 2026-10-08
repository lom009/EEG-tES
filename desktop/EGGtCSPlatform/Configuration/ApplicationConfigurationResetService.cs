using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.Configuration;

public interface IApplicationConfigurationResetService
{
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class ApplicationConfigurationResetService : IApplicationConfigurationResetService
{
    private readonly string _defaultPath;
    private readonly string _userPath;
    private readonly string _backupDirectory;
    private readonly string _deviceBackendBackupPath;
    private readonly TimeProvider _timeProvider;
    private readonly bool _blockRuntimeWritesAfterReset;

    public ApplicationConfigurationResetService()
        : this(
            AppPaths.DefaultConfigurationPath,
            AppPaths.UserConfigurationPath,
            AppPaths.ConfigurationBackupDirectory,
            AppPaths.DeviceBackendConfigurationBackupPath,
            TimeProvider.System
        ) { }

    internal ApplicationConfigurationResetService(
        string defaultPath,
        string userPath,
        string backupDirectory,
        string deviceBackendBackupPath,
        TimeProvider? timeProvider = null
    )
    {
        _defaultPath = Path.GetFullPath(defaultPath);
        _userPath = Path.GetFullPath(userPath);
        _backupDirectory = Path.GetFullPath(backupDirectory);
        _deviceBackendBackupPath = Path.GetFullPath(deviceBackendBackupPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _blockRuntimeWritesAfterReset = string.Equals(
            _userPath,
            Path.GetFullPath(AppPaths.UserConfigurationPath),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        using var operation = new LoggedOperation(
            nameof(ApplicationConfigurationResetService),
            "Configuration.Reset",
            "恢复默认配置",
            cancellationToken: cancellationToken
        );
        await AppSettingsFileGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var defaultBytes = await ReadAndValidateDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var originalUserBytes = File.Exists(_userPath)
                ? await File.ReadAllBytesAsync(_userPath, cancellationToken).ConfigureAwait(false)
                : null;
            var originalBackendBackupBytes = File.Exists(_deviceBackendBackupPath)
                ? await File.ReadAllBytesAsync(_deviceBackendBackupPath, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var configurationChanged =
                originalUserBytes is null
                || !originalUserBytes.AsSpan().SequenceEqual(defaultBytes);

            if (configurationChanged && originalUserBytes is not null)
                BackupUserConfiguration();

            try
            {
                if (configurationChanged)
                    await WriteBytesAtomicallyAsync(_userPath, defaultBytes, cancellationToken)
                        .ConfigureAwait(false);
                if (File.Exists(_deviceBackendBackupPath))
                    File.Delete(_deviceBackendBackupPath);
                if (_blockRuntimeWritesAfterReset)
                    AppSettingsFileGate.BlockWritesUntilRestart();
            }
            catch
            {
                await RestoreOriginalStateAsync(
                        originalUserBytes,
                        originalBackendBackupBytes,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                throw;
            }
        }
        catch (ApplicationConfigurationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ApplicationConfigurationException(
                _userPath,
                $"恢复默认配置失败：{exception.Message}",
                exception
            );
        }
        finally
        {
            AppSettingsFileGate.Semaphore.Release();
        }
        operation.Complete("已恢复默认配置，需重启");
    }

    private async Task<byte[]> ReadAndValidateDefaultAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_defaultPath))
        {
            throw new ApplicationConfigurationException(
                _defaultPath,
                $"找不到默认配置文件：{_defaultPath}"
            );
        }

        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(_defaultPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApplicationConfigurationException(
                _defaultPath,
                $"无法读取默认配置文件：{exception.Message}",
                exception
            );
        }

        JsonObject root;
        try
        {
            root =
                JsonNode.Parse(content) as JsonObject
                ?? throw new JsonException("根节点必须是 JSON 对象。");
        }
        catch (JsonException exception)
        {
            throw new ApplicationConfigurationException(
                _defaultPath,
                $"默认配置不是有效的 JSON：{exception.Message}",
                exception
            );
        }

        var schemaNode = root["_meta"]?["schemaVersion"];
        if (
            schemaNode is not JsonValue schemaValue
            || !schemaValue.TryGetValue<int>(out var schemaVersion)
            || schemaVersion != ApplicationConfigurationSynchronizer.CurrentSchemaVersion
        )
        {
            throw new ApplicationConfigurationException(
                _defaultPath,
                $"默认配置 schemaVersion 必须为 {ApplicationConfigurationSynchronizer.CurrentSchemaVersion}。"
            );
        }

        return content;
    }

    private void BackupUserConfiguration()
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            var timestamp = _timeProvider
                .GetUtcNow()
                .UtcDateTime.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(
                _backupDirectory,
                $"appsettings.{timestamp}.{Guid.NewGuid():N}.json"
            );
            File.Copy(_userPath, backupPath, overwrite: false);
            foreach (
                var backup in new DirectoryInfo(_backupDirectory)
                    .EnumerateFiles("appsettings.*.json")
                    .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                    .Skip(ApplicationConfigurationSynchronizer.RetainedBackupCount)
            )
            {
                backup.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApplicationConfigurationException(
                _userPath,
                $"创建用户配置备份失败：{exception.Message}",
                exception
            );
        }
    }

    private async Task RestoreOriginalStateAsync(
        byte[]? originalUserBytes,
        byte[]? originalBackendBackupBytes,
        CancellationToken cancellationToken
    )
    {
        if (originalUserBytes is null)
        {
            if (File.Exists(_userPath))
                File.Delete(_userPath);
        }
        else
        {
            await WriteBytesAtomicallyAsync(_userPath, originalUserBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        if (originalBackendBackupBytes is not null && !File.Exists(_deviceBackendBackupPath))
        {
            await WriteBytesAtomicallyAsync(
                    _deviceBackendBackupPath,
                    originalBackendBackupBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteBytesAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken
    )
    {
        var directory =
            Path.GetDirectoryName(path) ?? throw new IOException($"无法确定配置目录：{path}");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"
        );
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
