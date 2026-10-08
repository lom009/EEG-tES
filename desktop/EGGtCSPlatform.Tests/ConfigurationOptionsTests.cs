using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.Bootstrap;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ConfigurationOptionsTests
{
    [Fact]
    public void DefaultJsonConfigurationCreatesStimulusCapabilityProfile()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.default.json", optional: false)
            .Build();
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var application = provider.GetRequiredService<IOptions<ApplicationBehaviorOptions>>().Value;
        var display = provider.GetRequiredService<IOptions<DisplayOptions>>().Value;
        var options = provider.GetRequiredService<IOptions<StimulationChannelOptions>>().Value;
        var capability = provider.GetRequiredService<StimulusCapabilityProfile>();
        var impedance = provider.GetRequiredService<IOptions<ImpedanceDetectionOptions>>().Value;
        var runTiming = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value;
        var eegAcquisition = provider.GetRequiredService<IOptions<EegAcquisitionOptions>>().Value;
        var stimulationRun = provider.GetRequiredService<IOptions<StimulationRunOptions>>().Value;
        var backend = provider.GetRequiredService<IOptions<DeviceBackendOptions>>().Value;
        var electrodePositions = provider
            .GetRequiredService<IOptions<ElectrodePositionOptions>>()
            .Value;
        var electrodeCatalog = provider.GetRequiredService<IElectrodePositionCatalog>();
        var stimulationCurrent = provider
            .GetRequiredService<IOptions<StimulationCurrentOptions>>()
            .Value;
        var protocolBehavior = provider
            .GetRequiredService<IOptions<V101ProtocolBehaviorOptions>>()
            .Value;
        var heartbeat = provider.GetRequiredService<IOptions<DeviceHeartbeatOptions>>().Value;
        var autoConnection = provider
            .GetRequiredService<IOptions<DeviceAutoConnectionOptions>>()
            .Value;
        var connectionNetwork = provider.GetRequiredService<IDeviceConnectionNetworkContext>();
        var currentPolicy = provider.GetRequiredService<StimulationCurrentPolicy>();

        Assert.True(application.SingleInstance);
        Assert.True(application.NavigationAnimationsEnabled);
        Assert.False(application.ShowPreviousCrashOnStartup);
        Assert.Equal(FontSizePreset.Standard, display.FontSizePreset);
        Assert.Equal(2.5d, display.WaveformStrokeThickness);
        Assert.True(options.IsValid());
        Assert.Equal(8, options.AllowedElectrodeSiteIds.Length);
        Assert.Equal(
            8,
            options.AllowedElectrodeSiteIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
        );
        Assert.Equal(8, capability.StimulationPhysicalChannelCount);
        Assert.Equal(4, capability.MaximumSelectableChannelCount);
        Assert.Equal(8, capability.StimulusSiteIds.Count);
        Assert.True(impedance.AutoStopStimulationWhenPassed);
        Assert.True(impedance.AutoStopEegWhenPassed);
        Assert.Equal(TimeSpan.FromSeconds(5), impedance.AutoStopWhenNotPassedTimeout);
        Assert.True(impedance.AllowConfirmationWhenFailed);
        Assert.Equal(1000, runTiming.DurationStepMilliseconds);
        Assert.Equal(1000, runTiming.DurationMinimumMilliseconds);
        Assert.Equal(65535000, runTiming.DurationMaximumMilliseconds);
        Assert.Equal(1000, runTiming.BlankingDurationMilliseconds);
        Assert.Equal(1000, runTiming.RecoveryDurationMilliseconds);
        Assert.Equal(30, runTiming.WaveformRefreshRateFps);
        Assert.Equal(500, eegAcquisition.SampleRateHz);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), eegAcquisition.DataPacketTimeout);
        Assert.True(eegAcquisition.EnablePacketReordering);
        Assert.Equal(TimeSpan.FromMilliseconds(50), eegAcquisition.PacketReorderTimeout);
        Assert.Equal(16, eegAcquisition.PacketReorderWindowPackets);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), stimulationRun.ProgressPacketTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), stimulationRun.CompletionEventGracePeriod);
        Assert.Equal(DeviceConnectionSource.Real, backend.ConnectionSource);
        Assert.Equal(34, electrodePositions.Positions.Length);
        Assert.Equal(34, electrodeCatalog.Positions.Count);
        Assert.Equal(
            ElectrodeRole.Ground,
            electrodeCatalog.Positions.Single(item => item.Id == "AFz").DefaultRole
        );
        Assert.Equal(
            ElectrodeRole.Reference,
            electrodeCatalog.Positions.Single(item => item.Id == "FCz").DefaultRole
        );
        Assert.Equal(4, stimulationCurrent.Minimum);
        Assert.Equal(200, stimulationCurrent.Maximum);
        Assert.Equal(4, stimulationCurrent.Step);
        Assert.Equal(0.04d, currentPolicy.MinimumMilliAmps, 6);
        Assert.Equal(2d, currentPolicy.MaximumMilliAmps, 6);
        Assert.Equal(0.04d, currentPolicy.StepMilliAmps, 6);
        Assert.Equal(100, currentPolicy.ToRaw(1d));
        Assert.False(protocolBehavior.RequireMatchingResponseIndex);
        Assert.True(heartbeat.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(2), heartbeat.Interval);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), heartbeat.ResponseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), heartbeat.DisconnectTimeout);
        Assert.Equal(30307, autoConnection.DefaultDevicePort);
        Assert.Equal(30307, connectionNetwork.Snapshot.DevicePort);
    }

    [Theory]
    [InlineData("Small", FontSizePreset.Small)]
    [InlineData("extraLarge", FontSizePreset.ExtraLarge)]
    [InlineData("not-a-preset", FontSizePreset.Standard)]
    public void DisplayFontSizePresetIsParsedWithSafeFallback(
        string configuredValue,
        FontSizePreset expected
    )
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?> { ["Display:FontSizePreset"] = configuredValue }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var display = provider.GetRequiredService<IOptions<DisplayOptions>>().Value;

        Assert.Equal(expected, display.FontSizePreset);
    }

    [Theory]
    [InlineData("0.1", 0.1d)]
    [InlineData("3.75", 3.75d)]
    [InlineData("10", 10d)]
    [InlineData("0", 2.5d)]
    [InlineData("-1", 2.5d)]
    [InlineData("10.1", 2.5d)]
    [InlineData("NaN", 2.5d)]
    [InlineData("Infinity", 2.5d)]
    [InlineData("not-a-number", 2.5d)]
    public void DisplayWaveformStrokeThicknessIsParsedWithSafeFallback(
        string configuredValue,
        double expected
    )
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["Display:WaveformStrokeThickness"] = configuredValue,
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var display = provider.GetRequiredService<IOptions<DisplayOptions>>().Value;

        Assert.Equal(expected, display.WaveformStrokeThickness);
    }

    [Fact]
    public void MissingDisplayWaveformStrokeThicknessUsesDefault()
    {
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, BuildConfiguration([]));
        using var provider = services.BuildServiceProvider();

        var display = provider.GetRequiredService<IOptions<DisplayOptions>>().Value;

        Assert.Equal(2.5d, display.WaveformStrokeThickness);
    }

    [Fact]
    public void StronglyTypedConfigurationBindsHeartbeatAndSerialLogValues()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["Application:NavigationAnimationsEnabled"] = "false",
                ["Application:ShowPreviousCrashOnStartup"] = "true",
                ["DeviceHeartbeat:Enabled"] = "true",
                ["DeviceHeartbeat:Interval"] = "00:00:03",
                ["DeviceHeartbeat:ResponseTimeout"] = "00:00:01",
                ["DeviceHeartbeat:DisconnectTimeout"] = "00:00:09",
                ["DeviceAutoConnection:DefaultDevicePort"] = "31007",
                ["SerialLog:Enabled"] = "true",
                ["SerialLog:MaxFileSizeBytes"] = "2048",
                ["SerialLog:RetentionDays"] = "3",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var heartbeat = provider.GetRequiredService<IOptions<DeviceHeartbeatOptions>>().Value;
        var application = provider.GetRequiredService<IOptions<ApplicationBehaviorOptions>>().Value;
        var serialLog = provider.GetRequiredService<IOptions<SerialLogOptions>>().Value;
        var impedance = provider.GetRequiredService<IOptions<ImpedanceDetectionOptions>>().Value;
        var runTiming = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value;
        var backend = provider.GetRequiredService<IOptions<DeviceBackendOptions>>().Value;
        var autoConnection = provider
            .GetRequiredService<IOptions<DeviceAutoConnectionOptions>>()
            .Value;
        var connectionNetwork = provider.GetRequiredService<IDeviceConnectionNetworkContext>();

        Assert.Equal(3, heartbeat.Interval.TotalSeconds);
        Assert.False(application.NavigationAnimationsEnabled);
        Assert.True(application.ShowPreviousCrashOnStartup);
        Assert.Equal(1, heartbeat.ResponseTimeout.TotalSeconds);
        Assert.Equal(9, heartbeat.DisconnectTimeout.TotalSeconds);
        Assert.Equal(2048, serialLog.MaxFileSizeBytes);
        Assert.Equal(3, serialLog.RetentionDays);
        Assert.True(impedance.AutoStopStimulationWhenPassed);
        Assert.True(impedance.AutoStopEegWhenPassed);
        Assert.Equal(TimeSpan.FromSeconds(5), impedance.AutoStopWhenNotPassedTimeout);
        Assert.True(impedance.AllowConfirmationWhenFailed);
        Assert.Equal(1000, runTiming.DurationStepMilliseconds);
        Assert.Equal(1000, runTiming.DurationMinimumMilliseconds);
        Assert.Equal(65535000, runTiming.DurationMaximumMilliseconds);
        Assert.Equal(1000, runTiming.BlankingDurationMilliseconds);
        Assert.Equal(1000, runTiming.RecoveryDurationMilliseconds);
        Assert.Equal(DeviceConnectionSource.Real, backend.ConnectionSource);
        Assert.Equal(DeviceCapabilitySource.Real, backend.Capabilities.EegAcquisition);
        Assert.Equal(DeviceCapabilitySource.Real, backend.Capabilities.Stimulation);
        Assert.Equal(DeviceCapabilitySource.Real, backend.Capabilities.EegImpedance);
        Assert.Equal(DeviceCapabilitySource.Real, backend.Capabilities.StimulationImpedance);
        Assert.Equal(DeviceCapabilitySource.Simulated, backend.Capabilities.Tolerance);
        Assert.Equal(31007, autoConnection.DefaultDevicePort);
        Assert.Equal(31007, connectionNetwork.Snapshot.DevicePort);
    }

    [Theory]
    [InlineData(null, 500)]
    [InlineData("250", 250)]
    [InlineData("500", 500)]
    public void EegAcquisitionSampleRateBindsSupportedValues(string? configured, int expected)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null)
            values["EegAcquisition:SampleRateHz"] = configured;
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, BuildConfiguration(values));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            expected,
            provider.GetRequiredService<IOptions<EegAcquisitionOptions>>().Value.SampleRateHz
        );
    }

    [Fact]
    public void EegAcquisitionDataTimeoutBindsAndDefaults()
    {
        var defaults = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(defaults, BuildConfiguration([]));
        using var defaultProvider = defaults.BuildServiceProvider();
        Assert.Equal(
            TimeSpan.FromMilliseconds(2500),
            defaultProvider
                .GetRequiredService<IOptions<EegAcquisitionOptions>>()
                .Value.DataPacketTimeout
        );

        var configured = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(
            configured,
            BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["EegAcquisition:DataPacketTimeout"] = "00:00:00.750",
                }
            )
        );
        using var configuredProvider = configured.BuildServiceProvider();
        Assert.Equal(
            TimeSpan.FromMilliseconds(750),
            configuredProvider
                .GetRequiredService<IOptions<EegAcquisitionOptions>>()
                .Value.DataPacketTimeout
        );
    }

    [Fact]
    public void NonPositiveEegAcquisitionDataTimeoutFailsStartupValidation()
    {
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(
            services,
            BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["EegAcquisition:DataPacketTimeout"] = "00:00:00",
                }
            )
        );
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<EegAcquisitionOptions>>().Value
        );
    }

    [Fact]
    public void StimulationRunTimeoutsBindAndDefault()
    {
        var defaults = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(defaults, BuildConfiguration([]));
        using var defaultProvider = defaults.BuildServiceProvider();
        var defaultOptions = defaultProvider
            .GetRequiredService<IOptions<StimulationRunOptions>>()
            .Value;
        Assert.Equal(TimeSpan.FromMilliseconds(2500), defaultOptions.ProgressPacketTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), defaultOptions.CompletionEventGracePeriod);

        var configured = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(
            configured,
            BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["StimulationRun:ProgressPacketTimeout"] = "00:00:00.750",
                    ["StimulationRun:CompletionEventGracePeriod"] = "00:00:01.250",
                }
            )
        );
        using var configuredProvider = configured.BuildServiceProvider();
        var configuredOptions = configuredProvider
            .GetRequiredService<IOptions<StimulationRunOptions>>()
            .Value;
        Assert.Equal(TimeSpan.FromMilliseconds(750), configuredOptions.ProgressPacketTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), configuredOptions.CompletionEventGracePeriod);
    }

    [Theory]
    [InlineData("00:00:00", "00:00:01")]
    [InlineData("00:00:01", "00:00:00")]
    public void NonPositiveStimulationRunTimeoutFailsStartupValidation(
        string progressTimeout,
        string completionGrace
    )
    {
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(
            services,
            BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["StimulationRun:ProgressPacketTimeout"] = progressTimeout,
                    ["StimulationRun:CompletionEventGracePeriod"] = completionGrace,
                }
            )
        );
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StimulationRunOptions>>().Value
        );
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("1000")]
    public void UnsupportedEegAcquisitionSampleRateFailsStartupValidation(string configured)
    {
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(
            services,
            BuildConfiguration(
                new Dictionary<string, string?> { ["EegAcquisition:SampleRateHz"] = configured }
            )
        );
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<EegAcquisitionOptions>>().Value
        );
    }

    [Fact]
    public void MissingElectrodePositionsFailsValidation()
    {
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, BuildConfiguration([]));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ElectrodePositionOptions>>().Value
        );
    }

    [Theory]
    [InlineData("FP1", "", "224", "97", "None")]
    [InlineData("FP1", "FP1", "-1", "97", "None")]
    [InlineData("FP1", "FP1", "224", "575", "None")]
    [InlineData("FP1", "FP1", "224", "97", "999")]
    public void InvalidElectrodePositionFailsValidation(
        string id,
        string position,
        string x,
        string y,
        string role
    )
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ElectrodePositions:Positions:0:Id"] = id,
                ["ElectrodePositions:Positions:0:Position"] = position,
                ["ElectrodePositions:Positions:0:ReferenceX"] = x,
                ["ElectrodePositions:Positions:0:ReferenceY"] = y,
                ["ElectrodePositions:Positions:0:DefaultRole"] = role,
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ElectrodePositionOptions>>().Value
        );
    }

    [Fact]
    public void UnknownAllowedStimulationSiteFailsValidation()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ElectrodePositions:Positions:0:Id"] = "FP1",
                ["ElectrodePositions:Positions:0:Position"] = "FP1",
                ["ElectrodePositions:Positions:0:ReferenceX"] = "224",
                ["ElectrodePositions:Positions:0:ReferenceY"] = "97",
                ["StimulationChannels:AllowedElectrodeSiteIds:0"] = "UNKNOWN",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StimulationChannelOptions>>().Value
        );
    }

    [Fact]
    public void MissingStimulationCurrentUsesProtocolDefaults()
    {
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, BuildConfiguration([]));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<StimulationCurrentOptions>>().Value;
        Assert.Equal((4, 200, 4), (options.Minimum, options.Maximum, options.Step));
    }

    [Theory]
    [InlineData("0", "200", "4")]
    [InlineData("4", "204", "4")]
    [InlineData("4", "200", "2")]
    [InlineData("8", "200", "12")]
    public void InvalidStimulationCurrentFailsValidation(
        string minimum,
        string maximum,
        string step
    )
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["StimulationCurrent:Minimum"] = minimum,
                ["StimulationCurrent:Maximum"] = maximum,
                ["StimulationCurrent:Step"] = step,
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StimulationCurrentOptions>>().Value
        );
    }

    [Fact]
    public void DeviceBackendConfigurationBindsPerCapabilitySources()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["DeviceBackend:ConnectionSource"] = "Real",
                ["DeviceBackend:Capabilities:Status"] = "Simulated",
                ["DeviceBackend:Capabilities:EegAcquisition"] = "Real",
                ["DeviceBackend:Capabilities:Stimulation"] = "Disabled",
                ["DeviceBackend:Capabilities:EegImpedance"] = "Simulated",
                ["DeviceBackend:Capabilities:StimulationImpedance"] = "Real",
                ["DeviceBackend:Capabilities:Tolerance"] = "Disabled",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<DeviceBackendOptions>>().Value;
        Assert.Equal(DeviceCapabilitySource.Simulated, options.Capabilities.Status);
        Assert.Equal(DeviceCapabilitySource.Real, options.Capabilities.EegAcquisition);
        Assert.Equal(DeviceCapabilitySource.Disabled, options.Capabilities.Stimulation);
        Assert.Equal(DeviceCapabilitySource.Simulated, options.Capabilities.EegImpedance);
        Assert.Equal(DeviceCapabilitySource.Real, options.Capabilities.StimulationImpedance);
        Assert.Equal(DeviceCapabilitySource.Disabled, options.Capabilities.Tolerance);
    }

    [Theory]
    [InlineData("00:00:00", "16")]
    [InlineData("00:00:00.050", "0")]
    [InlineData("00:00:00.050", "128")]
    public void InvalidEegPacketReorderingOptionsFailStartupValidation(
        string timeout,
        string window
    )
    {
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(
            services,
            BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["EegAcquisition:PacketReorderTimeout"] = timeout,
                    ["EegAcquisition:PacketReorderWindowPackets"] = window,
                }
            )
        );
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<EegAcquisitionOptions>>().Value
        );
    }

    [Fact]
    public void SimulatedConnectionRejectsRealCapabilities()
    {
        var options = new DeviceBackendOptions
        {
            ConnectionSource = DeviceConnectionSource.Simulated,
        };
        options.Capabilities.Status = DeviceCapabilitySource.Simulated;

        var result = new DeviceBackendOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(DeviceCapabilityKind.EegAcquisition), result.FailureMessage);
    }

    [Fact]
    public void StatusCapabilityCannotBeDisabled()
    {
        var options = new DeviceBackendOptions();
        options.Capabilities.Status = DeviceCapabilitySource.Disabled;

        var result = new DeviceBackendOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Status", result.FailureMessage);
    }

    [Fact]
    public void UndefinedCapabilitySourceFailsValidation()
    {
        var options = new DeviceBackendOptions();
        options.Capabilities.Tolerance = (DeviceCapabilitySource)999;

        var result = new DeviceBackendOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(DeviceCapabilityKind.Tolerance), result.FailureMessage);
    }

    [Fact]
    public void SimulatedConnectionAllowsSimulatedAndDisabledCapabilities()
    {
        var options = new DeviceBackendOptions
        {
            ConnectionSource = DeviceConnectionSource.Simulated,
            Capabilities = new DeviceCapabilitySourceOptions
            {
                Status = DeviceCapabilitySource.Simulated,
                EegAcquisition = DeviceCapabilitySource.Simulated,
                Stimulation = DeviceCapabilitySource.Disabled,
                EegImpedance = DeviceCapabilitySource.Simulated,
                StimulationImpedance = DeviceCapabilitySource.Disabled,
                Tolerance = DeviceCapabilitySource.Simulated,
            },
        };

        var result = new DeviceBackendOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void SimulatedConnectionDoesNotRequireUdpDiscoveryModes()
    {
        var autoConnection = new DeviceAutoConnectionOptions
        {
            GlobalBroadcast = new BroadcastDiscoveryModeOptions { Enabled = false },
            LocalBroadcast = new BroadcastDiscoveryModeOptions { Enabled = false },
            SubnetUnicast = new SubnetUnicastDiscoveryModeOptions { Enabled = false },
        };
        var backend = new DeviceBackendOptions
        {
            ConnectionSource = DeviceConnectionSource.Simulated,
        };

        var result = new DeviceAutoConnectionOptionsValidator(backend).Validate(
            null,
            autoConnection
        );

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ExperimentRunTimingBindsMillisecondValues()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ExperimentRunTiming:DurationStepMilliseconds"] = "250",
                ["ExperimentRunTiming:DurationMinimumMilliseconds"] = "250",
                ["ExperimentRunTiming:DurationMaximumMilliseconds"] = "60000",
                ["ExperimentRunTiming:BlankingDurationMilliseconds"] = "750",
                ["ExperimentRunTiming:RecoveryDurationMilliseconds"] = "1250",
                ["ExperimentRunTiming:WaveformRefreshRateFps"] = "45",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value;

        Assert.Equal(250, options.DurationStepMilliseconds);
        Assert.Equal(250, options.DurationMinimumMilliseconds);
        Assert.Equal(60000, options.DurationMaximumMilliseconds);
        Assert.Equal(750, options.BlankingDurationMilliseconds);
        Assert.Equal(1250, options.RecoveryDurationMilliseconds);
        Assert.Equal(45, options.WaveformRefreshRateFps);
    }

    [Theory]
    [InlineData("0", "1000")]
    [InlineData("1000", "-1")]
    public void NonPositiveExperimentRunTimingFailsValidation(string blanking, string recovery)
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ExperimentRunTiming:BlankingDurationMilliseconds"] = blanking,
                ["ExperimentRunTiming:RecoveryDurationMilliseconds"] = recovery,
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value
        );
    }

    [Theory]
    [InlineData("0", "1000", "1000")]
    [InlineData("-1", "1000", "1000")]
    [InlineData("1000", "1500", "1000")]
    [InlineData("1000", "1000", "1500")]
    public void InvalidDurationStepOrUnalignedFixedDurationFailsValidation(
        string step,
        string blanking,
        string recovery
    )
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ExperimentRunTiming:DurationStepMilliseconds"] = step,
                ["ExperimentRunTiming:BlankingDurationMilliseconds"] = blanking,
                ["ExperimentRunTiming:RecoveryDurationMilliseconds"] = recovery,
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value
        );
    }

    [Theory]
    [InlineData("0")]
    [InlineData("500")]
    [InlineData("1500")]
    public void InvalidDurationMaximumFailsValidation(string maximum)
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ExperimentRunTiming:DurationStepMilliseconds"] = "1000",
                ["ExperimentRunTiming:DurationMaximumMilliseconds"] = maximum,
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value
        );
    }

    [Theory]
    [InlineData("0")]
    [InlineData("500")]
    [InlineData("1500")]
    [InlineData("66000000")]
    public void InvalidDurationMinimumFailsValidation(string minimum)
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ExperimentRunTiming:DurationStepMilliseconds"] = "1000",
                ["ExperimentRunTiming:DurationMinimumMilliseconds"] = minimum,
                ["ExperimentRunTiming:DurationMaximumMilliseconds"] = "65535000",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value
        );
    }

    [Fact]
    public void FixedDurationBelowConfiguredMinimumFailsValidation()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ExperimentRunTiming:DurationStepMilliseconds"] = "1000",
                ["ExperimentRunTiming:DurationMinimumMilliseconds"] = "2000",
                ["ExperimentRunTiming:BlankingDurationMilliseconds"] = "1000",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value
        );
    }

    [Fact]
    public void FixedDurationAboveConfiguredMaximumFailsValidation()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ExperimentRunTiming:DurationStepMilliseconds"] = "1000",
                ["ExperimentRunTiming:DurationMaximumMilliseconds"] = "5000",
                ["ExperimentRunTiming:BlankingDurationMilliseconds"] = "6000",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value
        );
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    public void UnsupportedWaveformRefreshRateFailsValidation(string refreshRateFps)
    {
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(
            services,
            BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["ExperimentRunTiming:WaveformRefreshRateFps"] = refreshRateFps,
                }
            )
        );
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value
        );
    }

    [Fact]
    public void ImpedanceDetectionConfigurationBindsExplicitFalseValues()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ImpedanceDetection:AutoStopStimulationWhenPassed"] = "false",
                ["ImpedanceDetection:AutoStopEegWhenPassed"] = "false",
                ["ImpedanceDetection:AutoStopWhenNotPassedTimeout"] = "00:00:00.750",
                ["ImpedanceDetection:AllowConfirmationWhenFailed"] = "false",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ImpedanceDetectionOptions>>().Value;

        Assert.False(options.AutoStopStimulationWhenPassed);
        Assert.False(options.AutoStopEegWhenPassed);
        Assert.Equal(TimeSpan.FromMilliseconds(750), options.AutoStopWhenNotPassedTimeout);
        Assert.False(options.AllowConfirmationWhenFailed);
    }

    [Fact]
    public void NonPositiveImpedanceAutoStopTimeoutFailsValidation()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["ImpedanceDetection:AutoStopWhenNotPassedTimeout"] = "00:00:00",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<ImpedanceDetectionOptions>>().Value
        );
    }

    [Fact]
    public void StronglyTypedConfigurationBindsPerCommandV101Timeouts()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["V101RequestTimeouts:ReadDeviceStatus"] = "00:00:00.250",
                ["V101RequestTimeouts:ConfigureStimulationImpedance"] = "00:00:02.500",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var configured = provider.GetRequiredService<IOptions<V101RequestTimeoutOptions>>().Value;
        var protocol = configured.CreateProtocolOptions(_ => null);

        Assert.Equal(250, protocol.GetTimeout(typeof(ReadDeviceStatusRequest)).TotalMilliseconds);
        Assert.Equal(
            2500,
            protocol.GetTimeout(typeof(ConfigureStimulationImpedanceRequest)).TotalMilliseconds
        );
        Assert.Equal(
            1500,
            protocol.GetTimeout(typeof(ControlAcquisitionRequest)).TotalMilliseconds
        );
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void V101ResponseIndexMatchingBindsAndDefaults(string? configured, bool expected)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null)
            values["V101Protocol:RequireMatchingResponseIndex"] = configured;
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, BuildConfiguration(values));
        using var provider = services.BuildServiceProvider();

        var behavior = provider.GetRequiredService<IOptions<V101ProtocolBehaviorOptions>>().Value;
        Assert.Equal(expected, behavior.RequireMatchingResponseIndex);
        var protocol = provider
            .GetRequiredService<IOptions<V101RequestTimeoutOptions>>()
            .Value.CreateProtocolOptions(
                requireMatchingResponseIndex: behavior.RequireMatchingResponseIndex
            );
        Assert.Equal(expected, protocol.RequireMatchingResponseIndex);
    }

    [Fact]
    public void EnvironmentTimeoutOverridesConfiguredCommandWithoutAffectingOthers()
    {
        var options = new V101RequestTimeoutOptions
        {
            ReadDeviceStatus = TimeSpan.FromMilliseconds(250),
            ConfigureStimulationImpedance = TimeSpan.FromMilliseconds(500),
        };

        var protocol = options.CreateProtocolOptions(name =>
            name == "EGGTCS_V101_TIMEOUT_STIMULATION_CONFIG_MS" ? "2750" : null
        );

        Assert.Equal(250, protocol.GetTimeout(typeof(ReadDeviceStatusRequest)).TotalMilliseconds);
        Assert.Equal(
            2750,
            protocol.GetTimeout(typeof(ConfigureStimulationImpedanceRequest)).TotalMilliseconds
        );
    }

    [Fact]
    public void InvalidPerCommandV101TimeoutFailsValidation()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["V101RequestTimeouts:ConfigureStimulationImpedance"] = "00:00:00",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<V101RequestTimeoutOptions>>().Value
        );
    }

    [Theory]
    [InlineData("00:00:00", "00:00:01", "00:00:08")]
    [InlineData("00:00:02", "00:00:08", "00:00:08")]
    [InlineData("00:00:08", "00:00:01", "00:00:08")]
    public void InvalidHeartbeatConfigurationFailsWithClearValidationMessage(
        string interval,
        string responseTimeout,
        string disconnectTimeout
    )
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["DeviceHeartbeat:Interval"] = interval,
                ["DeviceHeartbeat:ResponseTimeout"] = responseTimeout,
                ["DeviceHeartbeat:DisconnectTimeout"] = disconnectTimeout,
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<DeviceHeartbeatOptions>>().Value
        );
        Assert.Contains("must be positive", exception.Message);
    }

    [Theory]
    [InlineData("0", "5")]
    [InlineData("1024", "0")]
    public void InvalidSerialLogConfigurationFailsValidation(string size, string retainedCount)
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["SerialLog:MaxFileSizeBytes"] = size,
                ["SerialLog:RetentionDays"] = retainedCount,
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<SerialLogOptions>>().Value
        );
    }

    [Fact]
    public void EmptyFixedStimulationChannelsAreAllowedAtStartup()
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["StimulationChannels:PhysicalChannelCount"] = "8",
                ["StimulationChannels:MaximumSelectableChannelCount"] = "4",
                ["StimulationChannels:DualChannelSelectableCount"] = "1",
                ["StimulationChannels:HdSelectableChannelCount"] = "4",
                ["StimulationChannels:AllowedElectrodeSiteIds:0"] = "FP1",
                ["ElectrodePositions:Positions:0:Id"] = "FP1",
                ["ElectrodePositions:Positions:0:Position"] = "FP1",
                ["ElectrodePositions:Positions:0:ReferenceX"] = "224",
                ["ElectrodePositions:Positions:0:ReferenceY"] = "97",
                ["ElectrodePositions:Positions:0:DefaultRole"] = "None",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<StimulationChannelOptions>>().Value;

        Assert.Empty(options.FixedActivePhysicalChannelIds);
        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("0", "8")]
    [InlineData("1", "9")]
    public void InvalidFixedStimulationChannelConfigurationFailsValidation(
        string first,
        string second
    )
    {
        var configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["StimulationChannels:PhysicalChannelCount"] = "8",
                ["StimulationChannels:FixedActivePhysicalChannelIds:0"] = first,
                ["StimulationChannels:FixedActivePhysicalChannelIds:1"] = second,
                ["StimulationChannels:MaximumSelectableChannelCount"] = "4",
                ["StimulationChannels:DualChannelSelectableCount"] = "1",
                ["StimulationChannels:HdSelectableChannelCount"] = "4",
            }
        );
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StimulationChannelOptions>>().Value
        );
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
