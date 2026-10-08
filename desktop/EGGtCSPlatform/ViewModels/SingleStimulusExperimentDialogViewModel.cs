using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.ViewModels;

public enum SingleStimulusRunState
{
    Ready,
    Starting,
    Running,
    Stopping,
    Completed,
    Interrupted,
    Failed,
    StopUnconfirmed,
    Exited,
}

public partial class SingleStimulusExperimentDialogViewModel : DialogViewModel, IDisposable
{
    private readonly IExperimentRunService _runs;
    private readonly IExperimentRunPersistenceCoordinator? _persistence;
    private readonly IExperimentRunClock _clock;
    private readonly IExperimentRunErrorDialogService? _errors;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private CancellationTokenSource? _cancellation;
    private Task _activeRun = Task.CompletedTask;
    private Guid _runId;
    private bool _prepared;
    private bool _stopRequested;
    private bool _disposed;
    private DateTimeOffset _startedAt;
    private TimeSpan _plannedDuration;

    [ObservableProperty]
    private SingleStimulusRunState _state;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isStopping;

    [ObservableProperty]
    private bool _stopUnconfirmed;

    [ObservableProperty]
    private string _statusText = "未开始";

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private TimeSpan _elapsed;

    public SingleStimulusExperimentDialogViewModel(
        ExperimentRunRouteData route,
        IExperimentRunService runs,
        IExperimentRunPersistenceCoordinator? persistence = null,
        IExperimentRunClock? clock = null,
        ExperimentRunTimingOptions? timing = null,
        IExperimentRunErrorDialogService? errors = null
    )
    {
        Route = route;
        _runs = runs;
        _persistence = persistence;
        _clock = clock ?? new SystemExperimentRunClock();
        _errors = errors;
        timing ??= new ExperimentRunTimingOptions();
        var units = new[]
        {
            new DurationUnitOption(ExperimentDurationUnit.Milliseconds, "ms"),
            new DurationUnitOption(ExperimentDurationUnit.Seconds, "s"),
            new DurationUnitOption(ExperimentDurationUnit.Minutes, "min"),
        };
        Duration = new ExperimentStageViewModel(
            0,
            ExperimentRunStage.Stimulation,
            "刺激时长",
            units[2],
            true,
            timing.DurationStepMilliseconds,
            Math.Min(timing.DurationMaximumMilliseconds, 65535000),
            Math.Max(timing.DurationMinimumMilliseconds, 1000)
        )
        {
            Units = units,
        };
        Duration.SetDurationMilliseconds(
            (decimal)(route.ImportedTiming?.StimulationMilliseconds ?? 600_000)
        );
        Duration.PropertyChanged += OnDurationChanged;
        if (route.HistoricalResult is { } historical)
        {
            StatusText = historical.Status switch
            {
                ExperimentRunStatus.Completed => "已完成",
                ExperimentRunStatus.InterruptedByUser => "已紧急停止",
                ExperimentRunStatus.InterruptedByExit => "退出中断",
                ExperimentRunStatus.RecoveredAfterCrash => "崩溃恢复",
                _ => "运行失败",
            };
            Elapsed = historical.LogicalTimelineEndSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : historical.EndedAtUtc - historical.StartedAtUtc;
        }
        else
            _runs.TelemetryReceived += OnTelemetry;
        ValidateDuration();
    }

