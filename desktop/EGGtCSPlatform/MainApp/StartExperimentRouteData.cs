namespace EGGtCSPlatform.MainApp;

public enum StartExperimentSection
{
    New = 0,
    History = 1,
}

public sealed record StartExperimentRouteData(StartExperimentSection Section)
{
    public static StartExperimentRouteData NewExperiment { get; } = new(StartExperimentSection.New);

    public static StartExperimentRouteData History { get; } = new(StartExperimentSection.History);
}
