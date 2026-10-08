using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.ViewModels.Pages;

public partial class MainViewModel : ViewModelBase, IDialogProvider, INavigationRouter
{
    private readonly PageFactory _pageFactory;
    private readonly IDeviceSelectionContext _deviceConnection;
    private readonly IDeviceCapabilityAvailability _capabilityAvailability;
    private readonly IndexViewModel _indexViewModel;
    private readonly Stack<PageViewModel> _backStack = new();

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private bool _isTransitionReversed;

    public MainViewModel(
        PageFactory pageFactory,
        IDeviceSelectionContext deviceConnection,
        IDeviceCapabilityAvailability? capabilityAvailability = null,
        IOptions<ApplicationBehaviorOptions>? applicationOptions = null
    )
    {
        _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
        _deviceConnection =
            deviceConnection ?? throw new ArgumentNullException(nameof(deviceConnection));
        _capabilityAvailability = capabilityAvailability ?? AllDeviceCapabilitiesAvailable.Instance;
        IsNavigationAnimationEnabled =
            applicationOptions?.Value.NavigationAnimationsEnabled ?? true;
        _indexViewModel = new IndexViewModel(this, _deviceConnection, _capabilityAvailability);
        _currentView = _indexViewModel;
    }

    public bool IsNavigationAnimationEnabled { get; }

    public System.Collections.ObjectModel.ObservableCollection<DialogViewModel> DialogStack { get; } =
    [];

    public void Navigate(ApplicationPageNames route) => Navigate(route, null);

    public void Navigate(StartExperimentRouteData routeData) =>
        Navigate(ApplicationPageNames.StartExperiment, routeData);

    public void Navigate(StimulusConfigurationRouteData routeData) =>
        Navigate(ApplicationPageNames.StimulusConfiguration, routeData);

    public void Navigate(ElectrodeConfigurationRouteData routeData) =>
        Navigate(ApplicationPageNames.ElectrodeConfiguration, routeData);

    public void Navigate(ExperimentRunRouteData routeData) =>
        Navigate(ApplicationPageNames.ExperimentRun, routeData);

    public void Navigate(EnvelopeStimulationRouteData routeData) =>
        Navigate(ApplicationPageNames.EnvelopeStimulation, routeData);

    public void Navigate(ExperimentRerunRouteData routeData)
    {
        ArgumentNullException.ThrowIfNull(routeData);
        var capabilityError = _capabilityAvailability.GetExperimentUnavailableReason(
            routeData.RouteData.CreationMode
        );
        if (!string.IsNullOrEmpty(capabilityError))
        {
            _deviceConnection.StatusMessage = capabilityError;
            return;
        }
        if (!_deviceConnection.IsConnected)
            return;
        if (CurrentView is not BasicViewModel basicView)
        {
            Navigate(routeData.RouteData);
            return;
        }

        var previousPage = basicView.CurrentPage;
        var replacement = _pageFactory.GetPageViewModel(
            ApplicationPageNames.ExperimentRun,
            routeData.RouteData
        );
        basicView.IsTransitionReversed = false;
        basicView.CurrentPage = replacement;
        DisposePage(previousPage);
    }

    private void Navigate(ApplicationPageNames route, object? routeData)
    {
        var isHistoricalResult = routeData is ExperimentRunRouteData { HistoricalResult: not null };
        var isHistorySection =
            routeData is StartExperimentRouteData { Section: StartExperimentSection.History };
        var creationMode = routeData switch
        {
            ElectrodeConfigurationRouteData electrode => electrode.CreationMode,
            StimulusConfigurationRouteData stimulus => stimulus.CreationMode,
            ExperimentRunRouteData run => run.CreationMode,
            EnvelopeStimulationRouteData envelope => envelope.Configuration.CreationMode,
            _ => ExperimentCreationMode.AcquisitionAndStimulation,
        };
        var capabilityError = GetCapabilityNavigationError(
            route,
            isHistoricalResult,
            isHistorySection,
            creationMode
        );
        if (!string.IsNullOrEmpty(capabilityError))
        {
            _deviceConnection.StatusMessage = capabilityError;
            return;
        }
        if (
            RequiresDeviceConnection(route)
            && !isHistoricalResult
            && !isHistorySection
            && !_deviceConnection.IsConnected
        )
        {
            _deviceConnection.StatusMessage = "请先进入配置并连接设备，再使用通信相关功能";
            route = ApplicationPageNames.DeviceConnection;
            routeData = null;
        }

        if (CurrentView is BasicViewModel basicView)
        {
            if (basicView.CurrentPage.PageName == route)
            {
                if (
                    basicView.CurrentPage is StartExperimentPageViewModel startExperiment
                    && routeData is StartExperimentRouteData startRouteData
                )
                    startExperiment.ApplyRoute(startRouteData);
                return;
            }

            basicView.IsTransitionReversed = false;
            _backStack.Push(basicView.CurrentPage);
            basicView.CurrentPage = _pageFactory.GetPageViewModel(route, routeData);
            return;
        }

        _backStack.Clear();
        IsTransitionReversed = false;
        CurrentView = new BasicViewModel(
            router: this,
            currentPage: _pageFactory.GetPageViewModel(route, routeData),
            deviceConnection: _deviceConnection,
            isNavigationAnimationEnabled: IsNavigationAnimationEnabled
        );
    }

    private string GetCapabilityNavigationError(
        ApplicationPageNames route,
        bool isHistoricalResult,
        bool isHistorySection,
        ExperimentCreationMode creationMode
    )
    {
        if (route == ApplicationPageNames.ToleranceTest)
            return _capabilityAvailability.GetUnavailableReason(DeviceCapabilityKind.Tolerance);
        if (
            !isHistoricalResult
            && !isHistorySection
            && route
                is ApplicationPageNames.StartExperiment
                    or ApplicationPageNames.StimulusConfiguration
                    or ApplicationPageNames.ElectrodeConfiguration
                    or ApplicationPageNames.ExperimentRun
                    or ApplicationPageNames.EnvelopeStimulation
        )
        {
            return route == ApplicationPageNames.StartExperiment
                ? _capabilityAvailability.GetExperimentEntryUnavailableReason()
                : _capabilityAvailability.GetExperimentUnavailableReason(creationMode);
        }
        return string.Empty;
    }

    private static bool RequiresDeviceConnection(ApplicationPageNames route) =>
        route
            is ApplicationPageNames.StartExperiment
                or ApplicationPageNames.ToleranceTest
                or ApplicationPageNames.StimulusConfiguration
                or ApplicationPageNames.ElectrodeConfiguration
                or ApplicationPageNames.ExperimentRun
                or ApplicationPageNames.EnvelopeStimulation;

    public void GoBack()
    {
        if (CurrentView is not BasicViewModel basicView)
            return;

        if (!basicView.CurrentPage.CanGoBack)
            return;

        if (_backStack.TryPop(out var previousPage))
        {
            basicView.IsTransitionReversed = true;
            DisposePage(basicView.CurrentPage);
            basicView.CurrentPage = previousPage;
            return;
        }

        GoHome();
    }

    public void GoHome()
    {
        if (CurrentView is not BasicViewModel basicView)
            return;

        IsTransitionReversed = true;
        DisposePage(basicView.CurrentPage);
        while (_backStack.TryPop(out var page))
            DisposePage(page);
        CurrentView = _indexViewModel;
    }

    private static void DisposePage(PageViewModel page)
    {
        if (page is IDisposable disposable)
            disposable.Dispose();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DismissTopDialog()
    {
        if (DialogStack.Count > 0)
            DialogStack[^1].OverlayDismissCommand.Execute(null);
    }
}
