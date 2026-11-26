using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using System.Text.Json;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

// Uses the production MainView, adorner host, electrode page and native browser.
// The interactive variant waits for real OS input; it never starts device stimulation.
public sealed class Head3DDialogVisualApp : App
{
    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(output);
        try
        {
            await Run(desktop, output).WaitAsync(VisualTestOptions.Current.Timeout);
            File.WriteAllText(Path.Combine(output, "Head3DDialog.passed.txt"),
                "PASS: inherited modality, nested/closing dialogs, blocked selection, browser identity, state synchronization, loading, 2D and reentry.");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "Head3DDialog.error.txt"), error.ToString());
            desktop.Shutdown(1);
        }
    }

    private static async Task Run(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        var connection = new DeviceSelectionContext();
        connection.SetConnectedDevice(DeviceId.Simulator.Value, "Dialog regression simulator");
        var mainModel = new MainViewModel(new PageFactory((_, _) => throw new NotSupportedException()), connection);
        var window = new Window { Title = "3D dialog regression - simulator", Width = 1400, Height = 850 };
        var service = new DialogService(() => window);
        var capability = StimulusCapabilityProfile.FromOptions(new StimulationChannelOptions { FixedActivePhysicalChannelIds = [7] });
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        using var page = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData("TEST-3D-DIALOG", "SIMULATED", stimulus.CreateSnapshot(), DeviceId.Simulator.Value),
            new VisualTestNavigationRouter(), service, mainModel, capability, new PreviewImpedanceDetectionService(),
            new ImpedanceDetectionOptions(), electrodeCatalog: VisualElectrodeCatalog.Create());
        var points = new Queue<ElectrodeSiteViewModel>(page.Points.Where(p => p.CanStimulate).Take(page.RequiredStimulusElectrodeCount));
        foreach (var option in page.StimulusSelectionOptions)
        {
            page.SelectStimulusSelectionOptionCommand.Execute(option);
            for (var i = 0; i < option.RequiredCount; i++) page.SelectPointCommand.Execute(points.Dequeue());
        }
        mainModel.CurrentView = new BasicViewModel(new VisualTestNavigationRouter(), page, connection);
        var main = new MainView { DataContext = mainModel };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        var scaledMain = new LayoutTransformControl { Child = main };
        layout.Children.Add(scaledMain);
        window.Content = layout;
        desktop.MainWindow = window;
        window.Show();
        await Task.Delay(500);
        var view = main.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
        var host = view.FindControl<ContentControl>("Head3DHost")!;
        void Switch(string name)
        {
            var button = view.FindControl<RadioButton>(name)!;
            button.IsChecked = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        // Delay the real local model response to exercise the HTML loader and cancellation.
        Switch("View3D");
        var delayed = new ElectrodeHead3D { Page = page, ModelLoadDelay = TimeSpan.FromSeconds(20) };
        if (VisualTestOptions.Current.Scenario.Contains("loading-preview"))
        {
            delayed.ModelLoadDelay = TimeSpan.FromSeconds(90);
            delayed.StartupTimeout = TimeSpan.FromSeconds(120);
        }
        host.Content = delayed;
        await Task.Delay(100); // Allow ContentPresenter to attach the replacement control.
        Check(delayed.GetVisualDescendants().OfType<RunLoadingSpinner>().Any(), "Native startup must show a spinner immediately");
        await Task.Delay(2500);
        var delayedWeb = delayed.GetVisualDescendants().OfType<NativeWebView>().Single();
        Check(!delayed.IsScenePresented, "Delayed model must not count as presented");
        Check((await delayedWeb.InvokeScript("(!!document.querySelector('#loading') && document.querySelector('#stage').inert).toString()"))?.Trim('"') == "true",
            "HTML loader must cover the scene and prevent interaction before model loading");
        Check((await delayedWeb.InvokeScript("[document.documentElement, document.body, document.querySelector('#loading')].every(e => getComputedStyle(e).backgroundColor === 'rgba(0, 0, 0, 0)').toString()"))?.Trim('"') == "true",
            "All web loading backgrounds must be transparent");
        Check((await delayedWeb.InvokeScript("(getComputedStyle(document.querySelector('#stage')).opacity === '0' && document.querySelector('#loading').textContent === '' && !document.querySelector('.loading-spinner')).toString()"))?.Trim('"') == "true",
            "Partial scene must remain hidden with no duplicate web spinner or text");
        Check(delayed.GetVisualDescendants().OfType<RunLoadingSpinner>().Count() == 1,
            "One native spinner must remain throughout web initialization");
        foreach (var (width, height, scale) in new[] { (1000, 700, 1d), (1400, 850, 1.5d), (1400, 850, 1d) })
        {
            window.Width = width; window.Height = height;
            scaledMain.LayoutTransform = new Avalonia.Media.ScaleTransform(scale, scale);
            await Task.Delay(150);
            var spinner = delayed.GetVisualDescendants().OfType<RunLoadingSpinner>().Single();
            var panel = (StackPanel)spinner.Parent!;
            var center = panel.TranslatePoint(new Avalonia.Point(panel.Bounds.Width / 2, panel.Bounds.Height / 2), delayed)!.Value;
            Check(Math.Abs(center.X - delayed.Bounds.Width / 2) < 1 && Math.Abs(center.Y - delayed.Bounds.Height / 2) < 1,
                "Loading group must remain centered at small window and 150% layout scale");
        }
        if (VisualTestOptions.Current.Scenario.Contains("loading-preview"))
        {
            var resume = new TaskCompletionSource();
            var resumeButton = new Button { Content = "继续加载验收" };
            resumeButton.Click += (_, _) => resume.TrySetResult();
            toolbar.Children.Add(resumeButton);
            await resume.Task;
            toolbar.Children.Remove(resumeButton);
        }
        Switch("View2D");
        Check(delayed.Content is null && !delayed.IsScenePresented, "Leaving during loading must release the browser and spinner");

        Switch("View3D");
        var timedOut = new ElectrodeHead3D { Page = page, ModelLoadDelay = TimeSpan.FromSeconds(20), StartupTimeout = TimeSpan.FromSeconds(2) };
        host.Content = timedOut;
        await Task.Delay(3000);
        Check(timedOut.LastError?.Contains("超时") == true && timedOut.Content is TextBlock,
            "Startup deadline must replace loading animation with the failure message");
        Check(!timedOut.GetVisualDescendants().OfType<RunLoadingSpinner>().Any(), "Error must stop the native animation");
        Switch("View2D");

        Switch("View3D");
        var failed = (ElectrodeHead3D)host.Content!;
        await Ready(failed);
        await failed.GetVisualDescendants().OfType<NativeWebView>().Single().InvokeScript("window.invokeCSharpAction(JSON.stringify({type:'error',message:'Simulated model failure'}))");
        await Task.Delay(200);
        Check(failed.LastError == "Simulated model failure" && failed.Content is TextBlock && !failed.IsScenePresented,
            "Model failure must release the native browser and replace the loader");
        Switch("View2D");

        Switch("View3D");
        var head = (ElectrodeHead3D)host.Content!;
        var initialDialog = Dialog(false);
        var initialTask = service.ShowDialogAsync(mainModel, initialDialog);
        await Task.Delay(200);
        Check(((Grid)head.Content!).Children.OfType<NativeWebView>().SingleOrDefault() is { IsVisible: false }, "Opening a dialog during browser startup must hide it: " + head.LastError);
        await Ready(head);
        var web = ((Grid)head.Content!).Children.OfType<NativeWebView>().Single();
        Check(!web.IsVisible, "Ready message must not expose a blocked browser");
        Check(!head.IsScenePresented, "Model ready alone must not dismiss the loader before initial host state");
        initialDialog.CancelCommand.Execute(null);
        await initialTask;
        await Presented(head);
        Check((await web.InvokeScript("(document.querySelector('#loading') === null && !document.querySelector('#stage').inert).toString()"))?.Trim('"') == "true",
            "First rendered electrode frame must remove the loader and enable interaction");
        Check(!head.GetVisualDescendants().OfType<RunLoadingSpinner>().Any(), "Presentation must detach the native spinner");
        await web.InvokeScript("window.__dialogRegressionToken = 'same-document'");
        var selected = page.SelectedPoint;
        var another = page.Points.First(p => p.IsStimulus && p != selected);
        for (var i = 0; i < 3; i++)
        {
            var lower = Dialog(i % 2 == 0);
            var lowerTask = service.ShowDialogAsync(mainModel, lower);
            await Task.Delay(250);
            Check(!web.IsVisible && NativeContentModalState.GetIsBlocked(head), "MainView must hide the native browser");
            await web.InvokeScript($"window.invokeCSharpAction(JSON.stringify({{type:'select',id:'{another.PositionName}'}}))");
            await Task.Delay(100);
            Check(ReferenceEquals(page.SelectedPoint, selected), "Modal must reject queued webpage selections");
            var upper = new ConfirmDialogViewModel { Message = "Nested simulated confirmation" };
            var upperTask = service.ShowDialogAsync(mainModel, upper);
            await Task.Delay(100);
            upper.CancelCommand.Execute(null);
            Check(!web.IsVisible, "Browser must remain hidden during exit animation");
            await upperTask;
            Check(mainModel.DialogStack.Count == 1 && !web.IsVisible, "Lower dialog still blocks native content");
            lower.CancelCommand.Execute(null);
            Check(!web.IsVisible, "Last closing dialog still blocks native content");
            await lowerTask;
            await Task.Delay(250);
            Check(web.IsVisible && ReferenceEquals(web, ((Grid)head.Content!).Children.OfType<NativeWebView>().Single()) && head.IsSceneReady, "Restore the same ready browser");
            Check((await web.InvokeScript("window.__dialogRegressionToken"))?.Trim('"') == "same-document", "Document must not reload");
        }

        var syncDialog = Dialog(false);
        var syncTask = service.ShowDialogAsync(mainModel, syncDialog);
        await Task.Delay(200);
        await web.InvokeScript("window.__originalApply = window.applyHostState; window.applyHostState = s => { window.__lastApplied = s; window.__originalApply(s); }");
        page.FocusPointCommand.Execute(another);
        syncDialog.CancelCommand.Execute(null);
        await syncTask;
        await Task.Delay(400);
        Check((await web.InvokeScript($"window.__lastApplied.points.find(p=>p.id==='{another.PositionName}').focused.toString()"))?.Trim('"') == "true",
            "Restoring the browser must synchronize state changed while hidden");

        // Open a modal before entering 3D, then exercise native initialization underneath it.
        Switch("View2D");
        Check(head.Content is null && !head.IsSceneReady, "2D switch releases the renderer");
        var loadingDialog = Dialog(true);
        var loadingTask = service.ShowDialogAsync(mainModel, loadingDialog);
        await Task.Delay(200);
        Switch("View3D");
        head = (ElectrodeHead3D)host.Content!;
        web = ((Grid)head.Content!).Children.OfType<NativeWebView>().Single();
        Check(!web.IsVisible, "Browser created under a dialog must start hidden");
        await Task.Delay(1500);
        Check(!web.IsVisible && head.LastError is null, "Loading must not expose native content");
        loadingDialog.CancelCommand.Execute(null);
        await loadingTask;
        await Ready(head);
        await Presented(head);
        Check(web.IsVisible && ReferenceEquals(web, ((Grid)head.Content!).Children.OfType<NativeWebView>().Single()), "Initially hidden browser must load and restore");

        if (VisualTestOptions.Current.Scenario.Contains("interactive"))
        {
            var done = new TaskCompletionSource();
            var status = new TextBlock { Text = "Rotate head, then open dialogs. Simulator only.", VerticalAlignment = VerticalAlignment.Center };
            var count = 0;
            foreach (var name in new[] { "Ordinary", "Envelope", "Cancel dialog" })
            {
                var button = new Button { Content = name };
                button.Click += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(250); // allow orbit damping to settle
                        var before = await web.InvokeScript("JSON.stringify(window.getHead3DState())");
                        var originalSelection = page.SelectedPoint;
                        var dialog = name == "Cancel dialog" ? (DialogViewModel)new ConfirmDialogViewModel { Message = "Native input cancellation check" } : Dialog(name == "Envelope");
                        await service.ShowDialogAsync(mainModel, dialog);
                        await Task.Delay(400);
                        Check(web.IsVisible && ReferenceEquals(web, ((Grid)head.Content!).Children.OfType<NativeWebView>().Single()), "Interactive close must restore browser identity");
                        Check(ReferenceEquals(originalSelection, page.SelectedPoint), "Native click must not pass through to electrodes");
                        var after = await web.InvokeScript("JSON.stringify(window.getHead3DState())");
                        Check(before == after, "Projected electrode positions (camera) must survive modal");
                        var confirmed = dialog is ExperimentConfigurationDialogViewModel experiment ? experiment.Confirmed : ((ConfirmDialogViewModel)dialog).Confirmed;
                        status.Text = $"Native checks passed: {++count}; {name}; confirmed={confirmed}";
                        File.AppendAllText(Path.Combine(output, "Head3DDialog.native-input.txt"), status.Text + Environment.NewLine);
                    }
                    catch (Exception error) { done.TrySetException(error); }
                };
                toolbar.Children.Add(button);
            }
            var finish = new Button { Content = "Finish" };
            finish.Click += (_, _) => done.TrySetResult();
            toolbar.Children.Add(finish);
            toolbar.Children.Add(status);
            await done.Task;
        }
        // Simulate leaving the page after a successful confirmation; no device command runs.
        var confirm = Dialog(true);
        var confirmTask = service.ShowDialogAsync(mainModel, confirm);
        await Task.Delay(200);
        confirm.ConfirmCommand.Execute(null);
        await confirmTask;
        mainModel.CurrentView = new IndexViewModel(mainModel, connection);
        await Task.Delay(700);
        Check(head.Content is null && !head.IsSceneReady, "Navigation must release hidden/restored browser");
        mainModel.CurrentView = new BasicViewModel(new VisualTestNavigationRouter(), page, connection);
        await Task.Delay(700);
        view = main.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
        host = view.FindControl<ContentControl>("Head3DHost")!;
        Switch("View3D");
        head = (ElectrodeHead3D)host.Content!;
        await Ready(head);
        Check((((Grid)head.Content!).Children.OfType<NativeWebView>().Single()).IsVisible, "Reentered page must not retain stale modality");
        foreach (var count in new[] { 2, 40 })
        {
            var definitions = Enumerable.Range(1, count).Select(i =>
                new ElectrodePositionDefinition($"CUSTOM-{i}", $"显示-{i}", 150+i, 180+i,
                    Position3D: new ElectrodeVector3(i, 30, 50))).Reverse().ToArray();
            using var customPage = new ElectrodeConfigurationPageViewModel(
                page.RouteData, new VisualTestNavigationRouter(), service, mainModel, capability,
                new PreviewImpedanceDetectionService(), electrodeCatalog: new ElectrodePositionCatalog(
                    new ElectrodePositionOptions { Positions = definitions }));
            customPage.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
            mainModel.CurrentView = new BasicViewModel(new VisualTestNavigationRouter(), customPage, connection);
            await Task.Delay(700);
            view = main.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
            host = view.FindControl<ContentControl>("Head3DHost")!;
            Switch("View3D");
            head = (ElectrodeHead3D)host.Content!;
            await Ready(head);
            var customWeb = ((Grid)head.Content!).Children.OfType<NativeWebView>().Single();
            var countText = await customWeb.InvokeScript("document.querySelectorAll('#labels button').length.toString()");
            Check(countText?.Trim('"') == count.ToString(), "3D must follow configured count without leftover electrodes");
            var geometry = await customWeb.InvokeScript("JSON.stringify(window.getHead3DState().points)");
            using var parsed = JsonDocument.Parse(JsonSerializer.Deserialize<string>(geometry!)!);
            Check(parsed.RootElement[0].GetProperty("id").GetString() == definitions[0].Id, "3D must follow configured IDs and order");
            Check(Math.Abs(parsed.RootElement[0].GetProperty("position")[0].GetDouble() + count/1000d) < 1e-8,
                "3D must use configured position with wearer-right conversion");
            await customWeb.InvokeScript("window.invokeCSharpAction(JSON.stringify({type:'select',id:'custom-1'}))");
            await Task.Delay(300);
            Check(customPage.SelectedPoint?.Name == "CUSTOM-1", "Bridge must select by stable ID, case insensitive, not display name");
            var retained = customPage.SelectedPoint;
            Switch("View2D");
            Check(ReferenceEquals(retained, customPage.SelectedPoint) && retained?.Role == ElectrodeRole.Acquisition,
                "2D must retain the same configured electrode and role");
            Check(head.Content is null && !head.IsSceneReady, "Dynamic browser must release on 2D switch");
        }
        foreach (var (destination, animated) in new[] { ("back", true), ("home", true), ("forward", true), ("loading", true), ("back", false) })
        {
            MainViewModel? navigation = null;
            var navigationPage = new ElectrodeConfigurationPageViewModel(page.RouteData,
                new VisualTestNavigationRouter(), service, mainModel, capability, new PreviewImpedanceDetectionService(),
                electrodeCatalog: VisualElectrodeCatalog.Create());
            navigation = new MainViewModel(new PageFactory((route, _) => route == ApplicationPageNames.ElectrodeConfiguration
                ? navigationPage : new NavigationTestPage(route)), connection);
            navigation.CurrentView = new BasicViewModel(navigation, new NavigationTestPage(ApplicationPageNames.StimulusConfiguration), connection, animated);
            main.DataContext = navigation;
            navigation.Navigate(ApplicationPageNames.ElectrodeConfiguration);
            await Task.Delay(700);
            view = main.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
            host = view.FindControl<ContentControl>("Head3DHost")!;
            Switch("View3D");
            head = (ElectrodeHead3D)host.Content!;
            if (destination == "loading")
            {
                head.ModelLoadDelay = TimeSpan.FromSeconds(20);
                await Task.Delay(2000); // Browser initialized; model response is still pending.
            }
            else { await Ready(head); await Presented(head); }
            var departingWeb = ((Grid)head.Content!).Children.OfType<NativeWebView>().Single();
            var basic = (BasicViewModel)navigation.CurrentView;
            navigation.Navigate(ApplicationPageNames.ElectrodeConfiguration);
            Check(departingWeb.IsVisible, "No-op navigation must retain the browser");
            var changed = new TaskCompletionSource();
            void CheckExit()
            {
                Check(!departingWeb.IsVisible && head.Content is null && !head.IsSceneReady,
                    "Native browser must hide and release synchronously before the page transition");
                changed.TrySetResult();
            }
            basic.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BasicViewModel.CurrentPage)) CheckExit(); };
            navigation.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.CurrentView)) CheckExit(); };
            if (destination == "back" && animated && VisualTestOptions.Current.Scenario.Contains("navigation-preview"))
                await changed.Task; // Actual Back button is bound to the production router.
            else if (destination == "home") navigation.GoHome();
            else if (destination == "forward") navigation.Navigate(ApplicationPageNames.ExperimentRun);
            else navigation.GoBack();
            await changed.Task;
            File.AppendAllText(Path.Combine(output, "Head3DNavigation.checks.txt"), $"PASS {destination}, animation={animated}: synchronous native exit\n");
            if (destination == "back" && animated && VisualTestOptions.Current.Scenario.Contains("navigation-preview"))
                await Task.Delay(8000); // Allow a native screenshot of the destination page.
            head.Exit(); // Repeated cleanup must remain harmless.
            await Task.Delay(450);
            Check(head.Content is null, "Late startup or modal callbacks must not restore an exited scene");
            navigation.GoHome();
        }
        window.Close();
        Check(head.Content is null && !head.IsSceneReady, "Window close must release the renderer");
    }

    private static ExperimentConfigurationDialogViewModel Dialog(bool envelope) => new(
        "SIMULATED", "TEST-3D-DIALOG", "tDCS", "2 mA", "FP2 / CH1", "C3 / CH2", "Simulation only",
        envelopeConfirmation: envelope ? new EnvelopeConfirmationData("2 mA", "0 s", "F3 / CH7", "FP2 / CH1") : null);

    private sealed class NavigationTestPage(ApplicationPageNames name) : PageViewModel(name, "Navigation regression");

    private static async Task Ready(ElectrodeHead3D head)
    {
        for (var i = 0; i < 160 && !head.IsSceneReady && head.LastError is null; i++) await Task.Delay(200);
        Check(head.IsSceneReady, "3D load failed: " + head.LastError);
        await Task.Delay(500);
    }
    private static async Task Presented(ElectrodeHead3D head)
    {
        for (var i = 0; i < 160 && !head.IsScenePresented && head.LastError is null; i++) await Task.Delay(200);
        Check(head.IsScenePresented, "3D first frame failed: " + head.LastError);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
