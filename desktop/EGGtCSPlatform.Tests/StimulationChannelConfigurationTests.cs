using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class StimulationChannelConfigurationTests
{
    [Fact]
    public void PointFocusPreservesImpedanceRowsAndChannelSelections()
    {
        var capability = CreateCapability([7]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability);
        var point = model.Points.First(p => p.CanStimulate);
        model.SelectPointCommand.Execute(point);
        var row = model.CurrentImpedanceItems.Single(r => r.Site == point);
        model.SelectedPoint = null;
        model.SelectedPoint = point;
        Assert.Same(row, model.CurrentImpedanceItems.Single(r => r.Site == point));
        Assert.Equal(7, row.SelectedStimulationChannel?.PhysicalChannelId);
        Assert.Contains("CH7", model.SelectedPointRoleText);
    }

    [Fact]
    public void PanelChannelEditFocusesItsHeadPointAndUsesTheSameAssignment()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability);
        var points = model.Points.Where(p => p.CanStimulate).Take(2).ToArray();
        model.SelectPointCommand.Execute(points[0]);
        var selectable = model.StimulusSelectionOptions.Single(o => o.Role == StimulationChannelRole.Selectable);
        model.SelectStimulusSelectionOptionCommand.Execute(selectable);
        model.SelectPointCommand.Execute(points[1]);
        var row = model.CurrentImpedanceItems.Single(r => r.Site == points[0]);
        row.SelectedStimulationChannel = row.StimulationChannels.First();
        Assert.Same(points[0], model.SelectedPoint);
        Assert.True(points[0].IsSelected);
        Assert.False(points[1].IsSelected);
        Assert.Equal(row.SelectedStimulationChannel.PhysicalChannelId, points[0].StimulationPhysicalChannelId);
        Assert.Equal(points[0].StimulationPositionLabel, row.Label);
        Assert.Equal(points[0].RoleText, row.RoleText);
    }

    [Theory]
    [InlineData(false, 8d, true)]
    [InlineData(false, 24d, false)]
    [InlineData(true, 24d, true)]
    public async Task EnvelopeHonorsSharedImpedancePolicyAndUsesProbeOnlyMapping(
        bool allowFailed,
        double impedance,
        bool expected
    )
    {
        var capability = CreateCapability([1]);
        var stimulus = new StimulusModeConfigurationViewModel(
            StimulusKind.EnvelopeTAcs,
            capability
        );
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            impedanceDetectionOptions: new() { AllowConfirmationWhenFailed = allowFailed },
            creationMode: ExperimentCreationMode.StimulusOnly
        );
        ConfigureStimulusPointsAndChannels(model);
        Assert.False(model.CanOpenConfirmation);
        model.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Stimulus,
            model
                .Points.Where(p => p.StimulationChannelRole == StimulationChannelRole.Selectable)
                .ToDictionary(p => p.Name, _ => impedance)
        );
        Assert.Equal(expected, model.CanOpenConfirmation);
        var assignments = model.CreateStimulusAssignments();
        var source = ExperimentStimulusConfigurationSnapshot.From(
            stimulus.CreateSnapshot(),
            assignments
        );
        await using var device = new DeviceSdk.Simulation.SimulatedEggtCsDevice();
        var probe = DeviceStimulationConfigurationMapper.CreateForImpedance(
            device,
            source,
            assignments
        );
        Assert.Equal(StimulationWaveform.TAcs, probe.Waveform);
        Assert.Equal(40m, probe.FrequencyHz);
        Assert.Equal(StimulusKind.EnvelopeTAcs, source.Kind);
        Assert.Throws<InvalidOperationException>(() =>
            DeviceStimulationConfigurationMapper.Create(device, source, assignments)
        );
        model.ClearPointCommand.Execute(model.Points.First(p => p.IsStimulus));
        Assert.False(model.CanOpenConfirmation);
    }

    [Theory]
    [InlineData(false, 8d, true)]
    [InlineData(false, 24d, false)]
    [InlineData(true, 24d, true)]
    public async Task AcquisitionOnlyRequiresEegDetectionAndCannotStartStimulationDetection(
        bool allowFailed,
        double impedance,
        bool expected
    )
    {
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(
            AcquisitionOnlyConfiguration.StimulusPlaceholder,
            CreateCapability([]),
            service,
            new ImpedanceDetectionOptions { AllowConfirmationWhenFailed = allowFailed },
            ExperimentCreationMode.AcquisitionOnly
        );
        Assert.True(model.IsAcquisitionMode);
        Assert.False(model.CanOpenConfirmation);
        Assert.Empty(model.CreateStimulusAssignments());
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);
        Assert.True(model.IsAcquisitionMode);
        var acquisitionPoint = model.Points.First(p => p.Role == ElectrodeRole.None);
        model.SelectPointCommand.Execute(acquisitionPoint);
        model.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Acquisition,
            model
                .Points.Where(p => p.Role == ElectrodeRole.Acquisition)
                .ToDictionary(p => p.Name, _ => impedance)
        );
        Assert.Equal(expected, model.CanOpenConfirmation);
        Assert.Equal(ImpedanceDetectionState.NotStarted, model.StimulusDetectionState);
        model.CurrentMode = ElectrodeConfigurationMode.Stimulus;
        Assert.False(model.ToggleDetectionCommand.CanExecute(null));
        await model.ToggleDetectionCommand.ExecuteAsync(null);
        Assert.Null(service.LastWatchStimulationRequest);
        Assert.Equal(0, service.StimulationStopCount);
    }

    [Theory]
    [InlineData(ExperimentCreationMode.StimulusOnly, "单刺激模式")]
    [InlineData(ExperimentCreationMode.AcquisitionOnly, "单采集模式")]
    [InlineData(ExperimentCreationMode.AcquisitionAndStimulation, "采集-刺激模式")]
    public void ElectrodeHeaderBadgesIncludeExperimentMode(
        ExperimentCreationMode creationMode,
        string expectedModeText
    )
    {
        var capability = CreateCapability([1]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            creationMode: creationMode
        );

        Assert.Equal(
            new[] { "患者ID：SUBJECT-ROLE", "实验ID：EXP-ROLE", expectedModeText },
            model.HeaderBadges.Select(badge => badge.DisplayText)
        );
    }

    [Fact]
    public void ElectrodeConfigurationModeAlwaysHasExactlyOneSelection()
    {
        using var model = CreateElectrodePage(
            new StimulusModeConfigurationViewModel(StimulusKind.TDcs).CreateSnapshot(),
            StimulusCapabilityProfile.Default
        );

        Assert.True(model.IsStimulusMode);
        Assert.False(model.IsAcquisitionMode);
        Assert.True(model.IsStimulusMode ^ model.IsAcquisitionMode);

        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Stimulus);

        Assert.True(model.IsStimulusMode);
        Assert.False(model.IsAcquisitionMode);
        Assert.True(model.IsStimulusMode ^ model.IsAcquisitionMode);

        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);

        Assert.False(model.IsStimulusMode);
        Assert.True(model.IsAcquisitionMode);
        Assert.True(model.IsStimulusMode ^ model.IsAcquisitionMode);

        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);

        Assert.False(model.IsStimulusMode);
        Assert.True(model.IsAcquisitionMode);
        Assert.True(model.IsStimulusMode ^ model.IsAcquisitionMode);
    }

    [Fact]
    public void StimulusDefaultsAndFrequencyMaximumsFollowMode()
    {
        var tdcs = new StimulusModeConfigurationViewModel(StimulusKind.TDcs);
        var tacs = new StimulusModeConfigurationViewModel(StimulusKind.TAcs);
        var sham = new StimulusModeConfigurationViewModel(StimulusKind.Sham);
        var tpcs = new StimulusModeConfigurationViewModel(StimulusKind.TPcs);

        Assert.Equal(15d, tdcs.RampSeconds);
        Assert.Equal(40d, tacs.Frequency);
        Assert.Equal(100d, tacs.MaximumFrequency);
        Assert.Equal(15d, sham.RampSeconds);
        Assert.Equal(40d, sham.Frequency);
        Assert.Equal(100d, sham.MaximumFrequency);
        Assert.Equal(80d, tpcs.Frequency);
        Assert.Equal(100d, tpcs.MaximumFrequency);

        tdcs.RampSeconds = 31d;
        sham.RampSeconds = 31d;
        tacs.Frequency = 101d;
        sham.Frequency = 101d;
        tpcs.Frequency = 101d;

        Assert.Equal(30d, tdcs.RampSeconds);
        Assert.Equal(30d, sham.RampSeconds);
        Assert.Equal(100d, tacs.Frequency);
        Assert.Equal(100d, sham.Frequency);
        Assert.Equal(100d, tpcs.Frequency);
        tpcs.Frequency = 0.9d;
        Assert.Equal(1d, tpcs.Frequency);
    }

    [Fact]
    public void DisabledImpedanceCapabilityDisablesDetectionAndShowsConfigurationReason()
    {
        var options = new DeviceBackendOptions();
        options.Capabilities.StimulationImpedance = DeviceCapabilitySource.Disabled;
        options.Capabilities.EegImpedance = DeviceCapabilitySource.Disabled;
        var model = new ElectrodeConfigurationPageViewModel(
            ElectrodeConfigurationRouteDataDefaults.Create(),
            new NullRouter(),
            new DialogService(() => null),
            new NullDialogProvider(),
            capabilityAvailability: new DeviceCapabilityAvailability(options)
        );

        Assert.False(model.IsCurrentDetectionCapabilityEnabled);
        Assert.False(model.ToggleDetectionCommand.CanExecute(null));
        Assert.Contains("刺激阻抗", model.ValidationText);

        model.CurrentMode = ElectrodeConfigurationMode.Acquisition;

        Assert.False(model.IsCurrentDetectionCapabilityEnabled);
        Assert.Contains("EEG 阻抗", model.ValidationText);
    }

    [Theory]
    [InlineData(8d, "≤10 kΩ")]
    [InlineData(15d, "≤20 kΩ")]
    [InlineData(25d, "≤30 kΩ")]
    [InlineData(35d, "≤40 kΩ")]
    [InlineData(45d, ">40 kΩ")]
    public void ElectrodeImpedanceDisplaysProtocolRangeInsteadOfApproximateValue(
        double impedance,
        string expected
    )
    {
        var point = new ElectrodeSiteViewModel("F3", 0, 0, true) { Impedance = impedance };

        Assert.Equal(expected, point.ImpedanceText);
        Assert.Contains(expected, point.ToolTipText, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFixedChannelConfigurationBlocksStimulusSaveWithoutCrashing()
    {
        var model = new StimulusModeConfigurationViewModel(
            StimulusKind.TDcs,
            StimulusCapabilityProfile.Default
        );

        Assert.False(model.IsAllocationValid);
        Assert.Contains("未配置固定刺激", model.AllocationStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void HdUsesOneForcedFixedChannelAndExactlyFourSelectableChannels()
    {
        var model = CreateHdConfiguration([1, 8]);
        var fixedChannel = model.PhysicalChannels.Single(channel => channel.IsSelectedFixed);

        Assert.Equal(1, fixedChannel.PhysicalChannelId);
        Assert.True(fixedChannel.Enabled);
        Assert.False(fixedChannel.CanToggle);
        Assert.False(fixedChannel.CanEditCurrent);
        Assert.Equal(model.Current, fixedChannel.Current);
        Assert.Equal(4, model.EnabledSelectableChannelCount);
        Assert.True(model.IsAllocationValid);

        var secondFixed = model.PhysicalChannels.Single(channel => channel.PhysicalChannelId == 8);
        model.SelectFixedChannelCommand.Execute(secondFixed);

        Assert.True(secondFixed.IsSelectedFixed);
        Assert.True(secondFixed.Enabled);
        Assert.False(fixedChannel.Enabled);
        Assert.False(fixedChannel.IsSelectableChannel);
        Assert.Equal(4, model.EnabledSelectableChannelCount);
    }

    [Fact]
    public void HdRequiresSelectableCurrentSumToEqualPeakCurrent()
    {
        var model = CreateHdConfiguration([1]);
        var channel = model.PhysicalChannels.First(item =>
            item is { IsSelectableChannel: true, Enabled: true }
        );

        Assert.False(model.IsPeakAboveSelectableTotal);
        Assert.False(model.IsPeakBelowSelectableTotal);

        channel.Current = 0.1d;

        Assert.False(model.IsAllocationValid);
        Assert.True(model.IsPeakAboveSelectableTotal);
        Assert.False(model.IsPeakBelowSelectableTotal);
        Assert.Contains("须等于峰值", model.AllocationStatusText, StringComparison.Ordinal);

        channel.Current = model.MaximumCurrent;

        Assert.False(model.IsPeakAboveSelectableTotal);
        Assert.True(model.IsPeakBelowSelectableTotal);
    }

    [Fact]
    public void StimulusParameterEditorExposesPeakBalancePseudoClasses()
    {
        var editor = new TestStimulusParameterEditor();

        editor.IsPeakAboveSelectableTotal = true;
        Assert.True(editor.HasPseudoClass(":peak-above-selectable-total"));
        Assert.False(editor.HasPseudoClass(":peak-below-selectable-total"));

        editor.IsPeakAboveSelectableTotal = false;
        editor.IsPeakBelowSelectableTotal = true;
        Assert.False(editor.HasPseudoClass(":peak-above-selectable-total"));
        Assert.True(editor.HasPseudoClass(":peak-below-selectable-total"));
    }

    [Fact]
    public void ChannelAllocationEditorExposesPeakBalancePseudoClasses()
    {
        var editor = new TestChannelAllocationEditor();

        editor.IsPeakAboveSelectableTotal = true;
        Assert.True(editor.HasPseudoClass(":peak-above-selectable-total"));
        Assert.False(editor.HasPseudoClass(":peak-below-selectable-total"));

        editor.IsPeakAboveSelectableTotal = false;
        editor.IsPeakBelowSelectableTotal = true;
        Assert.False(editor.HasPseudoClass(":peak-above-selectable-total"));
        Assert.True(editor.HasPseudoClass(":peak-below-selectable-total"));
    }

    [Fact]
    public void HdRedistributionUsesProtocolStepAndKeepsExactPeakTotal()
    {
        var model = CreateHdConfiguration([1]);
        var currents = model
            .PhysicalChannels.Where(channel =>
                channel is { IsSelectableChannel: true, Enabled: true }
            )
            .Select(channel => channel.Current)
            .ToArray();

        Assert.Equal([0.52d, 0.52d, 0.48d, 0.48d], currents);
        Assert.Equal(2d, currents.Sum(), 6);
        Assert.All(currents, current => Assert.Equal(0, (int)Math.Round(current * 100) % 4));

        model.Current = 1d;
        var adjusted = model
            .PhysicalChannels.Where(channel =>
                channel is { IsSelectableChannel: true, Enabled: true }
            )
            .Select(channel => channel.Current)
            .ToArray();
        Assert.Equal(1d, adjusted.Sum(), 6);
        Assert.True(model.IsAllocationValid);
        Assert.All(adjusted, current => Assert.Equal(0, (int)Math.Round(current * 100) % 4));
    }

    [Fact]
    public void ConfiguredCurrentBoundsAndStepApplyToPeakAndChannels()
    {
        var policy = new StimulationCurrentPolicy(
            new StimulationCurrentOptions
            {
                Minimum = 8,
                Maximum = 160,
                Step = 8,
            }
        );
        var capability = StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions { FixedActivePhysicalChannelIds = [1] },
            policy
        );
        var model = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);

        Assert.Equal(0.08d, model.MinimumCurrent, 6);
        Assert.Equal(1.6d, model.MaximumCurrent, 6);
        Assert.Equal(0.08d, model.CurrentIncrement, 6);
        Assert.Equal(1.6d, model.Current, 6);

        model.SelectedArrayOption = model.ArrayOptions.Single(option =>
            option.Value is StimulusArrayMode.Hd
        );
        Assert.All(
            model.PhysicalChannels,
            channel =>
            {
                Assert.Equal(0.08d, channel.MinimumCurrent, 6);
                Assert.Equal(1.6d, channel.MaximumCurrent, 6);
                Assert.Equal(0.08d, channel.CurrentIncrement, 6);
            }
        );
    }

    [Fact]
    public void HdPeakBelowSelectableMinimumTotalIsRejectedClearly()
    {
        var model = CreateHdConfiguration([1]);

        model.Current = 0.04d;

        Assert.False(model.IsAllocationValid);
        Assert.Contains("小于", model.AllocationStatusText, StringComparison.Ordinal);
        Assert.Contains("0.16 mA", model.AllocationStatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StimulusDirection.Positive, StimulationChannelRole.FixedActive, "阴极")]
    [InlineData(StimulusDirection.Positive, StimulationChannelRole.Selectable, "阳极")]
    [InlineData(StimulusDirection.Negative, StimulationChannelRole.FixedActive, "阳极")]
    [InlineData(StimulusDirection.Negative, StimulationChannelRole.Selectable, "阴极")]
    [InlineData(
        StimulusDirection.Bidirectional,
        StimulationChannelRole.FixedActive,
        "固定刺激电极"
    )]
    [InlineData(StimulusDirection.Bidirectional, StimulationChannelRole.Selectable, "自选刺激电极")]
    public void ElectrodeRoleComesFromDirectionAndPhysicalChannelRole(
        StimulusDirection direction,
        StimulationChannelRole role,
        string expected
    )
    {
        Assert.Equal(expected, StimulationElectrodeRoleResolver.GetLabel(direction, role));
    }

    [Theory]
    [InlineData(StimulusDirection.Positive, StimulationChannelRole.FixedActive, false)]
    [InlineData(StimulusDirection.Positive, StimulationChannelRole.Selectable, true)]
    [InlineData(StimulusDirection.Negative, StimulationChannelRole.FixedActive, true)]
    [InlineData(StimulusDirection.Negative, StimulationChannelRole.Selectable, false)]
    [InlineData(StimulusDirection.Bidirectional, StimulationChannelRole.FixedActive, true)]
    [InlineData(StimulusDirection.Bidirectional, StimulationChannelRole.Selectable, false)]
    public void ElectrodeSelectionColorFollowsResolvedRole(
        StimulusDirection direction,
        StimulationChannelRole role,
        bool usesBlueColor
    )
    {
        var option = new StimulusElectrodeSelectionOptionViewModel(
            Guid.NewGuid(),
            1,
            role,
            1,
            direction
        );

        Assert.Equal(usesBlueColor, option.UsesBlueColor);
        Assert.Equal(!usesBlueColor, option.UsesRedColor);
    }

    [Theory]
    [InlineData(
        StimulusDirection.Positive,
        StimulationChannelRole.Selectable,
        "阳极",
        StimulationChannelRole.FixedActive,
        "阴极"
    )]
    [InlineData(
        StimulusDirection.Negative,
        StimulationChannelRole.FixedActive,
        "阳极",
        StimulationChannelRole.Selectable,
        "阴极"
    )]
    [InlineData(
        StimulusDirection.Bidirectional,
        StimulationChannelRole.FixedActive,
        "固定刺激电极",
        StimulationChannelRole.Selectable,
        "自选刺激电极"
    )]
    public void ElectrodeRoleSectionsAlwaysPlaceBlueOnLeftAndRedOnRight(
        StimulusDirection direction,
        StimulationChannelRole expectedLeftRole,
        string expectedLeftLabel,
        StimulationChannelRole expectedRightRole,
        string expectedRightLabel
    )
    {
        var target = new StimulusTargetSnapshot(Guid.NewGuid(), 1, 2d, null, []);
        var fixedOption = new StimulusElectrodeSelectionOptionViewModel(
            target.TargetId,
            1,
            StimulationChannelRole.FixedActive,
            1,
            direction
        );
        var selectableOption = new StimulusElectrodeSelectionOptionViewModel(
            target.TargetId,
            1,
            StimulationChannelRole.Selectable,
            1,
            direction
        );
        var group = new StimulusElectrodeSelectionGroupViewModel(
            target,
            fixedOption,
            selectableOption
        );

        Assert.Equal(expectedLeftRole, group.Options[0].Role);
        Assert.Equal(expectedLeftLabel, group.Options[0].Label);
        Assert.True(group.Options[0].UsesBlueColor);
        Assert.Equal(expectedRightRole, group.Options[1].Role);
        Assert.Equal(expectedRightLabel, group.Options[1].Label);
        Assert.True(group.Options[1].UsesRedColor);
    }

    [Fact]
    public void PointSelectionUsesSeparateRoleSectionsBeforeChannelMapping()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var model = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData(
                "EXP-ROLE",
                "SUBJECT-ROLE",
                stimulus.CreateSnapshot()
            ),
            new NullRouter(),
            new DialogService(() => null),
            new NullDialogProvider(),
            capability,
            new PreviewImpedanceDetectionService()
        );
        var fixedOption = model.StimulusSelectionOptions.Single(option =>
            option.Role == StimulationChannelRole.FixedActive
        );
        var selectableOption = model.StimulusSelectionOptions.Single(option =>
            option.Role == StimulationChannelRole.Selectable
        );
        var points = model.Points.Where(point => point.CanStimulate).Take(3).ToArray();

        Assert.False(model.StimulusSelectionGroups.Single().ShowDisplayName);
        Assert.True(fixedOption.IsSelected);
        model.SelectPointCommand.Execute(points[0]);
        model.SelectPointCommand.Execute(points[1]);

        Assert.Equal(1, fixedOption.AssignedCount);
        Assert.Equal(StimulationChannelRole.FixedActive, points[0].StimulationChannelRole);
        Assert.False(points[1].IsStimulus);
        Assert.Equal("阴极", points[0].RoleText);

        model.SelectStimulusSelectionOptionCommand.Execute(selectableOption);
        model.SelectPointCommand.Execute(points[1]);

        Assert.Equal(1, selectableOption.AssignedCount);
        Assert.Equal(StimulationChannelRole.Selectable, points[1].StimulationChannelRole);
        Assert.Equal("阳极", points[1].RoleText);
        Assert.All(
            model.CurrentImpedanceItems.Single(row => row.Site == points[0]).StimulationChannels,
            channel => Assert.Equal(StimulationChannelRole.FixedActive, channel.Role)
        );
        Assert.All(
            model.CurrentImpedanceItems.Single(row => row.Site == points[1]).StimulationChannels,
            channel => Assert.Equal(StimulationChannelRole.Selectable, channel.Role)
        );

        model.SelectedPoint = points[0];
        model.ClearSelectedPointCommand.Execute(null);

        Assert.Equal(0, fixedOption.AssignedCount);
        Assert.False(points[0].IsStimulus);
    }

    [Fact]
    public void ElectrodePointConsumesSecondPressAfterExecutingDoubleClickCommand()
    {
        var executionCount = 0;
        var point = new ElectrodePoint
        {
            DoubleClickCommand = new RelayCommand(() => executionCount++),
        };

        Assert.False(point.TryHandleDoubleClick(1));
        Assert.Equal(0, executionCount);

        Assert.True(point.TryHandleDoubleClick(2));
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public void ClearPointCommandClearsStimulusPointAndKeepsRemainingAssignment()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability);
        var fixedOption = model.StimulusSelectionOptions.Single(option =>
            option.Role == StimulationChannelRole.FixedActive
        );
        var selectableOption = model.StimulusSelectionOptions.Single(option =>
            option.Role == StimulationChannelRole.Selectable
        );
        var points = model.Points.Where(point => point.CanStimulate).Take(2).ToArray();

        model.SelectPointCommand.Execute(points[0]);
        model.SelectStimulusSelectionOptionCommand.Execute(selectableOption);
        model.SelectPointCommand.Execute(points[1]);

        model.ClearPointCommand.Execute(points[0]);

        Assert.False(points[0].IsStimulus);
        Assert.Null(points[0].StimulationPhysicalChannelId);
        Assert.Equal(0, fixedOption.AssignedCount);
        Assert.True(points[1].IsStimulus);
        Assert.Equal(1, selectableOption.AssignedCount);
        Assert.Null(model.SelectedPoint);
    }

    [Theory]
    [InlineData(ElectrodeRole.Acquisition)]
    [InlineData(ElectrodeRole.Reference)]
    [InlineData(ElectrodeRole.Ground)]
    public void ClearPointCommandClearsAcquisitionRoles(ElectrodeRole role)
    {
        var model = CreateElectrodePage(
            new StimulusModeConfigurationViewModel(StimulusKind.TDcs).CreateSnapshot(),
            StimulusCapabilityProfile.Default
        );
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var point = model.Points.First(candidate =>
            candidate.Role == ElectrodeRole.None && !candidate.IsStimulus
        );
        model.SelectedPoint = point;
        model.SetSelectedPointRoleCommand.Execute(role);

        model.ClearPointCommand.Execute(point);

        Assert.Equal(ElectrodeRole.None, point.Role);
        Assert.Null(point.Impedance);
        Assert.Null(model.SelectedPoint);
    }

    [Fact]
    public void ClearPointCommandLeavesUnconfiguredPointUnchanged()
    {
        var model = CreateElectrodePage(
            new StimulusModeConfigurationViewModel(StimulusKind.TDcs).CreateSnapshot(),
            StimulusCapabilityProfile.Default
        );
        var point = model.Points.First(candidate =>
            candidate.Role == ElectrodeRole.None && !candidate.IsStimulus
        );

        model.ClearPointCommand.Execute(point);

        Assert.Equal(ElectrodeRole.None, point.Role);
        Assert.False(point.IsStimulus);
    }

    [Fact]
    public void SoleFixedPhysicalChannelIsAssignedAutomatically()
    {
        var capability = CreateCapability([7]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability);
        var fixedPoint = model.Points.First(point => point.CanStimulate);

        model.SelectPointCommand.Execute(fixedPoint);

        Assert.Equal(StimulationChannelRole.FixedActive, fixedPoint.StimulationChannelRole);
        Assert.Equal(7, fixedPoint.StimulationPhysicalChannelId);
        var fixedRow = model.CurrentImpedanceItems.Single(row => row.Site == fixedPoint);
        Assert.Equal(7, fixedRow.SelectedStimulationChannel?.PhysicalChannelId);
        Assert.False(fixedRow.RequiresImpedanceDetection);
        Assert.Equal("-", fixedRow.ImpedanceText);
        Assert.Equal("无需检测", fixedRow.QualityText);
    }

    [Fact]
    public void ShamRoleSectionsFollowDirectAndAlternatingDirections()
    {
        var capability = CreateCapability([1]);
        var direct = new StimulusModeConfigurationViewModel(StimulusKind.Sham, capability);
        direct.SelectedDirectionOption = direct.DirectionOptions.Single(option =>
            option.Value is StimulusDirection.Negative
        );
        var directPage = CreateElectrodePage(direct.CreateSnapshot(), capability);

        Assert.Equal(
            "阳极",
            directPage
                .StimulusSelectionOptions.Single(option =>
                    option.Role == StimulationChannelRole.FixedActive
                )
                .Label
        );
        Assert.Equal(
            "阴极",
            directPage
                .StimulusSelectionOptions.Single(option =>
                    option.Role == StimulationChannelRole.Selectable
                )
                .Label
        );

        var alternating = new StimulusModeConfigurationViewModel(StimulusKind.Sham, capability);
        alternating.SelectedShamModeOption = alternating.ShamModeOptions.Single(option =>
            option.Value is ShamWaveformMode.Alternating
        );
        var alternatingPage = CreateElectrodePage(alternating.CreateSnapshot(), capability);

        Assert.Equal(
            "固定刺激电极",
            alternatingPage
                .StimulusSelectionOptions.Single(option =>
                    option.Role == StimulationChannelRole.FixedActive
                )
                .Label
        );
        Assert.Equal(
            "自选刺激电极",
            alternatingPage
                .StimulusSelectionOptions.Single(option =>
                    option.Role == StimulationChannelRole.Selectable
                )
                .Label
        );
    }

    [Fact]
    public void MultiTargetRoleSectionsOnlyOfferChannelsFromTheirOwnTarget()
    {
        var capability = StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions
            {
                PhysicalChannelCount = 12,
                FixedActivePhysicalChannelIds = [1, 8],
                MultiTargetEnabled = true,
                AllowedElectrodeSiteIds = ["FP1", "FP2", "F3", "F4"],
            }
        );
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        var snapshot = new StimulusConfigurationSnapshot(
            StimulusKind.TAcs,
            StimulusArrayMode.MultiTarget,
            StimulusDirection.Bidirectional,
            ShamWaveformMode.Direct,
            7,
            40,
            79,
            [
                new StimulusTargetSnapshot(
                    firstTarget,
                    1,
                    1d,
                    1,
                    [
                        new StimulationPhysicalChannelSnapshot(
                            1,
                            StimulationChannelRole.FixedActive,
                            1d
                        ),
                        new StimulationPhysicalChannelSnapshot(
                            2,
                            StimulationChannelRole.Selectable,
                            1d
                        ),
                    ]
                ),
                new StimulusTargetSnapshot(
                    secondTarget,
                    2,
                    1d,
                    8,
                    [
                        new StimulationPhysicalChannelSnapshot(
                            8,
                            StimulationChannelRole.FixedActive,
                            1d
                        ),
                        new StimulationPhysicalChannelSnapshot(
                            9,
                            StimulationChannelRole.Selectable,
                            1d
                        ),
                    ]
                ),
            ]
        );
        var model = CreateElectrodePage(snapshot, capability);
        var points = new Queue<ElectrodeSiteViewModel>(
            model.Points.Where(point => point.CanStimulate)
        );
        foreach (var option in model.StimulusSelectionOptions)
        {
            model.SelectStimulusSelectionOptionCommand.Execute(option);
            model.SelectPointCommand.Execute(points.Dequeue());
        }

        var firstFixed = model.CurrentImpedanceItems.Single(row =>
            row.Site.StimulusTargetId == firstTarget
            && row.Site.StimulationChannelRole == StimulationChannelRole.FixedActive
        );
        var secondFixed = model.CurrentImpedanceItems.Single(row =>
            row.Site.StimulusTargetId == secondTarget
            && row.Site.StimulationChannelRole == StimulationChannelRole.FixedActive
        );

        Assert.Equal(
            [1],
            firstFixed.StimulationChannels.Select(channel => channel.PhysicalChannelId)
        );
        Assert.Equal(
            [8],
            secondFixed.StimulationChannels.Select(channel => channel.PhysicalChannelId)
        );
        Assert.Equal(2, model.StimulusSelectionGroups.Count);
        Assert.All(model.StimulusSelectionGroups, group => Assert.Equal(2, group.Options.Count));
        Assert.All(model.StimulusSelectionGroups, group => Assert.True(group.ShowDisplayName));
        Assert.Contains(
            "靶点1：CH1 1.00 mA · CH2 1.00 mA",
            model.StimulusChannelCurrentSummary,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "靶点2：CH8 1.00 mA · CH9 1.00 mA",
            model.StimulusChannelCurrentSummary,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void DualChannelElectrodePageRequiresOneFixedAndOneSelectableMapping()
    {
        var capability = CreateCapability([1, 8]);
        var targetId = Guid.NewGuid();
        var route = new ElectrodeConfigurationRouteData(
            "EXP-TEST",
            "SUBJECT-TEST",
            new StimulusConfigurationSnapshot(
                StimulusKind.TDcs,
                StimulusArrayMode.DualChannel,
                StimulusDirection.Positive,
                ShamWaveformMode.Direct,
                7,
                40,
                79,
                [new StimulusTargetSnapshot(targetId, 1, 2d, null, [])]
            )
        );
        var model = new ElectrodeConfigurationPageViewModel(
            route,
            new NullRouter(),
            new DialogService(() => null),
            new NullDialogProvider(),
            capability,
            new PreviewImpedanceDetectionService()
        );
        var first = model.Points.First(point =>
            point.Name.Equals("FP2", StringComparison.OrdinalIgnoreCase)
        );
        var second = model.Points.First(point =>
            point.Name.Equals("F3", StringComparison.OrdinalIgnoreCase)
        );

        model.SelectPointCommand.Execute(first);
        model.SelectStimulusSelectionOptionCommand.Execute(
            model.StimulusSelectionOptions.Single(option =>
                option.Role == StimulationChannelRole.Selectable
            )
        );
        model.SelectPointCommand.Execute(second);
        model.CurrentImpedanceItems[0].SelectedStimulationChannel = model
            .CurrentImpedanceItems[0]
            .StimulationChannels.First(channel =>
                channel is { PhysicalChannelId: 1, Role: StimulationChannelRole.FixedActive }
            );
        model.CurrentImpedanceItems[1].SelectedStimulationChannel = model
            .CurrentImpedanceItems[1]
            .StimulationChannels.First(channel =>
                channel is { PhysicalChannelId: 2, Role: StimulationChannelRole.Selectable }
            );

        Assert.True(model.IsStimulusConfigurationComplete);
        Assert.Contains(
            model.CreateStimulusAssignments(),
            assignment =>
                assignment is { PhysicalChannelId: 1, Role: StimulationChannelRole.FixedActive }
        );
        Assert.Contains(
            model.CreateStimulusAssignments(),
            assignment =>
                assignment is { PhysicalChannelId: 2, Role: StimulationChannelRole.Selectable }
        );

        Assert.All(
            model.CurrentImpedanceItems[0].StimulationChannels,
            channel => Assert.Equal(StimulationChannelRole.FixedActive, channel.Role)
        );
        Assert.All(
            model.CurrentImpedanceItems[1].StimulationChannels,
            channel => Assert.Equal(StimulationChannelRole.Selectable, channel.Role)
        );
    }

    [Fact]
    public void DualChannelCurrentSummaryAppearsPerMappingAndUpdatesAfterClear()
    {
        var capability = CreateCapability([1, 8]);
        var targetId = Guid.NewGuid();
        var snapshot = new StimulusConfigurationSnapshot(
            StimulusKind.TDcs,
            StimulusArrayMode.DualChannel,
            StimulusDirection.Positive,
            ShamWaveformMode.Direct,
            7,
            40,
            79,
            [new StimulusTargetSnapshot(targetId, 1, 2d, null, [])]
        );
        var model = CreateElectrodePage(snapshot, capability);
        var fixedOption = model.StimulusSelectionOptions.Single(option =>
            option.Role == StimulationChannelRole.FixedActive
        );
        var selectableOption = model.StimulusSelectionOptions.Single(option =>
            option.Role == StimulationChannelRole.Selectable
        );
        var points = model.Points.Where(point => point.CanStimulate).Take(2).ToArray();

        Assert.False(model.HasStimulusChannelCurrentSummary);
        Assert.False(model.ShowStimulusChannelCurrentSummary);
        Assert.Equal(string.Empty, model.StimulusChannelCurrentSummary);

        model.SelectStimulusSelectionOptionCommand.Execute(fixedOption);
        model.SelectPointCommand.Execute(points[0]);
        var fixedRow = model.CurrentImpedanceItems.Single(row => row.Site == points[0]);
        fixedRow.SelectedStimulationChannel = fixedRow.StimulationChannels.Single(channel =>
            channel.PhysicalChannelId == 1
        );

        Assert.True(model.ShowStimulusChannelCurrentSummary);
        Assert.Equal("CH1 2.00 mA", model.StimulusChannelCurrentSummary);

        model.SelectStimulusSelectionOptionCommand.Execute(selectableOption);
        model.SelectPointCommand.Execute(points[1]);
        var selectableRow = model.CurrentImpedanceItems.Single(row => row.Site == points[1]);
        selectableRow.SelectedStimulationChannel = selectableRow.StimulationChannels.Single(
            channel => channel.PhysicalChannelId == 2
        );

        Assert.Equal("CH1 2.00 mA · CH2 2.00 mA", model.StimulusChannelCurrentSummary);

        model.SelectedPoint = points[0];
        model.ClearSelectedPointCommand.Execute(null);

        Assert.Equal("CH2 2.00 mA", model.StimulusChannelCurrentSummary);
    }

    [Fact]
    public void HdCurrentSummaryIsAvailableBeforeElectrodePointMapping()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        stimulus.SelectedArrayOption = stimulus.ArrayOptions.Single(option =>
            option.Value is StimulusArrayMode.Hd
        );
        var snapshot = stimulus.CreateSnapshot();
        var model = CreateElectrodePage(snapshot, capability);

        Assert.False(model.StimulusSelectionGroups.Single().ShowDisplayName);
        Assert.Equal("HD", model.ArrayModeText);
        Assert.True(model.ShowStimulusChannelCurrentSummary);
        foreach (var channel in snapshot.Targets.Single().Channels)
        {
            Assert.Contains(
                $"CH{channel.PhysicalChannelId} {channel.Current:0.00} mA",
                model.StimulusChannelCurrentSummary,
                StringComparison.Ordinal
            );
        }
    }

    [Fact]
    public void SameRolePhysicalChannelCannotBeReusedAcrossHdElectrodes()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        stimulus.SelectedArrayOption = stimulus.ArrayOptions.Single(option =>
            option.Value is StimulusArrayMode.Hd
        );
        var model = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData(
                "EXP-CONFLICT",
                "SUBJECT-CONFLICT",
                stimulus.CreateSnapshot()
            ),
            new NullRouter(),
            new DialogService(() => null),
            new NullDialogProvider(),
            capability,
            new PreviewImpedanceDetectionService()
        );
        var points = new Queue<ElectrodeSiteViewModel>(
            model
                .Points.Where(point => point.CanStimulate)
                .Take(model.RequiredStimulusElectrodeCount)
        );
        foreach (var option in model.StimulusSelectionOptions)
        {
            model.SelectStimulusSelectionOptionCommand.Execute(option);
            for (var count = 0; count < option.RequiredCount; count++)
                model.SelectPointCommand.Execute(points.Dequeue());
        }
        var selectableRows = model
            .CurrentImpedanceItems.Where(row =>
                row.Site.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            .Take(2)
            .ToArray();
        var sharedChannel = selectableRows[0].StimulationChannels.First();

        selectableRows[0].SelectedStimulationChannel = sharedChannel;
        selectableRows[1].SelectedStimulationChannel = selectableRows[1]
            .StimulationChannels.Single(channel =>
                channel.PhysicalChannelId == sharedChannel.PhysicalChannelId
            );

        Assert.True(model.HasChannelConflicts);
        Assert.True(selectableRows[1].HasChannelConflict);
        Assert.False(selectableRows[1].Site.HasStimulationChannel);
    }

    [Fact]
    public void ConflictingHdChannelsCommitWhenFinalCombinationIsUnique()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        stimulus.SelectedArrayOption = stimulus.ArrayOptions.Single(option =>
            option.Value is StimulusArrayMode.Hd
        );
        var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability);
        ConfigureStimulusPointsAndChannels(model);
        var selectableRows = model
            .CurrentImpedanceItems.Where(row =>
                row.Site.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            .Take(2)
            .ToArray();
        var firstChannel = selectableRows[0].SelectedStimulationChannel!;
        var secondChannel = selectableRows[1].SelectedStimulationChannel!;

        selectableRows[1].SelectedStimulationChannel = selectableRows[1]
            .StimulationChannels.Single(channel =>
                channel.PhysicalChannelId == firstChannel.PhysicalChannelId
            );

        Assert.True(model.HasChannelConflicts);
        Assert.False(model.CanToggleDetection);

        selectableRows[0].SelectedStimulationChannel = selectableRows[0]
            .StimulationChannels.Single(channel =>
                channel.PhysicalChannelId == secondChannel.PhysicalChannelId
            );

        Assert.False(model.HasChannelConflicts);
        Assert.False(selectableRows[0].HasChannelConflict);
        Assert.False(selectableRows[1].HasChannelConflict);
        Assert.Equal(
            secondChannel.PhysicalChannelId,
            selectableRows[0].Site.StimulationPhysicalChannelId
        );
        Assert.Equal(
            firstChannel.PhysicalChannelId,
            selectableRows[1].Site.StimulationPhysicalChannelId
        );
        Assert.True(model.IsStimulusConfigurationComplete);
        Assert.True(model.CanToggleDetection);
    }

    [Fact]
    public void SelectingOriginalChannelAfterConflictReenablesDetection()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        stimulus.SelectedArrayOption = stimulus.ArrayOptions.Single(option =>
            option.Value is StimulusArrayMode.Hd
        );
        var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability);
        ConfigureStimulusPointsAndChannels(model);
        var selectableRows = model
            .CurrentImpedanceItems.Where(row =>
                row.Site.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            .Take(2)
            .ToArray();
        var originalChannel = selectableRows[1].SelectedStimulationChannel!;
        var conflictingChannel = selectableRows[0].SelectedStimulationChannel!;

        selectableRows[1].SelectedStimulationChannel = selectableRows[1]
            .StimulationChannels.Single(channel =>
                channel.PhysicalChannelId == conflictingChannel.PhysicalChannelId
            );
        Assert.False(model.CanToggleDetection);

        selectableRows[1].SelectedStimulationChannel = selectableRows[1]
            .StimulationChannels.Single(channel =>
                channel.PhysicalChannelId == originalChannel.PhysicalChannelId
            );

        Assert.False(model.HasChannelConflicts);
        Assert.False(selectableRows[1].HasChannelConflict);
        Assert.True(model.IsStimulusConfigurationComplete);
        Assert.True(model.CanToggleDetection);
        Assert.True(model.ToggleDetectionCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(
        StimulusKind.TDcs,
        StimulusArrayMode.DualChannel,
        StimulusDirection.Positive,
        "阴极",
        "阳极"
    )]
    [InlineData(
        StimulusKind.TDcs,
        StimulusArrayMode.DualChannel,
        StimulusDirection.Negative,
        "阳极",
        "阴极"
    )]
    [InlineData(
        StimulusKind.TDcs,
        StimulusArrayMode.Hd,
        StimulusDirection.Positive,
        "阴极",
        "阳极"
    )]
    [InlineData(
        StimulusKind.TAcs,
        StimulusArrayMode.Hd,
        StimulusDirection.Bidirectional,
        "固定刺激电极",
        "自选刺激电极"
    )]
    public void CompletePageFlowMapsEveryElectrodeForSupportedAcceptanceScenarios(
        StimulusKind kind,
        StimulusArrayMode arrayMode,
        StimulusDirection direction,
        string fixedRoleLabel,
        string selectableRoleLabel
    )
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(kind, capability);
        stimulus.SelectedArrayOption = stimulus.ArrayOptions.Single(option =>
            option.Value is StimulusArrayMode value && value == arrayMode
        );
        stimulus.SelectedDirectionOption = stimulus.DirectionOptions.Single(option =>
            option.Value is StimulusDirection value && value == direction
        );
        Assert.True(stimulus.IsAllocationValid);

        var model = new ElectrodeConfigurationPageViewModel(
            new ElectrodeConfigurationRouteData(
                "EXP-FLOW",
                "SUBJECT-FLOW",
                stimulus.CreateSnapshot()
            ),
            new NullRouter(),
            new DialogService(() => null),
            new NullDialogProvider(),
            capability,
            new PreviewImpedanceDetectionService()
        );
        var availablePoints = new Queue<ElectrodeSiteViewModel>(
            model
                .Points.Where(point => point.CanStimulate)
                .Take(model.RequiredStimulusElectrodeCount)
        );
        foreach (var option in model.StimulusSelectionOptions)
        {
            model.SelectStimulusSelectionOptionCommand.Execute(option);
            for (var count = 0; count < option.RequiredCount; count++)
                model.SelectPointCommand.Execute(availablePoints.Dequeue());
        }

        var usedChannels = new HashSet<int>();
        foreach (var row in model.CurrentImpedanceItems)
        {
            var channel = row.StimulationChannels.First(option =>
                usedChannels.Add(option.PhysicalChannelId)
            );
            row.SelectedStimulationChannel = channel;
        }

        var assignments = model.CreateStimulusAssignments();
        Assert.True(model.IsStimulusPositionSelectionComplete);
        Assert.True(model.IsStimulusChannelMappingComplete);
        Assert.True(model.IsStimulusConfigurationComplete);
        Assert.True(model.CanDetectCurrentMode);
        Assert.Equal(model.RequiredStimulusElectrodeCount, assignments.Count);
        Assert.Equal(
            fixedRoleLabel,
            model
                .Points.Single(point =>
                    point.StimulationChannelRole == StimulationChannelRole.FixedActive
                )
                .RoleText
        );
        Assert.All(
            model.Points.Where(point =>
                point.StimulationChannelRole == StimulationChannelRole.Selectable
            ),
            point => Assert.Equal(selectableRoleLabel, point.RoleText)
        );
    }

    [Fact]
    public void MultiTargetSnapshotCanRepresentIndependentTargetGroups()
    {
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        var snapshot = new StimulusConfigurationSnapshot(
            StimulusKind.TDcs,
            StimulusArrayMode.MultiTarget,
            StimulusDirection.Positive,
            ShamWaveformMode.Direct,
            7,
            40,
            79,
            [
                new StimulusTargetSnapshot(
                    firstTarget,
                    1,
                    1d,
                    1,
                    [
                        new StimulationPhysicalChannelSnapshot(
                            1,
                            StimulationChannelRole.FixedActive,
                            1d
                        ),
                        new StimulationPhysicalChannelSnapshot(
                            2,
                            StimulationChannelRole.Selectable,
                            1d
                        ),
                    ]
                ),
                new StimulusTargetSnapshot(
                    secondTarget,
                    2,
                    1d,
                    8,
                    [
                        new StimulationPhysicalChannelSnapshot(
                            8,
                            StimulationChannelRole.FixedActive,
                            1d
                        ),
                        new StimulationPhysicalChannelSnapshot(
                            7,
                            StimulationChannelRole.Selectable,
                            1d
                        ),
                    ]
                ),
            ]
        );

        Assert.Equal(2, snapshot.Targets.Count);
        Assert.Equal(
            2,
            snapshot
                .Targets.SelectMany(target => target.Channels)
                .Count(channel => channel.Role == StimulationChannelRole.FixedActive)
        );
        Assert.Equal(
            4,
            snapshot
                .Targets.SelectMany(target => target.Channels)
                .Select(channel => channel.PhysicalChannelId)
                .Distinct()
                .Count()
        );
    }

    [Fact]
    public void MultiTargetConfigurationRejectsCrossTargetChannelReuse()
    {
        var capability = StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions
            {
                PhysicalChannelCount = 12,
                FixedActivePhysicalChannelIds = [1, 8],
                MultiTargetEnabled = true,
                AllowedElectrodeSiteIds =
                [
                    "FP1",
                    "FP2",
                    "F3",
                    "F4",
                    "FC5",
                    "FC6",
                    "T7",
                    "T8",
                    "CP5",
                    "CP6",
                ],
            }
        );
        var model = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        model.SelectedArrayOption = model.ArrayOptions.Single(option =>
            option.Value is StimulusArrayMode.MultiTarget
        );
        model.AddTargetCommand.Execute(null);

        Assert.Equal(2, model.Targets.Count);
        Assert.True(model.HasCrossTargetChannelConflicts);
        Assert.False(model.IsAllocationValid);
        Assert.Contains("不得重复", model.AllocationStatusText, StringComparison.Ordinal);

        var secondTarget = model.Targets[1];
        secondTarget.SelectFixedChannelCommand.Execute(
            secondTarget.Channels.Single(channel => channel.PhysicalChannelId == 8)
        );
        foreach (
            var channel in secondTarget
                .Channels.Where(channel => channel is { IsSelectableChannel: true, Enabled: true })
                .ToArray()
        )
            channel.Enabled = false;
        foreach (var channelId in new[] { 6, 7, 9, 10 })
            secondTarget
                .Channels.Single(channel => channel.PhysicalChannelId == channelId)
                .Enabled = true;

        Assert.False(model.HasCrossTargetChannelConflicts);
        Assert.True(model.IsAllocationValid);
        Assert.Equal(10, model.RequiredElectrodeCount);
    }

    [Fact]
    public async Task StimulusDetectionStreamsReadingsLocksEditingAndStopsWithoutLateUpdates()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            service,
            new ImpedanceDetectionOptions
            {
                AutoStopStimulationWhenPassed = false,
                AutoStopWhenNotPassedTimeout = TimeSpan.FromMilliseconds(20),
            }
        );
        ConfigureStimulusPointsAndChannels(model);

        await model.ToggleDetectionCommand.ExecuteAsync(null);

        Assert.True(model.IsAnyDetecting);
        Assert.True(model.IsCurrentDetecting);
        Assert.False(model.CanEditConfiguration);
        Assert.False(model.CanGoBack);
        Assert.False(model.CanOpenConfirmation);
        Assert.Equal("停止", model.DetectionButtonText);
        Assert.Equal("检测中", model.CurrentDetectionStatusText);
        Assert.False(model.SelectModeCommand.CanExecute(ElectrodeConfigurationMode.Acquisition));
        Assert.True(model.IsStimulusMode);
        Assert.False(model.IsAcquisitionMode);
        Assert.True(model.IsStimulusMode ^ model.IsAcquisitionMode);

        var fixedSite = model.Points.Single(point =>
            point.StimulationChannelRole == StimulationChannelRole.FixedActive
        );
        var selectableSites = model
            .Points.Where(point =>
                point.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            .Select(point => point.Name)
            .ToArray();
        service.Publish(selectableSites.ToDictionary(site => site, _ => 24d));
        await WaitUntilAsync(() => model.StimulusDetectionState == ImpedanceDetectionState.Failed);
        Assert.Equal(model.RequiredStimulusElectrodeCount, model.CurrentImpedanceItems.Count);
        service.Publish(selectableSites.ToDictionary(site => site, _ => 8d));
        await WaitUntilAsync(() => model.StimulusDetectionState == ImpedanceDetectionState.Passed);
        await Task.Delay(50);

        Assert.True(model.IsAnyDetecting);
        Assert.Equal(0, service.StimulationStopCount);

        await model.ToggleDetectionCommand.ExecuteAsync(null);

        Assert.False(model.IsAnyDetecting);
        Assert.True(model.CanEditConfiguration);
        Assert.True(model.CanGoBack);
        Assert.Equal("检测", model.DetectionButtonText);
        Assert.Equal(1, service.StimulationStopCount);
        Assert.Null(fixedSite.Impedance);
        Assert.All(
            model.Points.Where(point =>
                point.StimulationChannelRole == StimulationChannelRole.Selectable
            ),
            point => Assert.Equal(8d, point.Impedance)
        );

        service.Publish(selectableSites.ToDictionary(site => site, _ => 45d));
        await Task.Delay(50);
        Assert.Null(fixedSite.Impedance);
        Assert.All(
            model.Points.Where(point =>
                point.StimulationChannelRole == StimulationChannelRole.Selectable
            ),
            point => Assert.Equal(8d, point.Impedance)
        );
    }

    [Fact]
    public async Task StimulusDetectionFreezesCompleteConfigurationAndReusesItWhenStopping()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability)
        {
            RampSeconds = 7,
            Frequency = 40,
            DutyPercent = 79,
            Current = 1,
        };
        stimulus.SelectedDirectionOption = stimulus.DirectionOptions.Single(option =>
            option.Value is StimulusDirection.Negative
        );
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            service,
            new ImpedanceDetectionOptions { AutoStopStimulationWhenPassed = false }
        );
        ConfigureStimulusPointsAndChannels(model);

        await model.ToggleDetectionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => service.LastWatchStimulationRequest is not null);

        var startedRequest = Assert.IsType<StimulationImpedanceDetectionRequest>(
            service.LastWatchStimulationRequest
        );
        Assert.Equal(StimulusKind.TDcs, startedRequest.Configuration.Kind);
        Assert.Equal(StimulusDirection.Negative, startedRequest.Configuration.Direction);
        Assert.Equal(7d, startedRequest.Configuration.RampSeconds);
        Assert.Equal(40d, startedRequest.Configuration.Frequency);
        Assert.Equal(79d, startedRequest.Configuration.DutyPercent);
        Assert.All(
            startedRequest.Configuration.Targets.SelectMany(target => target.Channels),
            channel => Assert.Equal(1d, channel.Current)
        );

        await model.ToggleDetectionCommand.ExecuteAsync(null);

        Assert.Same(startedRequest, service.LastStopStimulationRequest);
        Assert.Equal(1, service.StimulationStopCount);
    }

    [Theory]
    [InlineData(StimulusKind.TAcs, ShamWaveformMode.Direct, "100 Hz")]
    [InlineData(StimulusKind.TPcs, ShamWaveformMode.Direct, "100 Hz / 占空比 79%")]
    [InlineData(StimulusKind.Sham, ShamWaveformMode.Direct, "缓升降 7 s")]
    [InlineData(StimulusKind.Sham, ShamWaveformMode.Alternating, "100 Hz")]
    public void ConfirmationIncludesAllEffectiveWaveformParameters(
        StimulusKind kind,
        ShamWaveformMode sham,
        string expected
    )
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(kind, capability)
        {
            RampSeconds = 7,
            Frequency = 100,
            DutyPercent = 79,
        };
        stimulus.SelectedShamModeOption = stimulus.ShamModeOptions.Single(x =>
            Equals(x.Value, sham)
        );
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            new ControlledImpedanceDetectionService()
        );
        Assert.EndsWith(expected, model.StimulusParameterSummary);
        if (kind == StimulusKind.Sham)
            Assert.Contains(
                sham == ShamWaveformMode.Direct ? "直流" : "交流",
                model.StimulusKindText
            );
    }

    [Fact]
    public async Task StimulusDetectionAutomaticallyStopsAfterPassingReading()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability, service);
        ConfigureStimulusPointsAndChannels(model);
        var selectableSites = model
            .Points.Where(point =>
                point.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            .Select(point => point.Name)
            .ToArray();

        await model.ToggleDetectionCommand.ExecuteAsync(null);
        service.Publish(selectableSites.ToDictionary(site => site, _ => 24d));
        await WaitUntilAsync(() => model.StimulusDetectionState == ImpedanceDetectionState.Failed);

        Assert.True(model.IsAnyDetecting);
        Assert.Equal(0, service.StimulationStopCount);

        service.Publish(selectableSites.ToDictionary(site => site, _ => 8d));
        await WaitUntilAsync(() => !model.IsAnyDetecting);

        Assert.Equal(ImpedanceDetectionState.Passed, model.StimulusDetectionState);
        Assert.Equal(1, service.StimulationStopCount);
        Assert.True(model.CanEditConfiguration);

        service.Publish(selectableSites.ToDictionary(site => site, _ => 45d));
        await Task.Delay(50);
        Assert.Equal(ImpedanceDetectionState.Passed, model.StimulusDetectionState);
    }

    [Fact]
    public async Task EegDetectionAutomaticallyStopsAfterPassingReading()
    {
        var capability = CreateCapability([1]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability, service);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var acquisitionPoint = model.Points.First(point =>
            point.Role == ElectrodeRole.None && !point.IsStimulus
        );
        model.SelectPointCommand.Execute(acquisitionPoint);

        await model.ToggleDetectionCommand.ExecuteAsync(null);
        service.Publish(new Dictionary<string, double> { [acquisitionPoint.Name] = 7.5d });
        await WaitUntilAsync(() => !model.IsAnyDetecting);

        Assert.Equal(ImpedanceDetectionState.Passed, model.AcquisitionDetectionState);
        Assert.Equal(1, service.EegStopCount);
        Assert.True(model.CanEditConfiguration);
    }

    [Fact]
    public async Task EegDetectionImmediateStartupFailureDoesNotReuseDisposedTokenAndThrottlesRapidToggle()
    {
        var capability = CreateCapability([1]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService
        {
            EegWatchException = new InvalidOperationException(
                "EEG physical channel mapping is missing"
            ),
        };
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability, service);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var acquisitionPoint = model.Points.First(point =>
            point.Role == ElectrodeRole.None && !point.IsStimulus
        );
        model.SelectPointCommand.Execute(acquisitionPoint);

        var firstToggle = model.ToggleDetectionCommand.ExecuteAsync(null);
        var rapidSecondToggle = model.ToggleDetectionCommand.ExecuteAsync(null);
        await Task.WhenAll(firstToggle, rapidSecondToggle);
        await WaitUntilAsync(() => !model.IsAnyDetecting);

        Assert.Equal(1, service.EegWatchCount);
        Assert.Equal(1, service.EegStopCount);
        Assert.Equal(ImpedanceDetectionState.Failed, model.AcquisitionDetectionState);
        Assert.True(model.CanToggleDetection);
    }

    [Fact]
    public async Task StimulusDetectionAutomaticallyStopsWhenNotPassedByConfiguredTimeout()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            service,
            new ImpedanceDetectionOptions
            {
                AutoStopWhenNotPassedTimeout = TimeSpan.FromMilliseconds(50),
            }
        );
        ConfigureStimulusPointsAndChannels(model);

        await model.ToggleDetectionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !model.IsAnyDetecting);

        Assert.Equal(1, service.StimulationStopCount);
        Assert.Equal(ImpedanceDetectionState.Failed, model.StimulusDetectionState);
        Assert.Contains("0.05 秒内未通过", model.DetectionErrorText, StringComparison.Ordinal);
        Assert.True(model.CanEditConfiguration);

        var selectableSites = model
            .Points.Where(point =>
                point.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            .Select(point => point.Name)
            .ToArray();
        service.Publish(selectableSites.ToDictionary(site => site, _ => 8d));
        await Task.Delay(50);
        Assert.Equal(ImpedanceDetectionState.Failed, model.StimulusDetectionState);
    }

    [Fact]
    public async Task EegDetectionAutomaticallyStopsWhenReadingsRemainFailedAtConfiguredTimeout()
    {
        var capability = CreateCapability([1]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            service,
            new ImpedanceDetectionOptions
            {
                AutoStopWhenNotPassedTimeout = TimeSpan.FromMilliseconds(50),
            }
        );
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var acquisitionPoint = model.Points.First(point =>
            point.Role == ElectrodeRole.None && !point.IsStimulus
        );
        model.SelectPointCommand.Execute(acquisitionPoint);

        await model.ToggleDetectionCommand.ExecuteAsync(null);
        service.Publish(new Dictionary<string, double> { [acquisitionPoint.Name] = 24d });
        await WaitUntilAsync(() =>
            model.AcquisitionDetectionState == ImpedanceDetectionState.Failed
        );
        await WaitUntilAsync(() => !model.IsAnyDetecting);

        Assert.Equal(1, service.EegStopCount);
        Assert.Equal(ImpedanceDetectionState.Failed, model.AcquisitionDetectionState);
        Assert.Contains("0.05 秒内未通过", model.DetectionErrorText, StringComparison.Ordinal);
        Assert.True(model.CanEditConfiguration);
    }

    [Fact]
    public async Task AutomaticStopFailureMarksDetectionFailedAndUsesExistingErrorState()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService
        {
            StimulationStopException = new InvalidOperationException("stop failed"),
        };
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability, service);
        ConfigureStimulusPointsAndChannels(model);
        var selectableSites = model
            .Points.Where(point =>
                point.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            .Select(point => point.Name)
            .ToArray();

        await model.ToggleDetectionCommand.ExecuteAsync(null);
        service.Publish(selectableSites.ToDictionary(site => site, _ => 8d));
        await WaitUntilAsync(() => !model.IsAnyDetecting);

        Assert.False(model.IsAnyDetecting);
        Assert.Equal(ImpedanceDetectionState.Failed, model.StimulusDetectionState);
        Assert.Contains("停止阻抗检测失败", model.DetectionErrorText, StringComparison.Ordinal);
        Assert.Equal(1, service.StimulationStopCount);
    }

    [Fact]
    public async Task AcquisitionDetectionStreamsAndUnexpectedFailureUnlocksPage()
    {
        var capability = CreateCapability([1]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            service,
            new ImpedanceDetectionOptions { AutoStopEegWhenPassed = false }
        );
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var acquisitionPoint = model.Points.First(point =>
            point.Role == ElectrodeRole.None && !point.IsStimulus
        );
        model.SelectPointCommand.Execute(acquisitionPoint);
        Assert.True(model.IsAcquisitionConfigurationComplete);

        var referenceRow = model.CurrentImpedanceItems.Single(item =>
            item.Site.Role == ElectrodeRole.Reference
        );
        var groundRow = model.CurrentImpedanceItems.Single(item =>
            item.Site.Role == ElectrodeRole.Ground
        );
        Assert.False(referenceRow.RequiresImpedanceDetection);
        Assert.False(groundRow.RequiresImpedanceDetection);
        Assert.Equal("无需检测", referenceRow.QualityText);
        Assert.Equal("无需检测", groundRow.QualityText);

        await model.ToggleDetectionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => service.LastEegElectrodeIds.Count > 0);
        Assert.Equal(
            model
                .Points.Where(point => point.Role == ElectrodeRole.Acquisition)
                .Select(point => point.Name),
            service.LastEegElectrodeIds
        );
        Assert.DoesNotContain("FCz", service.LastEegElectrodeIds);
        Assert.DoesNotContain("AFz", service.LastEegElectrodeIds);
        service.Publish(new Dictionary<string, double> { [acquisitionPoint.Name] = 7.5d });
        await WaitUntilAsync(() => acquisitionPoint.Impedance == 7.5d);
        service.Fail(new InvalidOperationException("device stream failed"));
        await WaitUntilAsync(() => !model.IsAnyDetecting);

        Assert.Equal(ImpedanceDetectionState.Failed, model.AcquisitionDetectionState);
        Assert.Equal("检测", model.DetectionButtonText);
        Assert.Contains("阻抗检测失败", model.DetectionErrorText, StringComparison.Ordinal);
        Assert.True(model.CanEditConfiguration);
        Assert.Equal(1, service.EegStopCount);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task EegDataTimeoutStopsDeviceBeforeUnlockingWithoutDuplicateCommand(
        bool stopAlreadyRequested,
        int expectedPageStopCount
    )
    {
        var capability = CreateCapability([1]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService();
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability, service);
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var acquisitionPoint = model.Points.First(point =>
            point.Role == ElectrodeRole.None && !point.IsStimulus
        );
        model.SelectPointCommand.Execute(acquisitionPoint);

        await model.ToggleDetectionCommand.ExecuteAsync(null);
        service.TimeoutEeg(stopAlreadyRequested);
        await WaitUntilAsync(() => !model.IsAnyDetecting);

        Assert.Equal(expectedPageStopCount, service.EegStopCount);
        Assert.Equal(ImpedanceDetectionState.Failed, model.AcquisitionDetectionState);
        Assert.Contains("未上传 EEG 阻抗数据", model.DetectionErrorText, StringComparison.Ordinal);
        Assert.True(model.CanEditConfiguration);
    }

    [Theory]
    [InlineData(true, 8d, 8d, true)]
    [InlineData(true, 8d, 24d, true)]
    [InlineData(true, 24d, 8d, true)]
    [InlineData(true, 24d, 24d, true)]
    [InlineData(false, 8d, 8d, true)]
    [InlineData(false, 8d, 24d, false)]
    [InlineData(false, 24d, 8d, false)]
    [InlineData(false, 24d, 24d, false)]
    public void ConfirmationEligibilityHonorsFailedDetectionConfiguration(
        bool allowFailed,
        double stimulationImpedance,
        double eegImpedance,
        bool expected
    )
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            impedanceDetectionOptions: new ImpedanceDetectionOptions
            {
                AllowConfirmationWhenFailed = allowFailed,
            }
        );
        ConfigureStimulusPointsAndChannels(model);
        var stimulationReadings = model
            .Points.Where(point =>
                point.StimulationChannelRole == StimulationChannelRole.Selectable
            )
            .ToDictionary(point => point.Name, _ => stimulationImpedance);
        model.ApplyImpedanceReadings(ElectrodeConfigurationMode.Stimulus, stimulationReadings);

        Assert.False(model.CanOpenConfirmation);

        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var acquisitionPoint = model.Points.First(point =>
            point.Role == ElectrodeRole.None && !point.IsStimulus
        );
        model.SelectPointCommand.Execute(acquisitionPoint);
        model.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Acquisition,
            new Dictionary<string, double> { [acquisitionPoint.Name] = eegImpedance }
        );

        Assert.Equal(expected, model.CanOpenConfirmation);
    }

    [Fact]
    public async Task PreviewDetectionProducesAnotherReadingAfterFiveHundredMilliseconds()
    {
        var service = new PreviewImpedanceDetectionService();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var readings = service
            .WatchEegAsync("PREVIEW", ["F3"], cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var startedAt = DateTime.UtcNow;

        Assert.True(await readings.MoveNextAsync());
        var firstElapsed = DateTime.UtcNow - startedAt;
        Assert.True(await readings.MoveNextAsync());

        Assert.True(firstElapsed >= TimeSpan.FromMilliseconds(400));
        Assert.NotEqual(0d, readings.Current["F3"]);
    }

    [Fact]
    public async Task DisposingDetectingPageCancelsStreamAndStopsDevice()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        var service = new ControlledImpedanceDetectionService();
        var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability, service);
        ConfigureStimulusPointsAndChannels(model);
        await model.ToggleDetectionCommand.ExecuteAsync(null);

        model.Dispose();
        await WaitUntilAsync(() => service.StimulationStopCount == 1);

        Assert.Equal(1, service.StimulationStopCount);
    }

    [Fact]
    public void ConfigurationDetectionSnapshotKeepsBandsAndTimesAfterFurtherDetection()
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        using var model = CreateElectrodePage(stimulus.CreateSnapshot(), capability);
        ConfigureStimulusPointsAndChannels(model);
        var selectable = model.Points.Single(x =>
            x.IsStimulus && x.StimulationChannelRole == StimulationChannelRole.Selectable
        );
        model.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Stimulus,
            new Dictionary<string, double> { [selectable.Name] = 8 }
        );
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        var electrodes = model
            .Points.Where(x => x.Role == ElectrodeRole.None && !x.IsStimulus)
            .Take(3)
            .ToArray();
        foreach (var point in electrodes)
            model.SelectPointCommand.Execute(point);
        var started = DateTimeOffset.UtcNow;
        model.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Acquisition,
            electrodes
                .Select((point, i) => (point.Name, Value: new[] { 8d, 15d, 25d }[i]))
                .ToDictionary(x => x.Name, x => x.Value)
        );
        var snapshot = model.CreatePreRunImpedanceSnapshot();
        Assert.Equal(3, snapshot.Acquisition.Count);
        Assert.Equal(
            new[]
            {
                ImpedanceBand.UpTo10KOhms,
                ImpedanceBand.UpTo20KOhms,
                ImpedanceBand.UpTo30KOhms,
            },
            snapshot.Acquisition.Select(x => x.Band!.Value)
        );
        Assert.InRange(snapshot.AcquisitionMeasuredAt!.Value, started, DateTimeOffset.UtcNow);
        Assert.NotNull(snapshot.StimulationMeasuredAt);
        Assert.Equal(ImpedanceBand.Normal, Assert.Single(snapshot.Stimulation).Band);
        model.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Acquisition,
            electrodes.ToDictionary(x => x.Name, _ => 45d)
        );
        Assert.All(
            model.CreatePreRunImpedanceSnapshot().Acquisition,
            x => Assert.Equal(ImpedanceBand.Above40KOhms, x.Band)
        );
        Assert.Equal(ImpedanceBand.UpTo10KOhms, snapshot.Acquisition[0].Band);
        model.SelectPointCommand.Execute(electrodes[0]);
        model.ClearSelectedPointCommand.Execute(null);
        Assert.Null(model.CreatePreRunImpedanceSnapshot().AcquisitionMeasuredAt);
        Assert.NotNull(snapshot.AcquisitionMeasuredAt);
    }

    private static StimulusModeConfigurationViewModel CreateHdConfiguration(int[] fixedChannels)
    {
        var model = new StimulusModeConfigurationViewModel(
            StimulusKind.TDcs,
            CreateCapability(fixedChannels)
        );
        model.SelectedArrayOption = model.ArrayOptions.Single(option =>
            option.Value is StimulusArrayMode.Hd
        );
        return model;
    }

    private static StimulusCapabilityProfile CreateCapability(int[] fixedChannels) =>
        StimulusCapabilityProfile.FromOptions(
            new StimulationChannelOptions { FixedActivePhysicalChannelIds = fixedChannels }
        );

    [Theory]
    [InlineData(false, 8d, true)]
    [InlineData(false, 24d, false)]
    [InlineData(true, 24d, true)]
    public void SingleStimulusOnlyRequiresStimulationDetection(
        bool allowFailed,
        double impedance,
        bool expected
    )
    {
        var capability = CreateCapability([1, 8]);
        var stimulus = new StimulusModeConfigurationViewModel(StimulusKind.TDcs, capability);
        using var model = CreateElectrodePage(
            stimulus.CreateSnapshot(),
            capability,
            impedanceDetectionOptions: new ImpedanceDetectionOptions
            {
                AllowConfirmationWhenFailed = allowFailed,
            },
            creationMode: ExperimentCreationMode.StimulusOnly
        );
        ConfigureStimulusPointsAndChannels(model);
        Assert.False(model.CanOpenConfirmation);
        model.ApplyImpedanceReadings(
            ElectrodeConfigurationMode.Stimulus,
            model
                .Points.Where(p => p.StimulationChannelRole == StimulationChannelRole.Selectable)
                .ToDictionary(p => p.Name, _ => impedance)
        );
        Assert.Equal(expected, model.CanOpenConfirmation);
        Assert.Equal(ImpedanceDetectionState.NotStarted, model.AcquisitionDetectionState);
        Assert.DoesNotContain(
            model.Points,
            p =>
                p.Role
                    is ElectrodeRole.Acquisition
                        or ElectrodeRole.Reference
                        or ElectrodeRole.Ground
        );
        model.SelectModeCommand.Execute(ElectrodeConfigurationMode.Acquisition);
        Assert.True(model.IsStimulusMode);
    }

    private static ElectrodeConfigurationPageViewModel CreateElectrodePage(
        StimulusConfigurationSnapshot snapshot,
        StimulusCapabilityProfile capability,
        IImpedanceDetectionService? impedanceDetectionService = null,
        ImpedanceDetectionOptions? impedanceDetectionOptions = null,
        ExperimentCreationMode creationMode = ExperimentCreationMode.AcquisitionAndStimulation
    ) =>
        new(
            new ElectrodeConfigurationRouteData(
                "EXP-ROLE",
                "SUBJECT-ROLE",
                snapshot,
                CreationMode: creationMode
            ),
            new NullRouter(),
            new DialogService(() => null),
            new NullDialogProvider(),
            capability,
            impedanceDetectionService ?? new PreviewImpedanceDetectionService(),
            impedanceDetectionOptions
        );

    private static void ConfigureStimulusPointsAndChannels(
        ElectrodeConfigurationPageViewModel model
    )
    {
        var points = new Queue<ElectrodeSiteViewModel>(
            model
                .Points.Where(point => point.CanStimulate)
                .Take(model.RequiredStimulusElectrodeCount)
        );
        foreach (var option in model.StimulusSelectionOptions)
        {
            model.SelectStimulusSelectionOptionCommand.Execute(option);
            for (var count = 0; count < option.RequiredCount; count++)
                model.SelectPointCommand.Execute(points.Dequeue());
        }
        var usedChannels = new HashSet<int>();
        foreach (var row in model.CurrentImpedanceItems)
        {
            row.SelectedStimulationChannel = row.StimulationChannels.First(channel =>
                usedChannels.Add(channel.PhysicalChannelId)
            );
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ControlledImpedanceDetectionService : IImpedanceDetectionService
    {
        private readonly Channel<IReadOnlyDictionary<string, double>> _readings =
            Channel.CreateUnbounded<IReadOnlyDictionary<string, double>>();

        public int EegStopCount { get; private set; }

        public int EegWatchCount { get; private set; }

        public int StimulationStopCount { get; private set; }

        public Exception? EegWatchException { get; init; }

        public Exception? StimulationStopException { get; init; }

        public IReadOnlyList<string> LastEegElectrodeIds { get; private set; } = [];

        public StimulationImpedanceDetectionRequest? LastWatchStimulationRequest
        {
            get;
            private set;
        }

        public StimulationImpedanceDetectionRequest? LastStopStimulationRequest
        {
            get;
            private set;
        }

        public IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchEegAsync(
            string deviceId,
            IReadOnlyList<string> electrodeIds,
            CancellationToken cancellationToken = default
        )
        {
            EegWatchCount++;
            if (EegWatchException is not null)
                throw EegWatchException;
            LastEegElectrodeIds = electrodeIds.ToArray();
            return ReadAllAsync(cancellationToken);
        }

        public Task<IReadOnlyDictionary<string, double>> StartEegAsync(
            string deviceId,
            IReadOnlyList<string> electrodeIds,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());

        public Task StopEegAsync(
            string deviceId,
            IReadOnlyList<string> electrodeIds,
            CancellationToken cancellationToken = default
        )
        {
            EegStopCount++;
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<IReadOnlyDictionary<string, double>> WatchStimulationAsync(
            string deviceId,
            StimulationImpedanceDetectionRequest request,
            CancellationToken cancellationToken = default
        )
        {
            LastWatchStimulationRequest = request;
            return ReadAllAsync(cancellationToken);
        }

        public Task<IReadOnlyDictionary<string, double>> StartStimulationAsync(
            string deviceId,
            StimulationImpedanceDetectionRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());

        public Task StopStimulationAsync(
            string deviceId,
            StimulationImpedanceDetectionRequest request,
            CancellationToken cancellationToken = default
        )
        {
            StimulationStopCount++;
            LastStopStimulationRequest = request;
            return StimulationStopException is null
                ? Task.CompletedTask
                : Task.FromException(StimulationStopException);
        }

        public void Publish(IReadOnlyDictionary<string, double> readings) =>
            _readings.Writer.TryWrite(readings);

        public void Fail(Exception exception) => _readings.Writer.TryComplete(exception);

        public void TimeoutEeg(bool stopCommandRequested) =>
            _readings.Writer.TryComplete(
                new EegImpedanceDataTimeoutException(
                    DeviceImpedanceDetectionService.EegDataTimeout,
                    stopCommandRequested
                )
            );

        private async IAsyncEnumerable<IReadOnlyDictionary<string, double>> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await foreach (var readings in _readings.Reader.ReadAllAsync(cancellationToken))
                yield return readings;
        }
    }

    private sealed class TestStimulusParameterEditor : StimulusParameterEditor
    {
        public bool HasPseudoClass(string name) => PseudoClasses.Contains(name);
    }

    private sealed class TestChannelAllocationEditor : StimulationChannelAllocationEditor
    {
        public bool HasPseudoClass(string name) => PseudoClasses.Contains(name);
    }

    private sealed class NullDialogProvider : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private sealed class NullRouter : INavigationRouter
    {
        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData routeData) { }

        public void Navigate(StimulusConfigurationRouteData routeData) { }

        public void Navigate(ElectrodeConfigurationRouteData routeData) { }

        public void Navigate(ExperimentRunRouteData routeData) { }

        public void Navigate(ExperimentRerunRouteData routeData) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
