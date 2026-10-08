using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Persistence;

public sealed record ExperimentRunPreparation(
    Guid RunId,
    long ExperimentId,
    string RunMode,
    int CycleCount,
    int SampleRateHz,
    TimeSpan AcquisitionDuration,
    TimeSpan BlankingDuration,
    TimeSpan StimulationDuration,
    TimeSpan RecoveryDuration,
    ExperimentConfigurationTemplate? Configuration = null
);

public interface IActiveExperimentSession
{
    bool IsActive { get; }
    Guid RunId { get; }
    string DeviceId { get; }
}

public interface IExperimentRunPersistenceCoordinator : IActiveExperimentSession, IAsyncDisposable
{
    CancellationToken RunCancellationToken { get; }
    void ReportProgress(Guid runId, ExperimentRunStage stage, int cycle, TimeSpan elapsed);
    Task PrepareAsync(
        ExperimentRunPreparation preparation,
        string deviceId,
        CancellationToken cancellationToken = default
    );
    Task RequestStageAsync(
        ExperimentRunStage stage,
        int cycle,
        TimeSpan timelineStart,
        CancellationToken cancellationToken = default
    );
    Task ObserveStageAsync(
        ExperimentRunStage stage,
        int cycle,
        TimeSpan timelineStart,
        TimeSpan elapsed,
        CancellationToken cancellationToken = default
    );
    Task CompleteCurrentStageAsync(
        TimeSpan timelineEnd,
        CancellationToken cancellationToken = default
    );
    Task RequestInterruptionAsync(string source, CancellationToken cancellationToken = default);
    Task FinishAsync(
        ExperimentRunStatus status,
        ExperimentIncidentKind? incidentKind = null,
        Exception? exception = null,
        string source = "experiment-run",
        bool? deviceStopSucceeded = null,
        string? deviceStopError = null,
        CancellationToken cancellationToken = default,
        EegPacketStatistics? packetStatistics = null,
        TimeSpan? timelineEnd = null,
        double? dataEndExclusiveSeconds = null
    );
    Task RecordIncidentAsync(
        ExperimentIncidentKind kind,
        string source,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    );
}

