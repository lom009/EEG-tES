namespace EGGtCSPlatform.SpeechTraining;

public sealed partial class SpeechTrainingSession
{
    private readonly IRirGenerator _rirGenerator;
    private CancellationTokenSource? _rirCancellation;
    private long _rirGeneration;
    public TrainingRoomSettings RirSettings { get; private set; } = TrainingRoomSettings.Default;
    public RirResponse? RirPreview { get; private set; }
    public bool IsGeneratingRir { get; private set; }
    public bool CanCancelRir => IsGeneratingRir || Mode == SpeechTrainingMode.FixedRir && IsLoading;
    public string? RirUnavailableReason
    {
        get
        {
            try
            {
                RirSettings.Validate();
                var rms = _noiseProvider.CalibrationRms;
                if (!double.IsFinite(rms) || rms <= 0)
                    return "RIR 校准资源没有有效能量。";
                return RirSettings.AddNoise
                    ? GetNoiseUnavailableReason(RirSettings.NoiseKind)
                    : null;
            }
            catch (Exception e)
                when (e
                        is ArgumentException
                            or IOException
                            or UnauthorizedAccessException
                            or OverflowException
                )
            {
                return $"RIR 配置不可用：{e.Message}";
            }
        }
    }

    public void SetRir(TrainingRoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (!CanConfigure || RirSettings == settings)
                return;
            RirSettings = settings;
            if (Mode == SpeechTrainingMode.FixedRir)
                AudioSettings = AudioSettings with { Room = settings };
            RirPreview = null;
            ResetSelection();
        }
    }

    public async Task GenerateRirAsync()
    {
        TrainingRoomSettings settings;
        CancellationToken token;
        long generation;
        lock (_gate)
        {
            if (!CanConfigure || Mode != SpeechTrainingMode.FixedRir || IsGeneratingRir)
                return;
            if (RirUnavailableReason is { } error)
            {
                Error = error;
                Notify();
                return;
            }
            CancelRirGeneration();
            settings = RirSettings;
            token = (_rirCancellation = new()).Token;
            generation = _rirGeneration;
            IsGeneratingRir = true;
            Error = null;
            Notify();
        }
        try
        {
            var response = await Task.Run(
                    () => _rirGenerator.Generate(settings.Acoustics, token),
                    token
                )
                .ConfigureAwait(false);
            lock (_gate)
                if (
                    !_disposed
                    && generation == _rirGeneration
                    && RirSettings == settings
                    && !Started
                )
                    RirPreview = response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (_gate)
                if (!_disposed && generation == _rirGeneration)
                    Error = $"RIR 生成失败：{e.Message}";
        }
        finally
        {
            lock (_gate)
                if (generation == _rirGeneration)
                {
                    IsGeneratingRir = false;
                    _rirCancellation?.Dispose();
                    _rirCancellation = null;
                    Notify();
                }
        }
    }

    public void CancelRir()
    {
        lock (_gate)
        {
            CancelRirGeneration();
            if (Mode == SpeechTrainingMode.FixedRir && IsLoading)
                StopPlayback();
            Notify();
        }
    }

    private void CancelRirGeneration()
    {
        _rirGeneration++;
        _rirCancellation?.Cancel();
        _rirCancellation?.Dispose();
        _rirCancellation = null;
        IsGeneratingRir = false;
    }
}
