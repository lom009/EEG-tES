using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed partial class ExperimentRunPageViewModelTests
{
    [Fact]
    public void WaveformStartHintTracksLifecycleAndNotifiesBindings()
    {
        using var model = CreateModel();
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.True(model.ShowWaveformStartHint);
        model.ExperimentStartedAt = DateTimeOffset.UtcNow;
        Assert.False(model.ShowWaveformStartHint);
        Assert.Contains(nameof(model.ShowWaveformStartHint), changes);
        foreach (
            var state in new[]
            {
                ExperimentRunPageState.Running,
                ExperimentRunPageState.Completed,
                ExperimentRunPageState.EmergencyStopped,
                ExperimentRunPageState.Results,
            }
        )
        {
            model.PageState = state;
            model.ExperimentStartedAt = null;
            Assert.False(model.ShowWaveformStartHint);
        }
        model.PageState = ExperimentRunPageState.TimingSetup;
        Assert.True(model.ShowWaveformStartHint);
        model.IsHistoricalWaveformLoading = true;
        Assert.False(model.ShowWaveformStartHint);
        model.HistoricalWaveformErrorText = "历史波形加载失败";
        Assert.False(model.ShowHistoricalWaveformError);
        model.IsHistoricalWaveformLoading = false;
        Assert.True(model.ShowHistoricalWaveformError);
        Assert.False(model.ShowWaveformStartHint);
        Assert.Contains(nameof(model.ShowHistoricalWaveformError), changes);
        model.HistoricalWaveformErrorText = string.Empty;
        Assert.False(model.ShowHistoricalWaveformError);
        Assert.True(model.ShowWaveformStartHint);
    }

    [Theory]
    [InlineData(ExperimentCreationMode.StimulusOnly, "单刺激模式")]
    [InlineData(ExperimentCreationMode.AcquisitionOnly, "单采集模式")]
    [InlineData(ExperimentCreationMode.AcquisitionAndStimulation, "采集-刺激模式")]
    public void RunHeaderBadgesIncludeExperimentMode(
        ExperimentCreationMode creationMode,
        string expectedModeText
    )
    {
        using var model = CreateModel(CreateRoute(creationMode: creationMode));

        Assert.Equal(
            new[] { "患者ID：SUBJECT-TEST", "实验ID：EXP-TEST", expectedModeText },
            model.HeaderBadges.Select(badge => badge.DisplayText)
        );
    }

    [Fact]
    public void WaveformStrokeThicknessUsesConfiguredDisplayValue()
    {
        using var model = CreateModel(
            displayOptions: new DisplayOptions { WaveformStrokeThickness = 4.25d }
        );

        Assert.Equal(4.25d, model.WaveformStrokeThickness);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(10.1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WaveformStrokeThicknessFallsBackForInvalidDisplayValue(double value)
    {
        using var model = CreateModel(
            displayOptions: new DisplayOptions { WaveformStrokeThickness = value }
        );

        Assert.Equal(2.5d, model.WaveformStrokeThickness);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1500)]
    public void HistoricalAlternatingParametersRemainReadableWithoutNormalization(double frequency)
    {
        var source = CreateRoute(StimulusKind.TAcs, frequency: frequency);
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            source.AcquisitionChannels,
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            historicalResult: new HistoricalExperimentRunContext(
                Guid.NewGuid(),
                "missing.eegraw",
                ExperimentRunStatus.InterruptedByUser,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(20),
                new Dictionary<int, string>(),
                []
            )
        );
        using var model = CreateModel(route);
        Assert.True(model.IsResults);
        Assert.Equal(
            $"{frequency} Hz",
            Assert.Single(model.ResultStimulationItems, x => x.Label == "波形参数").Value
        );
        Assert.Equal(frequency, route.StimulusConfiguration.Frequency);
        Assert.Equal(7, route.StimulusConfiguration.RampSeconds);
        Assert.Equal(79, route.StimulusConfiguration.DutyPercent);
    }

    [Fact]
    public async Task RealSimulationServiceFinishesManualAcquisitionAndEnablesStimulationAfterBlanking()
    {
        await using var runtime = new EGGtCSPlatform.DeviceRuntime.DeviceRuntimeBuilder()
            .UseSimulation()
            .Build();
        await foreach (var candidate in runtime.Discovery.DiscoverAsync(TimeSpan.Zero))
            await runtime.ConnectVerifiedAsync(candidate);
        using var service = new DeviceExperimentRunService(
            runtime.Devices,
            new TestChannelMappingService()
        );
        using var model = CreateModel(service: service, clock: new SystemExperimentRunClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Manual);
        SetSeconds(model.Stages[0], 5);
        SetSeconds(model.Stages[2], 15);
        Assert.False(model.StartStimulationCommand.CanExecute(null));
        await model.StartAcquisitionCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(ExperimentStageStatus.Completed, model.Stages[0].Status);
        Assert.Equal(ExperimentStageStatus.Completed, model.Stages[1].Status);
        Assert.Equal(ExperimentRunStage.Standby, model.CurrentStage);
        Assert.True(model.StartStimulationCommand.CanExecute(null));
        Assert.True(model.HasWaveformData);
    }

    [Fact]
    public async Task HistoricalRouteOpensResultsAndLoadsRegisteredRawPath()
    {
        var source = CreateRoute();
        var runId = Guid.NewGuid();
        var rawPath = Path.Combine(Path.GetTempPath(), $"{runId:N}.eegraw");
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            source.AcquisitionChannels,
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            historicalResult: new HistoricalExperimentRunContext(
                runId,
                rawPath,
                ExperimentRunStatus.InterruptedByUser,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(20),
                new Dictionary<int, string> { [3] = "F3", [25] = "Cz" },
                [new HistoricalExperimentStageInterval(ExperimentRunStage.Acquisition, 1, 0d, 10d)]
            )
        );
        var history = new ControllableHistoryWindowProvider();
        using var model = CreateModel(route, historyWindowProvider: history);

        Assert.True(model.IsResults);
        Assert.True(model.IsHistoricalResult);
        Assert.False(model.ShowWaveformStartHint);
        Assert.Equal("人工急停", model.ResultStatusText);
        Assert.Equal(20d, model.TimelineMaximum);
        Assert.Equal(model.TimelineMaximum, model.WaveformDataAvailableThrough);
        var request = await history.WaitForRequestAsync(1);
        Assert.Equal(rawPath, request.Request.RawFilePath);

        // Moving the historical range to the left edge used to make the navigator
        // mistake the zero-second playback position for a live-following window.
        model.IsFollowingLatest = true;
        Assert.False(model.IsFollowingLatest);

        request.Complete(CreateHistoryResult(request.Request, 12d));
        await WaitUntilAsync(() => model.HasWaveformData);

        history.CachedPreviewValue = 24d;
        model.TimeRangeSeconds = 5;
        await WaitUntilAsync(() =>
            model.WaveformChannels[0].Samples.Any(point => Math.Abs(point.Value - 24d) < 0.000001d)
        );
        Assert.Equal(240, history.LastCachedRequest!.MaximumPointsPerChannel);

        model.TogglePlaybackCommand.Execute(null);
        Assert.True(model.IsPlaybackActive);
        model.TogglePlaybackCommand.Execute(null);
        Assert.False(model.IsPlaybackActive);

        // A successfully loaded empty window must not suggest starting acquisition.
        history.CachedPreviewValue = null;
        model.TimelineViewStart = 15d;
        model.TimelineViewEnd = 20d;
        var emptyRequest = await history.WaitForRequestAsync(2);
        emptyRequest.Complete(CreateHistoryResult(emptyRequest.Request, 0d) with { Channels = [] });
        await WaitUntilAsync(() => !model.IsHistoricalWaveformLoading && !model.HasWaveformData);
        Assert.False(model.ShowWaveformStartHint);
        Assert.False(model.ShowHistoricalWaveformError);
    }

    [Fact]
    public async Task DraggingHistoricalTimelineUsesCachedPreviewAndDefersDetailedLoadUntilRelease()
    {
        var source = CreateRoute();
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            source.AcquisitionChannels,
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            historicalResult: new HistoricalExperimentRunContext(
                Guid.NewGuid(),
                "history.eegraw",
                ExperimentRunStatus.Completed,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(60),
                new Dictionary<int, string> { [3] = "F3" },
                [new HistoricalExperimentStageInterval(ExperimentRunStage.Acquisition, 1, 0d, 60d)]
            )
        );
        var history = new ControllableHistoryWindowProvider();
        using var model = CreateModel(route, historyWindowProvider: history);
        var initial = await history.WaitForRequestAsync(1);
        initial.Complete(CreateHistoryResult(initial.Request, 10d));
        await WaitUntilAsync(() => model.HasWaveformData);

        history.CachedPreviewValue = 24d;
        model.IsTimelineDragging = true;
        model.TimelineViewEnd = 25d;
        model.TimelineViewStart = 15d;
        model.TimelineViewEnd = 35d;
        model.TimelineViewStart = 25d;

        await Task.Delay(180);
        Assert.Equal(1, history.RequestCount);
        Assert.Equal(25d, history.LastCachedRequest!.StartTimeSeconds);
        Assert.Equal(35d, history.LastCachedRequest.EndTimeSeconds);
        Assert.Equal(240, history.LastCachedRequest.MaximumPointsPerChannel);

        model.IsTimelineDragging = false;
        var detailed = await history.WaitForRequestAsync(2);
        Assert.Equal(15d, detailed.Request.StartTimeSeconds);
        Assert.Equal(45d, detailed.Request.EndTimeSeconds);
        Assert.True(detailed.Request.MaximumPointsPerChannel >= 1200);
        await Task.Delay(180);
        Assert.Equal(2, history.RequestCount);
        detailed.Complete(CreateHistoryResult(detailed.Request, 48d));
        await WaitUntilAsync(() =>
            model.WaveformChannels[0].Samples.Any(point => Math.Abs(point.Value - 48d) < 0.000001d)
        );
    }

    [Fact]
    public async Task SlowCachedPreviewAppliesOnlyTheLatestDraggedWindow()
    {
        var source = CreateRoute();
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            source.AcquisitionChannels,
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            historicalResult: new HistoricalExperimentRunContext(
                Guid.NewGuid(),
                "history.eegraw",
                ExperimentRunStatus.Completed,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(60),
                new Dictionary<int, string> { [3] = "F3" },
                [new HistoricalExperimentStageInterval(ExperimentRunStage.Acquisition, 1, 0d, 60d)]
            )
        );
        var history = new ControllableHistoryWindowProvider();
        using var model = CreateModel(route, historyWindowProvider: history);
        var initial = await history.WaitForRequestAsync(1);
        initial.Complete(CreateHistoryResult(initial.Request, 10d));
        await WaitUntilAsync(() => model.HasWaveformData);

        history.BlockCachedPreviews = true;
        model.IsTimelineDragging = true;
        model.TimelineViewEnd = 30d;
        model.TimelineViewStart = 20d;
        var stalePreview = await history.WaitForCachedRequestAsync(1);
        model.TimelineViewEnd = 50d;
        model.TimelineViewStart = 40d;

        stalePreview.Complete(CreateHistoryResult(stalePreview.Request, 111d));
        var latestPreview = await history.WaitForCachedRequestAsync(2);
        Assert.Equal(40d, latestPreview.Request.StartTimeSeconds);
        Assert.Equal(50d, latestPreview.Request.EndTimeSeconds);
        latestPreview.Complete(CreateHistoryResult(latestPreview.Request, 222d));

        await WaitUntilAsync(() =>
            model.WaveformChannels[0].Samples.Any(point => Math.Abs(point.Value - 222d) < 0.000001d)
        );
        Assert.DoesNotContain(
            model.WaveformChannels[0].Samples,
            point => Math.Abs(point.Value - 111d) < 0.000001d
        );
        Assert.Equal(2, history.CachedRequestCount);
        Assert.Equal(1, history.MaximumConcurrentCachedRequests);
    }

    [Fact]
    public void HistoricalLogicalTimelineDoesNotIncludeWallClockFinalizationTail()
    {
        var source = CreateRoute();
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            source.StimulusElectrodes,
            source.AcquisitionChannels,
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            historicalResult: new HistoricalExperimentRunContext(
                Guid.NewGuid(),
                "history.eegraw",
                ExperimentRunStatus.Completed,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(47.694),
                new Dictionary<int, string> { [3] = "F3" },
                [
                    new HistoricalExperimentStageInterval(
                        ExperimentRunStage.Acquisition,
                        1,
                        31d,
                        45d
                    ),
                ],
                LogicalTimelineEndSeconds: 45d
            )
        );

        using var model = CreateModel(route);

        Assert.Equal(47.694d, model.Elapsed.TotalSeconds, 3);
        Assert.Equal(45d, model.TimelineMaximum, 6);
    }

    [Fact]
    public async Task HistoricalResultRerunPreservesConfigurationAndUsesConnectedDevice()
    {
        var source = CreateRoute();
        var runId = Guid.NewGuid();
        var mapping = new Dictionary<int, string> { [3] = "F3", [25] = "Cz" };
        var timing = new ExperimentTimingTemplate(
            ExperimentRunMode.Automatic,
            2_000,
            750,
            15_000,
            1_250,
            4
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
            experimentDatabaseId: 42,
            scheduledAt: DateTimeOffset.UnixEpoch,
            remarks: "rerun-test",
            importedTiming: timing,
            historicalResult: new HistoricalExperimentRunContext(
                runId,
                "C:\\recordings\\history.eegraw",
                ExperimentRunStatus.Completed,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(20),
                mapping,
                []
            ),
            physicalChannelNames: mapping,
            preRunImpedance: new PreRunImpedanceSnapshot(
                [],
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow
            ),
            creationMode: ExperimentCreationMode.AcquisitionOnly
        );
        var router = new FakeRouter();
        var device = new DeviceSelectionContext();
        device.SetConnectedDevice("device-rerun", "Rerun Device");
        using var model = CreateModel(route, router: router, deviceSelection: device);

        Assert.Equal("单采集模式", model.HeaderBadges[2].DisplayText);
        await model.RerunExperimentCommand.ExecuteAsync(null);

        var rerun = Assert.IsType<ExperimentRerunRouteData>(router.RerunRoute).RouteData;
        Assert.Null(rerun.HistoricalResult);
        Assert.Null(rerun.PreRunImpedance);
        Assert.Equal("device-rerun", rerun.DeviceId);
        Assert.Equal(route.ExperimentId, rerun.ExperimentId);
        Assert.Equal(route.SubjectId, rerun.SubjectId);
        Assert.Equal(42, rerun.ExperimentDatabaseId);
        Assert.Equal("rerun-test", rerun.Remarks);
        Assert.Equal(route.AcquisitionChannels, rerun.AcquisitionChannels);
        Assert.Equal(ExperimentRunMode.Automatic, rerun.ImportedTiming!.Mode);
        Assert.Equal(4, rerun.ImportedTiming.CycleCount);
        Assert.Equal(2_000, rerun.ImportedTiming.AcquisitionMilliseconds);
        Assert.Equal(mapping, rerun.PhysicalChannelNames);
        Assert.Equal(ExperimentCreationMode.AcquisitionOnly, rerun.CreationMode);
    }

    [Fact]
    public async Task DisconnectedRerunShowsErrorAndStaysOnResults()
    {
        var router = new FakeRouter();
        var dialogs = new FakeErrorDialogService();
        using var model = CreateModel(
            router: router,
            errorDialogService: dialogs,
            deviceSelection: new DeviceSelectionContext()
        );
        model.ShowResultsCommand.Execute(null);

        await model.RerunExperimentCommand.ExecuteAsync(null);

        Assert.Null(router.RerunRoute);
        Assert.True(model.IsResults);
        Assert.Equal("无法再次运行", dialogs.Title);
        Assert.Contains("连接设备", dialogs.Message);
    }

    [Fact]
    public async Task CurrentResultRerunUsesEditedTimingWithoutStartingDevice()
    {
        var router = new FakeRouter();
        var service = new FakeRunService();
        var device = new DeviceSelectionContext();
        device.SetConnectedDevice("device-current", "Current Device");
        using var model = CreateModel(service: service, router: router, deviceSelection: device);
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 3;
        SetSeconds(model.Stages[0], 6);
        SetSeconds(model.Stages[2], 16);
        model.ShowResultsCommand.Execute(null);

        await model.RerunExperimentCommand.ExecuteAsync(null);

        var rerun = Assert.IsType<ExperimentRerunRouteData>(router.RerunRoute).RouteData;
        Assert.Equal("device-current", rerun.DeviceId);
        Assert.Equal(ExperimentRunMode.Automatic, rerun.ImportedTiming!.Mode);
        Assert.Equal(3, rerun.ImportedTiming.CycleCount);
        Assert.Equal(6_000, rerun.ImportedTiming.AcquisitionMilliseconds);
        Assert.Equal(16_000, rerun.ImportedTiming.StimulationMilliseconds);
        Assert.Empty(service.Calls);
        Assert.Null(service.RecordingMetadata);
    }

    [Fact]
    public void DisplayFilterOptionsAndDefaultsMatchClinicalControls()
    {
        using var model = CreateModel();

        Assert.Equal(
            new[] { "关闭", "0.1 Hz", "0.5 Hz", "1.0 Hz" },
            model.HighPassOptions.Select(option => option.Label)
        );
        Assert.Equal(
            new[] { "关闭", "30 Hz", "70 Hz", "100 Hz" },
            model.LowPassOptions.Select(option => option.Label)
        );
        Assert.Equal(
            new[] { "关闭", "50 Hz", "60 Hz" },
            model.NotchOptions.Select(option => option.Label)
        );
        Assert.Equal(0.5d, model.SelectedHighPassFilter.FrequencyHz);
        Assert.Equal(70d, model.SelectedLowPassFilter.FrequencyHz);
        Assert.Null(model.SelectedNotchFilter.FrequencyHz);
    }

    [Fact]
    public async Task RunUsesOneRecordingIdAndPersistsFilterChangesAndEmergencyStatus()
    {
        var service = new FakeRunService { BlockAutomatic = true };
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        var running = model.StartAutomaticExperimentCommand.ExecuteAsync(null);
        Assert.NotNull(service.RecordingMetadata);
        Assert.Equal("EXP-TEST", service.RecordingMetadata.ExperimentId);
        Assert.Equal(0.5d, service.RecordingMetadata.InitialDisplayFilters.HighPassHz);
        Assert.Equal(service.RecordingMetadata.RecordingId, service.AutomaticRequest!.RecordingId);

        model.SelectedNotchFilter = model.NotchOptions[1];
        Assert.Single(service.FilterChanges);
        Assert.Equal(50d, service.FilterChanges[0].Settings.NotchHz);

        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;

        Assert.Equal(service.RecordingMetadata.RecordingId, service.CompletedRecordingId);
        Assert.Equal(EegRecordingCompletionStatus.EmergencyStopped, service.CompletionStatus);
    }

    [Fact]
    public void EmptyDurationsDisableStartAndExposeValidation()
    {
        using var model = CreateModel();

        Assert.False(model.StartAcquisitionCommand.CanExecute(null));
        Assert.Equal("请输入采集时长", model.Stages[0].ValidationText);
        Assert.Equal("请输入刺激时长", model.Stages[2].ValidationText);
        Assert.True(model.CanGoBack);
    }

    [Fact]
    public void ImportedTimingInitializesTimelineWithoutThrowing()
    {
        var source = CreateRoute();
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
                5_000,
                1_000,
                15_000,
                1_000,
                3
            )
        );

        using var model = CreateModel(route);

        Assert.Equal(ExperimentRunMode.Automatic, model.SelectedMode);
        Assert.Equal(3, model.CycleCount);
        Assert.All(
            model.Stages,
            stage => Assert.Equal(ExperimentDurationUnit.Seconds, stage.SelectedUnit!.Value)
        );
        Assert.Equal(5m, model.Stages[0].DurationValue);
        Assert.Equal(1m, model.Stages[1].DurationValue);
        Assert.Equal(15m, model.Stages[2].DurationValue);
        Assert.Equal(1m, model.Stages[3].DurationValue);
        Assert.Equal(71d, model.TimelineMaximum, 6);
        Assert.Equal(13, model.TimelineSegments.Count);
    }

    [Theory]
    [InlineData(500, ExperimentDurationUnit.Milliseconds, 0.5)]
    [InlineData(2, ExperimentDurationUnit.Seconds, 2)]
    [InlineData(1.5, ExperimentDurationUnit.Minutes, 90)]
    public void DurationUnitsConvertToSeconds(
        double value,
        ExperimentDurationUnit unit,
        double expectedSeconds
    )
    {
        var options = CreateUnits();
        var stage = new ExperimentStageViewModel(
            1,
            ExperimentRunStage.Acquisition,
            "采集",
            options[(int)unit],
            true
        );
        stage.DurationValue = (decimal)value;

        Assert.Equal(expectedSeconds, stage.GetDuration()!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void DurationStepRejectsNonMultipleAndAcceptsConfiguredFraction()
    {
        var units = CreateUnits();
        var wholeSecondStage = new ExperimentStageViewModel(
            1,
            ExperimentRunStage.Acquisition,
            "采集",
            units[1],
            true,
            1000
        );

        wholeSecondStage.DurationValue = 1.5m;
        Assert.Null(wholeSecondStage.GetDuration());
        wholeSecondStage.DurationValue = 2m;
        Assert.Equal(2d, wholeSecondStage.GetDuration()!.Value.TotalSeconds, 6);

        var halfSecondStage = new ExperimentStageViewModel(
            1,
            ExperimentRunStage.Acquisition,
            "采集",
            units[1],
            true,
            500
        )
        {
            DurationValue = 1.5m,
        };
        Assert.Equal(1.5d, halfSecondStage.GetDuration()!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void DurationMaximumRejectsValuesAboveConfiguredMilliseconds()
    {
        var units = CreateUnits();
        var stage = new ExperimentStageViewModel(
            1,
            ExperimentRunStage.Acquisition,
            "采集",
            units[1],
            true,
            1000,
            5000
        );

        stage.DurationValue = 5m;
        Assert.Equal(5d, stage.GetDuration()!.Value.TotalSeconds, 6);

        stage.DurationValue = 6m;
        Assert.Null(stage.GetDuration());
    }

    [Fact]
    public void DurationMinimumRejectsTypedValueAndClampsImportedMilliseconds()
    {
        var units = CreateUnits();
        var stage = new ExperimentStageViewModel(
            1,
            ExperimentRunStage.Acquisition,
            "采集",
            units[1],
            true,
            1000,
            65535000,
            1000
        );

        stage.DurationValue = 0.5m;
        Assert.Null(stage.GetDuration());

        stage.SetDurationMilliseconds(500m);
        Assert.Equal(1000m, stage.DurationMilliseconds);
        Assert.Equal(1m, stage.DurationValue);
    }

    [Theory]
    [InlineData(65535001, ExperimentDurationUnit.Milliseconds, true)]
    [InlineData(65536, ExperimentDurationUnit.Seconds, true)]
    [InlineData(1092.26, ExperimentDurationUnit.Minutes, true)]
    [InlineData(65535, ExperimentDurationUnit.Seconds, false)]
    public void DurationMaximumComparisonUsesTheSelectedUnit(
        double value,
        ExperimentDurationUnit unit,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ExperimentDurationMath.IsAboveMaximum((decimal)value, unit, 65535000)
        );
    }

    [Theory]
    [InlineData(999, ExperimentDurationUnit.Milliseconds, true)]
    [InlineData(0.5, ExperimentDurationUnit.Seconds, true)]
    [InlineData(0.01, ExperimentDurationUnit.Minutes, true)]
    [InlineData(1, ExperimentDurationUnit.Seconds, false)]
    public void DurationMinimumComparisonUsesTheSelectedUnit(
        double value,
        ExperimentDurationUnit unit,
        bool expected
    )
    {
        Assert.Equal(expected, ExperimentDurationMath.IsBelowMinimum((decimal)value, unit, 1000));
    }

    [Fact]
    public void RepeatingMinuteConversionPreservesCanonicalMilliseconds()
    {
        var units = CreateUnits();
        var stage = new ExperimentStageViewModel(
            1,
            ExperimentRunStage.Acquisition,
            "采集",
            units[1],
            true,
            1000
        )
        {
            DurationValue = 1m,
        };

        stage.SelectedUnit = units[2];
        Assert.Equal(0.016667m, stage.DurationValue);
        Assert.Equal(1000m, stage.DurationMilliseconds);

        stage.SelectedUnit = units[1];
        Assert.Equal(1m, stage.DurationValue);
        Assert.Equal(1d, stage.GetDuration()!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void DurationInputSyntaxAcceptsOnlyUnsignedNumericTextAtConfiguredPrecision()
    {
        var separator = System
            .Globalization
            .CultureInfo
            .CurrentCulture
            .NumberFormat
            .NumberDecimalSeparator;

        Assert.True(ExperimentDurationInput.IsNumericText(string.Empty, 1));
        Assert.True(ExperimentDurationInput.IsNumericText($"12{separator}5", 1));
        Assert.False(ExperimentDurationInput.IsNumericText("abc", 1));
        Assert.False(ExperimentDurationInput.IsNumericText("-1", 1));
        Assert.False(ExperimentDurationInput.IsNumericText("1 000", 1));
        Assert.False(ExperimentDurationInput.IsNumericText("1e3", 1));
        Assert.False(ExperimentDurationInput.IsNumericText($"1{separator}5", 0));
        Assert.False(ExperimentDurationInput.IsNumericText($"1{separator}55", 1));
    }

    [Fact]
    public void MissingUnitIsInvalidWithoutThrowing()
    {
        using var model = CreateModel();
        SetSeconds(model.Stages[0], 20);
        SetSeconds(model.Stages[2], 15);

        model.Stages[0].SelectedUnit = null;

        Assert.Null(model.Stages[0].GetDuration());
        Assert.False(model.StartAcquisitionCommand.CanExecute(null));
        Assert.Equal(5d, model.TimelineMaximum, 6);
    }

    [Fact]
    public void DirectCurrentRequiresMoreThanTwiceRampDuration()
    {
        using var model = CreateModel(
            CreateRoute(StimulusKind.TDcs, 7, 40),
            timingOptions: new ExperimentRunTimingOptions
            {
                DurationStepMilliseconds = 100,
                DurationMinimumMilliseconds = 100,
            }
        );
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 14);

        Assert.False(model.StartAcquisitionCommand.CanExecute(null));
        Assert.Contains("大于缓升缓降", model.Stages[2].ValidationText);

        SetSeconds(model.Stages[2], 14.1);
        Assert.True(model.StartAcquisitionCommand.CanExecute(null));
    }

    [Fact]
    public void AlternatingCurrentRequiresOneCompletePeriod()
    {
        using var model = CreateModel(
            CreateRoute(StimulusKind.TAcs, 0, 10),
            timingOptions: new ExperimentRunTimingOptions
            {
                DurationStepMilliseconds = 1,
                DurationMinimumMilliseconds = 1,
            }
        );
        SetSeconds(model.Stages[0], 1);
        SetMilliseconds(model.Stages[2], 99);

        Assert.False(model.StartAcquisitionCommand.CanExecute(null));
        Assert.Contains("完整波形周期", model.Stages[2].ValidationText);

        SetMilliseconds(model.Stages[2], 100);
        Assert.True(model.StartAcquisitionCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(40)]
    public void NoiseAcceptsShortPositiveDurationRegardlessOfHiddenFrequency(double frequency)
    {
        using var model = CreateModel(
            CreateRoute(StimulusKind.TRns, frequency: frequency),
            timingOptions: new ExperimentRunTimingOptions
            {
                DurationStepMilliseconds = 1,
                DurationMinimumMilliseconds = 1,
            }
        );
        SetSeconds(model.Stages[0], 1);
        SetMilliseconds(model.Stages[2], 1);
        Assert.True(model.StartAcquisitionCommand.CanExecute(null));
        Assert.Empty(model.Stages[2].ValidationText);
        Assert.DoesNotContain(model.ResultStimulationItems, x => x.Label == "波形参数");
    }

    [Theory]
    [InlineData(StimulusKind.TAcs, ShamWaveformMode.Direct, "100 Hz")]
    [InlineData(StimulusKind.TDcs, ShamWaveformMode.Direct, "缓升降 7 s")]
    [InlineData(StimulusKind.TPcs, ShamWaveformMode.Direct, "100 Hz / 占空比 79%")]
    [InlineData(StimulusKind.Sham, ShamWaveformMode.Direct, "缓升降 7 s")]
    [InlineData(StimulusKind.Sham, ShamWaveformMode.Alternating, "100 Hz")]
    public void ResultsOnlyShowApplicableWaveformParameters(
        StimulusKind kind,
        ShamWaveformMode sham,
        string expected
    )
    {
        var route = CreateRoute(kind, frequency: 100, shamMode: sham);
        using var model = CreateModel(route);
        Assert.Equal(
            expected,
            Assert.Single(model.ResultStimulationItems, x => x.Label == "波形参数").Value
        );
        Assert.Equal(7, route.StimulusConfiguration.RampSeconds);
        Assert.Equal(79, route.StimulusConfiguration.DutyPercent);
        if (kind == StimulusKind.Sham)
            Assert.Contains(
                sham == ShamWaveformMode.Direct ? "直流" : "交流",
                model.StimulusModeText
            );
    }

    [Fact]
    public void TimelineUsesRealStageDurationsAndFiniteCycleCount()
    {
        using var model = CreateModel();
        SetSeconds(model.Stages[0], 5);
        SetSeconds(model.Stages[2], 15);

        Assert.Equal(27, model.TimelineMaximum, 6);
        Assert.Equal(0, model.TimelineViewStart);
        Assert.Equal(10, model.TimelineViewEnd, 6);
        Assert.Equal(5, model.TimelineSegments.Count);
        Assert.Equal("采集 5s", model.TimelineSegments[0].Label);
        Assert.Equal(string.Empty, model.TimelineSegments[1].Label);
        Assert.Equal(string.Empty, model.TimelineSegments[3].Label);
        Assert.Equal("采集 5s", model.TimelineSegments[4].Label);

        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 3;

        Assert.Equal(71, model.TimelineMaximum, 6);
        Assert.Equal(10, model.TimelineViewEnd - model.TimelineViewStart, 6);
        Assert.Equal(13, model.TimelineSegments.Count);
        Assert.Equal(3, model.TimelineSegments[^1].Cycle);

        model.CycleCount = 0;
        Assert.Equal(22, model.TimelineMaximum, 6);
        Assert.Equal(10, model.TimelineViewEnd - model.TimelineViewStart, 6);
        Assert.Equal(4, model.TimelineSegments.Count);
    }

    [Fact]
    public void UnitSwitchKeepsDurationAndImmediatelyConvertsDisplayValue()
    {
        using var model = CreateModel();
        SetSeconds(model.Stages[0], 20);
        SetSeconds(model.Stages[2], 15);

        Assert.Equal(57, model.TimelineMaximum, 6);
        model.Stages[0].SelectedUnit = model.DurationUnits[2];

        Assert.Equal(0.333333m, model.Stages[0].DurationValue);
        Assert.Equal(20, model.Stages[0].GetDuration()!.Value.TotalSeconds, 6);
        Assert.Equal(57, model.TimelineMaximum, 6);
        Assert.Equal("采集 20s", model.TimelineSegments[0].Label);

        model.Stages[0].SelectedUnit = model.DurationUnits[0];

        Assert.Equal(20000m, model.Stages[0].DurationValue);
        Assert.Equal(20, model.Stages[0].GetDuration()!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void FixedBlankingAndRecoveryDurationsComeFromMillisecondConfiguration()
    {
        using var model = CreateModel(
            timingOptions: new ExperimentRunTimingOptions
            {
                DurationStepMilliseconds = 250,
                DurationMinimumMilliseconds = 250,
                BlankingDurationMilliseconds = 750,
                RecoveryDurationMilliseconds = 1250,
            }
        );

        Assert.Equal(4, model.Stages.Count);
        Assert.Equal(0.75d, model.Stages[1].GetDuration()!.Value.TotalSeconds, 6);
        Assert.Equal(1.25d, model.Stages[3].GetDuration()!.Value.TotalSeconds, 6);
        Assert.False(model.Stages[1].IsEditable);
        Assert.False(model.Stages[3].IsEditable);
    }

    [Fact]
    public void SharedHoverStateKeepsActiveChannelAndClearsAsOneState()
    {
        var hover = new WaveformHoverState();

        hover.Update("CP4", 1.15d);

        Assert.True(hover.IsActive);
        Assert.Equal("CP4", hover.ActiveChannelId);
        Assert.Equal(1.15d, hover.TimeSeconds, 6);
        hover.Clear("Fz");
        Assert.True(hover.IsActive);
        hover.Clear("CP4");
        Assert.False(hover.IsActive);
    }

    [Fact]
    public void ChannelsUseStrictOddPurpleEvenOrangePalette()
    {
        using var model = CreateModel();

        Assert.Equal("#9536F3", model.WaveformChannels[0].Stroke);
        Assert.Equal("#FFFFFF", model.WaveformChannels[0].Background);
        Assert.False(model.WaveformChannels[0].IsEven);
        Assert.Equal("#FD5B38", model.WaveformChannels[1].Stroke);
        Assert.Equal("#F5F8FC", model.WaveformChannels[1].Background);
        Assert.True(model.WaveformChannels[1].IsEven);
    }

    [Fact]
    public void BothWaveformCursorsAreEnabledWhenPageOpens()
    {
        using var model = CreateModel();

        Assert.True(model.ShowXCursor);
        Assert.True(model.ShowYCursor);
    }

    [Fact]
    public void DisplayRangeUsesSupportedStepsAndDefaultsToTwoHundredMicrovolts()
    {
        using var model = CreateModel();

        Assert.Equal(200m, model.DisplayRangeMicrovolts);
        Assert.Equal([20m, 50m, 100m, 200m, 500m, 1000m, 2000m], model.DisplayRangeOptions);
    }

    [Fact]
    public void TimeRangeUsesSupportedSecondsAndDefaultsToTenSeconds()
    {
        using var model = CreateModel();

        Assert.Equal(10, model.TimeRangeSeconds);
        Assert.Equal([5, 10, 30, 60], model.TimeRangeOptions);
        Assert.Equal(
            Math.Min(10d, model.TimelineMaximum),
            model.TimelineViewEnd - model.TimelineViewStart,
            6
        );

        model.TimeRangeSeconds = 60;
        Assert.Equal(model.TimelineMaximum, model.TimelineViewEnd - model.TimelineViewStart, 6);
    }

    [Fact]
    public void TimeRangeFollowsLatestPositionWhenLiveTrackingIsEnabled()
    {
        using var model = CreateModel();
        SetSeconds(model.Stages[0], 20);
        SetSeconds(model.Stages[2], 15);
        model.TimelineLivePosition = 25d;
        model.IsFollowingLatest = true;

        Assert.Equal(25d, model.WaveformDataAvailableThrough);

        model.TimeRangeSeconds = 5;

        Assert.Equal(20d, model.TimelineViewStart, 6);
        Assert.Equal(25d, model.TimelineViewEnd, 6);
    }

    [Fact]
    public void TimeRangePreservesManualReviewCenterAndClampsAtTimelineEdges()
    {
        using var model = CreateModel();
        SetSeconds(model.Stages[0], 20);
        SetSeconds(model.Stages[2], 15);
        model.IsFollowingLatest = false;
        model.TimelineViewStart = 20d;
        model.TimelineViewEnd = 30d;

        model.TimeRangeSeconds = 5;

        Assert.Equal(22.5d, model.TimelineViewStart, 6);
        Assert.Equal(27.5d, model.TimelineViewEnd, 6);

        model.TimelineViewStart = 0d;
        model.TimelineViewEnd = 5d;
        model.TimeRangeSeconds = 30;
        Assert.Equal(0d, model.TimelineViewStart, 6);
        Assert.Equal(30d, model.TimelineViewEnd, 6);

        model.TimeRangeSeconds = 5;
        model.TimelineViewStart = 52d;
        model.TimelineViewEnd = 57d;
        model.TimeRangeSeconds = 30;
        Assert.Equal(27d, model.TimelineViewStart, 6);
        Assert.Equal(57d, model.TimelineViewEnd, 6);
    }

    [Fact]
    public async Task ManualModeRunsFourStagesThenWaitsForUserToStartFinalAcquisition()
    {
        var service = new FakeRunService();
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        Assert.Equal("开始采集", model.PrimaryActionText);
        Assert.True(model.IsPrimaryActionVisible);
        Assert.True(model.IsEmergencyActionVisible);
        Assert.False(model.IsEmergencyActionEnabled);
        Assert.Equal("开始采集", model.FooterPrimaryText);
        Assert.True(model.IsFooterPrimaryEnabled);
        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunPageState.Running, model.PageState);
        Assert.Equal(ExperimentStageStatus.Completed, model.Stages[1].Status);
        Assert.Equal(2, model.TimelineLivePosition, 6);
        Assert.Equal(0, model.TimelineViewStart, 6);
        Assert.Equal(10d, model.TimelineViewEnd, 6);
        Assert.True(model.StartStimulationCommand.CanExecute(null));
        Assert.True(model.IsManualStimulusReady);
        Assert.Equal("开始刺激", model.PrimaryActionText);
        Assert.True(model.IsEmergencyActionVisible);
        Assert.True(model.IsEmergencyActionEnabled);
        Assert.Equal("开始刺激", model.FooterPrimaryText);
        Assert.False(model.CanGoBack);

        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunPageState.Running, model.PageState);
        Assert.True(model.IsManualFinalAcquisitionReady);
        Assert.Equal("待末次采集", model.StageDisplayText);
        Assert.Equal("开始末次采集", model.PrimaryActionText);
        Assert.Equal("开始末次采集", model.FooterPrimaryText);

        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunPageState.Completed, model.PageState);
        Assert.Equal("结束实验并查看结果", model.FooterPrimaryText);
        Assert.True(model.IsCompletionActionVisible);
        Assert.False(model.IsStandardFooterVisible);
        Assert.False(model.IsEmergencyActionEnabled);
        Assert.True(model.CanGoBack);
        Assert.Equal(
            2,
            model.TimelineSegments.Count(segment => segment.Stage == ExperimentRunStage.Acquisition)
        );
        Assert.Equal(["Acquisition", "Stimulation", "Acquisition"], service.Calls);
        Assert.Equal(2, service.AcquisitionRequests.Count);
        Assert.Equal(TimeSpan.Zero, service.AcquisitionRequests[0].TimelineOffset);
        Assert.Equal(TimeSpan.FromSeconds(18), service.AcquisitionRequests[1].TimelineOffset);
        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);
        Assert.Equal(ExperimentRunPageState.Results, model.PageState);
        Assert.True(model.CanGoBack);
    }

    [Fact]
    public async Task ManualCompletedAcquisitionCannotBeRestoredToRunningByLateTelemetry()
    {
        var service = new FakeRunService();
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        SetSeconds(model.Stages[0], 15);
        SetSeconds(model.Stages[2], 15);

        await model.ExecutePrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunStage.Standby, model.CurrentStage);
        Assert.Equal(ExperimentStageStatus.Completed, model.Stages[0].Status);
        Assert.Equal(ExperimentStageStatus.Completed, model.Stages[1].Status);

        service.Publish(
            new ExperimentRunTelemetry(
                ExperimentRunStage.Acquisition,
                TimeSpan.FromSeconds(14.9),
                TimeSpan.FromSeconds(14.9),
                0.99d,
                1,
                1,
                0d,
                3.9d,
                true,
                [],
                []
            )
        );

        Assert.Equal(ExperimentRunStage.Standby, model.CurrentStage);
        Assert.Equal(ExperimentStageStatus.Completed, model.Stages[0].Status);
        Assert.Equal(ExperimentStageStatus.Completed, model.Stages[1].Status);
    }

    [Fact]
    public async Task CompletedRunBuildsDesignResultSectionsFromRouteData()
    {
        using var model = CreateModel(clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        await model.StartAutomaticExperimentCommand.ExecuteAsync(null);

        Assert.NotNull(model.ExperimentStartedAt);
        Assert.Contains(
            model.ResultOverviewItems,
            item => item.Label == "实验ID" && item.Value == "EXP-TEST"
        );
        Assert.Contains(
            model.ResultOverviewItems,
            item => item.Label == "被试ID" && item.Value == "SUBJECT-TEST"
        );
        Assert.Contains(
            model.ResultAcquisitionItems,
            item => item.Label == "REF / GND" && item.Value == "FCz/AFz"
        );
        Assert.Contains(
            model.ResultAcquisitionItems,
            item => item.Label == "点位" && item.Value == "F3, Cz"
        );
        Assert.Contains(
            model.ResultAcquisitionItems,
            item => item.Label == "采样率" && item.Value == "500 Hz"
        );
        Assert.Contains(
            model.ResultStimulationItems,
            item => item.Label == "设定峰值电流" && item.Value == "2.00 mA"
        );
        Assert.Contains(
            model.ResultStimulationItems,
            item =>
                item.Label == "固定刺激通道"
                && item.Value.Contains("CH1=Fz(阴极", StringComparison.Ordinal)
        );
        Assert.Contains(
            model.ResultStimulationItems,
            item =>
                item.Label == "自选刺激通道"
                && item.Value.Contains("CH2=CP4(阳极", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(StimulusArrayMode.DualChannel)]
    [InlineData(StimulusArrayMode.MultiTarget)]
    public void EveryArrayModeShowsRealStimulationPhysicalChannels(StimulusArrayMode mode)
    {
        using var model = CreateModel(CreateRoute(arrayMode: mode));

        Assert.Contains(
            model.ResultStimulationItems,
            item => item.Value.Contains("CH", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void HdResultSummaryShowsFixedAndSelectablePhysicalChannelsInOrder()
    {
        using var model = CreateModel(CreateHdRoute());

        Assert.Contains(
            model.ResultStimulationItems,
            item =>
                item.Label == "固定刺激通道"
                && item.Value.Contains("CH1=Fz(阴极, 2 mA)", StringComparison.Ordinal)
        );
        Assert.Contains(
            model.ResultStimulationItems,
            item =>
                item.Label == "自选刺激通道"
                && item.Value
                    == "CH2=CP4(阳极, 0.5 mA), CH3=T7(阳极, 0.5 mA), CH4=T8(阳极, 0.5 mA), CH5=P3(阳极, 0.5 mA)"
        );
    }

    [Theory]
    [InlineData(StimulusKind.TDcs, ShamWaveformMode.Direct, true)]
    [InlineData(StimulusKind.TAcs, ShamWaveformMode.Direct, false)]
    [InlineData(StimulusKind.TRns, ShamWaveformMode.Direct, false)]
    [InlineData(StimulusKind.TPcs, ShamWaveformMode.Direct, false)]
    [InlineData(StimulusKind.Sham, ShamWaveformMode.Direct, true)]
    [InlineData(StimulusKind.Sham, ShamWaveformMode.Alternating, false)]
    public void RampSummaryIsOnlyVisibleForDirectCurrentModes(
        StimulusKind kind,
        ShamWaveformMode shamMode,
        bool expectedVisible
    )
    {
        using var model = CreateModel(CreateRoute(kind: kind, shamMode: shamMode));

        Assert.Equal(expectedVisible, model.IsStimulusRampVisible);
        Assert.Equal(
            expectedVisible,
            model.StimulusSummary.Contains(model.StimulusRampText, StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(StimulusArrayMode.DualChannel, "双通道")]
    [InlineData(StimulusArrayMode.Hd, "HD")]
    [InlineData(StimulusArrayMode.MultiTarget, "多靶点")]
    public void ResultSummaryUsesConfiguredArrayMode(StimulusArrayMode mode, string expected)
    {
        using var model = CreateModel(CreateRoute(arrayMode: mode));

        Assert.Contains(
            model.ResultStimulationItems,
            item => item.Label == "阵列" && item.Value == expected
        );
    }

    [Fact]
    public void ResultSummaryUsesExecutedElectrodeAssignmentsWhenSnapshotSitesAreEmpty()
    {
        using var model = CreateModel(ExperimentRunRouteDataDefaults.Create());

        Assert.Contains(
            model.ResultStimulationItems,
            item => item.Label == "固定刺激通道" && item.Value.Contains("Fz")
        );
        Assert.Contains(
            model.ResultStimulationItems,
            item => item.Label == "自选刺激通道" && item.Value.Contains("CP4")
        );
    }

    [Theory]
    [InlineData(StimulusDirection.Positive, "阴极", "阳极")]
    [InlineData(StimulusDirection.Negative, "阳极", "阴极")]
    [InlineData(StimulusDirection.Bidirectional, "固定刺激电极", "自选刺激电极")]
    public void ResultSummaryUsesResolvedPolarity(
        StimulusDirection direction,
        string fixedRole,
        string selectableRole
    )
    {
        using var model = CreateModel(CreateRoute(direction: direction));

        Assert.Contains(
            model.ResultStimulationItems,
            item =>
                item.Label == "固定刺激通道"
                && item.Value.Contains(fixedRole, StringComparison.Ordinal)
        );
        Assert.Contains(
            model.ResultStimulationItems,
            item =>
                item.Label == "自选刺激通道"
                && item.Value.Contains(selectableRole, StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ViewModelRejectsWaveformBatchesOutsideAcquisition()
    {
        var service = new FakeRunService { BlockAutomatic = true };
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);
        var running = model.ExecutePrimaryActionCommand.ExecuteAsync(null);

        var batch = new WaveformChannelBatch("F3", 15d, 0.002d, [1d, 2d, 3d]);
        service.Publish(CreateTelemetry(ExperimentRunStage.Stimulation, [batch]));
        Assert.False(model.HasWaveformData);

        service.Publish(
            CreateTelemetry(ExperimentRunStage.Acquisition, [batch with { StartTimeSeconds = 0d }]),
            "another-device"
        );
        Assert.False(model.HasWaveformData);

        service.Publish(
            CreateTelemetry(ExperimentRunStage.Acquisition, [batch with { StartTimeSeconds = 0d }])
        );
        Assert.True(model.HasWaveformData);
        model.WaveformChannels[0].RefreshVisible(0d, model.TimelineMaximum);
        Assert.NotEmpty(model.WaveformChannels[0].Samples);

        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;
    }

    [Fact]
    public async Task WaveformOnlyTelemetryDoesNotRegressCurrentStage()
    {
        var service = new FakeRunService { BlockAutomatic = true };
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);
        var running = model.StartAutomaticExperimentCommand.ExecuteAsync(null);
        service.Publish(CreateTelemetry(ExperimentRunStage.Stimulation, []));
        var elapsed = model.Elapsed;

        service.Publish(
            CreateTelemetry(
                ExperimentRunStage.Acquisition,
                [new WaveformChannelBatch("F3", 0d, 0.002d, [1d, 2d])]
            ) with
            {
                IsWaveformOnly = true,
            }
        );

        Assert.Equal(ExperimentRunStage.Stimulation, model.CurrentStage);
        Assert.Equal(elapsed, model.Elapsed);
        Assert.True(model.HasWaveformData);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;
    }

    [Fact]
    public void TelemetryBufferPreservesEveryWaveformBatchAndCoalescesMonotonicState()
    {
        var buffer = new ExperimentRunTelemetryBuffer();
        const int packetCount = 625;
        const int channelCount = 32;
        for (var packet = 0; packet < packetCount; packet++)
        {
            var batches = Enumerable
                .Range(1, channelCount)
                .Select(channel => new WaveformChannelBatch(
                    $"CH{channel}",
                    packet * 8d / 500d,
                    1d / 500d,
                    Enumerable.Range(0, 8).Select(sample => (double)sample).ToArray()
                ))
                .ToArray();
            buffer.Enqueue(
                CreateTelemetry(ExperimentRunStage.Acquisition, batches) with
                {
                    TotalElapsed = TimeSpan.FromSeconds(packet * 8d / 500d),
                    StageElapsed = TimeSpan.FromSeconds(packet * 8d / 500d),
                    StageProgress = packet / (double)packetCount,
                    IsWaveformOnly = true,
                }
            );
        }

        buffer.Enqueue(
            CreateTelemetry(ExperimentRunStage.Acquisition, []) with
            {
                TotalElapsed = TimeSpan.FromSeconds(1),
                StageElapsed = TimeSpan.FromSeconds(1),
            }
        );
        buffer.Enqueue(
            CreateTelemetry(ExperimentRunStage.Acquisition, []) with
            {
                TotalElapsed = TimeSpan.FromSeconds(2),
                StageElapsed = TimeSpan.FromSeconds(2),
            }
        );
        buffer.Enqueue(
            CreateTelemetry(ExperimentRunStage.Acquisition, []) with
            {
                TotalElapsed = TimeSpan.FromSeconds(1.5),
                StageElapsed = TimeSpan.FromSeconds(1.5),
            }
        );
        buffer.Enqueue(
            CreateTelemetry(ExperimentRunStage.Stimulation, []) with
            {
                TotalElapsed = TimeSpan.FromSeconds(3),
                StageElapsed = TimeSpan.FromSeconds(1),
            }
        );

        var drain = buffer.Drain();

        Assert.Equal(packetCount, drain.WaveformUpdates.Count);
        Assert.Equal(
            packetCount * channelCount * 8,
            drain
                .WaveformUpdates.SelectMany(telemetry => telemetry.WaveformBatches)
                .Sum(batch => batch.Samples.Count)
        );
        Assert.Equal(2, drain.StateUpdates.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), drain.StateUpdates[0].TotalElapsed);
        Assert.Equal(ExperimentRunStage.Stimulation, drain.StateUpdates[1].Stage);
        Assert.True(buffer.Drain().IsEmpty);

        buffer.Enqueue(
            CreateTelemetry(ExperimentRunStage.Acquisition, []) with
            {
                TotalElapsed = TimeSpan.FromSeconds(2.5),
                StageElapsed = TimeSpan.FromSeconds(2.5),
            }
        );
        Assert.Empty(buffer.Drain().StateUpdates);
    }

    [Fact]
    public async Task AutomaticModeCompletesFiniteCyclesAndLocksMode()
    {
        var service = new FakeRunService();
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 3;
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        await model.StartAutomaticExperimentCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunPageState.Completed, model.PageState);
        Assert.Equal(["Automatic", "Acquisition"], service.Calls);
        var finalAcquisition = Assert.Single(service.AcquisitionRequests);
        Assert.Equal(TimeSpan.FromSeconds(1), finalAcquisition.Duration);
        Assert.Equal(TimeSpan.FromSeconds(54), finalAcquisition.TimelineOffset);
        Assert.Equal(3, model.CurrentCycle);
        Assert.True(model.IsModeLocked);
        model.SelectModeCommand.Execute(ExperimentRunMode.Manual);
        Assert.Equal(ExperimentRunMode.Automatic, model.SelectedMode);
    }

    [Fact]
    public async Task InfiniteAutomaticRunCanBeEmergencyStopped()
    {
        var service = new FakeRunService { BlockAutomatic = true };
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 0;
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        var running = model.StartAutomaticExperimentCommand.ExecuteAsync(null);
        Assert.Equal(ExperimentRunPageState.Running, model.PageState);

        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;

        Assert.Equal(ExperimentRunPageState.EmergencyStopped, model.PageState);
        Assert.Equal(ExperimentRunStage.Stopped, model.CurrentStage);
        Assert.True(model.CanGoBack);
        Assert.True(model.IsEmergencyActionVisible);
        Assert.False(model.IsEmergencyActionEnabled);
        Assert.Equal("保存当前数据", model.FooterPrimaryText);
        Assert.True(model.IsFooterPrimaryEnabled);
        Assert.True(service.EmergencyStopCalled);
        Assert.Empty(service.AcquisitionRequests);

        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunPageState.Results, model.PageState);
        Assert.True(model.IsResults);
        Assert.Contains("选择需要导出的内容", model.ExportStatusText);
    }

    [Fact]
    public async Task ServiceFailureMovesPageToSafeStoppedState()
    {
        var service = new FakeRunService
        {
            AutomaticException = new InvalidOperationException("模拟通信失败"),
        };
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        await model.StartAutomaticExperimentCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunPageState.EmergencyStopped, model.PageState);
        Assert.Single(model.Exceptions);
        Assert.Contains("模拟通信失败", model.Exceptions[0].Message);
        Assert.Equal("1 项", model.ExceptionSummaryText);
        Assert.True(model.CanGoBack);
        Assert.Empty(service.AcquisitionRequests);
    }

    [Fact]
    public async Task AcquisitionFailureUsesSpecificErrorDialog()
    {
        var presenter = new FakeErrorDialogService();
        var service = new FakeRunService
        {
            AcquisitionException = new EegAcquisitionException(
                EegAcquisitionFailureKind.DataPacketTimeout,
                "连续 2.5 秒未收到有效 EEG 数据包。",
                TimeSpan.FromMilliseconds(2500),
                stopCommandRequested: true
            ),
        };
        using var model = CreateModel(
            service: service,
            clock: new ImmediateClock(),
            errorDialogService: presenter
        );
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        await model.StartAcquisitionCommand.ExecuteAsync(null);

        Assert.Equal("EEG 数据接收超时", presenter.Title);
        Assert.Contains("停止采集", presenter.Message);
        Assert.Equal(ExperimentRunPageState.EmergencyStopped, model.PageState);
    }

    [Fact]
    public async Task StimulationFailureUsesSpecificErrorDialog()
    {
        var presenter = new FakeErrorDialogService();
        var service = new FakeRunService
        {
            StimulationException = new StimulationException(
                StimulationFailureKind.ProgressPacketTimeout,
                "连续 2.5 秒未收到有效刺激状态数据包。",
                TimeSpan.FromMilliseconds(2500),
                stopCommandRequested: true
            ),
        };
        using var model = CreateModel(
            service: service,
            clock: new ImmediateClock(),
            errorDialogService: presenter
        );
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);
        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);

        Assert.Equal("刺激状态接收超时", presenter.Title);
        Assert.Contains("停止刺激", presenter.Message);
        Assert.Equal(ExperimentRunPageState.EmergencyStopped, model.PageState);
    }

    [Fact]
    public async Task FinalAcquisitionFailureMovesPageToSafeStoppedState()
    {
        var service = new FakeRunService
        {
            AcquisitionException = new InvalidOperationException("末次采集失败"),
        };
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 2;
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        await model.StartAutomaticExperimentCommand.ExecuteAsync(null);

        Assert.Equal(ExperimentRunPageState.EmergencyStopped, model.PageState);
        Assert.Single(service.AcquisitionRequests);
        Assert.Contains("末次采集失败", model.Exceptions[0].Message);
    }

    [Fact]
    public async Task StaleRecoveryTelemetryCannotRestoreSpinnerDuringFinalAcquisition()
    {
        var service = new FakeRunService
        {
            PublishStaleRecoveryDuringFinalAcquisition = true,
            BlockFinalAcquisition = true,
        };
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        model.CycleCount = 2;
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        var run = model.StartAutomaticExperimentCommand.ExecuteAsync(null);
        await service.FinalAcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ExperimentStageStatus.Running, model.Stages[0].Status);
        Assert.Equal(ExperimentStageStatus.Completed, model.Stages[3].Status);

        service.ReleaseFinalAcquisition();
        await run;
    }

    [Fact]
    public async Task ManualFinalAcquisitionStartsANewWaveformSegmentAtTimelineOffset()
    {
        var service = new FakeRunService { PublishAcquisitionTelemetry = true };
        using var model = CreateModel(service: service, clock: new ImmediateClock());
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);

        await model.ExecutePrimaryActionCommand.ExecuteAsync(null);
        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);
        await model.ExecuteFooterPrimaryCommand.ExecuteAsync(null);

        var channel = model.WaveformChannels.First(item => item.ChannelId == "F3");
        channel.RefreshVisible(0d, model.TimelineMaximum);
        Assert.Equal(4, channel.Samples.Length);
        Assert.True(channel.Samples[0].StartsNewSegment);
        Assert.Equal(0d, channel.Samples[0].TimeSeconds, 6);
        Assert.True(channel.Samples[2].StartsNewSegment);
        Assert.Equal(18d, channel.Samples[2].TimeSeconds, 6);
    }

    [Fact]
    public void RouteDataCopiesSelectedCollections()
    {
        var channels = new[] { "F3", "Cz" };
        var assignments = new[]
        {
            new StimulusElectrodeAssignment(
                "Fz",
                Guid.NewGuid(),
                1,
                StimulationChannelRole.FixedActive
            ),
        };
        var source = CreateRoute();
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            assignments,
            channels,
            "FCz",
            "AFz",
            500
        );

        channels[0] = "Changed";
        assignments[0] = assignments[0] with { SiteId = "Changed" };

        Assert.Equal(new[] { "F3", "Cz" }, route.AcquisitionChannels);
        Assert.Equal("Fz", route.StimulusElectrodes[0].SiteId);
        Assert.Equal("FCz", route.ReferenceChannel);
        Assert.Equal("AFz", route.GroundChannel);
        Assert.Equal(500, route.SampleRateHz);
    }

    [Fact]
    public void DisposingPageDoesNotDisposeSharedRunService()
    {
        var service = new FakeRunService();
        var model = CreateModel(service: service);

        model.Dispose();

        Assert.False(service.DisposeCalled);
    }

    [Fact]
    public async Task RapidHistoricalMovesOnlyApplyTheNewestWindowResult()
    {
        var service = new FakeRunService { BlockAutomatic = true };
        var history = new ControllableHistoryWindowProvider();
        using var model = CreateModel(
            service: service,
            clock: new ImmediateClock(),
            historyWindowProvider: history,
            channelMappings: new TestChannelMappingService()
        );
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        SetSeconds(model.Stages[0], 20);
        SetSeconds(model.Stages[2], 15);
        var running = model.StartAutomaticExperimentCommand.ExecuteAsync(null);

        model.IsFollowingLatest = false;
        var first = await history.WaitForRequestAsync(1);
        model.TimelineViewEnd = 20d;
        model.TimelineViewStart = 10d;
        var second = await history.WaitForRequestAsync(2);
        second.Complete(CreateHistoryResult(second.Request, 222d));
        await WaitUntilAsync(() =>
            model.WaveformChannels[0].Samples.Any(point => Math.Abs(point.Value - 222d) < 0.000001d)
        );

        first.Complete(CreateHistoryResult(first.Request, 111d));
        await Task.Delay(30);

        Assert.DoesNotContain(model.WaveformChannels[0].Samples, point => point.Value == 111d);
        Assert.Contains(model.WaveformChannels[0].Samples, point => point.Value == 222d);
        Assert.False(model.IsHistoricalWaveformLoading);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;
    }

    [Fact]
    public async Task ShrinkingHistoricalRangeDebouncesAndRequestsHigherDrawingDetail()
    {
        var service = new FakeRunService { BlockAutomatic = true };
        var history = new ControllableHistoryWindowProvider();
        using var model = CreateModel(
            service: service,
            clock: new ImmediateClock(),
            historyWindowProvider: history,
            channelMappings: new TestChannelMappingService()
        );
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        SetSeconds(model.Stages[0], 20);
        SetSeconds(model.Stages[2], 15);
        var running = model.StartAutomaticExperimentCommand.ExecuteAsync(null);

        model.IsFollowingLatest = false;
        var wideRequest = await history.WaitForRequestAsync(1);
        wideRequest.Complete(CreateHistoryResult(wideRequest.Request, 100d));
        await WaitUntilAsync(() =>
            model.WaveformChannels[0].Samples.Any(point => Math.Abs(point.Value - 100d) < 0.000001d)
        );

        model.TimelineViewEnd = 9d;
        model.TimelineViewStart = 1d;
        model.TimelineViewEnd = 7d;
        model.TimelineViewStart = 2d;
        model.TimelineViewEnd = 6d;

        var detailedRequest = await history.WaitForRequestAsync(2);
        await Task.Delay(180);
        Assert.Equal(2, history.RequestCount);
        var wideDensity =
            wideRequest.Request.MaximumPointsPerChannel
            / (wideRequest.Request.EndTimeSeconds - wideRequest.Request.StartTimeSeconds);
        var detailedDensity =
            detailedRequest.Request.MaximumPointsPerChannel
            / (detailedRequest.Request.EndTimeSeconds - detailedRequest.Request.StartTimeSeconds);
        Assert.True(detailedDensity > wideDensity * 2d);

        detailedRequest.Complete(CreateHistoryResult(detailedRequest.Request, 200d));
        await WaitUntilAsync(() =>
            model.WaveformChannels[0].Samples.Any(point => Math.Abs(point.Value - 200d) < 0.000001d)
        );
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;
    }

    private static ExperimentRunPageViewModel CreateModel(
        ExperimentRunRouteData? route = null,
        IExperimentRunService? service = null,
        IExperimentRunClock? clock = null,
        ExperimentRunTimingOptions? timingOptions = null,
        DisplayOptions? displayOptions = null,
        IExperimentRunErrorDialogService? errorDialogService = null,
        IEegHistoryWindowProvider? historyWindowProvider = null,
        IEegPhysicalChannelMappingService? channelMappings = null,
        FakeRouter? router = null,
        IDeviceSelectionContext? deviceSelection = null
    ) =>
        new(
            route ?? CreateRoute(),
            router ?? new FakeRouter(),
            service ?? new FakeRunService(),
            clock ?? new ImmediateClock(),
            timingOptions,
            errorDialogService,
            historyWindowProvider,
            channelMappings,
            deviceSelection: deviceSelection,
            displayOptions: displayOptions
        );

    private static EegHistoryWindowResult CreateHistoryResult(
        EegHistoryWindowRequest request,
        double value
    ) =>
        new(
            request.RecordingId,
            request.StartTimeSeconds,
            request.EndTimeSeconds,
            request.FilterSettings,
            request.DataVersion,
            request
                .ChannelNames.Values.Select(channel => new EegHistoryChannelWindow(
                    channel,
                    [
                        new TimedWaveformPoint(request.StartTimeSeconds, value, true),
                        new TimedWaveformPoint(request.EndTimeSeconds, value, false),
                    ]
                ))
                .ToArray()
        );

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached before the test deadline.");
            await Task.Delay(10);
        }
    }

    private static ExperimentRunRouteData CreateRoute(
        StimulusKind kind = StimulusKind.TDcs,
        double rampSeconds = 7,
        double frequency = 40,
        StimulusArrayMode arrayMode = StimulusArrayMode.DualChannel,
        StimulusDirection direction = StimulusDirection.Positive,
        ShamWaveformMode shamMode = ShamWaveformMode.Direct,
        ExperimentCreationMode creationMode = ExperimentCreationMode.AcquisitionAndStimulation
    )
    {
        var targetId = Guid.NewGuid();
        var target = new ExperimentStimulusTargetSnapshot(
            targetId,
            1,
            2,
            1,
            [
                new ExperimentStimulationChannelSnapshot(1, StimulationChannelRole.FixedActive, 2),
                new ExperimentStimulationChannelSnapshot(2, StimulationChannelRole.Selectable, 2),
            ]
        );
        return new ExperimentRunRouteData(
            "EXP-TEST",
            "SUBJECT-TEST",
            new ExperimentStimulusConfigurationSnapshot(
                kind,
                arrayMode,
                direction,
                shamMode,
                rampSeconds,
                frequency,
                79,
                [target]
            ),
            [
                new StimulusElectrodeAssignment(
                    "Fz",
                    targetId,
                    1,
                    StimulationChannelRole.FixedActive
                ),
                new StimulusElectrodeAssignment(
                    "CP4",
                    targetId,
                    2,
                    StimulationChannelRole.Selectable
                ),
            ],
            ["F3", "Cz"],
            "FCz",
            "AFz",
            500,
            creationMode: creationMode
        );
    }

    private static ExperimentRunRouteData CreateHdRoute()
    {
        var targetId = Guid.NewGuid();
        var target = new ExperimentStimulusTargetSnapshot(
            targetId,
            1,
            2,
            1,
            [
                new ExperimentStimulationChannelSnapshot(1, StimulationChannelRole.FixedActive, 2),
                new ExperimentStimulationChannelSnapshot(4, StimulationChannelRole.Selectable, 0.5),
                new ExperimentStimulationChannelSnapshot(2, StimulationChannelRole.Selectable, 0.5),
                new ExperimentStimulationChannelSnapshot(5, StimulationChannelRole.Selectable, 0.5),
                new ExperimentStimulationChannelSnapshot(3, StimulationChannelRole.Selectable, 0.5),
            ]
        );
        return new ExperimentRunRouteData(
            "EXP-TEST",
            "SUBJECT-TEST",
            new ExperimentStimulusConfigurationSnapshot(
                StimulusKind.TDcs,
                StimulusArrayMode.Hd,
                StimulusDirection.Positive,
                ShamWaveformMode.Direct,
                7,
                40,
                79,
                [target]
            ),
            [
                new StimulusElectrodeAssignment(
                    "Fz",
                    targetId,
                    1,
                    StimulationChannelRole.FixedActive
                ),
                new StimulusElectrodeAssignment(
                    "CP4",
                    targetId,
                    2,
                    StimulationChannelRole.Selectable
                ),
                new StimulusElectrodeAssignment(
                    "T7",
                    targetId,
                    3,
                    StimulationChannelRole.Selectable
                ),
                new StimulusElectrodeAssignment(
                    "T8",
                    targetId,
                    4,
                    StimulationChannelRole.Selectable
                ),
                new StimulusElectrodeAssignment(
                    "P3",
                    targetId,
                    5,
                    StimulationChannelRole.Selectable
                ),
            ],
            ["F3", "Cz"],
            "FCz",
            "AFz",
            500
        );
    }

    private static IReadOnlyList<DurationUnitOption> CreateUnits() =>
        [
            new DurationUnitOption(ExperimentDurationUnit.Milliseconds, "ms"),
            new DurationUnitOption(ExperimentDurationUnit.Seconds, "s"),
            new DurationUnitOption(ExperimentDurationUnit.Minutes, "mins"),
        ];

    private static ExperimentRunTelemetry CreateTelemetry(
        ExperimentRunStage stage,
        IReadOnlyList<WaveformChannelBatch> batches
    ) =>
        new(
            stage,
            TimeSpan.FromSeconds(stage == ExperimentRunStage.Acquisition ? 1d : 17d),
            TimeSpan.FromSeconds(1d),
            0.1d,
            1,
            1,
            stage == ExperimentRunStage.Stimulation ? 1.5d : 0d,
            3.9d,
            true,
            [],
            batches
        );

    private static void SetSeconds(ExperimentStageViewModel stage, double seconds)
    {
        stage.SelectedUnit = CreateUnits()[1];
        stage.DurationValue = (decimal)seconds;
    }

    private static void SetMilliseconds(ExperimentStageViewModel stage, double milliseconds)
    {
        stage.SelectedUnit = CreateUnits()[0];
        stage.DurationValue = (decimal)milliseconds;
    }

    private sealed class ImmediateClock : IExperimentRunClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeErrorDialogService : IExperimentRunErrorDialogService
    {
        public string? Title { get; private set; }

        public string? Message { get; private set; }

        public Task ShowAsync(string title, string message)
        {
            Title = title;
            Message = message;
            return Task.CompletedTask;
        }
    }

    private sealed class ControllableHistoryWindowProvider : IEegHistoryWindowProvider
    {
        private readonly object _gate = new();
        private readonly List<PendingHistoryRequest> _requests = [];
        private readonly List<PendingCachedHistoryRequest> _cachedRequests = [];
        private int _activeCachedRequests;

        public int RequestCount
        {
            get
            {
                lock (_gate)
                    return _requests.Count;
            }
        }

        public double? CachedPreviewValue { get; set; }

        public bool BlockCachedPreviews { get; set; }

        public int CachedRequestCount
        {
            get
            {
                lock (_gate)
                    return _cachedRequests.Count;
            }
        }

        public int MaximumConcurrentCachedRequests { get; private set; }

        public EegHistoryWindowRequest? LastCachedRequest { get; private set; }

        public Task<EegHistoryWindowResult> LoadAsync(
            EegHistoryWindowRequest request,
            CancellationToken cancellationToken = default
        )
        {
            lock (_gate)
            {
                var pending = new PendingHistoryRequest(request);
                _requests.Add(pending);
                return pending.Completion.Task;
            }
        }

        public Task<EegHistoryWindowResult?> LoadCachedAsync(
            EegHistoryWindowRequest request,
            CancellationToken cancellationToken = default
        )
        {
            LastCachedRequest = request;
            if (BlockCachedPreviews)
            {
                lock (_gate)
                {
                    var pending = new PendingCachedHistoryRequest(
                        request,
                        () =>
                        {
                            lock (_gate)
                                _activeCachedRequests--;
                        }
                    );
                    _cachedRequests.Add(pending);
                    _activeCachedRequests++;
                    MaximumConcurrentCachedRequests = Math.Max(
                        MaximumConcurrentCachedRequests,
                        _activeCachedRequests
                    );
                    return pending.Completion.Task;
                }
            }
            return Task.FromResult(
                CachedPreviewValue is { } value ? CreateHistoryResult(request, value) : null
            );
        }

        public void InvalidateRecording(Guid recordingId) { }

        public async Task<PendingHistoryRequest> WaitForRequestAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (true)
            {
                lock (_gate)
                {
                    if (_requests.Count >= count)
                        return _requests[count - 1];
                }
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Historical request was not issued.");
                await Task.Delay(10);
            }
        }

        public async Task<PendingCachedHistoryRequest> WaitForCachedRequestAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (true)
            {
                lock (_gate)
                {
                    if (_cachedRequests.Count >= count)
                        return _cachedRequests[count - 1];
                }
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Cached historical request was not issued.");
                await Task.Delay(10);
            }
        }
    }

    private sealed class PendingHistoryRequest(EegHistoryWindowRequest request)
    {
        public EegHistoryWindowRequest Request { get; } = request;
        public TaskCompletionSource<EegHistoryWindowResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(EegHistoryWindowResult result) => Completion.TrySetResult(result);
    }

    private sealed class PendingCachedHistoryRequest(
        EegHistoryWindowRequest request,
        Action completed
    )
    {
        private int _completed;

        public EegHistoryWindowRequest Request { get; } = request;

        public TaskCompletionSource<EegHistoryWindowResult?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(EegHistoryWindowResult result)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            completed();
            Completion.TrySetResult(result);
        }
    }

    private sealed class TestChannelMappingService : IEegPhysicalChannelMappingService
    {
        public string StoragePath => string.Empty;

        public EegPhysicalChannelMappingSnapshot Load() =>
            new(
                32,
                [new EegPhysicalChannelMapping("F3", 3), new EegPhysicalChannelMapping("Cz", 25)],
                []
            );

        public void Save(
            int physicalChannelCount,
            IReadOnlyList<EegPhysicalChannelMapping> mappings
        ) { }
    }

    private sealed class FakeRunService : IExperimentRunService, IDisposable
    {
        public event EventHandler<ExperimentRunTelemetryEventArgs>? TelemetryReceived;

        public bool BlockAutomatic { get; init; }

        public Exception? AutomaticException { get; init; }

        public Exception? AcquisitionException { get; init; }

        public Exception? StimulationException { get; init; }

        public bool PublishAcquisitionTelemetry { get; init; }

        public bool PublishStaleRecoveryDuringFinalAcquisition { get; init; }

        public bool BlockFinalAcquisition { get; init; }

        public TaskCompletionSource FinalAcquisitionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource FinalAcquisitionRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool EmergencyStopCalled { get; private set; }

        public bool DisposeCalled { get; private set; }

        public List<string> Calls { get; } = [];

        public List<AcquisitionRunRequest> AcquisitionRequests { get; } = [];

        public AutomaticExperimentRunRequest? AutomaticRequest { get; private set; }

        public EegRecordingMetadata? RecordingMetadata { get; private set; }

        public List<EegFilterChangeRecord> FilterChanges { get; } = [];

        public Guid? CompletedRecordingId { get; private set; }

        public EegRecordingCompletionStatus? CompletionStatus { get; private set; }

        public Task StartAcquisitionAsync(
            AcquisitionRunRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add("Acquisition");
            AcquisitionRequests.Add(request);
            if (AcquisitionException is not null)
                throw AcquisitionException;
            if (
                request.TimelineOffset > TimeSpan.Zero
                && PublishStaleRecoveryDuringFinalAcquisition
            )
            {
                Publish(
                    new ExperimentRunTelemetry(
                        ExperimentRunStage.Recovery,
                        request.TimelineOffset,
                        TimeSpan.Zero,
                        0.5d,
                        Math.Max(1, request.CurrentCycle - 1),
                        request.TotalCycles,
                        0d,
                        3.9d,
                        true,
                        [],
                        []
                    )
                );
            }
            if (PublishAcquisitionTelemetry)
            {
                var timelineOffset = request.TimelineOffset;
                Publish(
                    new ExperimentRunTelemetry(
                        ExperimentRunStage.Acquisition,
                        timelineOffset + request.Duration,
                        request.Duration,
                        1d,
                        request.CurrentCycle,
                        request.TotalCycles,
                        0d,
                        3.9d,
                        true,
                        [],
                        [
                            new WaveformChannelBatch(
                                request.ChannelIds[0],
                                timelineOffset.TotalSeconds,
                                1d / request.SampleRateHz,
                                [1d, 2d]
                            ),
                        ]
                    )
                );
            }
            if (request.TimelineOffset <= TimeSpan.Zero || !BlockFinalAcquisition)
                return Task.CompletedTask;
            FinalAcquisitionStarted.TrySetResult();
            return FinalAcquisitionRelease.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseFinalAcquisition() => FinalAcquisitionRelease.TrySetResult();

        public Task StartStimulationAsync(
            StimulationRunRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add("Stimulation");
            if (StimulationException is not null)
                throw StimulationException;
            return Task.CompletedTask;
        }

        public async Task StartAutomaticExperimentAsync(
            AutomaticExperimentRunRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add("Automatic");
            AutomaticRequest = request;
            if (AutomaticException is not null)
                throw AutomaticException;
            if (BlockAutomatic)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task StopCurrentOperationAsync(
            string deviceId,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task EmergencyStopAsync(
            string deviceId,
            CancellationToken cancellationToken = default
        )
        {
            EmergencyStopCalled = true;
            return Task.CompletedTask;
        }

        public Task BeginRecordingAsync(
            EegRecordingMetadata metadata,
            CancellationToken cancellationToken = default
        )
        {
            RecordingMetadata = metadata;
            return Task.CompletedTask;
        }

        public Task RecordDisplayFilterChangeAsync(
            EegFilterChangeRecord change,
            CancellationToken cancellationToken = default
        )
        {
            FilterChanges.Add(change);
            return Task.CompletedTask;
        }

        public Task<EegRecordingCompletionResult>? PendingRecordingCompletion { get; set; }

        public Task<EegRecordingCompletionResult> CompleteRecordingAsync(
            Guid recordingId,
            EegRecordingCompletionStatus status,
            CancellationToken cancellationToken = default
        )
        {
            CompletedRecordingId = recordingId;
            CompletionStatus = status;
            return PendingRecordingCompletion?.WaitAsync(cancellationToken)
                ?? Task.FromResult(new EegRecordingCompletionResult(EegPacketStatistics.Empty()));
        }

        public void Publish(
            ExperimentRunTelemetry telemetry,
            string deviceId = "simulator-default"
        ) =>
            TelemetryReceived?.Invoke(
                this,
                new ExperimentRunTelemetryEventArgs(telemetry) { DeviceId = deviceId }
            );

        public void Dispose() => DisposeCalled = true;
    }

    private sealed class FakeRouter : INavigationRouter
    {
        public ExperimentRerunRouteData? RerunRoute { get; private set; }

        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData routeData) { }

        public void Navigate(StimulusConfigurationRouteData routeData) { }

        public void Navigate(ElectrodeConfigurationRouteData routeData) { }

        public void Navigate(ExperimentRunRouteData routeData) { }

        public void Navigate(ExperimentRerunRouteData routeData) => RerunRoute = routeData;

        public void GoBack() { }

        public void GoHome() { }
    }
}
