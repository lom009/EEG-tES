using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.Configuration;

namespace EGGtCSPlatform.Services;

public sealed class ElectrodePositionOptions
{
    public const string SectionName = "ElectrodePositions";
    public const double ReferenceWidth = 572d;
    public const double ReferenceHeight = 574d;

    public ElectrodePositionDefinition[] Positions { get; set; } = [];

    public static ElectrodePositionOptions Read(IConfiguration section) => new()
    {
        Positions = section.GetSection(nameof(Positions)).GetChildren().Select(point => new ElectrodePositionDefinition(
            point[nameof(ElectrodePositionDefinition.Id)] ?? "",
            point[nameof(ElectrodePositionDefinition.Position)] ?? "",
            point.GetValue(nameof(ElectrodePositionDefinition.ReferenceX), double.NaN),
            point.GetValue(nameof(ElectrodePositionDefinition.ReferenceY), double.NaN),
            point.GetValue<ElectrodeRole>(nameof(ElectrodePositionDefinition.DefaultRole)),
            ReadVector(point.GetSection(nameof(ElectrodePositionDefinition.Position3D))),
            ReadVector(point.GetSection(nameof(ElectrodePositionDefinition.Normal3D)))
        )).ToArray(),
    };

    private static ElectrodeVector3? ReadVector(IConfigurationSection section)
    {
        if (!section.Exists()) return null;
        // Malformed 3D fields disable that view, not otherwise valid 2D/device setup.
        double Component(string name) => double.TryParse(section[name], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
        return new(Component("X"), Component("Y"), Component("Z"));
    }

    public bool IsValid()
    {
        var positions = Positions ?? [];
        if (
            positions.Length == 0
            || positions.Any(position =>
                string.IsNullOrWhiteSpace(position.Id)
                || string.IsNullOrWhiteSpace(position.Position)
                || !double.IsFinite(position.ReferenceX)
                || !double.IsFinite(position.ReferenceY)
                || position.ReferenceX < 0d
                || position.ReferenceX > ReferenceWidth
                || position.ReferenceY < 0d
                || position.ReferenceY > ReferenceHeight
                || !Enum.IsDefined(position.DefaultRole)
            )
            || positions
                .Select(position => position.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != positions.Length
        )
        {
            return false;
        }

        return positions.Count(position => position.DefaultRole == ElectrodeRole.Reference) <= 1
            && positions.Count(position => position.DefaultRole == ElectrodeRole.Ground) <= 1;
    }
}

public interface IElectrodePositionCatalog
{
    IReadOnlyList<ElectrodePositionDefinition> Positions { get; }

    IReadOnlySet<string> ElectrodeIds { get; }
}

public sealed class ElectrodePositionCatalog : IElectrodePositionCatalog
{
    private static readonly Lazy<ElectrodePositionCatalog> DefaultCatalog = new(LoadDefault);

    public ElectrodePositionCatalog(ElectrodePositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid())
            throw new ArgumentException("ElectrodePositions 配置无效。", nameof(options));

        Positions = Array.AsReadOnly(options.Positions.ToArray());
        ElectrodeIds = new HashSet<string>(
            Positions.Select(position => position.Id),
            StringComparer.OrdinalIgnoreCase
        );
    }

    public IReadOnlyList<ElectrodePositionDefinition> Positions { get; }

    public IReadOnlySet<string> ElectrodeIds { get; }

    // Compatibility for test/preview hosts. Production pages receive the catalog from DI.
    public static IReadOnlyList<ElectrodePositionDefinition> All => DefaultCatalog.Value.Positions;

    public static IElectrodePositionCatalog Default => DefaultCatalog.Value;

    private static ElectrodePositionCatalog LoadDefault()
    {
        var configurationPath = File.Exists(AppPaths.UserConfigurationPath)
            ? AppPaths.UserConfigurationPath
            : AppPaths.DefaultConfigurationPath;
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configurationPath, optional: false, reloadOnChange: false)
            .Build();
        var options = ElectrodePositionOptions.Read(configuration.GetSection(ElectrodePositionOptions.SectionName));
        return new ElectrodePositionCatalog(options);
    }
}
