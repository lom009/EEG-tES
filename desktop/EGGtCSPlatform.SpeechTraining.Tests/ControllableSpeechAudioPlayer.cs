using System;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.TestSupport;

// Linked into test/visual hosts only. Production always uses the SDL implementation.
public sealed class ControllableSpeechAudioPlayer : IAudioPlayer
{
    public event EventHandler<AudioPlaybackUpdate>? Updated;
    public long PlaybackId { get; private set; }
    public AudioBuffer? Audio { get; private set; }
    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }
    public bool Disposed { get; private set; }
    public bool FailStart { get; set; }
    public bool FailVolume { get; set; }
    private double _volume = 1;
    public double Volume
    {
        get => _volume;
        set
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (!double.IsFinite(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (FailVolume)
                throw new InvalidOperationException("test volume failure");
            _volume = value;
        }
    }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration => Audio?.Duration ?? TimeSpan.Zero;

    public Task PlayAsync(
        AudioBuffer audio,
        long playbackId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailStart)
            throw new InvalidOperationException("test output unavailable");
        Audio = audio;
        Position = TimeSpan.Zero;
        PlaybackId = playbackId;
        PlayCount++;
        return Task.CompletedTask;
    }

    public void Advance(TimeSpan position) =>
        Emit(
            PlaybackId,
            position >= Audio!.Duration ? AudioPlaybackState.Completed : AudioPlaybackState.Playing,
            position
        );

    public void Complete() => Advance(Audio!.Duration);

    public void Fail() =>
        Emit(PlaybackId, AudioPlaybackState.Failed, TimeSpan.Zero, "test device lost");

    public void Emit(long id, AudioPlaybackState state, TimeSpan position, string? error = null)
    {
        if (id == PlaybackId)
            Position = position;
        Updated?.Invoke(this, new(id, state, position, error));
    }

    public void Stop() => StopCount++;

    public void Dispose() => Disposed = true;
}
