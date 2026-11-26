using EGGtCSPlatform.MainApp;

namespace EGGtCSPlatform.Interfaces;

public interface INavigationRouter
{
    void UpdateExperimentIdentity(
        long previousId,
        long id,
        string code,
        EGGtCSPlatform.MainApp.ExperimentConfigurationTemplate? configuration = null
    ) { }
    void Navigate(ApplicationPageNames route);

    void Navigate(StartExperimentRouteData routeData);

    void Navigate(StimulusConfigurationRouteData routeData);

    void Navigate(ElectrodeConfigurationRouteData routeData);

    void Navigate(ToleranceTestRouteData routeData) =>
        throw new System.NotSupportedException("耐受度调试页面未注册。");

    void Navigate(ExperimentRunRouteData routeData);

    void NavigatePictureNaming(ExperimentRunRouteData routeData) =>
        throw new System.NotSupportedException("图片命名页面未注册。");

    void Navigate(EnvelopeStimulationRouteData routeData) =>
        throw new System.NotSupportedException("包络-tACS 页面未注册。");

    void Navigate(SpeechTrainingResultsRouteData routeData) =>
        throw new System.NotSupportedException("言语训练结果页面未注册。");

    void Navigate(SpeechExperimentResultsRouteData routeData) =>
        throw new System.NotSupportedException("言语实验结果页面未注册。");

    void Navigate(ExperimentRerunRouteData routeData);

    void GoBack();

    void GoHome();
}
