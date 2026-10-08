using System;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.Interfaces;

public interface IExperimentRunService
{
    event EventHandler<ExperimentRunTelemetryEventArgs>? TelemetryReceived;

    Task StartAcquisitionAsync(
        AcquisitionRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task StartStimulationAsync(
        StimulationRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task StartAutomaticExperimentAsync(
        AutomaticExperimentRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task StopCurrentOperationAsync(string deviceId, CancellationToken cancellationToken = default);

    Task EmergencyStopAsync(string deviceId, CancellationToken cancellationToken = default);

    Task BeginRecordingAsync(
        EegRecordingMetadata metadata,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    Task RecordDisplayFilterChangeAsync(
        EegFilterChangeRecord change,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    Task<EegRecordingCompletionResult> CompleteRecordingAsync(
        Guid recordingId,
        EegRecordingCompletionStatus status,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(new EegRecordingCompletionResult(EegPacketStatistics.Empty()));
}
