using System;

namespace EGGtCSPlatform.Services;

public sealed class ImpedanceDetectionConfigurationException(string message)
    : InvalidOperationException(message);
