using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.Crash;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Bootstrap;

public static class Bootstrapper
{
    public static IConfiguration BuildConfiguration() =>
        BuildConfiguration(
            new ApplicationConfigurationPaths(
                AppPaths.DefaultConfigurationPath,
                AppPaths.UserConfigurationPath,
                AppPaths.ConfigurationBackupDirectory,
                AppPaths.LegacyConfigurationPath
            ),
            new ApplicationConfigurationSynchronizer()
        );

    internal static IConfiguration BuildConfiguration(
        ApplicationConfigurationPaths paths,
        ApplicationConfigurationSynchronizer synchronizer
    )
    {
        var synchronized = synchronizer.Synchronize(paths);
        foreach (var resetPath in synchronized.ResetPaths)
            ApplicationLog.Write(
                ApplicationLogLevel.Warning,
                nameof(Bootstrapper),
                "Configuration.TypeReset",
                $"配置项 {resetPath} 类型不兼容，已使用当前版本默认值。"
            );

        return new ConfigurationBuilder()
            .AddJsonFile(synchronized.UserConfigurationPath, optional: false, reloadOnChange: false)
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{ApplicationUpdateOptions.SectionName}:SourceBaseAddress"] =
                        synchronized.SourceBaseAddress,
                }
            )
            .Build();
    }

    public static void RegisterCommonServices(
        IServiceCollection collection,
        IConfiguration? suppliedConfiguration = null,
        SerialLog? applicationLogger = null
    )
    {
        var configuration = suppliedConfiguration ?? BuildConfiguration();
        collection.AddSingleton<IConfiguration>(configuration);
        collection
            .AddOptions<ApplicationBehaviorOptions>()
            .Bind(configuration.GetSection(ApplicationBehaviorOptions.SectionName));
        var displaySection = configuration.GetSection(DisplayOptions.SectionName);
        collection
            .AddOptions<DisplayOptions>()
            .Configure(options =>
            {
                var configuredPreset = displaySection[nameof(DisplayOptions.FontSizePreset)];
                options.FontSizePreset =
                    Enum.TryParse<FontSizePreset>(
                        configuredPreset,
                        ignoreCase: true,
                        out var preset
                    ) && Enum.IsDefined(preset)
                        ? preset
                        : FontSizePreset.Standard;
                var configuredWaveformStrokeThickness = displaySection[
                    nameof(DisplayOptions.WaveformStrokeThickness)
                ];
                options.WaveformStrokeThickness = double.TryParse(
                    configuredWaveformStrokeThickness,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var waveformStrokeThickness
                )
                    ? DisplayOptions.NormalizeWaveformStrokeThickness(waveformStrokeThickness)
                    : DisplayOptions.DefaultWaveformStrokeThickness;
            });
        collection
            .AddOptions<ApplicationUpdateOptions>()
            .Bind(configuration.GetSection(ApplicationUpdateOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "ApplicationUpdate SourceBaseAddress must be an absolute HTTP/HTTPS address without a query or fragment when startup update checks are enabled."
            )
            .ValidateOnStart();
        collection.AddSingleton<IApplicationVersionProvider, ApplicationVersionProvider>();
        collection.AddSingleton<IApplicationUpdateClient, VelopackApplicationUpdateClient>();
        collection.AddSingleton<ApplicationUpdateCoordinator>();
        collection.AddSingleton(TimeProvider.System);
        collection.AddSingleton<
            IApplicationConfigurationResetService,
            ApplicationConfigurationResetService
        >();
        collection.AddSingleton<IApplicationRestartService, ApplicationRestartService>();
        collection.AddSingleton<IFontScaleService, FontScaleService>();
        collection.AddSingleton<IDeviceBackendModeToggleService, DeviceBackendModeToggleService>();
        collection.AddDbContextFactory<AppDbContext>(options =>
            options
                .UseSqlite(
                    $"Data Source={AppPaths.DatabasePath};Foreign Keys=True;Default Timeout=5"
                )
                .AddInterceptors(SqlitePragmaConnectionInterceptor.Instance)
        );
        collection.AddSingleton<ApplicationSessionState>();
        collection.AddSingleton<ICurrentOperatorContext, CurrentOperatorContext>();
        collection.AddSingleton<IAuthenticationService, AuthenticationService>();
        collection.AddSingleton<IExperimentRecoveryService, ExperimentRecoveryService>();
        collection.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        collection.AddSingleton<ISubjectLookupService, SubjectLookupService>();
        collection.AddSingleton<IExperimentPersistenceService, ExperimentPersistenceService>();
        collection.AddSingleton<
            IExperimentConfigurationTemplateService,
            ExperimentConfigurationTemplateService
        >();
        collection.AddSingleton<
            IExperimentConfigurationTransferContext,
            ExperimentConfigurationTransferContext
        >();
        collection.AddSingleton<
            IExperimentRunPersistenceCoordinator,
            ExperimentRunPersistenceCoordinator
        >();
        collection.AddSingleton<IActiveExperimentSession>(services =>
            services.GetRequiredService<IExperimentRunPersistenceCoordinator>()
        );
        collection.AddSingleton<ITemporaryEegCleanupService, TemporaryEegCleanupService>();
        collection.AddSingleton<IManagedEegMigrationService, ManagedEegMigrationService>();
        collection.AddSingleton<IApplicationShutdownCoordinator, ApplicationShutdownCoordinator>();
        collection.AddSingleton<IFileRevealService, WindowsFileRevealService>();
        collection
            .AddOptions<DeviceHeartbeatOptions>()
            .Bind(configuration.GetSection(DeviceHeartbeatOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "DeviceHeartbeat times must be positive and both Interval and ResponseTimeout must be less than DisconnectTimeout."
            )
            .ValidateOnStart();
        collection
            .AddOptions<DeviceAutoConnectionOptions>()
            .Bind(configuration.GetSection(DeviceAutoConnectionOptions.SectionName))
            .ValidateOnStart();
        collection.AddSingleton<
            IValidateOptions<DeviceAutoConnectionOptions>,
            DeviceAutoConnectionOptionsValidator
        >();
        collection.AddSingleton(services =>
            services.GetRequiredService<IOptions<DeviceAutoConnectionOptions>>().Value
        );
        collection
            .AddOptions<SerialLogOptions>()
            .Bind(configuration.GetSection(SerialLogOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "SerialLog requires a positive MaxFileSizeBytes, RetentionDays in 1..36500, and a valid MinimumLevel."
            )
            .ValidateOnStart();
        collection
            .AddOptions<ImpedanceDetectionOptions>()
            .Bind(configuration.GetSection(ImpedanceDetectionOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "ImpedanceDetection AutoStopWhenNotPassedTimeout must be positive."
            )
            .ValidateOnStart();
        collection
            .AddOptions<ExperimentRunTimingOptions>()
            .Bind(configuration.GetSection(ExperimentRunTimingOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "ExperimentRunTiming requires positive aligned duration limits, durations within those limits and aligned to the step, and WaveformRefreshRateFps between 1 and 120."
            )
            .ValidateOnStart();
        collection
            .AddOptions<EegAcquisitionOptions>()
            .Bind(configuration.GetSection(EegAcquisitionOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "EegAcquisition requires a 250/500 Hz sample rate, positive timeouts, and a packet reorder window between 1 and 127."
            )
            .ValidateOnStart();
        collection
            .AddOptions<StimulationRunOptions>()
            .Bind(configuration.GetSection(StimulationRunOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "StimulationRun ProgressPacketTimeout and CompletionEventGracePeriod must be positive."
            )
            .ValidateOnStart();
        collection
            .AddOptions<DeviceBackendOptions>()
            .Bind(configuration.GetSection(DeviceBackendOptions.SectionName))
            .ValidateOnStart();
        collection.AddSingleton<
            IValidateOptions<DeviceBackendOptions>,
            DeviceBackendOptionsValidator
        >();
        collection.AddSingleton(services =>
            services.GetRequiredService<IOptions<DeviceBackendOptions>>().Value
        );
        collection.AddSingleton<IDeviceCapabilityAvailability, DeviceCapabilityAvailability>();
        collection
            .AddOptions<ElectrodePositionOptions>()
            .Bind(configuration.GetSection(ElectrodePositionOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "ElectrodePositions must contain unique, named positions with valid reference coordinates and default roles."
            )
            .ValidateOnStart();
        collection
            .AddOptions<StimulationCurrentOptions>()
            .Bind(configuration.GetSection(StimulationCurrentOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "StimulationCurrent must stay within raw protocol range 4-200 and use an aligned positive step divisible by 4."
            )
            .ValidateOnStart();
        var configuredElectrodeIds =
            configuration
                .GetSection(ElectrodePositionOptions.SectionName)
                .Get<ElectrodePositionOptions>()
                ?.Positions?.Select(position => position.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
        var stimulationChannels = configuration.GetSection(StimulationChannelOptions.SectionName);
        collection
            .AddOptions<StimulationChannelOptions>()
            .Configure(options =>
            {
                stimulationChannels.Bind(options);

                // ConfigurationBinder appends JSON arrays to initialized array defaults.
                // Replace them with the configured values so defaults are not duplicated.
                if (
                    stimulationChannels
                        .GetSection(nameof(StimulationChannelOptions.FixedActivePhysicalChannelIds))
                        .Get<int[]>() is
                    { } fixedChannels
                )
                {
                    options.FixedActivePhysicalChannelIds = fixedChannels;
                }
                if (
                    stimulationChannels
                        .GetSection(nameof(StimulationChannelOptions.AllowedElectrodeSiteIds))
                        .Get<string[]>() is
                    { } allowedSites
                )
                {
                    options.AllowedElectrodeSiteIds = allowedSites;
                }
            })
            .Validate(
                options => options.IsValid(),
                "StimulationChannels must define unique physical channels, valid selectable counts, and unique electrode sites."
            )
            .Validate(
                options =>
                    (options.AllowedElectrodeSiteIds ?? []).All(configuredElectrodeIds.Contains),
                "Every StimulationChannels allowed electrode site must exist in ElectrodePositions."
            )
            .ValidateOnStart();
        collection
            .AddOptions<V101RequestTimeoutOptions>()
            .Bind(configuration.GetSection(V101RequestTimeoutOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "Every V1.0.1 request-response timeout must be positive."
            )
            .ValidateOnStart();
        collection
            .AddOptions<V101ProtocolBehaviorOptions>()
            .Bind(configuration.GetSection(V101ProtocolBehaviorOptions.SectionName));
        if (applicationLogger is null)
        {
            collection.AddSingleton<SerialLog>();
            collection.AddSingleton<IApplicationLogger>(services =>
                services.GetRequiredService<SerialLog>()
            );
            collection.AddSingleton<IGlobalExceptionLogger>(services =>
                services.GetRequiredService<SerialLog>()
            );
        }
        else
        {
            // Instance registrations are externally owned and survive container disposal.
            collection.AddSingleton(applicationLogger);
            collection.AddSingleton<IApplicationLogger>(applicationLogger);
            collection.AddSingleton<IGlobalExceptionLogger>(applicationLogger);
        }
        collection.AddSingleton(services => new ByteTrafficLogDispatcher(
            services.GetRequiredService<SerialLog>()
        ));
        collection.AddSingleton<IByteTrafficLogger>(services =>
            services.GetRequiredService<ByteTrafficLogDispatcher>()
        );
        collection.AddSingleton<CommunicationDebugAssistantController>();
        collection.AddSingleton<IDeviceHeartbeatPauseService, DeviceHeartbeatPauseService>();
        collection.AddSingleton<MainViewModel>();
        collection.AddSingleton<INavigationRouter>(services =>
            services.GetRequiredService<MainViewModel>()
        );
        collection.AddSingleton<IDialogProvider>(services =>
            services.GetRequiredService<MainViewModel>()
        );
        collection.AddSingleton<DialogService>();
        collection.AddSingleton<
            IExperimentRunErrorDialogService,
            ExperimentRunErrorDialogService
        >();
        collection.AddSingleton<
            ISingleStimulusExperimentDialogService,
            SingleStimulusExperimentDialogService
        >();
        collection.AddSingleton<IGlobalExceptionLogger, CrashServiceExceptionLogger>();
        collection.AddSingleton<GlobalExceptionHandler>();
        collection.AddSingleton<IElectrodePositionCatalog>(services => new ElectrodePositionCatalog(
            services.GetRequiredService<IOptions<ElectrodePositionOptions>>().Value
        ));
        collection.AddSingleton(services => new StimulationCurrentPolicy(
            services.GetRequiredService<IOptions<StimulationCurrentOptions>>().Value
        ));
        collection.AddSingleton(services =>
            StimulusCapabilityProfile.FromOptions(
                services.GetRequiredService<IOptions<StimulationChannelOptions>>().Value,
                services.GetRequiredService<StimulationCurrentPolicy>()
            )
        );
        collection.AddSingleton<
            IEegPhysicalChannelMappingService,
            EegPhysicalChannelMappingService
        >();
        collection.AddSingleton<ExperimentPackageSerializer>();
        collection.AddSingleton<SimulationGenerationService>();
        collection.AddSingleton<SimulationGeneratorController>();
        collection.AddSingleton<IExperimentPackageSerializer>(services =>
            services.GetRequiredService<ExperimentPackageSerializer>()
        );
        collection.AddSingleton<IExperimentPackageValidator>(services =>
            services.GetRequiredService<ExperimentPackageSerializer>()
        );
        collection.AddSingleton<IEegArtifactFinalizer, EegArtifactFinalizer>();
        collection.AddSingleton<IEegFileRegistry, EegFileRegistry>();
        collection.AddSingleton<IEegExportService, EegExportService>();
        collection.AddSingleton<IEegBatchExportService, EegBatchExportService>();
        collection.AddSingleton(services => new FileEegRawPacketStore(
            AppPaths.RecordingsDirectory,
            fileRegistry: services.GetRequiredService<IEegFileRegistry>()
        ));
        collection.AddSingleton<IEegRawRecordingStore>(services =>
            services.GetRequiredService<FileEegRawPacketStore>()
        );
        collection.AddSingleton<IEegRawPacketRecorder>(services =>
            services.GetRequiredService<FileEegRawPacketStore>()
        );
        collection.AddSingleton<IEegRawPacketReader>(services =>
            services.GetRequiredService<FileEegRawPacketStore>()
        );
        collection.AddSingleton<
            IExperimentHistoryDeletionService,
            ExperimentHistoryDeletionService
        >();
        collection.AddSingleton<IEegHistoryWindowProvider, EegHistoryWindowProvider>();
        collection.AddSingleton<IDeviceSelectionContext, DeviceSelectionContext>();
        collection.AddSingleton<IDeviceConnectionNetworkContext>(
            services => new DeviceConnectionNetworkContext(
                GetSetting("EGGTCS_UDP_BROADCAST_ADDRESS", "255.255.255.255"),
                GetSetting("EGGTCS_UDP_LOCAL_ADDRESS", "0.0.0.0"),
                GetIntSetting(
                    "EGGTCS_UDP_DEVICE_PORT",
                    services
                        .GetRequiredService<IOptions<DeviceAutoConnectionOptions>>()
                        .Value.DefaultDevicePort
                ),
                GetIntSetting("EGGTCS_UDP_CALLBACK_PORT", 30302)
            )
        );
        collection.AddSingleton<ConfigurableDeviceBackend>(CreateDeviceBackend);
        collection.AddSingleton<EGGtCSPlatform.DeviceRuntime.IDeviceRuntime>(services =>
            services.GetRequiredService<ConfigurableDeviceBackend>().Runtime
        );
        collection.AddSingleton<IEnvelopeTrainingExecutionService, EnvelopeTrainingExecutionService>();
        collection.AddSingleton<IEnvelopeTrainingResultStore>(new EnvelopeTrainingResultStore());
        collection.AddSingleton<IDeviceManager>(services =>
            services.GetRequiredService<ConfigurableDeviceBackend>().DeviceManager
        );
        collection.AddSingleton<ILocalNetworkDiscoveryResolver, LocalNetworkDiscoveryResolver>();
        collection.AddSingleton<IDeviceConnectionProfileStore, DeviceConnectionProfileStore>();
        collection.AddSingleton<IDeviceConnectionFaultNotifier, DeviceConnectionFaultNotifier>();
        collection.AddSingleton<IConfiguredDeviceDiscovery>(
            services => new ConfiguredDeviceDiscovery(
                services.GetRequiredService<IOptions<DeviceAutoConnectionOptions>>().Value,
                services.GetRequiredService<IDeviceConnectionNetworkContext>(),
                services.GetRequiredService<ILocalNetworkDiscoveryResolver>(),
                services.GetRequiredService<IByteTrafficLogger>(),
                () => services.GetRequiredService<IDeviceManager>(),
                services.GetRequiredService<DeviceBackendOptions>(),
                services.GetRequiredService<ConfigurableDeviceBackend>().ProtocolModule
            )
        );
        collection.AddSingleton<DeviceConnectionCoordinator>();
        collection.AddSingleton<IDeviceConnectionCoordinator>(services =>
            services.GetRequiredService<DeviceConnectionCoordinator>()
        );
        collection.AddSingleton<IExperimentRunService, DeviceExperimentRunService>();
        collection.AddSingleton<IImpedanceDetectionService, DeviceImpedanceDetectionService>();
        collection.AddSingleton<IExperimentRunClock, SystemExperimentRunClock>();
        collection.AddTransient<LoginViewModel>();

        collection.AddTransient<StartExperimentPageViewModel>();
        collection.AddTransient<DeviceConnectionPageViewModel>();
        collection.AddTransient<ToleranceTestPageViewModel>();
        collection.AddTransient<PhysicalChannelMappingPageViewModel>();

        collection.AddSingleton<Func<ApplicationPageNames, object?, PageViewModel>>(services =>
            (route, routeData) =>
                route switch
                {
                    ApplicationPageNames.StartExperiment => CreateStartExperimentPageViewModel(
                        services,
                        routeData
                    ),
                    ApplicationPageNames.DeviceConnection =>
                        services.GetRequiredService<DeviceConnectionPageViewModel>(),
                    ApplicationPageNames.ToleranceTest =>
                        services.GetRequiredService<ToleranceTestPageViewModel>(),
                    ApplicationPageNames.StimulusConfiguration =>
                        new StimulusConfigurationPageViewModel(
                            routeData as StimulusConfigurationRouteData
                                ?? new StimulusConfigurationRouteData("未填写", "未填写"),
                            services.GetRequiredService<INavigationRouter>(),
                            services.GetRequiredService<DialogService>(),
                            services.GetRequiredService<IDialogProvider>(),
                            services.GetRequiredService<StimulusCapabilityProfile>(),
                            services.GetRequiredService<IDeviceSelectionContext>(),
                            services
                                .GetRequiredService<IOptions<ApplicationBehaviorOptions>>()
                                .Value
                        ),
                    ApplicationPageNames.ElectrodeConfiguration =>
                        new ElectrodeConfigurationPageViewModel(
                            routeData as ElectrodeConfigurationRouteData
                                ?? ElectrodeConfigurationRouteDataDefaults.Create(),
                            services.GetRequiredService<INavigationRouter>(),
                            services.GetRequiredService<DialogService>(),
                            services.GetRequiredService<IDialogProvider>(),
                            services.GetRequiredService<StimulusCapabilityProfile>(),
                            services.GetRequiredService<IImpedanceDetectionService>(),
                            services
                                .GetRequiredService<IOptions<ImpedanceDetectionOptions>>()
                                .Value,
                            services.GetRequiredService<IElectrodePositionCatalog>(),
                            services.GetRequiredService<IOptions<EegAcquisitionOptions>>().Value,
                            services.GetRequiredService<IExperimentPersistenceService>(),
                            services.GetRequiredService<IDeviceCapabilityAvailability>(),
                            services.GetRequiredService<IEegPhysicalChannelMappingService>(),
                            services.GetRequiredService<ISingleStimulusExperimentDialogService>()
                        ),
                    ApplicationPageNames.PhysicalChannelMapping =>
                        services.GetRequiredService<PhysicalChannelMappingPageViewModel>(),
                    ApplicationPageNames.ExperimentRun => new ExperimentRunPageViewModel(
                        routeData as ExperimentRunRouteData
                            ?? ExperimentRunRouteDataDefaults.Create(),
                        services.GetRequiredService<INavigationRouter>(),
                        services.GetRequiredService<IExperimentRunService>(),
                        services.GetRequiredService<IExperimentRunClock>(),
                        services.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value,
                        services.GetRequiredService<IExperimentRunErrorDialogService>(),
                        services.GetRequiredService<IEegHistoryWindowProvider>(),
                        services.GetRequiredService<IEegPhysicalChannelMappingService>(),
                        services.GetRequiredService<IExperimentRunPersistenceCoordinator>(),
                        services.GetRequiredService<IEegExportService>(),
                        services.GetRequiredService<DialogService>(),
                        services.GetRequiredService<IDeviceSelectionContext>(),
                        services.GetRequiredService<IOptions<DisplayOptions>>().Value
                    ),
                    ApplicationPageNames.EnvelopeStimulation =>
                        new EnvelopeStimulationPageViewModel(
                            routeData as EnvelopeStimulationRouteData
                                ?? throw new ArgumentException(
                                    "包络-tACS 配置不可用。",
                                    nameof(routeData)
                                ),
                            services.GetRequiredService<INavigationRouter>(),
                            services.GetRequiredService<IEnvelopeTrainingExecutionService>(),
                            services.GetRequiredService<IEnvelopeTrainingResultStore>()
                        ),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(route),
                        route,
                        "No page is registered for this route."
                    ),
                }
        );
        collection.AddSingleton<PageFactory>();

        collection.AddTransient<ConfirmDialogViewModel>();
        collection.AddTransient<ErrorViewModel>();

        collection.AddTopLevelProvider();
    }

    public static void ValidateConfiguration(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ = services.GetRequiredService<IOptions<ApplicationBehaviorOptions>>().Value;
        _ = services.GetRequiredService<IOptions<ApplicationUpdateOptions>>().Value;
        _ = services.GetRequiredService<IOptions<DeviceHeartbeatOptions>>().Value;
        _ = services.GetRequiredService<IOptions<DeviceAutoConnectionOptions>>().Value;
        _ = services.GetRequiredService<IOptions<SerialLogOptions>>().Value;
        _ = services.GetRequiredService<IOptions<ImpedanceDetectionOptions>>().Value;
        _ = services.GetRequiredService<IOptions<ExperimentRunTimingOptions>>().Value;
        _ = services.GetRequiredService<IOptions<EegAcquisitionOptions>>().Value;
        _ = services.GetRequiredService<IOptions<StimulationRunOptions>>().Value;
        _ = services.GetRequiredService<IOptions<DeviceBackendOptions>>().Value;
        _ = services.GetRequiredService<IOptions<ElectrodePositionOptions>>().Value;
        _ = services.GetRequiredService<IOptions<StimulationCurrentOptions>>().Value;
        _ = services.GetRequiredService<IOptions<StimulationChannelOptions>>().Value;
        _ = services.GetRequiredService<IOptions<V101RequestTimeoutOptions>>().Value;
        _ = services.GetRequiredService<IOptions<V101ProtocolBehaviorOptions>>().Value;

        var mappings = services.GetRequiredService<IEegPhysicalChannelMappingService>().Load();
        if (!mappings.IsValid)
        {
            throw new ApplicationConfigurationException(
                services.GetRequiredService<IEegPhysicalChannelMappingService>().StoragePath,
                string.Join("；", mappings.ValidationErrors)
            );
        }
    }

    private static StartExperimentPageViewModel CreateStartExperimentPageViewModel(
        IServiceProvider services,
        object? routeData
    )
    {
        var viewModel = services.GetRequiredService<StartExperimentPageViewModel>();
        viewModel.ApplyRoute(
            routeData as StartExperimentRouteData ?? StartExperimentRouteData.NewExperiment
        );
        return viewModel;
    }

    private static ConfigurableDeviceBackend CreateDeviceBackend(IServiceProvider services)
    {
        var networkContext = services.GetRequiredService<IDeviceConnectionNetworkContext>();
        var heartbeatPause = services.GetRequiredService<IDeviceHeartbeatPauseService>();
        var heartbeatOptions = services
            .GetRequiredService<IOptions<DeviceHeartbeatOptions>>()
            .Value;
        var protocolOptions = services
            .GetRequiredService<IOptions<V101RequestTimeoutOptions>>()
            .Value.CreateProtocolOptions(
                requireMatchingResponseIndex: services
                    .GetRequiredService<IOptions<V101ProtocolBehaviorOptions>>()
                    .Value.RequireMatchingResponseIndex
            );
        var byteTrafficLogger = services.GetRequiredService<IByteTrafficLogger>();
        var selection = services.GetRequiredService<IDeviceSelectionContext>();
        var faultNotifier = services.GetRequiredService<IDeviceConnectionFaultNotifier>();
        IDeviceManager? manager = null;
        var backend = new ConfigurableDeviceBackend(
            services.GetRequiredService<DeviceBackendOptions>(),
            networkContext,
            heartbeatPause,
            heartbeatOptions,
            byteTrafficLogger,
            (identity, exception) =>
                ClearFaultedConnection(selection, faultNotifier, manager, identity, exception),
            protocolOptions,
            (device, status) => UpdateConnectedBattery(selection, manager, device, status)
        );
        manager = backend.DeviceManager;
        return backend;
    }

    private static void UpdateConnectedBattery(
        IDeviceSelectionContext selection,
        IDeviceManager? deviceManager,
        IEggtCsDevice sourceDevice,
        DeviceStatusResponse status
    )
    {
        void Update()
        {
            if (
                deviceManager is not null
                && deviceManager.TryGet(sourceDevice.Identity.DeviceId, out var currentDevice)
                && ReferenceEquals(currentDevice, sourceDevice)
                && selection.IsConnected
                && string.Equals(
                    selection.SelectedDeviceId,
                    sourceDevice.Identity.DeviceId.Value,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                selection.UpdateBattery(status.BatteryPercent);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Update();
        else
            Dispatcher.UIThread.Post(Update);
    }

    private static void ClearFaultedConnection(
        IDeviceSelectionContext selection,
        IDeviceConnectionFaultNotifier faultNotifier,
        IDeviceManager? manager,
        DeviceIdentity identity,
        Exception exception
    )
    {
        IEggtCsDevice? source = null;
        manager?.TryGet(identity.DeviceId, out source);
        faultNotifier.Notify(identity, exception, source);

        void Clear()
        {
            if (
                manager?.TryGet(identity.DeviceId, out var current) == true
                && !ReferenceEquals(source, current)
            )
                return;
            if (
                string.Equals(
                    selection.SelectedDeviceId,
                    identity.DeviceId.Value,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                selection.ClearConnection("设备心跳超时，连接已断开，请重新连接");
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Clear();
        else
            Dispatcher.UIThread.Post(Clear);
    }

    private static string GetSetting(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private static int GetIntSetting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}
