using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

/// <summary>Engine kinds the library ships adapters for. New kinds = new adapter class,
/// still no caller code changes; unknown kinds fail fast at config load.</summary>
public enum EngineKind : byte { OpenAiCompatible }

/// <summary>Declared lifecycle command: exec template with {{model}}/{{profile}}/{{gpu}}/{{suffixCache}} expansion.</summary>
public sealed class LifecycleCommand
{
    public required string Exec { get; init; }
    public string[] Args { get; init; } = [];
    public bool WaitHealth { get; init; } = true;
    public int WaitTimeoutMs { get; init; } = 7_200_000;   // select-model strata waits up to 2h
}

/// <summary>How an engine's model loads are managed. Delegated means a selector program does it (the
/// historical <c>select = { exec = … }</c>); Docker means the router drives the Engine API itself and can
/// therefore prove which containers it owns.</summary>
public enum EngineLifecycleKind : byte { Delegated, Docker }

/// <summary>Who may change the engine's model residency. Independent of the inference endpoint.</summary>
public enum EngineLifecycleOwner : byte { External, Router, Delegated }

/// <summary>Native Docker lifecycle for one engine: the image, how it is launched, and how long the
/// router waits for it to prove it is serving. What readiness must prove stays in config, because the
/// router can only verify what it can name.</summary>
public sealed class DockerLifecycleConfig
{
    /// <summary>Local image lookup reference used for preflight. Launch uses the pinned image ID.</summary>
    public required string Image { get; init; }
    public DockerImageIdentity? ImageIdentity { get; init; }
    internal DockerLaunchResources? Resources { get; init; }
    internal DockerTargetPlans? TargetPlans { get; init; }
    internal bool HasUnqualifiedTargetDocker => TargetPlans is not null;
    internal DockerContainerPolicy? ContainerPolicy { get; init; }
    internal bool HasUnqualifiedLaunchOptions => Entrypoint is not null || IpcMode is not null || ContainerPolicy is not null;
    /// <summary><c>unix:///path/to/sock</c> or <c>http(s)://host:port</c>.</summary>
    public string Socket { get; init; } = "unix:///var/run/docker.sock";
    /// <summary>Pinned so a daemon update cannot change the API under a running router.</summary>
    public string ApiVersion { get; init; } = "v1.44";
    /// <summary>Written into the container labels, and what makes a container ours on a shared host.
    /// Required rather than defaulted: two routers sharing one daemon with the same id would each be
    /// entitled to stop the other's containers.</summary>
    public required string RouterId { get; init; }
    public string[] Command { get; init; } = [];
    public string? Entrypoint { get; init; }
    public string? IpcMode { get; init; }
    public string[] Env { get; init; } = [];
    public int StopTimeoutMs { get; init; } = 30_000;
    public int? HostPort { get; init; }
    /// <summary>Interface a published port binds to. Defaults to loopback, because a router with no
    /// ingress credential must not hand the engine a public port.</summary>
    public string HostIp { get; init; } = "127.0.0.1";
    /// <summary>Remove the containers we stopped as part of a switch. Leaving them behind means every
    /// model change adds another stopped container to the host.</summary>
    public bool RemoveStopped { get; init; } = true;
    public int ContainerPort { get; init; } = 8080;
    public string? NetworkMode { get; init; }
    public bool WaitHealth { get; init; } = true;
    public int WaitTimeoutMs { get; init; } = 600_000;
    /// <summary>Deadline for one Engine API call. A wedged daemon must not be able to hold a switch
    /// open forever: the wait above is how long a model may take to load, not how long one HTTP call
    /// may take.</summary>
    public int CallTimeoutMs { get; init; } = 15_000;
}

/// <summary>The local image ID, named manifest digest and platform a native switch must verify.</summary>
public sealed record DockerImageIdentity
{
    public DockerImageIdentity(string id, string repoDigest, string platform)
    {
        if (!IsFullSha256(id.AsSpan()))
            throw new ArgumentException("must be sha256: followed by 64 hex digits", nameof(id));
        if (!IsNamedDigest(repoDigest))
            throw new ArgumentException("must be a repository name followed by @sha256: and 64 hex digits", nameof(repoDigest));
        if (!IsCanonicalPlatform(platform))
            throw new ArgumentException("must be a lowercase os/architecture[/variant]", nameof(platform));
        Id = id;
        RepoDigest = repoDigest;
        Platform = platform;
    }

