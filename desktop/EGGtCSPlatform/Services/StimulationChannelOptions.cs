using System;
using System.Collections.Generic;
using System.Linq;

namespace EGGtCSPlatform.Services;

public sealed class StimulationChannelOptions
{
    public const string SectionName = "StimulationChannels";

    public int PhysicalChannelCount { get; set; } = 8;

    public int[] FixedActivePhysicalChannelIds { get; set; } = [];

    public int MaximumSelectableChannelCount { get; set; } = 4;

    public int DualChannelSelectableCount { get; set; } = 1;

    public int HdSelectableChannelCount { get; set; } = 4;

    public bool MultiTargetEnabled { get; set; }

    public string[] AllowedElectrodeSiteIds { get; set; } =
    ["FP2", "F3", "FC5", "T7", "Cz", "T8", "CP5", "P3"];

    public bool IsValid()
    {
        var fixedChannels = FixedActivePhysicalChannelIds ?? [];
        var allowedSites = AllowedElectrodeSiteIds ?? [];
        return PhysicalChannelCount > 0
            && MaximumSelectableChannelCount > 0
            && DualChannelSelectableCount > 0
            && HdSelectableChannelCount > 0
            && DualChannelSelectableCount <= MaximumSelectableChannelCount
            && HdSelectableChannelCount <= MaximumSelectableChannelCount
            && MaximumSelectableChannelCount
                <= PhysicalChannelCount - fixedChannels.Distinct().Count()
            && fixedChannels.Distinct().Count() == fixedChannels.Length
            && fixedChannels.All(channel => channel >= 1 && channel <= PhysicalChannelCount)
            && allowedSites.Length > 0
            && allowedSites.All(site => !string.IsNullOrWhiteSpace(site))
            && new HashSet<string>(allowedSites, StringComparer.OrdinalIgnoreCase).Count
                == allowedSites.Length;
    }
}
