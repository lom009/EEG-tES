using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views;

namespace EGGtCSPlatform.VisualTests;

public sealed class EnvelopeTrainingVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var directory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(directory);
        var router = new VisualTestNavigationRouter();
        var source = ExperimentRunRouteDataDefaults.Create();
        var route = new ExperimentRunRouteData("EXP-20261008-001", "PAT-001",
            source.StimulusConfiguration with { Kind = StimulusKind.EnvelopeTAcs, Envelope = new(DelayMilliseconds: 40) },
            source.StimulusElectrodes, [], "", "", 500, creationMode: ExperimentCreationMode.StimulusOnly);
        var device = new SimulatedEggtCsDevice();
        var manager = new DeviceManager(connectedDevices: [device]);
        var page = new EnvelopeStimulationPageViewModel(new(route, "刺激阻抗检测已完成"), router,
            new EnvelopeTrainingExecutionService(manager), new EnvelopeTrainingResultStore(Path.Combine(directory, "records")));
        var connection = new DeviceSelectionContext();
        connection.SetConnectedDevice(DeviceId.Simulator.Value, "Simulator");
        var content = new BasicView { DataContext = new BasicViewModel(router, page, connection, false) };
        var window = new Window { Width = 1440, Height = 900, WindowDecorations = WindowDecorations.None, Content = content };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                File.Delete(VisualTestOptions.Current.ErrorPath);
                var prompts = new List<string>();
                await Task.Delay(200); Capture(content, directory, "01-ready");
                VerifyInstruction(content, page, "先进行题目音频播放并刺激", prompts);
                window.Width = 1200; window.Height = 760;
                await Task.Delay(150); Capture(content, directory, "01a-ready-small");
                window.Width = 1440; window.Height = 900;
                await Task.Delay(150);
                File.WriteAllText(Path.Combine(directory, "controls-ready.txt"), string.Join("\n",
                    content.GetVisualDescendants().OfType<Button>().Where(button => button.DataContext is EnvelopeTrainingCharacterViewModel)
                        .Select(button => $"{button.Classes} enabled={button.IsEffectivelyEnabled} opacity={button.Opacity} command={button.Command?.CanExecute(null)}")));
                var scoreButtons = content.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.DataContext is EnvelopeTrainingCharacterViewModel && button.Command is not null).ToArray();
                if (scoreButtons.Length != page.Characters.Count * 2 || scoreButtons.Any(button => button.IsEffectivelyEnabled))
                    throw new InvalidOperationException("Ready-state score buttons are not bound or disabled.");
                var startButton = content.GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.Command, page.PlayAndStimulateCommand));
                ((IInvokeProvider)new ButtonAutomationPeer(startButton)).Invoke();
                await Task.Delay(20);
                var execution = page.PlayAndStimulateCommand.ExecutionTask
                    ?? throw new InvalidOperationException("Native start button did not execute its command.");
                await Task.Delay(1000); Capture(content, directory, "02-running");
                VerifyInstruction(content, page, "请等待音频和刺激结束", prompts);
                if (!page.IsExecuting || page.CanScore) throw new InvalidOperationException("Running gate failed.");
                await execution;
                if (!page.CanScore) throw new InvalidOperationException("Audio/device completion failed: " + page.StatusMessage);
                VerifyInstruction(content, page, "等待回答与评分后，可保存并进入下一句", prompts);
                await Task.Delay(150); Capture(content, directory, "03a-awaiting-answer");
                page.Characters[0].CorrectCommand.Execute(null);
                await Task.Delay(150); Capture(content, directory, "03b-partial-score");
                if (page.CanSave) throw new InvalidOperationException("Partial scores unlocked saving.");
                page.SetAllCorrectCommand.Execute(null); page.Characters[1].IncorrectCommand.Execute(null);
                await Task.Delay(150); Capture(content, directory, "03-scoring");
                var replay = page.ReplayAudioCommand.ExecuteAsync(null);
                await Task.Delay(150);
                VerifyInstruction(content, page, "正在重复播放题目音频，请等待播放结束", prompts);
                Capture(content, directory, "03c-replaying");
                await replay;
                VerifyInstruction(content, page, "等待回答与评分后，可保存并进入下一句", prompts);
                if (page.Characters[1].IsCorrect != false) throw new InvalidOperationException("Replay reset score.");
                await page.SaveTrialCommand.ExecuteAsync(null);
                if (!page.CanAdvance) throw new InvalidOperationException("Persistence gate failed: " + page.StatusMessage);
                await Task.Delay(150); Capture(content, directory, "04-saved");
                VerifyInstruction(content, page, "本句已保存，可进入下一句", prompts);
                if (page.StatusMessageIsError) throw new InvalidOperationException("Save success used an error style.");
                window.Width = 1200; window.Height = 760;
                await Task.Delay(150); Capture(content, directory, "04a-saved-small");
                var instruction = content.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "TrainingInstructionText");
                var leftScroll = instruction.GetVisualAncestors().OfType<ScrollViewer>().First();
                leftScroll.Offset = new Vector(0, leftScroll.Extent.Height);
                await Task.Delay(150); Capture(content, directory, "04b-saved-small-scrolled");
                leftScroll.Offset = new Vector(0, 0);
                window.Width = 1440; window.Height = 900; await Task.Delay(150);
                page.NextSentenceCommand.Execute(null);
                VerifyInstruction(content, page, "先进行题目音频播放并刺激", prompts);
                page.CharactersPerSecond = 3;
                await page.PlayAndStimulateCommand.ExecuteAsync(null);
                page.SetAllCorrectCommand.Execute(null);
                await page.SaveTrialCommand.ExecuteAsync(null);
                page.NextSentenceCommand.Execute(null);
                page.CharactersPerSecond = 5;
                var interrupted = page.PlayAndStimulateCommand.ExecuteAsync(null);
                await Task.Delay(300); page.StopCommand.Execute(null); await interrupted;
                if (page.CanScore || page.SavedTrials.Count != 2) throw new InvalidOperationException("Emergency stop gate failed.");
                VerifyInstruction(content, page, "本句已中断，请重新播放并刺激", prompts);
                File.WriteAllLines(Path.Combine(directory, "instruction-transitions.txt"), prompts);
                page.RequestEndCommand.Execute(null);
                await Task.Delay(150); Capture(content, directory, "05-end-confirmation");
                await page.ConfirmEndCommand.ExecuteAsync(null);
                if (!page.IsResults || page.SavedTrials.Count != 2) throw new InvalidOperationException("Results included unsaved trial.");
                await Task.Delay(200); Capture(content, directory, "06-results");
                if (page.ResultScoredText != "15/15" || page.ResultCorrectCountText != "14" || page.ResultAccuracyText != "93%"
                    || page.ResultSpeedText != "2–3 字/s（逐句调整）") throw new InvalidOperationException("Saved result totals or speed summary are inconsistent.");
                var detailButton = content.GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.CommandParameter, page.SavedTrials[0]));
                ((IInvokeProvider)new ButtonAutomationPeer(detailButton)).Invoke();
                await Task.Delay(150); Capture(content, directory, "07-detail");
                if (!page.IsDetailOpen || page.PreviousDetailCommand.CanExecute(null) || !page.NextDetailCommand.CanExecute(null))
                    throw new InvalidOperationException("Detail opening/navigation failed.");
                page.NextDetailCommand.Execute(null);
                await Task.Delay(150); Capture(content, directory, "07a-detail-second");
                if (page.DetailCharacters.Count != 8 || page.NextDetailCommand.CanExecute(null))
                    throw new InvalidOperationException("Second sentence detail failed.");
                page.CloseDetailCommand.Execute(null);
                window.Width = 1200; window.Height = 760;
                await Task.Delay(150); Capture(content, directory, "08-results-small");
                using var emptyPage = new EnvelopeStimulationPageViewModel(new(route, "刺激阻抗检测已完成"), router,
                    null, new EnvelopeTrainingResultStore(Path.Combine(directory, "empty-records")));
                await emptyPage.ConfirmEndCommand.ExecuteAsync(null);
                if (!emptyPage.IsResults || emptyPage.HasSavedTrials || emptyPage.ResultScoredText != "0/0")
                    throw new InvalidOperationException("Empty results included an unsaved score.");
                content.DataContext = new BasicViewModel(router, emptyPage, connection, false);
                window.Width = 1440; window.Height = 900;
                await Task.Delay(150); Capture(content, directory, "09-results-empty");
                File.WriteAllText(Path.Combine(directory, "integration-passed.txt"),
                    "Native audio playback + SDK envelope simulator completion; audio-only replay; atomic save; emergency stop; saved-trial result totals/speeds; detail button/navigation; responsive results; empty results passed.");
                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            { File.WriteAllText(VisualTestOptions.Current.ErrorPath, exception.ToString()); Environment.ExitCode = 1; }
            finally { page.Dispose(); await manager.DisposeAsync(); desktop.Shutdown(Environment.ExitCode); }
        };
        window.Show();
    }
    private static void VerifyInstruction(Control content, EnvelopeStimulationPageViewModel page, string expected, List<string> prompts)
    {
        var label = content.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "TrainingInstructionText");
        if (page.Instruction != expected || label.Text != expected)
            throw new InvalidOperationException($"Instruction mismatch: {page.Instruction} / {label.Text}; expected {expected}.");
        prompts.Add($"{page.Stage} executing={page.IsExecuting} score={page.CanScore} save={page.CanSave} next={page.CanAdvance}: {label.Text}");
    }
    private static void Capture(Control control, string directory, string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height), new Vector(96, 96));
        bitmap.Render(control); bitmap.Save(Path.Combine(directory, name + ".png"));
    }
}
