using EGGtCSPlatform.MainApp;

namespace EGGtCSPlatform.Interfaces;

public interface INavigationRouter
{
    void Navigate(ApplicationPageNames route);

    void Navigate(StartExperimentRouteData routeData);

    void Navigate(StimulusConfigurationRouteData routeData);

    void Navigate(ElectrodeConfigurationRouteData routeData);

    void Navigate(ExperimentRunRouteData routeData);

    void Navigate(EnvelopeStimulationRouteData routeData) =>
        throw new System.NotSupportedException("包络-tACS 页面未注册。");

    void Navigate(ExperimentRerunRouteData routeData);

    void GoBack();

    void GoHome();
}
