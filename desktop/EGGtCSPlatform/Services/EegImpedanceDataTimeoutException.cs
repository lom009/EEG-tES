using System;

namespace EGGtCSPlatform.Services;

public sealed class EegImpedanceDataTimeoutException(
    TimeSpan timeout,
    bool stopCommandRequested = false
) : TimeoutException($"The device did not upload valid EEG impedance data within {timeout}.")
{
    public TimeSpan Timeout { get; } = timeout;

    public bool StopCommandRequested { get; } = stopCommandRequested;
}
