using System.Text.Json;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter;

internal sealed partial class DockerTargetPlans
{
    private static DockerGpu ParseTierGpu(DockerTargetGpuDto dto, DockerLaunchResources? common, string path)
    {
        if (dto.Unknown is { Count: > 0 } unknown)
            throw new RouterConfigException($"{path}.{unknown.Keys.First()} is not a tier GPU setting");
        if (dto.DeviceIds is not [var id] || !DockerLaunchResources.IsCanonicalIndex(id.AsSpan()))
            throw new RouterConfigException($"{path}.device_ids must contain exactly one canonical numeric GPU index");
        if (common is { DevicePaths.IsEmpty: false })
            throw new RouterConfigException($"{path} cannot be combined with common docker.resources.device_paths");
        return new DockerGpu(false, [id]);
    }

    private static void CheckTierGpuJson(JsonElement target, string path)
    {
        CheckMember(target, "gpu", path);
        if (!target.TryGetProperty("gpu", out var gpu)) return;
        if (gpu.ValueKind != JsonValueKind.Object)
            throw new RouterConfigException($"{path} must be an object");
        CheckMember(gpu, "device_ids", $"{path}.device_ids");
        foreach (var field in gpu.EnumerateObject())
            if (!field.NameEquals("device_ids"))
                throw new RouterConfigException($"{path}.{field.Name} is not a tier GPU setting");
        if (!gpu.TryGetProperty("device_ids", out var ids) || ids.ValueKind != JsonValueKind.Array ||
            ids.GetArrayLength() != 1 || ids[0].ValueKind != JsonValueKind.String)
            throw new RouterConfigException($"{path}.device_ids must contain exactly one string");
    }

    private static void CheckCommonGpuJson(JsonElement resources, string path)
    {
        CheckMember(resources, "gpu", $"{path}.gpu");
        if (resources.ValueKind == JsonValueKind.Object && resources.TryGetProperty("gpu", out var gpu))
        {
            CheckMember(gpu, "device_ids", $"{path}.gpu.device_ids");
            CheckMember(gpu, "all", $"{path}.gpu.all");
        }
    }

    private static bool CheckTierGpuToml(TomlTable target, string path)
    {
        CheckTomlMember(target, "gpu", path);
        foreach (var value in TomlMembers(target, "gpu"))
        {
            if (value is not TomlTable gpu)
                throw new RouterConfigException($"{path} must be a table");
            CheckTomlMember(gpu, "device_ids", $"{path}.device_ids");
            foreach (var field in gpu.Keys)
                if (!field.Equals("device_ids", StringComparison.OrdinalIgnoreCase))
                    throw new RouterConfigException($"{path}.{field} is not a tier GPU setting");
            if (TomlMembers(gpu, "device_ids").SingleOrDefault() is not TomlArray ids ||
                ids.Count != 1 || ids[0] is not string)
                throw new RouterConfigException($"{path}.device_ids must contain exactly one string");
            return true;
        }
        return false;
    }

    private static void CheckCommonGpuToml(TomlTable docker, string path)
    {
        CheckTomlMember(docker, "resources", $"{path}.resources");
        foreach (var resources in TomlMembers(docker, "resources").OfType<TomlTable>())
        {
            CheckTomlMember(resources, "gpu", $"{path}.resources.gpu");
            foreach (var gpu in TomlMembers(resources, "gpu").OfType<TomlTable>())
                CheckTomlFields(gpu, $"{path}.resources.gpu", "device_ids", "all");
        }
    }
}
