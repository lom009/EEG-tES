using Avalonia;

namespace EGGtCSPlatform.VisualTests;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VisualTestOptions.Current = VisualTestOptions.Parse(args);
        BuildAvaloniaApp(VisualTestOptions.Current.Scenario).StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp(string scenario) => (
            scenario.Equals("envelope-training", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<EnvelopeTrainingVisualApp>()
            : scenario.Equals("home-motion", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<HomeMotionVisualApp>()
            : scenario.Equals("login", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<LoginVisualApp>()
            : scenario.Equals("dialog-adorner", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<DialogAdornerVisualApp>()
            : scenario.Equals("envelope", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<EnvelopeVisualApp>()
            : scenario.Equals("acquisition-only", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<AcquisitionOnlyVisualApp>()
            : scenario.StartsWith("single-stimulus", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<SingleStimulusVisualApp>()
            : scenario.Equals("window-disposal", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<WindowDisposalVisualApp>()
            : scenario.StartsWith("simulation-generator", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<SimulationGeneratorVisualApp>()
            : scenario.StartsWith("start-experiment-history", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<StartExperimentHistoryVisualApp>()
            : scenario.StartsWith("stimulus-configuration", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<StimulusConfigurationVisualApp>()
            : scenario.StartsWith("historical-results", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<HistoricalResultsVisualApp>()
            : scenario.StartsWith("eeg-mapping", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<EegMappingVisualApp>()
            : scenario.StartsWith("electrode-configuration", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<ElectrodeConfigurationVisualApp>()
            : scenario.StartsWith("device-connection", StringComparison.OrdinalIgnoreCase)
                ? AppBuilder.Configure<DeviceConnectionVisualApp>()
            : AppBuilder.Configure<VisualTestApp>()
        ).UsePlatformDetect().WithInterFont().LogToTrace();
}

internal sealed record VisualTestOptions(
    string Scenario,
    string AutomaticSetupOutputPath,
    string TimingSetupOutputPath,
    string AcquisitionOutputPath,
    string StimulationOutputPath,
    string CompletedOutputPath,
    string ResultsOutputPath,
    string HistoricalResultsOutputPath,
    string DeviceConnectionDialogOutputPath,
    string DeviceConnectionConnectedOutputPath,
    string ErrorPath,
    TimeSpan Timeout
)
{
    public static VisualTestOptions Current { get; set; } = Parse([]);

    public static VisualTestOptions Parse(string[] args)
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        var outputDirectory = Path.Combine(repositoryRoot, "TestArtifacts");
        string? compatibleOutputPath = null;
        var scenario = "experiment-run";
        var timeout = TimeSpan.FromSeconds(15);

        foreach (var argument in args)
        {
            if (argument.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
                compatibleOutputPath = Path.GetFullPath(argument["--output=".Length..].Trim('"'));
            else if (argument.StartsWith("--scenario=", StringComparison.OrdinalIgnoreCase))
                scenario = argument["--scenario=".Length..].Trim('"');
            else if (argument.StartsWith("--output-directory=", StringComparison.OrdinalIgnoreCase))
                outputDirectory = Path.GetFullPath(
                    argument["--output-directory=".Length..].Trim('"')
                );
            else if (
                argument.StartsWith("--timeout-seconds=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(argument["--timeout-seconds=".Length..], out var seconds)
                && seconds > 0
            )
                timeout = TimeSpan.FromSeconds(seconds);
        }

        var acquisitionOutputPath =
            compatibleOutputPath
            ?? Path.Combine(outputDirectory, "ExperimentRunPage.acquisition-hover.png");
        outputDirectory = Path.GetDirectoryName(acquisitionOutputPath)!;
        var acquisitionStem = Path.GetFileNameWithoutExtension(acquisitionOutputPath);
        var timingSetupStem = acquisitionStem.Contains(
            "acquisition-hover",
            StringComparison.OrdinalIgnoreCase
        )
            ? acquisitionStem.Replace(
                "acquisition-hover",
                "timing-setup",
                StringComparison.OrdinalIgnoreCase
            )
            : $"{acquisitionStem}.timing-setup";
        var automaticSetupStem = acquisitionStem.Contains(
            "acquisition-hover",
            StringComparison.OrdinalIgnoreCase
        )
            ? acquisitionStem.Replace(
                "acquisition-hover",
                "automatic-setup",
                StringComparison.OrdinalIgnoreCase
            )
            : $"{acquisitionStem}.automatic-setup";
        var stimulationStem = acquisitionStem.Contains(
            "acquisition-hover",
            StringComparison.OrdinalIgnoreCase
        )
            ? acquisitionStem.Replace(
                "acquisition-hover",
                "stimulation-frozen",
                StringComparison.OrdinalIgnoreCase
            )
            : $"{acquisitionStem}.stimulation-frozen";
        var completedStem = acquisitionStem.Contains(
            "acquisition-hover",
            StringComparison.OrdinalIgnoreCase
        )
            ? acquisitionStem.Replace(
                "acquisition-hover",
                "completed",
                StringComparison.OrdinalIgnoreCase
            )
            : $"{acquisitionStem}.completed";
        var resultsStem = acquisitionStem.Contains(
            "acquisition-hover",
            StringComparison.OrdinalIgnoreCase
        )
            ? acquisitionStem.Replace(
                "acquisition-hover",
                "results",
                StringComparison.OrdinalIgnoreCase
            )
            : $"{acquisitionStem}.results";
        var automaticSetupOutputPath = Path.Combine(outputDirectory, $"{automaticSetupStem}.png");
        var timingSetupOutputPath = Path.Combine(outputDirectory, $"{timingSetupStem}.png");
        var stimulationOutputPath = Path.Combine(outputDirectory, $"{stimulationStem}.png");
        var completedOutputPath = Path.Combine(outputDirectory, $"{completedStem}.png");
        var resultsOutputPath = Path.Combine(outputDirectory, $"{resultsStem}.png");
        var historicalResultsOutputPath = Path.Combine(
            outputDirectory,
            "ExperimentRunPage.historical-results.png"
        );
        var deviceConnectionDialogOutputPath = Path.Combine(
            outputDirectory,
            "DeviceConnectionPage.dialog.png"
        );
        var deviceConnectionConnectedOutputPath = Path.Combine(
            outputDirectory,
            "DeviceConnectionPage.connected.png"
        );
        var errorPath = Path.Combine(outputDirectory, "ExperimentRunPage.visual.error.txt");
        return new VisualTestOptions(
            scenario,
            automaticSetupOutputPath,
            timingSetupOutputPath,
            acquisitionOutputPath,
            stimulationOutputPath,
            completedOutputPath,
            resultsOutputPath,
            historicalResultsOutputPath,
            deviceConnectionDialogOutputPath,
            deviceConnectionConnectedOutputPath,
            errorPath,
            timeout
        );
    }
}
