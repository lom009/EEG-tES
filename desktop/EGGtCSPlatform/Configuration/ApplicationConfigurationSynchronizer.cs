using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.Configuration;

public sealed class ApplicationConfigurationException : Exception
{
    public ApplicationConfigurationException(string configurationPath, string message)
        : base(message)
    {
        ConfigurationPath = configurationPath;
    }

    public ApplicationConfigurationException(
        string configurationPath,
        string message,
        Exception innerException
    )
        : base(message, innerException)
    {
        ConfigurationPath = configurationPath;
    }

    public string ConfigurationPath { get; }
}

internal sealed record ApplicationConfigurationPaths(
    string DefaultConfigurationPath,
    string UserConfigurationPath,
    string BackupDirectory,
    string? LegacyConfigurationPath = null
);

internal sealed record ApplicationConfigurationSynchronizationResult(
    string UserConfigurationPath,
    string SourceBaseAddress,
    IReadOnlyList<string> ResetPaths
);

internal sealed class UserConfigurationFileSnapshot
{
    private readonly byte[]? _content;

    private UserConfigurationFileSnapshot(string path, byte[]? content)
    {
        Path = path;
        _content = content;
    }

    public string Path { get; }

    public static UserConfigurationFileSnapshot Capture(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        try
        {
            return new UserConfigurationFileSnapshot(
                fullPath,
                File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null
            );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApplicationConfigurationException(
                fullPath,
                $"无法保存用户配置启动快照：{exception.Message}",
                exception
            );
        }
    }

