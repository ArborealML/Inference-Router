using System.Text.Json;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter;

internal sealed partial class DockerTargetPlans
{
    private static readonly string[] ContainerPolicyFields = ["init", "restart", "log_rotation"];

    private static void RejectMisplacedContainerPolicyJson(JsonElement table, string path)
    {
        foreach (var field in ContainerPolicyFields)
            if (Members(table, field).Any())
                throw new RouterConfigException($"{path}.{field} belongs in the engine's Docker block");
    }

    private static void CheckContainerPolicyJson(JsonElement docker, string path)
    {
        if (docker.TryGetProperty("init", out var init) && init.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new RouterConfigException($"{path}.init must be a boolean");
        if (docker.TryGetProperty("restart", out var restart) && restart.ValueKind != JsonValueKind.String)
            throw new RouterConfigException($"{path}.restart must be a string");
        if (!docker.TryGetProperty("log_rotation", out var rotation)) return;

        var rotationPath = $"{path}.log_rotation";
        if (rotation.ValueKind != JsonValueKind.Object)
            throw new RouterConfigException($"{rotationPath} must be an object");
        CheckMember(rotation, "max_size", $"{rotationPath}.max_size");
        CheckMember(rotation, "max_files", $"{rotationPath}.max_files");
        foreach (var field in rotation.EnumerateObject())
        {
            switch (field.Name)
            {
                case "max_size":
                    if (field.Value.ValueKind != JsonValueKind.String)
                        throw new RouterConfigException($"{rotationPath}.max_size must be a string");
                    break;
                case "max_files":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out _))
                        throw new RouterConfigException($"{rotationPath}.max_files must be an Int32 integer");
                    break;
                default:
                    throw new RouterConfigException($"{rotationPath}.{field.Name} is not a log rotation setting");
            }
        }
    }

    private static void RejectMisplacedContainerPolicyToml(TomlTable table, string path)
    {
        foreach (var field in ContainerPolicyFields)
            if (HasTomlMember(table, field))
                throw new RouterConfigException($"{path}.{field} belongs in the engine's Docker block");
    }

    private static void CheckContainerPolicyToml(TomlTable root, TomlTable engines, string engineName, TomlTable engine, string path)
    {
        RejectMisplacedContainerPolicyToml(engine, path);
        foreach (var docker in TomlMembers(engine, "docker").OfType<TomlTable>())
        {
            if (!ContainerPolicyFields.Any(field => HasTomlMember(docker, field))) continue;
            CheckTomlMember(root, "engine", "engine");
            CheckTomlMember(engines, engineName, path);
            CheckTomlFields(engine, path, "catalog", "docker", "lifecycle_owner", "remote", "lifecycle");
            foreach (var catalog in TomlMembers(engine, "catalog").OfType<TomlTable>())
                CheckTomlSelectors(catalog, path);
            CheckContainerPolicyValuesToml(docker, $"{path}.docker");
        }
    }

    private static void CheckContainerPolicyValuesToml(TomlTable docker, string path)
    {
        foreach (var field in ConfigJsonContext.Default.DockerDto.Properties)
            CheckTomlMember(docker, field.Name, $"{path}.{field.Name}");
        if (TomlMembers(docker, "init").Any(value => value is not bool))
            throw new RouterConfigException($"{path}.init must be a boolean");
        if (TomlMembers(docker, "restart").Any(value => value is not string))
            throw new RouterConfigException($"{path}.restart must be a string");
        foreach (var value in TomlMembers(docker, "log_rotation"))
        {
            var rotationPath = $"{path}.log_rotation";
            if (value is not TomlTable rotation)
                throw new RouterConfigException($"{rotationPath} must be a table");
            CheckTomlFields(rotation, rotationPath, "max_size", "max_files");
            foreach (var (key, field) in rotation)
            {
                if (key.Equals("max_size", StringComparison.OrdinalIgnoreCase))
                {
                    if (field is not string)
                        throw new RouterConfigException($"{rotationPath}.max_size must be a string");
                }
                else if (key.Equals("max_files", StringComparison.OrdinalIgnoreCase))
                {
                    if (field is not long files || files is < int.MinValue or > int.MaxValue)
                        throw new RouterConfigException($"{rotationPath}.max_files must be an Int32 integer");
                }
                else
                    throw new RouterConfigException($"{rotationPath}.{key} is not a log rotation setting");
            }
        }
    }
}
