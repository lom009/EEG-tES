using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.Bootstrap;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed partial class StimulationChannelConfigurationTests
{
    [Theory]
    [InlineData(ElectrodeRole.Acquisition)]
    [InlineData(ElectrodeRole.Reference)]
    [InlineData(ElectrodeRole.Ground)]
    public async System.Threading.Tasks.Task SharedTemplateRoundTripPreservesBothRolesAndRejectsDisabledSharing(ElectrodeRole role)
    {
        var capability = CreateCapability([1]);
        var configuration = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability).CreateSnapshot();
        using var source = CreateElectrodePage(configuration, capability);
        ConfigureStimulusPointsAndChannels(source);
        var point = source.Points.First(p => p.StimulationChannelRole == StimulationChannelRole.Selectable);
        var template = new ExperimentConfigurationTemplate(configuration, source.CreateStimulusAssignments(),
            role == ElectrodeRole.Acquisition ? [point.Name] : ["Cz"],
            role == ElectrodeRole.Reference ? point.Name : "FCz",
            role == ElectrodeRole.Ground ? point.Name : "AFz", 500,
            new(ExperimentRunMode.Manual, 1000, 100, 60000, 100, 1), "shared test");
        var mapping = new SharedTestMapping(template.AcquisitionChannels);
        var serializer = new ExperimentPackageSerializer(capability, ElectrodePositionCatalog.Default, mapping, new ApplicationVersionProvider());
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shared-{Guid.NewGuid():N}.expp");
        ExperimentConfigurationTemplate restored;
        try
        {
            await serializer.WriteAsync(file, template);
            restored = await serializer.ReadAsync(file);
        }
        finally { System.IO.File.Delete(file); }
        using var imported = CreateElectrodePage(configuration, capability, template: restored);
        Assert.Single(imported.Points.Where(p => p.Role == ElectrodeRole.Reference));
        Assert.Single(imported.Points.Where(p => p.Role == ElectrodeRole.Ground));
        Assert.True(imported.Points.Single(p => p.Name == point.Name).IsShared);
        Assert.Equal(role, imported.Points.Single(p => p.Name == point.Name).Role);
        var error = Assert.Throws<InvalidOperationException>(() => CreateElectrodePage(configuration, capability,
            selectionOptions: new() { AllowSharedStimulationAcquisitionSite = false }, template: restored));
        Assert.Contains("AllowSharedStimulationAcquisitionSite", error.Message);
        using var legacy = CreateElectrodePage(configuration, capability,
            selectionOptions: new() { AllowSharedStimulationAcquisitionSite = false },
            template: restored with { AcquisitionChannels = ["Cz"], ReferenceChannel = "FCz", GroundChannel = "AFz" });
        Assert.DoesNotContain(legacy.Points, p => p.IsShared);
    }

    private sealed class SharedTestMapping(IReadOnlyList<string> sites) : IEegPhysicalChannelMappingService
    {
        public string StoragePath => string.Empty;
        public EegPhysicalChannelMappingSnapshot Load() => new(32,
            sites.Select((site, index) => new EegPhysicalChannelMapping(site, index + 1)).ToArray(), []);
        public void Save(int count, IReadOnlyList<EegPhysicalChannelMapping> mappings) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void SharedSiteConfigurationBinds(string? setting, bool expected)
    {
        var values = new Dictionary<string, string?>();
        if (setting is not null) values["ElectrodeSelection:AllowSharedStimulationAcquisitionSite"] = setting;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(services, configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetRequiredService<IOptions<ElectrodeSelectionOptions>>().Value.AllowSharedStimulationAcquisitionSite);
    }

    [Theory]
    [InlineData(ElectrodeRole.Acquisition, false)]
    [InlineData(ElectrodeRole.Acquisition, true)]
    [InlineData(ElectrodeRole.Reference, false)]
    [InlineData(ElectrodeRole.Reference, true)]
    [InlineData(ElectrodeRole.Ground, false)]
    [InlineData(ElectrodeRole.Ground, true)]
    public void SharedRolesWorkInBothOrdersAndClearOnlyCurrentSide(ElectrodeRole role, bool acquisitionFirst)
    {
        using var model = CreateElectrodePage(new StimulusModeConfigurationViewModel(StimulusKind.TDcs).CreateSnapshot(), StimulusCapabilityProfile.Default);
        var point = model.Points.First(p => p.CanStimulate && p.Role == ElectrodeRole.None);
        void Acquisition()
        {
            model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
            model.SelectPointCommand.Execute(point);
            model.SetSelectedPointRoleCommand.Execute(role);
        }
        void Stimulation()
        {
            model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);
            model.SelectPointCommand.Execute(point);
        }
        if (acquisitionFirst) { Acquisition(); Stimulation(); }
        else { Stimulation(); Acquisition(); }
        Assert.True(point.IsShared);
        Assert.Equal(role, point.Role);
        Assert.True(point.IsAvailable);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        Assert.Null(model.CurrentImpedanceItems.Single(x => x.Site == point).ResolvedStimulationRole);
        model.ClearPointCommand.Execute(point);
        Assert.True(point.IsStimulus);
        Assert.False(point.IsAcquisition);
        Acquisition();
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);
        model.ClearPointCommand.Execute(point);
        Assert.False(point.IsStimulus);
        Assert.Equal(role, point.Role);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledSharingBlocksBothSelectionOrders(bool acquisitionFirst)
    {
        using var model = CreateElectrodePage(new StimulusModeConfigurationViewModel(StimulusKind.TDcs).CreateSnapshot(), StimulusCapabilityProfile.Default,
            selectionOptions: new() { AllowSharedStimulationAcquisitionSite = false });
        var point = model.Points.First(p => p.CanStimulate && p.Role == ElectrodeRole.None);
        model.SelectModeCommand.Execute(acquisitionFirst ? ElectrodeConfigurationMode.Acquisition : ElectrodeConfigurationMode.Stimulus);
        model.SelectPointCommand.Execute(point);
        model.SelectModeCommand.Execute(acquisitionFirst ? ElectrodeConfigurationMode.Stimulus : ElectrodeConfigurationMode.Acquisition);
        Assert.False(point.IsAvailable);
        model.SelectPointCommand.Execute(point);
        Assert.False(point.IsShared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedImpedancesRemainIndependentAcrossDetectionAndClearing(bool acquisitionFirst)
    {
        var capability = CreateCapability([1]);
        using var model = CreateElectrodePage(new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability).CreateSnapshot(), capability);
        ConfigureStimulusPointsAndChannels(model);
        var point = model.Points.First(p => p.StimulationChannelRole == StimulationChannelRole.Selectable);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        model.SelectPointCommand.Execute(point);
        void Stimulus() => model.ApplyImpedanceReadings(ElectrodeConfigurationMode.Stimulus, new Dictionary<string, double> { [point.Name] = 45 });
        void Acquisition() => model.ApplyImpedanceReadings(ElectrodeConfigurationMode.Acquisition, new Dictionary<string, double> { [point.Name] = 8 });
        if (acquisitionFirst) { Acquisition(); Stimulus(); } else { Stimulus(); Acquisition(); }
        Assert.Equal(45d, point.StimulationImpedance);
        Assert.Equal(8d, point.AcquisitionImpedance);
        Assert.Equal(ImpedanceQuality.Excellent, model.CurrentImpedanceItems.Single(x => x.Site == point).Quality);
        Assert.Equal(ImpedanceDetectionState.Failed, model.StimulusDetectionState);
        Assert.Equal(ImpedanceDetectionState.Passed, model.AcquisitionDetectionState);
        var snapshot = model.CreatePreRunImpedanceSnapshot();
        Assert.NotEqual(snapshot.Acquisition.Single(x => x.SiteId == point.Name).Band, snapshot.Stimulation.Single(x => x.SiteId == point.Name).Band);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);
        Assert.Equal(ImpedanceQuality.Bad, model.CurrentImpedanceItems.Single(x => x.Site == point).Quality);
        model.ClearPointCommand.Execute(point);
        Assert.Equal(8d, point.AcquisitionImpedance);
        Assert.Equal(ImpedanceDetectionState.Passed, model.AcquisitionDetectionState);
    }

    [Theory]
    [InlineData(ElectrodeRole.None)]
    [InlineData(ElectrodeRole.Acquisition)]
    [InlineData(ElectrodeRole.Reference)]
    [InlineData(ElectrodeRole.Ground)]
    public void UnsupportedSitesAreDimmedInStimulusModeRegardlessOfAcquisitionRole(ElectrodeRole role)
    {
        using var model = CreateElectrodePage(new StimulusModeConfigurationViewModel(StimulusKind.TDcs).CreateSnapshot(), StimulusCapabilityProfile.Default);
        var point = model.Points.First(p => !p.CanStimulate);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        model.SelectedPoint = point;
        model.SetSelectedPointRoleCommand.Execute(role);
        point.AcquisitionImpedance = 8;
        Assert.False(point.IsDimmed);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);
        Assert.False(point.IsAvailable);
        Assert.True(point.IsDimmed);
        Assert.Equal(role, point.Role);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        Assert.False(point.IsDimmed);
        Assert.True(point.IsAvailable);
        Assert.Equal(8d, point.AcquisitionImpedance);
    }

    [Theory]
    [InlineData(ElectrodeRole.Reference)]
    [InlineData(ElectrodeRole.Ground)]
    public void NoTestRegionsUseExcellentDisplayWithoutSyntheticReadings(ElectrodeRole role)
    {
        var site = new ElectrodeSiteViewModel("CP10", 0, 0, true, role);
        Assert.Equal(ImpedanceQuality.Unknown, site.DisplayQuality);
        site.SetDetectionResultAvailable(ElectrodeConfigurationMode.Acquisition, true);
        Assert.Equal(ImpedanceQuality.Excellent, site.DisplayQuality);
        Assert.Equal(ImpedanceQuality.Unknown, site.Quality);
        Assert.Equal("#38A169", site.AcquisitionColor);
        Assert.Equal("#FFFFFF", site.AcquisitionForeground);
        Assert.Equal("无需检测", site.DetectionDescription(ElectrodeConfigurationMode.Acquisition));
        site.AssignStimulusPosition(Guid.NewGuid(), 1, 1, StimulationChannelRole.Selectable, StimulusDirection.Positive);
        Assert.Equal(ImpedanceQuality.Unknown, site.StimulationDisplayQuality);
        Assert.True(site.ShowSharedDivider);
        site.StimulationImpedance = 45;
        Assert.Equal(ImpedanceQuality.Bad, site.StimulationDisplayQuality);
        Assert.Equal(ImpedanceQuality.Excellent, site.AcquisitionDisplayQuality);
        Assert.Null(site.AcquisitionImpedance);
        Assert.False(site.ShowSharedDivider);
        using var row = new ElectrodeImpedanceItemViewModel(site, [], null, false, null, null);
        Assert.Equal("无需检测", row.QualityText);
        Assert.Equal("-", row.ImpedanceText);
        Assert.Equal(ImpedanceQuality.Unknown, row.Quality);
    }

    [Fact]
    public void NoTestColorsWaitForTheirOwnModeDetectionAndResetOnEdit()
    {
        var capability = CreateCapability([1]);
        using var model = CreateElectrodePage(new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability).CreateSnapshot(), capability);
        ConfigureStimulusPointsAndChannels(model);
        var fixedPoint = model.Points.First(p => p.StimulationChannelRole == StimulationChannelRole.FixedActive);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        model.SelectPointCommand.Execute(fixedPoint);
        model.SetSelectedPointRoleCommand.Execute(ElectrodeRole.Reference);
        Assert.Equal(ImpedanceQuality.Unknown, fixedPoint.StimulationDisplayQuality);
        Assert.Equal(ImpedanceQuality.Unknown, fixedPoint.AcquisitionDisplayQuality);
        model.ApplyImpedanceReadings(ElectrodeConfigurationMode.Stimulus, new Dictionary<string, double>());
        Assert.Equal(ImpedanceQuality.Excellent, fixedPoint.StimulationDisplayQuality);
        Assert.Equal(ImpedanceQuality.Unknown, fixedPoint.AcquisitionDisplayQuality);
        model.ApplyImpedanceReadings(ElectrodeConfigurationMode.Acquisition, new Dictionary<string, double>());
        Assert.Equal(ImpedanceQuality.Excellent, fixedPoint.AcquisitionDisplayQuality);
        Assert.Null(fixedPoint.StimulationImpedance);
        Assert.Null(fixedPoint.AcquisitionImpedance);
        model.SetSelectedPointRoleCommand.Execute(ElectrodeRole.Ground);
        Assert.Equal(ImpedanceQuality.Unknown, fixedPoint.AcquisitionDisplayQuality);
        Assert.Equal(ImpedanceQuality.Excellent, fixedPoint.StimulationDisplayQuality);
    }

    [Fact]
    public void FixedStimulusDisplayDoesNotChangeAcquisitionOrActualQuality()
    {
        var site = new ElectrodeSiteViewModel("F3", 0, 0, true, ElectrodeRole.Acquisition);
        var changed = new List<string?>();
        site.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        site.AssignStimulusPosition(Guid.NewGuid(), 1, 1, StimulationChannelRole.FixedActive, StimulusDirection.Positive);
        Assert.Contains(nameof(site.DisplayQuality), changed);
        Assert.Equal(ImpedanceQuality.Unknown, site.DisplayQuality);
        site.SetDetectionResultAvailable(ElectrodeConfigurationMode.Stimulus, true);
        Assert.Equal(ImpedanceQuality.Excellent, site.DisplayQuality);
        Assert.Equal("#38A169", site.StimulationColor);
        Assert.Equal("#FFFFFF", site.StimulationForeground);
        Assert.Equal(ImpedanceQuality.Unknown, site.AcquisitionDisplayQuality);
        Assert.Null(site.StimulationImpedance);
        Assert.Equal(ImpedanceQuality.Unknown, site.Quality);
        Assert.True(site.ShowSharedDivider);
        site.AcquisitionImpedance = 45;
        Assert.Equal(ImpedanceQuality.Bad, site.AcquisitionDisplayQuality);
        Assert.Equal(ImpedanceQuality.Excellent, site.StimulationDisplayQuality);
        Assert.Equal("无需检测", site.DetectionDescription(ElectrodeConfigurationMode.Stimulus));
        site.ClearStimulusAssignment();
        Assert.Equal(ImpedanceQuality.Bad, site.DisplayQuality);
    }

    [Fact]
    public void MeasuredRegionsUseWhiteTextAndUnmeasuredRegionsKeepRoleColor()
    {
        foreach (var quality in Enum.GetValues<ImpedanceQuality>())
            Assert.Equal(quality == ImpedanceQuality.Unknown ? "#3941B6" : "#FFFFFF",
                ElectrodeSiteViewModel.ForegroundFor(quality));
        var site = new ElectrodeSiteViewModel("CP10", 0, 0, true, ElectrodeRole.Acquisition);
        site.AssignStimulusPosition(Guid.NewGuid(), 1, 1, StimulationChannelRole.Selectable, StimulusDirection.Positive);
        Assert.True(site.ShowSharedDivider);
        site.StimulationImpedance = 28;
        Assert.False(site.ShowSharedDivider);
        Assert.Equal("#FFFFFF", site.StimulationForeground);
        Assert.Equal("#3941B6", site.AcquisitionForeground);
        site.AcquisitionImpedance = 8;
        Assert.Equal("#FFFFFF", site.AcquisitionForeground);
        site.StimulationImpedance = null;
        Assert.False(site.ShowSharedDivider);
        site.AcquisitionImpedance = null;
        Assert.True(site.ShowSharedDivider);
    }
}
