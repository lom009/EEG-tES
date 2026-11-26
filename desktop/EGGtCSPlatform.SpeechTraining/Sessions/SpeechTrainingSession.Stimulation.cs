namespace EGGtCSPlatform.SpeechTraining;

public sealed partial class SpeechTrainingSession
{
    private ISpeechStimulationOutput? _output;
    private EnvelopePlaybackSettings? _stimulationSettings;
    private Task _activePlayback = Task.CompletedTask;
    private TaskCompletionSource? _audioCompleted;
    private bool _outputBusy;
    private bool _preparingOutput;
    private bool _audioFinished;
    private int _previewVersion;
    private CancellationTokenSource? _previewCancellation;
    public bool IsPreparingStimulus { get; private set; }
    public bool IsOutputBusy => _outputBusy;
    public bool CanAdjustStimulation => CanConfigure && !IsLoading && !IsPlaying && !_outputBusy;
    public EnvelopePlaybackPlan? StimulusPlan { get; private set; }
    public string? OutputStatus => _preparingOutput ? "正在准备音频与刺激计划" : _output?.Status;

    public void AttachStimulation(
        ISpeechStimulationOutput output,
        EnvelopePlaybackSettings settings
    )
    {
        lock (_gate)
        {
            if (Started || _output is not null)
                throw new InvalidOperationException("输出只能在训练开始前绑定一次。");
            settings.Validate();
            _output = output;
            _stimulationSettings = settings;
            output.Changed += OnOutputChanged;
            output.Faulted += OnOutputFaulted;
        }
    }

    public async Task ConfigureStimulationAsync(EnvelopePlaybackSettings settings)
    {
        SpeechWaveformData? waveform;
        int version;
        CancellationToken token;
        long playbackId;
        lock (_gate)
        {
            if (!CanAdjustStimulation)
                return;
            settings.Validate();
            _stimulationSettings = settings;
            version = ++_previewVersion;
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            _previewCancellation = new();
            token = _previewCancellation.Token;
            playbackId = _generation;
            waveform = Waveform;
            IsPreparingStimulus = waveform is not null;
            Notify();
        }
        try
        {
            var plan = waveform is null
                ? null
                : await Task.Run(
                        () => EnvelopePlaybackPlan.Create(playbackId, waveform, settings, token),
                        token
                    )
                    .ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || version != _previewVersion)
                    return;
                StimulusPlan = plan;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (_gate)
                if (!_disposed && version == _previewVersion)
                {
                    StimulusPlan = null;
                    Error = $"刺激预览准备失败：{e.Message}";
                }
        }
        finally
        {
            lock (_gate)
                if (!_disposed && version == _previewVersion)
                {
                    IsPreparingStimulus = false;
                    Notify();
                }
        }
    }

    private Task StartPlayback() => _activePlayback = BeginPlaybackAsync();

    private void CancelStimulusPreview()
    {
        ++_previewVersion;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        IsPreparingStimulus = false;
    }

    public async Task StopAsync()
    {
        Task active;
        lock (_gate)
        {
            Stop();
            active = _activePlayback;
        }
        await active.ConfigureAwait(false);
        if (_output is not null)
            await _output.StopAsync().ConfigureAwait(false);
    }

    public async Task EndAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            if (!Started || Ended || _disposed)
                return;
            SpeechTrainingResult snapshot;
            lock (_gate)
                snapshot = _pendingSubmission ?? SnapshotCore(_results);
            await PersistCheckpointAsync(snapshot, true).ConfigureAwait(false);
            lock (_gate)
            {
                if (_pendingSubmission is not null)
                {
                    _results.Add(_pendingSubmission.Sentences[^1]);
                    _pendingSubmission = null;
                }
                End();
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    private void OnOutputChanged(object? sender, EventArgs e)
    {
        lock (_gate)
            if (!_disposed)
                NotifyPlaybackState();
    }

    private void OnOutputFaulted(object? sender, string message)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            StopPlayback();
            _played = false;
            Error = message;
            NotifyPlaybackState();
        }
    }
}
