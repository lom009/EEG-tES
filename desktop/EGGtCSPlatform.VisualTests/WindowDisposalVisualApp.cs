using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EGGtCSPlatform.VisualTests;

// Run with --scenario=window-disposal. Uses actual Avalonia windows and DI disposal.
public sealed class WindowDisposalVisualApp : App
{
    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(output);
        var error = Path.Combine(output, "WindowDisposal.error.txt");
        var result = Path.Combine(output, "WindowDisposal.passed.txt");
        File.Delete(error);
        File.Delete(result);
        try
        {
            foreach (
                var mode in new[]
                {
                    "never-opened",
                    "debug",
                    "simulation",
                    "both",
                    "closed",
                    "reopened",
                    "reentrant",
                }
            )
                await RunScenario(desktop, mode).WaitAsync(VisualTestOptions.Current.Timeout);
            File.WriteAllText(
                result,
                "PASS: seven real-window DI disposal scenarios, including forced asynchronous continuation."
            );
            desktop.Shutdown(0);
        }
        catch (Exception exception)
        {
            File.WriteAllText(error, exception.ToString());
            Environment.ExitCode = 1;
            desktop.Shutdown(1);
        }
    }

    private static async Task RunScenario(
        IClassicDesktopStyleApplicationLifetime desktop,
        string mode
    )
    {
        var current = new CurrentOperatorContext();
        current.Set(1, "disposal-test");
        var mapping = new Mapping();
        var capability = StimulusCapabilityProfile.Default;
        var catalog = new ElectrodePositionCatalog(
            new ElectrodePositionOptions
            {
                Positions = capability
                    .StimulusSiteIds.Concat(new[] { "C3", "C4", "FCz", "AFz" })
                    .Distinct()
                    .Select(x => new ElectrodePositionDefinition(x, x, 100, 100))
                    .ToArray(),
            }
        );
        var generator = new SimulationGenerationService(
            new Factory(),
            current,
            new ApplicationSessionState(),
            mapping,
            new ExperimentPackageSerializer(
                capability,
                catalog,
                mapping,
                new ApplicationVersionProvider()
            )
        );
        var main = new MainViewModel(
            new PageFactory((_, _) => throw new NotSupportedException()),
            new DeviceSelectionContext()
        );
        var services = new ServiceCollection()
            .AddSingleton(_ => new ByteTrafficLogDispatcher(NullByteTrafficLogger.Instance))
            .AddSingleton<CommunicationDebugAssistantController>()
            .AddSingleton(_ => new SimulationGeneratorController(
                generator,
                current,
                mapping,
                capability,
                main
            ))
            .AddSingleton<AsyncBarrier>()
            .BuildServiceProvider();
        var debug = services.GetRequiredService<CommunicationDebugAssistantController>();
        var simulation = services.GetRequiredService<SimulationGeneratorController>();
        var barrier = services.GetRequiredService<AsyncBarrier>(); // Last captured, first disposed.
        var host = new Window { Title = "Window disposal regression: " + mode };
        desktop.MainWindow = host;
        host.Show();
        debug.Attach(host);
        debug.Attach(host); // Repeated attachment must not duplicate callbacks.
        simulation.Attach(host);
        simulation.Attach(host);
        void OpenWindows()
        {
            if (mode != "simulation")
                debug.Open();
            if (mode != "debug")
                Gesture(host, Key.G);
        }
        if (mode != "never-opened")
            OpenWindows();
        await Task.Delay(30); // Let loaded callbacks run before closing.
        if (mode is "closed" or "reopened")
        {
            foreach (var window in desktop.Windows.Where(x => x != host).ToArray())
                window.Close();
            if (mode == "reopened")
                OpenWindows();
        }
        var windows = desktop.Windows.Where(x => x != host).ToArray();
        var expected =
            mode is "never-opened" or "closed" ? 0
            : mode is "debug" or "simulation" ? 1
            : 2;
        Check(windows.Length == expected, mode + ": expected auxiliary windows");
        var closed = 0;
        foreach (var window in windows)
            window.Closed += (_, _) =>
            {
                Check(Dispatcher.UIThread.CheckAccess(), "Closed must run on UI thread");
                closed++;
                if (mode == "reentrant")
                {
                    if (window is CommunicationDebugWindow)
                        debug.Dispose();
                    else
                        simulation.Dispose();
                }
            };
        var disposal = services.DisposeAsync().AsTask();
        Check(
            barrier.Started && !disposal.IsCompleted,
            "DI must suspend before controller disposal"
        );
        await Task.Run(() => barrier.Release.TrySetResult());
        await disposal;
        Check(
            closed == expected && windows.All(x => !x.IsVisible),
            mode + ": disposal returned before windows closed"
        );
        await Task.Run(async () =>
        {
            await debug.DisposeAsync();
            await simulation.DisposeAsync();
        });
        debug.Dispose();
        simulation.Dispose();
        Gesture(host, Key.D);
        Gesture(host, Key.G);
        Check(desktop.Windows.All(x => x == host), "Disposed shortcut handlers must be detached");
        host.Close();
        await generator.DisposeAsync();
    }

    private static void Gesture(Window host, Key key) =>
        host.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
            }
        );

    private static void Check(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private sealed class AsyncBarrier : IAsyncDisposable
    {
        public bool Started { get; private set; }
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            Started = true;
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            throw new NotSupportedException("This scenario does not write experiment data.");
    }

    private sealed class Mapping : IEegPhysicalChannelMappingService
    {
        public EegPhysicalChannelMappingSnapshot Load() =>
            new(32, [new("C3", 1), new("C4", 2)], []);

        public string StoragePath => "disposal-test";

        public void Save(int count, IReadOnlyList<EegPhysicalChannelMapping> values) =>
            throw new NotSupportedException();
    }
}
