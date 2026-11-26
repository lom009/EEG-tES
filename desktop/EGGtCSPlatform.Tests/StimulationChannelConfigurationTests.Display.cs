using System.Linq;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed partial class StimulationChannelConfigurationTests
{
    [Theory]
    [InlineData(StimulusKind.EnvelopeTAcs, StimulusArrayMode.DualChannel)]
    [InlineData(StimulusKind.TRns, StimulusArrayMode.DualChannel)]
    [InlineData(StimulusKind.TAcs, StimulusArrayMode.Hd)]
    [InlineData(StimulusKind.TPcs, StimulusArrayMode.Hd)]
    [InlineData(StimulusKind.Sham, StimulusArrayMode.Hd)]
    public void BidirectionalDisplayPreservesChannelAllocation(
        StimulusKind kind, StimulusArrayMode arrayMode)
    {
        var capability = CreateCapability([5]);
        var stimulus = new StimulusModeConfigurationViewModel(kind, capability);
        stimulus.SelectedArrayOption = stimulus.ArrayOptions.Single(x => Equals(x.Value, arrayMode));
        if (kind == StimulusKind.Sham)
            stimulus.SelectedShamModeOption = stimulus.ShamModeOptions.Single(x => Equals(x.Value, ShamWaveformMode.Alternating));
        else if (kind is StimulusKind.TAcs or StimulusKind.TPcs)
            stimulus.SelectedDirectionOption = stimulus.DirectionOptions.Single(x => Equals(x.Value, StimulusDirection.Bidirectional));

        Assert.Equal(StimulusDirection.Bidirectional, stimulus.Direction);
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability,
            creationMode: ExperimentCreationMode.StimulusOnly);
        ConfigureStimulusPointsAndChannels(model);
        Assert.True(model.IsStimulusConfigurationComplete);
        Assert.Equal(arrayMode == StimulusArrayMode.DualChannel ? 2 : 5, model.RequiredStimulusElectrodeCount);

        foreach (var option in model.StimulusSelectionOptions)
            Assert.Equal("~", option.RoleSymbol);

        foreach (var row in model.CurrentImpedanceItems)
        {
            var isFixed = row.Site.StimulationChannelRole == StimulationChannelRole.FixedActive;
            Assert.Equal(isFixed ? "阴极" : "阳极", row.UiRoleText);
            Assert.EndsWith(isFixed ? "·C" : "·A", row.UiLabel);
            Assert.Equal(!isFixed, row.RequiresImpedanceDetection);
            Assert.Equal(isFixed ? 1 : arrayMode == StimulusArrayMode.DualChannel ? 7 : 4,
                row.StimulationChannels.Count);
            if (isFixed)
                Assert.Equal(5, row.Site.StimulationPhysicalChannelId);
            else
                Assert.DoesNotContain(row.StimulationChannels, x => x.PhysicalChannelId == 5);
        }
    }

    [Theory]
    [InlineData(ResolvedElectrodeRole.Anode, true)]
    [InlineData(ResolvedElectrodeRole.Cathode, false)]
    [InlineData(ResolvedElectrodeRole.FixedActive, false)]
    [InlineData(ResolvedElectrodeRole.Stimulating, true)]
    [InlineData(null, false)]
    public void ElectrodeAndImpedanceControlsShareDisplayColors(ResolvedElectrodeRole? role, bool blue)
    {
        var point = new ElectrodePoint { ResolvedStimulationRole = role };
        var item = new ImpedanceStatusItem { ResolvedStimulationRole = role };
        Assert.Equal(blue, point.Classes.Contains(":blue-stimulus"));
        Assert.Equal(blue, item.Classes.Contains(":blue-stimulus"));
        Assert.Equal(role.HasValue && !blue, point.Classes.Contains(":red-stimulus"));
        Assert.Equal(role.HasValue && !blue, item.Classes.Contains(":red-stimulus"));
    }
}
