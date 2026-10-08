using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.Persistence;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Services;

public interface IFontScaleService
{
    FontSizePreset CurrentPreset { get; }

    void Initialize();

    Task ApplyAsync(FontSizePreset preset, CancellationToken cancellationToken = default);
}

public sealed class FontScaleService : IFontScaleService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly double[] BaseFontSizes =
    [
        9,
        10,
        10.5,
        11,
        11.5,
        12,
        13,
        14,
        15,
        16,
        17,
        18,
        19,
        20,
        22,
        24,
        25,
        26,
        28,
        32,
        34,
        43,
    ];

    private readonly string _configurationPath;
    private readonly Action<FontSizePreset> _applyResources;
    private readonly bool _useUiDispatcher;

    public FontScaleService(IOptions<DisplayOptions> options)
        : this(
            options?.Value.FontSizePreset ?? FontSizePreset.Standard,
            AppPaths.UserConfigurationPath,
            ApplyApplicationResources,
            useUiDispatcher: true
        ) { }

    internal FontScaleService(
        FontSizePreset initialPreset,
        string configurationPath,
        Action<FontSizePreset>? applyResources = null,
        bool useUiDispatcher = false
    )
    {
        CurrentPreset = Normalize(initialPreset);
        _configurationPath = Path.GetFullPath(configurationPath);
        _applyResources = applyResources ?? (_ => { });
        _useUiDispatcher = useUiDispatcher;
    }

    public FontSizePreset CurrentPreset { get; private set; }

    public void Initialize() => _applyResources(CurrentPreset);

    public async Task ApplyAsync(
        FontSizePreset preset,
        CancellationToken cancellationToken = default
    )
    {
        preset = Normalize(preset);
        if (preset == CurrentPreset)
            return;

        var previous = CurrentPreset;
        using var operation = new LoggedOperation(
            nameof(FontScaleService),
            "Configuration.Save",
            "fields=Display.FontSizePreset",
            cancellationToken: cancellationToken
        );
        CurrentPreset = preset;
        await ApplyOnUiThreadAsync(preset);
        try
        {
            await PersistAsync(preset, cancellationToken).ConfigureAwait(false);
            operation.Complete("fields=Display.FontSizePreset");
        }
        catch
        {
            CurrentPreset = previous;
            await ApplyOnUiThreadAsync(previous);
            throw;
        }
    }

    public static double GetScale(FontSizePreset preset) =>
        Normalize(preset) switch
        {
            FontSizePreset.Small => 0.85,
            FontSizePreset.Standard => 1.0,
            FontSizePreset.Large => 1.15,
            FontSizePreset.ExtraLarge => 1.30,
            _ => 1.0,
        };

    private static FontSizePreset Normalize(FontSizePreset preset) =>
        Enum.IsDefined(preset) ? preset : FontSizePreset.Standard;

    private Task ApplyOnUiThreadAsync(FontSizePreset preset)
    {
        if (!_useUiDispatcher || Dispatcher.UIThread.CheckAccess())
        {
            _applyResources(preset);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => _applyResources(preset)).GetTask();
    }

    private async Task PersistAsync(FontSizePreset preset, CancellationToken cancellationToken)
    {
        await AppSettingsFileGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettingsFileGate.ThrowIfWritesBlocked();
            var directory =
                Path.GetDirectoryName(_configurationPath)
                ?? throw new InvalidOperationException("无法确定用户配置文件所在目录。");
            Directory.CreateDirectory(directory);
            var root = File.Exists(_configurationPath)
                ? JsonNode.Parse(
                    await File.ReadAllTextAsync(_configurationPath, cancellationToken)
                        .ConfigureAwait(false)
                ) as JsonObject
                    ?? throw new InvalidDataException("用户配置文件根节点必须是 JSON 对象。")
                : new JsonObject();
            var section = root[DisplayOptions.SectionName] as JsonObject ?? new JsonObject();
            section[nameof(DisplayOptions.FontSizePreset)] = preset.ToString();
            root[DisplayOptions.SectionName] = section;

            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp"
            );
            try
            {
                await File.WriteAllTextAsync(
                        temporaryPath,
                        root.ToJsonString(JsonOptions),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                File.Move(temporaryPath, _configurationPath, true);
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

        ApplicationConfigurationRuntime.SynchronizeAfterProductionWrite(_configurationPath);
    }

    private static void ApplyApplicationResources(FontSizePreset preset)
    {
        var resources =
            Application.Current?.Resources
            ?? throw new InvalidOperationException("应用资源尚未初始化。");
        var scale = GetScale(preset);
        foreach (var baseSize in BaseFontSizes)
            resources[GetResourceKey(baseSize)] = Math.Round(baseSize * scale, 3);
    }

    private static string GetResourceKey(double baseSize) =>
        $"FontSize{baseSize.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_')}";
}
