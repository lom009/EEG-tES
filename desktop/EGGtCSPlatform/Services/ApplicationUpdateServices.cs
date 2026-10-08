using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Velopack;

namespace EGGtCSPlatform.Services;

public sealed class ApplicationUpdateOptions
{
    public const string SectionName = "ApplicationUpdate";
    public const string DefaultSourceBaseAddress = "http://127.0.0.1:8080";

    public bool AutoCheckOnStartup { get; set; } = true;

    public string SourceBaseAddress { get; set; } = DefaultSourceBaseAddress;

    public bool IsValid() =>
        !AutoCheckOnStartup || ApplicationUpdateSource.IsValidBaseAddress(SourceBaseAddress);
}

public static class ApplicationUpdateSource
{
    public static bool IsValidBaseAddress(string? sourceBaseAddress) =>
        Uri.TryCreate(sourceBaseAddress?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    public static string Resolve(string sourceBaseAddress, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!IsValidBaseAddress(sourceBaseAddress))
            throw new ArgumentException(
                "更新源基地址必须是无查询参数和片段的 HTTP/HTTPS 绝对地址。",
                nameof(sourceBaseAddress)
            );

        var baseUri = new Uri(sourceBaseAddress.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var relativeTarget = targetPath.Trim().Trim('/');
        if (relativeTarget.Length == 0 || relativeTarget.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("更新目标路径无效。", nameof(targetPath));

        return new Uri(baseUri, relativeTarget).AbsoluteUri.TrimEnd('/');
    }
}

public static class ApplicationUpdateTarget
{
    public static string GetFeedPath(string runtimeIdentifier) =>
        runtimeIdentifier switch
        {
            "win-x86" => "win/x86",
            "win-x64" => "win/x64",
            "win-arm64" => "win/arm64",
            "linux-x64" => "linux/x64",
            "linux-arm64" => "linux/arm64",
            "osx-x64" => "osx/x64",
            "osx-arm64" => "osx/arm64",
            _ => throw new ArgumentOutOfRangeException(
                nameof(runtimeIdentifier),
                runtimeIdentifier,
                "不支持的更新运行时标识。"
            ),
        };

    public static string CurrentRuntimeIdentifier => RuntimeInformation.RuntimeIdentifier;

    public static string CurrentFeedPath => GetFeedPath(CurrentRuntimeIdentifier);
}

public sealed record AvailableApplicationUpdate(string CurrentVersion, string TargetVersion);

public interface IApplicationUpdateClient
{
    bool IsInstalled { get; }

    Task<AvailableApplicationUpdate?> CheckForUpdatesAsync();

    Task DownloadAsync(
        AvailableApplicationUpdate update,
        Action<int> progress,
        CancellationToken cancellationToken = default
    );

    void ApplyAndRestart(AvailableApplicationUpdate update);
}

internal sealed class VelopackApplicationUpdateClient : IApplicationUpdateClient
{
    private readonly UpdateManager _manager;
    private readonly IApplicationVersionProvider _applicationVersion;
    private readonly string _runtimeIdentifier;
    private readonly string _source;
    private UpdateInfo? _pendingUpdate;
    private readonly IApplicationLogger _logger;

    public VelopackApplicationUpdateClient(
        IOptions<ApplicationUpdateOptions> options,
        IApplicationVersionProvider applicationVersion,
        IApplicationLogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? ApplicationLog.Current;
        _applicationVersion =
            applicationVersion ?? throw new ArgumentNullException(nameof(applicationVersion));

        var sourceBaseAddress = string.IsNullOrWhiteSpace(options.Value.SourceBaseAddress)
            ? ApplicationUpdateOptions.DefaultSourceBaseAddress
            : options.Value.SourceBaseAddress;
        _runtimeIdentifier = ApplicationUpdateTarget.CurrentRuntimeIdentifier;
        _source = ApplicationUpdateSource.Resolve(
            sourceBaseAddress,
            ApplicationUpdateTarget.GetFeedPath(_runtimeIdentifier)
        );
        _logger.Write(
            ApplicationLogLevel.Debug,
            nameof(VelopackApplicationUpdateClient),
            "Update.SourceResolved",
            $"rid={_runtimeIdentifier}; source={_source}"
        );
        _manager = new UpdateManager(_source);
    }

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<AvailableApplicationUpdate?> CheckForUpdatesAsync()
    {
        using var operation = new LoggedOperation(
            nameof(VelopackApplicationUpdateClient),
            "Update.Check",
            "检查更新",
            logger: _logger
        );
        try
        {
            _pendingUpdate = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw new InvalidOperationException(
                $"检查更新失败（RID={_runtimeIdentifier}，更新地址={_source}）：{exception.Message}",
                exception
            );
        }

        operation.Complete(
            _pendingUpdate is null
                ? "没有可用更新"
                : $"available={_pendingUpdate.TargetFullRelease.Version}"
        );
        if (_pendingUpdate is null)
            return null;

        return new AvailableApplicationUpdate(
            _manager.CurrentVersion?.ToString() ?? _applicationVersion.Version,
            _pendingUpdate.TargetFullRelease.Version.ToString()
        );
    }

    public async Task DownloadAsync(
        AvailableApplicationUpdate update,
        Action<int> progress,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);
        using var operation = new LoggedOperation(
            nameof(VelopackApplicationUpdateClient),
            "Update.Download",
            $"version={update.TargetVersion}",
            cancellationToken: cancellationToken,
            logger: _logger
        );
        try
        {
            var pending = GetPendingUpdate(update);
            await _manager.DownloadUpdatesAsync(
                pending,
                value => progress(Math.Clamp(value, 0, 100)),
                cancellationToken
            );
            operation.Complete("更新下载完成");
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public void ApplyAndRestart(AvailableApplicationUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var pending = GetPendingUpdate(update);
        using var operation = new LoggedOperation(
            nameof(VelopackApplicationUpdateClient),
            "Update.Apply",
            $"version={update.TargetVersion}; restartRequested=true",
            logger: _logger
        );
        try
        {
            ApplicationLog.FlushBeforeExitAsync().GetAwaiter().GetResult();
            _manager.ApplyUpdatesAndRestart(pending.TargetFullRelease);
            operation.Complete("已提交安装及重启请求");
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    private UpdateInfo GetPendingUpdate(AvailableApplicationUpdate update)
    {
        if (
            _pendingUpdate is null
            || !string.Equals(
                _pendingUpdate.TargetFullRelease.Version.ToString(),
                update.TargetVersion,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidOperationException("待应用的更新已失效，请重新检查更新。");
        }

        return _pendingUpdate;
    }
}
