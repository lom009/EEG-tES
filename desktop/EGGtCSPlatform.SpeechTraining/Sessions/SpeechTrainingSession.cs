namespace EGGtCSPlatform.SpeechTraining;

/// <summary>UI-independent, in-memory training. Owns and disposes its player.</summary>
public sealed partial class SpeechTrainingSession : IDisposable
{
    private readonly object _gate = new();
    private readonly ISpeechCorpusProvider _provider;
    private readonly SpeechSentenceListService _listService = new();
    private readonly IAudioPlayer _player;
    private readonly ITrainingAudioPreparationService _preparation;
    private readonly List<SpeechSentenceResult> _results = [];
    private readonly List<SpeechPlaybackRecord> _playbacks = [];
    private SpeechTrainingResult? _pendingSubmission;
    private readonly AdaptiveSpeechState _adaptive = new();
    private AdaptiveSnrState _snr = new();
    private readonly ISpeechNoiseProvider _noiseProvider;
    private readonly int _noiseSeed = System.Security.Cryptography.RandomNumberGenerator.GetInt32(
        int.MaxValue
    );
    private decimal _fixedSpeechRate;
    private AdaptiveScoringRule? _startedScoringRule;
    private AdaptiveScoringRule? _scoringOverride;
    private static readonly AdaptiveScoringRule MspScoring = new(7, 4);
    private static readonly AdaptiveScoringRule MhintScoring = new(10, 8);
    public AdaptiveScoringRule? ScoringRule => Corpus.Id switch
    {
        "MSP" => MspScoring,
        "MHINT" or "MHINT-P" => MhintScoring,
        _ => _scoringOverride ?? Corpus.AdaptiveScoring,
    };

    public void SetScoringRule(AdaptiveScoringRule? rule)
    {
        lock (_gate)
        {
            if (!CanConfigure)
                return;
            _scoringOverride = rule;
            Notify();
        }
    }

    private bool?[] _answers = [];
    private bool[] _suggestedAnswers = [];
    private CancellationTokenSource? _playbackCancellation;
    private long _generation;
    private bool _played,
        _disposed;
    private DateTimeOffset _startedAt;
    private TrainingAudioSettings _startedAudioSettings = new();
    private TrainingAudioSettings? _preparedSettings;
    private SpeechSentence? _waveformSentence;

    public SpeechTrainingSession(
        ISpeechCorpusProvider provider,
        IAudioPlayer player,
        int listSize = 20,
        ITrainingAudioPreparationService? preparation = null,
        ISpeechNoiseProvider? noiseProvider = null,
        IRirGenerator? rirGenerator = null
    )
    {
        _provider = provider;
        _player = player;
        _noiseProvider = noiseProvider ?? new FileSpeechNoiseProvider();
        _rirGenerator = rirGenerator ?? new RirGenerator();
        _preparation =
            preparation
            ?? new TrainingAudioPreparationService(
                provider,
                noiseProvider: _noiseProvider,
                rirGenerator: _rirGenerator
            );
        Corpus = provider.Corpora.First();
        Lists = _listService.GetLists(Corpus, listSize);
        ListSize = listSize;
        SentenceList = Lists.First();
        ResetAnswers();
        _player.Updated += OnPlaybackUpdated;
    }

