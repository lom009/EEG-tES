using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechTrainingSessionTests
{
    [Fact]
    public async Task ResponseSuggestionsRequireCurrentCompletedPlaybackAndPreserveManualScores()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new PreparationProvider(), player);
        await session.StartAsync();
        Assert.Null(session.ResponsePlaybackId);
        player.Complete();
        var playbackId = Assert.IsType<long>(session.ResponsePlaybackId);
        var suggestions = Enumerable.Repeat<bool?>(true, session.Answers.Count).ToArray();
        session.Rate(0, false);
        Assert.True(session.TryApplyResponseSuggestion(playbackId, suggestions));
        Assert.False(session.Answers[0]);
        Assert.True(session.Answers[1]);
        session.Rate(1, false);
        session.ClearResponseSuggestion(playbackId);
        Assert.False(session.Answers[0]);
        Assert.False(session.Answers[1]);
        Assert.Null(session.Answers[2]);
        await session.ReplayAsync();
        Assert.False(session.TryApplyResponseSuggestion(playbackId, suggestions));
        player.Complete();
        Assert.False(session.TryApplyResponseSuggestion(playbackId, suggestions));
    }

    [Fact]
    public async Task ParametersStayLockedThroughPlaybackCompletionReplayAndFailure()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new PreparationProvider(), player);
        session.SetSpeechRate(2);
        session.SetVocoder(new(Bands: 1, Maxima: 1));
        var initial = session.AudioSettings;
        await session.StartAsync();
        player.Complete();
        session.RateAll(true);
        var first = session.Waveform;
        session.SetSpeechRate(4);
        session.SetVocoder(null);
        Assert.False(session.CanAdjustSpeechRate);
        Assert.False(session.CanAdjustCarrier);
        Assert.Equal(initial, session.AudioSettings);
        await session.ReplayAsync();
        Assert.Same(first, session.Waveform);
        player.Complete();
        await session.SaveNextAsync();
        Assert.Equal(initial, session.CurrentAudioSettings);
        player.Fail();
        session.SetSpeechRate(5);
        session.SetVocoder(null);
        Assert.Equal(initial, session.AudioSettings);
        await session.ReplayAsync();
        session.End();
        Assert.Equal(initial, session.Result!.Settings.AudioSettings);
        Assert.Equal(initial, Assert.Single(session.Result.Sentences).AudioSnapshot!.Settings);
    }

    [Fact]
    public async Task FailedDurableSubmissionCanRetryWithoutAdvancingSentenceOrAlgorithm()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new PreparationProvider(), player);
        var attempts = 0;
        session.PersistAsync = (snapshot, ended) =>
        {
            if (snapshot.Sentences.Count == 1 && attempts++ == 0)
                throw new IOException("disk unavailable");
            return Task.CompletedTask;
        };
        await session.StartAsync();
        player.Complete();
        session.RateAll(true);
        await Assert.ThrowsAsync<IOException>(() => session.SaveNextAsync());
        Assert.Equal(0, session.SentenceIndex);
        Assert.Empty(session.Snapshot().Sentences);
        Assert.All(session.Answers, x => Assert.True(x));
        Assert.True(session.CanSaveNext);
        Assert.False(session.CanReplay);
        Assert.False(session.CanScore);
        session.RateAll(false);
        Assert.All(session.Answers, x => Assert.True(x));
        await session.SaveNextAsync();
        Assert.Equal(1, session.SentenceIndex);
        Assert.Single(session.Snapshot().Sentences);
        Assert.Equal(2, attempts);
        Assert.Null(session.PersistenceError);
    }

    [Fact]
    public async Task ScoringRuleFreezesForRoundAndDuplicateSubmissionsWaitForDurability()
    {
        using var session = new SpeechTrainingSession(
            new PreparationProvider(),
            new ControllableSpeechAudioPlayer()
        );
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        session.SetScoringRule(new(7, 4));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        session.PersistAsync = async (snapshot, _) =>
        {
            if (snapshot.Sentences.Count == 0)
                return;
            count++;
            entered.TrySetResult();
            await release.Task;
        };
        await session.StartAsync();
        session.SetScoringRule(new(7, 7));
        Assert.Equal(new(7, 4), session.ScoringRule);
        session.RateAll(true);
        var submit = session.SaveNextAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, session.SentenceIndex);
        await session.SaveNextAsync();
        Assert.Equal(1, count);
        release.TrySetResult();
        await submit;
        Assert.Single(session.Snapshot().Sentences);
        Assert.Equal(new(7, 4), session.Snapshot().Settings.AdaptiveScoring);
    }

    [Fact]
    public async Task StoppedPreparationCannotPublishAndDoesNotUnlockParameters()
    {
        var provider = new PreparationProvider { BlockFirst = true };
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(provider, player);
        var first = session.StartAsync();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Stop();
        session.SetSpeechRate(2);
        session.SetVocoder(new());
        Assert.False(session.CanAdjustSpeechRate);
        Assert.False(session.CanAdjustCarrier);
        Assert.Equal(0, session.SpeechRate);
        await session.ReplayAsync();
        var waveform = session.Waveform;
        provider.Release.TrySetResult();
        await first;
        Assert.Same(waveform, session.Waveform);
        Assert.Equal(1, player.PlayCount);
        Assert.Null(session.CurrentAudioSettings!.Vocoder);
    }

    [Fact]
    public async Task RateConfigurationPlaybackReferenceAndResultsStayConsistent()
    {
        var provider = new FileSpeechCorpusProvider();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(provider, player);
        session.SetSpeechRate(2);
        var configuration = session.AudioSettings;
        await session.StartAsync();
        Assert.Null(session.Error);
        var first = session.Waveform!;
        Assert.Same(first.Audio, player.Audio);
        Assert.Same(first.Audio, first.Preparation!.EnvelopeSource.Audio);
        Assert.Equal(3.5, first.Audio.Duration.TotalSeconds, 6);
        var recomputed = TestAudio.Display(player.Audio!);
        Assert.Equal(recomputed.Envelope, first.Envelope);
        Assert.Equal(recomputed.Stimulus.Waveform, first.Stimulus.Waveform);
        session.SetSpeechRate(4);
        Assert.Equal(2, session.SpeechRate);
        Assert.Same(configuration, session.AudioSettings);
        player.Complete();
        await session.ReplayAsync();
        Assert.Same(first, session.Waveform);
        session.RateAll(true);
        await session.SaveNextAsync();
        Assert.NotSame(first, session.Waveform);
        session.End();
        Assert.Equal(2, session.Result!.Settings.SpeechRate);
        Assert.Same(configuration, session.Result.Settings.AudioSettings);
        Assert.Equal(5, session.Result.Sentences[0].AudioSnapshot!.Nodes.Length);
        Assert.DoesNotContain(
            session.Result.Sentences[0].AudioSnapshot!.Nodes,
            node => node.ProcessorIdentity == "butterworth-filtfilt-1"
        );
        Assert.Equal(
            "solafs-1",
            session.Result.Sentences[0].AudioSnapshot!.Nodes[0].ProcessorIdentity
        );
    }

    [Fact]
    public async Task InjectedPreparationIsCancelledAndCannotPublishAfterExit()
    {
        var processor = new BlockingPreparation();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            new PreparationProvider(),
            player,
            preparation: processor
        );
        var start = session.StartAsync();
        await processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Dispose();
        Assert.True(processor.Token.IsCancellationRequested);
        processor.Release.TrySetResult();
        await start;
        Assert.Null(session.Waveform);
        Assert.Equal(0, player.PlayCount);
    }

    private sealed class BlockingPreparation : TestPreparationBase
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public override TrainingAudioPreparationResult Prepare(
            TrainingAudioRequest request,
            TrainingAudioPreparationResult? cached = null,
            CancellationToken token = default
        )
        {
            Token = token;
            Entered.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(10));
            return Service.Prepare(request with { Settings = new() }); // Deliberately ignores cancellation: session must still reject it.
        }
    }

    [Fact]
    public async Task ReplayReusesPreparedWaveformAndNextSentenceReplacesIt()
    {
        var provider = new PreparationProvider();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(provider, player);
        await session.StartAsync();
        var first = session.Waveform;
        Assert.NotNull(first);
        Assert.Same(player.Audio, first.Audio);
        Assert.NotEmpty(first.Envelope);
        Assert.NotEmpty(first.Stimulus.Waveform);
        player.Complete();
        await session.ReplayAsync();
        Assert.Same(first, session.Waveform);
        Assert.Same(first.Stimulus, session.Waveform!.Stimulus);
        Assert.Equal(2, provider.ReadCount); // Source identity is rechecked on replay; DSP/display stay cached.
        session.RateAll(true);
        var clearedBeforeNext = false;
        session.Changed += (_, _) =>
        {
            if (session.SentenceIndex == 1 && session.IsLoading && session.Waveform is null)
                clearedBeforeNext = true;
        };
        await session.SaveNextAsync();
        Assert.True(clearedBeforeNext);
        Assert.NotSame(first, session.Waveform);
        Assert.NotSame(first.Stimulus, session.Waveform!.Stimulus);
        Assert.Equal(3, provider.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalePreparationCannotReplaceReplayOrPublishAfterDisposal(bool dispose)
    {
        var provider = new PreparationProvider { BlockFirst = true };
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(provider, player);
        var first = session.StartAsync();
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (dispose)
                session.Dispose();
            else
            {
                session.Stop();
                await session.ReplayAsync();
                Assert.NotNull(session.Waveform);
            }
            var current = session.Waveform;
            provider.Release.TrySetResult();
            await first;
            Assert.Same(current, session.Waveform);
            Assert.Equal(dispose ? 0 : 1, player.PlayCount);
            Assert.Equal(!dispose, session.IsPlaying);
        }
        finally
        {
            provider.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task EnvelopePreparationFailureIsReportedAndRetryDoesNotUsePeakFallback()
    {
        var provider = new PreparationProvider { InvalidAudio = true };
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(provider, player);
        await session.StartAsync();
        Assert.Contains("需要有效的 PCM16", session.Error);
        Assert.Null(session.Waveform);
        Assert.Equal(0, player.PlayCount);
        provider.InvalidAudio = false;
        await session.ReplayAsync();
        Assert.Null(session.Error);
        Assert.NotNull(session.Waveform);
        Assert.Equal(1, player.PlayCount);
    }

    private sealed class PreparationProvider : ISpeechCorpusProvider
    {
        private readonly FileSpeechCorpusProvider _inner = new();
        public IReadOnlyList<SpeechCorpus> Corpora => _inner.Corpora;
        public int ReadCount;
        public bool InvalidAudio,
            BlockFirst;
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AudioBuffer ReadAudio(SpeechSentence sentence)
        {
            if (Interlocked.Increment(ref ReadCount) == 1 && BlockFirst)
            {
                Entered.TrySetResult();
                Release.Task.Wait(TimeSpan.FromSeconds(10));
            }
            return new(16000, 1, InvalidAudio ? [0] : new byte[2050]);
        }
    }

    [Fact]
    public async Task VolumeChangesPreservePlaybackScoringAndSelection()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        Assert.Equal(1, session.Volume);
        var changes = 0;
        session.Changed += (_, _) => changes++;
        session.Volume = 0.4;
        session.SelectCorpus("MHINT-P");
        Assert.Equal(0.4, session.Volume);
        await session.StartAsync();
        session.RateAll(true);
        var position = session.Position;
        var stops = player.StopCount;
        session.Volume = 0;
        Assert.True(session.IsPlaying);
        Assert.True(session.CanSaveNext);
        Assert.Equal(position, session.Position);
        Assert.Equal(stops, player.StopCount);
        Assert.Equal(1, player.PlayCount);
        player.FailVolume = true;
        session.Volume = 0.8;
        Assert.Equal(0, session.Volume);
        Assert.Contains("音量调节失败", session.VolumeError);
        Assert.True(session.CanSaveNext);
        player.FailVolume = false;
        session.Volume = 1;
        Assert.Null(session.VolumeError);
        player.Complete();
        await session.ReplayAsync();
        Assert.Equal(1, session.Volume);
        session.Volume = 0.3;
        await session.SaveNextAsync();
        Assert.Equal(0.3, player.Volume);
        Assert.True(changes > 0);
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Volume = 0.5);
        using var next = new SpeechTrainingSession(
            new FileSpeechCorpusProvider(),
            new ControllableSpeechAudioPlayer()
        );
        Assert.Equal(1, next.Volume);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidVolumeIsRejected(double volume)
    {
        using var session = new SpeechTrainingSession(
            new FileSpeechCorpusProvider(),
            new ControllableSpeechAudioPlayer()
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Volume = volume);
        Assert.Equal(1, session.Volume);
    }

    [Fact]
    public async Task SelectionScoringReplayAndFullSessionUseRealList()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        session.SelectCorpus("MHINT-P");
        session.SelectSentenceList("02");
        Assert.Equal("MHINT-P/021", session.CurrentSentence.Id);
        session.SelectCorpus("MSP");
        Assert.Equal("01", session.SentenceList.Id);
        session.SelectSentenceList("05");
        await session.StartAsync();
        session.SelectCorpus("MHINT");
        Assert.Equal("MSP", session.Corpus.Id);
        session.Rate(0, true);
        Assert.False(session.CanSaveNext);
        player.Complete();
        player.Emit(player.PlaybackId, AudioPlaybackState.Playing, TimeSpan.Zero);
        Assert.False(session.IsPlaying);
        await session.ReplayAsync();
        Assert.True(session.Answers[0]);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(i, session.SentenceIndex);
            session.RateAll(i % 2 == 0);
            await session.SaveNextAsync();
            await session.SaveNextAsync();
        }
        Assert.True(session.Ended);
        Assert.Equal(20, session.Result!.Sentences.Count);
        Assert.Equal(140, session.Result.RatedCount);
        Assert.Equal(70, session.Result.CorrectCount);
        Assert.Equal(0.5, session.Result.Accuracy);
        var result = session.Result;
        session.End();
        await session.ReplayAsync();
        Assert.Same(result, session.Result);
    }

    [Fact]
    public async Task OldCallbacksCannotCompleteNextSentenceOrResumeStoppedSession()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        await session.StartAsync();
        var old = player.PlaybackId;
        session.RateAll(true);
        await session.SaveNextAsync();
        player.Emit(old, AudioPlaybackState.Completed, TimeSpan.FromHours(1));
        Assert.True(session.IsPlaying);
        Assert.Equal(TimeSpan.Zero, session.Position);
        session.Stop();
        player.Complete();
        Assert.False(session.CanScore);
        await session.ReplayAsync();
        Assert.True(session.CanScore);
        session.Dispose();
        player.Complete();
        Assert.True(player.Disposed);
        Assert.False(session.CanScore);
    }

    [Fact]
    public async Task FailureCanBeRetriedWithoutSkippingOrLosingAnswers()
    {
        var player = new ControllableSpeechAudioPlayer { FailStart = true };
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        await session.StartAsync();
        Assert.NotNull(session.Error);
        Assert.False(session.CanScore);
        Assert.True(session.CanReplay);
        player.FailStart = false;
        await session.ReplayAsync();
        session.Rate(0, true);
        player.Fail();
        Assert.False(session.CanSaveNext);
        Assert.Equal(0, session.SentenceIndex);
        await session.ReplayAsync();
        Assert.Null(session.Error);
        Assert.True(session.Answers[0]);
        session.End();
        Assert.Empty(session.Result!.Sentences);
    }

    [Fact]
    public async Task FileReadFailureStaysOnSameSentenceUntilRetry()
    {
        var provider = new FailingProvider();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(provider, player);
        await session.StartAsync();
        Assert.Contains("test corrupt WAV", session.Error);
        Assert.False(session.CanScore);
        Assert.Equal(0, player.PlayCount);
        Assert.Equal(0, session.SentenceIndex);
        provider.Fail = false;
        await session.ReplayAsync();
        Assert.Null(session.Error);
        Assert.True(session.IsPlaying);
        Assert.Equal(0, session.SentenceIndex);
    }

    private sealed class FailingProvider : ISpeechCorpusProvider
    {
        private readonly FileSpeechCorpusProvider _inner = new();
        public bool Fail { get; set; } = true;
        public IReadOnlyList<SpeechCorpus> Corpora => _inner.Corpora;

        public AudioBuffer ReadAudio(SpeechSentence sentence) =>
            Fail ? throw new InvalidDataException("test corrupt WAV") : _inner.ReadAudio(sentence);
    }

    [Fact]
    public async Task EndingDuringLoadNeverStartsOutput()
    {
        var provider = new BlockingProvider();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(provider, player);
        var start = session.StartAsync();
        Assert.True(session.IsLoading);
        session.End();
        provider.Release.Set();
        await start;
        Assert.Equal(0, player.PlayCount);
        Assert.Empty(session.Result!.Sentences);
    }

    private sealed class BlockingProvider : ISpeechCorpusProvider
    {
        private readonly FileSpeechCorpusProvider _inner = new();
        public ManualResetEventSlim Release { get; } = new();
        public IReadOnlyList<SpeechCorpus> Corpora => _inner.Corpora;

        public AudioBuffer ReadAudio(SpeechSentence sentence)
        {
            Release.Wait(TimeSpan.FromSeconds(5));
            return _inner.ReadAudio(sentence);
        }
    }
}
