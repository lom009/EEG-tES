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
    [Theory]
    [InlineData(0, 1.8, "0 ms (0 s)", "1.8 mA")]
    [InlineData(40, 0.04, "40 ms (0.04 s)", "0.04 mA")]
    [InlineData(300, 2, "300 ms (0.3 s)", "2 mA")]
    public async Task ConfirmationUsesConfiguredParametersAndChannelRoles(
        double delay, double current, string expectedDelay, string expectedCurrent)
    {
        var router = new Router();
        var capability = StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions { FixedActivePhysicalChannelIds = [4] });
        var page = new StimulusConfigurationPageViewModel(
            new("EXP", "SUBJECT", CreationMode: ExperimentCreationMode.StimulusOnly),
            router, new DialogService(() => null), new Host(), capability);
        page.CurrentConfiguration.DelayMilliseconds = delay;
        page.CurrentConfiguration.Current = current;
        await page.SaveConfigurationCommand.ExecuteAsync(null);
        using var electrodes = new ElectrodeConfigurationPageViewModel(
            router.Saved!, router, new DialogService(() => null), new Host(), capability);
        var sites = electrodes.Points.Where(p => p.CanStimulate).Reverse().Take(2).ToArray();
        var index = 0;
        foreach (var option in electrodes.StimulusSelectionOptions)
        {
            electrodes.SelectStimulusSelectionOptionCommand.Execute(option);
            electrodes.SelectPointCommand.Execute(sites[index++]);
        }
        foreach (var row in electrodes.CurrentImpedanceItems)
            row.SelectedStimulationChannel = row.StimulationChannels.First();

        var data = Assert.IsType<EnvelopeConfirmationData>(electrodes.CreateEnvelopeConfirmationData());
        Assert.Equal(expectedDelay, data.StartupDelay);
        Assert.Equal(expectedCurrent, data.MaximumCurrent);
        var fixedSite = sites.Single(p => p.StimulationChannelRole == StimulationChannelRole.FixedActive);
        var selectableSite = sites.Single(p => p.StimulationChannelRole == StimulationChannelRole.Selectable);
        Assert.Equal($"{fixedSite.PositionName} / CH4", data.FixedChannel);
        Assert.Equal($"{selectableSite.PositionName} / CH{selectableSite.StimulationPhysicalChannelId}", data.SelectableChannel);

        using var ordinary = new ElectrodeConfigurationPageViewModel(
            ElectrodeConfigurationRouteDataDefaults.Create(), router,
            new DialogService(() => null), new Host(), capability);
        Assert.Null(ordinary.CreateEnvelopeConfirmationData());
    }

    [Fact]
    public void DelayBoundsAndFixedArrayCannotProduceInvalidSetup()
    {
        var configuration = new StimulusModeConfigurationViewModel(StimulusKind.EnvelopeTAcs);
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
        Assert.Equal(40, configuration.DelayMilliseconds);
        Assert.False(configuration.ShowSelectionSection);
        Assert.False(configuration.ShowFrequencyParameter);
        Assert.False(configuration.ShowRampParameter);
        Assert.Equal(StimulusDirection.Bidirectional, configuration.Direction);
        configuration.Current = 1;
        configuration.DelayMilliseconds = 125;
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
