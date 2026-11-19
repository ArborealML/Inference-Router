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
            foreach (var catalog in TomlMembers(engine, "catalog").OfType<TomlTable>())
            foreach (var (modelName, modelValue) in catalog)
            {
                if (modelValue is not TomlTable model) continue;
                foreach (var serving in TomlMembers(model, "serving"))
                {
                    if (serving is TomlTable malformed && HasTomlTierDeclaration(malformed))
                        throw new RouterConfigException($"engine.{engineName}.catalog.{modelName}.serving must be an array of serving tiers");
                    IEnumerable<object?> rows = serving switch
                    {
                        TomlTableArray tables => tables,
                        TomlArray array => array,
                        _ => [],
                    };
                    var index = 0;
                    foreach (var row in rows)
                    {
                        if (row is TomlTable tier && HasTomlTierDeclaration(tier))
                        {
                            var enginePath = $"engine.{engineName}";
                            var modelPath = $"{enginePath}.catalog.{modelName}";
                            CheckTomlMember(root, "engine", "engine");
                            CheckTomlMember(engines, engineName, enginePath);
                            CheckTomlFields(engine, enginePath, "catalog", "docker", "lifecycle_owner", "remote", "lifecycle");
                            CheckTomlMember(catalog, modelName, modelPath);
                            CheckTomlFields(model, modelPath, "serving", "aliases", "profile", "default_profile");
                            CheckTomlFields(tier, $"{modelPath}.serving[{index}]",
                                "name", "max_context", "max_slots", "docker", "aliases");
                            CheckTomlSelectors(catalog, enginePath);
                        }
                        index++;
                    }
                }
            }
        }
    }

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
