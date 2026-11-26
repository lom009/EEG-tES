using EGGtCSPlatform.Audio.Core.Buffers;

namespace EGGtCSPlatform.Audio.Core.Playback;

public enum AudioPlaybackState
{
    Playing,
    Completed,
    Failed,
}

public sealed record AudioPlaybackUpdate(
    long PlaybackId,
    AudioPlaybackState State,
    TimeSpan Position,
    string? Error = null
);

public interface IAudioPlayer : IDisposable
{
    event EventHandler<AudioPlaybackUpdate>? Updated;
    double Volume { get; set; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    Task PlayAsync(
        AudioBuffer audio,
        long playbackId,
        CancellationToken cancellationToken = default
    );
    void Stop();
}
