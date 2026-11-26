using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed class PictureNamingVisualApp : App
{
    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var directory = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(directory);
        try
        {
            await Run(desktop, directory).WaitAsync(VisualTestOptions.Current.Timeout);
            File.WriteAllText(Path.Combine(directory, "passed.txt"),
                "PASS: single-stimulation setup, tDCS selection, electrode confirmation cancellation and acceptance, S1 ready, S2 fixation, S3 answer, S4 interference, S5 score and correct, S6 finish and restart, S7 review completed and interrupted rounds, return and ordinary single-stimulation route.");
            Environment.ExitCode = 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "error.txt"), error.ToString());
            Environment.ExitCode = 1;
        }
        desktop.Shutdown(Environment.ExitCode);
    }

    private static async Task Run(IClassicDesktopStyleApplicationLifetime desktop, string directory)
    {
        var window = new Window { Width = 1440, Height = 900, Title = "图片命名 · 模拟设备验收" };
        var connection = new DeviceSelectionContext();
        connection.SetConnectedDevice(DeviceId.Simulator.Value, "验收模拟器");
        var dialogs = new DialogService(() => window);
        var capability = StimulusCapabilityProfile.FromOptions(new StimulationChannelOptions { FixedActivePhysicalChannelIds = [1, 8] });
        var ordinary = new OrdinaryDialog();
        MainViewModel main = null!;
        main = new MainViewModel(new PageFactory((page, data) => page switch {
            ApplicationPageNames.StimulusConfiguration => new StimulusConfigurationPageViewModel((StimulusConfigurationRouteData)data!, main, dialogs, main, capability, connection),
            ApplicationPageNames.ElectrodeConfiguration => new ElectrodeConfigurationPageViewModel((ElectrodeConfigurationRouteData)data!, main, dialogs, main, capability, singleStimulusDialog: ordinary),
            ApplicationPageNames.PictureNaming => new PictureNamingPageViewModel((ExperimentRunRouteData)data!),
            _ => throw new InvalidOperationException(page.ToString())
        }), connection);
        main.Navigate(new StimulusConfigurationRouteData("QA-TDCS", "S-001", CreationMode: ExperimentCreationMode.StimulusOnly));
        var shell = (BasicViewModel)main.CurrentView;
        var root = new MainView { DataContext = main };
        window.Content = root;
        desktop.MainWindow = window;
        window.Show();
        await Task.Delay(450);
        var setup = (StimulusConfigurationPageViewModel)shell.CurrentPage;
        await Click(setup.Modes.Single(x => x.Kind == StimulusKind.TDcs).SelectCommand);
        await Click(setup.SaveConfigurationCommand);
        var electrodes = (ElectrodeConfigurationPageViewModel)shell.CurrentPage;
        var points = new Queue<ElectrodeSiteViewModel>(electrodes.Points.Where(p => p.CanStimulate).Take(electrodes.RequiredStimulusElectrodeCount));
        foreach (var option in electrodes.StimulusSelectionOptions)
        {
            electrodes.SelectStimulusSelectionOptionCommand.Execute(option);
            for (var i = 0; i < option.RequiredCount; i++) electrodes.SelectPointCommand.Execute(points.Dequeue());
        }
        var channels = new HashSet<int>();
        foreach (var row in electrodes.CurrentImpedanceItems)
            row.SelectedStimulationChannel = row.StimulationChannels.First(c => channels.Add(c.PhysicalChannelId));
        electrodes.ApplyImpedanceReadings(ElectrodeConfigurationMode.Stimulus,
            electrodes.Points.Where(p => p.StimulationChannelRole == StimulationChannelRole.Selectable).ToDictionary(p => p.Name, _ => 8d));
        var choice = root.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == "PictureNamingOption");
        Invoke(choice);
        await Task.Delay(100);
        if (!electrodes.UsePictureNaming) throw new Exception("入口勾选未生效");
        Capture("entry");
        await Click(electrodes.OpenConfirmationCommand);
        await Click(main.DialogStack.OfType<ExperimentConfigurationDialogViewModel>().Single().CancelCommand);
        if (shell.CurrentPage != electrodes) throw new Exception("取消后误入实验");
        await Click(electrodes.OpenConfirmationCommand);
        await Click(main.DialogStack.OfType<ExperimentConfigurationDialogViewModel>().Single().ConfirmCommand);
        var naming = (PictureNamingPageViewModel)shell.CurrentPage;
        await Task.Delay(700);
        if (!root.GetVisualDescendants().OfType<PictureNamingPageView>().Any(v => v.IsEffectivelyVisible)) throw new Exception("图片命名页未呈现");
        var materials = root.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "MaterialsBox");
        Check(naming.ShowIntro && materials.IsEffectivelyEnabled, "S1 可选择材料");
        Check(naming.RecordSpeech == naming.SpeechAvailable, "S1 检测到模型时默认开启本地语音识别");
        naming.RecordSpeech = false;
        materials.SelectedIndex = 1;
        Check(naming.Target == "剪刀", "切换第二套材料");
        materials.SelectedIndex = 0;
        Capture("S1-ready");
        naming.RampSeconds = 1;
        naming.FixationSeconds = 1.5;
        naming.AnswerSeconds = 1.5;
        await Click(naming.AdvanceCommand);
        Check(naming.IsRamping && !materials.IsEffectivelyEnabled && !naming.CanAct, "S1b 刺激缓升中");
        Capture("S1b-ramp");
        for (int n = 0; n < 60 && !naming.IsFixation; n++) await Task.Delay(40);
        Check(naming.IsFixation && !materials.IsEffectivelyEnabled && !naming.CanScore, "S2 注视状态");
        CheckFixationControls();
        Capture("S2-fixation");
        for (int n = 0; n < 100 && !naming.IsAnswering; n++) await Task.Delay(40);
        Check(naming.IsAnswering && !naming.ShowDistractors && !naming.CanAct, "S3 回答期间禁止前进");
        await Task.Delay(180);
        var prompt = root.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text == "请说出图片名称" && x.IsEffectivelyVisible);
        Check(prompt.Bounds.Height > 0 && naming.IsAnswering, "S3 图片提示完成布局");
        Check(!HasNumberCards(), "回答页仅呈现当前题");
        Capture("S3-answer");
        await naming.AdvanceCommand.ExecutionTask!;
        Check(naming.CanScore && !naming.CanAct, "待核对");
        Capture("review-before-score");
        await Click(naming.ScoreCommand, "错误");
        await Click(naming.ScoreCommand, "正确");
        Check(naming.IsCorrect && naming.Scored == 1 && naming.Wrong == 0, "S5 改判");
        Check(!HasNumberCards(), "评分页仅呈现当前题");
        Capture("S5-score");
        for (int trial = 2; trial <= 6; trial++)
        {
            await Click(naming.AdvanceCommand);
            if (trial == 5)
            {
                Check(naming.IsFixation && naming.Scored == 4, "第五张注视阶段保留前四题统计");
                CheckFixationControls();
                Capture("S2-fifth-fixation");
            }
            await naming.AdvanceCommand.ExecutionTask!;
            await Click(naming.ScoreCommand, trial == 3 ? "错误" : "正确");
        }
        Check(naming.IsFinished && naming.ShowPicture && naming.Correct == 5 && naming.Wrong == 1, "S6 完成统计");
        await Click(naming.ScoreCommand, "无反应");
        Check(naming.Unanswered == 1 && naming.Correct == 4, "S6 末题可改判");
        await Click(naming.ScoreCommand, "正确");
        Capture("S6-finished");
        await Click(naming.AdvanceCommand);
        Check(naming.Scored == 0 && naming.Rounds.Single().Scored == 6, "重开保留上一轮");
        await Click(naming.EndCommand);
        Check(naming.IsDetail && naming.Rounds.Count == 2 && !naming.SelectedRound!.Complete, "S7 中途结束");
        var tabs = root.GetVisualDescendants().OfType<TabControl>().Single(x => x.IsEffectivelyVisible);
        foreach (var tabIndex in new[]{0,1,2}) { tabs.SelectedIndex = tabIndex; await Task.Delay(100); }
        var rounds = root.GetVisualDescendants().OfType<ComboBox>().Single(x => x.IsEffectivelyVisible);
        rounds.SelectedIndex = 0;
        await Task.Delay(200);
        Check(naming.SelectedRound!.Scored == 6 && naming.SelectedRound.Rows[2].Score == "错误", "S7 切换历史轮次");
        Capture("S7-detail");
        var rowDetail = root.GetVisualDescendants().OfType<ToggleButton>().First(x => x.Name == "TrialDetails" && x.IsEffectivelyVisible);
        Invoke(rowDetail);
        await Task.Delay(150);
        Check(root.GetVisualDescendants().OfType<TextBlock>().Any(x => x.IsEffectivelyVisible && x.Text == "人工判分"), "S7 展开逐题评分依据");
        main.GoBack();
        await Task.Delay(500);
        await Click(electrodes.OpenConfirmationCommand);
        await Click(main.DialogStack.OfType<ExperimentConfigurationDialogViewModel>().Single().ConfirmCommand);
        naming = (PictureNamingPageViewModel)shell.CurrentPage;
        await Task.Delay(500);
        naming.RampSeconds = 0;
        naming.FixationSeconds = 0;
        naming.AnswerSeconds = 2;
        Invoke(root.GetVisualDescendants().OfType<ToggleButton>().Single(x => x.Name == "InterferenceToggle" && x.IsEffectivelyVisible));
        await Click(naming.AdvanceCommand);
        Check(naming.IsAnswering && naming.ShowDistractors && !naming.CanScore, "S4 干扰回答");
        Capture("S4-interference");
        await Click(naming.EndCommand);
        main.GoBack();
        if (shell.CurrentPage != electrodes) throw new Exception("返回路径错误");

        await Task.Delay(500);
        Invoke(root.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == "PictureNamingOption"));
        await Click(electrodes.OpenConfirmationCommand);
        await Click(main.DialogStack.OfType<ExperimentConfigurationDialogViewModel>().Single().ConfirmCommand);
        if (ordinary.Count != 1 || shell.CurrentPage != electrodes) throw new Exception("常规单刺激入口回归");

        async Task Click(ICommand command, string? parameter = null)
        {
            var button = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Command == command && Equals(b.CommandParameter, parameter));
            Invoke(button);
            await Task.Delay(450);
        }
        void CheckFixationControls()
        {
            root.UpdateLayout();
            Check(!HasNumberCards(), "注视时无编号卡");
            Check(root.GetVisualDescendants().OfType<TextBlock>().Any(x => x.IsEffectivelyVisible && x.Text == "请注视屏幕中心点"), "注视提示可见");
            Check(!root.GetVisualDescendants().OfType<Button>().Any(x => x.IsEffectivelyVisible &&
                (x.Command == naming.ScoreCommand || x.Content is "✓ 正确" or "× 错误" or "↺ 重置")), "注视时无评分按钮");
        }
        bool HasNumberCards() => root.GetVisualDescendants().OfType<TextBlock>().Any(x =>
            x.IsEffectivelyVisible && x.Text is "01" or "02" or "03" or "04" or "05" or "06");

        static void Check(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException(scenario);
        }
        static void Invoke(Button button)
        {
            if (!button.IsEffectivelyEnabled) throw new Exception($"按钮禁用 {button.Name}");
            ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
        }
        void Capture(string name)
        {
            root.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96));
            bitmap.Render(root);
            using var file = File.Create(Path.Combine(directory, name + ".png"));
            bitmap.Save(file, PngBitmapEncoderOptions.Default);
        }
    }
    private sealed class OrdinaryDialog : ISingleStimulusExperimentDialogService
    {
        public int Count { get; private set; }
        public Task ShowAsync(ExperimentRunRouteData route) { Count++; return Task.CompletedTask; }
    }
}
