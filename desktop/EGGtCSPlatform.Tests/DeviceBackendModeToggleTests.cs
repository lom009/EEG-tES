using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceBackendModeToggleTests
{
    [Fact]
    public async Task FirstToggleBacksUpWholeFileAndSetsEveryBackendSourceToSimulated()
    {
        using var fixture = new ConfigurationFixture();
        var original = fixture.WriteConfiguration("Real", "Real", "before");
        var service = new DeviceBackendModeToggleService(fixture.ConfigurationPath);

        var result = await service.ToggleAsync();

        Assert.Equal(DeviceBackendModeToggleResult.SwitchedToSimulated, result);
        Assert.Equal(original, await File.ReadAllBytesAsync(service.BackupPath));
        var root = JsonNode
            .Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath))!
            .AsObject();
        var backend = root["DeviceBackend"]!.AsObject();
        Assert.Equal("Simulated", backend["ConnectionSource"]!.GetValue<string>());
        var capabilities = backend["Capabilities"]!.AsObject();
        Assert.Equal(
            new[] { "Simulated" },
            capabilities.Select(property => property.Value!.GetValue<string>()).Distinct()
        );
        Assert.Equal("before", root["Unrelated"]!["Value"]!.GetValue<string>());
    }

    [Fact]
    public async Task SecondToggleRestoresOnlyBackendAndDeletesBackup()
    {
        using var fixture = new ConfigurationFixture();
        fixture.WriteConfiguration("Real", "Disabled", "before");
        var service = new DeviceBackendModeToggleService(fixture.ConfigurationPath);
        await service.ToggleAsync();

        var current = JsonNode
            .Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath))!
            .AsObject();
        current["Unrelated"]!["Value"] = "changed-while-simulated";
        await File.WriteAllTextAsync(fixture.ConfigurationPath, current.ToJsonString());

        var result = await service.ToggleAsync();

        Assert.Equal(DeviceBackendModeToggleResult.RestoredOriginal, result);
        Assert.False(File.Exists(service.BackupPath));
        var restored = JsonNode
            .Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath))!
            .AsObject();
        Assert.Equal("Real", restored["DeviceBackend"]!["ConnectionSource"]!.GetValue<string>());
        Assert.Equal(
            "Disabled",
            restored["DeviceBackend"]!["Capabilities"]!["Tolerance"]!.GetValue<string>()
        );
        Assert.Equal(
            "changed-while-simulated",
            restored["Unrelated"]!["Value"]!.GetValue<string>()
        );
    }

    [Fact]
    public async Task InvalidBackupDoesNotChangeCurrentConfiguration()
    {
        using var fixture = new ConfigurationFixture();
        var service = new DeviceBackendModeToggleService(fixture.ConfigurationPath);
        fixture.WriteConfiguration("Simulated", "Simulated", "current");
        await File.WriteAllTextAsync(service.BackupPath, "not-json");
        var before = await File.ReadAllBytesAsync(fixture.ConfigurationPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ToggleAsync());

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ConfigurationPath));
        Assert.True(File.Exists(service.BackupPath));
    }

    [Fact]
    public async Task FiveClicksWithinThreeSecondsToggleAndRestartOnce()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var toggle = new RecordingToggleService();
        var viewModel = new LoginViewModel(new StubAuthenticationService(), toggle, clock);
        var exitRequestCount = 0;
        viewModel.ExitRequested += () => exitRequestCount++;

        for (var index = 0; index < 4; index++)
        {
            await viewModel.CompanyNameClickedCommand.ExecuteAsync(null);
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(0, toggle.CallCount);
        await viewModel.CompanyNameClickedCommand.ExecuteAsync(null);

        Assert.Equal(1, toggle.CallCount);
        Assert.Equal(1, exitRequestCount);
    }

    [Fact]
    public async Task ExpiredClicksDoNotCountTowardGesture()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var toggle = new RecordingToggleService();
        var viewModel = new LoginViewModel(new StubAuthenticationService(), toggle, clock);
        var exitRequestCount = 0;
        viewModel.ExitRequested += () => exitRequestCount++;

        for (var index = 0; index < 4; index++)
            await viewModel.CompanyNameClickedCommand.ExecuteAsync(null);
        clock.Advance(TimeSpan.FromSeconds(3.001));
        await viewModel.CompanyNameClickedCommand.ExecuteAsync(null);

        Assert.Equal(0, toggle.CallCount);
        Assert.Equal(0, exitRequestCount);
    }

    [Fact]
    public async Task ToggleFailureIsShownAndDoesNotExit()
    {
        var viewModel = new LoginViewModel(
            new StubAuthenticationService(),
            new RecordingToggleService(new IOException("配置不可写")),
            new ManualTimeProvider(DateTimeOffset.UtcNow)
        );
        var exitRequestCount = 0;
        viewModel.ExitRequested += () => exitRequestCount++;

        for (var index = 0; index < 5; index++)
            await viewModel.CompanyNameClickedCommand.ExecuteAsync(null);

        Assert.Equal("切换设备模式失败：配置不可写", viewModel.ErrorMessage);
        Assert.Equal(0, exitRequestCount);
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"eggtcs-backend-toggle-{Guid.NewGuid():N}"
        );

        public ConfigurationFixture()
        {
            Directory.CreateDirectory(_directory);
            ConfigurationPath = Path.Combine(_directory, "appsettings.json");
        }

        public string ConfigurationPath { get; }

        public byte[] WriteConfiguration(
            string connectionSource,
            string toleranceSource,
            string unrelatedValue
        )
        {
            var root = new JsonObject
            {
                ["DeviceBackend"] = new JsonObject
                {
                    ["ConnectionSource"] = connectionSource,
                    ["Capabilities"] = new JsonObject
                    {
                        ["Status"] = "Real",
                        ["EegAcquisition"] = "Real",
                        ["Stimulation"] = "Real",
                        ["EegImpedance"] = "Real",
                        ["StimulationImpedance"] = "Real",
                        ["Tolerance"] = toleranceSource,
                    },
                },
                ["Unrelated"] = new JsonObject { ["Value"] = unrelatedValue },
            };
            var bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
            File.WriteAllBytes(ConfigurationPath, bytes);
            return bytes;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class StubAuthenticationService : IAuthenticationService
    {
        public Task<bool> SignInAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);
    }

    private sealed class RecordingToggleService(Exception? exception = null)
        : IDeviceBackendModeToggleService
    {
        public int CallCount { get; private set; }

        public Task<DeviceBackendModeToggleResult> ToggleAsync(
            CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            return exception is null
                ? Task.FromResult(DeviceBackendModeToggleResult.SwitchedToSimulated)
                : Task.FromException<DeviceBackendModeToggleResult>(exception);
        }
    }
}
