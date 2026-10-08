namespace EGGtCSPlatform.Services;

public sealed class ApplicationBehaviorOptions
{
    public const string SectionName = "Application";

    public bool SingleInstance { get; set; } = true;

    public bool NavigationAnimationsEnabled { get; set; } = true;

    public bool ShowPreviousCrashOnStartup { get; set; }
}
