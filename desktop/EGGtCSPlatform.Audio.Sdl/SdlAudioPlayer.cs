using System.Diagnostics;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Playback;
using SDL3;

namespace EGGtCSPlatform.Audio.Sdl;

/// <summary>Default-device PCM playback. Only the SDL audio subsystem is acquired; no window/event loop is installed.</summary>
public sealed class SdlAudioPlayer : IAudioPlayer
{
    private static readonly object LifecycleGate = new();
    private readonly object _gate = new();
    private nint _stream;
    private double _volume = 1;
    private Timer? _timer;
    private bool _initialized,
        _disposed;
    private long _playbackId;
    private AudioBuffer? _audio;
    private int _lastQueued;
    private long _lastProgress,
        _drainedAt;
    private TimeSpan _drainDelay;
    private CancellationToken _cancellation;
    private TimeSpan _position,
        _duration;
    public event EventHandler<AudioPlaybackUpdate>? Updated;
    public double Volume
    {
        get
        {
            lock (_gate)
                return _volume;
        }
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_stream != 0)
                    Check(SDL.SetAudioStreamGain(_stream, (float)value));
                _volume = value;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
                return _position;
        }
    }
    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
                return _duration;
        }
    }

    /// <summary>Create on the application's main thread, as required by SDL audio initialization.</summary>
    public SdlAudioPlayer()
    {
        lock (LifecycleGate)
            Check(SDL.InitSubSystem(SDL.InitFlags.Audio));
        _initialized = true;
    }

    public Task PlayAsync(
        AudioBuffer audio,
        long playbackId,
        CancellationToken cancellationToken = default
    )
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            StopCore();
            _position = TimeSpan.Zero;
            _duration = audio.Duration;
            try
            {
                var spec = new SDL.AudioSpec
                {
                    Format = SDL.AudioFormat.AudioS16LE,
                    Channels = audio.Channels,
                    Freq = audio.SampleRate,
                };
                _stream = SDL.OpenAudioDeviceStream(
                    SDL.AudioDeviceDefaultPlayback,
                    in spec,
                    null!,
                    IntPtr.Zero
                );
                if (_stream == IntPtr.Zero)
                    throw new IOException(SDL.GetError());
                Check(
                    SDL.GetAudioDeviceFormat(
                        SDL.GetAudioStreamDevice(_stream),
                        out var deviceSpec,
                        out var sampleFrames
                    )
                );
                _drainDelay = TimeSpan.FromSeconds(2d * sampleFrames / deviceSpec.Freq);
                Check(SDL.SetAudioStreamGain(_stream, (float)_volume));
                Check(SDL.PutAudioStreamData(_stream, audio.ToArray(), audio.Data.Length));
                Check(SDL.FlushAudioStream(_stream));
                _audio = audio;
                _playbackId = playbackId;
                _cancellation = cancellationToken;
                _lastQueued = audio.Data.Length;
                _lastProgress = Stopwatch.GetTimestamp();
                _drainedAt = 0;
                Check(SDL.ResumeAudioStreamDevice(_stream));
                _timer = new Timer(Poll, playbackId, 30, 30);
            }
            catch
            {
                StopCore();
                throw;
            }
        }
        return Task.CompletedTask;
    }

    private void Poll(object? state)
    {
        AudioPlaybackUpdate? update = null;
        lock (_gate)
        {
            if (_disposed || _stream == 0 || state is not long id || id != _playbackId)
                return;
            if (_cancellation.IsCancellationRequested)
            {
                StopCore();
                return;
            }
            try
            {
                var queued = SDL.GetAudioStreamQueued(_stream);
                var available = SDL.GetAudioStreamAvailable(_stream);
                if (queued < 0 || available < 0)
                    throw new IOException(SDL.GetError());
                var audio = _audio!;
                var consumed = Math.Clamp(audio.Data.Length - queued, 0, audio.Data.Length);
                var position = TimeSpan.FromSeconds(
                    (double)consumed / (audio.SampleRate * audio.Channels * 2)
                );
                _position = position;
                if (queued != _lastQueued)
                {
                    _lastProgress = Stopwatch.GetTimestamp();
                    _lastQueued = queued;
                }
                if (queued == 0 && available == 0)
                {
                    if (_drainedAt == 0)
                        _drainedAt = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(_drainedAt) >= _drainDelay)
                    {
                        update = new(id, AudioPlaybackState.Completed, audio.Duration);
                        StopCore();
                    }
                }
                else if (Stopwatch.GetElapsedTime(_lastProgress) > TimeSpan.FromSeconds(5))
                    throw new IOException("音频输出设备未继续接收数据，请检查设备后重播。");
                update ??= new(id, AudioPlaybackState.Playing, position);
            }
            catch (Exception exception)
            {
                update = new(id, AudioPlaybackState.Failed, TimeSpan.Zero, exception.Message);
                StopCore();
            }
        }
        // Never call subscribers while holding the native-player lock: session.Stop may run concurrently.
        Updated?.Invoke(this, update);
    }

    public void Stop()
    {
        lock (_gate)
            StopCore();
    }

    private void StopCore()
    {
        _timer?.Dispose();
        _timer = null;
        if (_stream != 0)
            SDL.DestroyAudioStream(_stream);
        _stream = 0;
        _audio = null;
    }

    private static void Check(bool success)
    {
        if (!success)
            throw new IOException(SDL.GetError());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopCore();
            if (_initialized)
                lock (LifecycleGate)
                    SDL.QuitSubSystem(SDL.InitFlags.Audio);
            _initialized = false;
        }
    }
}
