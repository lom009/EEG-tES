using System.Diagnostics;
using EGGtCSPlatform.Audio.Sdl;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class AudioDeviceFactAttribute : FactAttribute
{
    public AudioDeviceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPEECH_AUDIO_DEVICE_TEST") != "1")
            Skip = "Set SPEECH_AUDIO_DEVICE_TEST=1 on a machine with an audio output device.";
    }
}

public sealed class SdlAudioDeviceTests
{
    [AudioDeviceFact]
    [Trait("Category", "AudioHardware")]
    public async Task DefaultDevicePlaysMspAndMhintAndStopsOnNavigation()
    {
        var provider = new FileSpeechCorpusProvider();
        using var session = new SpeechTrainingSession(provider, new SdlAudioPlayer());
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, _) =>
        {
            if (session.Error is { } error)
                complete.TrySetException(new IOException(error));
            else if (
                session.Started
                && !session.IsLoading
                && !session.IsPlaying
                && session.Position == session.Duration
                && session.Duration > TimeSpan.Zero
            )
                complete.TrySetResult();
        };
        var clock = Stopwatch.StartNew();
        session.Volume = 0;
        await session.StartAsync();
        await Task.Delay(150);
        var position = session.Position;
        session.Volume = 0.5;
        Assert.True(session.IsPlaying);
        Assert.True(session.Position >= position);
        session.Volume = 1;
        Assert.Null(session.VolumeError);
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(clock.Elapsed >= session.Duration * 0.8);
        await session.ReplayAsync();
        await Task.Delay(100);
        session.Stop();
        await Task.Delay(150);
        Assert.False(session.IsPlaying);
        Assert.False(session.CanScore);
        using var mhint = new SpeechTrainingSession(provider, new SdlAudioPlayer());
        mhint.SelectCorpus("MHINT-P");
        mhint.SelectSentenceList("02");
        await mhint.StartAsync();
        Assert.Null(mhint.Error);
        Assert.True(mhint.IsPlaying);
        var mhintId = mhint.CurrentSentence.Id;
        mhint.RateAll(true);
        await mhint.SaveNextAsync();
        Assert.NotEqual(mhintId, mhint.CurrentSentence.Id);
        Assert.Null(mhint.Error);
        await Task.Delay(200);
        mhint.End();
        Assert.False(mhint.IsPlaying);
    }
}
