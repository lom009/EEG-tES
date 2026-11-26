using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace EGGtCSPlatform.Configuration;

internal sealed class UserConfigurationSchemaOneToTwoMigration : IUserConfigurationMigration
{
    public int FromVersion => 1;
    public int ToVersion => 2;
    public void Apply(JsonObject configuration) { }

    // Defaults are supplied by the synchronizer so migration and the shipped catalogue
    // have exactly one source for the corrected 3D geometry.
    internal static void AddKnownGeometry(JsonObject configuration, JsonObject defaults)
    {
        if (configuration["ElectrodePositions"]?["Positions"] is not JsonArray positions
            || defaults["ElectrodePositions"]?["Positions"] is not JsonArray known) return;
        foreach (var point in positions.OfType<JsonObject>())
        {
            if (point["Id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id)) continue;
            var template = known.OfType<JsonObject>().FirstOrDefault(candidate =>
                candidate["Id"] is JsonValue candidateId && candidateId.TryGetValue<string>(out var value)
                && string.Equals(value, id, StringComparison.OrdinalIgnoreCase));
            // A custom position without a normal must keep its radial fallback; a stock
            // normal could point in the wrong direction for its new location.
            if (template is null || point.ContainsKey("Position3D")) continue;
            point["Position3D"] = template["Position3D"]?.DeepClone();
            if (!point.ContainsKey("Normal3D")) point["Normal3D"] = template["Normal3D"]?.DeepClone();
        }
    }
}
