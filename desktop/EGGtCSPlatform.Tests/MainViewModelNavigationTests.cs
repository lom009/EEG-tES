using System;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Options;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class MainViewModelNavigationTests
{
    [Fact]
    public void IndexExperimentRecordsCommandTargetsHistorySection()
    {
        var router = new RecordingRouter();
        var model = new IndexViewModel(router, new DeviceSelectionContext());

        model.ShowExperimentRecordsCommand.Execute(null);

        Assert.Same(StartExperimentRouteData.History, router.StartExperimentRoute);
    }

    [Fact]
    public void IndexDisablesConfiguredCapabilitiesAndExplainsWhy()
    {
        var options = new DeviceBackendOptions();
        options.Capabilities.EegAcquisition = DeviceCapabilitySource.Disabled;
        options.Capabilities.Tolerance = DeviceCapabilitySource.Disabled;
        var model = new IndexViewModel(
            new RecordingRouter(),
            new DeviceSelectionContext(),
            new DeviceCapabilityAvailability(options)
        );

        Assert.False(model.StartExperimentCommand.CanExecute(null));
        Assert.Contains("EEG", model.StartExperimentSubtitle);
        Assert.False(model.StartToleranceTestCommand.CanExecute(null));
        Assert.Contains("耐受度", model.ToleranceSubtitle);
    }

    [Fact]
    public void CapabilityRouteGuardBlocksProgrammaticNavigation()
    {
        var options = new DeviceBackendOptions();
        options.Capabilities.Stimulation = DeviceCapabilitySource.Disabled;
        var context = CreateConnectedContext();
        var factory = new PageFactory((page, _) => new TestPage(page));
        var model = new MainViewModel(factory, context, new DeviceCapabilityAvailability(options));

        model.Navigate(ApplicationPageNames.StimulusConfiguration);

        Assert.IsType<IndexViewModel>(model.CurrentView);
        Assert.Contains("电刺激", context.StatusMessage);
    }

    [Fact]
    public void AcquisitionOnlyNavigationAndRerunDoNotRequireStimulationCapabilities()
    {
        var options = new DeviceBackendOptions();
        options.Capabilities.Stimulation = DeviceCapabilitySource.Disabled;
        options.Capabilities.StimulationImpedance = DeviceCapabilitySource.Disabled;
        var factory = new PageFactory((page, _) => new TestPage(page));
        var model = new MainViewModel(
            factory,
            CreateConnectedContext(),
            new DeviceCapabilityAvailability(options)
        );
        Assert.True(Assert.IsType<IndexViewModel>(model.CurrentView).CanStartExperiment);
        model.Navigate(ApplicationPageNames.StartExperiment);
        var basic = Assert.IsType<BasicViewModel>(model.CurrentView);
        model.Navigate(
            new ElectrodeConfigurationRouteData(
                "test",
                "subject",
                AcquisitionOnlyConfiguration.StimulusPlaceholder,
                CreationMode: ExperimentCreationMode.AcquisitionOnly
            )
        );
        Assert.Equal(ApplicationPageNames.ElectrodeConfiguration, basic.CurrentPage.PageName);
        var source = ExperimentRunRouteDataDefaults.Create();
        var route = new ExperimentRunRouteData(
            source.ExperimentId,
            source.SubjectId,
            source.StimulusConfiguration,
            [],
            source.AcquisitionChannels,
            source.ReferenceChannel,
            source.GroundChannel,
            source.SampleRateHz,
            creationMode: ExperimentCreationMode.AcquisitionOnly
        );
        model.Navigate(route);
        var running = basic.CurrentPage;
        model.Navigate(new ExperimentRerunRouteData(route));
        Assert.NotSame(running, basic.CurrentPage);
        Assert.Equal(ApplicationPageNames.ExperimentRun, basic.CurrentPage.PageName);
    }

    [Fact]
    public void GoBackDisposesDiscardedCurrentPageBeforeRestoringPreviousPage()
    {
        var previous = new TestPage(ApplicationPageNames.StartExperiment);
        var current = new DisposableTestPage(ApplicationPageNames.ExperimentRun);
        var factory = new PageFactory(
            (route, _) =>
                route switch
                {
                    ApplicationPageNames.StartExperiment => previous,
                    ApplicationPageNames.ExperimentRun => current,
                    _ => new TestPage(route),
                }
        );
        var model = new MainViewModel(factory, CreateConnectedContext());
        model.Navigate(ApplicationPageNames.StartExperiment);
        model.Navigate(ApplicationPageNames.ExperimentRun);

        model.GoBack();

        Assert.True(current.IsDisposed);
        Assert.Same(previous, Assert.IsType<BasicViewModel>(model.CurrentView).CurrentPage);
    }

    [Fact]
    public void NavigationDirectionTracksForwardAndBackStackMovement()
    {
        var factory = new PageFactory((route, _) => new TestPage(route));
        var model = new MainViewModel(factory, CreateConnectedContext());

        model.Navigate(ApplicationPageNames.StartExperiment);

        Assert.False(model.IsTransitionReversed);
        var basicView = Assert.IsType<BasicViewModel>(model.CurrentView);
        Assert.False(basicView.IsTransitionReversed);

        model.Navigate(ApplicationPageNames.ExperimentRun);
        model.GoBack();

        Assert.True(basicView.IsTransitionReversed);

        model.Navigate(ApplicationPageNames.ElectrodeConfiguration);

        Assert.False(basicView.IsTransitionReversed);
    }

    [Fact]
    public void HomeNavigationReversesOuterTransitionAndNextEntryResetsIt()
    {
        var factory = new PageFactory((route, _) => new TestPage(route));
        var model = new MainViewModel(factory, CreateConnectedContext());
        model.Navigate(ApplicationPageNames.StartExperiment);

        model.GoHome();

        Assert.True(model.IsTransitionReversed);
        Assert.IsType<IndexViewModel>(model.CurrentView);

        model.Navigate(ApplicationPageNames.ExperimentRun);

        Assert.False(model.IsTransitionReversed);
        Assert.IsType<BasicViewModel>(model.CurrentView);
    }

    [Fact]
    public void NavigationAnimationConfigurationIsPropagatedToBothNavigationLayers()
    {
        var factory = new PageFactory((route, _) => new TestPage(route));
        var model = new MainViewModel(
            factory,
            CreateConnectedContext(),
            applicationOptions: Options.Create(
                new ApplicationBehaviorOptions { NavigationAnimationsEnabled = false }
            )
        );

        model.Navigate(ApplicationPageNames.StartExperiment);

        Assert.False(model.IsNavigationAnimationEnabled);
        Assert.False(Assert.IsType<BasicViewModel>(model.CurrentView).IsNavigationAnimationEnabled);
    }

    [Fact]
    public void GoHomeDisposesCurrentPageAndDiscardedBackStack()
    {
        var previous = new DisposableTestPage(ApplicationPageNames.StartExperiment);
        var current = new DisposableTestPage(ApplicationPageNames.ExperimentRun);
        var factory = new PageFactory(
            (route, _) =>
                route switch
                {
                    ApplicationPageNames.StartExperiment => previous,
                    ApplicationPageNames.ExperimentRun => current,
                    _ => new TestPage(route),
                }
        );
        var model = new MainViewModel(factory, CreateConnectedContext());
        model.Navigate(ApplicationPageNames.StartExperiment);
        model.Navigate(ApplicationPageNames.ExperimentRun);

        model.GoHome();

        Assert.True(current.IsDisposed);
        Assert.True(previous.IsDisposed);
    }

    [Fact]
    public void RerunRouteReplacesAndDisposesCurrentResultPage()
    {
        var current = new DisposableTestPage(ApplicationPageNames.ExperimentRun);
        var replacement = new TestPage(ApplicationPageNames.ExperimentRun);
        var createCount = 0;
        var factory = new PageFactory(
            (route, _) =>
                route == ApplicationPageNames.ExperimentRun && createCount++ == 0
                    ? current
                    : replacement
        );
        var model = new MainViewModel(factory, CreateConnectedContext());
        var route = ExperimentRunRouteDataDefaults.Create();
        model.Navigate(route);
        var basicView = Assert.IsType<BasicViewModel>(model.CurrentView);
        basicView.IsTransitionReversed = true;

        model.Navigate(new ExperimentRerunRouteData(route));

        Assert.True(current.IsDisposed);
        Assert.False(basicView.IsTransitionReversed);
        Assert.Same(replacement, basicView.CurrentPage);
    }

    [Fact]
    public void NoOpAndBlockedNavigationDoNotChangeTransitionDirection()
    {
        var options = new DeviceBackendOptions();
        options.Capabilities.Stimulation = DeviceCapabilitySource.Disabled;
        var factory = new PageFactory((route, _) => new TestPage(route));
        var model = new MainViewModel(
            factory,
            CreateConnectedContext(),
            new DeviceCapabilityAvailability(options)
        );

        model.Navigate(ApplicationPageNames.DeviceConnection);
        var basicView = Assert.IsType<BasicViewModel>(model.CurrentView);
        basicView.IsTransitionReversed = true;

        model.Navigate(ApplicationPageNames.DeviceConnection);
        model.Navigate(ApplicationPageNames.StimulusConfiguration);

        Assert.True(basicView.IsTransitionReversed);
        Assert.Equal(ApplicationPageNames.DeviceConnection, basicView.CurrentPage.PageName);
    }

    [Theory]
    [InlineData(ApplicationPageNames.StartExperiment)]
    [InlineData(ApplicationPageNames.ToleranceTest)]
    [InlineData(ApplicationPageNames.StimulusConfiguration)]
    [InlineData(ApplicationPageNames.ElectrodeConfiguration)]
    [InlineData(ApplicationPageNames.ExperimentRun)]
    public void DisconnectedCommunicationRoutesAreRedirectedToDeviceConnection(
        ApplicationPageNames route
    )
    {
        var context = new DeviceSelectionContext();
        var factory = new PageFactory((page, _) => new TestPage(page));
        var model = new MainViewModel(factory, context);

        model.Navigate(route);

        Assert.Equal(
            ApplicationPageNames.DeviceConnection,
            Assert.IsType<BasicViewModel>(model.CurrentView).CurrentPage.PageName
        );
        Assert.Contains("请先", context.StatusMessage);
    }

    [Theory]
    [InlineData(ApplicationPageNames.PhysicalChannelMapping)]
    [InlineData(ApplicationPageNames.DeviceConnection)]
    public void DisconnectedLocalRoutesRemainAvailable(ApplicationPageNames route)
    {
        var context = new DeviceSelectionContext();
        var factory = new PageFactory((page, _) => new TestPage(page));
        var model = new MainViewModel(factory, context);

        model.Navigate(route);

        Assert.Equal(route, Assert.IsType<BasicViewModel>(model.CurrentView).CurrentPage.PageName);
    }

    [Fact]
    public void DisconnectedHistorySectionRemainsAvailable()
    {
        var context = new DeviceSelectionContext();
        object? receivedRouteData = null;
        var factory = new PageFactory(
            (page, routeData) =>
            {
                receivedRouteData = routeData;
                return new TestPage(page);
            }
        );
        var model = new MainViewModel(factory, context);

        model.Navigate(StartExperimentRouteData.History);

        Assert.Equal(
            ApplicationPageNames.StartExperiment,
            Assert.IsType<BasicViewModel>(model.CurrentView).CurrentPage.PageName
        );
        Assert.Same(StartExperimentRouteData.History, receivedRouteData);
    }

    private static DeviceSelectionContext CreateConnectedContext()
    {
        var context = new DeviceSelectionContext();
        context.SetConnectedDevice("simulator-default", "EGG/tCS Simulator");
        return context;
    }

    private class TestPage(ApplicationPageNames pageName)
        : PageViewModel(pageName, pageName.ToString());

    private sealed class DisposableTestPage(ApplicationPageNames pageName)
        : TestPage(pageName),
            IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingRouter : INavigationRouter
    {
        public StartExperimentRouteData? StartExperimentRoute { get; private set; }

        public void Navigate(ApplicationPageNames route) { }

        public void Navigate(StartExperimentRouteData routeData) =>
            StartExperimentRoute = routeData;

        public void Navigate(StimulusConfigurationRouteData routeData) { }

        public void Navigate(ElectrodeConfigurationRouteData routeData) { }

        public void Navigate(ExperimentRunRouteData routeData) { }

        public void Navigate(ExperimentRerunRouteData routeData) { }

        public void GoBack() { }

        public void GoHome() { }
    }
}
