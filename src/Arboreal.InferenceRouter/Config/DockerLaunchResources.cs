using System.Collections.Immutable;
using System.Text.Json;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter;

internal sealed record DockerGpu(bool All, ImmutableArray<string> DeviceIds);
internal sealed record DockerBind(string Source, string Target, bool ReadOnly);
internal sealed record DockerUlimit(string Name, long Soft, long Hard);

/// <summary>A checked container request. Resolving paths, identifying character
/// devices and proving effective limits require authority on the daemon host.</summary>
internal sealed record DockerLaunchResources(
    DockerGpu? Gpu,
    ImmutableArray<string> DevicePaths,
    ImmutableArray<DockerBind> BindMounts,
    long? NanoCpus,
    long? MemoryBytes,
    long? MemorySwapBytes,
    long? ShmSizeBytes,
    ImmutableArray<DockerUlimit> Ulimits)
{
    private const string Kfd = "/dev/kfd";
    private const string RenderNode = "/dev/dri/renderD";
    private static readonly string[] ControlPaths = ["/dev", "/proc", "/sys", "/run", "/var/run", "/var/lib/docker", "/var/lib/containerd"];

    internal static DockerLaunchResources Parse(DockerResourcesDto dto, string engine)
    {
        var path = $"engine.{engine}.docker.resources";
        CheckUnknown(dto.Unknown, path);
        if (dto is { Gpu: null, DevicePaths: null, BindMounts: null, NanoCpus: null, MemoryBytes: null,
                     MemorySwapBytes: null, ShmSizeBytes: null, Ulimits: null })
            throw new RouterConfigException($"{path} must contain a resource setting");
        if (dto.Gpu is not null && dto.DevicePaths is not null)
            throw new RouterConfigException($"{path}.gpu cannot be combined with device_paths");
        if (dto.NanoCpus is <= 0) throw new RouterConfigException($"{path}.nano_cpus must be positive");
        if (dto.MemoryBytes is < 6 * 1024 * 1024L)
            throw new RouterConfigException($"{path}.memory_bytes must be at least 6 MiB");
        if (dto.MemorySwapBytes is { } total && (dto.MemoryBytes is not { } memory || total < memory))
            throw new RouterConfigException($"{path}.memory_swap_bytes requires memory_bytes and must be at least that value");
        if (dto.ShmSizeBytes is <= 0) throw new RouterConfigException($"{path}.shm_size_bytes must be positive");

        return new DockerLaunchResources(
            dto.Gpu is { } gpu ? ParseGpu(gpu, $"{path}.gpu") : null,
            ParseDevices(dto.DevicePaths, $"{path}.device_paths"),
            ParseBinds(dto.BindMounts, $"{path}.bind_mounts"),
            dto.NanoCpus, dto.MemoryBytes, dto.MemorySwapBytes, dto.ShmSizeBytes,
            ParseUlimits(dto.Ulimits, $"{path}.ulimits"));
    }

    private static DockerGpu ParseGpu(DockerGpuDto dto, string path)
    {
        CheckUnknown(dto.Unknown, path);
        if (dto is { All: true, DeviceIds: null }) return new DockerGpu(true, []);
        var ids = dto.DeviceIds;
        if (dto.All is not null || ids is not { Length: > 0 })
            throw new RouterConfigException($"{path} requires either all = true or nonempty device_ids");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Length; i++)
        {
            if (!IsCanonicalIndex(ids[i].AsSpan()) || !seen.Add(ids[i]))
                throw new RouterConfigException($"{path}.device_ids[{i}] must be a unique canonical numeric GPU index");
        }
        return new DockerGpu(false, [.. ids]);
    }

    private static ImmutableArray<string> ParseDevices(string[]? values, string path)
    {
        if (values is null) return [];
        if (values.Length < 2 || !values.Contains(Kfd, StringComparer.Ordinal))
            throw new RouterConfigException($"{path} requires /dev/kfd and at least one DRM render node");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < values.Length; i++)
        {
            var item = values[i];
            if (item is null || (item != Kfd && (!item.StartsWith(RenderNode, StringComparison.Ordinal) ||
                  !IsCanonicalIndex(item.AsSpan(RenderNode.Length)))) || !seen.Add(item))
                throw new RouterConfigException($"{path}[{i}] must be /dev/kfd or a distinct /dev/dri/renderD<N> node");
        }
        return [.. values];
    }

    private static ImmutableArray<DockerBind> ParseBinds(DockerBindDto[]? values, string path)
    {
        if (values is null) return [];
        if (values.Length == 0) throw new RouterConfigException($"{path} must be nonempty");
        var binds = ImmutableArray.CreateBuilder<DockerBind>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var item = values[i] ?? throw new RouterConfigException($"{path}[{i}] must be an object");
            var itemPath = $"{path}[{i}]";
            CheckUnknown(item.Unknown, itemPath);
            var source = CheckBindPath(item.Source, $"{itemPath}.source");
            var target = CheckBindPath(item.Target, $"{itemPath}.target");
            if (item.ReadOnly is not { } readOnly)
                throw new RouterConfigException($"{itemPath}.read_only must explicitly specify true or false");
            foreach (var prior in binds)
            {
                if (Within(target, prior.Target) || Within(prior.Target, target))
                    throw new RouterConfigException($"{itemPath}.target overlaps another bind target");
            }
            binds.Add(new DockerBind(source, target, readOnly));
        }
        return binds.MoveToImmutable();
    }

    private static string CheckBindPath(string? value, string path)
    {
        if (value is not { Length: > 1 } || value[0] != '/' || value.Contains('\\', StringComparison.Ordinal) ||
            value.Any(char.IsControl) || value[1..].Split('/').Any(segment => segment is "" or "." or "..") ||
            ControlPaths.Any(root => Within(value, root)))
            throw new RouterConfigException($"{path} must be a clean absolute Linux path outside control directories");
        return value;
    }

    private static bool Within(string value, string root) =>
        value == root || (value.Length > root.Length && value[root.Length] == '/' && value.StartsWith(root, StringComparison.Ordinal));

    private static ImmutableArray<DockerUlimit> ParseUlimits(DockerUlimitDto[]? values, string path)
    {
        if (values is null) return [];
        if (values.Length == 0) throw new RouterConfigException($"{path} must be nonempty");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var limits = ImmutableArray.CreateBuilder<DockerUlimit>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var item = values[i] ?? throw new RouterConfigException($"{path}[{i}] must be an object");
            var itemPath = $"{path}[{i}]";
            CheckUnknown(item.Unknown, itemPath);
            var name = item.Name;
            if (name is not ("core" or "memlock") || !seen.Add(name))
                throw new RouterConfigException($"{itemPath}.name must be a unique core or memlock limit");
            if (item.Soft is not { } soft || soft < -1)
                throw new RouterConfigException($"{itemPath}.soft must be -1 or nonnegative");
            if (item.Hard is not { } hard || hard < -1)
                throw new RouterConfigException($"{itemPath}.hard must be -1 or nonnegative");
            if (hard != -1 && (soft == -1 || soft > hard))
                throw new RouterConfigException($"{itemPath}.soft cannot exceed hard (-1 means unlimited)");
            limits.Add(new DockerUlimit(name, soft, hard));
        }
        return limits.MoveToImmutable();
    }

    private static bool IsCanonicalIndex(ReadOnlySpan<char> index) =>
        !index.IsEmpty && (index.Length == 1 || index[0] != '0') && index.IndexOfAnyExceptInRange('0', '9') < 0;

    private static void CheckUnknown(TomlTable? keys, string path)
    {
        if (keys is { Count: > 0 }) throw new RouterConfigException($"{path}.{keys.Keys.First()} is not a resource setting");
    }

    // STJ accepts duplicate keys by default. Reject them in the new resource subtree and on its
    // ancestry so a later value cannot shadow the request. Keep legacy parsing outside that scope.
    internal static void CheckJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var engines in root.EnumerateObject())
        {
            if (!engines.NameEquals("engine") || engines.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var engine in engines.Value.EnumerateObject())
            {
                if (engine.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var docker in engine.Value.EnumerateObject())
                {
                    if (!docker.NameEquals("docker") || docker.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var resource in docker.Value.EnumerateObject())
                    {
                        if (!resource.NameEquals("resources")) continue;
                        var path = $"engine.{engine.Name}.docker.resources";
                        RequireSingleKey(root, "engine", "engine");
                        RequireSingleKey(engines.Value, engine.Name, $"engine.{engine.Name}");
                        RequireSingleKey(engine.Value, "docker", $"engine.{engine.Name}.docker");
                        RequireSingleKey(docker.Value, "resources", path);
                        if (resource.Value.ValueKind != JsonValueKind.Object)
                            throw new RouterConfigException($"{path} must be an object");
                        CheckJsonTree(resource.Value, path);
                    }
                }
            }
        }
    }

    private static void RequireSingleKey(JsonElement table, string name, string path)
    {
        if (table.EnumerateObject().Count(value => value.NameEquals(name)) > 1)
            throw new RouterConfigException($"{path} appears more than once");
    }

    private static void CheckJsonTree(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
            throw new RouterConfigException($"{path} cannot be null");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in value.EnumerateObject())
            {
                var childPath = $"{path}.{child.Name}";
                if (!seen.Add(child.Name)) throw new RouterConfigException($"{childPath} appears more than once");
                CheckJsonTree(child.Value, childPath);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var child in value.EnumerateArray())
                CheckJsonTree(child, $"{path}[{i++}]");
        }
    }
}
