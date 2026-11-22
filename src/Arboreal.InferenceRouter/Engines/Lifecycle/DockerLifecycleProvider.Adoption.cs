using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

public sealed partial class DockerLifecycleProvider
{
    internal readonly record struct ResidentInspection(NativeLaunch Launch, NativeContainerObservation Baseline,
        bool SettingsMatch);

    internal async ValueTask<ResidentInspection?> InspectResidentAsync(
        LifecycleTarget target, EnginePeer peer, CancellationToken ct)
    {
        if (_cfg.ImageIdentity is not { } image || !DockerImageIdentity.IsFullSha256(peer.ImageId.AsSpan()) ||
            !IsFullContainerId(peer.Id) || !CanRestoreIncumbentUnderGate(peer.Id)) return null;

        var container = await CallAsync(HttpMethod.Get, $"{_cfg.ApiVersion}/containers/{peer.Id}/json", null, ct,
            requiredStatus: 200).ConfigureAwait(false);
        if (!container.Ok) return null;
        using var inspect = JsonDocument.Parse(container.Body);
        var actual = inspect.RootElement;
        var launch = new NativeLaunch(peer.Id, target.Model, target.EffectiveProfile, peer.ImageId!);
        var sample = ClassifyContainer(actual.Deserialize(DockerJsonContext.Default.DockerObservationResponse), launch);
        if (sample.Status != NativeContainerStatus.Running) return null;
        if (launch.ImageId != image.Id) return new ResidentInspection(launch, sample, false);

        var defaults = await CallAsync(HttpMethod.Get, $"{_cfg.ApiVersion}/images/{image.Id}/json", null, ct,
            requiredStatus: 200).ConfigureAwait(false);
        if (!defaults.Ok) return null;
        using var source = JsonDocument.Parse(defaults.Body);
        var imageInspect = source.RootElement;
        if (imageInspect.GetProperty("Id").GetString() != image.Id) return null;
        return new ResidentInspection(launch, sample,
            MatchesResidentCreate(BuildCreateRequest(target, image), actual, imageInspect.GetProperty("Config")));
    }

    internal async ValueTask<bool> StillSameResidentAsync(ResidentInspection verified, CancellationToken ct)
    {
        using var deadline = ReadinessDeadline.Start(ObservationClock, ObservationTimeout, ct);
        var final = await ObserveContainerAsync(verified.Launch, deadline).ConfigureAwait(false);
        return final.Outcome == ReadinessStepOutcome.Completed && final.Observation == verified.Baseline;
    }

    internal static bool MatchesResidentCreate(DockerCreateRequest plan, JsonElement inspect, JsonElement image)
    {
        var config = inspect.GetProperty("Config");
        var actual = config.Deserialize(DockerJsonContext.Default.DockerCreateRequest)!;
        var expected = plan;
        expected.Cmd ??= image.TryGetProperty("Cmd", out var cmd) ? Strings(cmd) : null;
        expected.Entrypoint ??= image.TryGetProperty("Entrypoint", out var entry) ? Strings(entry) : null;
        var env = image.TryGetProperty("Env", out var inherited) ? Strings(inherited) : null;
        var desiredEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in (env ?? []).Concat(expected.Env ?? []))
            desiredEnv[setting.Split('=', 2)[0]] = setting;
        if (!(actual.Env ?? []).Order(StringComparer.Ordinal).SequenceEqual(desiredEnv.Values.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return false;
        expected.Env = actual.Env;
        if (image.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            foreach (var label in labels.EnumerateObject()) expected.Labels.TryAdd(label.Name, label.Value.GetString()!);
        if (expected.Healthcheck is null) expected.Healthcheck = image.TryGetProperty("Healthcheck", out var health) &&
            health.ValueKind == JsonValueKind.Object ? health.Deserialize(DockerJsonContext.Default.DockerCreateHealthcheck) : null;
        if (expected.ExposedPorts is null && image.TryGetProperty("ExposedPorts", out var ports) &&
            ports.ValueKind == JsonValueKind.Object)
            expected.ExposedPorts = ports.EnumerateObject().ToDictionary(p => p.Name, _ => new DockerExposedPort(), StringComparer.Ordinal);

        var hostJson = inspect.GetProperty("HostConfig");
        if (hostJson.TryGetProperty("Binds", out var binds) && binds.ValueKind == JsonValueKind.Array && binds.GetArrayLength() > 0)
            return false;
        var host = hostJson.Deserialize(DockerJsonContext.Default.DockerHostConfig)!;
        actual.HostConfig = host;
        var wanted = expected.HostConfig ??= new();
        wanted.IpcMode ??= "private";
        wanted.ShmSize ??= 67108864;
        wanted.Memory ??= 0;
        wanted.MemorySwap ??= 0;
        wanted.NanoCpus ??= 0;
        wanted.RestartPolicy ??= new("no", 0);
        wanted.LogConfig ??= new("", new(StringComparer.Ordinal));
        if (wanted.LogConfig.Type.Length == 0 && host.LogConfig?.Type == "json-file")
            wanted.LogConfig = wanted.LogConfig with { Type = "json-file" };
        if (host.Devices is []) host.Devices = null;
        if (host.DeviceRequests is []) host.DeviceRequests = null;
        if (host.Mounts is []) host.Mounts = null;
        if (host.Ulimits is []) host.Ulimits = null;
        if (host.PortBindings is { Count: 0 }) host.PortBindings = null;
        return SupervisedLaunchEvidence.EffectiveSettingsMatch(expected, actual);
    }

    private static string[]? Strings(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null :
        value.EnumerateArray().Select(item => item.GetString()!).ToArray();
}
