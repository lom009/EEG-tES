using System;

namespace EGGtCSPlatform.Services;

public sealed class DeviceHeartbeatOptions
{
    public const string SectionName = "DeviceHeartbeat";

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan DisconnectTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public bool IsValid() =>
        Interval > TimeSpan.Zero
        && ResponseTimeout > TimeSpan.Zero
        && DisconnectTimeout > TimeSpan.Zero
        && Interval < DisconnectTimeout
        && ResponseTimeout < DisconnectTimeout;
}