    public void RestoreIfChanged()
    {
        AppSettingsFileGate.Semaphore.Wait();
        try
        {
            if (_content is null)
            {
                if (File.Exists(Path))
                    File.Delete(Path);
                return;
            }

            if (File.Exists(Path) && File.ReadAllBytes(Path).SequenceEqual(_content))
                return;
            WriteBytesAtomically(Path, _content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApplicationConfigurationException(
                Path,
                $"恢复启动前的用户配置失败：{exception.Message}",
                exception
            );
        }
        finally
        {
            AppSettingsFileGate.Semaphore.Release();
        }
    }

    private static void WriteBytesAtomically(string path, byte[] content)
    {
        var directory =
            System.IO.Path.GetDirectoryName(path)
            ?? throw new IOException("无法确定用户配置目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"
        );
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

internal static class ApplicationConfigurationRuntime
{
    public static void SynchronizeAfterProductionWrite(string configurationPath)
    {
        if (
            !string.Equals(
                System.IO.Path.GetFullPath(configurationPath),
                System.IO.Path.GetFullPath(Persistence.AppPaths.UserConfigurationPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
        )
        {
            return;
        }

        new ApplicationConfigurationSynchronizer().Synchronize(
            new ApplicationConfigurationPaths(
                Persistence.AppPaths.DefaultConfigurationPath,
                Persistence.AppPaths.UserConfigurationPath,
                Persistence.AppPaths.ConfigurationBackupDirectory,
                Persistence.AppPaths.LegacyConfigurationPath
            )
        );
    }
}

internal interface IUserConfigurationMigration
{
    int FromVersion { get; }

    int ToVersion { get; }

    void Apply(JsonObject configuration);
}

internal sealed class UserConfigurationSchemaZeroToOneMigration : IUserConfigurationMigration
{
    public int FromVersion => 0;

    public int ToVersion => 1;

    public void Apply(JsonObject configuration)
    {
        configuration["_meta"] = new JsonObject { ["schemaVersion"] = ToVersion };
    }
}

internal sealed class ApplicationConfigurationSynchronizer
{
    public const int CurrentSchemaVersion = 1;
    public const int RetainedBackupCount = 5;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly IReadOnlyDictionary<int, IUserConfigurationMigration> _migrations;
    private readonly TimeProvider _timeProvider;
    private readonly int _currentSchemaVersion;

    public ApplicationConfigurationSynchronizer(
        IEnumerable<IUserConfigurationMigration>? migrations = null,
        TimeProvider? timeProvider = null,
        int currentSchemaVersion = CurrentSchemaVersion
    )
    {
        if (currentSchemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(currentSchemaVersion));
        var configuredMigrations = new IUserConfigurationMigration[]
        {
            new UserConfigurationSchemaZeroToOneMigration(),
        }
            .Concat(migrations ?? [])
            .ToArray();
        _migrations = configuredMigrations.ToDictionary(migration => migration.FromVersion);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentSchemaVersion = currentSchemaVersion;
    }

    public ApplicationConfigurationSynchronizationResult Synchronize(
        ApplicationConfigurationPaths paths
    )
    {
        AppSettingsFileGate.Semaphore.Wait();
        try
        {
            return SynchronizeCore(paths);
        }
        finally
        {
            AppSettingsFileGate.Semaphore.Release();
        }
    }

    private ApplicationConfigurationSynchronizationResult SynchronizeCore(
        ApplicationConfigurationPaths paths
    )
    {
        ArgumentNullException.ThrowIfNull(paths);
        var defaultPath = Path.GetFullPath(paths.DefaultConfigurationPath);
        var userPath = Path.GetFullPath(paths.UserConfigurationPath);
        var backupDirectory = Path.GetFullPath(paths.BackupDirectory);
        var legacyPath = string.IsNullOrWhiteSpace(paths.LegacyConfigurationPath)
            ? null
            : Path.GetFullPath(paths.LegacyConfigurationPath);

        var defaultRoot = ReadRequiredObject(defaultPath, "默认配置");
        var defaultSchemaVersion = ReadSchemaVersion(defaultRoot, defaultPath, required: true);
        if (defaultSchemaVersion != _currentSchemaVersion)
        {
            throw new ApplicationConfigurationException(
                defaultPath,
                $"默认配置 schemaVersion={defaultSchemaVersion}，但程序要求 {_currentSchemaVersion}。请重新安装当前版本。"
            );
        }

        var userFileExists = File.Exists(userPath);
        JsonObject userRoot;
        if (userFileExists)
        {
            userRoot = ReadRequiredObject(userPath, "用户配置");
        }
        else if (legacyPath is not null && File.Exists(legacyPath))
        {
            userRoot = ReadRequiredObject(legacyPath, "旧版配置");
        }
        else
        {
            userRoot = (JsonObject)defaultRoot.DeepClone();
        }

        var originalUserRoot = userRoot.DeepClone();
        var originalSchemaVersion = ReadSchemaVersion(userRoot, userPath, required: false);
        var migrationBackupCreated =
            userFileExists && originalSchemaVersion < _currentSchemaVersion;
        if (migrationBackupCreated)
            Backup(userPath, backupDirectory);
        Migrate(userRoot, userPath);
        var resetPaths = new List<string>();
        var synchronizedRoot = (JsonObject)
            ReconcileNode(defaultRoot, userRoot, string.Empty, resetPaths)!;
        var changed = !userFileExists || !JsonNode.DeepEquals(originalUserRoot, synchronizedRoot);
        if (changed)
        {
            if (userFileExists && !migrationBackupCreated)
                Backup(userPath, backupDirectory);
            WriteAtomically(userPath, synchronizedRoot);
        }

        var sourceBaseAddress = ReadSourceBaseAddress(defaultRoot, defaultPath);
        return new ApplicationConfigurationSynchronizationResult(
            userPath,
            sourceBaseAddress,
            resetPaths.AsReadOnly()
        );
    }

    private void Migrate(JsonObject root, string userPath)
    {
        var version = ReadSchemaVersion(root, userPath, required: false);
        if (version > _currentSchemaVersion)
        {
            throw new ApplicationConfigurationException(
                userPath,
                $"用户配置 schemaVersion={version} 高于当前程序支持的 {_currentSchemaVersion}。请安装较新版本，禁止使用旧程序覆盖该配置。"
            );
        }

        while (version < _currentSchemaVersion)
        {
            if (
                !_migrations.TryGetValue(version, out var migration)
                || migration.ToVersion <= version
                || migration.ToVersion > _currentSchemaVersion
            )
            {
                throw new ApplicationConfigurationException(
                    userPath,
                    $"缺少从用户配置 schemaVersion={version} 开始的迁移步骤。"
                );
            }

            try
            {
                migration.Apply(root);
            }
            catch (Exception exception) when (exception is not ApplicationConfigurationException)
            {
                throw new ApplicationConfigurationException(
                    userPath,
                    $"用户配置从 schemaVersion={version} 迁移失败：{exception.Message}",
                    exception
                );
            }
            version = migration.ToVersion;
            SetSchemaVersion(root, version);
        }
    }

    private static JsonNode? ReconcileNode(
        JsonNode? defaultNode,
        JsonNode? userNode,
        string path,
        ICollection<string> resetPaths
    )
    {
        if (IsForcedDefaultPath(path))
            return defaultNode?.DeepClone();
        if (defaultNode is null)
            return userNode?.DeepClone();

        if (defaultNode is JsonObject defaultObject)
        {
            if (userNode is not JsonObject userObject)
            {
                resetPaths.Add(DisplayPath(path));
                return defaultObject.DeepClone();
            }

            var result = new JsonObject();
            foreach (var property in defaultObject)
            {
                var childPath = AppendPath(path, property.Key);
                if (userObject.TryGetPropertyValue(property.Key, out var userPropertyValue))
                {
                    result[property.Key] = ReconcileNode(
                        property.Value,
                        userPropertyValue,
                        childPath,
                        resetPaths
                    );
                }
                else
                {
                    result[property.Key] = property.Value?.DeepClone();
                }
            }
            return result;
        }

        if (defaultNode is JsonArray defaultArray)
        {
            if (userNode is not JsonArray userArray)
            {
                resetPaths.Add(DisplayPath(path));
                return defaultArray.DeepClone();
            }

            var fallbackTemplate =
                defaultArray.FirstOrDefault(item => item is JsonObject) as JsonObject;
            if (fallbackTemplate is null)
                return userArray.DeepClone();

            var result = new JsonArray();
            for (var index = 0; index < userArray.Count; index++)
            {
                var item = userArray[index];
                var objectTemplate =
                    FindArrayObjectTemplate(defaultArray, item as JsonObject, index)
                    ?? fallbackTemplate;
                result.Add(ReconcileNode(objectTemplate, item, $"{path}[]", resetPaths));
            }
            return result;
        }

        if (
            defaultNode is JsonValue defaultValue
            && userNode is JsonValue userValue
            && AreCompatibleValues(defaultValue, userValue)
        )
        {
            return userValue.DeepClone();
        }

        resetPaths.Add(DisplayPath(path));
        return defaultNode.DeepClone();
    }

    private static JsonObject? FindArrayObjectTemplate(
        JsonArray defaultArray,
        JsonObject? userObject,
        int userIndex
    )
    {
        if (userObject is not null)
        {
            foreach (var identityProperty in new[] { "Id", "ElectrodeId" })
            {
                if (
                    userObject[identityProperty] is not JsonValue userIdentity
                    || !userIdentity.TryGetValue<string>(out var identity)
                )
                {
                    continue;
                }

                var match = defaultArray
                    .OfType<JsonObject>()
                    .FirstOrDefault(candidate =>
                        candidate[identityProperty] is JsonValue candidateIdentity
                        && candidateIdentity.TryGetValue<string>(out var candidateValue)
                        && string.Equals(
                            candidateValue,
                            identity,
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                if (match is not null)
                    return match;
            }
        }

        return userIndex < defaultArray.Count ? defaultArray[userIndex] as JsonObject : null;
    }

    private static bool AreCompatibleValues(JsonValue defaultValue, JsonValue userValue)
    {
        var defaultKind = defaultValue.GetValueKind();
        var userKind = userValue.GetValueKind();
        return defaultKind == userKind
            || defaultKind is JsonValueKind.True or JsonValueKind.False
                && userKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool IsForcedDefaultPath(string path) =>
        string.Equals(path, "_meta", StringComparison.Ordinal)
        || string.Equals(path, "ApplicationUpdate:SourceBaseAddress", StringComparison.Ordinal);

    private static string AppendPath(string parent, string property) =>
        string.IsNullOrEmpty(parent) ? property : $"{parent}:{property}";

    private static string DisplayPath(string path) =>
        string.IsNullOrEmpty(path) ? "<根节点>" : path;

    private static int ReadSchemaVersion(JsonObject root, string path, bool required)
    {
        var node = root["_meta"]?["schemaVersion"];
        if (node is null && !required)
            return 0;
        if (node is JsonValue value && value.TryGetValue<int>(out var version) && version >= 0)
        {
            return version;
        }

        throw new ApplicationConfigurationException(
            path,
            "配置缺少有效的 _meta.schemaVersion 非负整数。请恢复有效配置后重试。"
        );
    }

    private static void SetSchemaVersion(JsonObject root, int version)
    {
        if (root["_meta"] is not JsonObject metadata)
        {
            metadata = new JsonObject();
            root["_meta"] = metadata;
        }
        metadata["schemaVersion"] = version;
    }

    private static string ReadSourceBaseAddress(JsonObject root, string path)
    {
        var node = root["ApplicationUpdate"]?["SourceBaseAddress"];
        if (node is JsonValue value && value.TryGetValue<string>(out var source))
            return source;
        throw new ApplicationConfigurationException(
            path,
            "默认配置缺少字符串 ApplicationUpdate.SourceBaseAddress。请重新安装当前版本。"
        );
    }

    private static JsonObject ReadRequiredObject(string path, string description)
    {
        if (!File.Exists(path))
            throw new ApplicationConfigurationException(path, $"找不到{description}文件：{path}");
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new ApplicationConfigurationException(
                    path,
                    $"{description}根节点必须是 JSON 对象。"
                );
        }
        catch (ApplicationConfigurationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ApplicationConfigurationException(
                path,
                $"无法读取{description}：{exception.Message}",
                exception
            );
        }
    }

    private void Backup(string userPath, string backupDirectory)
    {
        try
        {
            Directory.CreateDirectory(backupDirectory);
            var timestamp = _timeProvider
                .GetUtcNow()
                .UtcDateTime.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(
                backupDirectory,
                $"appsettings.{timestamp}.{Guid.NewGuid():N}.json"
            );
            File.Copy(userPath, backupPath, overwrite: false);
            TrimBackups(backupDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApplicationConfigurationException(
                userPath,
                $"创建用户配置备份失败：{exception.Message}",
                exception
            );
        }
    }

    private static void TrimBackups(string backupDirectory)
    {
        try
        {
            foreach (
                var backup in new DirectoryInfo(backupDirectory)
                    .EnumerateFiles("appsettings.*.json")
                    .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                    .Skip(RetainedBackupCount)
            )
            {
                backup.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApplicationConfigurationException(
                backupDirectory,
                $"清理旧配置备份失败：{exception.Message}",
                exception
            );
        }
    }

    private static void WriteAtomically(string path, JsonObject root)
    {
        var directory =
            Path.GetDirectoryName(path)
            ?? throw new ApplicationConfigurationException(path, "无法确定用户配置目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"
        );
        try
        {
            var content = root.ToJsonString(JsonOptions) + Environment.NewLine;
            File.WriteAllText(temporaryPath, content, Utf8WithoutBom);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApplicationConfigurationException(
                path,
                $"写入用户配置失败：{exception.Message}",
                exception
            );
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
