using Tomlyn.Model;

namespace Arboreal.InferenceRouter;

internal sealed partial class DockerTargetPlans
{
    // TOML's typed binding accepts a single case variant. Inspect the opted-in tables
    // before binding so a case twin cannot hide a declaration in a replacement table.
    internal static void CheckTargetDeclarationsToml(TomlTable root)
    {
        foreach (var engines in TomlMembers(root, "engine").OfType<TomlTable>())
        foreach (var (engineName, engineValue) in engines)
        {
            if (engineValue is not TomlTable engine) continue;
            var enginePath = $"engine.{engineName}";
            RejectMisplacedTierResourceToml(engine, enginePath);
            CheckCommonTargetOptionsToml(root, engines, engineName, engine, enginePath);
            CheckContainerPolicyToml(root, engines, engineName, engine, enginePath);
            foreach (var catalog in TomlMembers(engine, "catalog").OfType<TomlTable>())
            foreach (var (modelName, modelValue) in catalog)
            {
                if (modelValue is not TomlTable model) continue;
                var modelPath = $"{enginePath}.catalog.{modelName}";
                RejectMisplacedContainerPolicyToml(model, modelPath);
                RejectMisplacedTierResourceToml(model, modelPath);
                foreach (var modelDocker in TomlMembers(model, "docker").OfType<TomlTable>())
                {
                    RejectMisplacedContainerPolicyToml(modelDocker, $"{modelPath}.docker");
                    RejectMisplacedTierResourceToml(modelDocker, $"{modelPath}.docker");
                }
                foreach (var serving in TomlMembers(model, "serving"))
                {
                    if (serving is TomlTable malformed)
                    {
                        RejectMisplacedContainerPolicyToml(malformed, $"{modelPath}.serving");
                        RejectMisplacedTierResourceToml(malformed, $"{modelPath}.serving");
                        if (HasTomlTierDeclaration(malformed) || HasTomlMember(malformed, "command"))
                            throw new RouterConfigException($"{modelPath}.serving must be an array of serving tiers");
                    }
                    var index = 0;
                    foreach (var row in TomlArrayItems(serving))
                    {
                        var tierPath = $"{modelPath}.serving[{index}]";
                        if (row is not TomlTable tier)
                        {
                            index++;
                            continue;
                        }
                        RejectMisplacedContainerPolicyToml(tier, tierPath);
                        RejectMisplacedTierResourceToml(tier, tierPath);
                        foreach (var tierDocker in TomlMembers(tier, "docker").OfType<TomlTable>())
                            RejectMisplacedContainerPolicyToml(tierDocker, $"{tierPath}.docker");
                        if (HasTomlMember(tier, "command"))
                            throw new RouterConfigException($"{tierPath}.command belongs inside serving-tier docker");
                        if (HasTomlMember(tier, "nested_under"))
                            throw new RouterConfigException($"{tierPath}.nested_under belongs on a serving-tier bind mount");
                        if (HasTomlTierDeclaration(tier))
                        {
                            CheckTomlMember(root, "engine", "engine");
                            CheckTomlMember(engines, engineName, enginePath);
                            CheckTomlFields(engine, enginePath, "catalog", "docker", "lifecycle_owner", "remote", "lifecycle");
                            CheckTomlMember(catalog, modelName, modelPath);
                            CheckTomlFields(model, modelPath, "serving", "aliases", "profile", "default_profile");
                            CheckTomlFields(tier, tierPath, "name", "max_context", "max_slots", "docker", "aliases");
                            CheckTomlSelectors(catalog, enginePath);
                            var hasGpu = CheckTierTargetTablesToml(tier, $"{tierPath}.docker");
                            foreach (var common in TomlMembers(engine, "docker").OfType<TomlTable>())
                            {
                                CheckCommandToml(common, $"{enginePath}.docker.command", requireArguments: false);
                                if (hasGpu) CheckCommonGpuToml(common, $"{enginePath}.docker");
                            }
                        }
                        index++;
                    }
                }
            }
        }
    }

    private static void CheckCommonTargetOptionsToml(TomlTable root, TomlTable engines, string engineName,
        TomlTable engine, string enginePath)
    {
        var path = $"{enginePath}.docker";
        foreach (var docker in TomlMembers(engine, "docker").OfType<TomlTable>())
        {
            RejectMisplacedTargetOptionsToml(docker, path);
            RejectMisplacedTierResourceToml(docker, path);
            foreach (var resources in TomlMembers(docker, "resources").OfType<TomlTable>())
            {
                var resourcePath = $"{path}.resources";
                RejectMisplacedTargetOptionsToml(resources, resourcePath);
                RejectMisplacedContainerPolicyToml(resources, resourcePath);
                if (!HasTomlMember(resources, "shm_size_bytes")) continue;
                CheckTomlMember(root, "engine", "engine");
                CheckTomlMember(engines, engineName, enginePath);
                CheckTomlMember(engine, "docker", path);
                CheckTomlMember(docker, "resources", resourcePath);
                CheckSharedMemoryToml(resources, $"{resourcePath}.shm_size_bytes");
            }
        }
    }

