using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ManagedVllmProfileTests
{
    private const string Model = "fixture-model";
    private const string Id = DockerLifecycleProviderTests.FullPeerId;
    private const int InitPid = 101;
    private const int ChildPid = 102;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private const string Settings = """
        auto_switch = true
        [engine.strata]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:18021/v1"
        lifecycle_owner = "router"
        lifecycle_group = "shared"
        models_context_window_path = "max_model_len"
        health_probe = { path = "/v1/models", slots_source = "vllm_argv", interval_ms = 600000 }
        [engine.strata.docker]
        image = "fixture/engine:local"
        image_id = "sha256:1111111111111111111111111111111111111111111111111111111111111111"
        image_repo_digest = "registry.local/strata@sha256:2222222222222222222222222222222222222222222222222222222222222222"
        image_platform = "linux/amd64"
        router_id = "r1"
        command = ["single"]
        env = ["MAX_SEQS={{max_slots}}", "API_SERVERS=1"]
        init = true
        remove_stopped = false
        host_port = 18021
        [engine.strata.catalog.fixture-model]
        default_profile = "one"
        serving = [{ name = "one", max_context = 262144, max_slots = 1 }, { name = "three", max_context = 524288, max_slots = 3 }]
        """;

    [Fact]
    public void ProjectsSelectedSlotsThroughLoadAndReload()
    {
        foreach (var settings in new[] { RouterConfigLoader.Load(Settings), RouterConfigLoader.LoadForReload(Settings, "candidate.toml").Config })
        {
            var engine = settings.Engines["strata"];
            var docker = engine.Docker!;
            var definition = engine.Catalog[Model];
            foreach (var tier in definition.ServingProfiles)
            {
                var plan = DockerLifecycleProvider.ProjectCreateRequest(docker, engine.Name,
                    new(engine.Name, Model, tier, definition), docker.ImageIdentity!);
                Assert.NotNull(plan.Cmd);
                Assert.Equal(["single"], plan.Cmd);
                Assert.Contains($"MAX_SEQS={tier.MaxSlots.ToString(CultureInfo.InvariantCulture)}", plan.Env!);
            }
        }
    }

    [Theory]
    [InlineData("vllm_argv", "bogus")]
    [InlineData("init = true", "init = false")]
    [InlineData("lifecycle_owner = \"router\"", "lifecycle_owner = \"external\"")]
    [InlineData("models_context_window_path = \"max_model_len\"", "")]
    [InlineData("MAX_SEQS={{max_slots}}", "MAX_SEQS=1")]
    [InlineData("\"API_SERVERS=1\"", "\"API_SERVERS=1\", \"EXTRA_ARGS=--max_num_seqs=2\"")]
    [InlineData("\"API_SERVERS=1\"", "\"API_SERVERS=1\", \"MAX_SEQS=3\"")]
    public void RefusesUnsupportedSourceOnLoadAndReload(string from, string to)
    {
        var invalid = Settings.Replace(from, to, StringComparison.Ordinal);
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(invalid));
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.LoadForReload(invalid, "candidate.toml"));
    }

    [Fact]
    public void RefusesShadowedJsonSlotSource()
    {
        const string json = """{"engine":{"strata":{"health_probe":{"slots_source":"vllm_argv","slots_source":null}}}}""";
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(json, isJson: true));
        Assert.Contains("health_probe.slots_source", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--max-num-seqs\0" + "1\0", 1)]
    [InlineData("--chat-template\0a file with spaces\0--max-num-seqs=3\0", 3)]
    [InlineData("", 0)]
    [InlineData("--max-num-seqs 3\0", 0)]
    [InlineData("--max-num-seqs\0", 0)]
    [InlineData("--max-num-seqs=0\0", 0)]
    [InlineData("--max-num-seqs=2147483648\0", 0)]
    [InlineData("--max-num-seqs=1\0--max-num-seqs=1\0", 0)]
    [InlineData("--max-num-seqs=1\0--max_num_seqs=3\0", 0)]
    [InlineData("--max-num-seqs=1\0--config\0settings\0", 0)]
    [InlineData("--max-num-seqs=1", null)]
    public void ReadsOnlyWholeNulDelimitedTokens(string flags, int? slots) =>
        Assert.Equal(slots, VllmProcessReader.ParseServingArgv(Argv(flags)));

    [Theory]
    [InlineData("matching", true)]
    [InlineData("descendant", true)]
    [InlineData("other-cgroup", false)]
    [InlineData("ambiguous", false)]
    [InlineData("different-parent", false)]
    [InlineData("oversized", false)]
    public void BindsOnlyTheSupportedInitAndChild(string shape, bool found)
    {
        using var proc = new ProcFixture();
        proc.Write(Argv("--max-num-seqs=1\0"), 200);
        switch (shape)
        {
            case "descendant": proc.WriteProcess(103, "vllm", ChildPid, 300, Id); break;
            case "other-cgroup": proc.WriteProcess(ChildPid, "vllm", InitPid, 200, new string('c', 64)); break;
            case "ambiguous": File.WriteAllText(proc.Children, "102 103 "); break;
            case "different-parent": proc.WriteProcess(ChildPid, "vllm", 103, 200, Id); break;
            case "oversized": File.WriteAllBytes(proc.Cmdline, new byte[65_537]); break;
        }
        var result = VllmProcessReader.Read(proc.Root, InitPid, Id, CancellationToken.None);
        if (found) Assert.Equal(new VllmProcessEvidence(new(InitPid, 100, ChildPid, 200), 1), result);
        else Assert.Null(result);
    }

    [Fact(Timeout = 15_000)]
    public async Task SettingsMatchingResidentStreamsWithoutReplacementAndRefreshRejectsNewChild()
    {
        using var fixture = new Fixture(adopt: true);
        await using var router = fixture.Start();
        var ticket = await EnqueueAsync(router, Model);
        await ticket.CompletionTask.WaitAsync(Limit);
        var runtime = router.Engines["strata"];
        Assert.Equal(1, runtime.Capability.Serving.TotalSlots);
        Assert.Equal(262144, runtime.Capability.Serving.ContextWindow);
        Assert.True(await runtime.ProbeHealthOnceAsync(CancellationToken.None));
        Assert.True(runtime.Healthy);
        fixture.Proc.Write(Argv("--max-num-seqs=1\0"), 201);
        Assert.True(await runtime.ProbeHealthOnceAsync(CancellationToken.None));
        Assert.False(runtime.Healthy);
        Assert.Equal(0, runtime.Capability.Serving.TotalSlots);
        Assert.Equal(NativeLaunchHealth.Invalid, runtime.Capability.NativeWatch?.Health);
        Assert.Empty(fixture.Writes);
        Assert.Equal(1, fixture.App.CompletionCalls);
    }

    [Fact(Timeout = 15_000)]
    public async Task ColdLaunchStreamsAndUnnamedFirstSendRechecksTheWarmProof()
    {
        using var fixture = new Fixture(adopt: false);
        await using var router = fixture.Start();
        var first = await EnqueueAsync(router, Model);
        await first.CompletionTask.WaitAsync(Limit);
        Assert.Equal(1, fixture.App.CompletionCalls);
        var engine = router.Engines["strata"];
        Assert.Equal(1, engine.Capability.Serving.TotalSlots);
        engine.AfterForwardAdmissionForTests = async () =>
        {
            fixture.Proc.Write(Argv("--max-num-seqs=1\0"), 202);
            await engine.ProbeHealthOnceAsync(CancellationToken.None);
        };
        var next = await EnqueueAsync(router, Model);
        var failure = await Assert.ThrowsAsync<RouterRequestException>(() => next.CompletionTask.WaitAsync(Limit));
        Assert.Equal("switch_failed", failure.Code);
        Assert.Equal(1, fixture.App.CompletionCalls);
        Assert.Equal(2, fixture.Writes.Count);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("wrapper")]
    [InlineData("context")]
    [InlineData("slots")]
    public async Task SettingsAndRuntimeMismatchRefuseWithoutReplacingAResident(string fault)
    {
        using var fixture = new Fixture(adopt: true);
        fixture.UnsupportedWrapper = fault == "wrapper";
        if (fault == "context") fixture.App.Window = 131072;
        if (fault == "slots") fixture.Proc.Write(Argv("--max-num-seqs=2\0"), 200);
        await using var router = fixture.Start();
        var ticket = await EnqueueAsync(router, Model);
        var failure = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(Limit));
        Assert.Equal("switch_failed", failure.Code);
        var reason = Assert.Single(router.Events.Replay(0), item => item.Kind == "engine.switch_failed" && item.Model == Model).Reason;
        Assert.Contains(fault switch
        {
            "context" => "model context: expected=262144 observed=131072",
            "slots" => "--max-num-seqs: expected=1 observed=2",
            _ => "vllm_launch_unsupported",
        }, reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Writes);
        Assert.Equal(0, fixture.App.CompletionCalls);
    }

    [Fact(Timeout = 15_000)]
    public async Task DockerGenerationChangeAfterArgvCannotPublishReadiness()
    {
        using var fixture = new Fixture(adopt: false);
        fixture.ChangeGenerationAtInspect = 3;
        await using var router = fixture.Start();
        var ticket = await EnqueueAsync(router, Model);
        var failure = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(Limit));
        Assert.Equal("switch_failed", failure.Code);
        var runtime = router.Engines["strata"];
        Assert.Equal(NativeLaunchHealth.Invalid, runtime.Capability.NativeWatch?.Health);
        Assert.Null(runtime.SwitchingToward);
        Assert.Equal(0, fixture.App.CompletionCalls);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("""{"data":[{"id":"fixture-model","max_model_len":1,"max_model_len":262144}]}""")]
    [InlineData("""{"data":[{"id":"fixture-model","max_model_len":262144},{"id":"fixture-model","max_model_len":262144}]}""")]
    public async Task AmbiguousModelContextExpiresAsUnknown(string roster)
    {
        var clock = new ReadinessDeadlineTests.ManualClock();
        using var fixture = new Fixture(adopt: false);
        fixture.App.Roster = roster;
        await using var router = fixture.Start(clock);
        var ticket = await EnqueueAsync(router, Model);
        await WaitForTimerCount(clock, 3);
        clock.AdvanceTo(10_000);
        var failure = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(Limit));
        Assert.Equal("switch_failed", failure.Code);
        Assert.Contains(router.Events.Replay(0), item => item.Kind == "engine.switch_failed" &&
            item.Reason?.Contains("unique loaded model row", StringComparison.Ordinal) == true);
        Assert.Equal(0, fixture.App.CompletionCalls);
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false, 600000, 0, 10000)]
    [InlineData(true, 600000, 0, 10000)]
    [InlineData(true, 600000, 12000, 22000)]
    [InlineData(true, 15000, 12000, 15000)]
    public async Task FailedEvidenceReleasesQueuedStreamWithoutReplayingTheOwner(bool missing, int startupMs, int firstAnswer, int expires)
    {
        var clock = new ReadinessDeadlineTests.ManualClock();
        using var fixture = new Fixture(adopt: false, startupMs);
        fixture.Proc.Write(Argv(missing ? "" : "--max-num-seqs=2\0"), 200);
        fixture.App.HoldFirstRead = true;
        fixture.App.FailAfterRead = missing ? 2 : int.MaxValue;
        fixture.HoldStop = true;
        await using var router = fixture.Start(clock);
        var owner = await EnqueueAsync(router, Model);
        try
        {
            await fixture.App.FirstRead.Task.WaitAsync(Limit);
            if (firstAnswer != 0) clock.AdvanceTo(firstAnswer);
            var next = await EnqueueAsync(router, "next");
            Assert.Equal(TicketState.Queued, next.State);
            fixture.App.ReleaseRead.TrySetResult();
            if (missing)
            {
                await WaitForTimerCount(clock, 3);
                clock.AdvanceTo(firstAnswer + 2_000);
                await fixture.App.LaterFailure.Task.WaitAsync(Limit);
                clock.AdvanceTo(expires - 1);
                Assert.False(owner.CompletionTask.IsCompleted);
                clock.AdvanceTo(expires);
            }
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => owner.CompletionTask.WaitAsync(Limit));
            Assert.Equal("switch_failed", failure.Code);
            Assert.Null(router.Engines["strata"].SwitchingToward);
            var reason = Assert.Single(router.Events.Replay(0), item => item.Kind == "engine.switch_failed" && item.Model == Model).Reason;
            Assert.Contains(missing ? "could not verify --max-num-seqs" : "--max-num-seqs: expected=1 observed=2", reason, StringComparison.Ordinal);
            await fixture.StopEntered.Task.WaitAsync(Limit);
            Assert.Equal(0, fixture.NextApp.CompletionCalls);
            fixture.ReleaseStop.TrySetResult();
            await next.CompletionTask.WaitAsync(Limit);
            Assert.Equal(TicketState.Completed, next.State);
            Assert.NotNull(next.StreamChannel);
            Assert.True(next.StreamChannel.Reader.TryRead(out var chunk));
            Assert.Contains("data:", Encoding.UTF8.GetString(chunk.Span), StringComparison.Ordinal);
            Assert.Single(fixture.Writes, write => write.Contains("/create", StringComparison.Ordinal));
            Assert.Equal(0, fixture.App.CompletionCalls);
            Assert.Single(router.Events.Replay(0), item => item.Kind == "request.selected" && item.TicketId == owner.Id);
        }
        finally
        {
            fixture.App.ReleaseRead.TrySetResult();
            fixture.ReleaseStop.TrySetResult();
        }
    }

    private static async Task WaitForTimerCount(ReadinessDeadlineTests.ManualClock clock, int count)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (clock.ActiveTimers < count) await Task.Delay(1, timeout.Token);
    }

    private static async Task<RouterTicket> EnqueueAsync(RouterCore router, string model) => await router.EnqueueAsync(model,
        new InferenceRequest { Model = model, Stream = true, Options = new(),
            Body = """{"messages":[{"role":"user","content":"hi"}],"stream":true}"""u8.ToArray() });

    private static byte[] Argv(string flags) => Encoding.UTF8.GetBytes("/app/venv/bin/python3\0/app/venv/bin/vllm\0serve\0model path\0" + flags);

    private sealed class ProcFixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
        public string Root => _directory.FullName;
        public string Children => Path.Combine(Root, "101", "task", "101", "children");
        public string Cmdline => Path.Combine(Root, "102", "cmdline");

        public void Write(byte[] argv, ulong childStart)
        {
            WriteProcess(InitPid, "docker-init", 1, 100, Id);
            WriteProcess(ChildPid, "vllm", InitPid, childStart, Id);
            Directory.CreateDirectory(Path.GetDirectoryName(Children)!);
            File.WriteAllText(Children, "102 ");
            File.WriteAllBytes(Cmdline, argv);
        }

        public void WriteProcess(int pid, string name, int parent, ulong start, string id)
        {
            var process = Path.Combine(Root, pid.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(process);
            File.WriteAllText(Path.Combine(process, "stat"), FormattableString.Invariant(
                $"{pid} ({name}) S {parent} {string.Join(' ', Enumerable.Repeat("0", 17))} {start} 0 0\n"));
            File.WriteAllText(Path.Combine(process, "cgroup"), $"0::/system.slice/docker-{id}.scope\n");
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class AppHandler : FakeEngineHandler
    {
        private int _reads;
        public int FailAfterRead { get; set; } = int.MaxValue;
        public int Window { get; set; } = 262144;
        public string? Roster { get; set; }
        public bool HoldFirstRead { get; set; }
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LaterFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/v1/models") return await base.SendAsync(request, ct);
            var call = Interlocked.Increment(ref _reads);
            if (call == 1)
            {
                FirstRead.TrySetResult();
                if (HoldFirstRead) await ReleaseRead.Task.WaitAsync(ct);
            }
            if (call > FailAfterRead)
            {
                LaterFailure.TrySetResult();
                return Reply(HttpStatusCode.ServiceUnavailable);
            }
            return Reply(HttpStatusCode.OK, Roster ?? FormattableString.Invariant($$"""{"data":[{"id":"fixture-model","max_model_len":{{Window}}}]}"""));
        }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly RouterConfig _config;
        private readonly DockerLifecycleProvider _provider;
        private readonly JsonObject _containerConfig;
        private readonly JsonNode _host;
        private string _state;
        private int _inspects;
        public ConcurrentQueue<string> Writes { get; } = new();
        public ProcFixture Proc { get; } = new();
        public AppHandler App { get; } = new();
        public FakeEngineHandler NextApp { get; } = new() { LoadedModels = ["next"], TotalSlots = 2 };
        public bool HoldStop { get; set; }
        public bool UnsupportedWrapper { get; set; }
        public int ChangeGenerationAtInspect { get; set; } = int.MaxValue;
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture(bool adopt, int startupMs = 600000)
        {
            _state = adopt ? "running" : "absent";
            var settings = Settings.Replace("init = true", FormattableString.Invariant($"init = true\nwait_timeout_ms = {startupMs}"), StringComparison.Ordinal);
            _config = RouterConfigLoader.Load(settings);
            var model = _config.Engines["strata"];
            var config = model.Docker!;
            _provider = new DockerLifecycleProvider(config, model.Name, this, localManaged: true) { VllmProcRoot = Proc.Root };
            var plan = _provider.BuildCreateRequest(new(model.Name, Model, null, model.Catalog[Model]), config.ImageIdentity!);
            _containerConfig = JsonSerializer.SerializeToNode(plan, DockerJsonContext.Default.DockerCreateRequest)!.AsObject();
            _host = _containerConfig["HostConfig"]!.DeepClone();
            _containerConfig.Remove("HostConfig");
            _containerConfig["Entrypoint"] = new JsonArray("bash", "docker/entrypoint.sh");
            _host["IpcMode"] = "private";
            _host["PidMode"] = "";
            _host["ShmSize"] = 67108864;
            _host["Memory"] = 0;
            _host["MemorySwap"] = 0;
            _host["NanoCpus"] = 0;
            _host["RestartPolicy"] = new JsonObject { ["Name"] = "no", ["MaximumRetryCount"] = 0 };
            _host["LogConfig"] = new JsonObject { ["Type"] = "json-file", ["Config"] = new JsonObject() };
            Proc.Write(Argv("--max-num-seqs=1\0"), 200);
        }

        public RouterCore Start(TimeProvider? readinessClock = null)
        {
            _config.Engines = new Dictionary<string, EngineDefinition>(_config.Engines, StringComparer.Ordinal)
            {
                ["next"] = new()
                {
                    Name = "next", Endpoint = new Uri("http://localhost:18022/v1"), LifecycleOwner = EngineLifecycleOwner.Router,
                    Lifecycle = EngineLifecycleKind.Docker, LifecycleGroup = "shared",
                    Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "r1" },
                    Catalog = new Dictionary<string, EngineModelDefinition> { ["next"] = new() { Name = "next" } },
                },
            };
            _config.HttpHandlerFactory = def => def.Name == "strata" ? App : NextApp;
            var router = new RouterCore(_config, null, null, TimeProvider.System,
                lifecycleFactory: def => def.Name == "strata" ? _provider : new NextProvider());
            router.Engines["strata"].ReadinessClockForTests = readinessClock ?? TimeProvider.System;
            router.Start();
            return router;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method != HttpMethod.Get) Writes.Enqueue(path);
            if (path.EndsWith("/version", StringComparison.Ordinal)) return Reply(HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
            if (path.Contains("/images/", StringComparison.Ordinal))
            {
                var image = JsonNode.Parse(DockerLifecycleProviderTests.Inspect())!;
                image["Config"] = JsonNode.Parse(UnsupportedWrapper ? """{"Entrypoint":["/other"],"Cmd":["single"]}""" :
                    """{"Entrypoint":["bash","docker/entrypoint.sh"],"Cmd":["single"]}""");
                return Reply(HttpStatusCode.OK, image.ToJsonString());
            }
            if (path.Contains("/containers/json?", StringComparison.Ordinal))
            {
                var entry = new JsonObject { ["Id"] = Id, ["ImageID"] = _config.Engines["strata"].Docker!.ImageIdentity!.Id,
                    ["State"] = _state, ["Labels"] = _containerConfig["Labels"]!.DeepClone() };
                return Reply(HttpStatusCode.OK, _state == "absent" ? "[]" : new JsonArray(entry).ToJsonString());
            }
            if (path.EndsWith($"/{Id}/json", StringComparison.Ordinal))
            {
                var restarted = Interlocked.Increment(ref _inspects) >= ChangeGenerationAtInspect;
                var inspect = JsonNode.Parse(DockerLifecycleProviderTests.NativeContainerInspect(Id, Model, "one", state: _state,
                    running: _state == "running", restarts: restarted ? 1 : 0))!;
                inspect["HostConfig"] = _host.DeepClone();
                inspect["State"]!["Pid"] = InitPid;
                inspect["Config"] = _containerConfig.DeepClone();
                return Reply(HttpStatusCode.OK, inspect.ToJsonString());
            }
            if (path == "/v1.44/containers/create")
            {
                _state = "created";
                return Reply(HttpStatusCode.Created, $$"""{"Id":"{{Id}}"}""");
            }
            if (path.EndsWith($"/{Id}/start", StringComparison.Ordinal))
            {
                _state = "running";
                return Reply(HttpStatusCode.NoContent);
            }
            if (path.Contains($"/{Id}/stop?", StringComparison.Ordinal))
            {
                StopEntered.TrySetResult();
                if (HoldStop) await ReleaseStop.Task.WaitAsync(ct);
                _state = "exited";
                return Reply(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException($"Unexpected fixture Docker request {path}.");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            _provider.Dispose();
            App.Dispose();
            NextApp.Dispose();
            Proc.Dispose();
        }
    }

    private sealed class NextProvider : IEngineLifecycleProvider
    {
        public string Kind => "docker";
        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) => ValueTask.FromResult(LifecycleResult.Ok());
        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);
        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) => ValueTask.FromResult(LifecycleResult.Ok());
        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct) => ValueTask.FromResult(LifecycleResult.Ok());
        public ReadinessPolicy Readiness(LifecycleTarget target) => new(true, TimeSpan.FromSeconds(5));
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string json = "{}") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
