using System;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed partial class ExperimentRunPageViewModelTests
{
    [Theory]
    [InlineData(
        StimulusKind.TAcs,
        StimulusDirection.Positive,
        ShamWaveformMode.Direct,
        "正向",
        "40 Hz"
    )]
    [InlineData(
        StimulusKind.TAcs,
        StimulusDirection.Bidirectional,
        ShamWaveformMode.Direct,
        "双向",
        "40 Hz"
    )]
    [InlineData(
        StimulusKind.TDcs,
        StimulusDirection.Positive,
        ShamWaveformMode.Direct,
        "正向",
        "缓升降 7 s"
    )]
    [InlineData(
        StimulusKind.TDcs,
        StimulusDirection.Negative,
        ShamWaveformMode.Direct,
        "反向",
        "缓升降 7 s"
    )]
    [InlineData(
        StimulusKind.TPcs,
        StimulusDirection.Bidirectional,
        ShamWaveformMode.Direct,
        "双向",
        "40 Hz / 占空比 79%"
    )]
    [InlineData(
        StimulusKind.TRns,
        StimulusDirection.Bidirectional,
        ShamWaveformMode.Direct,
        "双向",
        null
    )]
    [InlineData(
        StimulusKind.Sham,
        StimulusDirection.Positive,
        ShamWaveformMode.Direct,
        "正向",
        "缓升降 7 s"
    )]
    [InlineData(
        StimulusKind.Sham,
        StimulusDirection.Bidirectional,
        ShamWaveformMode.Alternating,
        "双向",
        "40 Hz"
    )]
    public void RunAndResultsShareApplicableParameters(
        StimulusKind kind,
        StimulusDirection direction,
        ShamWaveformMode sham,
        string directionText,
        string? waveform
    )
    {
        using var model = CreateModel(CreateRoute(kind, direction: direction, shamMode: sham));
        Assert.Same(model.StimulationConfigurationItems, model.ResultStimulationItems);
        Assert.Contains(
            model.StimulationConfigurationItems,
            x => x.Label == "电流方向" && x.Value == directionText
        );
        Assert.Equal(
            waveform,
            model.StimulationConfigurationItems.SingleOrDefault(x => x.Label == "波形参数")?.Value
        );
        Assert.Contains(
            model.StimulationConfigurationItems,
            x => x.Label == "刺激范式" && x.Value == StimulusParameterPolicy.ModeName(kind, sham)
        );
        Assert.Contains(model.StimulationConfigurationItems, x => x.Value.Contains("CH1=Fz"));
        Assert.Contains(model.StimulationConfigurationItems, x => x.Value.Contains("CH2=CP4"));
    }

    [Fact]
    public void MultipleTargetPeakSumDoesNotIncludeReturnCurrents()
    {
        var source = CreateHdRoute();
        var first = source.StimulusConfiguration.Targets[0];
        var second = first with
        {
            TargetId = Guid.NewGuid(),
            DisplayOrder = 2,
            PeakCurrent = 0.6,
            FixedActivePhysicalChannelId = 8,
            Channels =
            [
                new(8, StimulationChannelRole.FixedActive, 0.6),
                new(9, StimulationChannelRole.Selectable, 0.6),
            ],
        };
        var config = source.StimulusConfiguration with
        {
            ArrayMode = StimulusArrayMode.MultiTarget,
            Targets = [first, second],
        };
        using var model = CreateModel(
            new ExperimentRunRouteData(
                source.ExperimentId,
                source.SubjectId,
                config,
                [
                    .. source.StimulusElectrodes,
                    new("C3", second.TargetId, 8, StimulationChannelRole.FixedActive),
                    new("C4", second.TargetId, 9, StimulationChannelRole.Selectable),
                ],
                source.AcquisitionChannels,
                source.ReferenceChannel,
                source.GroundChannel,
                source.SampleRateHz
            )
        );
        Assert.Equal("设定峰值合计", model.StimulusCurrentLabel);
        Assert.Equal("2.60 mA", model.StimulusCurrentText);
        Assert.Contains(
            model.ResultStimulationItems,
            x => x.Label == "靶点2·峰值" && x.Value == "0.60 mA"
        );
        Assert.Contains(model.ResultStimulationItems, x => x.Value.Contains("CH9=C4"));
    }

    [Fact]
    public void EnvelopeSummaryPreservesCarrierRampAndModulationWithoutDuty()
    {
        var source = CreateRoute(StimulusKind.EnvelopeTAcs, frequency: 200);
        var config = source.StimulusConfiguration with { Envelope = new(4, 0.8, 125, true) };
        using var model = CreateModel(
            new ExperimentRunRouteData(
                source.ExperimentId,
                source.SubjectId,
                config,
                source.StimulusElectrodes,
                source.AcquisitionChannels,
                source.ReferenceChannel,
                source.GroundChannel,
                source.SampleRateHz
            )
        );
        Assert.Same(model.StimulationConfigurationItems, model.ResultStimulationItems);
        Assert.Contains(
            model.ResultStimulationItems,
            x => x.Label == "载波频率" && x.Value == "200 Hz"
        );
        Assert.Contains(
            model.ResultStimulationItems,
            x => x.Label == "波形参数" && x.Value.Contains("缓升降 7 s")
        );
        Assert.Contains(
            model.ResultStimulationItems,
            x => x.Label == "包络参数" && x.Value == "调制 4 Hz / 深度 0.8 / 时延 125 ms / 假刺激"
        );
        Assert.DoesNotContain(model.ResultStimulationItems, x => x.Value.Contains("占空比"));
    }

    [Fact]
    public async Task ZeroTelemetryAndStageChangesCannotOverwriteConfiguredMonitorValues()
    {
        var source = CreateRoute(StimulusKind.TAcs);
        var time = DateTimeOffset.UtcNow;
        var snapshot = new PreRunImpedanceSnapshot(
            [new("F3", 3, ImpedanceBand.UpTo10KOhms), new("Cz", 25, ImpedanceBand.UpTo30KOhms)],
            [new("CP4", 2, ImpedanceBand.Normal)],
            time,
            time
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
            preRunImpedance: snapshot
        );
        var service = new FakeRunService { BlockAutomatic = true };
        using var model = CreateModel(route, service);
        var acquisitionText = model.ImpedanceStatusText;
        var stimulationText = model.StimulationImpedanceStatusText;
        Assert.Contains("16.5 kΩ", acquisitionText);
        Assert.Equal("2.00 mA", model.StimulusCurrentText);
        model.SelectModeCommand.Execute(ExperimentRunMode.Automatic);
        SetSeconds(model.Stages[0], 1);
        SetSeconds(model.Stages[2], 15);
        var running = model.StartAutomaticExperimentCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => service.AutomaticRequest is not null);
        var elapsed = 0;
        foreach (
            var stage in new[]
            {
                ExperimentRunStage.Acquisition,
                ExperimentRunStage.Stimulation,
                ExperimentRunStage.Recovery,
            }
        )
        {
            var telemetry = CreateTelemetry(stage, []) with
            {
                ActualCurrentMilliAmps = 0,
                AverageImpedanceKiloOhms = 999,
                TotalElapsed = TimeSpan.FromSeconds(++elapsed),
            };
            service.Publish(telemetry);
            model.FlushPendingTelemetry();
            Assert.Equal("2.00 mA", model.StimulusCurrentText);
            Assert.Equal(acquisitionText, model.ImpedanceStatusText);
            Assert.Equal(stimulationText, model.StimulationImpedanceStatusText);
        }
        Assert.Equal(999, model.AverageImpedanceKiloOhms);
        await model.EmergencyStopCommand.ExecuteAsync(null);
        await running;
        Assert.Equal("2.00 mA", model.StimulusCurrentText);
        Assert.Equal(acquisitionText, model.ImpedanceStatusText);
        Assert.Equal(stimulationText, model.StimulationImpedanceStatusText);
        Assert.Same(snapshot, model.RouteData.PreRunImpedance);
    }
}