    public string Id { get; }
    public string RepoDigest { get; }
    public string Platform { get; }

    internal static bool IsFullSha256(ReadOnlySpan<char> value)
    {
        const string prefix = "sha256:";
        if (value.Length != prefix.Length + 64 || !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        foreach (var digit in value[prefix.Length..])
            if (!char.IsAsciiHexDigit(digit)) return false;
        return true;
    }

    internal static bool IsNamedDigest(string? value)
    {
        var separator = value?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        if (value is null || separator < 1 || !IsFullSha256(value.AsSpan(separator + 1))) return false;
        var repository = value.AsSpan(0, separator);
        if (repository[0] == '/' || repository[^1] == '/' || repository.Contains("//", StringComparison.Ordinal))
            return false;
        foreach (var character in repository)
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) &&
                character is not ('.' or '_' or '-' or '/' or ':' or '[' or ']')) return false;
        // Colons before the final slash can be registry ports. A tag belongs in the locator.
        return !repository[(repository.LastIndexOf('/') + 1)..].Contains(':');
    }

    internal static bool IsCanonicalPlatform(string? value)
    {
        if (value is null) return false;
        var segments = value.Split('/');
        return segments.Length is 2 or 3 && segments.All(IsPlatformSegment);
    }

    internal static bool IsPlatformSegment(string? segment) =>
        segment is { Length: > 0 } &&
        (char.IsAsciiLetterLower(segment[0]) || char.IsAsciiDigit(segment[0])) &&
        segment.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
}

/// <summary>Health probe definition. Expect is a tiny comparison DSL: "path OP value" with
/// path = dotted JSON path, OP in { >, >=, ==, !=, <, <= }, value = number or string literal.</summary>
public sealed class HealthProbeDefinition
{
    public required string Path { get; init; } = "/props";
    public string? Expect { get; init; }
    public int IntervalMs { get; init; } = 2_000;
    public int TimeoutMs { get; init; } = 5_000;
    /// <summary>Dotted path in the probe JSON reporting the active context window (tokens),
    /// e.g. "n_ctx". Empty when the engine does not expose it.</summary>
    public string ContextPath { get; init; } = "n_ctx";
    /// <summary>Dotted path reporting active concurrent slots, e.g. "total_slots".</summary>
    public string SlotsPath { get; init; } = "total_slots";
}

public sealed class EngineCapabilities
{
    public bool Vision { get; set; }
    public bool SuffixCache { get; set; }
    public long MaxContext { get; set; }
    public int MaxSlots { get; set; } = 1;
}

/// <summary>One model the engine can serve (catalog), with switch command + aliases.</summary>
/// <summary>A serving profile: a context-window tier with its slot capacity. Engines load a
/// profile via their lifecycle command; requests whose required context exceeds the active
/// profile's window are queued behind an escalation (profile switch), not compacted.</summary>
public sealed record ServingProfile
{
    public required string Name { get; init; }
    /// <summary>Maximum context window this profile provides, in tokens.</summary>
    public required long MaxContext { get; init; }
    /// <summary>Concurrent slots this profile allows (engine capacity while active).</summary>
    public int MaxSlots { get; init; } = 1;
}

public sealed record EngineModelDefinition
{
    public required string Name { get; init; }
    public string[] Aliases { get; init; } = [];
    public string[] Profiles { get; init; } = [];
    public LifecycleCommand? Select { get; init; }
    public string? Profile { get; init; }             // default profile for switch
    /// <summary>Serving profiles this model can be served under (context tier ladder).</summary>
    public IReadOnlyList<ServingProfile> ServingProfiles { get; init; } =
        Array.Empty<ServingProfile>();
}

