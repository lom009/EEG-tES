using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using EGGtCSPlatform.DeviceSdk;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Services;

public sealed class DeviceAutoConnectionOptions
{
    public const string SectionName = "DeviceAutoConnection";

    public bool Enabled { get; set; } = true;
    public int DefaultDevicePort { get; set; } = 30307;
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(2);
    public BroadcastDiscoveryModeOptions GlobalBroadcast { get; set; } =
        new() { Order = 10, Address = "255.255.255.255" };
    public BroadcastDiscoveryModeOptions LocalBroadcast { get; set; } =
        new() { Order = 20, Address = string.Empty };
    public SubnetUnicastDiscoveryModeOptions SubnetUnicast { get; set; } = new() { Order = 30 };
    public SavedDeviceConnectionProfile? LastDevice { get; set; }

    public IReadOnlyList<DeviceDiscoveryModeDefinition> GetEnabledModes() =>
        new DeviceDiscoveryModeDefinition[]
        {
            new(
                DeviceDiscoveryModeKind.GlobalBroadcast,
                "全广播",
                GlobalBroadcast.Order,
                GlobalBroadcast.ResponseTimeout
            ),
            new(
                DeviceDiscoveryModeKind.LocalBroadcast,
                "本地广播",
                LocalBroadcast.Order,
                LocalBroadcast.ResponseTimeout
            ),
            new(
                DeviceDiscoveryModeKind.SubnetUnicast,
                "子网单播",
                SubnetUnicast.Order,
                SubnetUnicast.ResponseTimeout
            ),
        }
            .Where(mode =>
                mode.Kind switch
                {
                    DeviceDiscoveryModeKind.GlobalBroadcast => GlobalBroadcast.Enabled,
                    DeviceDiscoveryModeKind.LocalBroadcast => LocalBroadcast.Enabled,
                    _ => SubnetUnicast.Enabled,
                }
            )
            .OrderBy(mode => mode.Order)
            .ToArray();

    public bool IsValid() => GetValidationErrors().Count == 0;

    public IReadOnlyList<string> GetValidationErrors(bool validateNetworkModes = true)
    {
        var errors = new List<string>();
        var modes = GetEnabledModes();
        if (DefaultDevicePort is < 1 or > ushort.MaxValue)
            errors.Add("DeviceAutoConnection.DefaultDevicePort 必须介于 1 和 65535 之间。");
        if (RetryInterval <= TimeSpan.Zero)
            errors.Add("DeviceAutoConnection.RetryInterval 必须为正值。");
        if (validateNetworkModes && modes.Count == 0)
            errors.Add("DeviceAutoConnection 至少需要启用一种设备发现模式。");
        foreach (var mode in validateNetworkModes ? modes : [])
        {
            if (mode.Order <= 0)
                errors.Add($"{mode.DisplayName}的 Order 必须是正整数。");
            if (mode.ResponseTimeout <= TimeSpan.Zero)
                errors.Add($"{mode.DisplayName}的 ResponseTimeout 必须为正值。");
        }
        foreach (
            var conflict in (validateNetworkModes ? modes : [])
                .GroupBy(mode => mode.Order)
                .Where(group => group.Count() > 1)
        )
        {
            errors.Add(
                $"已启用的设备发现模式 Order={conflict.Key} 冲突：{string.Join("、", conflict.Select(mode => mode.DisplayName))}。"
            );
        }
        if (
            validateNetworkModes
            && GlobalBroadcast.Enabled
            && !IsOptionalIpv4(GlobalBroadcast.Address, allowEmpty: false)
        )
            errors.Add("GlobalBroadcast.Address 必须是有效的 IPv4 地址。");
        if (
            validateNetworkModes
            && LocalBroadcast.Enabled
            && !IsOptionalIpv4(LocalBroadcast.Address, allowEmpty: true)
        )
            errors.Add("LocalBroadcast.Address 必须为空或有效的 IPv4 地址。");
        if (
            validateNetworkModes
            && SubnetUnicast.Enabled
            && SubnetUnicast.MaximumHostCount is < 1 or > 254
        )
            errors.Add("SubnetUnicast.MaximumHostCount 必须介于 1 和 254 之间。");
        return errors;
    }

    private static bool IsOptionalIpv4(string value, bool allowEmpty) =>
        allowEmpty && string.IsNullOrWhiteSpace(value)
        || IPAddress.TryParse(value, out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
}

public class DeviceDiscoveryModeOptions
{
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
}

public sealed class BroadcastDiscoveryModeOptions : DeviceDiscoveryModeOptions
{
    public string Address { get; set; } = string.Empty;
}

public sealed class SubnetUnicastDiscoveryModeOptions : DeviceDiscoveryModeOptions
{
    public int MaximumHostCount { get; set; } = 254;
}

public enum DeviceDiscoveryModeKind
{
    GlobalBroadcast,
    LocalBroadcast,
    SubnetUnicast,
    Simulator,
}

public interface IDeviceDiscoveryMode
{
    DeviceDiscoveryModeKind Kind { get; }
    string DisplayName { get; }
    int Order { get; }
    TimeSpan ResponseTimeout { get; }
}

public sealed record DeviceDiscoveryModeDefinition(
    DeviceDiscoveryModeKind Kind,
    string DisplayName,
    int Order,
    TimeSpan ResponseTimeout
) : IDeviceDiscoveryMode;

public sealed record SavedDeviceConnectionProfile(
    string DeviceId,
    string Model,
    string? SerialNumber,
    string? MacAddress,
    string Scheme,
    string Address,
    int DevicePort,
    string ProtocolVersion,
    string LocalAddress,
    int LocalPort
);

public sealed class DeviceAutoConnectionOptionsValidator(
    DeviceBackendOptions? backendOptions = null
) : IValidateOptions<DeviceAutoConnectionOptions>
{
    public ValidateOptionsResult Validate(string? name, DeviceAutoConnectionOptions options)
    {
        var errors = options.GetValidationErrors(
            backendOptions?.ConnectionSource != DeviceConnectionSource.Simulated
        );
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
