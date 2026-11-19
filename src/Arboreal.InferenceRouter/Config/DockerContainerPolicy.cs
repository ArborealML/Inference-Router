using System.Globalization;

namespace Arboreal.InferenceRouter;

internal sealed record DockerContainerPolicy(bool? Init, string? Restart, (string MaxSize, int MaxFiles)? LogRotation)
{
    internal static DockerContainerPolicy Parse(DockerDto docker, string path)
    {
        if (docker.Init is false)
            throw new RouterConfigException($"{path}.init must be true when supplied");
        if (docker.Restart is not (null or "unless-stopped"))
            throw new RouterConfigException($"{path}.restart must be unless-stopped when supplied");
        return new DockerContainerPolicy(docker.Init, docker.Restart,
            docker.LogRotation is { } rotation ? ParseLogRotation(rotation, path) : null);
    }

    private static (string MaxSize, int MaxFiles) ParseLogRotation(DockerLogRotationDto rotation, string path)
    {
        if (rotation.MaxSize is not { } size || !IsLogSize(size))
            throw new RouterConfigException($"{path}.log_rotation.max_size must be a positive Int32 followed by m");
        if (rotation.MaxFiles is not int maxFiles || maxFiles <= 0)
            throw new RouterConfigException($"{path}.log_rotation.max_files must be a positive Int32");
        return (size, maxFiles);
    }

    private static bool IsLogSize(string size)
    {
        if (size.Length < 2 || size[^1] != 'm' || size[0] is < '1' or > '9') return false;
        var magnitude = size.AsSpan(0, size.Length - 1);
        foreach (var digit in magnitude)
            if (!char.IsAsciiDigit(digit)) return false;
        return int.TryParse(magnitude, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}
