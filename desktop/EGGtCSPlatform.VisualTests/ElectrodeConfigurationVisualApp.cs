using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Views.Pages;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class ElectrodeConfigurationVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var capability = StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions { FixedActivePhysicalChannelIds = [7] }
        );
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var page = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData(
                "EXP-20260707-001",
                "SUBJECT-20260707-001",
                stimulus.CreateSnapshot(),
                DeviceId.Simulator.Value
            ),
            new VisualTestNavigationRouter(),
            new DialogService(() => null),
            new VisualDialogProvider(),
            capability,
            new PreviewImpedanceDetectionService(),
            new ImpedanceDetectionOptions
            {
                AutoStopStimulationWhenPassed = false,
                AutoStopEegWhenPassed = false,
            }
        );


        var connection = new DeviceSelectionContext();
        connection.SetConnectedDevice(DeviceId.Simulator.Value, "EGG/tCS Simulator");
        var content = new BasicView
        {
            DataContext = new BasicViewModel(new VisualTestNavigationRouter(), page, connection),
        };
        var window = new Window
        {
            Title = "刺激电极分板块点位选择 - 自动截图验收",
            Width = 1440,
            Height = 900,
            MinWidth = 1200,
            MinHeight = 760,
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = content,
        };

        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                var outputDirectory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
                Directory.CreateDirectory(outputDirectory);
                File.Delete(VisualTestOptions.Current.ErrorPath);
                await Task.Delay(300);
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.empty.png"));
                var verify3DDetection = VisualTestOptions.Current.Scenario.EndsWith("3d-detection");
                if (verify3DDetection)
                    AssignRolePointsAndChannels(page);
                if (VisualTestOptions.Current.Scenario.Contains("3d"))
                {
                    var view = content.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
                    view.FindControl<RadioButton>("View3D")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var head = (ElectrodeHead3D)view.FindControl<ContentControl>("Head3DHost")!.Content!;
                    for (var i = 0; i < 150 && !head.IsSceneReady && head.LastError is null; i++) await Task.Delay(200);
                    if (!head.IsSceneReady) throw new InvalidOperationException("3D load: " + head.LastError);
                    await Task.Delay(500);
                    var web = (NativeWebView)head.Content!;
                    var transparentLayers = await web.InvokeScript("[document.documentElement,document.body,document.querySelector('#stage')].every(e=>getComputedStyle(e).backgroundColor==='rgba(0, 0, 0, 0)').toString()");
                    if (transparentLayers?.Trim('"') != "true")
                        throw new InvalidOperationException("3D page background is not transparent");
                    var count = await web.InvokeScript("document.querySelectorAll('#labels button').length.toString()");
                    if (count?.Trim('"') != "34") throw new InvalidOperationException("Expected 34 labels: " + count);
                    var unavailableHidden = await web.InvokeScript("[...document.querySelectorAll('#labels button')].find(b=>b.textContent==='F4').hidden.toString()");
                    var assignedVisible = await web.InvokeScript("[...document.querySelectorAll('#labels button')].find(b=>b.textContent.startsWith('AFz')).hidden.toString()");
                    if (unavailableHidden?.Trim('"') != "true" || assignedVisible?.Trim('"') != "false")
                        throw new InvalidOperationException("Stimulation label visibility is wrong");
                    if (verify3DDetection)
                    {
                        var monitored = page.Points.First(p => p.IsStimulus && p.StimulationChannelRole == StimulationChannelRole.Selectable);
                        var panelRow = content.GetVisualDescendants().OfType<ImpedanceStatusItem>()
                            .Single(item => ReferenceEquals(item.PointCommandParameter, monitored));
                        panelRow.PointCommand!.Execute(panelRow.PointCommandParameter);
                        await Task.Delay(400);
                        var current = await web.InvokeScript($"[...document.querySelectorAll('#labels button')].find(b=>b.textContent.startsWith('{monitored.PositionName}')).getAttribute('aria-current')");
                        if (current?.Trim('"') != "true" || !ReferenceEquals(page.SelectedPoint, monitored) || !panelRow.IsSelected)
                            throw new InvalidOperationException("Panel focus did not reach the 3D label and current-point summary.");
                        if (!page.CanToggleDetection) throw new InvalidOperationException("Stimulus detection is unavailable in the test setup");
                        await page.ToggleDetectionCommand.ExecuteAsync(null);
                        await Task.Delay(250);
                        if (!page.IsCurrentDetecting) throw new InvalidOperationException("Native detection did not start");
                        var animating = await web.InvokeScript($"window.isHead3DAnimating('{monitored.PositionName}').toString()");
                        var labelState = await web.InvokeScript($"[...document.querySelectorAll('#labels button')].find(b=>b.textContent.startsWith('{monitored.PositionName}')).dataset.state");
                        if (animating?.Trim('"') != "true" || labelState?.Trim('"') != "checking")
                            throw new InvalidOperationException("3D checking animation did not follow native detection");
                        await Task.Delay(750);
                        if (monitored.Impedance is null) throw new InvalidOperationException("Native impedance reading did not reach the page");
                        await page.ToggleDetectionCommand.ExecuteAsync(null);
                        await Task.Delay(250);
                        animating = await web.InvokeScript($"window.isHead3DAnimating('{monitored.PositionName}').toString()");
                        if (page.IsCurrentDetecting || animating?.Trim('"') != "false")
                            throw new InvalidOperationException("3D checking animation did not stop with native detection");
                        page.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
                        var acquisitionPoint = page.Points.Single(p => p.PositionName == "C3");
                        page.SelectPointCommand.Execute(acquisitionPoint);
                        if (!page.CanToggleDetection) throw new InvalidOperationException("Acquisition detection is unavailable in the test setup");
                        await page.ToggleDetectionCommand.ExecuteAsync(null);
                        await Task.Delay(250);
                        animating = await web.InvokeScript("window.isHead3DAnimating('C3').toString()");
                        if (!page.IsCurrentDetecting || animating?.Trim('"') != "true")
                            throw new InvalidOperationException("3D acquisition animation did not follow native detection");
                        await page.ToggleDetectionCommand.ExecuteAsync(null);
                        await Task.Delay(250);
                        animating = await web.InvokeScript("window.isHead3DAnimating('C3').toString()");
                        if (page.IsCurrentDetecting || animating?.Trim('"') != "false")
                            throw new InvalidOperationException("3D acquisition animation did not stop");
                        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "3d-detection-pass.txt"),
                            $"Native stimulation and acquisition detection started and stopped 3D ring, particle and label animations; stimulation impedance for {monitored.PositionName}={monitored.Impedance}.");
                        return;
                    }
                    await web.InvokeScript("[...document.querySelectorAll('#labels button')].find(b=>b.textContent==='FP2').click()");
                    await Task.Delay(500);
                    var point = page.Points.Single(p => p.PositionName == "FP2");
                    if (!point.IsStimulus || !ReferenceEquals(page.SelectedPoint, point)) throw new InvalidOperationException("3D selection did not reach page state and panel focus");
                    var label = await web.InvokeScript("[...document.querySelectorAll('#labels button')].find(b=>b.textContent.startsWith('FP2')).textContent");
                    view.FindControl<RadioButton>("View2D")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!point.IsStimulus) throw new InvalidOperationException("Switch to 2D lost assignment");
                    view.FindControl<RadioButton>("View3D")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var i = 0; i < 150 && !head.IsSceneReady && head.LastError is null; i++) await Task.Delay(200);
                    if (!head.IsSceneReady) throw new InvalidOperationException("3D reload after 2D switch failed: " + head.LastError);
                    page.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
                    await Task.Delay(400);
                    web = (NativeWebView)head.Content!;
                    var restored = await web.InvokeScript("[...document.querySelectorAll('#labels button')].find(b=>b.textContent==='F4').hidden.toString()");
                    if (restored?.Trim('"') != "false") throw new InvalidOperationException("Acquisition label did not return");
                    await web.InvokeScript("[...document.querySelectorAll('#labels button')].find(b=>b.textContent==='C3').click()");
                    await Task.Delay(500);
                    if (page.Points.Single(p => p.PositionName == "C3").Role != ElectrodeRole.Acquisition)
                        throw new InvalidOperationException("3D acquisition click did not reach page state");
                    await File.WriteAllTextAsync(Path.Combine(outputDirectory,"3d-bridge-pass.txt"), "34 model labels loaded; unavailable F4 hidden in stimulation and restored in acquisition; assigned AFz remained visible; 3D stimulation FP2 and acquisition C3 assigned; 2D/3D switching preserved assignments. Label=" + label);
                    if (Environment.GetEnvironmentVariable("HEAD3D_REVIEW") == "1") await Task.Delay(TimeSpan.FromMinutes(5));
                    return;
                }

                AssignRolePointsAndChannels(page);
                await Task.Delay(300);
                var fixedSite = page.Points.Single(p => p.IsStimulus && p.StimulationChannelRole == StimulationChannelRole.FixedActive);
                var panelItem = content.GetVisualDescendants().OfType<ImpedanceStatusItem>()
                    .Single(item => ReferenceEquals(item.PointCommandParameter, fixedSite));
                var assignedCount = page.SelectedStimulusElectrodeCount;
                panelItem.PointCommand!.Execute(panelItem.PointCommandParameter);
                await Task.Delay(300);
                if (!ReferenceEquals(page.SelectedPoint, fixedSite) || !panelItem.IsSelected
                    || page.SelectedStimulusElectrodeCount != assignedCount
                    || !page.SelectedPointRoleText.Contains("CH7"))
                    throw new InvalidOperationException("Panel row focus did not preserve and synchronize the head assignment.");
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.panel-focus.png"));
                page.SelectPointCommand.Execute(page.Points.First(p => p.IsStimulus && p != fixedSite));
                await Task.Delay(300);
                if (panelItem.IsSelected) throw new InvalidOperationException("The old panel focus did not clear after head selection.");
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.ready.png"));
                page.ToggleHeadOrientationCommand.Execute(null);
                await Task.Delay(300);
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.back.png"));
                window.Width = 1200;
                window.Height = 760;
                await Task.Delay(300);
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.minimum.png"));
                page.ToggleHeadOrientationCommand.Execute(null);
                window.Width = 1440;
                window.Height = 900;
                await page.ToggleDetectionCommand.ExecuteAsync(null);
                await Task.Delay(1200);
                if (
                    page
                        .Points.Where(point =>
                            point.IsStimulus
                            && point.StimulationChannelRole == StimulationChannelRole.Selectable
                        )
                        .Any(point => point.Impedance is null)
                )
                    throw new InvalidOperationException("刺激阻抗数据未在视觉验收等待时间内更新。");
                if (
                    page
                        .Points.Where(point =>
                            point.IsStimulus
                            && point.StimulationChannelRole == StimulationChannelRole.FixedActive
                        )
                        .Any(point => point.Impedance is not null)
                )
                    throw new InvalidOperationException("固定刺激通道不应显示阻抗读数。");
                Capture(
                    content,
                    Path.Combine(outputDirectory, "ElectrodeConfigurationPage.role-sections.png")
                );
                Capture(
                    content,
                    Path.Combine(
                        outputDirectory,
                        "ElectrodeConfigurationPage.stimulus-detecting.png"
                    )
                );
                await page.ToggleDetectionCommand.ExecuteAsync(null);

                ConfigureAcquisition(page);
                await Task.Delay(300);
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.acquisition-ready.png"));
                await page.ToggleDetectionCommand.ExecuteAsync(null);
                await Task.Delay(1200);
                if (
                    page
                        .Points.Where(point => point.Role == ElectrodeRole.Acquisition)
                        .Any(point => point.Impedance is null)
                )
                    throw new InvalidOperationException("采集阻抗数据未在视觉验收等待时间内更新。");
                Capture(
                    content,
                    Path.Combine(
                        outputDirectory,
                        "ElectrodeConfigurationPage.acquisition-detecting.png"
                    )
                );
                await page.ToggleDetectionCommand.ExecuteAsync(null);
                var states = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 24,
                    Margin = new Thickness(32),
                };
                var samples = new (string Name, EGGtCSPlatform.Controls.ElectrodePoint Point)[]
                {
                    ("Normal", new()),
                    ("Hover", new()),
                    ("Selected", new() { IsSelected = true }),
                    ("Red", new() { IsStimulus = true, ResolvedStimulationRole = ResolvedElectrodeRole.Cathode, IsSelected = true }),
                    ("Blue", new() { IsStimulus = true, ResolvedStimulationRole = ResolvedElectrodeRole.Anode }),
                    ("Impedance", new() { Quality = ImpedanceQuality.Good }),
                    ("Unavailable", new() { IsAvailable = false }),
                    ("Assigned locked", new() { Role = ElectrodeRole.Reference, IsAvailable = false }),
                };
                foreach (var (name, point) in samples)
                {
                    point.Label = name == "Assigned locked" ? "FCz\nREF" : "C3";
                    if (name == "Hover")
                        ((Avalonia.Controls.IPseudoClasses)point.Classes).Set(":pointerover", true);
                    states.Children.Add(new StackPanel
                    {
                        Spacing = 20,
                        Children = { point, new TextBlock { Text = name } },
                    });
                }
                var stateSurface = new Border
                {
                    Background = Avalonia.Media.Brush.Parse("#F0F1FA"),
                    Child = states,
                };
                window.Content = stateSurface;
                await Task.Delay(300);
                Capture(stateSurface, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.point-states.png"));
                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(
                    VisualTestOptions.Current.ErrorPath,
                    exception.ToString()
                );
                Environment.ExitCode = 1;
            }
            finally
            {
                page.Dispose();
                window.Close();
                desktop.Shutdown(Environment.ExitCode);
            }
        };
    }

    private static void ConfigureAcquisition(ElectrodeConfigurationPageViewModel page)
    {
        page.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var point = page.Points.First(item => item.Role == ElectrodeRole.None && !item.IsStimulus);
        page.SelectPointCommand.Execute(point);
    }

    private static void AssignRolePointsAndChannels(ElectrodeConfigurationPageViewModel page)
    {
        var points = new Queue<ElectrodeSiteViewModel>(
            page.Points.Where(point => point.CanStimulate).Take(page.RequiredStimulusElectrodeCount)
        );
        foreach (var option in page.StimulusSelectionOptions)
        {
            page.SelectStimulusSelectionOptionCommand.Execute(option);
            for (var count = 0; count < option.RequiredCount; count++)
                page.SelectPointCommand.Execute(points.Dequeue());
        }

        var usedChannels = new HashSet<int>();
        foreach (var row in page.CurrentImpedanceItems)
        {
            row.SelectedStimulationChannel = row.StimulationChannels.First(option =>
                usedChannels.Add(option.PhysicalChannelId)
            );
        }
        page.SelectStimulusSelectionOptionCommand.Execute(page.StimulusSelectionOptions[0]);
    }

    private static void Capture(Control content, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Ceiling(content.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(content.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(content);
        using var stream = File.Create(outputPath);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private sealed class VisualDialogProvider : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }
}
