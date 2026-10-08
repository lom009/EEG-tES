using System;
using System.Linq;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class StimulusParameterPolicyTests
{
    [Theory]
    [InlineData(StimulusKind.TDcs, ShamWaveformMode.Direct, false, true, false, "缓升降 7 s")]
    [InlineData(StimulusKind.TAcs, ShamWaveformMode.Direct, true, false, false, "100 Hz")]
    [InlineData(StimulusKind.TRns, ShamWaveformMode.Direct, false, false, false, "")]
    [InlineData(
        StimulusKind.TPcs,
        ShamWaveformMode.Direct,
        true,
        false,
        true,
        "100 Hz / 占空比 79%"
    )]
    [InlineData(StimulusKind.Sham, ShamWaveformMode.Direct, false, true, false, "缓升降 7 s")]
    [InlineData(StimulusKind.Sham, ShamWaveformMode.Alternating, true, false, false, "100 Hz")]
    public void EditorAndSummaryUseTheSameApplicableParameters(
        StimulusKind kind,
        ShamWaveformMode sham,
        bool frequency,
        bool ramp,
        bool duty,
        string summary
    )
    {
        var editor = new StimulusModeConfigurationViewModel(kind);
        editor.SelectedShamModeOption = editor.ShamModeOptions.Single(x => Equals(x.Value, sham));
        Assert.Equal(frequency, editor.ShowFrequencyParameter);
        Assert.Equal(ramp, editor.ShowRampParameter);
        Assert.Equal(duty, editor.ShowDutyPercentParameter);
        Assert.Equal(summary, editor.ParameterPolicy.FormatSummary(100, 7, 79));
    }

    [Theory]
    [InlineData(StimulusKind.TAcs, 100.1, 7, 79, "频率")]
    [InlineData(StimulusKind.TPcs, 0.9, 7, 79, "频率")]
    [InlineData(StimulusKind.TPcs, 100.1, 7, 79, "频率")]
    [InlineData(StimulusKind.TPcs, 1500, 7, 79, "频率")]
    [InlineData(StimulusKind.TPcs, 40.05, 7, 79, "频率")]
    [InlineData(StimulusKind.TPcs, 100, 7, 0, "占空比")]
    [InlineData(StimulusKind.TPcs, 100, 7, 100, "占空比")]
    [InlineData(StimulusKind.TPcs, 100, 7, 50.5, "占空比")]
    [InlineData(StimulusKind.TDcs, 40, 30.1, 79, "缓升缓降")]
    [InlineData(StimulusKind.TDcs, 40, 0.5, 79, "缓升缓降")]
    [InlineData(StimulusKind.TAcs, double.NaN, 7, 79, "频率")]
    public void RejectsInvalidEffectiveValues(
        StimulusKind kind,
        double frequency,
        double ramp,
        double duty,
        string field
    ) =>
        Assert.Contains(
            field,
            StimulusParameterPolicy.Validate(kind, ShamWaveformMode.Direct, frequency, ramp, duty)
        );

    [Fact]
    public void HiddenInvalidFieldsHaveASeparateCompatibilityError()
    {
        Assert.Null(
            StimulusParameterPolicy.Validate(StimulusKind.TAcs, ShamWaveformMode.Direct, 100, 60, 0)
        );
        Assert.Contains(
            "设备协议兼容性字段无效",
            StimulusParameterPolicy.Validate(
                StimulusKind.TAcs,
                ShamWaveformMode.Direct,
                100,
                60,
                0,
                true
            )
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(40)]
    [InlineData(double.NaN)]
    public void NoiseDurationDoesNotDependOnHiddenFrequency(double frequency)
    {
        var policy = StimulusParameterPolicy.For(StimulusKind.TRns, ShamWaveformMode.Direct);
        Assert.Null(policy.ValidateDuration(0.001, frequency, 7));
        Assert.NotNull(policy.ValidateDuration(0, frequency, 7));
    }

    [Fact]
    public void DurationBoundariesMatchOrdinaryParadigms()
    {
        foreach (var kind in new[] { StimulusKind.TDcs, StimulusKind.Sham })
        {
            var policy = StimulusParameterPolicy.For(kind, ShamWaveformMode.Direct);
            Assert.NotNull(policy.ValidateDuration(14, 40, 7));
            Assert.Null(policy.ValidateDuration(14.001, 40, 7));
        }
        foreach (var kind in new[] { StimulusKind.TAcs, StimulusKind.TPcs, StimulusKind.Sham })
        {
            var policy = StimulusParameterPolicy.For(kind, ShamWaveformMode.Alternating);
            Assert.NotNull(policy.ValidateDuration(0.099, 10, 7));
            Assert.Null(policy.ValidateDuration(0.1, 10, 7));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(95)]
    [InlineData(99)]
    public void PulseGeometryPreservesDutyAndAlternatingSigns(double duty)
    {
        foreach (
            var direction in new[] { StimulusDirection.Positive, StimulusDirection.Bidirectional }
        )
        {
            var descriptor = new WaveformDescriptor(
                StimulusKind.TPcs,
                StimulusArrayMode.DualChannel,
                direction,
                ShamWaveformMode.Direct,
                2,
                7,
                100,
                duty
            );
            var points = StimulusWaveformControl.GetPulsePoints(descriptor);
            Assert.Equal(20, points.Count);
            for (var cycle = 0; cycle < 4; cycle++)
            {
                var edge = cycle * 5;
                Assert.Equal(duty / 100 / 4, points[edge + 2].X - points[edge + 1].X, 12);
                Assert.Equal(points[edge + 2].X, points[edge + 3].X);
                Assert.Equal(
                    direction == StimulusDirection.Bidirectional && cycle % 2 == 1 ? -2 : 2,
                    points[edge + 1].Y
                );
                Assert.Equal(0, points[edge + 3].Y);
            }
        }
    }

    [Theory]
    [InlineData(StimulusKind.TDcs, StimulationWaveform.TDcs)]
    [InlineData(StimulusKind.TAcs, StimulationWaveform.TAcs)]
    [InlineData(StimulusKind.TRns, StimulationWaveform.TRns)]
    [InlineData(StimulusKind.TPcs, StimulationWaveform.TPcs)]
    [InlineData(StimulusKind.Sham, StimulationWaveform.ShamDirect)]
    public void MappingPreservesExistingWireValues(StimulusKind kind, StimulationWaveform waveform)
    {
        var route = ExperimentRunRouteDataDefaults.Create();
        var source = route.StimulusConfiguration with { Kind = kind, Frequency = 100 };
        var mapped = DeviceStimulationConfigurationMapper.Create(
            new SimulatedEggtCsDevice(),
            source,
            route.StimulusElectrodes
        );
        var expected = new StimulationConfiguration(
            false,
            new[]
            {
                new StimulationTargetGroup(
                    1,
                    new[]
                    {
                        new StimulationChannel(1, 2m, DeviceStimulationChannelRole.FixedActive),
                        new StimulationChannel(2, 2m, DeviceStimulationChannelRole.Selectable),
                    }
                ),
            },
            waveform,
            EGGtCSPlatform.DeviceSdk.StimulationDirection.Positive,
            100m,
            79,
            TimeSpan.FromSeconds(7)
        );
        var profile = new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults());
        Assert.Equal(
            profile
                .Encode(
                    new ConfigureStimulationImpedanceRequest(MeasurementControl.Start, expected),
                    1
                )
                .Payload.ToArray(),
            profile
                .Encode(
                    new ConfigureStimulationImpedanceRequest(MeasurementControl.Start, mapped),
                    1
                )
                .Payload.ToArray()
        );
        Assert.Equal(7, source.RampSeconds);
        Assert.Equal(79, source.DutyPercent);
    }
}
