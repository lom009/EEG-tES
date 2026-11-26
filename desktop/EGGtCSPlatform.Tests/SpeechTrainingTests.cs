using System;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SpeechTrainingTests
{
    private static TrainingAudioSnapshot AudioSnapshot(TrainingAudioSettings settings) =>
        new(
            settings,
            "fixture",
            1729,
            1,
            [],
            System
                .Collections
                .Immutable
                .ImmutableDictionary<string, EGGtCSPlatform.Audio.Core.Routing.AudioRoute>
                .Empty,
            [],
            System.Collections.Immutable.ImmutableDictionary<string, AudioTimeline>.Empty,
            null,
            null,
            null
        );

    private static TrainingAudioPreparationResult Prepared(AudioBuffer audio)
    {
        var corpus = new FileSpeechCorpusProvider();
        var sentence = corpus.Corpora[0].Sentences[0];
        return new TrainingAudioPreparationService(corpus).Prepare(
            new(sentence, new(), audio, 1, 1729, null, null, 0)
        );
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void ScoringRuleHintOnlyAppearsForAdaptiveModes(int mode, bool visible)
    {
        using var page = new EnvelopeStimulationPageViewModel(
            Route(new EnvelopeParameters().DelayMilliseconds),
            new Router(),
            () => new(new FixedCorpusProvider(), new ControllableSpeechAudioPlayer())
        );
        page.SelectedMode = page.ModeOptions[mode];
        Assert.Equal(page.ModeOptions[mode], page.SelectedMode);
        Assert.Equal(visible, page.ShowAdaptiveScoringHint);
        Assert.Equal(0, page.DelayMilliseconds);
        Assert.Equal(0, page.Waveform.DelayMilliseconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedRirPageSettingsPreviewResultsAndLegacy(bool noise)
    {
        var router = new Router();
        var player = new ControllableSpeechAudioPlayer();
        using var page = new EnvelopeStimulationPageViewModel(
            Route(),
            router,
            () => new(new FixedCorpusProvider(), player)
        );
        page.SelectedMode = page.ModeOptions[4];
        page.RirT60 = .15m;
        page.RirLengthSeconds = .15m;
        page.RirAddNoise = noise;
        page.RirSnrDb = -10;
        page.RirNoise = page.NoiseOptions[1];
        Assert.True(page.IsRirMode);
        Assert.False(page.IsFixedMode);
        Assert.False(page.ShowAdaptiveScoringHint);
        Assert.True(page.StartCommand.CanExecute(null));
        await page.GenerateRirCommand.ExecuteAsync(null);
        Assert.NotNull(page.RirPreview);
        page.RirEarly = false;
        Assert.Null(page.RirPreview);
        page.SelectedMode = page.ModeOptions[3];
        page.FixedSnrDb = 30;
        page.SelectedMode = page.ModeOptions[4];
        Assert.Equal(-10, page.RirSnrDb);
        await page.StartCommand.ExecuteAsync(null);
        page.RirT60 = .6m;
        Assert.Equal(.15m, page.RirT60);
        page.AllCorrectCommand.Execute(null);
        await page.SaveNextCommand.ExecuteAsync(null);
        player.Complete();
        page.SelectedCarrier = page.CarrierOptions[1];
        await page.ReplayCommand.ExecuteAsync(null);
        await page.FinishAsync(true);
        var result = new SpeechTrainingResultsPageViewModel(router.Result!);
        Assert.True(result.IsFixedRir);
        Assert.False(result.IsAdaptive);
        Assert.False(result.HasAdaptiveValue);
        Assert.False(result.IsMixedCarrier);
        Assert.Equal("测试未完成", result.CompletionStatus);
        Assert.Equal("100%", result.AccuracyText);
        Assert.Equal("原速", result.SpeechRateText);
        Assert.Contains("T60", result.RirSettingsText);
        Assert.Contains("SHA256", result.Results[0].DetailText);
        Assert.NotNull(result.Results[0].Rir);
        Assert.Equal(noise, result.Results[0].Noise is not null);
        Assert.Null(router.Result!.SnrSummary);
        Assert.Null(router.Result.AdaptiveSummary);
        var legacy = new SpeechTrainingResultsPageViewModel(
            router.Result with
            {
                Settings = router.Result.Settings with { Rir = null },
                Results = router
                    .Result.Results.Select(r => r with { Rir = null, Noise = null })
                    .ToArray(),
            }
        );
        Assert.Contains("未记录", legacy.RirSettingsText);
        Assert.Contains("RIR 快照：未记录", legacy.Results[0].DetailText);
        var empty = new SpeechTrainingResultsPageViewModel(
            router.Result with
            {
                Results = [],
                Playbacks = [],
            }
        );
        Assert.Equal("待判定", empty.AccuracyText);
        Assert.Equal("测试未完成", empty.CompletionStatus);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(30, 1)]
    public async Task FixedSnrPageLocksParametersAndShowsAccuracyWithoutThreshold(
        double db,
        int noiseIndex
    )
    {
        var router = new Router();
        var player = new ControllableSpeechAudioPlayer();
        using var page = new EnvelopeStimulationPageViewModel(
            Route(),
            router,
            () => new(new FixedCorpusProvider(), player)
        );
        page.SpeechRate = 4;
        page.InitialSnrDb = 12;
        page.SelectedMode = page.ModeOptions[3];
        page.FixedSnrDb = (decimal)db;
        page.SelectedNoise = page.NoiseOptions[noiseIndex];
        Assert.True(page.IsFixedSnrMode);
        Assert.True(page.IsSnrMode);
        Assert.False(page.IsAdaptiveSnrMode);
        Assert.False(page.IsFixedMode);
        Assert.False(page.ShowAdaptiveScoringHint);
        Assert.Contains("dB", page.SnrText);
        Assert.DoesNotContain("反转", page.SnrText);
        Assert.True(page.StartCommand.CanExecute(null));
        page.SelectedMode = page.ModeOptions[0];
        Assert.Equal(4, page.SpeechRate);
        page.SelectedMode = page.ModeOptions[3];
        Assert.Equal((decimal)db, page.FixedSnrDb);
        await page.StartCommand.ExecuteAsync(null);
        page.FixedSnrDb = 1;
        page.SelectedNoise = page.NoiseOptions[1 - noiseIndex];
        Assert.Equal((decimal)db, page.FixedSnrDb);
        for (var i = 0; i < 10; i++)
        {
            if (i % 2 == 0)
                page.AllCorrectCommand.Execute(null);
            else
                page.AllWrongCommand.Execute(null);
            await page.SaveNextCommand.ExecuteAsync(null);
        }
        Assert.Null(router.Result);
        await page.FinishAsync(true);
        var route = router.Result!;
        Assert.Equal(SpeechTrainingMode.FixedSnr, route.Settings.TrainingMode);
        Assert.Equal(db, route.Settings.FixedSnrDb);
        Assert.Null(route.Settings.InitialSnrDb);
        Assert.Null(route.Settings.AdaptiveScoring);
        Assert.Null(route.SnrSummary);
        var results = new SpeechTrainingResultsPageViewModel(route);
        Assert.False(results.IsAdaptive);
        Assert.False(results.HasAdaptiveValue);
        Assert.True(results.IsFixedSnr);
        Assert.Equal("测试完成", results.CompletionStatus);
        Assert.Equal("50%", results.AccuracyText);
        Assert.Equal("原速", results.SpeechRateText);
        Assert.Contains("固定 SNR", results.SnrSettingsText);
        Assert.All(
            results.Results,
            row =>
            {
                Assert.Equal(db, row.Noise!.Settings.SnrDb);
                Assert.Contains("SHA256", row.DetailText);
                Assert.DoesNotContain("通过", row.DetailText);
                Assert.DoesNotContain("反转", row.DetailText);
            }
        );
        var legacy = new SpeechTrainingResultsPageViewModel(
            route with
            {
                Settings = route.Settings with { FixedSnrDb = null, AudioSettings = null },
                Results = route.Results.Select(x => x with { Noise = null }).ToArray(),
            }
        );
        Assert.Contains("未记录", legacy.SnrSettingsText);
        Assert.Contains("噪声快照：未记录", legacy.Results[0].DetailText);
        var empty = new SpeechTrainingResultsPageViewModel(
            route with
            {
                Results = [],
                Playbacks = [],
            }
        );
        Assert.Equal("测试未完成", empty.CompletionStatus);
        Assert.Equal("待判定", empty.AccuracyText);
    }

    [Fact]
    public async Task FixedSnrMixedCarrierIncludesUnsavedReplay()
    {
        var router = new Router();
        var player = new ControllableSpeechAudioPlayer();
        using var page = new EnvelopeStimulationPageViewModel(
            Route(),
            router,
            () => new(new FixedCorpusProvider(), player)
        );
        page.SelectedMode = page.ModeOptions[3];
        await page.StartCommand.ExecuteAsync(null);
        page.AllCorrectCommand.Execute(null);
        await page.SaveNextCommand.ExecuteAsync(null);
        player.Complete();
        page.SelectedCarrier = page.CarrierOptions[1];
        await page.ReplayCommand.ExecuteAsync(null);
        await page.FinishAsync(true);
        var results = new SpeechTrainingResultsPageViewModel(router.Result!);
        Assert.Single(results.Results);
        Assert.Equal("测试未完成", results.CompletionStatus);
        Assert.Equal("原始语音", results.CarrierText);
        Assert.False(results.HasAdaptiveValue);
    }

    private sealed class FixedCorpusProvider : ISpeechCorpusProvider
    {
        private readonly FileSpeechCorpusProvider _provider = new();
        public System.Collections.Generic.IReadOnlyList<SpeechCorpus> Corpora =>
            [
                _provider.Corpora[0] with
                {
                    Sentences = _provider.Corpora[0].Sentences.Take(10).ToArray(),
                    AdaptiveScoring = null,
                    AdaptiveConfigurationError = "测试：无自适应评分",
                },
            ];

        public AudioBuffer ReadAudio(SpeechSentence sentence) => _provider.ReadAudio(sentence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SnrPageControlsAndResultsUseActualNoiseSnapshot(int noiseIndex)
    {
        var router = new Router();
        var player = new ControllableSpeechAudioPlayer();
        using var page = new EnvelopeStimulationPageViewModel(
            Route(),
            router,
            () => new(new FileSpeechCorpusProvider(), player)
        );
        page.SpeechRate = 4;
        page.SelectedMode = page.ModeOptions[2];
        page.SelectedNoise = page.NoiseOptions[noiseIndex];
        page.InitialSnrDb = 12;
        page.ListSize = 10;
        Assert.True(page.IsSnrMode);
        Assert.False(page.IsFixedMode);
        Assert.False(page.IsAdaptiveMode);
        Assert.True(page.StartCommand.CanExecute(null));
        Assert.Contains("12 dB", page.SnrText);
        await page.StartCommand.ExecuteAsync(null);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(page.SaveNextCommand.CanExecute(null) == false);
            if (i % 2 == 0)
                page.AllCorrectCommand.Execute(null);
            else
                page.AllWrongCommand.Execute(null);
            await page.SaveNextCommand.ExecuteAsync(null);
        }
        Assert.Null(router.Result);
        await page.FinishAsync(true);
        var route = router.Result!;
        Assert.Equal(SpeechTrainingMode.AdaptiveSnr, route.Settings.TrainingMode);
        Assert.Equal(12, route.Settings.InitialSnrDb);
        var results = new SpeechTrainingResultsPageViewModel(route);
        Assert.True(results.IsSnr);
        Assert.Equal("原速", results.SpeechRateText);
        Assert.Equal("自适应信噪比阈值（SRT）", results.AdaptiveLabel);
        Assert.EndsWith(" dB", results.AdaptiveValue);
        Assert.Contains("种子", results.Results[0].DetailText);
        Assert.Contains("SHA256", results.Results[0].DetailText);
        Assert.Equal(
            route.Results.TakeLast(8).Average(x => x.AdaptiveSnr!.Submission.SnrDb),
            route.SnrSummary!.MeanSnrDb
        );
        var missing = new SpeechTrainingResultsPageViewModel(
            route with
            {
                Settings = route.Settings with { InitialSnrDb = null, AudioSettings = null },
                SnrSummary = null,
            }
        );
        Assert.Contains("未记录", missing.SnrSettingsText);
        Assert.False(missing.HasAdaptiveValue);
        var mixed = new SpeechTrainingResultsPageViewModel(
            route with
            {
                SnrSummary = route.SnrSummary with { MixedCarrier = true },
            }
        );
        Assert.Equal("末 8 句平均 SNR（描述性）", mixed.AdaptiveLabel);
        Assert.Equal("混合载体", mixed.CarrierText);
    }

    [Fact]
    public async Task CorpusScoringAvailabilityControlsStartAndPreservesSelectedMode()
    {
        var router = new Router();
        using var page = new EnvelopeStimulationPageViewModel(
            Route(),
            router,
            () => new(new ConfiguredProvider(), new ControllableSpeechAudioPlayer())
        );
        page.ListSize = 10;
        Assert.True(page.CanUseAdaptiveMode);
        Assert.Contains("每句 7 字，至少答对 4 字通过", page.AdaptiveScoringHint);
        page.SelectedMode = page.ModeOptions[1];
        page.SentenceList = "02";
        Assert.True(page.IsAdaptiveMode);
        Assert.False(page.CanUseAdaptiveMode);
        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Contains("句子 11", page.AdaptiveScoringHint);
        page.SentenceList = "01";
        Assert.True(page.StartCommand.CanExecute(null));
        page.SelectedCorpus = "Missing";
        Assert.True(page.IsAdaptiveMode);
        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Contains("corpus.json", page.AdaptiveScoringHint);
        page.SelectedCorpus = "Broken";
        Assert.Contains("配置错误", page.AdaptiveScoringHint);
        Assert.False(page.StartCommand.CanExecute(null));
        page.SelectedMode = page.ModeOptions[0];
        Assert.True(page.StartCommand.CanExecute(null));
        page.SelectedCorpus = "Configured";
        page.SelectedMode = page.ModeOptions[1];
        await page.StartCommand.ExecuteAsync(null);
        page.AllCorrectCommand.Execute(null);
        await page.SaveNextCommand.ExecuteAsync(null);
        await page.FinishAsync(true);
        Assert.Equal(new(7, 4), router.Result!.Settings.AdaptiveScoring);
        var result = new SpeechTrainingResultsPageViewModel(router.Result);
        Assert.Equal("每句 7 字，至少答对 4 字通过", result.AdaptiveScoringText);
        var legacy = new SpeechTrainingResultsPageViewModel(
            router.Result with
            {
                Settings = router.Result.Settings with { AdaptiveScoring = null },
            }
        );
        Assert.Equal("未记录", legacy.AdaptiveScoringText);
    }

    private sealed class ConfiguredProvider : ISpeechCorpusProvider
    {
        private static SpeechSentence[] Sentences(int count) =>
            Enumerable
                .Range(1, count)
                .Select(n => new SpeechSentence(
                    $"s/{n}",
                    n,
                    n == 11 ? "一二三四五六" : "一二三四五六七",
                    $"{n}.wav"
                ))
                .ToArray();

        public System.Collections.Generic.IReadOnlyList<SpeechCorpus> Corpora { get; } =
        [
            new("Configured", Sentences(11), new(7, 4)),
            new("Missing", Sentences(1)),
            new("Broken", Sentences(1), AdaptiveConfigurationError: "Broken/corpus.json 配置错误"),
        ];

        public AudioBuffer ReadAudio(SpeechSentence sentence) => new(16000, 1, new byte[6400]);
    }

    [Fact]
    public async Task AdaptiveModeAndResultsKeepActualUnroundedConfiguration()
    {
        var router = new Router();
        var player = new ControllableSpeechAudioPlayer();
        using var page = Create(router, player);
        Assert.True(page.IsFixedMode);
        page.SpeechRate = 3;
        page.SelectedMode = page.ModeOptions[1];
        Assert.True(page.IsAdaptiveMode);
        Assert.False(page.CanAdjustSpeechRate);
        Assert.Contains("1.5 倍", page.AdaptiveRateText);
        Assert.Contains("待准备", page.AdaptiveRateText);
        page.SelectedMode = page.ModeOptions[0];
        Assert.Equal(3, page.SpeechRate);
        page.SelectedMode = page.ModeOptions[1];
        page.ListSize = 10;
        await page.StartCommand.ExecuteAsync(null);
        Assert.DoesNotContain("待准备", page.AdaptiveRateText);
        page.SelectedMode = page.ModeOptions[0];
        Assert.True(page.IsAdaptiveMode);
        for (var i = 0; i < 10; i++)
        {
            if (i > 0)
                player.Complete();
            if (i % 2 == 0)
                page.AllCorrectCommand.Execute(null);
            else
                page.AllWrongCommand.Execute(null);
            await page.SaveNextCommand.ExecuteAsync(null);
        }
        Assert.Null(router.Result);
        await page.FinishAsync(true);
        var route = router.Result!;
        Assert.Contains(
            $"{route.Results[^1].Adaptive!.Submission.Multiplier:0.###} 倍",
            page.AdaptiveRateText
        );
        Assert.Equal(SpeechTrainingMode.AdaptiveRate, route.Settings.TrainingMode);
        Assert.False(route.Results[0].PlaybackCompleted);
        Assert.True(route.Results[1].PlaybackCompleted);
        Assert.Equal(
            route.Results.TakeLast(8).Average(s => s.Adaptive!.Timing.TargetSpeechRate),
            route.AdaptiveSummary!.MeanTargetRate
        );
        var model = new SpeechTrainingResultsPageViewModel(route);
        Assert.True(model.IsAdaptive);
        Assert.True(model.HasAdaptiveValue);
        Assert.Equal("自适应语速阈值（SRT）", model.AdaptiveLabel);
        Assert.Equal("测试完成", model.AdaptiveStatus);
        Assert.Contains("通过", model.Results[0].AdaptiveText);
        Assert.Contains("SOLAFS 输出", model.Results[0].DetailText);
        Assert.Equal(
            $"{route.Results[0].Adaptive!.Timing.TargetSpeechRate:0.###} 字/s",
            model.Results[0].SpeechRateText
        );
        var mixed = new SpeechTrainingResultsPageViewModel(
            route with
            {
                AdaptiveSummary = route.AdaptiveSummary with { MixedCarrier = true },
            }
        );
        Assert.Equal("混合载体", mixed.CarrierText);
        Assert.Equal("末 8 句平均语速（描述性）", mixed.AdaptiveLabel);
    }

    [Fact]
    public void LegacyResultsStayFixedAndDecimalRatesTrimZeros()
    {
        var route = new SpeechTrainingResultsRouteData(
            Route(),
            new("MSP", "01", "固定语速", "原始语音", 0),
            DateTimeOffset.Now,
            1,
            [new(1, "测试", 2, 2, SpeechRate: 2.1250m)]
        );
        var model = new SpeechTrainingResultsPageViewModel(route);
        Assert.False(model.IsAdaptive);
        Assert.False(model.HasAdaptiveValue);
        Assert.Equal(
            "固定语速测试(安静)",
            new SpeechTrainingResultsPageViewModel(
                route with
                {
                    Settings = route.Settings with { Mode = null! },
                }
            ).ModeText
        );
        Assert.Equal("2.125 字/s", model.Results[0].SpeechRateText);
        Assert.Equal("未知（旧记录）", model.Results[0].PlaybackCompletedText);
    }

    [Fact]
    public async Task VocoderSettingsAreRetainedBeforeStartAndLockedForRound()
    {
        var player = new ControllableSpeechAudioPlayer();
        var router = new Router();
        using var page = Create(router, player);
        Assert.Equal("原始语音", page.SelectedCarrier);
        page.SelectedCarrier = "声码器仿真";
        page.VocoderAlgorithm = "ACE";
        Assert.True(page.ShowVocoderMaxima);
        page.VocoderBands = 4;
        Assert.Equal(4, page.VocoderMaxima);
        page.SelectedCarrier = "原始语音";
        page.SelectedCarrier = "声码器仿真";
        Assert.Equal(4, page.VocoderBands);
        Assert.Equal("ACE", page.VocoderAlgorithm);
        page.VocoderAlgorithm = "经典 CIS";
        await page.StartCommand.ExecuteAsync(null);
        Assert.False(page.CanAdjustCarrier);
        page.SelectedCarrier = "原始语音";
        Assert.True(page.IsVocoderEnabled);
        player.Complete();
        var audio = player.Audio;
        var wave = page.VoiceSamples;
        page.SelectedCarrier = "原始语音";
        Assert.Same(wave, page.VoiceSamples);
        Assert.Same(audio, player.Audio);
        Assert.True(page.IsVocoderEnabled);
        Assert.False(page.CanAdjustCarrier);
        Assert.Contains("训练中不可修改", page.CarrierHint);
        page.AllCorrectCommand.Execute(null);
        await page.SaveNextCommand.ExecuteAsync(null);
        await page.FinishAsync(true);
        Assert.Contains(
            "经典 CIS",
            router.Result!.Results[0].AudioSnapshot!.Settings.CarrierSummary
        );
        Assert.Contains(
            "经典 CIS",
            new SpeechTrainingResultsPageViewModel(router.Result).CarrierText
        );
        page.Dispose();
        Assert.False(page.CanAdjustCarrier);
    }

    [Fact]
    public async Task WheelZoomIsIndependentBoundedAndSurvivesPlayback()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var page = Create(new Router(), player);
        Assert.Equal(1, page.VoiceMaximumAmplitude);
        Assert.Equal(2, page.StimulusMaximumAmplitude);
        page.ZoomVoice(1);
        Assert.Equal(0.8, page.VoiceMaximumAmplitude, 12);
        Assert.Equal(2, page.StimulusMaximumAmplitude);
        page.ZoomVoice(-1);
        page.ZoomVoice(0.5);
        page.ZoomVoice(0.5);
        Assert.Equal(0.8, page.VoiceMaximumAmplitude, 12);
        page.ZoomStimulus(-1);
        Assert.Equal(2.5, page.StimulusMaximumAmplitude, 12);
        await page.StartCommand.ExecuteAsync(null);
        player.Complete();
        await page.ReplayCommand.ExecuteAsync(null);
        page.AllCorrectCommand.Execute(null);
        await page.SaveNextCommand.ExecuteAsync(null);
        page.DelayMilliseconds = 100;
        Assert.Equal(0.8, page.VoiceMaximumAmplitude, 12);
        Assert.Equal(2.5, page.StimulusMaximumAmplitude, 12);
        page.ZoomVoice(10000);
        page.ZoomStimulus(10000);
        Assert.Equal(0.001, page.VoiceMaximumAmplitude);
        Assert.Equal(0.01, page.StimulusMaximumAmplitude);
        page.ZoomVoice(-10000);
        page.ZoomStimulus(-10000);
        Assert.Equal(10, page.VoiceMaximumAmplitude);
        Assert.Equal(20, page.StimulusMaximumAmplitude);
        page.ZoomVoice(double.NaN);
        page.ZoomStimulus(double.PositiveInfinity);
        Assert.Equal(10, page.VoiceMaximumAmplitude);
        Assert.Equal(20, page.StimulusMaximumAmplitude);
        using var fresh = Create(new Router(), new());
        Assert.Equal(1, fresh.VoiceMaximumAmplitude);
        Assert.Equal(2, fresh.StimulusMaximumAmplitude);
    }

    [Fact]
    public void SubUnitAmplitudeScalePreservesZeroAndDefaultEegMapping()
    {
        Assert.Equal(50, Controls.WaveformAmplitudeScale.ToY(0, 0.1, 100, 0.001));
        Assert.Equal(0, Controls.WaveformAmplitudeScale.ToY(0.1, 0.1, 100, 0.001));
        Assert.Equal(100, Controls.WaveformAmplitudeScale.ToY(-0.1, 0.1, 100, 0.001));
        Assert.Equal(45, Controls.WaveformAmplitudeScale.ToY(0.1, 0.1, 100));
    }

    [Fact]
    public void StimulusProjectionUsesSecondsCurrentAndDelayWithoutClampingPeaks()
    {
        var plot = new Avalonia.Rect(46, 24, 1000, 100);
        var point = new SpeechStimulusPoint(0.25, 1);
        var projected = Controls.SpeechStimulusPreviewControl.Project(point, plot, 2, 0.5, 100, 2);
        Assert.Equal(221, projected.X, 10);
        Assert.Equal(61.5, projected.Y, 10);
        var lower = Controls.SpeechStimulusPreviewControl.Project(
            new(0.25, -1),
            plot,
            2,
            0.5,
            100,
            2
        );
        Assert.Equal(2 * plot.Center.Y, projected.Y + lower.Y, 10);
        var zoomed = Controls.SpeechStimulusPreviewControl.Project(point, plot, 2, 0.5, 100, 0.1);
        Assert.True(zoomed.Y < plot.Top); // Geometry is clipped by the control, never flattened.
        var tail = Controls.SpeechStimulusPreviewControl.Project(new(2, 1), plot, 2, 0.5, 100, 2);
        Assert.True(tail.X > plot.Right); // Shared axis remains the audio duration.
    }

    [Fact]
    public async Task SpeechRateRemainsLockedUntilNextRound()
    {
        var router = new Router();
        var player = new ControllableSpeechAudioPlayer();
        using var page = Create(router, player);
        Assert.Equal(0, page.SpeechRate);
        page.SpeechRate = -1;
        Assert.Equal(0, page.SpeechRate);
        page.SpeechRate = 6;
        Assert.Equal(5, page.SpeechRate);
        page.SpeechRate = 2.5m;
        Assert.Equal(3, page.SpeechRate);
        await page.StartCommand.ExecuteAsync(null);
        var audio = player.Audio;
        var duration = page.AudioDurationSeconds;
        page.SpeechRate = 4;
        Assert.Equal(3, page.SpeechRate);
        Assert.False(page.CanAdjustSpeechRate);
        Assert.Equal(7d / 3, duration, 4);
        Assert.Same(audio, player.Audio);
        Assert.Equal(duration, page.AudioDurationSeconds);
        Assert.Equal(1, player.PlayCount);
        player.Complete();
        Assert.False(page.CanAdjustSpeechRate);
        page.AllCorrectCommand.Execute(null);
        page.SpeechRate = 4;
        Assert.Equal(3, page.SpeechRate);
        Assert.Contains("训练中不可修改", page.SpeechRateHint);
        Assert.Equal(duration, page.AudioDurationSeconds);
        Assert.Same(audio, player.Audio);
        Assert.Equal(1, player.PlayCount);
        await page.SaveNextCommand.ExecuteAsync(null);
        Assert.Equal(7d / 3, page.AudioDurationSeconds, 4);
        await page.FinishAsync(true);
        Assert.Equal(3, router.Result!.Settings.SpeechRate);
        Assert.NotNull(router.Result.Settings.AudioSettings);
        Assert.Equal(3, router.Result.Results[0].SpeechRate);
        Assert.Equal(3, router.Result.Results[0].AudioSnapshot!.Settings.SpeechRate);
        Assert.Equal(
            "3 字/s",
            new SpeechTrainingResultsPageViewModel(router.Result).SpeechRateText
        );
        page.Dispose();
        Assert.False(page.CanAdjustSpeechRate);
        using var reopened = Create(new Router(), new());
        Assert.Equal(0, reopened.SpeechRate);
    }

    [Fact]
    public void ResultRatesSupportMixedLegacyAndEmptyResults()
    {
        var settings = new EGGtCSPlatform.MainApp.SpeechTrainingSettings(
            "MSP",
            "01",
            "固定语速",
            "原始语音",
            2
        );
        var route = new SpeechTrainingResultsRouteData(
            Route(),
            settings,
            DateTimeOffset.Now,
            3,
            [new(1, "甲", 1, 1), new(2, "乙", 1, 1, SpeechRate: 3)]
        );
        var model = new SpeechTrainingResultsPageViewModel(route);
        Assert.Equal("逐句调整", model.SpeechRateText);
        Assert.Equal(new[] { "2 字/s", "3 字/s" }, model.Results.Select(x => x.SpeechRateText));
        Assert.Null(route.Results[0].SpeechRate); // Legacy data is not mutated.
        Assert.Equal("原始语音", model.CarrierText);
        var mixedCarrier = route with
        {
            Results =
            [
                new(1, "甲", 1, 1),
                new(2, "乙", 1, 1, AudioSnapshot: AudioSnapshot(new(Vocoder: new()))),
            ],
        };
        var mixedModel = new SpeechTrainingResultsPageViewModel(mixedCarrier);
        Assert.Equal("逐句调整", mixedModel.CarrierText);
        Assert.Contains("经典 CIS", mixedModel.Results[1].CarrierText);
        Assert.Equal(
            "原始语音",
            new SpeechTrainingResultsPageViewModel(route with { Results = [] }).CarrierText
        );
        Assert.Equal(
            "2 字/s",
            new SpeechTrainingResultsPageViewModel(route with { Results = [] }).SpeechRateText
        );
        Assert.Equal(
            "原速",
            new SpeechTrainingResultsPageViewModel(
                route with
                {
                    Results = [new(1, "甲", 1, 1, SpeechRate: 0)],
                }
            ).SpeechRateText
        );
    }

    [Fact]
    public async Task MuteRestoresLastSuccessfulVolumeDuringPlayback()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var page = Create(new Router(), player);
        page.MuteVolumeCommand.Execute(null);
        page.RestoreVolumeCommand.Execute(null);
        Assert.Equal(100, page.VolumePercent);
        page.VolumePercent = 35;
        await page.StartCommand.ExecuteAsync(null);
        page.MuteVolumeCommand.Execute(null);
        page.MuteVolumeCommand.Execute(null);
        Assert.Equal(0, player.Volume);
        page.RestoreVolumeCommand.Execute(null);
        Assert.Equal(35, page.VolumePercent);
        player.FailVolume = true;
        page.VolumePercent = 70;
        Assert.Contains("音量调节失败", page.StatusText);
        player.FailVolume = false;
        page.VolumePercent = 0;
        page.RestoreVolumeCommand.Execute(null);
        Assert.Equal(35, page.VolumePercent);
        page.RestoreVolumeCommand.Execute(null);
        Assert.Equal(35, page.VolumePercent);
        page.Dispose();
        Assert.False(page.MuteVolumeCommand.CanExecute(null));
        Assert.False(page.RestoreVolumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task ToggleMuteTracksActualVolumeAndRecoversFromFailedChanges()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var page = Create(new Router(), player);
        page.ToggleMuteCommand.Execute(null);
        Assert.True(page.IsMuted);
        Assert.Equal("恢复音量", page.MuteActionText);
        page.ToggleMuteCommand.Execute(null);
        Assert.Equal(100, page.VolumePercent);
        page.VolumePercent = 35;
        await page.StartCommand.ExecuteAsync(null);
        player.FailVolume = true;
        page.ToggleMuteCommand.Execute(null);
        Assert.False(page.IsMuted);
        Assert.Equal(35, page.VolumePercent);
        player.FailVolume = false;
        page.ToggleMuteCommand.Execute(null);
        Assert.True(page.IsMuted);
        player.FailVolume = true;
        page.ToggleMuteCommand.Execute(null);
        Assert.True(page.IsMuted);
        player.FailVolume = false;
        page.ToggleMuteCommand.Execute(null);
        Assert.Equal(35, page.VolumePercent);
        page.VolumePercent = 0;
        Assert.True(page.IsMuted);
        page.VolumePercent = 22;
        Assert.False(page.IsMuted);
        Assert.Equal("静音", page.MuteActionText);
        page.ToggleMuteCommand.Execute(null);
        page.ToggleMuteCommand.Execute(null);
        Assert.Equal(22, page.VolumePercent);
        page.Dispose();
        Assert.False(page.ToggleMuteCommand.CanExecute(null));
        page.ToggleMuteCommand.Execute(null);
        Assert.Equal(22, page.VolumePercent);
    }

    private static EnvelopeStimulationPageViewModel Create(
        Router router,
        ControllableSpeechAudioPlayer player
    ) => new(Route(), router, () => new(new FileSpeechCorpusProvider(), player));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(40.5, 41)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(double.NaN, 40)]
    [InlineData(double.PositiveInfinity, 40)]
    public void DelayUpdatesConfigurationAndPreview(double value, double expected)
    {
        using var page = Create(new Router(), new());
        var original = page.RouteData;
        Assert.Equal(40, page.DelayMilliseconds);
        page.DelayMilliseconds = value;
        Assert.Equal(expected, page.DelayMilliseconds);
        Assert.Equal(expected, page.Waveform.DelayMilliseconds);
        Assert.Equal(
            expected,
            page.RouteData.Configuration.StimulusConfiguration.Envelope!.DelayMilliseconds
        );
        Assert.Equal(40, original.Configuration.StimulusConfiguration.Envelope!.DelayMilliseconds);
        Assert.Equal(
            original.Configuration.StimulusElectrodes,
            page.RouteData.Configuration.StimulusElectrodes
        );
        Assert.Equal(original.DetectionStatus, page.RouteData.DetectionStatus);
        Assert.Equal(
            original.Configuration.CreationMode,
            page.RouteData.Configuration.CreationMode
        );
        Assert.Equal(
            original.Configuration.ExperimentId,
            page.RouteData.Configuration.ExperimentId
        );
        Assert.Equal(
            original.Configuration.StimulusConfiguration.Envelope.ModulationHz,
            page.RouteData.Configuration.StimulusConfiguration.Envelope.ModulationHz
        );
    }

    [Fact]
    public async Task DelayLocksAtStartAndFlowsToResults()
    {
        var router = new Router();
        using var page = Create(router, new());
        page.DelayMilliseconds = 123;
        await page.StartCommand.ExecuteAsync(null);
        page.DelayMilliseconds = 200;
        Assert.Equal(123, page.DelayMilliseconds);
        await page.FinishAsync(true);
        Assert.Equal(
            123,
            router.Result!.Source.Configuration.StimulusConfiguration.Envelope!.DelayMilliseconds
        );
        Assert.Equal(0, router.Result.Settings.SpeechRate);
        page.DelayMilliseconds = 300;
        Assert.Equal(123, page.DelayMilliseconds);
    }

    [Fact]
    public async Task VolumePercentRemainsAdjustableDuringTraining()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var page = Create(new Router(), player);
        Assert.Equal(100, page.VolumePercent);
        page.VolumePercent = 35;
        Assert.Equal(0.35, player.Volume);
        Assert.Equal("35%", page.VolumeText);
        await page.StartCommand.ExecuteAsync(null);
        Assert.False(page.CanConfigure);
        Assert.True(page.CanAdjustVolume);
        page.AllCorrectCommand.Execute(null);
        page.VolumePercent = 0;
        Assert.Equal("0%", page.VolumeText);
        Assert.True(page.SaveNextCommand.CanExecute(null));
        Assert.Equal(1, player.PlayCount);
    }

    [Fact]
    public async Task GroupSizeUpdatesOptionsAndResultsDescribeActualResourceRange()
    {
        var router = new Router();
        using var page = Create(router, new());
        Assert.Equal(new[] { 10, 20, 30, 50 }, page.ListSizeOptions);
        page.SentenceList = "05";
        page.ListSize = 30;
        Assert.Equal("01", page.SentenceList);
        Assert.Equal(4, page.SentenceListOptions.Count);
        page.SentenceList = "04";
        Assert.Equal("马上过来踢足球", string.Concat(page.Words.Select(w => w.Text)));
        await page.StartCommand.ExecuteAsync(null);
        page.ListSize = 10;
        Assert.Equal(30, page.ListSize);
        for (var i = 0; i < 10; i++)
        {
            page.AllCorrectCommand.Execute(null);
            await page.SaveNextCommand.ExecuteAsync(null);
        }
        Assert.Null(router.Result);
        await page.FinishAsync(true);
        var result = new SpeechTrainingResultsPageViewModel(router.Result!);
        Assert.Equal(30, result.Settings.ListSize);
        Assert.Equal("04", result.Settings.SentenceList);
        Assert.Equal("091–100", result.ResourceRangeText);
        Assert.Equal(10, result.ActualSentenceCount);
        Assert.Equal("10/10", result.CompletedText);
    }

    [Fact]
    public void ReenteringPageDiscoversAddedCorpusAndVariableLengthList()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "speech-ui-" + Guid.NewGuid()
        );
        void Add(string corpus, string[] texts)
        {
            var folder = System.IO.Path.Combine(root, corpus);
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "sentences.txt"), texts);
            for (var i = 0; i < texts.Length; i++)
                System.IO.File.Copy(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "SpeechCorpora/MSP/1.wav"),
                    System.IO.Path.Combine(folder, $"{i + 1}.wav")
                );
        }
        try
        {
            Add("MSP", new[] { "句子" });
            using var original = new EnvelopeStimulationPageViewModel(
                Route(),
                new Router(),
                () => new(new FileSpeechCorpusProvider(root), new ControllableSpeechAudioPlayer())
            );

            Add("中文语料", new[] { "练习句" });
            Assert.Single(original.SentenceListOptions);
            using var reopened = new EnvelopeStimulationPageViewModel(
                Route(),
                new Router(),
                () => new(new FileSpeechCorpusProvider(root), new ControllableSpeechAudioPlayer())
            );
            Assert.Equal(new[] { "MSP", "中文语料" }, reopened.CorpusOptions);
            Assert.Equal(new[] { "01" }, reopened.SentenceListOptions);
            reopened.SelectedCorpus = "中文语料";
            Assert.Equal("01", reopened.SentenceList);
            Assert.Equal("练习句", string.Concat(reopened.Words.Select(x => x.Text)));
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CorpusAndListSelectionsDisplayRealText()
    {
        using var page = Create(new Router(), new());
        Assert.Equal(new[] { "MSP", "MHINT", "MHINT-P" }, page.CorpusOptions);
        Assert.Equal(5, page.SentenceListOptions.Count);
        Assert.Equal("今天的阳光真好", string.Concat(page.Words.Select(x => x.Text)));
        page.SentenceList = "05";
        Assert.Contains("句表 05", page.CorpusLabel);
        page.SelectedCorpus = "MHINT";
        Assert.Equal("01", page.SentenceList);
        Assert.Equal(12, page.SentenceListOptions.Count);
        Assert.Equal("她切菜不小心切伤手指", string.Concat(page.Words.Select(x => x.Text)));
        page.SelectedCorpus = "MHINT-P";
        Assert.Equal("他非常喜欢看武侠小说", string.Concat(page.Words.Select(x => x.Text)));
        Assert.Equal("0.50 mA", page.CurrentText);
        Assert.Equal("0.04 s", page.DelayText);
        Assert.Empty(page.VoiceSamples);
        Assert.Null(page.StimulusPreview);
        Assert.False(page.CanScore);
    }

    [Fact]
    public async Task PlaybackLocksSettingsAndReplayPreservesAnswers()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var page = Create(new Router(), player);
        page.SelectedCorpus = "MHINT";
        await page.StartCommand.ExecuteAsync(null);
        Assert.True(page.IsPlaying);
        Assert.NotEmpty(page.VoiceSamples);
        var expected = SpeechWaveformData.Prepare(Prepared(player.Audio!));
        Assert.Equal(expected.Stimulus.Waveform, page.StimulusPreview!.Waveform);
        var cachedStimulus = page.StimulusPreview;
        Assert.Equal(
            expected.Voice.SelectMany(p => new[] { p.Minimum, p.Maximum }),
            page.VoiceSamples.Select(p => p.Value)
        );
        Assert.Equal(
            expected
                .Envelope.Select(p => p.Amplitude)
                .Concat(expected.Envelope.Select(p => -p.Amplitude)),
            page.VoiceEnvelope.Select(p => p.Value)
        );
        Assert.Equal(
            expected
                .Envelope.Select(p => p.Seconds)
                .Concat(expected.Envelope.Select(p => p.Seconds)),
            page.VoiceEnvelope.Select(p => p.TimeSeconds)
        );
        Assert.Equal(
            new[] { expected.Envelope.Count },
            page.VoiceEnvelope.Select((p, index) => (p, index))
                .Where(x => x.p.StartsNewSegment)
                .Select(x => x.index)
        );
        Assert.Equal(player.Audio!.Duration.TotalSeconds, page.AudioDurationSeconds);
        page.SelectedCorpus = "MSP";
        page.SelectedCorpus = "MHINT-P";
        Assert.Equal("MHINT", page.SelectedCorpus);
        Assert.Equal("01", page.SentenceList);
        page.AllWrongCommand.Execute(null);
        Assert.Equal(10, page.WrongCount);
        page.ResetCommand.Execute(null);
        Assert.Equal("0/10", page.RatedText);
        Assert.False(page.SaveNextCommand.CanExecute(null));
        page.AllCorrectCommand.Execute(null);
        page.Words[1].WrongCommand.Execute(null);
        Assert.Equal("90%", page.AccuracyText);
        player.Complete();
        await page.ReplayCommand.ExecuteAsync(null);
        Assert.Equal(9, page.CorrectCount);
        Assert.Same(cachedStimulus, page.StimulusPreview);
        Assert.True(page.SaveNextCommand.CanExecute(null));
    }

    [Fact]
    public async Task FullListProducesCorrectResultsOnlyOnce()
    {
        var router = new Router();
        using var page = Create(router, new());
        page.SelectedCorpus = "MSP";
        await page.StartCommand.ExecuteAsync(null);
        for (var i = 0; i < 20; i++)
        {
            Assert.Contains($"第 {i + 1:00}", page.SentencePosition);
            Assert.All(page.Words, x => Assert.Null(x.IsCorrect));
            if (i == 1)
                page.AllWrongCommand.Execute(null);
            else
                page.AllCorrectCommand.Execute(null);
            await page.SaveNextCommand.ExecuteAsync(null);
            await page.SaveNextCommand.ExecuteAsync(null);
        }
        Assert.Null(router.Result);
        await page.FinishAsync(true);
        Assert.Equal(1, router.ResultNavigations);
        var result = new SpeechTrainingResultsPageViewModel(router.Result!);
        Assert.Equal("20/20", result.CompletedText);
        Assert.Equal(140, result.RatedCount);
        Assert.Equal(133, result.CorrectCount);
        Assert.Equal("95%", result.AccuracyText);
        Assert.Equal("节假日不用门票", router.Result!.Results[1].Text);
        await page.FinishAsync(true);
        Assert.Equal(1, router.ResultNavigations);
    }

    [Fact]
    public async Task EarlyExitOnlyIncludesCommittedSentencesAndNavigationStopsAudio()
    {
        var router = new Router();
        var player = new ControllableSpeechAudioPlayer();
        using var page = Create(router, player);
        await page.StartCommand.ExecuteAsync(null);
        page.OnNavigatedFrom();
        player.Complete();
        Assert.False(page.IsPlaying);
        Assert.False(page.CanScore);
        await page.ReplayCommand.ExecuteAsync(null);
        page.AllCorrectCommand.Execute(null);
        await page.SaveNextCommand.ExecuteAsync(null);
        page.AllWrongCommand.Execute(null);
        await page.FinishAsync(true);
        Assert.Single(router.Result!.Results);
        Assert.Equal(7, router.Result.Results[0].Correct);
        page.Dispose();
        Assert.True(player.Disposed);
        Assert.False(page.ReplayCommand.CanExecute(null));
    }

    [Fact]
    public void ResourceFailureIsVisibleAndCannotStart()
    {
        using var page = new EnvelopeStimulationPageViewModel(
            Route(),
            new Router(),
            () => throw new System.IO.FileNotFoundException("MSP/sentences.txt")
        );
        Assert.Contains("语料加载失败", page.StatusText);
        Assert.Empty(page.CorpusOptions);
        Assert.False(page.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResultsNavigateWithoutDeviceAndBackRestoresEndedSession()
    {
        var device = new DeviceSelectionContext();
        device.SetConnectedDevice("demo", "demo");
        MainViewModel? router = null;
        var factory = new PageFactory(
            (page, data) =>
                page switch
                {
                    ApplicationPageNames.EnvelopeStimulation =>
                        new EnvelopeStimulationPageViewModel(
                            (EnvelopeStimulationRouteData)data!,
                            router!,
                            () =>
                                new(
                                    new FileSpeechCorpusProvider(),
                                    new ControllableSpeechAudioPlayer()
                                )
                        ),
                    ApplicationPageNames.SpeechTrainingResults =>
                        new SpeechExperimentResultsPageViewModel(
                            (SpeechExperimentResultsRouteData)data!
                        ),
                    _ => new TestPage(page),
                }
        );
        router = new MainViewModel(factory, device);
        router.Navigate(ApplicationPageNames.ElectrodeConfiguration);
        router.Navigate(Route());
        var shell = Assert.IsType<BasicViewModel>(router.CurrentView);
        var training = Assert.IsType<EnvelopeStimulationPageViewModel>(shell.CurrentPage);
        await training.StartCommand.ExecuteAsync(null);
        device.ClearConnection();
        await training.FinishAsync(true);
        Assert.IsType<SpeechExperimentResultsPageViewModel>(shell.CurrentPage);
        router.GoBack();
        Assert.Same(training, shell.CurrentPage);
        Assert.True(training.IsEnded);
        router.GoBack();
        Assert.Equal(ApplicationPageNames.ElectrodeConfiguration, shell.CurrentPage.PageName);
        training.Dispose();
    }

    private static EnvelopeStimulationRouteData Route(double delay = 40)
    {
        var original = ExperimentRunRouteDataDefaults.Create();
        var config = original.StimulusConfiguration with
        {
            Kind = StimulusKind.EnvelopeTAcs,
            Targets = original
                .StimulusConfiguration.Targets.Take(1)
                .Select(t => t with { PeakCurrent = 0.5 })
                .ToArray(),
            Envelope = new EnvelopeParameters(1, 1, delay, false),
        };
        return new(
            new ExperimentRunRouteData(
                "EXP-DEMO",
                "SUBJECT-DEMO",
                config,
                original.StimulusElectrodes,
                [],
                "",
                "",
                1000,
                creationMode: ExperimentCreationMode.StimulusOnly
            ),
            "已完成"
        );
    }

    private sealed class TestPage(ApplicationPageNames page) : PageViewModel(page, "test");

    private sealed class Router : INavigationRouter
    {
        public SpeechTrainingResultsRouteData? Result { get; private set; }
        public int ResultNavigations { get; private set; }

        public void Navigate(SpeechExperimentResultsRouteData route)
        {
            var experiment = route.Snapshot!;
            Result = SpeechResultRoutes.Detail(experiment.Source, experiment.Rounds.Last());
            ResultNavigations++;
        }

        public void Navigate(SpeechTrainingResultsRouteData route)
        {
            Result = route;
            ResultNavigations++;
        }

        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData route) { }

        public void Navigate(StimulusConfigurationRouteData route) { }

        public void Navigate(ElectrodeConfigurationRouteData route) { }

        public void Navigate(ExperimentRunRouteData route) =>
            throw new InvalidOperationException("Must not start a real run.");

        public void Navigate(ExperimentRerunRouteData route) =>
            throw new InvalidOperationException("Must not start a real run.");

        public void GoBack() { }

        public void GoHome() { }
    }
}
