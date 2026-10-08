namespace EGGtCSPlatform.Services;

public sealed class SerialLogOptions
{
    public const string SectionName = "SerialLog";

    public bool Enabled { get; set; } = true;

    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    public int RetentionDays { get; set; } = 30;

    public ApplicationLogLevel MinimumLevel { get; set; } = ApplicationLogLevel.Trace;

    public bool IsValid() =>
        MaxFileSizeBytes > 0
        && RetentionDays is > 0 and <= 36500
        && System.Enum.IsDefined(MinimumLevel);
}
