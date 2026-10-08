using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed class PreviewImpedanceDetectionService : IImpedanceDetectionService
{
    private readonly ConcurrentDictionary<
        (string DeviceId, ImpedanceDetectionKind Kind),
        int
    > _attempts = new();

    public async IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
            yield return await CreateReadings(
                    deviceId,
                    ImpedanceDetectionKind.Eeg,
                    electrodeIds,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyDictionary<string, double>> StartEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        CancellationToken cancellationToken = default
    ) => CreateReadings(deviceId, ImpedanceDetectionKind.Eeg, electrodeIds, cancellationToken);

    public Task StopEegAsync(
        string deviceId,
        IReadOnlyList<string> electrodeIds,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var electrodeIds = GetStimulationElectrodeIds(request.Assignments);
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
            yield return await CreateReadings(
                    deviceId,
                    ImpedanceDetectionKind.Stimulation,
                    electrodeIds,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyDictionary<string, double>> StartStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        CancellationToken cancellationToken = default
    ) =>
        CreateReadings(
            deviceId,
            ImpedanceDetectionKind.Stimulation,
            GetStimulationElectrodeIds(request.Assignments),
            cancellationToken
        );

    public Task StopStimulationAsync(
        string deviceId,
        StimulationImpedanceDetectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string[] GetStimulationElectrodeIds(
        IReadOnlyList<StimulusElectrodeAssignment> assignments
    ) =>
        assignments
            .Where(assignment => assignment.Role == StimulationChannelRole.Selectable)
            .Select(assignment => assignment.SiteId)
            .ToArray();

    private Task<IReadOnlyDictionary<string, double>> CreateReadings(
        string deviceId,
        ImpedanceDetectionKind kind,
        IReadOnlyList<string> electrodeIds,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = _attempts.AddOrUpdate((deviceId, kind), 1, (_, value) => value + 1);
        var values = (attempt % 3) switch
        {
            1 => new[] { 8.1, 21d, 34d, 45d, 13d, 7.5, 28d, 41d },
            2 => new[] { 7d, 8.1, 9d, 6.8, 8.5, 7.4, 9.2, 6.6 },
            _ => new[] { 9.4, 16d, 26d, 36d, 11d, 8.2, 23d, 32d },
        };
        IReadOnlyDictionary<string, double> result = electrodeIds
            .Select((id, index) => new { Id = id, Value = values[index % values.Length] })
            .ToDictionary(item => item.Id, item => item.Value, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }
}
