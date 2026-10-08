using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class FontScaleServiceTests
{
    [Theory]
    [InlineData(FontSizePreset.Small, 0.85)]
    [InlineData(FontSizePreset.Standard, 1.0)]
    [InlineData(FontSizePreset.Large, 1.15)]
    [InlineData(FontSizePreset.ExtraLarge, 1.30)]
    public void PresetsHaveExpectedScale(FontSizePreset preset, double expected) =>
        Assert.Equal(expected, FontScaleService.GetScale(preset));

    [Fact]
    public void InvalidInitialPresetFallsBackToStandard()
    {
        FontSizePreset? applied = null;
        using var fixture = new TemporaryDirectory();
        var service = new FontScaleService(
            (FontSizePreset)999,
            Path.Combine(fixture.Path, "appsettings.json"),
            preset => applied = preset
        );

        service.Initialize();

        Assert.Equal(FontSizePreset.Standard, service.CurrentPreset);
        Assert.Equal(FontSizePreset.Standard, applied);
    }

    [Fact]
    public async Task ApplyPersistsOnlyDisplaySettingAndPreservesOtherConfiguration()
    {
        using var fixture = new TemporaryDirectory();
        var path = Path.Combine(fixture.Path, "appsettings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "_meta": { "schemaVersion": 1 },
              "Application": { "SingleInstance": true },
              "Display": { "Unrelated": "keep" }
            }
            """
        );
        FontSizePreset? applied = null;
        var service = new FontScaleService(
            FontSizePreset.Standard,
            path,
            preset => applied = preset
        );

        await service.ApplyAsync(FontSizePreset.Large);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(root["Application"]!["SingleInstance"]!.GetValue<bool>());
        Assert.Equal("keep", root["Display"]!["Unrelated"]!.GetValue<string>());
        Assert.Equal("Large", root["Display"]!["FontSizePreset"]!.GetValue<string>());
        Assert.Equal(FontSizePreset.Large, service.CurrentPreset);
        Assert.Equal(FontSizePreset.Large, applied);
    }

    [Fact]
    public async Task WriteFailureRestoresPreviousPresetAndResources()
    {
        using var fixture = new TemporaryDirectory();
        var applied = FontSizePreset.Standard;
        var service = new FontScaleService(
            FontSizePreset.Standard,
            fixture.Path,
            preset => applied = preset
        );

        var exception = await Record.ExceptionAsync(() =>
            service.ApplyAsync(FontSizePreset.ExtraLarge)
        );

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Equal(FontSizePreset.Standard, service.CurrentPreset);
        Assert.Equal(FontSizePreset.Standard, applied);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"font-scale-tests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