public sealed class EngineDefinition
{
    public required string Name { get; init; }
    public EngineKind Kind { get; init; }
    public required Uri Endpoint { get; init; }       // base incl. /v1
    public string? ApiKey { get; init; }
    public string? ApiKeyFile { get; init; }
    public HealthProbeDefinition? Health { get; init; }
    public string ModelsEndpoint { get; init; } = "/models";
    /// <summary>Dotted path inside a model row to its observed effective serving context in tokens.</summary>
    public string? ModelsContextWindowPath { get; init; }
    /// <summary>When true, an entry counts as loaded only when its status says loaded
    /// (llama.cpp-style servers with alias entries).</summary>
    public bool LoadedRequiresStatusLoaded { get; init; }
    public EngineCapabilities Capabilities { get; init; } = new();
    public IReadOnlyDictionary<string, EngineModelDefinition> Catalog { get; init; } =
        new Dictionary<string, EngineModelDefinition>(StringComparer.Ordinal);
    /// <summary>Ceiling on the wait for the engine's *first response byte* after the request is sent
    /// (response headers for a non-stream call, first byte for a stream). 0 disables it. Once the
    /// engine has started answering, a long generation is never cut off — this bounds a wedged
    /// engine, not a slow completion. Over the limit the ticket fails `engine_timeout` (504).</summary>
    public int ResponseTimeoutMs { get; init; } = 600_000;
    /// <summary>HTTP SSE timeout on a pending upstream read after the first byte. Zero disables it.</summary>
    public int StreamIdleTimeoutMs { get; init; }
    /// <summary>Legacy forward-only flag. A false value does not grant lifecycle authority.</summary>
    public bool Remote { get; init; }
    /// <summary>Configured lifecycle owner. Null uses the legacy provider declarations.</summary>
    public EngineLifecycleOwner? LifecycleOwner { get; init; }
    public EngineLifecycleKind Lifecycle { get; init; } = EngineLifecycleKind.Delegated;
    public DockerLifecycleConfig? Docker { get; init; }

    // Loaded legacy configurations retain their existing Docker transport and delegated declaration.
    internal bool LegacyLifecycleConfiguration { get; init; }
    internal bool HasExplicitRouterLifecycle => LifecycleOwner == EngineLifecycleOwner.Router && !LegacyLifecycleConfiguration;

    internal EngineLifecycleOwner ResolveLifecycleOwner()
    {
        var native = Lifecycle == EngineLifecycleKind.Docker && Docker is not null;
        var owner = LifecycleOwner ?? (Remote ? EngineLifecycleOwner.External
            : native ? EngineLifecycleOwner.Router
            : Catalog.Values.Any(DelegatedExecProvider.HasUsableSelector) ? EngineLifecycleOwner.Delegated
            : EngineLifecycleOwner.External);
        if (Remote && LifecycleOwner.HasValue && !LegacyLifecycleConfiguration)
            throw new RouterConfigException($"engine.{Name}: remove remote when setting lifecycle_owner");
        if (LifecycleOwner is null || LegacyLifecycleConfiguration) return owner;
        if (!Endpoint.IsAbsoluteUri || (Endpoint.Scheme != Uri.UriSchemeHttp && Endpoint.Scheme != Uri.UriSchemeHttps))
            throw new RouterConfigException($"engine.{Name}.endpoint must be an absolute http(s) URI");

        var hasSelector = Catalog.Values.Any(model => model.Select is not null);
        return owner switch
        {
            EngineLifecycleOwner.External when Lifecycle != EngineLifecycleKind.Docker && Docker is null && !hasSelector => owner,
            EngineLifecycleOwner.External => throw new RouterConfigException(
                $"engine.{Name}.lifecycle_owner = external conflicts with a lifecycle controller"),
            EngineLifecycleOwner.Router when !native || hasSelector || string.IsNullOrWhiteSpace(Docker!.Image) ||
                string.IsNullOrWhiteSpace(Docker.RouterId) => throw new RouterConfigException(
                $"engine.{Name}.lifecycle_owner = router requires native docker configuration and no delegated selectors"),
            EngineLifecycleOwner.Router when !Endpoint.IsLoopback ||
                !DockerLifecycleProvider.TryParseUnixSocket(Docker!.Socket, out var socketPath) || !Path.IsPathFullyQualified(socketPath) =>
                throw new RouterConfigException($"engine.{Name}.lifecycle_owner = router requires a loopback inference endpoint and a local Docker socket until managed peer fencing is available"),
            EngineLifecycleOwner.Router => owner,
            EngineLifecycleOwner.Delegated when Lifecycle == EngineLifecycleKind.Delegated && Docker is null &&
                Catalog.Values.Any(DelegatedExecProvider.HasUsableSelector) => owner,
            EngineLifecycleOwner.Delegated => throw new RouterConfigException(
                $"engine.{Name}.lifecycle_owner = delegated requires a usable selector and no native docker configuration"),
            _ => throw new RouterConfigException($"engine.{Name}.lifecycle_owner is invalid"),
        };
    }
}

