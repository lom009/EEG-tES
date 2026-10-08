using System.Collections.Frozen;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

/// <summary>Explicit wire revisions, independent of assembly versions. No future revision is implied.</summary>
public static class EggtCsRevisions
{
    public const string V101 = "1.0.1";
    public static string DefaultRevision => V101;
    public static IReadOnlySet<string> Supported { get; } =
        new[] { V101 }.ToFrozenSet(StringComparer.Ordinal);
}

public static class EggtCsCommandCatalog
{
    public static IReadOnlyList<ProtocolCommandDefinition> Commands { get; } =
        Array.AsReadOnly(
            new[]
            {
                Define<ReadDeviceStatusRequest>("读取设备状态", EggtCsRevisions.V101),
                Define<ConfigureEegImpedanceRequest>("配置脑电阻抗检测", EggtCsRevisions.V101),
                Define<ControlAcquisitionRequest>("控制脑电采集", EggtCsRevisions.V101),
                Define<ControlStimulationRequest>("控制刺激", EggtCsRevisions.V101),
                Define<ConfigureEnvelopeStimulationRequest>("配置包络刺激", EggtCsRevisions.V101),
                Define<ControlEnvelopeStimulationRequest>("控制包络刺激", EggtCsRevisions.V101),
                Define<AdjustCurrentRequest>("调整电流", EggtCsRevisions.V101),
                Define<ConfigureStimulationImpedanceRequest>(
                    "配置刺激阻抗检测",
                    EggtCsRevisions.V101
                ),
            }
        );

    private static ProtocolCommandDefinition Define<T>(
        string displayName,
        params string[] revisions
    ) =>
        new(
            typeof(T),
            TimeSpan.FromMilliseconds(1500),
            revisions.ToFrozenSet(StringComparer.Ordinal)
        )
        {
            IntroducedIn = EggtCsRevisions.V101,
            DisplayName = displayName,
        };

    private static readonly ProtocolCommandCatalog Catalog = new(Commands);

    public static IReadOnlySet<Type> GetSupportedCommands(string revision) =>
        Catalog.GetSupportedCommands(revision);

    public static TimeSpan GetTimeout(
        Type commandType,
        string revision,
        IReadOnlyDictionary<Type, TimeSpan> overrides
    ) => Catalog.GetTimeout(commandType, revision, overrides);
}
