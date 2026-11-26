using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EnvelopeConfigurationTests
{
    internal static ExperimentRunRouteData EnvelopeRoute(
        int fixedChannel = 1,
        int selectableChannel = 5
    )
    {
        var original = ExperimentRunRouteDataDefaults.Create();
        var id = original.StimulusConfiguration.Targets.Single().TargetId;
        var configuration = original.StimulusConfiguration with
        {
            Kind = StimulusKind.EnvelopeTAcs,
            ArrayMode = StimulusArrayMode.DualChannel,
            Direction = StimulusDirection.Bidirectional,
            Envelope = new(DelayMilliseconds: 300, IsDeviceSetup: true),
            Targets =
            [
                new(
                    id,
                    1,
                    2,
                    fixedChannel,
                    [
                        new(fixedChannel, StimulationChannelRole.FixedActive, 2),
                        new(selectableChannel, StimulationChannelRole.Selectable, 2),
                    ]
                ),
            ],
        };
        return new(
            original.ExperimentId,
            original.SubjectId,
            configuration,
            [
                new("Fz", id, fixedChannel, StimulationChannelRole.FixedActive),
                new("CP4", id, selectableChannel, StimulationChannelRole.Selectable),
            ],
            [],
            "",
            "",
            500,
            creationMode: ExperimentCreationMode.StimulusOnly
        );
    }

    [Theory]
    [InlineData(1, 5, 0x10)]
    [InlineData(5, 1, 0x01)]
    [InlineData(1, 8, 0x80)]
    public void EnvelopeMaskSelectsOnlySelectableRole(
        int fixedChannel,
        int selectableChannel,
        byte expected
    )
    {
        var route = EnvelopeRoute(fixedChannel, selectableChannel);
        var settings = DeviceEnvelopeOutput.Settings(route);
        Assert.Equal(expected, settings.ChannelMask);
        Assert.Equal(0, settings.ChannelMask & (1 << (fixedChannel - 1)));
        Assert.Equal(2, route.StimulusElectrodes.Count);
        Assert.Equal(2m, settings.MaximumCurrent);
        Assert.Equal(500, settings.DebugFrequencyHz);
        Assert.Equal(300, settings.DelayMilliseconds);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 5)]
    [InlineData(9, 5)]
    [InlineData(1, 0)]
    [InlineData(1, 9)]
    public void InvalidPhysicalChannelsStillReject(int fixedChannel, int selectableChannel) =>
        Assert.Throws<ArgumentException>(() =>
            DeviceEnvelopeOutput.Settings(EnvelopeRoute(fixedChannel, selectableChannel))
        );

    [Theory]
    [InlineData("missing")]
    [InlineData("role")]
    [InlineData("channel")]
    [InlineData("target")]
    public void IncompleteOrMismatchedAssignmentsStillReject(string invalid)
    {
        var route = EnvelopeRoute();
        var assignments = route.StimulusElectrodes.ToArray();
        if (invalid == "missing")
            assignments = assignments.Take(1).ToArray();
        else
            assignments[1] = invalid switch
            {
                "role" => assignments[1] with { Role = StimulationChannelRole.FixedActive },
                "channel" => assignments[1] with { PhysicalChannelId = 6 },
                _ => assignments[1] with { TargetId = Guid.NewGuid() },
            };
        var invalidRoute = new ExperimentRunRouteData(
            route.ExperimentId,
            route.SubjectId,
            route.StimulusConfiguration,
            assignments,
            [],
            "",
            "",
            500,
            creationMode: ExperimentCreationMode.StimulusOnly
        );
        Assert.Throws<ArgumentException>(() => DeviceEnvelopeOutput.Settings(invalidRoute));
    }

    [Fact]
    public void LegacyEnvelopeWithoutDebugFrequencyKeepsItsOriginalMeaning()
    {
        var legacy = SimulationJson.Read<EnvelopeParameters>(
            "{\"ModulationHz\":7,\"Depth\":0.5,\"DelayMilliseconds\":40}"
        )!;
        Assert.Equal(500, legacy.DebugFrequencyHz);
        Assert.Equal(7, legacy.ModulationHz);
        Assert.Equal(.5, legacy.Depth);
        Assert.False(legacy.IsDeviceSetup);
    }

    [Theory]
    [InlineData(0, 1.8, "0 ms (0 s)", "1.8 mA")]
    [InlineData(40, 0.04, "40 ms (0.04 s)", "0.04 mA")]
    [InlineData(300, 2, "300 ms (0.3 s)", "2 mA")]
    public async Task ConfirmationUsesConfiguredParametersAndChannelRoles(
        double delay,
        double current,
        string expectedDelay,
        string expectedCurrent
    )
    {
        var router = new Router();
        var capability = StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions { FixedActivePhysicalChannelIds = [4] }
        );
        var page = new StimulusConfigurationPageViewModel(
            new("EXP", "SUBJECT", CreationMode: ExperimentCreationMode.StimulusOnly),
            router,
            new DialogService(() => null),
            new Host(),
            capability
        );
        page.CurrentConfiguration.DelayMilliseconds = delay;
        page.CurrentConfiguration.Current = current;
        await page.SaveConfigurationCommand.ExecuteAsync(null);
        using var electrodes = new ElectrodeConfigurationPageViewModel(
            router.Saved!,
            router,
            new DialogService(() => null),
            new Host(),
            capability
        );
        var sites = electrodes.Points.Where(p => p.CanStimulate).Reverse().Take(2).ToArray();
        var index = 0;
        foreach (var option in electrodes.StimulusSelectionOptions)
        {
            electrodes.SelectStimulusSelectionOptionCommand.Execute(option);
            electrodes.SelectPointCommand.Execute(sites[index++]);
        }
        foreach (var row in electrodes.CurrentImpedanceItems)
            row.SelectedStimulationChannel = row.StimulationChannels.First();

        var data = Assert.IsType<EnvelopeConfirmationData>(
            electrodes.CreateEnvelopeConfirmationData()
        );
        Assert.Equal(expectedDelay, data.StartupDelay);
        Assert.Equal(expectedCurrent, data.MaximumCurrent);
        var fixedSite = sites.Single(p =>
            p.StimulationChannelRole == StimulationChannelRole.FixedActive
        );
        var selectableSite = sites.Single(p =>
            p.StimulationChannelRole == StimulationChannelRole.Selectable
        );
        Assert.Equal($"{fixedSite.PositionName} / CH4", data.FixedChannel);
        Assert.Equal(
            $"{selectableSite.PositionName} / CH{selectableSite.StimulationPhysicalChannelId}",
            data.SelectableChannel
        );

        using var ordinary = new ElectrodeConfigurationPageViewModel(
            ElectrodeConfigurationRouteDataDefaults.Create(),
            router,
            new DialogService(() => null),
            new Host(),
            capability
        );
        Assert.Null(ordinary.CreateEnvelopeConfirmationData());
    }

    [Theory]
    [InlineData(ExperimentCreationMode.StimulusOnly, StimulusKind.TDcs, false, true)]
    [InlineData(ExperimentCreationMode.StimulusOnly, StimulusKind.TDcs, true, false)]
    [InlineData(ExperimentCreationMode.StimulusOnly, StimulusKind.EnvelopeTAcs, false, false)]
    [InlineData(ExperimentCreationMode.AcquisitionAndStimulation, StimulusKind.TDcs, false, false)]
    public void PictureNamingChoiceBelongsToOrdinarySingleTdcs(ExperimentCreationMode mode, StimulusKind kind, bool tolerance, bool expected)
    {
        var template = ElectrodeConfigurationRouteDataDefaults.Create();
        using var page = new ElectrodeConfigurationPageViewModel(template with {
            CreationMode = mode, StimulusConfiguration = template.StimulusConfiguration with { Kind = kind },
            Tolerance = tolerance ? new ToleranceTestContext("S-001", "") : null
        }, new Router(), new DialogService(() => null), new Host());
        Assert.Equal(expected, page.CanChoosePictureNaming);
        Assert.False(page.UsePictureNaming);
    }

    [Fact]
    public void DelayBoundsAndFixedArrayCannotProduceInvalidSetup()
    {
        var configuration = new StimulusModeConfigurationViewModel(StimulusKind.EnvelopeTAcs);
        Assert.Equal(0, configuration.DelayMilliseconds);
        Assert.Equal(500, configuration.DebugFrequencyHz);
        configuration.DelayMilliseconds = -1;
        Assert.Equal(0, configuration.DelayMilliseconds);
        configuration.DelayMilliseconds = 301;
        Assert.Equal(300, configuration.DelayMilliseconds);
        configuration.DelayMilliseconds = double.NaN;
        Assert.Equal(300, configuration.DelayMilliseconds);
        configuration.DelayMilliseconds = 40.4;
        Assert.Equal(40, configuration.DelayMilliseconds);
        configuration.SelectedArrayOption = configuration.ArrayOptions.Last();
        Assert.Equal(StimulusArrayMode.DualChannel, configuration.ArrayMode);
    }

    [Theory]
    [InlineData(ExperimentCreationMode.StimulusOnly, true)]
    [InlineData(ExperimentCreationMode.AcquisitionAndStimulation, false)]
    public async Task EntryAndSavePreserveInitialParameters(
        ExperimentCreationMode mode,
        bool enabled
    )
    {
        var router = new Router();
        var page = new StimulusConfigurationPageViewModel(
            new("EXP", "SUBJECT", CreationMode: mode),
            router,
            new DialogService(() => null),
            new Host(),
            StimulusCapabilityProfile.FromOptions(
                new StimulationChannelOptions { FixedActivePhysicalChannelIds = [1] }
            )
        );
        Assert.Equal(enabled, page.Modes.Any(m => m.Kind == StimulusKind.EnvelopeTAcs));
        if (!enabled)
        {
            Assert.Equal(StimulusKind.TDcs, page.CurrentConfiguration.Kind);
            return;
        }
        var configuration = page.CurrentConfiguration;
        Assert.Equal(StimulusKind.EnvelopeTAcs, configuration.Kind);
        Assert.Equal(2, configuration.Current);
        Assert.Equal(0, configuration.DelayMilliseconds);
        Assert.False(configuration.ShowSelectionSection);
        Assert.False(configuration.ShowFrequencyParameter);
        Assert.False(configuration.ShowRampParameter);
        Assert.Equal(StimulusDirection.Bidirectional, configuration.Direction);
        configuration.Current = 1;
        configuration.DelayMilliseconds = 125;
        configuration.DebugFrequencyHz = 800;
        page.Modes[1].SelectCommand.Execute(null);
        page.Modes[0].SelectCommand.Execute(null);
        Assert.Same(configuration, page.CurrentConfiguration);
        await page.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.NotNull(router.Saved);
        var copy = SimulationJson.Read<StimulusConfigurationSnapshot>(
            SimulationJson.Write(router.Saved!.StimulusConfiguration)
        )!;
        Assert.Equal(1, copy.Targets.Single().PeakCurrent);
        Assert.Equal(125, copy.Envelope!.DelayMilliseconds);
        Assert.True(copy.Envelope.IsDeviceSetup);
        Assert.Equal(800, copy.Envelope.DebugFrequencyHz);
        Assert.Equal(StimulusArrayMode.DualChannel, copy.ArrayMode);
    }

    [Theory]
    [InlineData(0.04)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryDesignPointScalesOnlyVertically(double current)
    {
        foreach (var lower in new[] { false, true })
        {
            var baseline = EnvelopeWaveformRenderer.Points(lower, 2);
            var scaled = EnvelopeWaveformRenderer.Points(lower, current);
            Assert.Equal(998, baseline.Length);
            for (var i = 0; i < baseline.Length; i++)
            {
                Assert.Equal(baseline[i].X, scaled[i].X);
                Assert.Equal(baseline[i].Y * current / 2, scaled[i].Y, 12);
                Assert.InRange(scaled[i].Y, -2, 2);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(300)]
    public void DelayUsesLinearAxisWithEquallySpacedTicks(double delay)
    {
        var ticks = EnvelopeWaveformRenderer.TimeTicks(delay);
        Assert.Equal(11, ticks.Length);
        Assert.Equal(11, ticks.Select(EnvelopeWaveformRenderer.FormatTimeTick).Distinct().Count());
        Assert.Equal(delay - 40, ticks[0]);
        Assert.True(ticks[^1] > 0);
        for (var i = 1; i < ticks.Length; i++)
            Assert.Equal(
                EnvelopeWaveformRenderer.TimeWindowMilliseconds / 10,
                ticks[i] - ticks[i - 1],
                10
            );
        var points = EnvelopeWaveformRenderer.Points(false, 2);
        var onset = points.TakeWhile(p => p.Y == 0).Last();
        Assert.Equal(delay, ticks[0] + onset.X * (ticks[^1] - ticks[0]), 10);
        var baselineTicks = EnvelopeWaveformRenderer.TimeTicks(40);
        for (var i = 0; i < ticks.Length; i++)
            Assert.Equal(delay - 40, ticks[i] - baselineTicks[i], 10);
        Assert.Equal(0, points[0].X, 10);
        Assert.InRange(points[^1].X, 0.999, 1.001);
    }

    [Theory]
    [InlineData(-0.0001, "0")]
    [InlineData(0, "0")]
    [InlineData(-40, "-40")]
    [InlineData(599.37456, "599.375")]
    public void TimeLabelsUseMillisecondsWithoutNegativeZero(double value, string expected) =>
        Assert.Equal(expected, EnvelopeWaveformRenderer.FormatTimeTick(value));

    private sealed class Host : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private sealed class Router : INavigationRouter
    {
        public ElectrodeConfigurationRouteData? Saved { get; private set; }

        public void Navigate(ElectrodeConfigurationRouteData route) => Saved = route;

        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData route) { }

        public void Navigate(StimulusConfigurationRouteData route) { }

        public void Navigate(ExperimentRunRouteData route) { }

        public void Navigate(ExperimentRerunRouteData route) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
