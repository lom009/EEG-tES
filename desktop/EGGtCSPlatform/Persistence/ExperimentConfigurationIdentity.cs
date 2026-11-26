using System;
using System.Linq;
using System.Text.Json;
using EGGtCSPlatform.MainApp;

namespace EGGtCSPlatform.Persistence;

internal static class ExperimentConfigurationIdentity
{
    public const string LegacyWarning = "此运行未独立留存配置，显示的配置可能反映后续修改。";

    public static ExperimentConfigurationTemplate Read(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ExperimentConfigurationTemplate>(json);
            if (
                value is null
                || value.StimulusConfiguration is null
                || value.Timing is null
                || value.StimulusElectrodes is null
                || value.AcquisitionChannels is null
                || value.SampleRateHz <= 0
                || value.Timing.CycleCount <= 0
                || !Enum.IsDefined(value.CreationMode)
                || !Enum.IsDefined(value.Timing.Mode)
            )
                throw new JsonException();
            _ = Key(value);
            _ = ExperimentStimulusConfigurationSnapshot.From(
                value.StimulusConfiguration,
                value.StimulusElectrodes
            );
            if (value.PhysicalChannels is not null)
                _ = value
                    .PhysicalChannels.Where(m =>
                        m.PhysicalChannel.HasValue
                        && value.AcquisitionChannels.Contains(
                            m.ElectrodeId,
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                    .ToDictionary(m => m.PhysicalChannel!.Value, m => m.ElectrodeId);
            return value;
        }
        catch (Exception e)
            when (e
                    is JsonException
                        or ArgumentException
                        or InvalidOperationException
                        or NullReferenceException
            )
        {
            throw new System.IO.InvalidDataException("运行配置快照损坏，无法可靠读取历史配置。", e);
        }
    }

    public static string Key(ExperimentConfigurationTemplate value)
    {
        value = AcquisitionOnlyConfiguration.Normalize(value);
        var s = value.StimulusConfiguration;
        return JsonSerializer.Serialize(
            new
            {
                value.CreationMode,
                value.SampleRateHz,
                value.Timing,
                s.Kind,
                s.ArrayMode,
                s.Direction,
                s.ShamMode,
                s.RampSeconds,
                s.Frequency,
                s.DutyPercent,
                s.Envelope,
                Targets = s
                    .Targets.OrderBy(t => t.DisplayOrder)
                    .Select(t => new
                    {
                        t.DisplayOrder,
                        t.PeakCurrent,
                        Channels = (
                            t.Channels.Count > 0
                                ? t.Channels
                                : value
                                    .StimulusElectrodes.Where(a => a.TargetId == t.TargetId)
                                    .Select(
                                        a => new ViewModels.Pages.StimulationPhysicalChannelSnapshot(
                                            a.PhysicalChannelId,
                                            a.Role,
                                            t.PeakCurrent
                                        )
                                    )
                                    .ToArray()
                        )
                            .OrderBy(c => c.PhysicalChannelId)
                            .Select(c => new
                            {
                                c.PhysicalChannelId,
                                c.Role,
                                c.Current,
                            }),
                        Sites = value
                            .StimulusElectrodes.Where(a => a.TargetId == t.TargetId)
                            .OrderBy(a => a.PhysicalChannelId)
                            .ThenBy(a => a.SiteId, StringComparer.OrdinalIgnoreCase)
                            .Select(a => new
                            {
                                Site = a.SiteId.ToUpperInvariant(),
                                a.PhysicalChannelId,
                                a.Role,
                            }),
                    }),
                Acquisition = value
                    .AcquisitionChannels.Select(x => x.ToUpperInvariant())
                    .OrderBy(x => x, StringComparer.Ordinal),
                Reference = value.ReferenceChannel.ToUpperInvariant(),
                Ground = value.GroundChannel.ToUpperInvariant(),
                Mapping = value
                    .AcquisitionChannels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Select(site => new
                    {
                        Site = site.ToUpperInvariant(),
                        Channel = value
                            .PhysicalChannels?.SingleOrDefault(m =>
                                string.Equals(
                                    m.ElectrodeId,
                                    site,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            ?.PhysicalChannel,
                    }),
            }
        );
    }
}
