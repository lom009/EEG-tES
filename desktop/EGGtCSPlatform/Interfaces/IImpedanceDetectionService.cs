using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Interfaces;

public enum ImpedanceDetectionKind
{
    Stimulation,
    Eeg,
}

public sealed class StimulationImpedanceDetectionRequest
{
    public StimulationImpedanceDetectionRequest(
        ExperimentStimulusConfigurationSnapshot configuration,
        IEnumerable<StimulusElectrodeAssignment> assignments
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assignments);
        Configuration = ExperimentStimulusConfigurationSnapshot.Freeze(configuration);
        Assignments = Array.AsReadOnly(
            assignments.Select(assignment => assignment with { }).ToArray()
        );
    }

    public ExperimentStimulusConfigurationSnapshot Configuration { get; }

    public IReadOnlyList<StimulusElectrodeAssignment> Assignments { get; }
}

public interface IImpedanceDetectionService
{
    IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyDictionary<string, double>> StartEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        CancellationToken cancellationToken = default
    );

    Task StopEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        CancellationToken cancellationToken = default
    );

    IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyDictionary<string, double>> StartStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        CancellationToken cancellationToken = default
    );

    Task StopStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        CancellationToken cancellationToken = default
    );
}
