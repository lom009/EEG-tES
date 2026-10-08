using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public interface IDeviceCapabilityAvailability
{
    bool IsEnabled(DeviceCapabilityKind capability);

    DeviceCapabilitySource GetSource(DeviceCapabilityKind capability);

    string GetUnavailableReason(DeviceCapabilityKind capability);

    string GetExperimentUnavailableReason();
}

public static class ExperimentCapabilityAvailability
{
    public static string GetExperimentUnavailableReason(
        this IDeviceCapabilityAvailability availability,
        ExperimentCreationMode mode
    )
    {
        if (mode != ExperimentCreationMode.AcquisitionOnly)
            return availability.GetExperimentUnavailableReason();
        var acquisition = availability.GetUnavailableReason(DeviceCapabilityKind.EegAcquisition);
        return string.IsNullOrEmpty(acquisition)
            ? availability.GetUnavailableReason(DeviceCapabilityKind.EegImpedance)
            : acquisition;
    }

    public static string GetExperimentEntryUnavailableReason(
        this IDeviceCapabilityAvailability availability
    ) =>
        string.IsNullOrEmpty(
            availability.GetExperimentUnavailableReason(ExperimentCreationMode.AcquisitionOnly)
        )
            ? string.Empty
            : availability.GetExperimentUnavailableReason();
}

public sealed class DeviceCapabilityAvailability(DeviceBackendOptions options)
    : IDeviceCapabilityAvailability
{
    private static readonly DeviceCapabilityKind[] RequiredExperimentCapabilities =
    [
        DeviceCapabilityKind.EegAcquisition,
        DeviceCapabilityKind.Stimulation,
        DeviceCapabilityKind.EegImpedance,
        DeviceCapabilityKind.StimulationImpedance,
    ];

    public bool IsEnabled(DeviceCapabilityKind capability) =>
        GetSource(capability) != DeviceCapabilitySource.Disabled;

    public DeviceCapabilitySource GetSource(DeviceCapabilityKind capability) =>
        options.GetSource(capability);

    public string GetUnavailableReason(DeviceCapabilityKind capability) =>
        IsEnabled(capability)
            ? string.Empty
            : $"配置未启用{DisplayName(capability)}能力，请修改 DeviceBackend.Capabilities 后重启软件。";

    public string GetExperimentUnavailableReason()
    {
        var disabled = RequiredExperimentCapabilities
            .Where(capability => !IsEnabled(capability))
            .ToArray();
        return disabled.Length == 0
            ? string.Empty
            : $"配置未启用实验所需能力：{string.Join("、", disabled.Select(DisplayName))}。";
    }

    private static string DisplayName(DeviceCapabilityKind capability) =>
        capability switch
        {
            DeviceCapabilityKind.Status => "设备状态",
            DeviceCapabilityKind.EegAcquisition => "EEG 采集",
            DeviceCapabilityKind.Stimulation => "电刺激",
            DeviceCapabilityKind.EegImpedance => "EEG 阻抗检测",
            DeviceCapabilityKind.StimulationImpedance => "刺激阻抗检测",
            DeviceCapabilityKind.Tolerance => "耐受度测试",
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };
}

internal sealed class AllDeviceCapabilitiesAvailable : IDeviceCapabilityAvailability
{
    public static AllDeviceCapabilitiesAvailable Instance { get; } = new();

    public bool IsEnabled(DeviceCapabilityKind capability) => true;

    public DeviceCapabilitySource GetSource(DeviceCapabilityKind capability) =>
        DeviceCapabilitySource.Simulated;

    public string GetUnavailableReason(DeviceCapabilityKind capability) => string.Empty;

    public string GetExperimentUnavailableReason() => string.Empty;
}