    public event EventHandler? Changed;
    public IReadOnlyList<SpeechCorpus> Corpora => _provider.Corpora;
    public SpeechCorpus Corpus { get; private set; }
    public SpeechSentenceList SentenceList { get; private set; }
    public int ListSize { get; private set; }
    public IReadOnlyList<SpeechSentenceList> Lists { get; private set; }
    public SpeechSentence CurrentSentence => SentenceList.Sentences[SentenceIndex];
    public int SentenceIndex { get; private set; }
    public IReadOnlyList<bool?> Answers
    {
        get
        {
            lock (_gate)
                return _answers.ToArray();
        }
    }
    public bool Started { get; private set; }
    public bool Ended { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsPlaying { get; private set; }
    private readonly SemaphoreSlim _transition = new(1, 1);
    public bool IsCommitting { get; private set; }
    public string? PersistenceError { get; private set; }

    /// <summary>Optional durable boundary. A failed write never advances the sentence or algorithm.</summary>
    public Func<SpeechTrainingResult, bool, Task>? PersistAsync { get; set; }
    public bool CanConfigure => !Started && !_disposed && !IsCommitting;
    public bool CanStart =>
        CanConfigure
        && !IsPreparingStimulus
        && !_outputBusy
        && !IsGeneratingRir
        && (
            Mode == SpeechTrainingMode.FixedRate
            || (
                Mode == SpeechTrainingMode.FixedRir ? RirUnavailableReason is null
                : Mode == SpeechTrainingMode.FixedSnr ? CanUseFixedSnr
                : Mode == SpeechTrainingMode.AdaptiveSnr ? CanUseAdaptiveSnr
                : CanUseAdaptiveMode
            )
        );
    public bool CanUseAdaptiveMode => AdaptiveUnavailableReason is null;
    public string? AdaptiveUnavailableReason
    {
        get
        {
            if (Corpus.AdaptiveConfigurationError is { } error)
                return error;
            if (ScoringRule is not { } rule)
                return $"语料库 {Corpus.Id} 缺少自适应评分规则，请配置 corpus.json。";
            foreach (var sentence in SentenceList.Sentences)
                if (sentence.ScoringCharacters.Count != rule.ExpectedCharacterCount)
                    return $"语料库 {Corpus.Id} 当前句表的句子 {sentence.Number} 有 {sentence.ScoringCharacters.Count} 个可评分字，"
                        + $"与评分配置要求的 {rule.ExpectedCharacterCount} 字不符。";
            return null;
        }
    }
    public double InitialSnrDb { get; private set; } = 10;
    public SpeechNoiseKind NoiseKind { get; private set; } = SpeechNoiseKind.Ssn;
    public double FixedSnrDb { get; private set; } = 10;
    public bool IsSnrMode => Mode is SpeechTrainingMode.AdaptiveSnr or SpeechTrainingMode.FixedSnr;
    public double CurrentSnrDb => Mode == SpeechTrainingMode.FixedSnr ? FixedSnrDb : _snr.SnrDb;
    public int SnrReversals => _snr.Reversals;
    public System.Collections.Immutable.ImmutableArray<AdaptiveSnrSubmission> SnrSubmissions =>
        _snr.Submissions;
    public bool CanUseAdaptiveSnr => SnrUnavailableReason is null;
    public string? SnrUnavailableReason => GetSnrUnavailableReason(NoiseKind);

    public bool CanUseFixedSnr => FixedSnrUnavailableReason is null;
    public string? FixedSnrUnavailableReason => GetNoiseUnavailableReason(NoiseKind);

    public string? GetSnrUnavailableReason(SpeechNoiseKind kind) =>
        AdaptiveUnavailableReason ?? GetNoiseUnavailableReason(kind);

    public string? GetNoiseUnavailableReason(SpeechNoiseKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (Corpus.SnrNoise?.Error(kind) is { } error)
            return error;
        if (Corpus.SnrNoise?.File(kind) is not { } name)
            return $"语料库 {Corpus.Id} 缺少 {kind} 噪声配置。";
        try
        {
            _noiseProvider.Read(name);
            var rms = _noiseProvider.CalibrationRms;
            if (!double.IsFinite(rms) || rms <= 0)
                return "噪声校准参考没有有效能量。";
            return null;
        }
        catch (Exception e)
            when (e
                    is IOException
                        or InvalidDataException
                        or ArgumentException
                        or UnauthorizedAccessException
            )
        {
            return $"噪声资源不可用（{name}）：{e.Message}";
        }
    }

    public void SetInitialSnr(double value)
    {
        lock (_gate)
        {
            if (!CanConfigure)
                return;
            var state = new AdaptiveSnrState(value);
            InitialSnrDb = value;
            _snr = state;
            if (Mode == SpeechTrainingMode.AdaptiveSnr)
                AudioSettings = AudioSettings with { SnrDb = value };
            ResetSelection();
        }
    }

    public void SetFixedSnr(double value)
    {
        lock (_gate)
        {
            if (!CanConfigure)
                return;
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "固定 SNR 必须是有限数值。");
            FixedSnrDb = value;
            if (Mode == SpeechTrainingMode.FixedSnr)
                AudioSettings = AudioSettings with { SnrDb = value };
            ResetSelection();
        }
    }

