using System;
using Avalonia;
using EGGtCSPlatform.Services;
using Velopack;

namespace EGGtCSPlatform.Desktop;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (
            ApplicationRestartProtocol.TryTakeParentProcessId(
                args,
                out var parentProcessId,
                out var remainingArguments
            )
        )
        {
            if (
                !ApplicationRestartProtocol.WaitForParentExit(
                    parentProcessId,
                    ApplicationRestartProtocol.ParentExitTimeout
                )
            )
            {
                Environment.ExitCode = 2;
                return;
            }
            args = remainingArguments;
        }

        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
