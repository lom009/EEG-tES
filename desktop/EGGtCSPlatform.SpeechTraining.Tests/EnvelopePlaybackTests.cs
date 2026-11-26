using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class EnvelopePlaybackTests
{
    private static SpeechWaveformData Waveform()
    {
        var audio = new AudioBuffer(16000, 1, new byte[1600]);
        return TestAudio.Display(audio) with
        {
            Stimulus = SpeechStimulusProcessor.BuildPreview([0, .5, 1, .5, 0], .05),
        };
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(800)]
    [InlineData(1000)]
    public void PreviewUsesWireCurrentsAndConfiguredCarrier(int frequency)
    {
        var plan = EnvelopePlaybackPlan.Create(7, Waveform(), new(2, 300, (ushort)frequency, 3));
        Assert.Equal(new byte[] { 0, 30, 60, 30, 0 }, plan.Samples);
        Assert.Equal(1.98, plan.Preview.Amplitudes.Max(), 10);
        Assert.Equal(.35, plan.DisplayDurationSeconds, 10);
        for (var i = 0; i < plan.Samples.Length; i++)
            Assert.Equal((double)(plan.Samples[i] * .033m), plan.Preview.Amplitudes[i], 10);
        foreach (var point in plan.Preview.Waveform.Select((p, i) => (p, i)))
        {
            var upper = plan.Preview.Upper[point.i].Value;
            Assert.Equal(
                upper * Math.Sin(2 * Math.PI * frequency * point.p.Seconds),
                point.p.Value,
                10
            );
            Assert.InRange(Math.Abs(point.p.Value), 0, 1.98 + 1e-10);
            if (point.p.Seconds < .01 || point.p.Seconds > .04)
                Assert.Equal(0, point.p.Value);
        }
    }

    [Fact]
    public void ChangingFrequencyDoesNotChangeBytesAndInvalidSettingsAreRejected()
    {
        var wave = Waveform();
        var low = EnvelopePlaybackPlan.Create(1, wave, new(2, 0, 200, 3));
        var high = EnvelopePlaybackPlan.Create(2, wave, new(2, 0, 1000, 3));
        Assert.Equal(low.Samples, high.Samples);
        Assert.True(high.Preview.Waveform.Count > low.Preview.Waveform.Count);
        foreach (
            var invalid in new EnvelopePlaybackSettings[]
            {
                new(2, 0, 199, 3),
                new(2, 0, 1001, 3),
                new(2, 301, 500, 3),
                new(2, 0, 500, 0),
                new(9, 0, 500, 3),
            }
        )
            Assert.ThrowsAny<ArgumentException>(() =>
                EnvelopePlaybackPlan.Create(1, wave, invalid)
            );
    }

    [Fact]
    public async Task AudioWaitsForAckAndNextSentenceWaitsForStimulusAndStop()
    {
        var player = new ControllableSpeechAudioPlayer();
        var output = new Output();
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        session.AttachStimulation(output, new(2, 0, 500, 3));
        var playback = session.StartAsync();
        await output.StartRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, player.PlayCount);
        Assert.Null(session.TimelineAnimation);
        Assert.False(session.CanAdjustStimulation);
        output.StartAck.SetResult();
        await Until(() => player.PlayCount == 1);
        session.RateAll(true);
        Assert.False(session.CanSaveNext);
        player.Complete();
        await output.CompletionRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.CanReplay);
        Assert.False(session.CanSaveNext);
        output.Completed.SetResult();
        await output.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.CanSaveNext);
        output.StopAck.SetResult();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.CanSaveNext);
        Assert.True(session.CanReplay);
        Assert.Equal(new[] { "configure", "start", "wait", "stop" }, output.Calls);
        var first = session.StimulusPlan!;
        var prepared = session.Waveform;
        await Task.WhenAll(
            Enumerable
                .Range(200, 601)
                .Where(f => f % 100 == 0)
                .Select(f => session.ConfigureStimulationAsync(new(2, 125, (ushort)f, 3)))
        );
        Assert.Same(prepared, session.Waveform); // Parameter edits never rerun audio DSP.
        Assert.Equal(first.Samples, session.StimulusPlan!.Samples);
        Assert.Equal(500, session.StimulusPlan.Settings.DebugFrequencyHz);
        var replay = session.ReplayAsync();
        await Until(() => player.PlayCount == 2);
        Assert.Equal(500, output.LastPlan!.Settings.DebugFrequencyHz);
        player.Complete();
        await replay.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, output.Calls.Count(x => x == "configure"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrEarlyEndStopsAudioAndAwaitsIndependentStop(bool disconnect)
    {
        var player = new ControllableSpeechAudioPlayer();
        var output = new Output();
        output.StartAck.SetResult();
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        session.AttachStimulation(output, new(2, 300, 500, 3));
        var playback = session.StartAsync();
        await Until(() => player.PlayCount == 1);
        var stops = player.StopCount;
        Task? ending = null;
        if (disconnect)
            output.Disconnect();
        else
            ending = session.EndAsync();
        await output.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(player.StopCount > stops);
        Assert.True(session.IsOutputBusy);
        Assert.False(session.Ended);
        Assert.False(session.CanSaveNext);
        output.StopAck.SetResult();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        if (ending is not null)
            await ending;
        Assert.Equal(!disconnect, session.Ended);
        if (disconnect)
            Assert.Contains("断连", session.Error);
    }

    [Fact]
    public void PartialWindowAndLengthLimitArePreservedAndInvalidNodesFail()
    {
        var wave = Waveform();
        // PCM is whole frames: 400 frames = 25 ms, rounded to three device samples.
        var partial = wave with
        {
            Audio = new AudioBuffer(16000, 1, new byte[800]),
        };
        var plan = EnvelopePlaybackPlan.Create(1, partial, new(2, 0, 500, 3));
        Assert.Equal(3, plan.Samples.Length);
        Assert.Equal(.03, plan.Duration.TotalSeconds, 10);
        var tooLong = wave with { Audio = new AudioBuffer(8000, 1, new byte[65519 * 160]) };
        Assert.Throws<ArgumentException>(() =>
            EnvelopePlaybackPlan.Create(1, tooLong, new(2, 0, 500, 3))
        );
        var invalid = wave with
        {
            Stimulus = wave.Stimulus with { Amplitudes = [0, double.NaN, 1] },
        };
        Assert.Throws<ArgumentException>(() =>
            EnvelopePlaybackPlan.Create(1, invalid, new(2, 0, 500, 3))
        );
    }

    [Theory]
    [InlineData("configure")]
    [InlineData("start")]
    [InlineData("audio")]
    [InlineData("cancel")]
    public async Task FailuresAndCancellationNeverContinueWithAudioOrNextSentence(string failure)
    {
        var player = new ControllableSpeechAudioPlayer { FailStart = failure == "audio" };
        var output = new Output { Failure = failure };
        output.StopAck.SetResult();
        if (failure != "cancel")
            output.StartAck.SetResult();
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        session.AttachStimulation(output, new(2, 0, 500, 3));
        var playback = session.StartAsync();
        if (failure == "cancel")
        {
            await output.StartRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await session.StopAsync();
            output.StartAck.TrySetResult(); // Late ACK must not restart output.
        }
        await playback.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, player.PlayCount);
        Assert.Contains("stop", output.Calls);
        Assert.False(session.CanSaveNext);
        Assert.False(session.IsOutputBusy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public async Task TimelineReachesDisplayEndWhileAudioTimeAndCompletionGatesStayUnchanged(ushort delay)
    {
        var player = new ControllableSpeechAudioPlayer();
        var output = new Output();
        output.StartAck.SetResult();
        output.StopAck.SetResult();
        using var session = new SpeechTrainingSession(new PartialWindowCorpus(), player);
        session.AttachStimulation(output, new(2, delay, 500, 3));
        var playback = session.StartAsync();
        await Until(() => player.PlayCount == 1);
        await Until(() => session.TimelineAnimation is not null);
        var animation = session.TimelineAnimation;
        Assert.Equal(TimeSpan.Zero, animation!.Start);
        player.Advance(session.Duration / 2);
        Assert.Same(animation, session.TimelineAnimation);
        session.RateAll(true);
        var end = TimeSpan.FromSeconds(session.StimulusPlan!.DisplayDurationSeconds);
        Assert.True(end > session.Duration + TimeSpan.FromMilliseconds(delay));
        player.Complete();
        Assert.Same(animation, session.TimelineAnimation);
        if (delay > 0)
        {
            await Until(() => session.TimelinePosition > session.Duration);
            Assert.True(session.TimelinePosition < end);
        }
        await Until(() => session.TimelinePosition == end);
        Assert.Equal(session.Duration, session.Position);
        Assert.False(session.CanReplay);
        Assert.False(session.CanSaveNext);
        output.Completed.SetResult();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.CanReplay);
        Assert.True(session.CanSaveNext);
        Assert.Equal(end, session.TimelinePosition);
    }

    [Fact]
    public async Task DrainingAudioDoesNotPauseOrRestartTailOrRefreshTrainingState()
    {
        var player = new ControllableSpeechAudioPlayer();
        var output = new Output();
        output.StartAck.SetResult();
        output.StopAck.SetResult();
        using var session = new SpeechTrainingSession(new PartialWindowCorpus(), player);
        session.AttachStimulation(output, new(2, 300, 500, 3));
        var playback = session.StartAsync();
        await Until(() => session.IsPlaying);
        // The native player reaches the last PCM frame before its Completed notification.
        player.Emit(player.PlaybackId, AudioPlaybackState.Playing, session.Duration);
        var stateUpdates = 0;
        session.Changed += (_, _) => Interlocked.Increment(ref stateUpdates);
        var end = TimeSpan.FromSeconds(session.StimulusPlan!.DisplayDurationSeconds);
        await Until(() => session.TimelinePosition > session.Duration);
        Assert.True(session.IsPlaying);
        Assert.False(session.CanReplay);
        await Until(() => session.TimelinePosition == end);
        Assert.Equal(0, Volatile.Read(ref stateUpdates));
        player.Emit(player.PlaybackId, AudioPlaybackState.Playing, session.Duration);
        Assert.Equal(end, session.TimelinePosition);
        player.Complete();
        Assert.Equal(end, session.TimelinePosition);
        output.Completed.SetResult();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(end, session.TimelinePosition);
    }

    [Fact]
    public async Task EarlyDeviceCompletionDoesNotCancelTailAndReplayResetsIt()
    {
        var player = new ControllableSpeechAudioPlayer();
        var output = new Output();
        output.StartAck.SetResult();
        output.Completed.SetResult();
        output.StopAck.SetResult();
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        session.AttachStimulation(output, new(2, 300, 500, 3));
        var playback = session.StartAsync();
        await Until(() => player.PlayCount == 1);
        player.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        var end = TimeSpan.FromSeconds(session.StimulusPlan!.DisplayDurationSeconds);
        await Until(() => session.TimelinePosition == end);
        var replay = session.ReplayAsync();
        Assert.Equal(TimeSpan.Zero, session.TimelinePosition);
        await Until(() => player.PlayCount == 2);
        player.Complete();
        await replay.WaitAsync(TimeSpan.FromSeconds(5));
        // Start again during the preceding tail; its late callbacks must not move the new playhead.
        var nextReplay = session.ReplayAsync();
        await Until(() => player.PlayCount == 3);
        var newAnimation = session.TimelineAnimation;
        await Task.Delay(350);
        Assert.Same(newAnimation, session.TimelineAnimation);
        Assert.True(session.TimelinePosition > TimeSpan.Zero);
        Assert.True(session.TimelinePosition < session.Duration);
        player.Complete();
        await nextReplay.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("fault")]
    [InlineData("end")]
    [InlineData("dispose")]
    public async Task InterruptingTailFreezesProgress(string action)
    {
        var player = new ControllableSpeechAudioPlayer();
        var output = new Output();
        output.StartAck.SetResult();
        output.StopAck.SetResult();
        using var session = new SpeechTrainingSession(new FileSpeechCorpusProvider(), player);
        session.AttachStimulation(output, new(2, 300, 500, 3));
        var playback = session.StartAsync();
        await Until(() => player.PlayCount == 1);
        player.Complete();
        await Until(() => session.TimelinePosition > session.Duration);
        switch (action)
        {
            case "stop": await session.StopAsync(); break;
            case "fault": output.Disconnect(); break;
            case "end": await session.EndAsync(); break;
            case "dispose": session.Dispose(); break;
        }
        var stoppedAt = session.TimelinePosition;
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(350);
        Assert.Equal(stoppedAt, session.TimelinePosition);
        Assert.True(stoppedAt.TotalSeconds < session.StimulusPlan!.DisplayDurationSeconds);
    }

    private sealed class PartialWindowCorpus : ISpeechCorpusProvider
    {
        public IReadOnlyList<SpeechCorpus> Corpora { get; } = new FileSpeechCorpusProvider().Corpora;

        // 1.0025 seconds leaves a partial 100 Hz stimulus sample even with zero delay.
        public AudioBuffer ReadAudio(SpeechSentence sentence) =>
            SpeechAudioPreparationTests.Tone(frames: 16040);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class Output : ISpeechStimulationOutput
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
        public event EventHandler<string>? Faulted;

        public void Disconnect() => Faulted?.Invoke(this, "设备断连");

        public string? Status => null;
        public string? Failure { get; init; }
        public List<string> Calls { get; } = [];
        public EnvelopePlaybackPlan? LastPlan { get; private set; }
        public TaskCompletionSource StartRequested { get; } = NewSource();
        public TaskCompletionSource StartAck { get; } = NewSource();
        public TaskCompletionSource CompletionRequested { get; } = NewSource();
        public TaskCompletionSource Completed { get; } = NewSource();
        public TaskCompletionSource StopRequested { get; } = NewSource();
        public TaskCompletionSource StopAck { get; } = NewSource();

        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PrepareAsync(EnvelopePlaybackPlan plan, CancellationToken token)
        {
            Calls.Add("configure");
            LastPlan = plan;
            if (Failure == "configure")
                throw new InvalidOperationException("configuration rejected");
            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken token)
        {
            Calls.Add("start");
            StartRequested.TrySetResult();
            if (Failure == "start")
                throw new InvalidOperationException("start rejected");
            await StartAck.Task.WaitAsync(token);
        }

        public async Task WaitForCompletionAsync(CancellationToken token)
        {
            Calls.Add("wait");
            CompletionRequested.TrySetResult();
            await Completed.Task.WaitAsync(token);
        }

        public async Task StopAsync()
        {
            Calls.Add("stop");
            StopRequested.TrySetResult();
            await StopAck.Task;
        }
    }
}