    public void SelectNoise(SpeechNoiseKind kind)
    {
        lock (_gate)
        {
            if (!CanConfigure)
                return;
            if (!Enum.IsDefined(kind))
                throw new ArgumentOutOfRangeException(nameof(kind));
            NoiseKind = kind;
            AudioSettings = AudioSettings with { NoiseKind = kind };
            ResetSelection();
        }
    }

    public bool CanAdjustCarrier => CanConfigure && !IsPlaying && !IsLoading && !_outputBusy;
    public bool CanAdjustSpeechRate => Mode == SpeechTrainingMode.FixedRate && CanAdjustCarrier;
    public SpeechTrainingMode Mode { get; private set; }
    public double AdaptiveR => _adaptive.R;
    public int AdaptiveReversals => _adaptive.Reversals;
    public System.Collections.Immutable.ImmutableArray<AdaptiveSpeechSubmission> AdaptiveSubmissions =>
        _adaptive.Submissions;
    public SpeechTimeStretchMetadata? CurrentTiming =>
        Error is null ? Waveform?.Preparation?.TimeStretch : null;
    public bool CanScore =>
        _pendingSubmission is null
        && Started
        && !Ended
        && !_disposed
        && !IsLoading
        && !IsCommitting
        && Error is null
        && (IsPlaying || _played || _audioFinished);

    public long? ResponsePlaybackId
    {
        get
        {
            lock (_gate)
                return CanScore && Mode == SpeechTrainingMode.FixedRate && _played && !IsPlaying && !_outputBusy
                    ? _generation : null;
        }
    }

    public bool TryApplyResponseSuggestion(long playbackId, IReadOnlyList<bool?> ratings)
    {
        lock (_gate)
        {
            if (ResponsePlaybackId != playbackId || ratings.Count != _answers.Length)
                return false;
            for (var i = 0; i < _answers.Length; i++)
            {
                if (_answers[i] is null || _suggestedAnswers[i])
                {
                    _answers[i] = ratings[i];
                    _suggestedAnswers[i] = ratings[i].HasValue;
                }
            }
            Notify();
            return true;
        }
    }

    public void ClearResponseSuggestion(long playbackId)
    {
        lock (_gate)
        {
            if (ResponsePlaybackId != playbackId || !_suggestedAnswers.Contains(true))
                return;
            for (var i = 0; i < _answers.Length; i++)
            {
                if (_suggestedAnswers[i])
                    _answers[i] = null;
            }
            Array.Clear(_suggestedAnswers);
            Notify();
        }
    }
    public bool CanSaveNext =>
        (_pendingSubmission is not null && !IsCommitting && !Ended && !_disposed)
        || (
            CanScore
            && !_outputBusy
            && !IsPreparingStimulus
            && (_output is null || _played)
            && _answers.All(x => x.HasValue)
        );
    public bool CanReplay =>
        _pendingSubmission is null
        && Started
        && !Ended
        && !_disposed
        && !IsPlaying
        && !IsLoading
        && !_outputBusy
        && !IsPreparingStimulus
        && !IsCommitting;
    public string? Error { get; private set; }
    public SpeechWaveformData? Waveform { get; private set; }
    public AudioBuffer? Audio => Waveform?.Audio;
    public TimeSpan Position { get; private set; }
    private TimeSpan _timelinePosition;
    public TimeSpan TimelinePosition
    {
        get => TimelineAnimation?.Position ?? _timelinePosition;
        private set => _timelinePosition = value;
    }
    public TimeSpan Duration => Audio?.Duration ?? TimeSpan.Zero;
    public SpeechTrainingResult? Result { get; private set; }
    public TrainingAudioSettings AudioSettings { get; private set; } = new();
    public decimal SpeechRate => AudioSettings.SpeechRate;
    public TrainingAudioSettings? CurrentAudioSettings => Waveform?.Preparation?.Snapshot.Settings;