/// <summary>Thread pre-configuration from TOML.</summary>
public sealed record ThreadDefinition
{
    public required ThreadPriority Priority { get; init; }
    public double Weight { get; init; } = 1.0;
    public string? ModelAffinity { get; init; }
}

public sealed class SchedulerConfig
{
    public int PrefixAffinityThresholdChars { get; set; } = PrefixAwareScheduler.AffinityThresholdChars;
    public double AffinityWeight { get; set; } = PrefixAwareScheduler.AffinityWeight;
    public double PrefillCostAlpha { get; set; } = 1.0;
    public int MaxQueuePerModel { get; set; } = 64;
    public int MaxInflightPerEngine { get; set; } = 8;
    public int PrefixTrieMaxNodes { get; set; } = 8192;
    /// <summary>Ceiling on how long an admitted request waits for a model switch it triggered
    /// (0 = wait as long as the switch itself takes, which is bounded by the engine's own
    /// `wait_timeout_ms`). The deadline only bounds queue wait, so without this a request can be
    /// parked in `WaitingForModel` for the whole model load.</summary>
    public int SwitchWaitTimeoutMs { get; set; }
    /// <summary>Chars-per-token estimate used to convert prompt chars into context demand.
    /// Conservative default; engines reporting n_ctx are compared in tokens.</summary>
    public double CharsPerTokenEstimate { get; set; } = 4.0;
}

public sealed class RouterConfig
{
    public string Listen { get; set; } = "http://127.0.0.1:8321";
    public string DefaultModel { get; set; } = "default";
    public ThreadPriority DefaultPriority { get; set; } = ThreadPriority.Normal();
    public bool AutoSwitch { get; set; } = true;
    public int SwitchCooldownMs { get; set; } = 15_000;
    public SchedulerConfig Scheduler { get; set; } = new();
    public IReadOnlyDictionary<string, ThreadDefinition> Threads { get; set; } =
        new Dictionary<string, ThreadDefinition>(StringComparer.Ordinal);
    /// <summary>Largest request body the router will read, on any surface. Bodies are buffered whole
    /// (the router forwards them as bytes), so this is also the bound on what one request can make the
    /// process hold. A caller that lies about Content-Length is still cut off, because the copy counts.</summary>
    public int MaxRequestBodyBytes { get; set; } = 16 * 1024 * 1024;
    /// <summary>Queued and pending-flush payload bytes per HTTP SSE stream. Zero disables the bounded relay.</summary>
    public int StreamBufferLimitBytes { get; init; }

    /// <summary>How many live event-feed subscriptions the router will hold. Each one is a stream a
    /// client keeps open; the cap is what stops the admin feed from being the thing that runs the
    /// process out of handles. Feeds beyond it are refused with 503.</summary>
    public int MaxEventSubscribers { get; set; } = 256;

    public IReadOnlyDictionary<string, EngineDefinition> Engines { get; set; } =
        new Dictionary<string, EngineDefinition>(StringComparer.Ordinal);
    /// <summary>Optional per-engine HTTP handler factory (test harnesses, custom transports).
    /// Production config leaves it null; engines then use a default HttpClient.</summary>
    public Func<EngineDefinition, HttpMessageHandler>? HttpHandlerFactory { get; set; }

    internal void ValidateLifecycleTopology(EngineDefinition? runtimeEngine = null)
    {
        if (runtimeEngine is not null)
        {
            if (runtimeEngine.HasExplicitRouterLifecycle &&
                (Engines.Count > 1 || (Engines.Count == 1 &&
                    !string.Equals(Engines.Values.Single().Name, runtimeEngine.Name, StringComparison.Ordinal))))
                throw DedicatedEngineRequired(runtimeEngine.Name);
            return;
        }

        if (Engines.Count <= 1) return;
        foreach (var engine in Engines.Values)
            if (engine.HasExplicitRouterLifecycle) throw DedicatedEngineRequired(engine.Name);
    }

    private static RouterConfigException DedicatedEngineRequired(string engine) => new(
        $"engine.{engine}.lifecycle_owner = router requires a dedicated single-engine configuration until managed peer fencing is available");
}

public sealed class RouterConfigException(string message) : Exception(message);
