using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.VisualTests;

internal static class ExperimentRunVisualScenario
{
    public static ExperimentRunPageViewModel CreateViewModel(out VisualExperimentRunService service)
    {
        service = new VisualExperimentRunService();
        var viewModel = new ExperimentRunPageViewModel(
            CreateRoute(),
            new VisualTestNavigationRouter(),
            service,
            new ImmediateVisualClock()
        );

        SetDuration(viewModel.Stages[0], viewModel.DurationUnits[2], 1m);
        SetDuration(viewModel.Stages[2], viewModel.DurationUnits[2], 1m);
        viewModel.ShowXCursor = true;
        viewModel.ShowYCursor = true;
        return viewModel;
    }

    private static ExperimentRunRouteData CreateRoute()
    {
        var source = ExperimentRunRouteDataDefaults.Create();
        var scenario = VisualTestOptions.Current.Scenario;
        var hd = scenario.Contains("hd", StringComparison.OrdinalIgnoreCase);
        var multi = scenario.Contains("multi", StringComparison.OrdinalIgnoreCase);
        var targets = new List<ExperimentStimulusTargetSnapshot>();
        var assignments = new List<StimulusElectrodeAssignment>();
        var sites = new[] { "Fz", "CP4", "T7", "T8", "P3", "C3", "C4", "P4", "O1", "O2" };
        for (var index = 0; index < (multi ? 2 : 1); index++)
        {
            var id = Guid.NewGuid();
            var peak = 0.6 + index * 0.2;
            var channels = new List<ExperimentStimulationChannelSnapshot>();
            var returns = hd || multi ? 4 : 1;
            for (var channel = 0; channel <= returns; channel++)
            {
                var physical = index * 5 + channel + 1;
                var role =
                    channel == 0
                        ? StimulationChannelRole.FixedActive
                        : StimulationChannelRole.Selectable;
                channels.Add(new(physical, role, channel == 0 ? peak : peak / returns));
                assignments.Add(new(sites[physical - 1], id, physical, role));
            }
            targets.Add(new(id, index + 1, peak, index * 5 + 1, channels));
        }
        var config = source.StimulusConfiguration with
        {
            Kind = scenario.Contains("tdcs", StringComparison.OrdinalIgnoreCase) ? StimulusKind.TDcs : StimulusKind.TAcs,
            Frequency = 100,
            Direction = scenario.Contains("tdcs", StringComparison.OrdinalIgnoreCase) ? StimulusDirection.Positive : StimulusDirection.Bidirectional,
            Targets = targets,
            ArrayMode =
                multi ? StimulusArrayMode.MultiTarget
                : hd ? StimulusArrayMode.Hd
                : StimulusArrayMode.DualChannel,
        };
        var measuredAt = DateTimeOffset.Now.AddMinutes(-2);
        var snapshot = new PreRunImpedanceSnapshot(
            [
                new("F3", 3, ImpedanceBand.UpTo10KOhms),
                new("FC3", 4, ImpedanceBand.UpTo20KOhms),
                new("Cz", 25, ImpedanceBand.UpTo30KOhms),
            ],
            assignments
                .Where(x => x.Role == StimulationChannelRole.Selectable)
                .Select(x => new PreRunImpedanceChannel(
                    x.SiteId,
                    x.PhysicalChannelId,
                    x.PhysicalChannelId % 5 == 0 ? ImpedanceBand.Abnormal : ImpedanceBand.Normal
                )),
            measuredAt,
            measuredAt
        );
        return new(
            source.ExperimentId,
            source.SubjectId,
            config,
            assignments,
            ["F3", "FC3", "Cz"],
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            preRunImpedance: snapshot
        );
    }

    private static void SetDuration(
        ExperimentStageViewModel stage,
        DurationUnitOption unit,
        decimal value
    )
    {
        stage.SelectedUnit = unit;
        stage.DurationValue = value;
    }
}

internal sealed class ImmediateVisualClock : IExperimentRunClock
{
    public DateTimeOffset UtcNow { get; private set; } =
        new(2026, 9, 1, 12, 23, 21, TimeSpan.FromHours(8));

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UtcNow += delay;
        return Task.CompletedTask;
    }
}

internal sealed class VisualExperimentRunService : IExperimentRunService, IDisposable
{
    private readonly TaskCompletionSource _acquisitionRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _stimulationRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public event EventHandler<ExperimentRunTelemetryEventArgs>? TelemetryReceived;

