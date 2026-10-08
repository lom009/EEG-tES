using System;

namespace EGGtCSPlatform.Crash;

public record CrashData(
    DateTimeOffset CrashDate,
    string Source,
    string ErrorMessage,
    string StackTrace
);
