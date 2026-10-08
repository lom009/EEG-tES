using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;

namespace EGGtCSPlatform.Services;

public sealed class V101RequestTimeoutOptions
{
    public const string SectionName = "V101RequestTimeouts";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    public TimeSpan ReadDeviceStatus { get; set; } = DefaultTimeout;

    public TimeSpan ConfigureEegImpedance { get; set; } = DefaultTimeout;

    public TimeSpan ControlAcquisition { get; set; } = DefaultTimeout;

    public TimeSpan ControlStimulation { get; set; } = DefaultTimeout;

    public TimeSpan AdjustCurrent { get; set; } = DefaultTimeout;

    public TimeSpan ConfigureStimulationImpedance { get; set; } = DefaultTimeout;

    public bool IsValid() => CreateConfiguredTimeouts().Values.All(value => value > TimeSpan.Zero);

    public EggtCsProtocolOptions CreateProtocolOptions(
        Func<string, string?>? environmentValue = null,
        bool requireMatchingResponseIndex = false
    )
    {
        environmentValue ??= Environment.GetEnvironmentVariable;
        var timeouts = CreateConfiguredTimeouts();
        foreach (var (type, variable) in EnvironmentVariables)
        {
            var raw = environmentValue(variable);
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (!int.TryParse(raw, out var milliseconds) || milliseconds <= 0)
                throw new InvalidOperationException(
                    $"{variable} must be a positive millisecond value."
                );
            timeouts[type] = TimeSpan.FromMilliseconds(milliseconds);
        }
        return new EggtCsProtocolOptions(timeouts, requireMatchingResponseIndex);
    }

    private Dictionary<Type, TimeSpan> CreateConfiguredTimeouts() =>
        new()
        {
            [typeof(ReadDeviceStatusRequest)] = ReadDeviceStatus,
            [typeof(ConfigureEegImpedanceRequest)] = ConfigureEegImpedance,
            [typeof(ControlAcquisitionRequest)] = ControlAcquisition,
            [typeof(ControlStimulationRequest)] = ControlStimulation,
            [typeof(AdjustCurrentRequest)] = AdjustCurrent,
            [typeof(ConfigureStimulationImpedanceRequest)] = ConfigureStimulationImpedance,
        };

    private static IReadOnlyDictionary<Type, string> EnvironmentVariables { get; } =
        new Dictionary<Type, string>
        {
            [typeof(ReadDeviceStatusRequest)] = "EGGTCS_V101_TIMEOUT_STATUS_MS",
            [typeof(ConfigureEegImpedanceRequest)] = "EGGTCS_V101_TIMEOUT_EEG_IMPEDANCE_MS",
            [typeof(ControlAcquisitionRequest)] = "EGGTCS_V101_TIMEOUT_ACQUISITION_MS",
            [typeof(ControlStimulationRequest)] = "EGGTCS_V101_TIMEOUT_STIMULATION_MS",
            [typeof(AdjustCurrentRequest)] = "EGGTCS_V101_TIMEOUT_CURRENT_MS",
            [typeof(ConfigureStimulationImpedanceRequest)] =
                "EGGTCS_V101_TIMEOUT_STIMULATION_CONFIG_MS",
        };
}