    public async Task StartAcquisitionAsync(
        AcquisitionRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        const double elapsedSeconds = 3d;
        var timelineOffsetSeconds = request.TimelineOffset.TotalSeconds;
        var batches = request
            .ChannelIds.SelectMany(
                (channelId, channelIndex) =>
                    channelIndex == 1
                        ? new[]
                        {
                            CreateBatch(
                                channelId,
                                channelIndex,
                                timelineOffsetSeconds,
                                1.352d,
                                request.SampleRateHz
                            ),
                            CreateBatch(
                                channelId,
                                channelIndex,
                                timelineOffsetSeconds + 1.39d,
                                elapsedSeconds - 1.39d,
                                request.SampleRateHz
                            ),
                        }
                        : new[]
                        {
                            CreateBatch(
                                channelId,
                                channelIndex,
                                timelineOffsetSeconds,
                                elapsedSeconds,
                                request.SampleRateHz
                            ),
                        }
            )
            .ToArray();
        Publish(
            ExperimentRunStage.Acquisition,
            timelineOffsetSeconds + elapsedSeconds,
            elapsedSeconds,
            elapsedSeconds / request.Duration.TotalSeconds,
            0d,
            batches
        );
        await _acquisitionRelease.Task.WaitAsync(cancellationToken);
    }

    public async Task StartStimulationAsync(
        StimulationRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        const double stageElapsedSeconds = 2d;
        var protocolTimeSeconds = request.TimelineOffset.TotalSeconds + stageElapsedSeconds;
        Publish(
            ExperimentRunStage.Stimulation,
            protocolTimeSeconds,
            stageElapsedSeconds,
            stageElapsedSeconds / request.Duration.TotalSeconds,
            0d,
            []
        );
        await _stimulationRelease.Task.WaitAsync(cancellationToken);
    }

    public Task StartAutomaticExperimentAsync(
        AutomaticExperimentRunRequest request,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("视觉验收场景使用手动两阶段流程。");

    public Task StopCurrentOperationAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task EmergencyStopAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public void ContinueAfterAcquisition() => _acquisitionRelease.TrySetResult();

    public void ContinueAfterStimulation() => _stimulationRelease.TrySetResult();

    public void Dispose()
    {
        _acquisitionRelease.TrySetResult();
        _stimulationRelease.TrySetResult();
    }

    private void Publish(
        ExperimentRunStage stage,
        double protocolTimeSeconds,
        double stageElapsedSeconds,
        double stageProgress,
        double actualCurrentMilliAmps,
        IReadOnlyList<WaveformChannelBatch> batches
    ) =>
        TelemetryReceived?.Invoke(
            this,
            new ExperimentRunTelemetryEventArgs(
                new ExperimentRunTelemetry(
                    stage,
                    TimeSpan.FromSeconds(protocolTimeSeconds),
                    TimeSpan.FromSeconds(stageElapsedSeconds),
                    Math.Clamp(stageProgress, 0d, 1d),
                    1,
                    1,
                    actualCurrentMilliAmps,
                    3.9d,
                    true,
                    [],
                    batches
                )
            )
        );

    private static WaveformChannelBatch CreateBatch(
        string channelId,
        int channelIndex,
        double startSeconds,
        double durationSeconds,
        int sampleRateHz
    )
    {
        var count = Math.Max(2, (int)Math.Round(durationSeconds * sampleRateHz));
        var interval = 1d / sampleRateHz;
        var random = new Random(20823 + channelIndex * 97);
        var samples = new double[count];

        for (var index = 0; index < samples.Length; index++)
        {
            var time = startSeconds + index * interval;
            var alpha = 28d * Math.Sin(2d * Math.PI * (8.5d + channelIndex * 0.35d) * time);
            var slow =
                18d * Math.Sin(2d * Math.PI * (0.8d + channelIndex * 0.08d) * time + channelIndex);
            var noise = (random.NextDouble() - 0.5d) * 12d;
            var artifact =
                index % Math.Max(1, sampleRateHz / 2) < 5
                    ? 55d * Math.Exp(-(index % Math.Max(1, sampleRateHz / 2)) / 2.2d)
                    : 0d;
            samples[index] = alpha + slow + noise + artifact;
        }

        return new WaveformChannelBatch(channelId, startSeconds, interval, samples);
    }
}
