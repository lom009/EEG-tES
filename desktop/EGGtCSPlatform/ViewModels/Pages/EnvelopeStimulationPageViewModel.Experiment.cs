using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed partial class EnvelopeStimulationPageViewModel
{
    private Func<SpeechTrainingSession> _createSession = null!;
    private Func<ExperimentRunRouteData, ISpeechStimulationOutput>? _createOutput;
    private SpeechOutputLifetime? _outputLifetime;
    private Func<Task>? _onShutdown;
    private ISpeechExperimentService? _experiments;
    private Func<SpeechEndDialogViewModel, Task>? _showEndDialog;
    private readonly Guid _experimentId = Guid.NewGuid();
    private Guid _trainingId;
    private int _trainingNumber;
    private readonly object _roundsGate = new();
    private readonly List<SpeechTrainingRound> _rounds = [];
    private bool _experimentEnded;
    private DateTimeOffset? _experimentEndedAt;
    private ExperimentRunStatus _experimentStatus = ExperimentRunStatus.Running;
    private bool _closing;
    private bool _backLocked;
    public override bool CanGoBack =>
        !_disposed && !_closing && (!_backLocked || _experimentEnded);
    private SpeechExperimentSource? _experimentSource;
    public AsyncRelayCommand AgainCommand { get; private set; } = null!;
    public string RoundCompletionText => "本轮已结束，可结束实验或再来一次";
    public bool CanEndExperiment =>
        !_disposed && !_experimentEnded && !_closing && _session?.IsCommitting != true;
    public bool CanTrainAgain => CanEndExperiment && IsEnded;

    private SpeechTrainingSession NewSession()
    {
        var session = _createSession();
        try
        {
            if (_createOutput is not null)
                session.AttachStimulation(
                    _createOutput(RouteData.Configuration),
                    DeviceEnvelopeOutput.Settings(RouteData.Configuration)
                );
            session.PersistAsync = SaveCheckpointAsync;
            _onShutdown ??= async () =>
            {
                if (!_disposed && !await FinishAsync(true, ExperimentRunStatus.InterruptedByExit))
                    throw new InvalidOperationException(StatusText);
            };
            _outputLifetime?.Register(session, _onShutdown);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task StartRoundAsync()
    {
        if (_session?.CanStart != true || !CanEndExperiment)
            return;
        if (_trainingId == Guid.Empty)
        {
            _trainingId = Guid.NewGuid();
            lock (_roundsGate)
                _trainingNumber = _rounds.Count + 1;
        }
        _experimentSource ??= SpeechExperimentSource.From(RouteData.Configuration);
        var wasBackLocked = _backLocked;
        _backLocked = true;
        Refresh();
        try
        {
            _outputError = null;
            await _session.StartAsync();
        }
        catch (Exception e)
        {
            _outputError = $"训练未能开始：{e.Message}";
        }
        finally
        {
            // Keep the lock across rounds, but allow leaving after an unsuccessful first start.
            _backLocked = wasBackLocked || _session.Started;
            RefreshPlaybackState();
        }
    }

    private async Task SaveCheckpointAsync(SpeechTrainingResult result, bool ended)
    {
        SpeechTrainingRound? previous;
        lock (_roundsGate)
            previous = _rounds.FirstOrDefault(x => x.Id == _trainingId);
        var round = new SpeechTrainingRound(
            _trainingId,
            _trainingNumber,
            DelayMilliseconds,
            ended ? previous?.EndedAt ?? DateTimeOffset.UtcNow : null,
            !ended ? SpeechRoundStatus.Running
                : result.Sentences.Count == result.SentenceCount ? SpeechRoundStatus.Completed
                : SpeechRoundStatus.Interrupted,
            result
        );
        if (_experiments is not null)
            await _experiments.SaveRoundAsync(_experimentId, _experimentSource!, round);
        lock (_roundsGate)
        {
            var index = _rounds.FindIndex(x => x.Id == round.Id);
            if (index < 0)
                _rounds.Add(round);
            else
                _rounds[index] = round;
        }
    }

    private async Task RequestEndAsync()
    {
        if (!CanEndExperiment)
            return;
        var dialog = new SpeechEndDialogViewModel(
            _session?.Snapshot().Sentences.Count ?? 0,
            _session is { Started: true, Ended: false }
        );
        if (_showEndDialog is null)
            return;
        await _showEndDialog(dialog);
        if (dialog.Choice == SpeechEndChoice.Cancel)
            return;
        await FinishAsync(dialog.Choice == SpeechEndChoice.Experiment);
    }

    // Also used by navigation and visual/flow tests; all paths share stop and persistence handling.
    public async Task<bool> FinishAsync(
        bool experiment,
        ExperimentRunStatus status = ExperimentRunStatus.Completed
    )
    {
        if (_closing || _experimentEnded)
            return _experimentEnded;
        _closing = true;
        ClearResponseReview();
        Refresh();
        try
        {
            _outputError = null;
            if (_session is not null)
                await _session.EndAsync();
            if (experiment)
            {
                if (_experiments is not null)
                    await _experiments.EndAsync(
                        _experimentId,
                        status,
                        source: status == ExperimentRunStatus.Completed
                            ? _experimentSource
                                ?? SpeechExperimentSource.From(RouteData.Configuration)
                            : null
                    );
                _experimentEnded = true;
                _experimentEndedAt = DateTimeOffset.UtcNow;
                _experimentStatus = status;
                if (status == ExperimentRunStatus.Completed)
                    _router.Navigate(
                        new SpeechExperimentResultsRouteData(
                            _experimentId,
                            ExperimentSnapshot(status)
                        )
                    );
            }
            return true;
        }
        catch (Exception e)
        {
            _outputError = $"结束失败：{e.Message}；请重试。";
            return false;
        }
        finally
        {
            _closing = false;
            Refresh();
        }
    }

    public SpeechExperimentResult ExperimentSnapshot(
        ExperimentRunStatus status = ExperimentRunStatus.Running
    )
    {
        lock (_roundsGate)
            return new(
                _experimentId,
                _experimentSource ?? SpeechExperimentSource.From(RouteData.Configuration),
                _rounds.FirstOrDefault()?.Result.StartedAt ?? DateTimeOffset.Now,
                _experimentEndedAt,
                _experimentEnded ? _experimentStatus : status,
                _rounds.ToArray()
            );
    }

    private async Task AgainAsync()
    {
        if (!CanTrainAgain || _session is null)
            return;
        _closing = true;
        Refresh();
        try
        {
            await _session.EndAsync();
            var settings = _session.Snapshot().Settings;
            var volume = _session.Volume;
            var next = NewSession();
            try
            {
                next.SelectCorpus(settings.Corpus);
                next.SelectListSize(settings.ListSize);
                next.SelectSentenceList(settings.SentenceList);
                next.ConfigureAudio(settings.AudioSettings ?? new());
                next.SetScoringRule(settings.AdaptiveScoring);
                next.Volume = volume;
            }
            catch
            {
                next.Dispose();
                throw;
            }
            _session.Changed -= OnSessionChanged;
            _session.TimelineChanged -= OnTimelineChanged;
            _session.Dispose();
            _session = next;
            _session.Changed += OnSessionChanged;
            _session.TimelineChanged += OnTimelineChanged;
            _trainingId = Guid.Empty;
            _resultRoute = null;
            _displayedSentence = null;
            _displayedAudio = null;
            _voice = [];
            _envelope = [];
            _listedCorpus = null;
            _outputError = null;
        }
        catch (Exception e)
        {
            _outputError = $"无法开始下一轮：{e.Message}";
        }
        finally
        {
            _closing = false;
            Refresh();
        }
    }
}