    private static void RejectMisplacedTargetOptionsToml(TomlTable table, string path)
    {
        if (HasTomlMember(table, "healthcheck"))
            throw new RouterConfigException($"{path}.healthcheck is only supported inside a serving-tier docker block");
        if (HasTomlMember(table, "nested_under"))
            throw new RouterConfigException($"{path}.nested_under is only supported on serving-tier bind mounts");
        foreach (var list in TomlMembers(table, "bind_mounts"))
        {
            if (list is TomlTable malformed && HasTomlMember(malformed, "nested_under"))
                throw new RouterConfigException($"{path}.bind_mounts.nested_under is only supported on serving-tier bind mounts");
            var index = 0;
            foreach (var row in TomlArrayItems(list))
            {
                if (row is TomlTable bind && HasTomlMember(bind, "nested_under"))
                    throw new RouterConfigException($"{path}.bind_mounts[{index}].nested_under is only supported on serving-tier bind mounts");
                index++;
            }
        }
    }

    private static bool CheckTierTargetTablesToml(TomlTable tier, string path)
    {
        var hasGpu = false;
        foreach (var target in TomlMembers(tier, "docker").OfType<TomlTable>())
        {
            CheckTomlFields(target, path, "bind_mounts", "healthcheck");
            CheckCommandToml(target, $"{path}.command", requireArguments: true);
            hasGpu |= CheckTierGpuToml(target, $"{path}.gpu");
            CheckSharedMemoryToml(target, $"{path}.shm_size_bytes");
            foreach (var health in TomlMembers(target, "healthcheck").OfType<TomlTable>())
            {
                CheckTomlFields(health, $"{path}.healthcheck", "command", "interval_ms", "start_period_ms", "timeout_ms", "retries");
                if (TomlMembers(health, "timeout_ms").Any(value => value is not long))
                    throw new RouterConfigException($"{path}.healthcheck.timeout_ms must be an Int64 integer");
                if (TomlMembers(health, "retries").Any(value => value is not long retries ||
                    retries > int.MaxValue || retries < int.MinValue))
                    throw new RouterConfigException($"{path}.healthcheck.retries must be an Int32 integer");
            }
            foreach (var list in TomlMembers(target, "bind_mounts"))
            {
                var index = 0;
                foreach (var row in TomlArrayItems(list))
                {
                    if (row is TomlTable bind)
                    {
                        var bindPath = $"{path}.bind_mounts[{index}]";
                        CheckTomlFields(bind, bindPath, "source", "target", "read_only", "nested_under");
                        if (TomlMembers(bind, "nested_under").Any(value => value is not string))
                            throw new RouterConfigException($"{bindPath}.nested_under must be a string");
                    }
                    index++;
                }
            }
        }
        return hasGpu;
    }

    private static void CheckCommandToml(TomlTable table, string path, bool requireArguments)
    {
        CheckTomlMember(table, "command", path);
        foreach (var value in TomlMembers(table, "command"))
        {
            if (value is not TomlArray command || (requireArguments && command.Count == 0))
                throw new RouterConfigException($"{path} must be {(requireArguments ? "a nonempty array" : "an array")} of strings");
            for (var i = 0; i < command.Count; i++)
                if (command[i] is not string argument || argument.Contains('\0', StringComparison.Ordinal))
                    throw new RouterConfigException($"{path}[{i}] must be a string without NUL");
        }
    }

    private static IEnumerable<object?> TomlArrayItems(object? value) => value switch
    {
        TomlTableArray tables => tables,
        TomlArray array => array,
        _ => [],
    };

    private static bool HasTomlTierDeclaration(TomlTable tier) =>
        HasTomlMember(tier, "docker") || HasTomlMember(tier, "aliases");

    private static void CheckTomlSelectors(TomlTable catalog, string enginePath)
    {
        foreach (var (name, entry) in catalog)
        {
            if (entry is not TomlTable sibling || !HasTomlMember(sibling, "select")) continue;
            var path = $"{enginePath}.catalog.{name}";
            CheckTomlMember(catalog, name, path);
            CheckTomlMember(sibling, "select", $"{path}.select");
        }
    }

    private static IEnumerable<object?> TomlMembers(TomlTable table, string name)
    {
        foreach (var (key, value) in table)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                yield return value;
    }

    private static bool HasTomlMember(TomlTable table, string name) =>
        table.Keys.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void CheckTomlFields(TomlTable table, string path, params ReadOnlySpan<string> fields)
    {
        foreach (var field in fields)
            CheckTomlMember(table, field, $"{path}.{field}");
    }

    private static void CheckTomlMember(TomlTable table, string name, string path)
    {
        if (table.Keys.Count(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new RouterConfigException($"{path} is duplicated");
    }
}
