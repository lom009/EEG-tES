using System.Collections.Generic;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

internal sealed record ExperimentRunTelemetryDrain(
    IReadOnlyList<ExperimentRunTelemetry> StateUpdates,
    IReadOnlyList<ExperimentRunTelemetry> WaveformUpdates
)
{
    public bool IsEmpty => StateUpdates.Count == 0 && WaveformUpdates.Count == 0;
}

internal sealed class ExperimentRunTelemetryBuffer
{
    private readonly object _gate = new();
    private readonly List<ExperimentRunTelemetry> _stateUpdates = [];
    private readonly List<ExperimentRunTelemetry> _waveformUpdates = [];
    private ExperimentRunTelemetry? _latestAcceptedState;

    public void Enqueue(ExperimentRunTelemetry telemetry)
    {
        lock (_gate)
        {
            if (telemetry.WaveformBatches.Count > 0)
                _waveformUpdates.Add(telemetry);
            if (telemetry.IsWaveformOnly)
                return;

            var latest = _stateUpdates.Count > 0 ? _stateUpdates[^1] : _latestAcceptedState;
            if (latest is null)
            {
                _stateUpdates.Add(telemetry);
                _latestAcceptedState = telemetry;
                return;
            }

            if (IsOlderState(telemetry, latest))
                return;
            if (
                latest.Stage == telemetry.Stage
                && latest.CurrentCycle == telemetry.CurrentCycle
                && latest.CommunicationHealthy == telemetry.CommunicationHealthy
                && latest.Exceptions.Count == 0
                && telemetry.Exceptions.Count == 0
            )
            {
                if (_stateUpdates.Count > 0)
                    _stateUpdates[^1] = telemetry;
                else
                    _stateUpdates.Add(telemetry);
                _latestAcceptedState = telemetry;
                return;
            }
            _stateUpdates.Add(telemetry);
            _latestAcceptedState = telemetry;
        }
    }

    public ExperimentRunTelemetryDrain Drain()
    {
        lock (_gate)
        {
            if (_stateUpdates.Count == 0 && _waveformUpdates.Count == 0)
                return new ExperimentRunTelemetryDrain([], []);
            var drain = new ExperimentRunTelemetryDrain(
                _stateUpdates.ToArray(),
                _waveformUpdates.ToArray()
            );
            _stateUpdates.Clear();
            _waveformUpdates.Clear();
            return drain;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _stateUpdates.Clear();
            _waveformUpdates.Clear();
            _latestAcceptedState = null;
        }
    }

    private static bool IsOlderState(
        ExperimentRunTelemetry candidate,
        ExperimentRunTelemetry latest
    )
    {
        if (candidate.Stage is ExperimentRunStage.Completed or ExperimentRunStage.Stopped)
            return false;
        if (latest.Stage is ExperimentRunStage.Completed or ExperimentRunStage.Stopped)
            return true;
        if (candidate.CurrentCycle < latest.CurrentCycle)
            return true;
        if (candidate.CurrentCycle != latest.CurrentCycle)
            return false;
        if (candidate.TotalElapsed < latest.TotalElapsed)
            return true;
        return candidate.TotalElapsed == latest.TotalElapsed
            && GetStageOrder(candidate.Stage) < GetStageOrder(latest.Stage);
    }

    private static int GetStageOrder(ExperimentRunStage stage) =>
        stage switch
        {
            ExperimentRunStage.Standby => -1,
            ExperimentRunStage.Acquisition => 0,
            ExperimentRunStage.Blanking => 1,
            ExperimentRunStage.Stimulation => 2,
            ExperimentRunStage.Recovery => 3,
            ExperimentRunStage.Completed => 4,
            ExperimentRunStage.Stopped => 4,
            _ => -1,
        };
}
