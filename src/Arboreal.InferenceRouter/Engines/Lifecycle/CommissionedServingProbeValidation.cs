using System.Globalization;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal static class CommissionedServingProbeValidation
{
    internal static bool Matches(EngineDefinition engine, ServingBackendProfile expected, ServingBrokerUseKind kind,
        ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            return kind switch
            {
                ServingBrokerUseKind.Models => MatchesModels(root, expected.CanonicalModel, engine.LoadedRequiresStatusLoaded),
                ServingBrokerUseKind.State =>
                    ReadGauge(root, engine.Health?.ContextPath ?? "n_ctx") == expected.Context &&
                    ReadGauge(root, engine.Health?.SlotsPath ?? "total_slots") == expected.Slots &&
                    (engine.Health?.Expect is not { } expect || ProbeExpect.Evaluate(root, expect)),
                _ => false,
            };
        }
        catch (JsonException) { return false; }
    }

    private static bool MatchesModels(JsonElement root, string canonical, bool requireLoaded)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return false;

        var canonicalSeen = false;
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                return false;
            if (row.TryGetProperty("alias_of", out var alias) && alias.ValueKind != JsonValueKind.Null)
            {
                if (alias.ValueKind != JsonValueKind.String || !alias.ValueEquals(canonical) || id.ValueEquals(canonical))
                    return false;
                continue;
            }

            if (canonicalSeen || !id.ValueEquals(canonical)) return false;
            if (row.TryGetProperty("status", out var status))
            {
                if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("value", out var value) ||
                    value.ValueKind != JsonValueKind.String || !value.ValueEquals("loaded")) return false;
            }
            else if (requireLoaded) return false;

            canonicalSeen = true;
        }

        return canonicalSeen;
    }

    private static long ReadGauge(JsonElement root, string path)
    {
        if (!ProbeExpect.TryResolvePath(root, path, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric)) return numeric;
        return value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
}