    public ExperimentRunRouteData Route { get; }
    public ExperimentStageViewModel Duration { get; }
    public bool IsReadOnly => Route.HistoricalResult is not null;
    public bool CanClose => !IsRunning && !IsStopping && !StopUnconfirmed;
    public bool IsFinished =>
        State is SingleStimulusRunState.Completed or SingleStimulusRunState.Interrupted;
    public bool CanEdit => CanClose && !IsReadOnly && !IsFinished;
    public bool CanStart =>
        CanEdit
        && !_prepared
        && !Duration.HasValidationError
        && Duration.GetDuration() is not null
        && !_disposed;
    public bool CanEmergencyStop => !IsReadOnly && !IsStopping && (IsRunning || StopUnconfirmed);
    public override bool AllowOverlayDismiss => CanClose;
    public WaveformDescriptor Waveform =>
        StimulusConfigurationSummaryBuilder.Waveform(Route.StimulusConfiguration);
    public string CurrentText =>
        StimulusConfigurationSummaryBuilder.CurrentText(Route.StimulusConfiguration);
    public bool ShowRemaining => !IsReadOnly && State != SingleStimulusRunState.Ready;
    public TimeSpan Remaining =>
        TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0d, (_plannedDuration - Elapsed).TotalSeconds)));
    public string RemainingText => $"{(int)Remaining.TotalMinutes}分{Remaining.Seconds}秒";
    public string ElapsedText => $"运行时长：{Elapsed:hh\\:mm\\:ss}";
    public string HistorySummary => $"被试：{Route.SubjectId}    实验：{Route.ExperimentId}";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void CloseExperiment()
    {
        if (CanClose)
            Close();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart)
            return;
        _stopRequested = false;
        _prepared = false;
        _runId = Guid.NewGuid();
        _startedAt = default;
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        ErrorText = string.Empty;
        _plannedDuration = Duration.GetDuration()!.Value;
        Elapsed = TimeSpan.Zero;
        NotifyRemaining();
        State = SingleStimulusRunState.Starting;
        StatusText = "启动中";
        IsRunning = true;
        _activeRun = RunAsync(_cancellation.Token);
        try
        {
            await _activeRun;
        }
        finally
        {
            if (!_stopRequested)
                IsRunning = false;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var duration = _plannedDuration;
        var operationRequested = false;
        try
        {
            var s = Route.StimulusConfiguration;
            var error = StimulusParameterPolicy.Validate(
                s.Kind,
                s.ShamMode,
                s.Frequency,
                s.RampSeconds,
                s.DutyPercent,
                true
            );
            if (error is not null)
                throw new InvalidOperationException(error);
            if (_persistence is not null && Route.ExperimentDatabaseId > 0)
            {
                await _persistence.PrepareAsync(
                    new ExperimentRunPreparation(
                        _runId,
                        Route.ExperimentDatabaseId,
                        ExperimentRunMode.Manual.ToString(),
                        1,
                        Route.SampleRateHz,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        duration,
                        TimeSpan.Zero,
                        SingleStimulusConfiguration.Snapshot(Route, duration)
                    ),
                    Route.DeviceId,
                    token
                );
                _prepared = true;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                _prepared ? _persistence!.RunCancellationToken : CancellationToken.None
            );
            var runToken = linked.Token;
            runToken.ThrowIfCancellationRequested();
            _startedAt = _clock.UtcNow;
            if (_prepared)
            {
                await _persistence!.RequestStageAsync(
                    ExperimentRunStage.Stimulation,
                    1,
                    TimeSpan.Zero,
                    runToken
                );
                await _persistence.ObserveStageAsync(
                    ExperimentRunStage.Stimulation,
                    1,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    runToken
                );
            }
            StatusText = $"{StimulusParameterPolicy.ModeName(s.Kind, s.ShamMode)}刺激中";
            State = SingleStimulusRunState.Running;
            operationRequested = true;
            await _runs.StartStimulationAsync(
                new StimulationRunRequest(
                    duration,
                    ExperimentRunRouteDataDefaults.GetTotalCurrent(s),
                    TimeSpan.Zero,
                    [],
                    Route.SampleRateHz,
                    s,
                    Route.StimulusElectrodes,
                    Route.DeviceId,
                    RecordingId: _runId
                ),
                runToken
            );
            runToken.ThrowIfCancellationRequested();
            Elapsed = duration;
            if (_prepared)
                await _persistence!.CompleteCurrentStageAsync(duration);
            await FinishAsync(ExperimentRunStatus.Completed);
            StatusText = "已结束";
            State = SingleStimulusRunState.Completed;
        }
        catch (OperationCanceledException) when (_stopRequested || _disposed) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            StatusText = "退出中断";
            State = SingleStimulusRunState.Exited;
        }
        catch (Exception exception)
        {
            if (_stopRequested)
                return; // The stop command owns terminal persistence.
            ErrorText = exception.Message;
            if (
                operationRequested
                && exception is not StimulationException
                && exception is not OperationCanceledException
            )
            {
                try
                {
                    await _runs.EmergencyStopAsync(Route.DeviceId);
                }
                catch (Exception stopError)
                {
                    StopUnconfirmed = true;
                    ErrorText += $"；停止失败：{stopError.Message}";
                }
            }
            if (
                exception is StimulationException { StopFailure: not null } stimulation
                && !string.IsNullOrWhiteSpace(stimulation.StopFailure)
            )
                StopUnconfirmed = true;
            StatusText = StopUnconfirmed ? "停止未确认" : "运行失败";
            State = StopUnconfirmed
                ? SingleStimulusRunState.StopUnconfirmed
                : SingleStimulusRunState.Failed;
            try
            {
                if (StopUnconfirmed)
                    await RecordStopFailureAsync(exception);
                else
                    await FinishAsync(ExperimentRunStatus.Failed, exception);
            }
            catch (Exception persistenceError)
            {
                ErrorText += $"；保存运行记录失败：{persistenceError.Message}";
            }
            if (_errors is not null)
                await _errors.ShowAsync("单刺激实验失败", ErrorText);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEmergencyStop))]
    private async Task EmergencyStopAsync()
    {
        if (!CanEmergencyStop)
            return;
        ErrorText = string.Empty;
        IsStopping = true;
        State = SingleStimulusRunState.Stopping;
        _stopRequested = true;
        _cancellation?.Cancel();
        StatusText = "停止中";
        var interruption = RequestInterruptionAsync();
        Exception? failure = null;
        try
        {
            await _runs.EmergencyStopAsync(Route.DeviceId);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        await _activeRun;
        await interruption;
        StopUnconfirmed = failure is not null;
        if (_startedAt != default && Elapsed == TimeSpan.Zero)
            Elapsed = _clock.UtcNow - _startedAt;
        try
        {
            if (failure is null)
                await FinishAsync(ExperimentRunStatus.InterruptedByUser, emergency: true);
            else
                await RecordStopFailureAsync(failure);
        }
        catch (Exception exception)
        {
            ErrorText = $"保存运行记录失败：{exception.Message}";
        }
        StatusText = failure is null ? "已紧急停止" : "停止未确认";
        State = failure is null
            ? SingleStimulusRunState.Interrupted
            : SingleStimulusRunState.StopUnconfirmed;
        if (failure is not null)
            ErrorText = $"停止刺激失败：{failure.Message}。请重试紧急停止。";
        IsRunning = false;
        IsStopping = false;
        if (failure is not null && _errors is not null)
            await _errors.ShowAsync("停止刺激失败", ErrorText);
    }

    private Task RecordStopFailureAsync(Exception exception) =>
        _prepared && _persistence!.RunId == _runId
            ? _persistence.RecordIncidentAsync(
                ExperimentIncidentKind.DeviceFailure,
                "单刺激停止未确认",
                exception.Message,
                exception
            )
            : Task.CompletedTask;

    private async Task RequestInterruptionAsync()
    {
        if (!_prepared || _persistence!.RunId != _runId)
            return;
        try
        {
            await _persistence.RequestInterruptionAsync("单刺激人工急停");
        }
        catch (Exception exception)
        {
            ErrorText = $"记录急停请求失败：{exception.Message}";
        }
    }

    private async Task FinishAsync(
        ExperimentRunStatus status,
        Exception? error = null,
        bool emergency = false
    )
    {
        if (!_prepared || _persistence!.RunId != _runId)
            return;
        await _persistence.FinishAsync(
            status,
            emergency ? ExperimentIncidentKind.UserEmergencyStop
                : error is null ? null
                : ExperimentIncidentKind.DeviceFailure,
            error,
            source: "单刺激实验",
            deviceStopSucceeded: emergency ? error is null : null,
            deviceStopError: emergency ? error?.Message : null,
            timelineEnd: Elapsed
        );
        _prepared = false;
    }

    private void OnTelemetry(object? sender, ExperimentRunTelemetryEventArgs args)
    {
        var runId = _runId;
        void Apply()
        {
            if (
                _disposed
                || !IsRunning
                || _stopRequested
                || runId != _runId
                || State is not (SingleStimulusRunState.Starting or SingleStimulusRunState.Running)
                || args.Telemetry.Stage != ExperimentRunStage.Stimulation
            )
                return;
            Elapsed = TimeSpan.FromTicks(
                Math.Clamp(args.Telemetry.StageElapsed.Ticks, Elapsed.Ticks, _plannedDuration.Ticks)
            );
            if (_prepared)
                _persistence!.ReportProgress(_runId, ExperimentRunStage.Stimulation, 1, Elapsed);
        }
        if (_uiContext is null || ReferenceEquals(_uiContext, SynchronizationContext.Current))
            Apply();
        else
            _uiContext.Post(_ => Apply(), null);
    }

    private void OnDurationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName
            is nameof(Duration.DurationMilliseconds)
                or nameof(Duration.SelectedUnit)
        )
            ValidateDuration();
    }

    private void ValidateDuration()
    {
        var s = Route.StimulusConfiguration;
        Duration.ValidationText = Duration.GetDuration() is { } duration
            ? duration.TotalSeconds != Math.Truncate(duration.TotalSeconds)
                ? "刺激时长必须为整数秒。"
                : StimulusParameterPolicy
                    .For(s.Kind, s.ShamMode)
                    .ValidateDuration(duration.TotalSeconds, s.Frequency, s.RampSeconds)
                    ?? string.Empty
            : "请输入有效刺激时长。";
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanEmergencyStop));
        OnPropertyChanged(nameof(AllowOverlayDismiss));
        StartCommand.NotifyCanExecuteChanged();
        EmergencyStopCommand.NotifyCanExecuteChanged();
        CloseExperimentCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value) => NotifyState();

    partial void OnIsStoppingChanged(bool value) => NotifyState();

    partial void OnStopUnconfirmedChanged(bool value) => NotifyState();

    partial void OnStateChanged(SingleStimulusRunState value)
    {
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(ShowRemaining));
        NotifyState();
    }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnElapsedChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(ElapsedText));
        NotifyRemaining();
    }

    private void NotifyRemaining()
    {
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(RemainingText));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runs.TelemetryReceived -= OnTelemetry;
        Duration.PropertyChanged -= OnDurationChanged;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }
}