public sealed class ExperimentRunPersistenceCoordinator(
    IDbContextFactory<AppDbContext> contextFactory,
    ApplicationSessionState applicationSession,
    IApplicationLogger? logger = null
) : IExperimentRunPersistenceCoordinator
{
    private IApplicationLogger Log => logger ?? ApplicationLog.Current;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _progressGate = new();
    private CancellationTokenSource? _runCancellation;
    private double _latestElapsedMilliseconds;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;
    private Guid _runId;
    private string _deviceId = string.Empty;
    private int _eventSequence;
    private ExperimentRunStage? _activeStage;
    private ExperimentRunStage? _lastStage;
    private int _activeCycle;

    public bool IsActive => RunId != Guid.Empty;
    public Guid RunId
    {
        get
        {
            lock (_progressGate)
                return _runId;
        }
    }
    public string DeviceId => _deviceId;
    public CancellationToken RunCancellationToken =>
        _runCancellation?.Token ?? CancellationToken.None;

    public void ReportProgress(Guid runId, ExperimentRunStage stage, int cycle, TimeSpan elapsed)
    {
        lock (_progressGate)
        {
            if (
                runId == Guid.Empty
                || runId != _runId
                || stage != _activeStage
                || cycle != _activeCycle
                || RunCancellationToken.IsCancellationRequested
            )
                return;
            _latestElapsedMilliseconds = Math.Max(
                _latestElapsedMilliseconds,
                NormalizeTimelineMilliseconds(elapsed.TotalMilliseconds)
            );
        }
    }

    private double LatestElapsed(double candidate = 0d)
    {
        lock (_progressGate)
            return Math.Max(_latestElapsedMilliseconds, NormalizeTimelineMilliseconds(candidate));
    }

    private void SetStage(ExperimentRunStage? stage, int cycle, double elapsed)
    {
        lock (_progressGate)
        {
            _activeStage = stage;
            if (stage is not null)
                _lastStage = stage;
            _activeCycle = cycle;
            _latestElapsedMilliseconds = Math.Max(
                _latestElapsedMilliseconds,
                NormalizeTimelineMilliseconds(elapsed)
            );
        }
    }

    public async Task PrepareAsync(
        ExperimentRunPreparation preparation,
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = new LoggedOperation(
            nameof(ExperimentRunPersistenceCoordinator),
            "Experiment.Prepare",
            $"experimentId={preparation.ExperimentId}; deviceId={deviceId}",
            preparation.RunId.ToString(),
            cancellationToken,
            Log
        );
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsActive)
                throw new InvalidOperationException("已有实验运行正在持久化。");
            var now = DateTimeOffset.UtcNow;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var experiment = await db.Experiments.SingleAsync(
                x => x.Id == preparation.ExperimentId,
                cancellationToken
            );
            db.ExperimentRuns.Add(
                new ExperimentRunEntity
                {
                    Id = preparation.RunId,
                    ConfigurationJson = preparation.Configuration is null
                        ? null
                        : JsonSerializer.Serialize(preparation.Configuration),
                    Experiment = experiment,
                    ApplicationSessionId = applicationSession.Id,
                    Status = ExperimentRunStatus.Preparing,
                    RunMode = preparation.RunMode,
                    CycleCount = preparation.CycleCount,
                    SampleRateHz = preparation.SampleRateHz,
                    AcquisitionDurationMilliseconds = preparation
                        .AcquisitionDuration
                        .TotalMilliseconds,
                    BlankingDurationMilliseconds = preparation.BlankingDuration.TotalMilliseconds,
                    StimulationDurationMilliseconds = preparation
                        .StimulationDuration
                        .TotalMilliseconds,
                    RecoveryDurationMilliseconds = preparation.RecoveryDuration.TotalMilliseconds,
                    CreatedAtUtc = now,
                    LastHeartbeatAtUtc = now,
                }
            );
            experiment.Status = ExperimentStatus.Running;
            experiment.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            lock (_progressGate)
            {
                _runCancellation?.Dispose();
                _runCancellation = new CancellationTokenSource();
                _runId = preparation.RunId;
                _latestElapsedMilliseconds = 0d;
                _deviceId = deviceId;
                _activeStage = null;
                _lastStage = null;
                _activeCycle = 0;
            }
            _eventSequence = 0;
            _heartbeatCancellation?.Dispose();
            _heartbeatCancellation = new CancellationTokenSource();
            _heartbeatTask = RunHeartbeatAsync(_runId, _heartbeatCancellation.Token);
            operation.Complete(
                $"experimentId={preparation.ExperimentId}; deviceId={deviceId}; prepared=true"
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RequestStageAsync(
        ExperimentRunStage stage,
        int cycle,
        TimeSpan timelineStart,
        CancellationToken cancellationToken = default
    )
    {
        if (!TryMapStage(stage, out var kind))
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireActive();
            RunCancellationToken.ThrowIfCancellationRequested();
            using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                RunCancellationToken
            );
            cancellationToken = stageCancellation.Token;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await CloseActiveEventsAsync(
                db,
                DateTimeOffset.UtcNow,
                ExperimentEventStatus.Completed,
                EventEndTimeAccuracy.Exact,
                timelineStart.TotalMilliseconds,
                cancellationToken
            );
            var now = DateTimeOffset.UtcNow;
            db.ExperimentEvents.Add(
                new ExperimentEventEntity
                {
                    ExperimentRunId = _runId,
                    Kind = kind,
                    Cycle = Math.Max(1, cycle),
                    Sequence = ++_eventSequence,
                    RequestedAtUtc = now,
                    TimelineStartMilliseconds = NormalizeTimelineMilliseconds(
                        timelineStart.TotalMilliseconds
                    ),
                    Status = ExperimentEventStatus.Starting,
                    EndTimeAccuracy = EventEndTimeAccuracy.Exact,
                }
            );
            var run = await db.ExperimentRuns.SingleAsync(x => x.Id == _runId, cancellationToken);
            run.LastKnownStage = stage.ToString();
            run.LastKnownCycle = Math.Max(1, cycle);
            run.LastKnownElapsedMilliseconds = Math.Max(
                run.LastKnownElapsedMilliseconds,
                LatestElapsed(timelineStart.TotalMilliseconds)
            );
            run.LastHeartbeatAtUtc = now;
            run.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            SetStage(stage, Math.Max(1, cycle), run.LastKnownElapsedMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ObserveStageAsync(
        ExperimentRunStage stage,
        int cycle,
        TimeSpan timelineStart,
        TimeSpan elapsed,
        CancellationToken cancellationToken = default
    )
    {
        if (!TryMapStage(stage, out var kind) || !IsActive)
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsActive)
                return;
            RunCancellationToken.ThrowIfCancellationRequested();
            using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                RunCancellationToken
            );
            cancellationToken = stageCancellation.Token;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var normalizedCycle = Math.Max(1, cycle);
            if (
                normalizedCycle < _activeCycle
                || normalizedCycle == _activeCycle
                    && _lastStage is { } lastStage
                    && (stage < lastStage || stage == lastStage && _activeStage is null)
            )
                return;
            if (_activeStage != stage || _activeCycle != normalizedCycle)
            {
                await CloseActiveEventsAsync(
                    db,
                    now,
                    ExperimentEventStatus.Completed,
                    EventEndTimeAccuracy.Exact,
                    timelineStart.TotalMilliseconds,
                    cancellationToken
                );
                db.ExperimentEvents.Add(
                    new ExperimentEventEntity
                    {
                        ExperimentRunId = _runId,
                        Kind = kind,
                        Cycle = normalizedCycle,
                        Sequence = ++_eventSequence,
                        RequestedAtUtc = now,
                        StartedAtUtc = now,
                        TimelineStartMilliseconds = NormalizeTimelineMilliseconds(
                            timelineStart.TotalMilliseconds
                        ),
                        Status = ExperimentEventStatus.Running,
                        EndTimeAccuracy = EventEndTimeAccuracy.Exact,
                    }
                );
            }
            else
            {
                var activeEvent = await db
                    .ExperimentEvents.Where(x =>
                        x.ExperimentRunId == _runId
                        && (
                            x.Status == ExperimentEventStatus.Starting
                            || x.Status == ExperimentEventStatus.Running
                        )
                    )
                    .OrderByDescending(x => x.Sequence)
                    .FirstOrDefaultAsync(cancellationToken);
                if (activeEvent is { Status: ExperimentEventStatus.Starting })
                {
                    activeEvent.Status = ExperimentEventStatus.Running;
                    activeEvent.StartedAtUtc = now;
                    activeEvent.TimelineStartMilliseconds ??= NormalizeTimelineMilliseconds(
                        timelineStart.TotalMilliseconds
                    );
                }
            }
            var run = await db.ExperimentRuns.SingleAsync(x => x.Id == _runId, cancellationToken);
            run.Status = ExperimentRunStatus.Running;
            var firstStart = run.StartedAtUtc is null;
            run.StartedAtUtc ??= now;
            run.LastKnownStage = stage.ToString();
            run.LastKnownCycle = normalizedCycle;
            run.LastKnownElapsedMilliseconds = Math.Max(
                run.LastKnownElapsedMilliseconds,
                LatestElapsed(elapsed.TotalMilliseconds)
            );
            run.LastHeartbeatAtUtc = now;
            run.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            SetStage(stage, normalizedCycle, run.LastKnownElapsedMilliseconds);
            if (firstStart)
                Log.Write(
                    ApplicationLogLevel.Info,
                    nameof(ExperimentRunPersistenceCoordinator),
                    "Experiment.Started",
                    $"experimentId={run.ExperimentId}; deviceId={_deviceId}",
                    _runId.ToString()
                );
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteCurrentStageAsync(
        TimeSpan timelineEnd,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsActive)
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsActive)
                return;
            RunCancellationToken.ThrowIfCancellationRequested();
            using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                RunCancellationToken
            );
            cancellationToken = stageCancellation.Token;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var logicalEnd = LatestElapsed(timelineEnd.TotalMilliseconds);
            await CloseActiveEventsAsync(
                db,
                now,
                ExperimentEventStatus.Completed,
                EventEndTimeAccuracy.Exact,
                logicalEnd,
                cancellationToken
            );
            var run = await db.ExperimentRuns.SingleAsync(x => x.Id == _runId, cancellationToken);
            run.LastKnownElapsedMilliseconds = Math.Max(
                run.LastKnownElapsedMilliseconds,
                logicalEnd
            );
            run.LastHeartbeatAtUtc = now;
            run.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            SetStage(null, _activeCycle, logicalEnd);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RequestInterruptionAsync(
        string source,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsActive)
            return;
        // Cancel the orchestration before waiting for database I/O or stopping the device.
        _runCancellation?.Cancel();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsActive)
                return;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var run = await db.ExperimentRuns.SingleAsync(x => x.Id == _runId, cancellationToken);
            if (run.InterruptRequestedAtUtc is null)
            {
                var now = DateTimeOffset.UtcNow;
                run.InterruptRequestedAtUtc = now;
                run.InterruptRequestSource = source;
                Log.Write(
                    ApplicationLogLevel.Info,
                    nameof(ExperimentRunPersistenceCoordinator),
                    "Experiment.StopRequested",
                    $"source={source}; deviceId={_deviceId}",
                    _runId.ToString()
                );
                run.LastHeartbeatAtUtc = now;
                run.LastKnownElapsedMilliseconds = Math.Max(
                    run.LastKnownElapsedMilliseconds,
                    LatestElapsed()
                );
                run.Revision++;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FinishAsync(
        ExperimentRunStatus status,
        ExperimentIncidentKind? incidentKind = null,
        Exception? exception = null,
        string source = "experiment-run",
        bool? deviceStopSucceeded = null,
        string? deviceStopError = null,
        CancellationToken cancellationToken = default,
        EegPacketStatistics? packetStatistics = null,
        TimeSpan? timelineEnd = null,
        double? dataEndExclusiveSeconds = null
    )
    {
        if (!IsActive)
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsActive)
                return;
            if (status == ExperimentRunStatus.Completed)
                RunCancellationToken.ThrowIfCancellationRequested();
            using var finishCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                status == ExperimentRunStatus.Completed
                    ? RunCancellationToken
                    : CancellationToken.None
            );
            cancellationToken = finishCancellation.Token;
            var now = DateTimeOffset.UtcNow;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var run = await db
                .ExperimentRuns.Include(x => x.Experiment)
                .SingleAsync(x => x.Id == _runId, cancellationToken);
            if (
                run.Status
                is ExperimentRunStatus.Completed
                    or ExperimentRunStatus.InterruptedByUser
                    or ExperimentRunStatus.InterruptedByExit
                    or ExperimentRunStatus.Failed
                    or ExperimentRunStatus.RecoveredAfterCrash
            )
            {
                ClearActiveState();
                return;
            }
            var eventStatus =
                status == ExperimentRunStatus.Completed
                    ? ExperimentEventStatus.Completed
                    : ExperimentEventStatus.Interrupted;
            var logicalEndMilliseconds = Math.Max(
                run.LastKnownElapsedMilliseconds,
                LatestElapsed(timelineEnd?.TotalMilliseconds ?? 0d)
            );
            logicalEndMilliseconds = Math.Max(
                logicalEndMilliseconds,
                NormalizeTimelineMilliseconds((dataEndExclusiveSeconds ?? 0d) * 1000d)
            );
            await CloseActiveEventsAsync(
                db,
                now,
                eventStatus,
                EventEndTimeAccuracy.Exact,
                logicalEndMilliseconds,
                cancellationToken
            );
            run.LastKnownElapsedMilliseconds = Math.Max(
                run.LastKnownElapsedMilliseconds,
                NormalizeTimelineMilliseconds(logicalEndMilliseconds)
            );
            run.Status = status;
            run.EndedAtUtc = now;
            run.LastHeartbeatAtUtc = now;
            run.Revision++;
            if (packetStatistics is not null)
            {
                run.EegPacketReorderingEnabled = packetStatistics.ReorderingEnabled;
                run.EegReceivedPacketCount = packetStatistics.ReceivedPacketCount;
                run.EegLostPacketCount = packetStatistics.LostPacketCount;
                run.EegOutOfOrderPacketCount = packetStatistics.OutOfOrderPacketCount;
                run.EegDuplicatePacketCount = packetStatistics.DuplicatePacketCount;
                run.EegLateDiscardedPacketCount = packetStatistics.LateDiscardedPacketCount;
            }
            run.Experiment.Status = status switch
            {
                ExperimentRunStatus.Completed => ExperimentStatus.Completed,
                ExperimentRunStatus.Failed => ExperimentStatus.Failed,
                _ => ExperimentStatus.Interrupted,
            };
            run.Experiment.UpdatedAtUtc = now;
            if (incidentKind is { } kind)
            {
                run.Incidents.Add(
                    new ExperimentIncidentEntity
                    {
                        Kind = kind,
                        OccurredAtUtc = now,
                        Source = source,
                        Message = exception?.Message ?? source,
                        ExceptionType = exception?.GetType().FullName,
                        StackTrace = exception?.StackTrace,
                        DeviceStopSucceeded = deviceStopSucceeded,
                        DeviceStopError = deviceStopError,
                    }
                );
            }
            await db.SaveChangesAsync(cancellationToken);
            _heartbeatCancellation?.Cancel();
            Log.Write(
                status == ExperimentRunStatus.Failed
                    ? ApplicationLogLevel.Error
                    : ApplicationLogLevel.Info,
                nameof(ExperimentRunPersistenceCoordinator),
                "Experiment." + status,
                $"experimentId={run.ExperimentId}; deviceId={_deviceId}; source={source}; deviceStopSucceeded={deviceStopSucceeded}",
                _runId.ToString(),
                exception
            );
            ClearActiveState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordIncidentAsync(
        ExperimentIncidentKind kind,
        string source,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsActive)
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            db.ExperimentIncidents.Add(
                new ExperimentIncidentEntity
                {
                    ExperimentRunId = _runId,
                    Kind = kind,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Source = source,
                    Message = message,
                    ExceptionType = exception?.GetType().FullName,
                    StackTrace = exception?.StackTrace,
                }
            );
            await db.SaveChangesAsync(cancellationToken);
            Log.Write(
                exception is null ? ApplicationLogLevel.Warning : ApplicationLogLevel.Error,
                nameof(ExperimentRunPersistenceCoordinator),
                "Experiment.Incident",
                $"kind={kind}; deviceId={_deviceId}; {message}",
                _runId.ToString(),
                exception
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunHeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (!IsActive || _runId != runId)
                        return;
                    await using var db = await contextFactory.CreateDbContextAsync(
                        cancellationToken
                    );
                    var now = DateTimeOffset.UtcNow;
                    var elapsed = LatestElapsed();
                    await db
                        .ExperimentRuns.Where(x => x.Id == runId)
                        .ExecuteUpdateAsync(
                            setters =>
                                setters
                                    .SetProperty(x => x.LastHeartbeatAtUtc, now)
                                    .SetProperty(
                                        x => x.LastKnownElapsedMilliseconds,
                                        x =>
                                            x.LastKnownElapsedMilliseconds > elapsed
                                                ? x.LastKnownElapsedMilliseconds
                                                : elapsed
                                    )
                                    .SetProperty(x => x.Revision, x => x.Revision + 1),
                            cancellationToken
                        );
                    await db
                        .ApplicationSessions.Where(x => x.Id == applicationSession.Id)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(x => x.LastHeartbeatAtUtc, now),
                            cancellationToken
                        );
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task CloseActiveEventsAsync(
        AppDbContext db,
        DateTimeOffset endedAt,
        ExperimentEventStatus status,
        EventEndTimeAccuracy accuracy,
        double? timelineEndMilliseconds,
        CancellationToken cancellationToken
    )
    {
        var active = await db
            .ExperimentEvents.Where(x =>
                x.ExperimentRunId == _runId
                && (
                    x.Status == ExperimentEventStatus.Starting
                    || x.Status == ExperimentEventStatus.Running
                )
            )
            .ToListAsync(cancellationToken);
        foreach (var item in active)
        {
            item.EndedAtUtc = endedAt;
            if (timelineEndMilliseconds is { } logicalEnd)
            {
                var normalizedEnd = NormalizeTimelineMilliseconds(logicalEnd);
                item.TimelineEndMilliseconds = item.TimelineStartMilliseconds is { } logicalStart
                    ? Math.Max(logicalStart, normalizedEnd)
                    : normalizedEnd;
            }
            item.Status = status;
            item.EndTimeAccuracy = accuracy;
            if (item.StartedAtUtc is null && status == ExperimentEventStatus.Completed)
                item.StartedAtUtc = item.RequestedAtUtc;
        }
    }

    private static double NormalizeTimelineMilliseconds(double milliseconds) =>
        double.IsFinite(milliseconds) ? Math.Max(0d, milliseconds) : 0d;

    private void RequireActive()
    {
        if (!IsActive)
            throw new InvalidOperationException("没有活动实验运行。");
    }

    private void ClearActiveState()
    {
        lock (_progressGate)
        {
            _runId = Guid.Empty;
            _deviceId = string.Empty;
            _activeStage = null;
            _lastStage = null;
            _activeCycle = 0;
            _latestElapsedMilliseconds = 0d;
        }
    }

    private static bool TryMapStage(ExperimentRunStage stage, out ExperimentEventKind kind)
    {
        kind = stage switch
        {
            ExperimentRunStage.Acquisition => ExperimentEventKind.Acquisition,
            ExperimentRunStage.Blanking => ExperimentEventKind.Blanking,
            ExperimentRunStage.Stimulation => ExperimentEventKind.Stimulation,
            ExperimentRunStage.Recovery => ExperimentEventKind.Recovery,
            _ => default,
        };
        return stage
            is ExperimentRunStage.Acquisition
                or ExperimentRunStage.Blanking
                or ExperimentRunStage.Stimulation
                or ExperimentRunStage.Recovery;
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatCancellation?.Cancel();
        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask;
            }
            catch (OperationCanceledException) { }
        }
        _heartbeatCancellation?.Dispose();
        _runCancellation?.Dispose();
        _gate.Dispose();
    }
}