    public void ConfigureAudio(TrainingAudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_gate)
        {
            if (!CanConfigure)
                return;
            var initialSnr =
                settings.Mode == SpeechTrainingMode.AdaptiveSnr
                    ? new AdaptiveSnrState(settings.SnrDb)
                    : null;
            Mode = settings.Mode;
            _fixedSpeechRate = settings.SpeechRate;
            NoiseKind = settings.NoiseKind;
            if (settings.Mode == SpeechTrainingMode.FixedSnr)
                FixedSnrDb = settings.SnrDb;
            if (initialSnr is not null)
            {
                _snr = initialSnr;
                InitialSnrDb = settings.SnrDb;
            }
            if (settings.Room is { } room)
                RirSettings = room;
            AudioSettings = settings;
            ResetSelection();
        }
    }

    public void SetVocoder(VocoderOptions? settings)
    {
        lock (_gate)
        {
            if (!CanAdjustCarrier)
                return;
            settings?.Validate();
            var configuration = AudioSettings with { Vocoder = settings };
            if (configuration == AudioSettings)
                return;
            AudioSettings = configuration;
            Notify();
        }
    }

    public void SelectMode(SpeechTrainingMode mode)
    {
        lock (_gate)
        {
            if (!CanConfigure || mode == Mode)
                return;
            if (!Enum.IsDefined(mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            AudioSettings = AudioSettings with
            {
                Mode = mode,
                SpeechRate = mode == SpeechTrainingMode.FixedRate ? _fixedSpeechRate : 0,
                SnrDb = mode == SpeechTrainingMode.FixedSnr ? FixedSnrDb : _snr.SnrDb,
                Room = mode == SpeechTrainingMode.FixedRir ? RirSettings : null,
            };
            Mode = mode;
            ResetSelection();
        }
    }

    public void SetSpeechRate(decimal rate)
    {
        lock (_gate)
        {
            if (!CanAdjustSpeechRate || rate == SpeechRate)
                return;
            if (rate is < 0 or > 5 || rate != decimal.Truncate(rate))
                throw new ArgumentOutOfRangeException(nameof(rate));
            AudioSettings = AudioSettings with { SpeechRate = rate };
            _fixedSpeechRate = rate;
            // The displayed audio and scores still describe the last playback, not this pending setting.
            Notify();
        }
    }

    public string? VolumeError { get; private set; }
    public double Volume
    {
        get
        {
            lock (_gate)
                return _player.Volume;
        }
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    _player.Volume = value;
                    VolumeError = null;
                }
                catch (Exception exception)
                {
                    VolumeError = $"音量调节失败：{exception.Message}";
                }
                Notify();
            }
        }
    }

    public void SelectCorpus(string id)
    {
        lock (_gate)
        {
            if (!CanConfigure || id == Corpus.Id)
                return;
            _scoringOverride = null;
            Corpus = Corpora.First(x => x.Id == id);
            Lists = _listService.GetLists(Corpus, ListSize);
            SentenceList = Lists.First();
            ResetSelection();
        }
    }

    public void SelectSentenceList(string id)
    {
        lock (_gate)
        {
            if (!CanConfigure || id == SentenceList.Id)
                return;
            SentenceList =
                Lists.FirstOrDefault(x => x.Id == id)
                ?? throw new ArgumentOutOfRangeException(nameof(id));
            ResetSelection();
        }
    }

    public void SelectListSize(int size)
    {
        lock (_gate)
        {
            if (!CanConfigure)
                return;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
            if (size == ListSize)
                return;
            Lists = _listService.GetLists(Corpus, size);
            ListSize = size;
            SentenceList = Lists.First();
            ResetSelection();
        }
    }

    private void ResetSelection()
    {
        CancelRirGeneration();
        SentenceIndex = 0;
        Waveform = null;
        StimulusPlan = null;
        Error = null;
        Position = TimeSpan.Zero;
        StopTimeline();
        TimelinePosition = TimeSpan.Zero;
        ResetAnswers();
        Notify();
    }

    private Task StartPlaybackConfigured()
    {
        lock (_gate)
        {
            if (!CanConfigure || IsGeneratingRir || IsPreparingStimulus || _outputBusy)
                return Task.CompletedTask;
            if (
                (
                    Mode == SpeechTrainingMode.FixedRir ? RirUnavailableReason
                    : Mode == SpeechTrainingMode.FixedSnr ? FixedSnrUnavailableReason
                    : Mode == SpeechTrainingMode.AdaptiveSnr ? SnrUnavailableReason
                    : Mode == SpeechTrainingMode.AdaptiveRate ? AdaptiveUnavailableReason
                    : null
                ) is
                { } reason
            )
            {
                Error = reason;
                Notify();
                return Task.CompletedTask;
            }
            _startedScoringRule = Mode
                is SpeechTrainingMode.AdaptiveRate
                    or SpeechTrainingMode.AdaptiveSnr
                ? ScoringRule
                : null;
            var resolved = AudioSettings with
            {
                Mode = Mode,
                NoiseKind = NoiseKind,
                SnrDb = CurrentSnrDb,
                Room = Mode == SpeechTrainingMode.FixedRir ? RirSettings : null,
            };
            if (resolved != AudioSettings)
                AudioSettings = resolved;
            Started = true;
            _startedAt = DateTimeOffset.Now;
            _startedAudioSettings = AudioSettings;
            return Task.CompletedTask;
        }
    }

    public async Task StartAsync()
    {
        if (!await _transition.WaitAsync(0).ConfigureAwait(false))
            return;
        Task playback = Task.CompletedTask;
        try
        {
            lock (_gate)
            {
                if (!CanConfigure)
                    return;
            }
            await StartPlaybackConfigured().ConfigureAwait(false);
            if (!Started)
                return;
            try
            {
                await PersistCheckpointAsync(Snapshot(), false).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                    Started = false;
                throw;
            }
            lock (_gate)
            {
                if (!_disposed && !Ended)
                    playback = StartPlayback();
            }
        }
        finally
        {
            _transition.Release();
        }
        await playback.ConfigureAwait(false);
    }

    private async Task PersistCheckpointAsync(SpeechTrainingResult snapshot, bool ended)
    {
        lock (_gate)
        {
            IsCommitting = true;
            PersistenceError = null;
            Notify();
        }
        try
        {
            if (PersistAsync is { } persist)
                await persist(snapshot, ended).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            lock (_gate)
                PersistenceError = $"保存训练失败：{e.Message}；请重试。";
            throw;
        }
        finally
        {
            lock (_gate)
            {
                IsCommitting = false;
                Notify();
            }
        }
    }

    public Task ReplayAsync()
    {
        lock (_gate)
            return CanReplay ? StartPlayback() : Task.CompletedTask;
    }

    public void Rate(int index, bool? correct)
    {
        lock (_gate)
        {
            if (!CanScore)
                return;
            if (index < 0 || index >= _answers.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            _answers[index] = correct;
            _suggestedAnswers[index] = false;
            Notify();
        }
    }

    public void RateAll(bool? correct)
    {
        lock (_gate)
        {
            if (!CanScore)
                return;
            Array.Fill(_answers, correct);
            Array.Clear(_suggestedAnswers);
            Notify();
        }
    }

    public async Task SaveNextAsync()
    {
        if (!await _transition.WaitAsync(0).ConfigureAwait(false))
            return;
        Task playback = Task.CompletedTask;
        try
        {
            SpeechSentenceResult candidate;
            SpeechTrainingResult snapshot;
            bool last;
            lock (_gate)
            {
                if (!CanSaveNext)
                    return;
                if (_pendingSubmission is { } pending)
                {
                    snapshot = pending;
                    candidate = pending.Sentences[^1];
                    last = pending.Sentences.Count == pending.SentenceCount;
                }
                else
                {
                    var sentence = CurrentSentence;
                    var correct = _answers.Count(x => x == true);
                    var total = _answers.Length;
                    AdaptiveSpeechRecord? adaptive =
                        Mode == SpeechTrainingMode.AdaptiveRate
                            ? new(
                                _adaptive.Preview(
                                    sentence.Id,
                                    SentenceIndex + 1,
                                    _startedScoringRule!,
                                    correct,
                                    total
                                ),
                                CurrentTiming
                                    ?? throw new InvalidOperationException("缺少自适应音频元数据。")
                            )
                            : null;
                    AdaptiveSnrRecord? snr =
                        Mode == SpeechTrainingMode.AdaptiveSnr
                            ? new(
                                _snr.Preview(
                                    sentence.Id,
                                    SentenceIndex + 1,
                                    _startedScoringRule!,
                                    correct,
                                    total
                                ),
                                Waveform?.Preparation?.Noise
                                    ?? throw new InvalidOperationException("缺少 SNR 音频元数据。")
                            )
                            : null;
                    candidate = new(
                        sentence.Id,
                        SentenceIndex + 1,
                        sentence.Text,
                        correct,
                        total,
                        _preparedSettings?.SpeechRate,
                        Waveform?.Preparation?.Snapshot,
                        adaptive,
                        _played,
                        snr,
                        Waveform?.Preparation?.Noise,
                        Waveform?.Preparation?.Rir
                    );
                    last = SentenceIndex == SentenceList.Sentences.Count - 1;
                    snapshot = SnapshotCore([.. _results, candidate]);
                    _pendingSubmission = snapshot;
                }
                IsCommitting = true;
                StopPlayback();
                Notify();
            }
            await PersistCheckpointAsync(snapshot, last).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || Ended)
                    return;
                if (candidate.AdaptiveSnr is not null)
                    _snr.Submit(
                        candidate.SentenceId,
                        candidate.Number,
                        _startedScoringRule!,
                        candidate.Correct,
                        candidate.Total
                    );
                if (candidate.Adaptive is not null)
                    _adaptive.Submit(
                        candidate.SentenceId,
                        candidate.Number,
                        _startedScoringRule!,
                        candidate.Correct,
                        candidate.Total
                    );
                _results.Add(candidate);
                _pendingSubmission = null;
                if (last)
                    End();
                else
                {
                    SentenceIndex++;
                    if (Mode == SpeechTrainingMode.AdaptiveSnr)
                        AudioSettings = AudioSettings with { SnrDb = _snr.SnrDb };
                    ResetAnswers();
                    Waveform = null;
                    playback = StartPlayback();
                }
            }
        }
        finally
        {
            _transition.Release();
        }
        await playback.ConfigureAwait(false);
    }

    public void End()
    {
        lock (_gate)
        {
            if (!Started || Ended || _disposed)
                return;
            StopPlayback();
            Ended = true;
            Result = SnapshotCore(_results);
            Notify();
        }
    }

    public SpeechTrainingResult Snapshot()
    {
        lock (_gate)
            return SnapshotCore(_results);
    }

    private SpeechTrainingResult SnapshotCore(IReadOnlyList<SpeechSentenceResult> sentences)
    {
        return new(
            new(
                Corpus.Id,
                SentenceList.Id,
                Carrier: _startedAudioSettings.CarrierSummary,
                SpeechRate: _startedAudioSettings.SpeechRate,
                ListSize: ListSize,
                ResourceStart: SentenceList.ResourceStart,
                ResourceEnd: SentenceList.ResourceEnd,
                AudioSettings: _startedAudioSettings,
                Mode: Mode == SpeechTrainingMode.AdaptiveRate ? "自适应语速测试(安静)"
                    : Mode == SpeechTrainingMode.AdaptiveSnr ? "自适应信噪比测试"
                    : Mode == SpeechTrainingMode.FixedRir ? "固定 RIR 测试"
                    : Mode == SpeechTrainingMode.FixedSnr ? "固定信噪比测试"
                    : "固定语速测试(安静)",
                TrainingMode: Mode,
                AdaptiveScoring: _startedScoringRule,
                InitialSnrDb: Mode == SpeechTrainingMode.AdaptiveSnr ? InitialSnrDb : null,
                FixedSnrDb: Mode == SpeechTrainingMode.FixedSnr ? FixedSnrDb : null,
                Rir: Mode == SpeechTrainingMode.FixedRir ? RirSettings : null
            ),
            _startedAt,
            SentenceList.Sentences.Count,
            sentences.ToArray(),
            Mode == SpeechTrainingMode.AdaptiveRate
                ? AdaptiveSpeechSummary.Create(SentenceList.Sentences.Count, sentences, _playbacks)
                : null,
            [.. _playbacks],
            Mode == SpeechTrainingMode.AdaptiveSnr
                ? AdaptiveSnrSummary.Create(SentenceList.Sentences.Count, sentences, _playbacks)
                : null
        );
    }

    /// <summary>Suspends output on navigation; a completed sentence remains scoreable, an interrupted one needs replay.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            CancelRirGeneration();
            CancelStimulusPreview();
            StopPlayback();
            Notify();
        }
    }

    private async Task BeginPlaybackAsync()
    {
        StopPlayback();
        _played = false;
        _audioFinished = false;
        _outputBusy = _output is not null;
        _preparingOutput = _outputBusy;
        _audioCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Error = null;
        Position = TimeSpan.Zero;
        TimelinePosition = TimeSpan.Zero;
        IsLoading = true;
        var generation = _generation;
        var sentence = CurrentSentence;
        var cancellation = _playbackCancellation = new();
        var token = cancellation.Token;
        var configuration = AudioSettings with
        {
            Mode = Mode,
            SnrDb = CurrentSnrDb,
            NoiseKind = NoiseKind,
            Room = Mode == SpeechTrainingMode.FixedRir ? RirSettings : null,
        };
        var multiplier = Mode == SpeechTrainingMode.AdaptiveRate ? 1 + _adaptive.R : 1;
        var mapping = Corpus.SnrNoise;
        var previous = Waveform;
        if (_waveformSentence != sentence || _preparedSettings != configuration)
        {
            Waveform = null;
            StimulusPlan = null;
        }
        Notify();
        var outputCompleted = false;
        try
        {
            var prepared = await Task.Run(
                    () =>
                    {
                        var request = _preparation.CreateRequest(
                            sentence,
                            configuration,
                            multiplier,
                            _noiseSeed,
                            mapping,
                            token
                        );
                        var result = _preparation.Prepare(request, previous?.Preparation, token);
                        if (result.Request != request)
                            throw new InvalidOperationException("准备结果元数据与本次请求不一致。");
                        result.ValidateRequest();
                        return ReferenceEquals(result, previous?.Preparation)
                            ? previous!
                            : SpeechWaveformData.Prepare(result, token);
                    },
                    token
                )
                .ConfigureAwait(false);
            EnvelopePlaybackPlan? plan = null;
            if (_stimulationSettings is { } settings)
                plan = await Task.Run(
                        () => EnvelopePlaybackPlan.Create(generation, prepared, settings, token),
                        token
                    )
                    .ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrent(generation))
                    return;
                if (prepared.Preparation?.RirResponse is { } response)
                    RirPreview = response;
                Waveform = prepared;
                StimulusPlan = plan;
                _preparedSettings = configuration;
                _waveformSentence = sentence;
                Notify();
            }
            if (_output is not null)
            {
                _preparingOutput = false;
                await _output.PrepareAsync(plan!, token).ConfigureAwait(false);
                await _output.StartAsync(token).ConfigureAwait(false);
            }
            Task start;
            lock (_gate)
            {
                if (!IsCurrent(generation))
                    return;
                start = _player.PlayAsync(prepared.Audio, generation, token);
                if (IsCurrent(generation))
                    StartTimeline();
            }
            await start.ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrent(generation))
                    return;
                RecordPlayback(generation);
                IsLoading = false;
                if (Error is null && !_played && !_audioFinished)
                    IsPlaying = true;
                NotifyPlaybackState();
            }
            if (_output is not null)
            {
                await _audioCompleted.Task.WaitAsync(token).ConfigureAwait(false);
                await _output.WaitForCompletionAsync(token).ConfigureAwait(false);
                outputCompleted = true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!IsCurrent(generation))
                    return;
                StopPlayback();
                _played = false;
                Waveform = null;
                Error =
                    $"{(_output is null ? "音频播放失败" : "本句执行失败")}：{exception.Message}";
                Notify();
            }
        }
        finally
        {
            if (_output is not null)
            {
                try
                {
                    await _output.StopAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    outputCompleted = false;
                    lock (_gate)
                    {
                        StopTimeline();
                        Error = $"设备停止未确认：{e.Message}";
                    }
                }
                lock (_gate)
                {
                    _outputBusy = false;
                    _preparingOutput = false;
                    if (IsCurrent(generation) && outputCompleted && Error is null)
                    {
                        _played = true;
                        StopPlayback(preserveTimeline: true);
                    }
                    NotifyPlaybackState();
                }
            }
        }
    }

    private bool IsCurrent(long generation) => !_disposed && !Ended && generation == _generation;

    private void OnPlaybackUpdated(object? sender, AudioPlaybackUpdate update)
    {
        lock (_gate)
        {
            if (!IsCurrent(update.PlaybackId))
                return;
            if (
                update.State == AudioPlaybackState.Playing
                && IsPlaying
                && !IsLoading
                && update.Position <= Position
            )
                return;
            var positionOnly = update.State == AudioPlaybackState.Playing && IsPlaying && !IsLoading;
            Position = TimeSpan.FromTicks(
                Math.Clamp(update.Position.Ticks, Position.Ticks, Duration.Ticks)
            );
            if (positionOnly)
            {
                TimelineChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (update.State == AudioPlaybackState.Failed)
            {
                StopPlayback();
                _played = false;
                Error = $"音频播放失败：{update.Error ?? "输出设备不可用"}";
            }
            else
            {
                RecordPlayback(update.PlaybackId);
                IsLoading = false;
                IsPlaying = update.State == AudioPlaybackState.Playing;
                _audioFinished = update.State == AudioPlaybackState.Completed;
                _played = _audioFinished && _output is null;
                if (_audioFinished)
                {
                    Position = Duration;
                }
                if (_audioFinished && _output is not null)
                {
                    _audioCompleted?.TrySetResult();
                }
                if (_played)
                {
                    StopPlayback(preserveTimeline: true);
                }
            }
            NotifyPlaybackState();
        }
    }

    private void StopPlayback(bool preserveTimeline = false)
    {
        if (!preserveTimeline)
            StopTimeline();
        _generation++;
        _playbackCancellation?.Cancel();
        _playbackCancellation?.Dispose();
        _playbackCancellation = null;
        _audioCompleted?.TrySetCanceled();
        _player.Stop();
        IsPlaying = false;
        IsLoading = false;
    }

    private void RecordPlayback(long generation)
    {
        if (
            _preparedSettings is { } configuration
            && _waveformSentence is { } sentence
            && !_playbacks.Any(p => p.PlaybackId == generation)
        )
            _playbacks.Add(
                new(
                    generation,
                    sentence.Id,
                    Waveform!.Preparation!.Snapshot,
                    Waveform?.Preparation?.Noise,
                    Waveform?.Preparation?.Rir
                )
            );
    }

    private void ResetAnswers()
    {
        _answers = new bool?[CurrentSentence.ScoringCharacters.Count];
        _suggestedAnswers = new bool[_answers.Length];
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    private void NotifyPlaybackState() => Changed?.Invoke(this, SpeechPlaybackStateChangedEventArgs.Instance);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelStimulusPreview();
            CancelRirGeneration();
            StopPlayback();
            _player.Updated -= OnPlaybackUpdated;
            _player.Dispose();
            if (_output is not null)
            {
                _output.Changed -= OnOutputChanged;
                _output.Faulted -= OnOutputFaulted;
            }
            Notify();
        }
    }
}
