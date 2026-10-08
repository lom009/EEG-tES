using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EGGtCSPlatform.VisualTests;

public sealed class SimulationGeneratorVisualApp : App
{
    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(output);
        File.Delete(Path.Combine(output, "SimulationGenerator.error.txt"));
        try
        {
            var directory = Path.Combine(output, "simulation-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var factory = new Factory(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(directory, "visual.db")};Pooling=False")
                    .Options
            );
            var current = new CurrentOperatorContext();
            var session = new ApplicationSessionState { Id = Guid.NewGuid() };
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                var op = new OperatorEntity
                {
                    Username = "visual",
                    NormalizedUsername = "VISUAL",
                    PasswordSalt = [],
                    PasswordHash = [],
                };
                db.Operators.Add(op);
                db.ApplicationSessions.Add(
                    new() { Id = session.Id, StartedAtUtc = DateTimeOffset.UtcNow }
                );
                await db.SaveChangesAsync();
                current.Set(op.Id, op.Username);
            }
            var mapping = new Mapping();
            var config = new ConfigurationBuilder()
                .AddJsonFile(AppPaths.DefaultConfigurationPath)
                .Build();
            var catalog = new ElectrodePositionCatalog(
                config.GetSection("ElectrodePositions").Get<ElectrodePositionOptions>()!
            );
            var capability = StimulusCapabilityProfile.Default;
            var serializer = new ExperimentPackageSerializer(
                capability,
                catalog,
                mapping,
                new ApplicationVersionProvider()
            );
            var generator = new SimulationGenerationService(
                factory,
                current,
                session,
                mapping,
                serializer,
                directory
            );
            var vm = new SimulationGeneratorViewModel(generator, mapping, capability);
            vm.ApplyRandomizedPresetCommand.Execute(null);
            var window = new SimulationGeneratorWindow
            {
                DataContext = vm,
                WindowDecorations = WindowDecorations.None,
            };
            desktop.MainWindow = window;
            window.Show();
            await Task.Delay(300);
            var parameterScroll = window
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .First(x => x.Content is StackPanel);
            vm.Frequency = 900;
            vm.RampSeconds = 59.5;
            foreach (
                var kind in new[]
                {
                    StimulusKind.TAcs,
                    StimulusKind.TDcs,
                    StimulusKind.TRns,
                    StimulusKind.TPcs,
                    StimulusKind.Sham,
                }
            )
            {
                vm.SelectedKind = vm.Kinds.Single(x => x.Kind == kind);
                await Task.Delay(120);
                if (vm.Frequency != 40 || vm.RampSeconds != 7)
                    throw new InvalidOperationException("范式切换污染了普通参数。");
                parameterScroll.Offset = new Vector(0, 550);
                await Task.Delay(80);
                Capture(
                    (Control)window.Content!,
                    Path.Combine(output, $"SimulationGenerator.{kind}.png")
                );
            }
            vm.SelectedKind = vm.Kinds.Single(x => x.Kind == StimulusKind.EnvelopeTAcs);
            await Task.Delay(120);
            if (vm.Frequency != 900 || vm.RampSeconds != 59.5)
                throw new InvalidOperationException("切回包络后参数未恢复。");
            vm.Frequency = 40;
            vm.RampSeconds = 7;
            parameterScroll.Offset = new Vector(0, 550);
            await Task.Delay(80);
            Capture(
                (Control)window.Content!,
                Path.Combine(output, "SimulationGenerator.parameters.png")
            );
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedIndex = 1;
            await Task.Delay(200);
            Capture(
                (Control)window.Content!,
                Path.Combine(output, "SimulationGenerator.electrodes.png")
            );
            window.Width = 920;
            window.Height = 650;
            await Task.Delay(150);
            Capture(
                (Control)window.Content!,
                Path.Combine(output, "SimulationGenerator.minimum.png")
            );
            vm.ValidateConfigurationCommand.Execute(null);
            if (!vm.Status.StartsWith("校验通过"))
                throw new InvalidOperationException(vm.Status);
            await vm.GenerateCommand.ExecuteAsync(null);
            if (!vm.Status.StartsWith("生成完成"))
                throw new InvalidOperationException(vm.Status);
            tabs.SelectedIndex = 2;
            await Task.Delay(150);
            Capture(
                (Control)window.Content!,
                Path.Combine(output, "SimulationGenerator.completed.png")
            );
            var history = new ExperimentPersistenceService(factory, current, mapping);
            var list = await history.ListHistoryAsync();
            if (list.Count != 32)
                throw new InvalidOperationException("32例未完整保存。");
            var route = await history.LoadHistoricalRunAsync(list.First().Runs.Single().RunId);
            await using var reader = new FileEegRawPacketStore(directory);
            using var historyVm = new StartExperimentPageViewModel(
                new VisualTestNavigationRouter(),
                history,
                new SubjectLookupService(factory),
                serializer,
                new DialogService(() => null),
                new DialogHost(),
                new DeviceSelectionContext(),
                new Cleanup(),
                reader,
                new ExperimentConfigurationTransferContext()
            );
            historyVm.ApplyRoute(StartExperimentRouteData.History);
            await historyVm.RefreshHistoryCommand.ExecuteAsync(null);
            var historyView = new BasicView
            {
                DataContext = new BasicViewModel(
                    new VisualTestNavigationRouter(),
                    historyVm,
                    new DeviceSelectionContext()
                ),
            };
            var historyWindow = new Window
            {
                Width = 1200,
                Height = 760,
                Content = historyView,
                WindowDecorations = WindowDecorations.None,
            };
            historyWindow.Show();
            await Task.Delay(200);
            Capture(historyView, Path.Combine(output, "SimulationGenerator.history.png"));
            historyWindow.Close();
            using var runVm = new ExperimentRunPageViewModel(
                route,
                new VisualTestNavigationRouter(),
                new VisualExperimentRunService(),
                historyWindowProvider: new EegHistoryWindowProvider(reader)
            );
            runVm.TimelineViewStart = 0;
            runVm.TimelineViewEnd = 37;
            var basic = new BasicView
            {
                DataContext = new BasicViewModel(
                    new VisualTestNavigationRouter(),
                    runVm,
                    new DeviceSelectionContext()
                ),
            };
            var playback = new Window
            {
                Width = 1440,
                Height = 900,
                Content = basic,
                WindowDecorations = WindowDecorations.None,
            };
            playback.Show();
            var deadline = DateTimeOffset.UtcNow + VisualTestOptions.Current.Timeout;
            while (!runVm.HasWaveformData && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(100);
            if (!runVm.HasWaveformData)
                throw new InvalidOperationException("生成数据回放未加载。");
            await Task.Delay(250);
            Capture(basic, Path.Combine(output, "SimulationGenerator.playback.png"));
            playback.Close();
            window.Close();
            desktop.Shutdown(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(output, "SimulationGenerator.error.txt"), e.ToString());
            desktop.Shutdown(1);
        }
    }

    private static void Capture(Control content, string path)
    {
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height),
            new Vector(96, 96)
        );
        bitmap.Render(content);
        using var stream = File.Create(path);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class Mapping : IEegPhysicalChannelMappingService
    {
        public EegPhysicalChannelMappingSnapshot Load() =>
            new(32, [new("C3", 1), new("C4", 2)], []);

        public string StoragePath => "visual";

        public void Save(int count, IReadOnlyList<EegPhysicalChannelMapping> values) =>
            throw new NotSupportedException();
    }

    private sealed class Cleanup : ITemporaryEegCleanupService
    {
        public Task CleanupAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class DialogHost : IDialogProvider
    {
        public System.Collections.ObjectModel.ObservableCollection<DialogViewModel> DialogStack { get; } =
        [];
    }
}
