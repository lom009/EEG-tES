using System.Collections.ObjectModel;
using System.Threading.Tasks;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class StimulusConfigurationNavigationTests
{
    [Theory]
    [InlineData(ExperimentCreationMode.StimulusOnly, "单刺激模式")]
    [InlineData(ExperimentCreationMode.AcquisitionOnly, "单采集模式")]
    [InlineData(ExperimentCreationMode.AcquisitionAndStimulation, "采集-刺激模式")]
    public void HeaderBadgesShowExperimentModeAsSeparateValue(
        ExperimentCreationMode creationMode,
        string expectedModeText
    )
    {
        var model = new StimulusConfigurationPageViewModel(
            new StimulusConfigurationRouteData(
                "EXP-TEST",
                "SUBJECT-TEST",
                CreationMode: creationMode
            ),
            new NullNavigationRouter(),
            new DialogService(() => null),
            new NullDialogProvider()
        );

        Assert.Collection(
            model.HeaderBadges,
            badge => Assert.Equal("患者ID：SUBJECT-TEST", badge.DisplayText),
            badge => Assert.Equal("实验ID：EXP-TEST", badge.DisplayText),
            badge =>
            {
                Assert.Equal("模式", badge.Label);
                Assert.Equal(expectedModeText, badge.Value);
                Assert.Equal(expectedModeText, badge.DisplayText);
            }
        );
    }

    [Fact]
    public async Task SavingSingleStimulusConfigurationPreservesMode()
    {
        var router = new NullNavigationRouter();
        var model = new StimulusConfigurationPageViewModel(
            new StimulusConfigurationRouteData(
                "single",
                "subject",
                CreationMode: ExperimentCreationMode.StimulusOnly
            ),
            router,
            new DialogService(() => null),
            new NullDialogProvider(),
            StimulusCapabilityProfile.FromOptions(
                new StimulationChannelOptions { FixedActivePhysicalChannelIds = [1] }
            )
        );
        Assert.True(model.CanSave);
        await model.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.Equal(ExperimentCreationMode.StimulusOnly, router.ElectrodeRoute!.CreationMode);
    }

    [Fact]
    public void InitialModeStartsAtFirstIndicatorPosition()
    {
        var model = CreateModel();

        Assert.Equal(StimulusKind.TDcs, model.CurrentConfiguration.Kind);
        Assert.Equal(0, model.SelectedModeIndex);
        Assert.Equal(0d, model.SelectionIndicatorOffset);
        Assert.False(model.IsModeTransitionReversed);
        Assert.True(model.Modes[0].IsSelected);
    }

    [Fact]
    public void SelectingModesTracksIndicatorOffsetAndTransitionDirection()
    {
        var model = CreateModel();

        model.Modes[3].SelectCommand.Execute(null);

        Assert.Equal(StimulusKind.TPcs, model.CurrentConfiguration.Kind);
        Assert.Equal(3, model.SelectedModeIndex);
        Assert.Equal(186d, model.SelectionIndicatorOffset);
        Assert.False(model.IsModeTransitionReversed);

        model.Modes[1].SelectCommand.Execute(null);

        Assert.Equal(StimulusKind.TAcs, model.CurrentConfiguration.Kind);
        Assert.Equal(1, model.SelectedModeIndex);
        Assert.Equal(62d, model.SelectionIndicatorOffset);
        Assert.True(model.IsModeTransitionReversed);

        model.Modes[4].SelectCommand.Execute(null);

        Assert.Equal(StimulusKind.Sham, model.CurrentConfiguration.Kind);
        Assert.Equal(4, model.SelectedModeIndex);
        Assert.Equal(248d, model.SelectionIndicatorOffset);
        Assert.False(model.IsModeTransitionReversed);
    }

    [Fact]
    public void SelectingCurrentModeDoesNotReplaceContentOrChangeDirection()
    {
        var model = CreateModel();
        model.Modes[3].SelectCommand.Execute(null);
        model.Modes[1].SelectCommand.Execute(null);
        var current = model.CurrentConfiguration;
        var currentChanges = 0;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.CurrentConfiguration))
                currentChanges++;
        };

        model.Modes[1].SelectCommand.Execute(null);

        Assert.Same(current, model.CurrentConfiguration);
        Assert.Equal(0, currentChanges);
        Assert.True(model.IsModeTransitionReversed);
    }

    [Fact]
    public void SwitchingModesPreservesEachModesConfigurationObject()
    {
        var model = CreateModel();
        model.Modes[1].SelectCommand.Execute(null);
        var alternating = model.CurrentConfiguration;
        alternating.Frequency = 77.7d;

        model.Modes[4].SelectCommand.Execute(null);
        model.Modes[1].SelectCommand.Execute(null);

        Assert.Same(alternating, model.CurrentConfiguration);
        Assert.Equal(77.7d, model.CurrentConfiguration.Frequency);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingApplicationSettingControlsModeAnimations(bool enabled)
    {
        var model = CreateModel(
            new ApplicationBehaviorOptions { NavigationAnimationsEnabled = enabled }
        );

        Assert.Equal(enabled, model.IsNavigationAnimationEnabled);
    }

    private static StimulusConfigurationPageViewModel CreateModel(
        ApplicationBehaviorOptions? applicationOptions = null
    ) =>
        new(
            new StimulusConfigurationRouteData("EXP-TEST", "SUBJECT-TEST"),
            new NullNavigationRouter(),
            new DialogService(() => null),
            new NullDialogProvider(),
            applicationOptions: applicationOptions
        );

    private sealed class NullDialogProvider : IDialogProvider
    {
        public ObservableCollection<DialogViewModel> DialogStack { get; } = [];
    }

    private sealed class NullNavigationRouter : INavigationRouter
    {
        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData routeData) { }

        public void Navigate(StimulusConfigurationRouteData routeData) { }

        public ElectrodeConfigurationRouteData? ElectrodeRoute;

        public void Navigate(ElectrodeConfigurationRouteData routeData)
        {
            ElectrodeRoute = routeData;
        }

        public void Navigate(ExperimentRunRouteData routeData) { }

        public void Navigate(ExperimentRerunRouteData routeData) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
