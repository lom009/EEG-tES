using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using Avalonia.Threading;
using EGGtCSPlatform.Bootstrap;
using EGGtCSPlatform.Configuration;
using EGGtCSPlatform.Crash;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using EGGtCSPlatform.Views.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

[assembly: XmlnsDefinition("https://github.com/avaloniaui", "EGGtCSPlatform.Controls")]

namespace EGGtCSPlatform;

public partial class App : Application
{
    private ApplicationExitWorkflow? _exitWorkflow;
    internal bool IsExiting => _exitWorkflow?.IsStarted == true;
    private ServiceProvider? _services;
    private ApplicationSingleInstanceLease? _singleInstanceLease;
    private SerialLog? _logger;
    private int _exitCode;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Startup records use defaults; historical cleanup waits for the user's retention setting.
        _logger = new SerialLog(
            new SerialLogOptions(),
            SerialLog.DefaultLogDirectory,
            deferRetention: true
        );
        ApplicationLog.SetLogger(_logger);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        ApplicationLog.Write(
            ApplicationLogLevel.Info,
            "Application",
            "Application.Starting",
            $"version={new ApplicationVersionProvider().Version}; processId={Environment.ProcessId}"
        );
        try
        {
            CompleteInitialization();
        }
        catch (Exception exception)
        {
            ApplicationLog.Write(
                ApplicationLogLevel.Fatal,
                "Application",
                "Application.StartupFailed",
                "程序初始化失败",
                exception: exception
            );
            CrashService.SetCrashData(exception);
            ApplicationLog.FlushBeforeExitAsync().GetAwaiter().GetResult();
            throw;
        }
    }

    private void CompleteInitialization()
    {
        UserConfigurationFileSnapshot? configurationSnapshot = null;
        IConfiguration configuration;
        try
        {
            configurationSnapshot = UserConfigurationFileSnapshot.Capture(
                AppPaths.UserConfigurationPath
            );
            configuration = Bootstrapper.BuildConfiguration();
            SerialLogOptions logging;
            try
            {
                logging =
                    configuration.GetSection(SerialLogOptions.SectionName).Get<SerialLogOptions>()
                    ?? new();
            }
            catch (InvalidOperationException exception)
            {
                throw new ApplicationConfigurationException(
                    AppPaths.UserConfigurationPath,
                    "日志配置无效。",
                    exception
                );
            }
            if (!logging.IsValid())
                throw new ApplicationConfigurationException(
                    AppPaths.UserConfigurationPath,
                    "日志配置无效。"
                );
            _logger!.Configure(logging);
        }
        catch (ApplicationConfigurationException exception)
        {
            var startupException = configurationSnapshot is null
                ? exception
                : RestoreConfigurationAfterValidationFailure(configurationSnapshot, exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime errorDesktop)
            {
                ShowStartupConfigurationError(errorDesktop, startupException);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            throw startupException;
        }

        if (
            ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime
            && !CanContinueDesktopStartup(desktopLifetime, configuration)
        )
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var collection = new ServiceCollection();

        // Inject common services
        Bootstrapper.RegisterCommonServices(collection, configuration, _logger);

        ServiceProvider? services = null;
        try
        {
            services = collection.BuildServiceProvider();
            Bootstrapper.ValidateConfiguration(services);
        }
        catch (Exception exception)
            when (exception is ApplicationConfigurationException or OptionsValidationException)
        {
            services?.Dispose();
            var startupException = RestoreConfigurationAfterValidationFailure(
                configurationSnapshot!,
                exception
            );
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime errorDesktop)
            {
                ShowStartupConfigurationError(errorDesktop, startupException);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            throw startupException;
        }
        if (services is null)
            throw new InvalidOperationException("依赖注入容器创建失败。");
        _services = services;
        services.GetRequiredService<IFontScaleService>().Initialize();
        // Database initialization may yield while rebuilding a large EEG index. Running it
        // on the Avalonia UI synchronization context and synchronously waiting here deadlocks
        // as soon as that yield tries to resume on the blocked UI thread.
        // The application-instance lease makes it safe to remove EF Core's SQLite
        // migration lock table: no other application process can be migrating this database.
        var recoverStaleMigrationLock = _singleInstanceLease is not null;
        Task.Run(() =>
                services
                    .GetRequiredService<IDatabaseInitializer>()
                    .InitializeAsync(recoverStaleMigrationLock)
            )
            .GetAwaiter()
            .GetResult();
        Task.Run(() => services.GetRequiredService<ITemporaryEegCleanupService>().CleanupAsync())
            .GetAwaiter()
            .GetResult();
        services.GetRequiredService<GlobalExceptionHandler>().Attach();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _exitWorkflow = new ApplicationExitWorkflow(
                () =>
                    services
                        .GetRequiredService<IApplicationShutdownCoordinator>()
                        .ShutdownAsync("application-exit"),
                async () =>
                {
                    try
                    {
                        await DisposeServicesAsync(services);
                    }
                    finally
                    {
                        _services = null;
                    }
                },
                () =>
                {
                    _singleInstanceLease?.Dispose();
                    _singleInstanceLease = null;
                },
                () =>
                {
                    AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                    desktop.Shutdown(_exitCode);
                },
                exception =>
                    ApplicationLog.Write(
                        ApplicationLogLevel.Error,
                        "Application",
                        "Application.CleanupFailed",
                        "退出收尾失败",
                        exception: exception
                    ),
                ApplicationLog.StopAsync
            );
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_exitWorkflow.IsComplete)
                    return;
                e.Cancel = true;
                RequestExit();
            };
            var debugAssistant =
                services.GetRequiredService<CommunicationDebugAssistantController>();
            var applicationVersion = services.GetRequiredService<IApplicationVersionProvider>();
            var loginViewModel = services.GetRequiredService<LoginViewModel>();
            var loginWindow = new LoginWindow
            {
                DataContext = loginViewModel,
                Title = applicationVersion.CreateWindowTitle("登录"),
            };
            var updateCoordinator = services.GetRequiredService<ApplicationUpdateCoordinator>();
            loginWindow.Opened += async (_, _) =>
                await updateCoordinator.RunStartupCheckAsync(() => desktop.MainWindow);
            debugAssistant.Attach(loginWindow);

            loginWindow.Closing += OnMainWindowClosing;
            loginViewModel.ExitRequested += RequestExit;

            loginViewModel.LoginSucceeded += () =>
            {
                if (IsExiting)
                    return;
                var mainWindow = new MainWindow
                {
                    DataContext = services.GetRequiredService<MainViewModel>(),
                    Title = applicationVersion.CreateWindowTitle("EEG-tES"),
                };
                mainWindow.Closing += OnMainWindowClosing;
                debugAssistant.Attach(mainWindow);
                services.GetRequiredService<SimulationGeneratorController>().Attach(mainWindow);

                // 先显示并登记主窗口，再关闭唯一的登录窗口，避免应用退出。
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                services
                    .GetRequiredService<IDeviceConnectionCoordinator>()
                    .StartAsync()
                    .ObserveFault();
                loginWindow.Close();
            };

            desktop.MainWindow = loginWindow;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView()
            {
                DataContext = services.GetRequiredService<MainViewModel>(),
            };
            services.GetRequiredService<IDeviceConnectionCoordinator>().StartAsync().ObserveFault();
        }

        var applicationBehavior = services
            .GetRequiredService<IOptions<ApplicationBehaviorOptions>>()
            .Value;
        var lastCrash = CrashService.TakeCrashData();
        if (applicationBehavior.ShowPreviousCrashOnStartup && lastCrash is not null)
        {
            new ErrorWindow
            {
                DataContext = new ErrorViewModel
                {
                    Title = "上次运行异常记录",
                    Description =
                        $"上次异常时间：{lastCrash.CrashDate.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n"
                        + $"异常位置：{lastCrash.Source}\r\n\r\n"
                        + $"{lastCrash.ErrorMessage}\r\n\r\n"
                        + $"堆栈信息：\r\n{lastCrash.StackTrace}",
                },
            }.Show();
        }

        base.OnFrameworkInitializationCompleted();
        ApplicationLog.Write(
            ApplicationLogLevel.Info,
            "Application",
            "Application.Started",
            "程序初始化完成"
        );
    }

    internal bool RequestFatalExit()
    {
        if (_exitWorkflow is null)
            return false;
        _exitCode = 1;
        RequestExit();
        return true;
    }

    internal void RequestExit()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestExit);
            return;
        }
        if (_exitWorkflow is null || _exitWorkflow.IsStarted)
            return;
        ApplicationLog.Write(
            ApplicationLogLevel.Info,
            "Application",
            "Application.ExitRequested",
            "收到退出请求"
        );
        var task = _exitWorkflow.RunAsync();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows.ToArray())
            {
                window.IsEnabled = false;
                window.Title = "正在退出…";
            }
        }
        task.ObserveFault();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (
            ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || !ReferenceEquals(sender, desktop.MainWindow)
            || _exitWorkflow?.IsComplete == true
        )
            return;
        e.Cancel = true;
        RequestExit();
    }

    private static Exception RestoreConfigurationAfterValidationFailure(
        UserConfigurationFileSnapshot snapshot,
        Exception validationException
    )
    {
        try
        {
            snapshot.RestoreIfChanged();
            return validationException;
        }
        catch (ApplicationConfigurationException restoreException)
        {
            return new ApplicationConfigurationException(
                snapshot.Path,
                $"{validationException.Message}\r\n\r\n{restoreException.Message}",
                new AggregateException(validationException, restoreException)
            );
        }
    }

    private void ShowStartupConfigurationError(
        IClassicDesktopStyleApplicationLifetime desktop,
        Exception exception
    )
    {
        var configurationPath = exception
            is ApplicationConfigurationException configurationException
            ? configurationException.ConfigurationPath
            : AppPaths.UserConfigurationPath;
        ApplicationLog.Write(
            ApplicationLogLevel.Fatal,
            "Application",
            "Application.StartupFailed",
            "配置校验失败",
            exception: exception
        );
        ApplicationLog.FlushBeforeExitAsync().GetAwaiter().GetResult();
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new ErrorWindow
        {
            DataContext = new ErrorViewModel
            {
                Title = "配置文件无效",
                Description = $"配置文件：{configurationPath}\r\n\r\n{exception.Message}",
                ConfirmText = "确认并退出",
            },
        };
        window.Closed += async (_, _) =>
        {
            _services?.Dispose();
            _services = null;
            _singleInstanceLease?.Dispose();
            _singleInstanceLease = null;
            await ApplicationLog.StopAsync();
            desktop.Shutdown(1);
        };
        desktop.MainWindow = window;
        window.Show();
        window.Activate();
    }

    private bool CanContinueDesktopStartup(
        IClassicDesktopStyleApplicationLifetime desktop,
        IConfiguration configuration
    )
    {
        var options =
            configuration
                .GetSection(ApplicationBehaviorOptions.SectionName)
                .Get<ApplicationBehaviorOptions>()
            ?? new ApplicationBehaviorOptions();
        if (!options.SingleInstance)
            return true;

        try
        {
            if (ApplicationSingleInstanceLease.TryAcquireCurrentUser(out var lease))
            {
                _singleInstanceLease = lease;
                return true;
            }

            ApplicationLog.Write(
                ApplicationLogLevel.Warning,
                "Application",
                "Application.DuplicateInstance",
                "已有运行实例，本次启动取消"
            );
            ShowSingleInstanceMessage(desktop, "程序已在运行，请勿重复启动。");
        }
        catch (System.Exception exception)
        {
            ShowSingleInstanceMessage(
                desktop,
                $"无法启用应用单例保护，程序将退出。\n{exception.Message}"
            );
        }

        return false;
    }

    private static void ShowSingleInstanceMessage(
        IClassicDesktopStyleApplicationLifetime desktop,
        string message
    )
    {
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new SingleInstanceWindow(message);
        desktop.MainWindow = window;
        window.Show();
    }

    internal static async Task DisposeServicesAsync(
        ServiceProvider services,
        IApplicationLogger? logger = null
    )
    {
        var log = logger ?? ApplicationLog.Current;
        var selection = services.GetService<IDeviceSelectionContext>();
        var deviceId = selection?.IsConnected == true ? selection.SelectedDeviceId : null;
        if (deviceId is not null)
            log.Write(
                ApplicationLogLevel.Info,
                "Application",
                "Device.DisconnectRequested",
                "退出时释放设备连接",
                deviceId
            );
        // DI also owns the manager/runtime aliases; observe their disposal as a whole.
        try
        {
            await services.DisposeAsync();
            if (deviceId is not null)
                log.Write(
                    ApplicationLogLevel.Info,
                    "Application",
                    "Device.Disconnected",
                    "设备服务已释放",
                    deviceId
                );
        }
        catch (Exception exception)
        {
            if (deviceId is not null)
                log.Write(
                    ApplicationLogLevel.Error,
                    "Application",
                    "Device.DisconnectFailed",
                    "退出时服务容器释放失败，设备释放结果未确认",
                    deviceId,
                    exception
                );
            throw;
        }
    }

    private void OnProcessExit(object? sender, System.EventArgs e)
    {
        if (IsExiting)
            return;
        try
        {
            var services = _services;
            Task.Run(async () =>
                {
                    if (services is not null)
                    {
                        await services
                            .GetRequiredService<IApplicationShutdownCoordinator>()
                            .ShutdownAsync("process-exit");
                        await DisposeServicesAsync(services);
                    }
                })
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            ApplicationLog.Write(
                ApplicationLogLevel.Error,
                "Application",
                "Application.CleanupFailed",
                "进程退出收尾失败",
                exception: exception
            );
        }
        ApplicationLog.StopAsync().GetAwaiter().GetResult();
    }
}
