using System.Net;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed class LifecycleAuthorityTests
{
    internal const string NamedPipeDockerAddress = "npipe:////./pipe/docker_engine";
    internal const string NamedPipeDockerError = "engine.fixture.docker.socket: Windows Docker named-pipe transport is unsupported";
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);
    private static readonly string[] ExpectedNativeCalls = ["ownership", "preflight", "launch"];

    [Theory]
    [InlineData(false, true, NamedPipeDockerAddress)]
    [InlineData(true, false, "NpIpE://./pipe/docker_engine")]
    [InlineData(true, false, @"\\.\pipe\docker_engine")]
    [InlineData(false, true, "//./pipe/docker_engine")]
    [InlineData(true, true, @"\\?\pipe\docker_engine")]
    [InlineData(false, false, @"\\?\UNC\server\pipe\docker_engine")]
    [InlineData(true, false, @"\\server\pipe\docker_engine")]
    [InlineData(false, true, "//server/pipe/docker_engine")]
    public void NamedPipeDockerRejectsConfiguredAddresses(bool json, bool explicitOwner, string socket)
    {
        var owner = explicitOwner ? Field(json, "lifecycle_owner", "\"router\"") : "";
        // Both JSON strings and TOML basic strings require escaped backslashes.
        var fields = owner + Docker(json, socket.Replace(@"\", @"\\", StringComparison.Ordinal));
        var error = Assert.Throws<RouterConfigException>(() => LoadConfig(json, fields));
        Assert.Equal(NamedPipeDockerError, error.Message);
        Assert.False(DockerLifecycleProvider.TryParseUnixSocket(socket, out _));

        if (socket == NamedPipeDockerAddress)
        {
            var dto = new ConfigDto
            {
                Engines = new Dictionary<string, EngineDto>(StringComparer.Ordinal)
                {
                    ["fixture"] = new()
                    {
                        Kind = "openai-compatible",
                        Endpoint = "http://127.0.0.1/v1",
                        LifecycleOwner = "router",
                        Docker = new DockerDto { Image = "fixture", RouterId = "test", Socket = socket },
                    },
                },
            };
            error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.FromDto(dto, "fixture"));
            Assert.Equal(NamedPipeDockerError, error.Message);
        }
    }

    [Theory]
    [InlineData("unix:////./pipe/docker_engine", "//./pipe/docker_engine")]
    [InlineData("//server/pipeline/socket", "//server/pipeline/socket")]
    public void NamedPipeDockerPreservesUnixPaths(string declaration, string expectedPath)
    {
        Assert.True(DockerLifecycleProvider.TryParseUnixSocket(declaration, out var path));
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public async Task NamedPipeDockerProgrammaticRuntimeRefusesBeforeBinding()
    {
        var definition = new EngineDefinition
        {
            Name = "fixture",
            Endpoint = new Uri("http://127.0.0.1/v1"),
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test", Socket = NamedPipeDockerAddress },
        };
        using var inference = new FakeDockerDaemonHandler { TransportFails = true };
        var provider = new RecordingLifecycleProvider { Kind = "docker" };

        var error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
        {
            await using var engine = new EngineRuntime(definition, new RouterConfig(), new EventRing(),
                inference, lifecycle: provider);
        });

        Assert.Equal(NamedPipeDockerError, error.Message);
        Assert.Empty(inference.Requests);
        Assert.Empty(provider.Calls);
    }

    [Fact(Timeout = 30_000)]
    public async Task NamedPipeDockerLegacyExternalForwardsResidentWithoutBinding()
    {
        var fields = "\nremote = true" + Docker(false, NamedPipeDockerAddress) + "\n[engine.fixture.catalog.resident]";
        var config = LoadConfig(false, fields, "peer.invalid");
        var definition = config.Engines["fixture"];
        Assert.Equal(EngineLifecycleOwner.External, definition.LifecycleOwner);
        Assert.Equal(NamedPipeDockerAddress, Assert.IsType<DockerLifecycleConfig>(definition.Docker).Socket);

        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var provider = new DockerLifecycleProvider(
            DockerLifecycleProviderTests.Cfg(socket: "https://controller.invalid"), definition.Name, daemon);
        using var inference = new FakeEngineHandler { LoadedModels = ["resident"] };
        config.HttpHandlerFactory = _ => inference;
        var feed = new EventRing();
        await using var router = new RouterCore(config, scheduler: null, events: feed, clock: TimeProvider.System,
            lifecycleFactory: _ => provider);
        using var deadline = new CancellationTokenSource(Ceiling);
        var ct = deadline.Token;
        router.Start();
        var engine = router.Engines["fixture"];
        await WaitHealthyAsync(engine, ct);
        Assert.Contains("resident", engine.LoadedModels);

        var ticket = await router.EnqueueAsync("fixture-thread", new InferenceRequest
        {
            Model = "resident",
            Body = """{"model":"resident","messages":[{"role":"user","content":"ping"}]}"""u8.ToArray(),
            Options = new(),
        }, ct);
        var response = await ticket.CompletionTask.WaitAsync(ct);

        Assert.Contains("pong", Encoding.UTF8.GetString(response), StringComparison.Ordinal);
        Assert.Equal(TicketState.Completed, ticket.State);
        Assert.Equal("fixture", ticket.Engine);
        Assert.Equal(1, inference.CompletionCalls);
        Assert.Equal("none", engine.LifecycleProvider);
        Assert.Empty(daemon.Requests);
        Assert.DoesNotContain(feed.Replay(0), item => item.Kind == "engine.switch_started");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyTransportAndProviderDeclarationsNormalizeInBothFormats(bool json)
    {
        foreach (var host in new[] { "127.0.0.1", "peer.invalid" })
        {
            foreach (var remote in new[] { "", "false", "true" })
            {
                var flag = remote.Length == 0 ? "" : Field(json, "remote", remote);
                var external = Load(json, flag, host);
                Assert.Equal(host, external.Endpoint.Host);
                Assert.Equal(EngineLifecycleOwner.External, external.LifecycleOwner);

                var delegated = Load(json, flag + Select(json, "\"fixture\""), host);
                var native = Load(json, flag + Docker(json, "https://controller.invalid"), host);
                Assert.Equal(remote == "true" ? EngineLifecycleOwner.External : EngineLifecycleOwner.Delegated,
                    delegated.LifecycleOwner);
                Assert.Equal(remote == "true" ? EngineLifecycleOwner.External : EngineLifecycleOwner.Router,
                    native.LifecycleOwner);
            }
        }
        Assert.Equal(EngineLifecycleOwner.Delegated,
            Load(json, Field(json, "lifecycle", "\"exec\"") + Select(json, "\"  \"")).LifecycleOwner);
        Assert.Equal(EngineLifecycleOwner.External, Load(json, Select(json, "\"  \"")).LifecycleOwner);
    }

    [Fact(Timeout = 30_000)]
    public async Task ExplicitDelegationChecksEachTargetAndUsesTheStartupSnapshotThroughLaunch()
    {
        const string fields = """

            lifecycle_owner = "delegated"
            [engine.fixture.catalog.target]
            aliases = ["alias"]
            profiles = ["tier"]
            default_profile = "tier"
            serving = [{ name = "tier", max_context = 8192 }]
            [engine.fixture.catalog.target.select]
            exec = "/bin/false"
            args = ["original"]
            [engine.fixture.catalog.absent]
            [engine.fixture.catalog.blank.select]
            exec = " "
            """;
        var config = LoadConfig(false, fields);
        var def = config.Engines["fixture"];
        var catalog = Assert.IsType<Dictionary<string, EngineModelDefinition>>(def.Catalog);
        var target = catalog["target"];
        var selector = Assert.IsType<LifecycleCommand>(target.Select);
        var serving = Assert.IsType<ServingProfile[]>(target.ServingProfiles);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LifecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingLifecycleProvider
        {
            Kind = "exec",
            PreflightHandler = () =>
            {
                entered.TrySetResult();
                return new ValueTask<LifecycleResult>(release.Task);
            },
        };
        using var handler = new FakeEngineHandler { LoadedModels = ["resident"] };
        var feed = new EventRing();
        await using var engine = new EngineRuntime(def, config, feed, handler, lifecycle: provider);
        using var deadline = new CancellationTokenSource(Ceiling);
        var ct = deadline.Token;
        engine.StartHealthLoop(ct);
        await WaitHealthyAsync(engine, ct);
        var proof = engine.Capability;

        foreach (var model in new[] { "absent", "blank" })
        {
            Assert.Equal("no_select_command", (await engine.WaitForModelAsync(model, Ceiling, ct)).Detail);
            Assert.Same(proof, engine.Capability);
            Assert.Empty(provider.Calls);
            var refusal = Assert.Single(feed.Replay(0), e => e.Kind == "engine.switch_failed" && e.Model == model);
            Assert.Contains("no usable select exec", refusal.Reason);
        }
        Assert.DoesNotContain(feed.Replay(0), e => e.Kind == "engine.switch_started");

        catalog["target"] = new EngineModelDefinition { Name = "replacement" };
        target.Aliases[0] = "changed";
        target.Profiles[0] = "changed";
        selector.Args[0] = "changed";
        serving[0] = new ServingProfile { Name = "changed", MaxContext = 1 };
        Assert.Null(engine.CanonicalModel("changed"));
        Assert.Equal("target", engine.CanonicalModel("alias"));
        Assert.Equal("tier", engine.FindProfile("target", 8192)?.Name);

        var pending = engine.WaitForModelAsync("alias", Ceiling, ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            catalog.Remove("target");
            target.Aliases[0] = "late";
            selector.Args[0] = "late";
            handler.LoadedModels = ["target"];
        }
        finally
        {
            release.TrySetResult(LifecycleResult.Ok());
        }
        Assert.True((await pending).IsReady);
        Assert.Equal(ExpectedNativeCalls, provider.Calls);
        Assert.Single(feed.Replay(0), e => e.Kind == "engine.switch_started");
        var launched = Assert.IsType<LifecycleTarget>(provider.LaunchedTarget);
        Assert.Equal("fixture", launched.EngineName);
        Assert.Equal("target", launched.Model);
        Assert.Equal("target", launched.Definition.Name);
        Assert.Equal(["alias"], launched.Definition.Aliases);
        Assert.Equal(["tier"], launched.Definition.Profiles);
        Assert.Equal("tier", Assert.Single(launched.Definition.ServingProfiles).Name);
        var launchedSelector = Assert.IsType<LifecycleCommand>(launched.Definition.Select);
        Assert.Equal("/bin/false", launchedSelector.Exec);
        Assert.Equal(["original"], launchedSelector.Args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitOwnersValidateProviderConfigurationAndRejectNewPeerActuation(bool json)
    {
        var external = Field(json, "lifecycle_owner", "\"external\"");
        var delegated = Field(json, "lifecycle_owner", "\"delegated\"");
        var router = Field(json, "lifecycle_owner", "\"router\"");
        var selector = Select(json, "\"fixture\"");
        var localDocker = Docker(json, "unix:///run/docker.sock");

        Assert.Equal(EngineLifecycleOwner.External, Load(json, external, "peer.invalid").LifecycleOwner);
        Assert.Equal(EngineLifecycleOwner.Delegated, Load(json, delegated + selector).LifecycleOwner);
        Assert.Equal(EngineLifecycleOwner.Router, Load(json, router + localDocker).LifecycleOwner);

        foreach (var remote in new[] { "false", "true" })
        {
            var conflict = Assert.Throws<RouterConfigException>(() => Load(json, external + Field(json, "remote", remote)));
            Assert.Contains("remove remote when setting lifecycle_owner", conflict.Message);
        }

        foreach (var invalid in new[]
        {
            external + selector, external + localDocker, external + Field(json, "lifecycle", "\"exec\""),
            delegated, delegated + localDocker, router, router + selector, router + localDocker + selector,
            Field(json, "lifecycle_owner", "\"peer\""),
        })
            Assert.Contains("engine.fixture", Assert.Throws<RouterConfigException>(() => Load(json, invalid)).Message);

        Assert.Contains("managed peer fencing",
            Assert.Throws<RouterConfigException>(() => Load(json, router + localDocker, "peer.invalid")).Message);
        Assert.Contains("managed peer fencing",
            Assert.Throws<RouterConfigException>(() => Load(json, router + Docker(json, "https://controller.invalid"))).Message);
        var sibling = json
            ? ",\"other\":{\"kind\":\"openai-compatible\",\"endpoint\":\"http://localhost:10/v1\"}"
            : "\n[engine.other]\nkind = \"openai-compatible\"\nendpoint = \"http://localhost:10/v1\"";
        Assert.Contains("dedicated single-engine configuration",
            Assert.Throws<RouterConfigException>(() => Load(json, router + localDocker, sibling: sibling)).Message);
        Assert.Equal(EngineLifecycleOwner.Router, Load(json, localDocker, sibling: sibling).LifecycleOwner);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("external", "exec")]
    [InlineData("external", "docker")]
    [InlineData("exec", "exec")]
    [InlineData("docker", "docker")]
    public async Task ExternalEngineNeverBindsAnInjectedProvider(string configuration, string kind)
    {
        using var handler = new FakeEngineHandler { LoadedModels = ["resident"] };
        var fields = configuration switch
        {
            "external" => "\nlifecycle_owner = \"external\"\n[engine.fixture.catalog.target]",
            "exec" => "\nremote = true" + Select(false, "\"/bin/false\""),
            _ => "\nremote = true" + Docker(false, "https://controller.invalid") + "\n[engine.fixture.catalog.target]",
        };
        var config = LoadConfig(false, fields, "peer.invalid");
        var def = config.Engines["fixture"];
        var provider = new RecordingLifecycleProvider { Kind = kind };
        var feed = new EventRing();
        await using var engine = new EngineRuntime(def, config, feed, handler, lifecycle: provider);
        using var deadline = new CancellationTokenSource(Ceiling);
        engine.StartHealthLoop(deadline.Token);
        await WaitHealthyAsync(engine, deadline.Token);
        var before = engine.Capability;

        Assert.True((await engine.WaitForModelAsync("resident", Ceiling, deadline.Token)).IsReady);
        var refused = await engine.WaitForModelAsync("target", Ceiling, deadline.Token);
        Assert.Equal("no_lifecycle_owner", refused.Detail);
        Assert.Same(before, engine.Capability);
        Assert.Empty(provider.Calls);
        Assert.Equal("none", engine.LifecycleProvider);
        Assert.DoesNotContain(feed.Replay(0), e => e.Kind == "engine.switch_started");
    }

    [Theory(Timeout = 30_000)]
    [InlineData("peer.invalid", "unix:///run/docker.sock", false)]
    [InlineData("127.0.0.1", "http://controller.invalid", false)]
    [InlineData("127.0.0.1", "https://controller.invalid", false)]
    [InlineData("127.0.0.1", "unix:///run/docker.sock", true)]
    public async Task LegacyNativeConfigurationCanStillUseItsPreviouslySupportedProviderScope(string host, string socket, bool multiple)
    {
        const string sibling = "\n[engine.other]\nkind = \"openai-compatible\"\nendpoint = \"http://localhost:10/v1\"";
        var fields = Docker(false, socket) + "\n[engine.fixture.catalog.target]";
        var config = LoadConfig(false, fields, host, multiple ? sibling : "");
        using var handler = new FakeEngineHandler { LoadedModels = ["target"] };
        var provider = new RecordingLifecycleProvider { Kind = "docker" };
        await using var engine = new EngineRuntime(config.Engines["fixture"], config, new EventRing(), handler, lifecycle: provider);

        Assert.True((await engine.WaitForModelAsync("target", Ceiling, CancellationToken.None)).IsReady);
        Assert.Equal(ExpectedNativeCalls, provider.Calls);
        Assert.Equal("fixture", provider.LaunchedTarget?.EngineName);
        Assert.Equal("target", provider.LaunchedTarget?.Model);
        Assert.Empty(provider.StoppedPeers);
        Assert.Throws<RouterConfigException>(() => LoadConfig(false, "\nlifecycle_owner = \"router\"" + fields,
            host, multiple ? sibling : ""));
    }

    [Theory]
    [InlineData("peer", "loopback")]
    [InlineData("socket", "local Docker socket")]
    [InlineData("shared", "dedicated single-engine")]
    [InlineData("sibling-only", "dedicated single-engine")]
    [InlineData("missing", "requires native docker")]
    [InlineData("external", "conflicts with a lifecycle controller")]
    [InlineData("delegated", "requires a usable selector")]
    [InlineData("remote", "remove remote")]
    [InlineData("scheme", "absolute http(s) URI")]
    public async Task ProgrammaticExplicitOwnerRejectsInvalidStandaloneOrConfiguredRuntime(string violation, string diagnosis)
    {
        var def = new EngineDefinition
        {
            Name = "fixture",
            Endpoint = new Uri(violation switch
            {
                "peer" => "http://peer.invalid/v1",
                "scheme" => "file:///v1",
                _ => "http://localhost/v1",
            }),
            LifecycleOwner = violation switch
            {
                "external" => EngineLifecycleOwner.External,
                "delegated" => EngineLifecycleOwner.Delegated,
                _ => EngineLifecycleOwner.Router,
            },
            Remote = violation == "remote",
            Lifecycle = violation == "delegated" ? EngineLifecycleKind.Delegated : EngineLifecycleKind.Docker,
            Docker = violation is "missing" or "delegated" ? null : new DockerLifecycleConfig
            {
                Image = "fixture", RouterId = "fixture",
                Socket = violation == "socket" ? "https://controller.invalid" : "unix:///run/docker.sock",
            },
        };
        var engines = new Dictionary<string, EngineDefinition>();
        if (violation == "shared") engines[def.Name] = def;
        if (violation is "shared" or "sibling-only")
            engines["other"] = new EngineDefinition { Name = "other", Endpoint = new Uri("http://localhost:10/v1") };
        var config = new RouterConfig { Engines = engines };
        using var handler = new FakeEngineHandler();
        var provider = new RecordingLifecycleProvider { Kind = "docker" };

        var error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
        {
            await using var engine = new EngineRuntime(def, config, new EventRing(), handler, lifecycle: provider);
        });
        Assert.Contains("engine.fixture", error.Message);
        Assert.Contains(diagnosis, error.Message);
        Assert.Empty(provider.Calls);
        if (violation == "shared")
        {
            error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
            {
                await using var router = new RouterCore(config);
            });
            Assert.Contains(diagnosis, error.Message);
        }
    }

    [Theory]
    [InlineData("docker", "docker")]
    [InlineData(null, "<null>")]
    public async Task ManagedEngineRejectsAnInjectedProviderOfTheWrongKind(string? kind, string diagnosis)
    {
        var def = Load(false, Select(false, "\"fixture\""));
        var provider = new RecordingLifecycleProvider { Kind = kind! };
        using var handler = new FakeEngineHandler();

        var error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
        {
            await using var engine = new EngineRuntime(def, new RouterConfig(), new EventRing(), handler, lifecycle: provider);
        });

        Assert.Contains("engine.fixture", error.Message);
        Assert.Contains($"injected lifecycle provider kind '{diagnosis}'", error.Message);
        Assert.Contains("configured kind 'exec'", error.Message);
        Assert.Empty(provider.Calls);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("", "no_lifecycle_owner")]
    [InlineData("\nremote = false", "no_lifecycle_owner")]
    [InlineData("\nlifecycle = \"exec\"", "no_select_command")]
    public async Task LegacySelectorlessRefusalReflectsWhetherAnOwnerWasDeclared(string fields, string reason)
    {
        var def = Load(false, fields + "\n[engine.fixture.catalog.target]");
        using var handler = new FakeEngineHandler { LoadedModels = ["resident"] };
        var provider = new RecordingLifecycleProvider { Kind = "exec" };
        var feed = new EventRing();
        await using var engine = new EngineRuntime(def, new RouterConfig(), feed, handler, lifecycle: provider);
        using var deadline = new CancellationTokenSource(Ceiling);
        engine.StartHealthLoop(deadline.Token);
        await WaitHealthyAsync(engine, deadline.Token);
        var before = engine.Capability;

        var refused = await engine.WaitForModelAsync("target", Ceiling, deadline.Token);

        Assert.Equal(reason, refused.Detail);
        Assert.Same(before, engine.Capability);
        Assert.True((await engine.WaitForModelAsync("resident", Ceiling, deadline.Token)).IsReady);
        Assert.Empty(provider.Calls);
        Assert.DoesNotContain(feed.Replay(0), e => e.Kind == "engine.switch_started");
    }

    [Fact(Timeout = 30_000)]
    public async Task MatchingNativeProviderReceivesOnlyTheBoundEngineAndCanonicalTarget()
    {
        using var handler = new FakeEngineHandler { LoadedModels = ["target"] };
        var catalog = new Dictionary<string, EngineModelDefinition>
        {
            ["target"] = new() { Name = "target", Aliases = ["alias"] },
        };
        var def = new EngineDefinition
        {
            Name = "fixture",
            Endpoint = new Uri("http://localhost/v1"),
            LifecycleOwner = EngineLifecycleOwner.Router,
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test" },
            Catalog = catalog,
        };
        var provider = new RecordingLifecycleProvider { Kind = "docker" };
        await using var engine = new EngineRuntime(def, new RouterConfig(), new EventRing(), handler, lifecycle: provider);
        catalog["late"] = new EngineModelDefinition { Name = "late" };

        var refused = await engine.WaitForModelAsync("late", Ceiling, CancellationToken.None);
        Assert.Equal("no_select_command", refused.Detail);
        Assert.Empty(provider.Calls);
        var selected = await engine.WaitForModelAsync("alias", Ceiling, CancellationToken.None);
        Assert.True(selected.IsReady);
        Assert.Equal(ExpectedNativeCalls, provider.Calls);
        Assert.Equal("fixture", provider.LaunchedTarget?.EngineName);
        Assert.Equal("target", provider.LaunchedTarget?.Model);
        Assert.Equal("target", provider.LaunchedTarget?.Definition.Name);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("omitted", false)]
    [InlineData("false", false)]
    [InlineData("true", false)]
    [InlineData("true", true)]
    [InlineData("exec", false)]
    [InlineData("docker", false)]
    [InlineData("external", false)]
    public async Task ExternalHttpForwardsResidentAndKeepsTheUnloadedRefusalContract(string ownership, bool otherReady)
    {
        const string unloadedCatalog = "\n[engine.fixture.catalog.unloaded]";
        var fields = ownership switch
        {
            "external" => "\nlifecycle_owner = \"external\"" + unloadedCatalog,
            "exec" => "\nremote = true" + unloadedCatalog + "\n[engine.fixture.catalog.unloaded.select]\nexec = \"/bin/false\"",
            "docker" => "\nremote = true" + Docker(false, "unix:///air-test-daemon-does-not-exist.sock") + unloadedCatalog,
            "omitted" => unloadedCatalog,
            _ => Field(false, "remote", ownership) + unloadedCatalog,
        };
        const string sibling = """

            [engine.other]
            kind = "openai-compatible"
            endpoint = "http://other.invalid/v1"
            remote = true
            [engine.other.catalog.unloaded]
            """;
        var config = LoadConfig(false, fields, "peer.invalid", otherReady ? sibling : "");
        using var handler = new FakeEngineHandler { LoadedModels = ["resident"] };
        using var other = new FakeEngineHandler { LoadedModels = ["unloaded"] };
        config.HttpHandlerFactory = engine => engine.Name == "fixture" ? handler : other;
        var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], configOverride: config);
        await using var _ = app;
        using var deadline = new CancellationTokenSource(Ceiling);
        var ct = deadline.Token;
        await app.StartAsync(ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        foreach (var engine in router.Engines.Values)
            await WaitHealthyAsync(engine, ct);
        var before = router.Engines["fixture"].Capability;
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var residentRequest = Request("resident");
        using var resident = await http.PostAsync("/v1/chat/completions", residentRequest, ct);
        Assert.Equal(HttpStatusCode.OK, resident.StatusCode);
        Assert.Contains("pong", await resident.Content.ReadAsStringAsync(ct));

        using var unloadedRequest = Request("unloaded");
        using var unloaded = await http.PostAsync("/v1/chat/completions", unloadedRequest, ct);
        Assert.Equal(otherReady ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, unloaded.StatusCode);
        var body = await unloaded.Content.ReadAsStringAsync(ct);
        if (otherReady) Assert.Contains("pong", body);
        else
        {
            using var json = JsonDocument.Parse(body);
            var error = json.RootElement.GetProperty("error");
            Assert.Equal("switch_failed", error.GetProperty("code").GetString());
            Assert.EndsWith("(no_lifecycle_owner)", error.GetProperty("message").GetString());
        }

        Assert.Same(before, router.Engines["fixture"].Capability);
        Assert.Equal(1, handler.CompletionCalls);
        Assert.Equal(otherReady ? 1 : 0, other.CompletionCalls);
        Assert.DoesNotContain(router.Events.Replay(0), e => e.Kind == "engine.switch_started");
    }

    private static async Task WaitHealthyAsync(EngineRuntime engine, CancellationToken token)
    {
        while (!engine.Healthy)
            await Task.Delay(10, token);
    }

    private static StringContent Request(string model) => new(
        $$"""{"model":"{{model}}","messages":[{"role":"user","content":"test"}]}""", Encoding.UTF8, "application/json");

    private static EngineDefinition Load(bool json, string fields, string host = "127.0.0.1", string sibling = "") =>
        LoadConfig(json, fields, host, sibling).Engines["fixture"];

    private static RouterConfig LoadConfig(bool json, string fields, string host = "127.0.0.1", string sibling = "")
    {
        var source = json
            ? "{\"engine\":{\"fixture\":{\"kind\":\"openai-compatible\",\"endpoint\":\"http://" + host + "/v1\"" + fields + "}" + sibling + "}}"
            : $"[engine.fixture]\nkind = \"openai-compatible\"\nendpoint = \"http://{host}/v1\"\n{fields}{sibling}";
        return RouterConfigLoader.Load(source, isJson: json);
    }

    private static string Field(bool json, string name, string value) =>
        json ? $",\"{name}\":{value}" : $"\n{name} = {value}";

    private static string Select(bool json, string exec) => json
        ? $",\"catalog\":{{\"target\":{{\"select\":{{\"exec\":{exec}}}}}}}"
        : $"\n[engine.fixture.catalog.target.select]\nexec = {exec}";

    private static string Docker(bool json, string socket) => json
        ? $",\"docker\":{{\"image\":\"fixture\",\"router_id\":\"test\",\"socket\":\"{socket}\"}}"
        : $"\n[engine.fixture.docker]\nimage = \"fixture\"\nrouter_id = \"test\"\nsocket = \"{socket}\"";
}
