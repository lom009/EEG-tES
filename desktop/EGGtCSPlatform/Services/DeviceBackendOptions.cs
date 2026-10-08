using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Services;

public sealed class DeviceBackendOptions
{
    public const string SectionName = "DeviceBackend";

    public DeviceConnectionSource ConnectionSource { get; set; } = DeviceConnectionSource.Real;

    public DeviceCapabilitySourceOptions Capabilities { get; set; } = new();

    public DeviceRuntime.DeviceBackendProfile ToRuntimeProfile() =>
        new()
        {
            ConnectionSource = ConnectionSource,
            Sources = Enum.GetValues<DeviceCapabilityKind>().ToDictionary(kind => kind, GetSource),
        };

    public DeviceCapabilitySource GetSource(DeviceCapabilityKind capability) =>
        capability switch
        {
            DeviceCapabilityKind.Status => Capabilities.Status,
            DeviceCapabilityKind.EegAcquisition => Capabilities.EegAcquisition,
            DeviceCapabilityKind.Stimulation => Capabilities.Stimulation,
            DeviceCapabilityKind.EegImpedance => Capabilities.EegImpedance,
            DeviceCapabilityKind.StimulationImpedance => Capabilities.StimulationImpedance,
            DeviceCapabilityKind.Tolerance => Capabilities.Tolerance,
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    public IReadOnlyList<DeviceCapabilityKind> DisabledCapabilities() =>
        Enum.GetValues<DeviceCapabilityKind>()
            .Where(capability => GetSource(capability) == DeviceCapabilitySource.Disabled)
            .ToArray();
}

public sealed class DeviceCapabilitySourceOptions
{
    public DeviceCapabilitySource Status { get; set; } = DeviceCapabilitySource.Real;

    public DeviceCapabilitySource EegAcquisition { get; set; } = DeviceCapabilitySource.Real;

    public DeviceCapabilitySource Stimulation { get; set; } = DeviceCapabilitySource.Real;

    public DeviceCapabilitySource EegImpedance { get; set; } = DeviceCapabilitySource.Real;

    public DeviceCapabilitySource StimulationImpedance { get; set; } = DeviceCapabilitySource.Real;

    public DeviceCapabilitySource Tolerance { get; set; } = DeviceCapabilitySource.Simulated;
}

public sealed class DeviceBackendOptionsValidator : IValidateOptions<DeviceBackendOptions>
{
    public ValidateOptionsResult Validate(string? name, DeviceBackendOptions options)
    {
        if (!Enum.IsDefined(options.ConnectionSource))
            return ValidateOptionsResult.Fail(
                "DeviceBackend.ConnectionSource must be Real or Simulated."
            );
        if (options.Capabilities is null)
            return ValidateOptionsResult.Fail("DeviceBackend.Capabilities is required.");

        var invalid = Enum.GetValues<DeviceCapabilityKind>()
            .Where(capability => !Enum.IsDefined(options.GetSource(capability)))
            .ToArray();
        if (invalid.Length > 0)
            return ValidateOptionsResult.Fail(
                $"DeviceBackend capability source is invalid: {string.Join(", ", invalid)}."
            );
        if (options.Capabilities.Status == DeviceCapabilitySource.Disabled)
            return ValidateOptionsResult.Fail(
                "DeviceBackend.Capabilities.Status cannot be Disabled because it validates connections."
            );

        if (options.ConnectionSource == DeviceConnectionSource.Simulated)
        {
            var realCapabilities = Enum.GetValues<DeviceCapabilityKind>()
                .Where(capability => options.GetSource(capability) == DeviceCapabilitySource.Real)
                .ToArray();
            if (realCapabilities.Length > 0)
            {
                return ValidateOptionsResult.Fail(
                    "Real capabilities require DeviceBackend.ConnectionSource=Real: "
                        + string.Join(", ", realCapabilities)
                );
            }
        }

        return ValidateOptionsResult.Success;
    }
}
