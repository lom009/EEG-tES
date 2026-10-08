using System;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed partial class ExperimentRunPageViewModelTests
{
    [Fact]
    public void AcquisitionOnlyHistoricalReplayPreservesPreviouslyRecordedStages()
    {
        var source = CreateRoute();
        HistoricalExperimentStageInterval[] intervals =
        [
            new(ExperimentRunStage.Acquisition, 1, 0, 2),
            new(ExperimentRunStage.Blanking, 1, 2, 3),
            new(ExperimentRunStage.Stimulation, 1, 3, 18),
            new(ExperimentRunStage.Recovery, 1, 18, 19),
            new(ExperimentRunStage.Acquisition, 1, 19, 21),
        ];
        var history = new HistoricalExperimentRunContext(
            Guid.NewGuid(),
            "missing.eegraw",
            ExperimentRunStatus.Completed,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(21),
            new System.Collections.Generic.Dictionary<int, string>(),
            intervals,
            LogicalTimelineEndSeconds: 21
        );
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            source.AcquisitionChannels,
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            importedTiming: new ExperimentTimingTemplate(
                ExperimentRunMode.Manual,
                2000,
                1000,
                15000,
                1000,
                1
            ),
            historicalResult: history,
            creationMode: ExperimentCreationMode.AcquisitionOnly
        );
        using var model = CreateModel(route);
        Assert.Equal(intervals.Select(x => x.Stage), model.TimelineSegments.Select(x => x.Stage));
        Assert.Equal(21, model.TimelineMaximum);
    }

    [Fact]
    public async Task AcquisitionOnlyManualCompletesAfterBlankingWithoutStimulationOrFinalAcquisition()
    {
        var service = new FakeRunService { PublishAcquisitionTelemetry = true };
        using var model = CreateModel(
            CreateRoute(
                StimulusKind.TAcs,
                frequency: -1,
                creationMode: ExperimentCreationMode.AcquisitionOnly
            ),
            service: service
        );
        model.AcquisitionStage.DurationValue = 2;

        Assert.Equal(
            new[] { ExperimentRunStage.Acquisition, ExperimentRunStage.Blanking },
            model.Stages.Select(x => x.Stage)
        );
        Assert.Equal("采集", model.AcquisitionStage.Title);
        Assert.Equal(2, model.TimelineSegments.Count);
        Assert.True(model.StartAcquisitionCommand.CanExecute(null));
        await model.StartAcquisitionCommand.ExecuteAsync(null);

        Assert.True(model.IsCompleted);
        Assert.Equal(new[] { "Acquisition" }, service.Calls);
        Assert.Single(service.AcquisitionRequests);
        Assert.Equal(TimeSpan.FromSeconds(3), model.Elapsed);
        Assert.False(model.IsManualStimulusReady);
        Assert.False(model.IsManualFinalAcquisitionReady);
        Assert.False(model.StartStimulationCommand.CanExecute(null));
        // A direct command invocation must also be unable to issue stimulation.
        await model.StartStimulationCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Acquisition" }, service.Calls);
    }

    [Fact]
    public async Task AcquisitionOnlyAutomaticUsesTwoStagesPerCycleWithoutFinalAcquisition()
    {
        var service = new FakeRunService();
        using var model = CreateModel(
            CreateRoute(creationMode: ExperimentCreationMode.AcquisitionOnly),
            service
        );
        model.AcquisitionStage.DurationValue = 2;
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 2;
        Assert.Equal(
            new[]
            {
                ExperimentRunStage.Acquisition,
                ExperimentRunStage.Blanking,
                ExperimentRunStage.Acquisition,
                ExperimentRunStage.Blanking,
            },
            model.TimelineSegments.Select(x => x.Stage)
        );
        Assert.Equal(6, model.TimelineMaximum);

        await model.StartAutomaticExperimentCommand.ExecuteAsync(null);

        Assert.True(model.IsCompleted);
        Assert.Empty(service.AcquisitionRequests);
        Assert.Equal(
            ExperimentCreationMode.AcquisitionOnly,
            service.AutomaticRequest!.CreationMode
        );
        Assert.Equal(TimeSpan.Zero, service.AutomaticRequest.StimulationDuration);
        Assert.Equal(TimeSpan.Zero, service.AutomaticRequest.RecoveryDuration);
        Assert.Equal(0, service.AutomaticRequest.TargetCurrentMilliAmps);
        Assert.Equal(TimeSpan.FromSeconds(6), model.Elapsed);
    }

    [Fact]
    public async Task AcquisitionOnlyInfiniteRunCanBeStoppedWithoutAppendingAcquisition()
    {
        var service = new FakeRunService { BlockAutomatic = true };
        using var model = CreateModel(
            CreateRoute(creationMode: ExperimentCreationMode.AcquisitionOnly),
            service
        );
        model.AcquisitionStage.DurationValue = 2;
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 0;
        var running = model.StartAutomaticExperimentCommand.ExecuteAsync(null);
        Assert.True(model.IsRunning);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;
        Assert.True(model.IsEmergencyStopped);
        Assert.True(service.EmergencyStopCalled);
        Assert.Empty(service.AcquisitionRequests);
        Assert.DoesNotContain(
            model.TimelineSegments,
            x => x.Stage is ExperimentRunStage.Stimulation or ExperimentRunStage.Recovery
        );
    }

    [Fact]
    public async Task AcquisitionOnlyAcquisitionFailureDoesNotContinueToBlanking()
    {
        var service = new FakeRunService
        {
            AcquisitionException = new InvalidOperationException("test failure"),
        };
        using var model = CreateModel(
            CreateRoute(creationMode: ExperimentCreationMode.AcquisitionOnly),
            service
        );
        model.AcquisitionStage.DurationValue = 2;
        await model.StartAcquisitionCommand.ExecuteAsync(null);
        Assert.False(model.IsRunning);
        Assert.False(model.IsCompleted);
        Assert.Equal(new[] { "Acquisition" }, service.Calls);
        Assert.NotEqual(ExperimentStageStatus.Completed, model.BlankingStage.Status);
    }
}
