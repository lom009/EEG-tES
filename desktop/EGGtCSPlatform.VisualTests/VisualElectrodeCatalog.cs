using EGGtCSPlatform.Services;
using Microsoft.Extensions.Configuration;

namespace EGGtCSPlatform.VisualTests;

internal static class VisualElectrodeCatalog
{
    // Visual scenarios deliberately bypass production startup. Use the shipped fixture,
    // never the operator's pre-migration user configuration.
    public static IElectrodePositionCatalog Create() => new ElectrodePositionCatalog(ElectrodePositionOptions.Read(
        new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.default.json"))
            .Build().GetSection(ElectrodePositionOptions.SectionName)));
}
