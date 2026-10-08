using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EnvelopeTrainingTests
{
    [Fact]
    public async Task InstructionsFollowExecutionReplaySavingAndNextSentenceGates()
    {
        var backend = new Backend();
        var store = new Store();
        using var page = new EnvelopeStimulationPageViewModel(Route(), null!, backend, store);
        Assert.Equal("先进行题目音频播放并刺激", page.Instruction);
        var execution = page.PlayAndStimulateCommand.ExecuteAsync(null);
        Assert.Equal("请等待音频和刺激结束", page.Instruction);
        Assert.False(page.CanSave);
        backend.Complete.TrySetResult(); await execution;
        Assert.Equal("等待回答与评分后，可保存并进入下一句", page.Instruction);
        page.Characters[0].CorrectCommand.Execute(null);
        Assert.False(page.CanSave);
        page.SetAllCorrectCommand.Execute(null);
        Assert.True(page.CanSave);
        backend.ReplayCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var replay = page.ReplayAudioCommand.ExecuteAsync(null);
        Assert.Equal("正在重复播放题目音频，请等待播放结束", page.Instruction);
        Assert.False(page.CanScore); Assert.False(page.CanSave);
        backend.ReplayCompletion.TrySetResult(); await replay;
        Assert.Equal("等待回答与评分后，可保存并进入下一句", page.Instruction);
        Assert.True(page.CanSave);
        store.SaveCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = page.SaveTrialCommand.ExecuteAsync(null);
        Assert.Equal("正在保存本句评分，请稍候", page.Instruction);
        Assert.False(page.CanAdvance); Assert.False(page.CanScore);
        store.SaveCompletion.TrySetResult(); await saving;
        Assert.Equal("本句已保存，可进入下一句", page.Instruction);
        Assert.True(page.CanAdvance);
        page.NextSentenceCommand.Execute(null);
        Assert.Equal("先进行题目音频播放并刺激", page.Instruction);
        Assert.False(page.CanScore); Assert.False(page.CanSave);
    }

    [Fact]
    public async Task ResultSummaryAndDetailUseSavedTrialsAndPreserveTheirSpeeds()
    {
        var backend = new Backend(); backend.Complete.TrySetResult();
        using var page = new EnvelopeStimulationPageViewModel(Route(), null!, backend, new Store());
        await page.PlayAndStimulateCommand.ExecuteAsync(null);
        page.SetAllCorrectCommand.Execute(null); page.Characters[1].IncorrectCommand.Execute(null);
        await page.SaveTrialCommand.ExecuteAsync(null);
        page.NextSentenceCommand.Execute(null); page.CharactersPerSecond = 3;
        await page.PlayAndStimulateCommand.ExecuteAsync(null); page.SetAllCorrectCommand.Execute(null);
        await page.SaveTrialCommand.ExecuteAsync(null);
        page.NextSentenceCommand.Execute(null); page.CharactersPerSecond = 5;
        await page.ConfirmEndCommand.ExecuteAsync(null);
        Assert.Equal("2/20", page.ResultProgressText);
        Assert.Equal("15/15", page.ResultScoredText);
        Assert.Equal("14", page.ResultCorrectCountText);
        Assert.Equal("93%", page.ResultAccuracyText);
        Assert.Equal("2–3 字/s（逐句调整）", page.ResultSpeedText);
        page.ShowDetailCommand.Execute(page.SavedTrials[0]);
        Assert.StartsWith("第 01/20 句", page.DetailTitle);
        Assert.False(page.PreviousDetailCommand.CanExecute(null));
        Assert.True(page.NextDetailCommand.CanExecute(null));
        page.NextDetailCommand.Execute(null);
        Assert.StartsWith("第 02/20 句", page.DetailTitle);
        Assert.Equal(8, page.DetailCharacters.Count);
        Assert.All(page.DetailCharacters, character => Assert.True(character.IsCorrect));
        Assert.False(page.NextDetailCommand.CanExecute(null));
        page.CloseDetailCommand.Execute(null);
        Assert.Single(page.SavedTrials[0].Characters.Where(character => !character.Correct));
    }

    [Fact]
    public void AudioEnvelopeComesFromFinalPcmAndNeverExceedsConfiguredCurrent()
    {
        var samples = Enumerable.Range(0, 24000).Select(i => (short)(Math.Sin(i * .1) * 20000)).ToArray();
        var audio = EnvelopeTrainingAudio.Create(samples, 24000, 1.8, 50);
        Assert.Equal(1, audio.Duration.TotalSeconds);
        Assert.Equal(50, audio.EnvelopeSamples.Length);
        Assert.All(audio.EnvelopeSamples, sample => Assert.InRange(sample * .033, 0, 1.8));
        Assert.True(audio.EnvelopeSamples.Any(sample => sample > 0));
        Assert.NotEmpty(audio.WaveSamples);
    }

    [Fact]
    public async Task IncompleteExecutionCannotBeScoredAndAudioReplayDoesNotRestimulate()
    {
        var backend = new Backend();
        var store = new Store();
        using var page = new EnvelopeStimulationPageViewModel(Route(), null!, backend, store);
        page.SetAllCorrectCommand.Execute(null);
        Assert.All(page.Characters, character => Assert.Null(character.IsCorrect));
        var running = page.PlayAndStimulateCommand.ExecuteAsync(null);
        Assert.True(page.IsExecuting);
        Assert.False(page.CanScore);
        backend.Complete.TrySetResult();
        await running;
        Assert.True(page.CanScore);
        page.SetAllCorrectCommand.Execute(null);
        await page.ReplayAudioCommand.ExecuteAsync(null);
        Assert.Equal(1, backend.StimulationCount);
        Assert.Equal(1, backend.ReplayCount);
        Assert.All(page.Characters, character => Assert.True(character.IsCorrect));
        await page.SaveTrialCommand.ExecuteAsync(null);
        Assert.True(page.CanAdvance);
        page.NextSentenceCommand.Execute(null);
        Assert.False(page.CanScore);
        Assert.All(page.Characters, character => Assert.Null(character.IsCorrect));
        Assert.Single(store.Saved.Trials);
    }

    [Fact]
    public async Task FailedExecutionAndFailedPersistenceNeverUnlockNextSentence()
    {
        var backend = new Backend { Fail = true };
        var store = new Store { Fail = true };
        using var page = new EnvelopeStimulationPageViewModel(Route(), null!, backend, store);
        await page.PlayAndStimulateCommand.ExecuteAsync(null);
        Assert.False(page.CanScore);
        Assert.Contains("失败", page.StatusMessage);
        backend.Fail = false;
        backend.Complete.TrySetResult();
        await page.PlayAndStimulateCommand.ExecuteAsync(null);
        page.SetAllCorrectCommand.Execute(null);
        await page.SaveTrialCommand.ExecuteAsync(null);
        Assert.False(page.CanAdvance);
        Assert.Empty(page.SavedTrials);
        Assert.Contains("保存失败", page.StatusMessage);
    }

    [Fact]
    public async Task EmergencyStopLeavesCurrentSentenceUnscoredAndResultsExcludeIt()
    {
        var backend = new Backend();
        var store = new Store();
        using var page = new EnvelopeStimulationPageViewModel(Route(), null!, backend, store);
        var execution = page.PlayAndStimulateCommand.ExecuteAsync(null);
        page.Characters[0].CorrectCommand.Execute(null);
        Assert.Null(page.Characters[0].IsCorrect);
        page.StopCommand.Execute(null);
        await execution;
        Assert.Equal(EnvelopeTrainingStage.Aborted, page.Stage);
        Assert.False(page.CanSave);
        await page.ConfirmEndCommand.ExecuteAsync(null);
        Assert.True(page.IsResults);
        Assert.Empty(page.SavedTrials);
        Assert.Equal("—", page.ResultAccuracyText);
        Assert.Equal(1, store.Saved.InterruptedTrials);
    }

    [Fact]
    public async Task PartialScoringAndDoubleSaveCannotProduceDuplicateTrials()
    {
        var backend = new Backend(); backend.Complete.TrySetResult();
        using var page = new EnvelopeStimulationPageViewModel(Route(), null!, backend, new Store());
        await page.PlayAndStimulateCommand.ExecuteAsync(null);
        page.Characters[0].CorrectCommand.Execute(null);
        await page.SaveTrialCommand.ExecuteAsync(null);
        Assert.Empty(page.SavedTrials);
        page.SetAllIncorrectCommand.Execute(null);
        page.Characters[0].CorrectCommand.Execute(null);
        await page.SaveTrialCommand.ExecuteAsync(null);
        await page.SaveTrialCommand.ExecuteAsync(null);
        Assert.Single(page.SavedTrials);
        Assert.Equal(1, page.SavedTrials[0].CorrectCount);
        Assert.False(page.CanScore);
    }

    [Fact]
    public async Task AtomicSnapshotRoundTripsWithoutLosingConfigurationOrScores()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var page = new EnvelopeStimulationPageViewModel(Route(), null!);
            var store = new EnvelopeTrainingResultStore(directory);
            await store.SaveAsync(page.CreateSession());
            var snapshot = JsonSerializer.Deserialize<EnvelopeTrainingSession>(File.ReadAllText(Directory.GetFiles(directory).Single()));
            Assert.Equal("EXP-TRAIN", snapshot!.Configuration.ExperimentId);
            Assert.Equal(40, snapshot.Configuration.StimulusConfiguration.Envelope!.DelayMilliseconds);
            Assert.Equal(2, snapshot.Configuration.StimulusElectrodes.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ImportedCorpusIsValidatedBeforeReplacingTheCurrentSentence()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "one.wav"), EnvelopeTrainingAudio.LoadDemo(0, 2, 1).WavBytes);
            var manifest = Path.Combine(directory, "corpus.json");
            await File.WriteAllTextAsync(manifest,
                "{\"name\":\"验收语料\",\"voice\":\"原始语音\",\"tables\":[{\"name\":\"A\",\"sentences\":[{\"text\":\"测试语句\",\"audioFile\":\"one.wav\"}]}]}");
            using var page = new EnvelopeStimulationPageViewModel(Route(), null!);
            await page.ImportCorpusAsync(manifest);
            Assert.Equal("测试语句", page.Sentence);
            Assert.Equal("验收语料 · 句表 A", page.CorpusText);
            Assert.Equal(2, page.Audio!.Duration.TotalSeconds);
            await File.WriteAllTextAsync(manifest,
                "{\"name\":\"错误语料\",\"voice\":\"原始\",\"tables\":[{\"name\":\"A\",\"sentences\":[{\"text\":\"错误\",\"audioFile\":\"missing.wav\"}]}]}");
            await page.ImportCorpusAsync(manifest);
            Assert.Equal("测试语句", page.Sentence);
            Assert.Contains("导入失败", page.StatusMessage);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StopAcknowledgementFailureBlocksNewStimulationAndEnding()
    {
        var backend = new Backend { StopUnconfirmed = true };
        var store = new Store();
        using var page = new EnvelopeStimulationPageViewModel(Route(), null!, backend, store);
        await page.PlayAndStimulateCommand.ExecuteAsync(null);
        Assert.True(page.StopUnconfirmed);
        Assert.False(page.CanStart);
        await page.ConfirmEndCommand.ExecuteAsync(null);
        Assert.False(page.IsResults);
        await page.RetryStopCommand.ExecuteAsync(null);
        Assert.False(page.StopUnconfirmed);
        Assert.True(page.CanStart);
    }

    [Fact]
    public async Task PlaybackFailureStopsTheSdkOperationAndDoesNotLeakAnActiveDevice()
    {
        await using var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        await using var service = new EnvelopeTrainingExecutionService(manager,
            (_, _) => Task.FromException(new InvalidOperationException("audio output failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(Route().Configuration,
            EnvelopeTrainingAudio.LoadDemo(0, 2, 1), true, new Progress<EnvelopeTrainingProgress>(), default));
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
    }

    [Fact]
    public async Task ApplicationDisposalCancelsAudioAndAwaitsDeviceStop()
    {
        await using var device = new SimulatedEggtCsDevice();
        await using var manager = new DeviceManager(connectedDevices: [device]);
        var service = new EnvelopeTrainingExecutionService(manager, (_, token) => Task.Delay(Timeout.Infinite, token));
        var execution = service.ExecuteAsync(Route().Configuration, EnvelopeTrainingAudio.LoadDemo(0, 2, 1), true,
            new Progress<EnvelopeTrainingProgress>(), default);
        await service.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
    }

    [Theory]
    [InlineData(0.5)] [InlineData(2)] [InlineData(5)]
    public void EveryBundledSentenceHasTheRequestedDurationAndAudioHash(double speed)
    {
        for (var index = 0; index < EnvelopeTrainingAudio.DemoSentences.Length; index++)
        {
            var audio = EnvelopeTrainingAudio.LoadDemo(index, speed, 1.8);
            Assert.Equal(EnvelopeTrainingAudio.DemoSentences[index].Length / speed, audio.Duration.TotalSeconds, 3);
            Assert.Equal(64, audio.Sha256.Length);
            Assert.All(audio.EnvelopeSamples, sample => Assert.InRange(sample * .033, 0, 1.8));
        }
    }

    internal static EnvelopeStimulationRouteData Route()
    {
        var original = ExperimentRunRouteDataDefaults.Create();
        return new(new ExperimentRunRouteData("EXP-TRAIN", "PAT-001",
            original.StimulusConfiguration with { Kind = StimulusKind.EnvelopeTAcs, Envelope = new(DelayMilliseconds: 40) },
            original.StimulusElectrodes, [], "", "", 500,
            creationMode: ExperimentCreationMode.StimulusOnly), "刺激阻抗检测已完成");
    }

    private sealed class Backend : IEnvelopeTrainingExecutionService
    {
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ReplayCompletion { get; set; }
        public bool Fail { get; set; }
        public bool StopUnconfirmed { get; set; }
        public int StimulationCount { get; private set; }
        public int ReplayCount { get; private set; }
        public async Task ExecuteAsync(ExperimentRunRouteData route, EnvelopeTrainingAudio audio, bool stimulate,
            IProgress<EnvelopeTrainingProgress> progress, CancellationToken token)
        {
            if (stimulate) StimulationCount++; else ReplayCount++;
            if (StopUnconfirmed) throw new EnvelopeTrainingStopUnconfirmedException(new Exception("no ack"));
            if (Fail) throw new InvalidOperationException("设备执行失败");
            await (stimulate ? Complete : ReplayCompletion ?? Complete).Task.WaitAsync(token);
            progress.Report(new(audio.Duration.TotalSeconds, audio.Duration.TotalSeconds + .04));
        }
        public Task ConfirmStoppedAsync(ExperimentRunRouteData route, CancellationToken token)
        { StopUnconfirmed = false; return Task.CompletedTask; }
    }

    private sealed class Store : IEnvelopeTrainingResultStore
    {
        public bool Fail { get; set; }
        public EnvelopeTrainingSession Saved { get; private set; } = null!;
        public TaskCompletionSource? SaveCompletion { get; set; }
        public async Task SaveAsync(EnvelopeTrainingSession session, CancellationToken token = default)
        {
            if (Fail) throw new InvalidOperationException("disk full");
            if (SaveCompletion is not null) await SaveCompletion.Task.WaitAsync(token);
            Saved = session;
        }
    }
}
