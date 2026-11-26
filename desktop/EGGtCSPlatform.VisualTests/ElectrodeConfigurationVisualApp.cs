using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Input;
using EGGtCSPlatform.Views.Pages;
using EGGtCSPlatform.Controls;
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
        var verifyRoleLabels = VisualTestOptions.Current.Scenario.Contains("role-labels");
        var stimulus = new StimulusModeConfigurationViewModel(
            verifyRoleLabels ? StimulusKind.TAcs : StimulusKind.TDcs, capability);
        if (verifyRoleLabels)
            stimulus.SelectedDirectionOption = stimulus.DirectionOptions.Single(option =>
                option.Value is StimulusDirection.Bidirectional);
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
            },
            electrodeCatalog: VisualElectrodeCatalog.Create()
        );
        AssignRolePointsAndChannels(page);

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
            Height = 768,
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
                if (VisualTestOptions.Current.Scenario.Contains("shared"))
                {
                    await VerifySharedPoints(content, page, outputDirectory);
                    return;
                }
                if (verifyRoleLabels)
                {
                    await VerifyRoleLabels(content, connection, outputDirectory);
                    content.DataContext = new BasicViewModel(new VisualTestNavigationRouter(), page, connection);
                    await Task.Delay(400);
                }
                if (VisualTestOptions.Current.Scenario.Contains("3d"))
                {
                    await VerifyHead3D(content, page);
                    File.WriteAllText(Path.Combine(outputDirectory, "head3d.passed.txt"),
                        "3D loading, focus, detection permissions, detach/reload and 2D fallback passed.");
                }
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.ready.png"));
                AssertChannelSelections(content, page, "initial display");
                var shell = (BasicViewModel)content.DataContext!;
                await VerifyNavigationLayout(content, shell, page, outputDirectory, "normal");
                await VerifyPageReturns(content, shell, page);
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.returned.png"));
                var selectableRow = page.CurrentImpedanceItems.Single(row =>
                    row.Site.StimulationChannelRole == StimulationChannelRole.Selectable);
                var changedChannel = selectableRow.StimulationChannels.First(option =>
                    option != selectableRow.SelectedStimulationChannel);
                FindChannelSelector(content, selectableRow).SelectedItem = changedChannel;
                AssertChannelSelections(content, page, "change channel after return");
                if (selectableRow.Site.StimulationPhysicalChannelId != changedChannel.PhysicalChannelId)
                    throw new InvalidOperationException("Channel selection did not update the mapping.");
                page.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
                await Task.Delay(100);
                page.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);
                await Task.Delay(100);
                AssertChannelSelections(content, page, "mode round trip");
                page.ToggleHeadOrientationCommand.Execute(null);
                await Task.Delay(300);
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.back.png"));
                window.Width = 1200;
                window.Height = 760;
                await Task.Delay(300);
                Capture(content, Path.Combine(outputDirectory, "ElectrodeConfigurationPage.minimum.png"));
                await VerifyNavigationLayout(content, shell, page, outputDirectory, "minimum");
                var withoutAnimation = new BasicViewModel(new VisualTestNavigationRouter(), page,
                    connection, isNavigationAnimationEnabled: false);
                content.DataContext = withoutAnimation;
                await Task.Delay(400);
                await VerifyNavigationLayout(content, withoutAnimation, page, outputDirectory, "no-animation");
                content.DataContext = shell;
                await Task.Delay(400);
                page.ToggleHeadOrientationCommand.Execute(null);
                window.Width = 1440;
                window.Height = 768;
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

                await VerifyPageReturns(content, shell, page);
                ConfigureAcquisition(page);
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
                await VerifySingleStimulusSelections(content, shell, capability);
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
                };
                foreach (var (name, point) in samples)
                {
                    point.Label = "C3";
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

    private static async Task VerifyRoleLabels(BasicView content, DeviceSelectionContext connection, string outputDirectory)
    {
        var capability = StimulusCapabilityProfile.FromOptions(new StimulationChannelOptions
        {
            FixedActivePhysicalChannelIds = [7, 8], MultiTargetEnabled = true, HdSelectableChannelCount = 2,
        });
        var passed = new List<string>();
        foreach (var kind in Enum.GetValues<StimulusKind>())
        {
            var configuration = new StimulusModeConfigurationViewModel(kind, capability);
            foreach (var array in configuration.ArrayOptions)
            foreach (var sham in configuration.ShamModeOptions.Take(kind == StimulusKind.Sham ? 2 : 1))
            foreach (var direction in configuration.DirectionOptions)
            {
                configuration.SelectedArrayOption = array;
                configuration.SelectedShamModeOption = sham;
                configuration.SelectedDirectionOption = direction;
                if (configuration.ArrayMode == StimulusArrayMode.MultiTarget)
                {
                    if (configuration.Targets.Count == 1) configuration.AddTargetCommand.Execute(null);
                    for (var targetIndex = 0; targetIndex < configuration.Targets.Count; targetIndex++)
                    {
                        var target = configuration.Targets[targetIndex];
                        target.SelectFixedChannelCommand.Execute(target.Channels.Single(c => c.PhysicalChannelId == 7 + targetIndex));
                        foreach (var channel in target.Channels.Where(c => c.IsSelectableChannel))
                            channel.Enabled = channel.PhysicalChannelId > targetIndex * 2
                                && channel.PhysicalChannelId <= targetIndex * 2 + 2;
                    }
                }
                using var page = new ElectrodeConfigurationPageViewModel(
                    new ElectrodeConfigurationRouteData("ROLE-LABELS", "VISUAL", configuration.CreateSnapshot()),
                    new VisualTestNavigationRouter(), new DialogService(() => null), new VisualDialogProvider(),
                    capability, electrodeCatalog: VisualElectrodeCatalog.Create());
                AssignRolePointsAndChannels(page);
                var assignments = page.Points.Select(p =>
                    (p.StimulationChannelRole, p.ResolvedStimulationRole, p.StimulationPhysicalChannelId)).ToArray();
                content.DataContext = new BasicViewModel(new VisualTestNavigationRouter(), page, connection, false);
                await Task.Delay(400);
                var buttons = content.GetVisualDescendants().OfType<RadioButton>()
                    .Where(b => b.DataContext is StimulusElectrodeSelectionOptionViewModel).ToArray();
                if (buttons.Length != page.StimulusSelectionOptions.Count)
                    throw new InvalidOperationException($"{kind}/{configuration.ArrayMode}: expected {page.StimulusSelectionOptions.Count} role buttons, found {buttons.Length}.");
                foreach (var button in buttons)
                {
                    var option = (StimulusElectrodeSelectionOptionViewModel)button.DataContext!;
                    var expected = option.UsesBlueColor ? "阳极" : "阴极";
                    if (!button.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == expected))
                        throw new InvalidOperationException("Rendered role button label is incorrect.");
                    // Execute the bound command as keyboard activation would.
                    button.Command!.Execute(button.CommandParameter);
                    if (!ReferenceEquals(page.SelectedStimulusSelectionOption, option))
                        throw new InvalidOperationException("Display label changed role selection.");
                }
                foreach (var row in page.CurrentImpedanceItems)
                {
                    Check(row.UiRoleText); Check(row.UiLabel); Check(row.Site.UiToolTipText);
                    var selector = FindChannelSelector(content, row);
                    selector.IsDropDownOpen = true;
                    await Task.Delay(10);
                    foreach (var channel in row.StimulationChannels) Check(channel.UiLabel);
                    selector.IsDropDownOpen = false;
                    if (row.Site.UiDisplayName != row.Site.PositionName + (row.UiRoleText == "阳极" ? "·A" : "·C"))
                        throw new InvalidOperationException("Head label does not use A/C.");
                }
                foreach (var text in content.GetVisualDescendants().OfType<TextBlock>()) Check(text.Text);
                Check(page.SelectedPointRoleText); Check(page.StimulusRequirementText);
                Check(page.StimulusElectrodeSummary); Check(page.PolarityHintText); Check(page.ValidationText);
                if (!assignments.SequenceEqual(page.Points.Select(p =>
                        (p.StimulationChannelRole, p.ResolvedStimulationRole, p.StimulationPhysicalChannelId))))
                    throw new InvalidOperationException("UI label verification changed channel assignments.");
                var name = $"{kind}-{configuration.ArrayMode}-{configuration.ShamMode}-{configuration.Direction}";
                Capture(content, Path.Combine(outputDirectory, $"Roles.{name}.png"));
                passed.Add(name);
            }
        }
        File.WriteAllLines(Path.Combine(outputDirectory, "RoleLabels.passed.txt"), passed);

        static void Check(string? text)
        {
            if (text is not null && (text.Contains("固定刺激") || text.Contains("自选刺激")
                || text.Contains("·固") || text.Contains("·选")))
                throw new InvalidOperationException("Old role label remains: " + text);
        }
    }

    private static void ConfigureAcquisition(ElectrodeConfigurationPageViewModel page)
    {
        page.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var point = page.Points.First(item => item.Role == ElectrodeRole.None && !item.IsStimulus);
        page.SelectPointCommand.Execute(point);
    }

    private static void AssertChannelSelections(
        Control content,
        ElectrodeConfigurationPageViewModel page,
        string phase
    )
    {
        var controls = content.GetVisualDescendants().OfType<ImpedanceStatusItem>().ToArray();
        if (controls.Length != page.CurrentImpedanceItems.Count)
            throw new InvalidOperationException($"{phase}: impedance row count differs.");
        foreach (var row in page.CurrentImpedanceItems)
        {
            var control = controls.Single(item => ReferenceEquals(item.DataContext, row));
            var selector = control.GetVisualDescendants().OfType<ComboBox>().Single();
            var displayed = selector.SelectedItem as StimulationChannelOptionViewModel;
            if ((!row.HasChannelConflict
                    && row.Site.StimulationPhysicalChannelId != row.SelectedStimulationChannel?.PhysicalChannelId)
                || row.SelectedStimulationChannel != displayed
                || control.SelectedStimulationChannel != row.SelectedStimulationChannel
                || (row.SelectedStimulationChannel is not null && selector.SelectionBoxItem is null))
                throw new InvalidOperationException(
                    $"{phase}: {row.Label}: site={row.Site.StimulationPhysicalChannelId}, "
                    + $"row={row.SelectedStimulationChannel?.PhysicalChannelId}, "
                    + $"control={control.SelectedStimulationChannel?.PhysicalChannelId}, "
                    + $"selector={displayed?.PhysicalChannelId}, selectionBox={selector.SelectionBoxItem}"
                );
        }
    }

    private static ComboBox FindChannelSelector(Control content, ElectrodeImpedanceItemViewModel row) =>
        content.GetVisualDescendants().OfType<ImpedanceStatusItem>()
            .Single(item => ReferenceEquals(item.DataContext, row))
            .GetVisualDescendants().OfType<ComboBox>().Single();

    private static async Task VerifyNavigationLayout(
        BasicView content,
        BasicViewModel shell,
        ElectrodeConfigurationPageViewModel page,
        string outputDirectory,
        string scenario
    )
    {
        var host = content.FindControl<TransitioningContentControl>("PageContentHost")!;
        var electrode = content.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
        var head = electrode.GetVisualDescendants().OfType<ElectrodeHeadMap>().Single();
        var hostBounds = host.Bounds;
        var electrodeSize = electrode.Bounds.Size;
        var headSize = head.Bounds.Size;
        var stimulus = new StimulusConfigurationPageViewModel(
            new StimulusConfigurationRouteData("EXP-LAYOUT", "SUBJECT-LAYOUT"),
            new VisualTestNavigationRouter(), new DialogService(() => null), new VisualDialogProvider());
        var samples = new List<string>();

        async Task VerifyOriginalLayout(bool isElectrode)
        {
            // Recreate the old host layout for a settled page, then restore the fixed host.
            // This compares static geometry without retaining the old navigation bug.
            var template = host.ContentTemplate;
            var fixedMargin = host.Margin;
            var currentView = content.GetVisualDescendants().OfType<UserControl>()
                .Single(view => isElectrode ? view is ElectrodeConfigurationPageView
                    : view is StimulusConfigurationPageView);
            var size = currentView.Bounds.Size;
            var origin = currentView.TranslatePoint(default, content);
            host.ContentTemplate = null;
            Grid.SetRow(host, isElectrode ? 1 : 2);
            host.Margin = isElectrode ? fixedMargin : default;
            await Task.Delay(400);
            var originalView = content.GetVisualDescendants().OfType<UserControl>()
                .Single(view => isElectrode ? view is ElectrodeConfigurationPageView
                    : view is StimulusConfigurationPageView);
            if (originalView.Bounds.Size != size || originalView.TranslatePoint(default, content) != origin)
                throw new InvalidOperationException($"{scenario}: settled layout differs from the original.");
            if (isElectrode && originalView.GetVisualDescendants().OfType<ElectrodeHeadMap>().Single().Bounds.Size != headSize)
                throw new InvalidOperationException($"{scenario}: settled head size differs from the original.");
            Capture(content, Path.Combine(outputDirectory, $"Navigation.{scenario}.{(isElectrode ? "electrode" : "stimulus")}.original.png"));
            host.ContentTemplate = template;
            Grid.SetRow(host, 1);
            host.Margin = fixedMargin;
            await Task.Delay(400);
        }

        await VerifyOriginalLayout(isElectrode: true);

        void Sample(string phase)
        {
            if (host.Bounds != hostBounds)
                throw new InvalidOperationException($"{scenario}/{phase}: navigation host resized: {hostBounds} -> {host.Bounds}");
            foreach (var view in content.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>())
            {
                var map = view.GetVisualDescendants().OfType<ElectrodeHeadMap>().Single();
                if (view.Bounds.Size != electrodeSize || map.Bounds.Size != headSize)
                    throw new InvalidOperationException($"{scenario}/{phase}: electrode or head map resized during navigation.");
                samples.Add($"{phase}: host={host.Bounds}; electrode={view.Bounds}; head={map.Bounds}");
            }
            foreach (var view in content.GetVisualDescendants().OfType<StimulusConfigurationPageView>())
            {
                if (Math.Abs(view.Bounds.Height - (hostBounds.Height - 44)) > 1
                    || Math.Abs(view.Bounds.Width - hostBounds.Width) > 1)
                    throw new InvalidOperationException($"{scenario}/{phase}: stimulus page has incorrect inset.");
            }
        }

        Sample("before");
        for (var visit = 0; visit < 3; visit++)
        {
            shell.IsTransitionReversed = true;
            shell.CurrentPage = stimulus;
            // The first round samples a complete transition; later rounds interrupt it.
            var count = visit == 0 ? 8 : 2;
            for (var frame = 0; frame < count; frame++)
            {
                await Task.Delay(50);
                Sample($"out-{visit}-{frame}");
            }
            if (visit == 0)
            {
                Capture(content, Path.Combine(outputDirectory, $"Navigation.{scenario}.stimulus.png"));
                VerifyBackButtonHit();
                await VerifyOriginalLayout(isElectrode: false);
            }
            shell.IsTransitionReversed = false;
            shell.CurrentPage = page;
            for (var frame = 0; frame < count; frame++)
            {
                await Task.Delay(50);
                Sample($"in-{visit}-{frame}");
            }
        }
        await Task.Delay(400);
        Sample("after");
        // The transparent area above ordinary page content must not intercept the header.
        VerifyBackButtonHit();
        File.WriteAllLines(Path.Combine(outputDirectory, $"Navigation.{scenario}.passed.txt"), samples);

        void VerifyBackButtonHit()
        {
            var navigation = content.FindControl<StackPanel>("HeaderNavigationAndStatusPanel")!;
            var backButton = navigation.GetVisualDescendants().OfType<Button>().First();
            var center = backButton.TranslatePoint(new Point(backButton.Bounds.Width / 2,
                backButton.Bounds.Height / 2), content)!.Value;
            var hit = content.InputHitTest(center) as Visual;
            if (hit != backButton && (hit is null || !hit.GetVisualAncestors().Contains(backButton)))
                throw new InvalidOperationException($"{scenario}: page host blocks the back button.");
        }
    }

    private static async Task VerifyPageReturns(
        Control content,
        BasicViewModel shell,
        ElectrodeConfigurationPageViewModel page
    )
    {
        var assignments = page.Points.Select(point =>
            (point.StimulationPhysicalChannelId, point.Impedance)).ToArray();
        var rows = page.CurrentImpedanceItems.Select(row =>
            (row.SelectedStimulationChannel, row.ChannelConflictText)).ToArray();
        var detectionState = page.StimulusDetectionState;
        for (var visit = 0; visit < 3; visit++)
        {
            shell.CurrentPage = new NavigationPlaceholderPage();
            await Task.Delay(400);
            shell.IsTransitionReversed = true;
            shell.CurrentPage = page;
            await Task.Delay(400);
            AssertChannelSelections(content, page, $"return {visit + 1}");
            if (!assignments.SequenceEqual(page.Points.Select(point =>
                    (point.StimulationPhysicalChannelId, point.Impedance)))
                || !rows.SequenceEqual(page.CurrentImpedanceItems.Select(row =>
                    (row.SelectedStimulationChannel, row.ChannelConflictText)))
                || detectionState != page.StimulusDetectionState)
                throw new InvalidOperationException("Navigation changed channel mappings or detection state.");
        }
    }

    private static async Task VerifySingleStimulusSelections(
        Control content,
        BasicViewModel shell,
        StimulusCapabilityProfile capability
    )
    {
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        stimulus.SelectedArrayOption = stimulus.ArrayOptions.Single(option => option.Value is StimulusArrayMode.Hd);
        using var page = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData("EXP-RETURN", "SUBJECT-RETURN", stimulus.CreateSnapshot(),
                CreationMode: ExperimentCreationMode.StimulusOnly),
            new VisualTestNavigationRouter(), new DialogService(() => null), new VisualDialogProvider(), capability);
        AssignRolePointsAndChannels(page);
        shell.CurrentPage = page;
        await Task.Delay(400);
        await VerifyPageReturns(content, shell, page);

        var rows = page.CurrentImpedanceItems.Where(row =>
            row.Site.StimulationChannelRole == StimulationChannelRole.Selectable).Take(2).ToArray();
        var original = rows[1].SelectedStimulationChannel;
        FindChannelSelector(content, rows[1]).SelectedItem = rows[1].StimulationChannels.Single(option =>
            option.PhysicalChannelId == rows[0].SelectedStimulationChannel!.PhysicalChannelId);
        if (!page.HasChannelConflicts || !rows[1].HasChannelConflict || page.CanToggleDetection)
            throw new InvalidOperationException("Duplicate channel selection did not show a conflict.");
        await VerifyPageReturns(content, shell, page);
        FindChannelSelector(content, rows[1]).SelectedItem = original;
        if (page.HasChannelConflicts)
            throw new InvalidOperationException("Restoring the original channel did not clear the conflict.");

        var clearedPoint = rows[1].Site;
        page.ClearPointCommand.Execute(clearedPoint);
        await Task.Delay(100);
        if (page.CurrentImpedanceItems.Any(row => row.Site == clearedPoint))
            throw new InvalidOperationException("Cleared electrode still has a channel row.");
        page.SelectStimulusSelectionOptionCommand.Execute(page.StimulusSelectionOptions.Single(option =>
            option.Role == StimulationChannelRole.Selectable));
        page.SelectPointCommand.Execute(clearedPoint);
        await Task.Delay(100);
        if (clearedPoint.StimulationPhysicalChannelId is not null)
            throw new InvalidOperationException("Re-added electrode restored its cleared channel.");
        await VerifyPageReturns(content, shell, page);
    }

    private sealed class NavigationPlaceholderPage()
        : PageViewModel(ApplicationPageNames.ElectrodeConfiguration, "Navigation regression check");

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

    private static async Task VerifySharedPoints(BasicView content, ElectrodeConfigurationPageViewModel page, string outputDirectory)
    {
        var shared = page.Points.First(p => p.StimulationChannelRole == StimulationChannelRole.Selectable);
        var fixedPoint = page.Points.First(p => p.StimulationChannelRole == StimulationChannelRole.FixedActive);
        page.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        page.SelectPointCommand.Execute(shared);
        page.SelectPointCommand.Execute(fixedPoint);
        page.SetSelectedPointRoleCommand.Execute(ElectrodeRole.Reference);
        page.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);
        await Task.Delay(300);
        Capture(content, Path.Combine(outputDirectory, "Shared.2d.unknown.png"));
        page.ApplyImpedanceReadings(ElectrodeConfigurationMode.Stimulus, new Dictionary<string, double> { [shared.Name] = 45 });
        await Task.Delay(100);
        Capture(content, Path.Combine(outputDirectory, "Shared.2d.stimulation.png"));
        page.ApplyImpedanceReadings(ElectrodeConfigurationMode.Acquisition, new Dictionary<string, double> { [shared.Name] = 8 });
        await Task.Delay(100);
        Capture(content, Path.Combine(outputDirectory, "Shared.2d.both.png"));
        page.ToggleHeadOrientationCommand.Execute(null);
        await Task.Delay(100);
        Capture(content, Path.Combine(outputDirectory, "Shared.2d.back.png"));
        page.ToggleHeadOrientationCommand.Execute(null);
        var rendered = content.GetVisualDescendants().OfType<ElectrodePoint>().Single(p => ReferenceEquals(p.Site, shared));
        var halves = rendered.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "SharedSurface");
        if (!halves.IsVisible || halves.Bounds.Height < 35)
            throw new InvalidOperationException("Shared halves did not render.");
        var upper = halves.Children.OfType<Border>().First(b => Grid.GetRow(b) == 0 && b.Child is Viewbox);
        var lower = halves.Children.OfType<Border>().Single(b => Grid.GetRow(b) == 1);
        if (Math.Abs(upper.Bounds.Height / halves.Bounds.Height - .7) > .01
            || Math.Abs(lower.Bounds.Height / halves.Bounds.Height - .3) > .01)
            throw new InvalidOperationException("Shared point must use a 70/30 split.");
        Capture(rendered, Path.Combine(outputDirectory, "Shared.point.png"));

        if (VisualTestOptions.Current.Scenario.Contains("3d"))
        {
        var view = content.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
        view.FindControl<RadioButton>("View3D")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = view.FindControl<ContentControl>("Head3DHost")!;
        var head = (ElectrodeHead3D)host.Content!;
        for (var i = 0; i < 160 && !head.IsSceneReady && head.LastError is null; i++) await Task.Delay(200);
        if (!head.IsSceneReady) throw new InvalidOperationException("3D load: " + head.LastError);
        await Task.Delay(600);
        var web = ((Grid)head.Content!).Children.OfType<NativeWebView>().Single();
        var details = await web.InvokeScript("JSON.stringify([...document.querySelectorAll('.point.shared')].map(b=>({text:b.textContent,halves:b.children.length,colors:[...b.children].map(s=>getComputedStyle(s).backgroundColor),visible:getComputedStyle(b).visibility})))");
        File.WriteAllText(Path.Combine(outputDirectory, "Shared.3d.json"), details ?? "");
        var valid = await web.InvokeScript("[...document.querySelectorAll('.point.shared')].every(b=>b.children.length===2 && getComputedStyle(b).borderRadius==='50%').toString()");
        var count = await web.InvokeScript("document.querySelectorAll('.point.shared').length.toString()");
        if (valid?.Trim('"') != "true" || count?.Trim('"') != "2")
            throw new InvalidOperationException("3D shared point markup mismatch: " + details);
        view.FindControl<RadioButton>("View2D")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        }

        var matrix = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        foreach (var role in new[] { ElectrodeRole.Acquisition, ElectrodeRole.Reference, ElectrodeRole.Ground })
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 24 };
            foreach (double? value in new double?[] { null, 8, 18, 28, 38, 45 })
            {
                var site = new ElectrodeSiteViewModel("CP10", 0, 0, true, role);
                site.AssignStimulusPosition(Guid.NewGuid(), 1, 1, StimulationChannelRole.Selectable, StimulusDirection.Positive);
                site.StimulationImpedance = value;
                site.SetDetectionResultAvailable(ElectrodeConfigurationMode.Stimulus, value is not null);
                site.SetDetectionResultAvailable(ElectrodeConfigurationMode.Acquisition, value is not null);
                if (role == ElectrodeRole.Acquisition) site.AcquisitionImpedance = value is null ? null : 53 - value;
                row.Children.Add(new ElectrodePoint { Site = site, IsStimulus = true, Role = role,
                    Label = site.UiDisplayName, Quality = site.DisplayQuality, IsSelected = value == 28 });
            }
            matrix.Children.Add(row);
        }
        var surface = new Border { Background = Avalonia.Media.Brushes.White, Child = matrix,
            Width = 600, Height = 300, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = (Window)TopLevel.GetTopLevel(content)!;
        window.Content = surface;
        await Task.Delay(300);
        if (surface.GetVisualDescendants().OfType<Grid>().Count(g => g.Name == "SharedSurface" && g.IsVisible) != 18)
            throw new InvalidOperationException("Color matrix controls were not rendered.");
        Capture(surface, Path.Combine(outputDirectory, "Shared.color-matrix.png"));
        File.WriteAllText(Path.Combine(outputDirectory, "Shared.passed.txt"), "Shared rendering passed: " + VisualTestOptions.Current.Scenario);
    }

    private static async Task VerifyHead3D(Control content, ElectrodeConfigurationPageViewModel page)
    {
        var view = content.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
        var host = view.FindControl<ContentControl>("Head3DHost")!;
        var button3D = view.FindControl<RadioButton>("View3D")!;
        var button2D = view.FindControl<RadioButton>("View2D")!;
        button3D.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var head = (ElectrodeHead3D)host.Content!;
        await WaitReady(head);
        var shell = (BasicViewModel)content.DataContext!;
        shell.CurrentPage = new NavigationPlaceholderPage();
        if (host.Content is not null || head.IsSceneReady || head.Content is not null)
            throw new InvalidOperationException("Navigation did not synchronously release the native 3D view.");
        await Task.Delay(400);
        shell.CurrentPage = page;
        await Task.Delay(400);
        view = content.GetVisualDescendants().OfType<ElectrodeConfigurationPageView>().Single();
        host = view.FindControl<ContentControl>("Head3DHost")!;
        button3D = view.FindControl<RadioButton>("View3D")!;
        button2D = view.FindControl<RadioButton>("View2D")!;
        button3D.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        head = (ElectrodeHead3D)host.Content!;
        await WaitReady(head);
        var web = ((Grid)head.Content!).Children.OfType<NativeWebView>().Single();
        var count = await web.InvokeScript("document.querySelectorAll('#labels button').length.toString()");
        if (count?.Trim('"') != "34") throw new InvalidOperationException("Expected 34 3D electrodes.");
        var polarityCount = await web.InvokeScript("[...document.querySelectorAll('#labels button')].filter(b=>b.textContent.includes('·A')||b.textContent.includes('·C')).length.toString()");
        if (polarityCount?.Trim('"') != page.Points.Count(p => p.IsStimulus).ToString())
            throw new InvalidOperationException("3D labels did not display A/C for all stimulation electrodes.");
        var monitored = page.Points.First(p => p.IsStimulus && p.StimulationChannelRole == StimulationChannelRole.Selectable);
        var another = page.Points.First(p => p.IsStimulus && p != monitored);
        page.FocusPointCommand.Execute(another);
        await web.InvokeScript($"[...document.querySelectorAll('#labels button')].find(b=>b.textContent.startsWith('{monitored.PositionName}')).click()");
        await Task.Delay(400);
        if (!ReferenceEquals(page.SelectedPoint, monitored))
            throw new InvalidOperationException("3D selection did not focus the native electrode.");
        await page.ToggleDetectionCommand.ExecuteAsync(null);
        try
        {
            await Task.Delay(400);
            var animating = await web.InvokeScript($"window.isHead3DAnimating('{monitored.PositionName}').toString()");
            if (animating?.Trim('"') != "true") throw new InvalidOperationException("3D detection animation did not start.");
            await web.InvokeScript($"[...document.querySelectorAll('#labels button')].find(b=>b.textContent.startsWith('{another.PositionName}')).click()");
            await Task.Delay(200);
            if (!ReferenceEquals(page.SelectedPoint, monitored))
                throw new InvalidOperationException("3D selection changed during detection.");
        }
        finally { await page.ToggleDetectionCommand.ExecuteAsync(null); }
        host.Content = null;
        if (head.IsSceneReady || head.Content is not null)
            throw new InvalidOperationException("Detached 3D view retained its renderer.");
        host.Content = head;
        await WaitReady(head);
        web = ((Grid)head.Content!).Children.OfType<NativeWebView>().Single();
        await web.InvokeScript("window.invokeCSharpAction(JSON.stringify({type:'error',message:'Visual test load failure'}))");
        await Task.Delay(300);
        if (head.IsSceneReady || head.LastError != "Visual test load failure" || head.Content is not TextBlock)
            throw new InvalidOperationException("3D loading failure did not show the fallback message.");
        button2D.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (host.Content is not null || head.IsSceneReady || !view.FindControl<ElectrodeHeadMap>("Head2D")!.IsVisible)
            throw new InvalidOperationException("2D fallback did not release the 3D view.");

        static async Task WaitReady(ElectrodeHead3D head)
        {
            for (var i = 0; i < 160 && !head.IsSceneReady && head.LastError is null; i++) await Task.Delay(200);
            if (!head.IsSceneReady) throw new InvalidOperationException("3D load: " + head.LastError);
            await Task.Delay(400);
        }
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
