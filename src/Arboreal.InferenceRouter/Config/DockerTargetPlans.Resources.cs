using System.Text.Json;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter;

internal sealed partial class DockerTargetPlans
{
    private static readonly string[] TierResourceFields = ["gpu", "shm_size_bytes"];

    private static void RejectMisplacedTierResourceJson(JsonElement table, string path)
    {
        foreach (var field in TierResourceFields)
            if (Members(table, field).Any())
                throw new RouterConfigException($"{path}.{field} belongs inside serving-tier docker or engine docker.resources");
    }

    private static void RejectMisplacedTierResourceToml(TomlTable table, string path)
    {
        foreach (var field in TierResourceFields)
            if (HasTomlMember(table, field))
                throw new RouterConfigException($"{path}.{field} belongs inside serving-tier docker or engine docker.resources");
    }

    private static void CheckSharedMemoryJson(JsonElement table, string path)
    {
        CheckMember(table, "shm_size_bytes", path);
        if (table.TryGetProperty("shm_size_bytes", out var value) &&
            (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var size) || size <= 0))
            throw new RouterConfigException($"{path} must be a positive Int64 integer");
    }

    private static void CheckSharedMemoryToml(TomlTable table, string path)
    {
        CheckTomlMember(table, "shm_size_bytes", path);
        if (TomlMembers(table, "shm_size_bytes").Any(value => value is not long size || size <= 0))
            throw new RouterConfigException($"{path} must be a positive Int64 integer");
    }
}
