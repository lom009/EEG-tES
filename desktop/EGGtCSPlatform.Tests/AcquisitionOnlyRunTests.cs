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
                ExperimentRunMode.Automatic,
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
        Assert.Equal(ExperimentRunMode.Automatic, model.SelectedMode);
        Assert.Equal(1000, route.ImportedTiming!.BlankingMilliseconds);
    }

    [Fact]
    public async Task AcquisitionOnlyManualCompletesImmediatelyWithoutBlankingOrFinalAcquisition()
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
            new[] { ExperimentRunStage.Acquisition },
            model.Stages.Select(x => x.Stage)
        );
        Assert.Equal("采集", model.AcquisitionStage.Title);
        Assert.Single(model.TimelineSegments);
        Assert.True(model.StartAcquisitionCommand.CanExecute(null));
        await model.StartAcquisitionCommand.ExecuteAsync(null);

        Assert.True(model.IsCompleted);
        Assert.Equal(new[] { "Acquisition" }, service.Calls);
        Assert.Single(service.AcquisitionRequests);
        Assert.Equal(TimeSpan.FromSeconds(2), model.Elapsed);
        Assert.False(model.IsManualStimulusReady);
        Assert.False(model.IsManualFinalAcquisitionReady);
        Assert.False(model.StartStimulationCommand.CanExecute(null));
        // A direct command invocation must also be unable to issue stimulation.
        await model.StartStimulationCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Acquisition" }, service.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task AcquisitionOnlyRejectsAutomaticModeAndDirectCommand(int cycles)
    {
        var service = new FakeRunService();
        using var model = CreateModel(
            CreateRoute(creationMode: ExperimentCreationMode.AcquisitionOnly), service);
        model.AcquisitionStage.DurationValue = 2;
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        Assert.True(model.IsManualMode);
        model.SelectedMode = ExperimentRunMode.Automatic;
        model.CycleCount = cycles;
        Assert.True(model.IsManualMode);
        Assert.Equal(1, model.CycleCount);
        Assert.False(model.IsCycleCountVisible);
        Assert.Single(model.TimelineSegments);
        Assert.Equal(2, model.TimelineMaximum);
        Assert.False(model.StartAutomaticExperimentCommand.CanExecute(null));
        await model.StartAutomaticExperimentCommand.ExecuteAsync(null);
        Assert.Empty(service.Calls);
        Assert.True(model.IsTimingSetup);
    }

    [Fact]
    public async Task AcquisitionOnlyManualRunCanBeStoppedWithoutAppendingStages()
    {
        var service = new FakeRunService { BlockAcquisition = true };
        using var model = CreateModel(
            CreateRoute(creationMode: ExperimentCreationMode.AcquisitionOnly),
            service
        );
        model.AcquisitionStage.DurationValue = 2;
        var running = model.StartAcquisitionCommand.ExecuteAsync(null);
        Assert.True(model.IsRunning);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;
        Assert.True(model.IsEmergencyStopped);
        Assert.True(service.EmergencyStopCalled);
        Assert.Single(service.AcquisitionRequests);
        Assert.False(model.IsCompleted);
        Assert.DoesNotContain(
            model.TimelineSegments,
            x => x.Stage is ExperimentRunStage.Blanking or ExperimentRunStage.Stimulation or ExperimentRunStage.Recovery
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
