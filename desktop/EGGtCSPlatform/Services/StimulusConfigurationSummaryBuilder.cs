using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public static class StimulusConfigurationSummaryBuilder
{
    public static string CurrentText(ExperimentStimulusConfigurationSnapshot configuration) =>
        $"{ExperimentRunRouteDataDefaults.GetTotalCurrent(configuration):0.00} mA";

    public static WaveformDescriptor Waveform(
        ExperimentStimulusConfigurationSnapshot configuration
    ) =>
        new(
            configuration.Kind,
            configuration.ArrayMode,
            configuration.Direction,
            configuration.ShamMode,
            ExperimentRunRouteDataDefaults.GetTotalCurrent(configuration),
            configuration.RampSeconds,
            configuration.Frequency,
            configuration.DutyPercent,
            configuration.Envelope?.DelayMilliseconds ?? 40
        );

    public static string DirectionText(StimulusDirection direction) =>
        direction switch
        {
            StimulusDirection.Positive => "正向",
            StimulusDirection.Negative => "反向",
            StimulusDirection.Bidirectional => "双向",
            _ => "未知",
        };

    public static string CurrentLabel(ExperimentStimulusConfigurationSnapshot configuration) =>
        configuration.Targets.Count > 1 ? "设定峰值合计" : "设定峰值电流";

    public static IReadOnlyList<ExperimentResultDetailItem> Build(ExperimentRunRouteData route)
    {
        var configuration = route.StimulusConfiguration;
        var items = new List<ExperimentResultDetailItem>
        {
            new(
                "刺激范式",
                StimulusParameterPolicy.ModeName(configuration.Kind, configuration.ShamMode)
            ),
            new("电流方向", DirectionText(configuration.Direction)),
            new(
                "阵列",
                configuration.ArrayMode switch
                {
                    StimulusArrayMode.DualChannel => "双通道",
                    StimulusArrayMode.Hd => "HD",
                    StimulusArrayMode.MultiTarget => "多靶点",
                    _ => "未知",
                }
            ),
            new(
                CurrentLabel(configuration),
                $"{configuration.Targets.Sum(x => x.PeakCurrent):0.00} mA"
            ),
        };
        var waveform = StimulusParameterPolicy
            .For(configuration.Kind, configuration.ShamMode)
            .FormatSummary(
                configuration.Frequency,
                configuration.RampSeconds,
                configuration.DutyPercent
            );
        if (waveform.Length > 0 && configuration.Envelope?.IsDeviceSetup != true)
            items.Add(new("波形参数", waveform));
        if (configuration.Envelope is { IsDeviceSetup: true } setup)
            items.Add(new("启动延时", $"{setup.DelayMilliseconds:0} ms"));
        else if (configuration.Envelope is { } envelope)
        {
            items.Add(new("载波频率", $"{configuration.Frequency:0.###} Hz"));
            items.Add(
                new(
                    "包络参数",
                    $"调制 {envelope.ModulationHz:0.###} Hz / 深度 {envelope.Depth:0.###} / 时延 {envelope.DelayMilliseconds:0.###} ms / {(envelope.IsSham ? "假刺激" : "刺激")}"
                )
            );
        }
        foreach (var target in configuration.Targets.OrderBy(x => x.DisplayOrder))
        {
            var prefix =
                configuration.Targets.Count > 1 ? $"靶点{target.DisplayOrder}·" : string.Empty;
            if (prefix.Length > 0)
                items.Add(new($"{prefix}峰值", $"{target.PeakCurrent:0.00} mA"));
            foreach (
                var role in new[]
                {
                    StimulationChannelRole.FixedActive,
                    StimulationChannelRole.Selectable,
                }
            )
            {
                var channels = target
                    .Channels.Where(x => x.Role == role)
                    .OrderBy(x => x.PhysicalChannelId)
                    .Select(channel =>
                    {
                        var assignment = route.StimulusElectrodes.FirstOrDefault(x =>
                            x.TargetId == target.TargetId
                            && x.PhysicalChannelId == channel.PhysicalChannelId
                        );
                        var roleLabel = Enum.IsDefined(configuration.Direction)
                            ? StimulationElectrodeRoleResolver.GetLabel(
                                configuration.Direction,
                                role
                            )
                            : "未知方向";
                        return $"CH{channel.PhysicalChannelId}={assignment?.SiteId ?? "--"}({roleLabel}, {channel.Current:0.##} mA)";
                    });
                items.Add(
                    new(
                        prefix + StimulationElectrodeRoleResolver.GetChannelRoleLabel(role),
                        string.Join(", ", channels)
                    )
                );
            }
        }
        return items.AsReadOnly();
    }
}
