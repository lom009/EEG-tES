using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.VisualTree;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed partial class SpeechExperimentVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var router = new VisualTestNavigationRouter();
        var d = ExperimentRunRouteDataDefaults.Create();
        var config = d.StimulusConfiguration with
        {
            Kind = StimulusKind.EnvelopeTAcs,
            Targets = d
                .StimulusConfiguration.Targets.Take(1)
                .Select(x => x with { PeakCurrent = .5 })
                .ToArray(),
            Envelope = new EnvelopeParameters(1, 1, 40, false),
        };
        var route = new EnvelopeStimulationRouteData(
            new(
                "DEMO-EXP-001",
                "DEMO-001",
                config,
                d.StimulusElectrodes,
                [],
                "",
                "",
                1000,
                creationMode: ExperimentCreationMode.StimulusOnly
            ),
            "已通过"
        );
        var snapshot = Fixture(SpeechExperimentSource.From(route.Configuration));
        var model = new SpeechExperimentResultsPageViewModel(new(snapshot.Id, snapshot));
        var shell = new BasicViewModel(router, model, new DeviceSelectionContext(), false);
        var content = new BasicView { DataContext = shell };
        var window = new Window
        {
            Width = 1440,
            Height = 900,
            WindowDecorations = WindowDecorations.None,
            Content = new LayoutTransformControl { Child = content },
            ShowInTaskbar = false,
            Title = "言语实验视觉验收",
        };
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Opened += async (_, _) =>
        {
            var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
            Directory.CreateDirectory(output);
            try
            {
                await model.LoadTask;
                await VerifyComparisonSelection(window, snapshot);
                model.Rounds[0].Selected = true;
                model.Rounds[1].Selected = true;
                model.Rounds[2].Selected = true;
                for (var tab = 0; tab < 3; tab++)
                {
                    model.SelectedTab = tab;
                    if (tab == 2)
                        model.SelectedRound = model.Rounds[0];
                    await Task.Delay(250);
                    if (tab == 1)
                        await VerifyComparisonTooltip(content, output);
                    await CaptureLayouts(window, content, output, $"speech-result-{tab}");
                    if (tab == 0)
                        File.WriteAllLines(
                            Path.Combine(output, "checkboxes.txt"),
                            content
                                .GetVisualDescendants()
                                .OfType<CheckBox>()
                                .Select(c =>
                                    $"{c.DataContext?.GetType().Name}: {(c.DataContext as SpeechRoundRow)?.Number} selected={(c.DataContext as SpeechRoundRow)?.Selected} checked={c.IsChecked} enabled={c.IsEnabled} template={c.Template} theme={c.Theme}"
                                )
                        );
                }
                model.SelectedRound = model.Rounds[5];
                await Task.Delay(200);
                await CaptureLayouts(window, content, output, "speech-result-adaptive");
                shell.CurrentPage = new SpeechExperimentResultsPageViewModel(
                    new(Guid.NewGuid(), snapshot with { Rounds = [] })
                );
                await Task.Delay(250);
                await CaptureLayouts(window, content, output, "speech-result-empty");
                using var training = new EnvelopeStimulationPageViewModel(
                    route,
                    router,
                    () => new(new TinyCorpus(), new ControllableSpeechAudioPlayer())
                );
                shell.CurrentPage = training;
                await training.StartCommand.ExecuteAsync(null);
                training.AllCorrectCommand.Execute(null);
                await training.SaveNextCommand.ExecuteAsync(null);
                await Task.Delay(250);
                if (!training.IsEnded || !training.AgainCommand.CanExecute(null))
                    throw new InvalidOperationException("训练完成状态错误。");
                await CaptureLayouts(window, content, output, "speech-round-completed");
                if (
                    content
                        .GetVisualDescendants()
                        .OfType<Control>()
                        .Any(x => x.Name == "DebugFrequencySettingsSection")
                )
                    throw new InvalidOperationException("调试频率控件未移除。");
                await training.AgainCommand.ExecuteAsync(null);
                if (!training.CanConfigure)
                    throw new InvalidOperationException("下一轮参数未解锁。");
                await Task.Delay(200);
                await CaptureLayouts(window, content, output, "speech-round-again");
                await VerifyTrainingNavigation(window, content, shell, training, router, output);
                var dialog = new SpeechEndDialogView
                {
                    DataContext = new SpeechEndDialogViewModel(2, true),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                var overlay = new Grid { Background = new SolidColorBrush(Color.Parse("#EEEEF4")) };
                overlay.Children.Add(dialog);
                window.Content = new LayoutTransformControl { Child = overlay };
                await Task.Delay(200);
                await CaptureLayouts(window, overlay, output, "speech-end-dialog");
                File.WriteAllText(
                    Path.Combine(output, "speech-experiment.passed.txt"),
                    "Overview, comparison, fixed/adaptive detail, empty, small, 150% layout scaling, completed/again and dialog verified."
                );
                desktop.Shutdown(0);
            }
            catch (Exception e)
            {
                File.WriteAllText(VisualTestOptions.Current.ErrorPath, e.ToString());
                desktop.Shutdown(1);
            }
        };
        window.Show();
    }

    private static async Task VerifyComparisonTooltip(Control content, string output)
    {
        var chart = content.GetVisualDescendants()
            .OfType<EGGtCSPlatform.Controls.SpeechComparisonChart>().Single();
        var root = TopLevel.GetTopLevel(chart)!;
        var rows = chart.Rows;
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        void Move(Point point, bool exit = false)
        {
            chart.RaiseEvent(new PointerEventArgs(
                exit ? InputElement.PointerExitedEvent : InputElement.PointerMovedEvent,
                chart, pointer, root, chart.TranslatePoint(point, root)!.Value, 0,
                new PointerPointProperties(), KeyModifiers.None));
        }
        for (var i = 0; i < rows.Count; i++)
        {
            Move(new Point(54 + (chart.Bounds.Width - 78) * (i + .5) / rows.Count,
                24 + (chart.Bounds.Height - 60) * (100 - rows[i].Value!.Value) / 100));
            await Task.Delay(80);
            var tip = (ToolTip)ToolTip.GetTip(chart)!;
            var texts = tip.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToArray();
            if (!ToolTip.GetIsOpen(chart) || texts.Length != 3
                || texts[0] != $"第{rows[i].Round.Number}次"
                || texts[1] != $"msp·01 {rows[i].Round.DelayMilliseconds:0}ms"
                || texts[2] != rows[i].Value!.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%")
                throw new InvalidOperationException("对比训练悬浮内容未匹配当前数据点。");
            if (i == 0)
                Capture(tip, output, "speech-comparison-tooltip", 2);
        }
        Move(default, exit: true);
        if (ToolTip.GetIsOpen(chart))
            throw new InvalidOperationException("离开图表后提示未关闭。");
        chart.SetCurrentValue(EGGtCSPlatform.Controls.SpeechComparisonChart.RowsProperty, Array.Empty<SpeechRoundRow>());
        Move(default);
        if (ToolTip.GetIsOpen(chart))
            throw new InvalidOperationException("清空数据后保留了旧提示。");
        chart.SetCurrentValue(EGGtCSPlatform.Controls.SpeechComparisonChart.RowsProperty, rows);
        await Task.Delay(80);
    }

    private static async Task VerifyComparisonSelection(Window window, SpeechExperimentResult snapshot)
    {
        var original = window.Content;
        var rows = snapshot.Rounds.Take(2).Select((r, i) => r with
        {
            DelayMilliseconds = i == 0 ? 100 : 165,
        }).ToArray();
        var model = new SpeechExperimentResultsPageViewModel(new(snapshot.Id, snapshot with { Rounds = rows }));
        await model.LoadTask;
        var view = new SpeechExperimentResultsPageView { DataContext = model };
        window.Content = view;
        await Task.Delay(200);
        try
        {
            // Invoke the real control click path (toggle, binding and command), not a model assignment.
            void Click(int index)
            {
                var checkbox = view.GetVisualDescendants().OfType<CheckBox>()
                    .Single(c => ReferenceEquals(c.DataContext, model.Rounds[index]));
                if (!checkbox.IsEffectivelyEnabled)
                    return;
                typeof(CheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(checkbox, null);
            }
            void Verify(int count, bool rejected)
            {
                foreach (var checkbox in view.GetVisualDescendants().OfType<CheckBox>()
                    .Where(c => c.DataContext is SpeechRoundRow))
                {
                    var row = (SpeechRoundRow)checkbox.DataContext!;
                    var expectedEnabled = row.Selected || model.SelectedCount == 0 || model.DifferentDelay;
                    if (checkbox.IsEffectivelyEnabled != expectedEnabled || row.CanToggle != expectedEnabled
                        || row.ToggleCommand.CanExecute(null) != expectedEnabled)
                        throw new InvalidOperationException("Incompatible checkbox availability mismatch.");
                    if (checkbox.IsChecked != row.Selected)
                        throw new InvalidOperationException($"Checkbox mismatch: {row.Number}, checked={checkbox.IsChecked}, selected={row.Selected}");
                }
                if (model.SelectedCount != count || model.SelectionText != $"已选 {count} 次训练"
                    || model.CompareSelectedCommand.CanExecute(null) != (count >= 2)
                    || (rejected ? !model.StatusText.Contains("延时不同") : model.HasStatus))
                    throw new InvalidOperationException("Comparison selection count, command or hint mismatch.");
            }
            foreach (var first in new[] { 0, 1 })
            {
                var second = 1 - first;
                Click(first);
                Verify(1, false);
                Click(second);
                Verify(1, false);
                Click(first);
                Verify(0, false);
                Click(first);
                Verify(1, false);
                Click(second);
                Verify(1, false);
                model.DifferentDelay = true;
                Click(second);
                Verify(2, false);
                if (model.ChartRows.Count != 2)
                    throw new InvalidOperationException("Different-delay chart missing.");
                model.Rounds[second].ToggleCommand.Execute(null);
                Verify(1, false);
                Click(first);
                Verify(0, false);
                model.DifferentDelay = false;
            }
        }
        finally
        {
            window.Content = original;
        }
    }

    private static async Task CaptureLayouts(
        Window window,
        Control view,
        string directory,
        string name
    )
    {
        var host = (LayoutTransformControl)window.Content!;
        host.LayoutTransform = new ScaleTransform(1, 1);
        await Task.Delay(150);
        Capture(host, directory, name);
        window.Width = 1024;
        window.Height = 720;
        await Task.Delay(150);
        Capture(host, directory, name + "-small");
        window.Width = 1440;
        window.Height = 900;
        host.LayoutTransform = new ScaleTransform(1.5, 1.5);
        await Task.Delay(150);
        Capture(host, directory, name + "-150percent");
        if (
            view.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault(x => x.Name == "ResultPageScroll") is
            { } scroll
        )
        {
            var offset = scroll.Offset;
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            await Task.Delay(150);
            Capture(host, directory, name + "-150percent-bottom");
            scroll.Offset = offset;
        }
        host.LayoutTransform = new ScaleTransform(1, 1);
        await Task.Delay(150);
    }

    private static void Capture(Control view, string directory, string name, double scale = 1)
    {
        using var image = new RenderTargetBitmap(
            new PixelSize(
                (int)Math.Ceiling(view.Bounds.Width * scale),
                (int)Math.Ceiling(view.Bounds.Height * scale)
            ),
            new Vector(96 * scale, 96 * scale)
        );
        image.Render(view);
        image.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private sealed class TinyCorpus : ISpeechCorpusProvider
    {
        private readonly FileSpeechCorpusProvider _source = new();
        public IReadOnlyList<SpeechCorpus> Corpora =>
            [
                _source.Corpora[0] with
                {
                    Sentences = _source.Corpora[0].Sentences.Take(1).ToArray(),
                },
            ];

        public AudioBuffer ReadAudio(SpeechSentence sentence) => _source.ReadAudio(sentence);
    }

    private static SpeechExperimentResult Fixture(SpeechExperimentSource source)
    {
        var start = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(8));
        var rows = Enumerable
            .Range(1, 7)
            .Select(i => new SpeechTrainingRound(
                Guid.NewGuid(),
                i,
                40,
                start.AddMinutes(i * 12),
                i == 4 ? SpeechRoundStatus.Interrupted : SpeechRoundStatus.Completed,
                new(
                    new(
                        "MSP",
                        "01",
                        SpeechRate: i > 5 ? 0 : 4,
                        AudioSettings: new() { SpeechRate = i > 5 ? 0 : 4 },
                        TrainingMode: i > 5
                            ? SpeechTrainingMode.AdaptiveRate
                            : SpeechTrainingMode.FixedRate
                    ),
                    start.AddMinutes((i - 1) * 12),
                    20,
                    Enumerable
                        .Range(1, i == 4 ? 10 : 20)
                        .Select(n => new EGGtCSPlatform.SpeechTraining.SpeechSentenceResult(
                            $"s{n}",
                            n,
                            "今天的阳光真好",
                            5 + (i % 3),
                            7
                        ))
                        .ToArray(),
                    i > 5
                        ? new AdaptiveSpeechSummary("测试完成", i == 6 ? 4.25 : 4.75, false)
                        : null,
                    []
                )
            ))
            .ToArray();
        return new(
            Guid.NewGuid(),
            source,
            start,
            start.AddHours(2),
            ExperimentRunStatus.Completed,
            rows
        );
    }
}
