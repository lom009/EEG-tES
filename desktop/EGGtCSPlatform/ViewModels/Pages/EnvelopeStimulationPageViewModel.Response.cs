using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed partial class EnvelopeStimulationPageViewModel
{
    private readonly LocalSpeechResponseRecognizer _responseRecognizer = new();
    private readonly DispatcherTimer _responseTimer = new()
    {
        Interval = TimeSpan.FromSeconds(SdlSpeechRecorder.MaximumSeconds),
    };
    private SdlSpeechRecorder? _responseRecorder;
    private SdlAudioPlayer? _responsePlayer;
    private AudioBuffer? _responseAudio;
    private CancellationTokenSource? _responseRequest;
    private long? _responsePlaybackId;
    private string? _responseStatus;

    public bool ShowResponseAssistance =>
        ShowRunActions && _session?.Mode == SpeechTrainingMode.FixedRate && _responseRecognizer.IsAvailable;
    public bool IsResponseRecording => _responseRecorder is not null;
    public bool HasResponseAudio => _responseAudio is not null;
    public string ResponseStatus => _responseStatus ?? (_session?.ResponsePlaybackId is null
        ? "题目播完后可录音"
        : "录制本句复述，初判结果请人工核对");

    public RelayCommand StartResponseCommand { get; private set; } = null!;
    public AsyncRelayCommand SubmitResponseCommand { get; private set; } = null!;
    public AsyncRelayCommand ReplayResponseCommand { get; private set; } = null!;

    private bool ResponseBusy => IsResponseRecording || _responseRequest is not null || _responsePlayer is not null;
    private bool CanCaptureResponse => ShowResponseAssistance && !_disposed && !_closing
        && !_experimentEnded && _session?.ResponsePlaybackId is not null;

    private void InitializeResponseCommands()
    {
        StartResponseCommand = new(StartResponse, () => CanCaptureResponse && !ResponseBusy);
        SubmitResponseCommand = new(SubmitResponseAsync, () => CanCaptureResponse && IsResponseRecording);
        ReplayResponseCommand = new(ReplayResponseAsync, () => CanCaptureResponse && !ResponseBusy && HasResponseAudio);
        _responseTimer.Tick += (_, _) =>
        {
            _responseTimer.Stop();
            if (SubmitResponseCommand.CanExecute(null))
                SubmitResponseCommand.Execute(null);
        };
    }

    private void StartResponse()
    {
        if (!CanCaptureResponse || ResponseBusy)
            return;
        var playbackId = _session?.ResponsePlaybackId;
        if (playbackId is null)
            return;

        ClearResponseReview();
        try
        {
            _responseRecorder = new();
            _responsePlaybackId = playbackId;
            _session?.ClearResponseSuggestion(playbackId.Value);
            _responseStatus = $"正在录音，请复述本句（最多 {SdlSpeechRecorder.MaximumSeconds} 秒）";
            _responseTimer.Start();
        }
        catch (Exception)
        {
            _responseStatus = "无法开始录音，请检查麦克风权限或使用人工评分。";
        }
        NotifyResponseState();
    }

    private async Task SubmitResponseAsync()
    {
        if (_responseRecorder is not { } recorder || _session is not { } session
            || _responsePlaybackId is not { } playbackId || session.ResponsePlaybackId != playbackId)
            return;
        _responseTimer.Stop();
        _responseRecorder = null;
        using var request = new CancellationTokenSource();
        _responseRequest = request;
        _responseStatus = "正在本地识别";
        NotifyResponseState();
        try
        {
            var recording = recorder.Complete();
            _responseAudio = recording.Frames > 0 ? recording : null;
            var sentence = session.CurrentSentence;
            var suggestion = await Task.Run(() => _responseRecognizer.RecognizeAsync(recording, sentence, request.Token));
            if (!ReferenceEquals(_responseRequest, request) || session.ResponsePlaybackId != playbackId)
                return;
            if (suggestion is null)
            {
                _responseStatus = "未识别到有效复述，可重录或手动评分。";
                return;
            }
            if (!session.TryApplyResponseSuggestion(playbackId, suggestion.Ratings))
                return;
            var reviewHint = suggestion.ReviewReason switch
            {
                SpeechResponseReviewReason.SelfCorrection => "待复核 · 转写可能含有改口，请回听后手动评分。",
                SpeechResponseReviewReason.LengthMismatch =>
                    $"待复核 · 识别到 {suggestion.RecognizedCharacters} 字，原句 {suggestion.Ratings.Length} 字，请回听后手动评分。",
                _ when Array.IndexOf(suggestion.Ratings, null) >= 0 => "待复核 · 部分字的读音需确认，请回听后评分。",
                _ => "请核对字卡后保存。",
            };
            _responseStatus = $"识别「{suggestion.Transcript}」。{reviewHint}";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (ReferenceEquals(_responseRequest, request))
                _responseStatus = "录音或本地识别失败，可重录或手动评分。";
        }
        finally
        {
            if (ReferenceEquals(_responseRequest, request))
            {
                _responseRequest = null;
                NotifyResponseState();
            }
        }
    }

    private async Task ReplayResponseAsync()
    {
        if (!CanCaptureResponse || ResponseBusy || _responseAudio is not { } recording)
            return;
        try
        {
            var player = new SdlAudioPlayer();
            _responsePlayer = player;
            player.Updated += OnResponsePlaybackUpdated;
            NotifyResponseState();
            await player.PlayAsync(recording, 0);
        }
        catch (Exception)
        {
            StopResponsePlayback();
            _responseStatus = "无法回听录音，可手动评分。";
            NotifyResponseState();
        }
    }

    private void OnResponsePlaybackUpdated(object? sender, AudioPlaybackUpdate update)
    {
        if (update.State == AudioPlaybackState.Playing)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sender, _responsePlayer))
                return;
            StopResponsePlayback();
            if (update.State == AudioPlaybackState.Failed)
                _responseStatus = "无法回听录音，可手动评分。";
            NotifyResponseState();
        });
    }

    private void StopResponsePlayback()
    {
        if (_responsePlayer is not { } player)
            return;
        _responsePlayer = null;
        player.Updated -= OnResponsePlaybackUpdated;
        player.Dispose();
    }

    private void ClearResponseReview()
    {
        _responseTimer.Stop();
        var request = _responseRequest;
        _responseRequest = null;
        request?.Cancel();
        _responseRecorder?.Dispose();
        _responseRecorder = null;
        StopResponsePlayback();
        _responseAudio = null;
        _responsePlaybackId = null;
        _responseStatus = null;
    }

    private void NotifyResponseState()
    {
        if (_responsePlaybackId is { } playbackId && _session?.ResponsePlaybackId != playbackId)
            ClearResponseReview();
        OnPropertyChanged(nameof(ShowResponseAssistance));
        OnPropertyChanged(nameof(IsResponseRecording));
        OnPropertyChanged(nameof(HasResponseAudio));
        OnPropertyChanged(nameof(ResponseStatus));
        StartResponseCommand.NotifyCanExecuteChanged();
        SubmitResponseCommand.NotifyCanExecuteChanged();
        ReplayResponseCommand.NotifyCanExecuteChanged();
        SaveNextCommand.NotifyCanExecuteChanged();
    }
}
