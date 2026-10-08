using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed class StimulusConfigurationVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var outputDirectory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(outputDirectory);
        File.Delete(VisualTestOptions.Current.ErrorPath);

        var figmaTacs = VisualTestOptions.Current.Scenario.EndsWith("figma-tacs");
        var page = CreatePage(animationsEnabled: !figmaTacs);
        var content = new StimulusConfigurationPageView
        {
            Background = new SolidColorBrush(Color.Parse("#F2F4FA")),
            DataContext = page,
        };
        var window = new Window
        {
            Title = "刺激模式垂直切换动画 - 自动视觉验收",
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
                await Task.Delay(150);
                var indicator =
                    content.FindControl<Border>("ModeSelectionIndicator")
                    ?? throw new InvalidOperationException("未找到刺激模式共享指示条。");
                var host =
                    content.FindControl<TransitioningContentControl>("ModeContentHost")
                    ?? throw new InvalidOperationException("未找到刺激模式内容过渡容器。");
                if (!host.ClipToBounds)
                    throw new InvalidOperationException("刺激模式内容过渡容器未开启边界裁剪。");

                var initialY = GetY(indicator, content);
                EnsureNear(initialY, 14d, 1d, "初始指示条位置");
                Capture(
                    content,
                    Path.Combine(outputDirectory, "StimulusConfigurationPage.initial.png")
                );
                if (figmaTacs)
                {
                    page.CurrentConfiguration.SelectedArrayOption = page.CurrentConfiguration.ArrayOptions[1];
                    page.CurrentConfiguration.SelectedDirectionOption = page.CurrentConfiguration.DirectionOptions[1];
                    await Task.Delay(200);
                    EnsureNumericInputGeometry(content, "Figma tDCS HD 反向页面");
                    Capture(content, Path.Combine(outputDirectory, "StimulusConfigurationPage.tdcs-hd-negative.png"));
                    page.Modes[1].SelectCommand.Execute(null);
                    await Task.Delay(400);
                    Capture(content, Path.Combine(outputDirectory, "StimulusConfigurationPage.tacs.png"));
                    page.CurrentConfiguration.SelectedArrayOption = page.CurrentConfiguration.ArrayOptions[1];
                    await Task.Delay(150);
                    EnsureNumericInputGeometry(content, "HD 标准窗口");
                    Capture(content, Path.Combine(outputDirectory, "StimulusConfigurationPage.tacs-hd.png"));
                    foreach (var modeIndex in new[] { 0, 2, 3, 4 })
                    {
                        page.Modes[modeIndex].SelectCommand.Execute(null);
                        await Task.Delay(250);
                        Capture(
                            content,
                            Path.Combine(outputDirectory, $"StimulusConfigurationPage.{page.CurrentConfiguration.Kind}.png")
                        );
                    }
                    window.Width = 1200;
                    window.Height = 760;
                    await Task.Delay(200);
                    Capture(content, Path.Combine(outputDirectory, "StimulusConfigurationPage.Sham-minimum.png"));
                    page.Modes[1].SelectCommand.Execute(null);
                    await Task.Delay(250);
                    EnsureNumericInputGeometry(content, "HD 最小窗口");
                    Capture(content, Path.Combine(outputDirectory, "StimulusConfigurationPage.tacs-minimum.png"));
                    window.Width = 1875;
                    window.Height = 900;
                    await Task.Delay(250);
                    EnsureNumericInputGeometry(content, "HD 宽窗口");
                    Capture(content, Path.Combine(outputDirectory, "StimulusConfigurationPage.tacs-hd-wide.png"));

                    var statusPage = CreatePage(
                        animationsEnabled: false,
                        capability: StimulusCapabilityProfile.FromOptions(
                            new StimulationChannelOptions { FixedActivePhysicalChannelIds = [5] }
                        )
                    );
                    var statusContent = new StimulusConfigurationPageView
                    {
                        Background = new SolidColorBrush(Color.Parse("#F2F4FA")),
                        DataContext = statusPage,
                    };
                    window.Width = 1440;
                    window.Content = statusContent;
                    statusPage.Modes[1].SelectCommand.Execute(null);
                    statusPage.CurrentConfiguration.SelectedArrayOption =
                        statusPage.CurrentConfiguration.ArrayOptions[1];
                    await Task.Delay(250);
                    var selectedChannels = statusPage.CurrentConfiguration.PhysicalChannels
                        .Where(channel => channel.IsSelectableChannel && channel.Enabled)
                        .ToArray();
                    if (selectedChannels.Length != 4)
                        throw new InvalidOperationException("HD 状态验收未初始化 4 个自选通道。");
                    selectedChannels[^1].Enabled = false;
                    await Task.Delay(150);
                    if (!statusPage.CurrentConfiguration.AllocationStatusText.Contains("当前 3/4"))
                        throw new InvalidOperationException("HD 3/4 错误提示未显示。");
                    Capture(
                        statusContent,
                        Path.Combine(outputDirectory, "StimulusConfigurationPage.tacs-hd-three-of-four.png")
                    );
                    selectedChannels[^1].Enabled = true;
                    await Task.Delay(150);
                    if (statusPage.CurrentConfiguration.HasAllocationError)
                        throw new InvalidOperationException("HD 4/4 正常状态仍显示错误。");
                    Capture(
                        statusContent,
                        Path.Combine(outputDirectory, "StimulusConfigurationPage.tacs-hd-valid.png")
                    );
                    statusPage.Modes[0].SelectCommand.Execute(null);
                    statusPage.CurrentConfiguration.SelectedArrayOption =
                        statusPage.CurrentConfiguration.ArrayOptions[1];
                    statusPage.CurrentConfiguration.SelectedDirectionOption =
                        statusPage.CurrentConfiguration.DirectionOptions[1];
                    await Task.Delay(250);
                    EnsureNumericInputGeometry(statusContent, "Figma tDCS HD 有效配置页面");
                    Capture(
                        statusContent,
                        Path.Combine(outputDirectory, "StimulusConfigurationPage.tdcs-hd-negative-valid.png")
                    );
                    Environment.ExitCode = 0;
                    return;
                }

                page.Modes[3].SelectCommand.Execute(null);
                await Task.Delay(120);
                var forwardIndicatorY = GetY(indicator, content);
                EnsureBetween(forwardIndicatorY, initialY, initialY + 186d, "向下指示条动画");
                EnsureContentDirections(
                    host,
                    StimulusKind.TDcs,
                    -1,
                    StimulusKind.TPcs,
                    1,
                    "向下内容动画"
                );
                Capture(
                    content,
                    Path.Combine(outputDirectory, "StimulusConfigurationPage.forward-mid.png")
                );

                await Task.Delay(220);
                EnsureNear(GetY(indicator, content), initialY + 186d, 1d, "向下完成指示条位置");
                EnsureSettledContent(host, StimulusKind.TPcs, "向下完成内容");
                Capture(
                    content,
                    Path.Combine(outputDirectory, "StimulusConfigurationPage.forward-complete.png")
                );

                foreach (var duty in new[] { 1d, 99d })
                {
                    page.CurrentConfiguration.DutyPercent = duty;
                    await Task.Delay(80);
                    Capture(
                        content,
                        Path.Combine(outputDirectory, $"StimulusConfigurationPage.pulse-{duty}.png")
                    );
                }
                page.CurrentConfiguration.DutyPercent = 79;

                page.Modes[1].SelectCommand.Execute(null);
                await Task.Delay(120);
                var reverseIndicatorY = GetY(indicator, content);
                EnsureBetween(reverseIndicatorY, initialY + 62d, initialY + 186d, "向上指示条动画");
                EnsureContentDirections(
                    host,
                    StimulusKind.TPcs,
                    1,
                    StimulusKind.TAcs,
                    -1,
                    "向上内容动画"
                );
                Capture(
                    content,
                    Path.Combine(outputDirectory, "StimulusConfigurationPage.reverse-mid.png")
                );

                await Task.Delay(220);
                EnsureNear(GetY(indicator, content), initialY + 62d, 1d, "向上完成指示条位置");
                EnsureSettledContent(host, StimulusKind.TAcs, "向上完成内容");
                Capture(
                    content,
                    Path.Combine(outputDirectory, "StimulusConfigurationPage.reverse-complete.png")
                );

                window.Width = 1200;
                window.Height = 760;
                await Task.Delay(150);
                if (!host.ClipToBounds || host.Bounds.Width <= 0 || host.Bounds.Height <= 0)
                    throw new InvalidOperationException("最小窗口尺寸下内容过渡区域布局异常。");
                Capture(
                    content,
                    Path.Combine(outputDirectory, "StimulusConfigurationPage.minimum-size.png")
                );

                var disabledPage = CreatePage(animationsEnabled: false);
                var disabledContent = new StimulusConfigurationPageView
                {
                    Background = new SolidColorBrush(Color.Parse("#F2F4FA")),
                    DataContext = disabledPage,
                };
                window.Content = disabledContent;
                await Task.Delay(100);
                var disabledIndicator =
                    disabledContent.FindControl<Border>("ModeSelectionIndicator")
                    ?? throw new InvalidOperationException("未找到关闭动画后的共享指示条。");
                var disabledHost =
                    disabledContent.FindControl<TransitioningContentControl>("ModeContentHost")
                    ?? throw new InvalidOperationException("未找到关闭动画后的内容容器。");
                var disabledInitialY = GetY(disabledIndicator, disabledContent);
                disabledPage.Modes[4].SelectCommand.Execute(null);
                await Task.Delay(100);
                EnsureNear(
                    GetY(disabledIndicator, disabledContent),
                    disabledInitialY + 248d,
                    1d,
                    "关闭动画后的指示条位置"
                );
                EnsureSettledContent(disabledHost, StimulusKind.Sham, "关闭动画后的内容");
                Capture(
                    disabledContent,
                    Path.Combine(
                        outputDirectory,
                        "StimulusConfigurationPage.animations-disabled.png"
                    )
                );

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
                window.Close();
                desktop.Shutdown(Environment.ExitCode);
            }
        };
    }

    private static StimulusConfigurationPageViewModel CreatePage(
        bool animationsEnabled,
        StimulusCapabilityProfile? capability = null
    ) =>
        new(
            new StimulusConfigurationRouteData("EXP-20260904-009", "65888305"),
            new VisualTestNavigationRouter(),
            new DialogService(() => null),
            new VisualDialogProvider(),
            capability: capability,
            applicationOptions: new ApplicationBehaviorOptions
            {
                NavigationAnimationsEnabled = animationsEnabled,
            }
        );

    private static void EnsureContentDirections(
        TransitioningContentControl host,
        StimulusKind outgoingKind,
        int outgoingDirection,
        StimulusKind incomingKind,
        int incomingDirection,
        string stage
    )
    {
        var scrolls = GetModeScrollViewers(host);
        var outgoing = scrolls.SingleOrDefault(scroll =>
            scroll.DataContext is StimulusModeConfigurationViewModel { Kind: var kind }
            && kind == outgoingKind
        );
        var incoming = scrolls.SingleOrDefault(scroll =>
            scroll.DataContext is StimulusModeConfigurationViewModel { Kind: var kind }
            && kind == incomingKind
        );
        if (outgoing is null || incoming is null)
            throw new InvalidOperationException($"{stage}未同时保留新旧内容。");

        var outgoingY = GetY(outgoing, host);
        var incomingY = GetY(incoming, host);
        if (Math.Sign(outgoingY) != outgoingDirection || Math.Sign(incomingY) != incomingDirection)
        {
            throw new InvalidOperationException(
                $"{stage}方向异常：旧内容 Y={outgoingY:F2}，新内容 Y={incomingY:F2}。"
            );
        }
    }

    private static void EnsureSettledContent(
        TransitioningContentControl host,
        StimulusKind expectedKind,
        string stage
    )
    {
        var scrolls = GetModeScrollViewers(host);
        if (
            scrolls.Count != 1
            || scrolls[0].DataContext is not StimulusModeConfigurationViewModel configuration
            || configuration.Kind != expectedKind
        )
        {
            var kinds = string.Join(
                ", ",
                scrolls.Select(scroll =>
                    scroll.DataContext is StimulusModeConfigurationViewModel item
                        ? item.Kind.ToString()
                        : scroll.DataContext?.GetType().Name ?? "null"
                )
            );
            throw new InvalidOperationException(
                $"{stage}结束后未仅保留目标内容：数量 {scrolls.Count}，内容 [{kinds}]。"
            );
        }

        EnsureNear(GetY(scrolls[0], host), 0d, 1d, $"{stage}位置");
        EnsureNear(scrolls[0].Offset.Y, 0d, 0.1d, $"{stage}滚动位置");
    }

    private static List<ScrollViewer> GetModeScrollViewers(TransitioningContentControl host) =>
        host.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Where(scroll => scroll.Classes.Contains("mode-content-scroll"))
            .ToList();

    private static double GetY(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(default, relativeTo)?.Y
        ?? throw new InvalidOperationException("无法计算动画元素位置。");

    private static void EnsureBetween(double actual, double first, double second, string stage)
    {
        var minimum = Math.Min(first, second);
        var maximum = Math.Max(first, second);
        if (actual <= minimum + 0.5d || actual >= maximum - 0.5d)
            throw new InvalidOperationException(
                $"{stage}不是中间帧：Y={actual:F2}，范围 {minimum:F2}..{maximum:F2}。"
            );
    }

    private static void EnsureNear(double actual, double expected, double tolerance, string stage)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException(
                $"{stage}异常：实际 {actual:F2}，预期 {expected:F2}±{tolerance:F2}。"
            );
    }

    private static void EnsureNumericInputGeometry(Control content, string stage)
    {
        var inputs = content.GetVisualDescendants().OfType<StimulusNumericInput>().ToArray();
        if (inputs.Length < 10)
            throw new InvalidOperationException($"{stage}未显示完整的 HD 通道输入框：{inputs.Length} 个。");

        var channelInputs = inputs.Where(input => input.ShowButtonSpinner).ToArray();
        if (channelInputs.Length != 8)
            throw new InvalidOperationException($"{stage}应显示 8 个带步进按钮的物理通道输入框，实际 {channelInputs.Length} 个。");

        foreach (var input in inputs)
        {
            var expectedWidth = input.ShowButtonSpinner ? 120d : 72d;
            EnsureNear(input.Bounds.Width, expectedWidth, 0.5d, $"{stage}输入框宽度");
            EnsureNear(input.Bounds.Height, 32d, 0.5d, $"{stage}输入框高度");
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
