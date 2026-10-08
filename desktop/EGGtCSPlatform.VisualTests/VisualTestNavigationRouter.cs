using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;

namespace EGGtCSPlatform.VisualTests;

internal sealed class VisualTestNavigationRouter : INavigationRouter
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
