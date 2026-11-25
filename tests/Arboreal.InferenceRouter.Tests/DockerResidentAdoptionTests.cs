using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class DockerResidentAdoptionTests
{
    [Theory]
    [InlineData("serve", "gpu", 0, true)]
    [InlineData("other", "gpu", 0, false)]
    [InlineData("serve", "cpu", 0, false)]
    [InlineData("serve", "gpu", 1024, false)]
    public void ComparesEffectiveContainerWithImageDefaultsAndLaunchOverrides(
        string command, string device, long memory, bool matches)
    {
        var plan = new DockerCreateRequest { Image = "pinned", Labels = new(StringComparer.Ordinal),
            Cmd = ["serve"], Env = ["DEVICE=gpu"] };
        using var image = JsonDocument.Parse("""{"Env":["BASE=1","DEVICE=cpu"],"Entrypoint":["/start"]}""");
        using var inspect = JsonDocument.Parse($$$$$"""
            {"Config":{"Image":"pinned","Labels":{},"Entrypoint":["/start"],"Cmd":["{{{{{command}}}}}"],"Env":["DEVICE={{{{{device}}}}}","BASE=1"]},
             "HostConfig":{"IpcMode":"private","ShmSize":67108864,"Memory":{{{{{memory}}}}},"MemorySwap":0,"NanoCpus":0,
             "RestartPolicy":{"Name":"no","MaximumRetryCount":0},"LogConfig":{"Type":"json-file","Config":{}}}}
            """);
        Assert.Equal(matches, DockerLifecycleProvider.MatchesResidentCreate(plan, inspect.RootElement, image.RootElement));
    }

    [Theory(Timeout = 20_000)]
    [InlineData("healthy", 0, true)]
    [InlineData("retained-exited", 1, true)]
    [InlineData("retained-created", 1, true)]
    [InlineData("settings", 1, true)]
    [InlineData("readiness", 1, true)]
    [InlineData("failed-start", 1, false)]
    [InlineData("failed-readiness", 1, false)]
    [InlineData("provider-timeout", 0, false)]
    [InlineData("preflight", 0, false)]
    [InlineData("foreign", 0, false)]
    [InlineData("expired", 0, false)]
    [InlineData("expired-preflight", 0, false)]
    [InlineData("changed-census", 0, false)]
    [InlineData("unstable", 0, false)]
    [InlineData("cancel", 0, false)]
    public async Task FirstRequestOwnsAtMostOneReplacement(string scenario, int expectedStops, bool completes)
    {
        const string model = "test-model";
        const string oldId = DockerLifecycleProviderTests.FullPeerId;
        var newId = new string('d', 64);
        var docker = DockerLifecycleProviderTests.Cfg(removeStopped: false, waitTimeoutMs: 2000);
        var definition = new EngineDefinition
        {
            Name = "strata", Lifecycle = EngineLifecycleKind.Docker, LifecycleOwner = EngineLifecycleOwner.Router,
            LifecycleGroup = "shared", Endpoint = new Uri("http://localhost:18021/v1"), Docker = docker,
            Catalog = new Dictionary<string, EngineModelDefinition> { [model] = new() { Name = model } },
        };
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new FakeEngineHandler { LoadedModels = [model], NCtx = 4096,
            ModelsStatus = scenario == "readiness" ? 503 : 200 };
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(docker, "strata", daemon, localManaged: true);
        var image = docker.ImageIdentity!;
        var plan = provider.BuildCreateRequest(new("strata", model, null, definition.Catalog[model]), image);
        var config = JsonSerializer.SerializeToNode(plan, DockerJsonContext.Default.DockerCreateRequest)!.AsObject();
        var host = config["HostConfig"]!.DeepClone();
        config.Remove("HostConfig");
        host["IpcMode"] = "private";
        host["ShmSize"] = 67108864;
        host["NanoCpus"] = 0;
        host["Memory"] = 0;
        host["MemorySwap"] = 0;
        host["RestartPolicy"] = new JsonObject { ["Name"] = "no", ["MaximumRetryCount"] = 0 };
        host["LogConfig"] = new JsonObject { ["Type"] = "json-file", ["Config"] = new JsonObject() };
        if (scenario is "settings" or "failed-start" or "failed-readiness") config["Cmd"] = new JsonArray("stale");
        var current = oldId;
        var inspections = 0;
        var state = scenario switch { "retained-exited" => "exited", "retained-created" => "created", _ => "running" };
        var creates = 0;
        var inventories = 0;
        var preflightCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transientPreflight = scenario == "preflight";
        daemon.Responder = path =>
        {
            if (path.EndsWith("/version", StringComparison.Ordinal)) return (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
            if (path.Contains("/images/", StringComparison.Ordinal))
            {
                if (scenario == "provider-timeout") throw new TimeoutException("fixture daemon timeout");
                if (transientPreflight) return (HttpStatusCode.NotFound, "{}");
                var info = JsonNode.Parse(DockerLifecycleProviderTests.Inspect())!;
                info["Config"] = new JsonObject();
                return (HttpStatusCode.OK, info.ToJsonString());
            }
            if (path.Contains("/containers/json?", StringComparison.Ordinal))
            {
                if (state == "absent" || (scenario == "changed-census" && ++inventories == 1))
                    return (HttpStatusCode.OK, "[]");
                var entry = new JsonObject { ["Id"] = current, ["ImageID"] = image.Id, ["State"] = state,
                    ["Labels"] = config["Labels"]!.DeepClone() };
                return (HttpStatusCode.OK, new JsonArray(entry).ToJsonString());
            }
            if (path.EndsWith("/json", StringComparison.Ordinal))
            {
                Assert.EndsWith($"/{current}/json", path, StringComparison.Ordinal);
                if (state == "absent") return (HttpStatusCode.NotFound, "{}");
                var sample = JsonNode.Parse(DockerLifecycleProviderTests.NativeContainerInspect(current, model,
                    state: state, running: state == "running"))!;
                sample["Config"] = config.DeepClone();
                sample["HostConfig"] = host.DeepClone();
                if (scenario == "foreign") sample["Config"]!["Labels"]!["arboreal.router"] = "another-router";
                if (scenario == "unstable") sample["RestartCount"] = ++inspections;
                if (scenario == "cancel") request.Cancel();
                return (HttpStatusCode.OK, sample.ToJsonString());
            }
            if (path.Contains("/stop?", StringComparison.Ordinal))
            {
                Assert.Contains($"/containers/{current}/stop?", path, StringComparison.Ordinal);
                var status = state == "running" ? HttpStatusCode.NoContent : HttpStatusCode.NotModified;
                state = "exited";
                return (status, "");
            }
            if (path.Contains("/containers/create", StringComparison.Ordinal))
            {
                current = ++creates == 1 ? newId : new string('e', 64);
                state = "created";
                return (HttpStatusCode.Created, $$"""{"Id":"{{current}}"}""");
            }
            if (daemon.Requests[^1].StartsWith("DELETE ", StringComparison.Ordinal))
            {
                Assert.EndsWith($"/containers/{newId}", path, StringComparison.Ordinal);
                Assert.Equal("created", state);
                state = "absent";
                return (HttpStatusCode.NoContent, "");
            }
            if (path.EndsWith("/start", StringComparison.Ordinal))
            {
                Assert.EndsWith($"/containers/{current}/start", path, StringComparison.Ordinal);
                if (scenario == "failed-start" && creates == 1) return (HttpStatusCode.Conflict, "");
                state = "running";
                backend.ModelsStatus = scenario == "failed-readiness" ? 503 : 200;
                return (HttpStatusCode.NoContent, "");
            }
            return (HttpStatusCode.NotFound, "{}");
        };
        var settings = new RouterConfig { AutoSwitch = true, Engines = new Dictionary<string, EngineDefinition>
            { ["strata"] = definition }, HttpHandlerFactory = _ => backend };
        if (scenario is "expired" or "expired-preflight")
        {
            settings.Scheduler.SwitchWaitTimeoutMs = 200;
            daemon.AsyncResponder = async (path, token) =>
            {
                if (scenario == "expired-preflight" && path.EndsWith("/version", StringComparison.Ordinal))
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    finally { if (token.IsCancellationRequested) preflightCancelled.TrySetResult(); }
                }
                if (path.EndsWith($"/{oldId}/json", StringComparison.Ordinal))
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                return daemon.Responder(path);
            };
        }
        await using var router = new RouterCore(settings, scheduler: null, events: null, clock: TimeProvider.System,
            lifecycleFactory: _ => provider);
        var engine = router.Engines["strata"];
        Assert.True(await engine.ProbeHealthOnceAsync(request.Token));
        Assert.True(await engine.ProbeHealthOnceAsync(request.Token));
        Assert.Empty(backend.SeenRoutes);
        router.Start();
        var first = await Enqueue();
        if (completes)
        {
            var second = await Enqueue();
            await Task.WhenAll(first.CompletionTask, second.CompletionTask).WaitAsync(request.Token);
            Assert.Equal(2, backend.CompletionCalls);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => first.CompletionTask.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal(0, backend.CompletionCalls);
            if (scenario is "failed-start" or "preflight")
            {
                transientPreflight = false;
                config["Cmd"] = JsonSerializer.SerializeToNode(plan, DockerJsonContext.Default.DockerCreateRequest)!["Cmd"]!.DeepClone();
                var successor = await Enqueue();
                await successor.CompletionTask.WaitAsync(request.Token);
                Assert.Equal(1, backend.CompletionCalls);
            }
            if (scenario == "expired-preflight") await preflightCancelled.Task.WaitAsync(request.Token);
            if (scenario == "provider-timeout") Assert.Contains("launch_failed", failure.Message, StringComparison.Ordinal);
            if (scenario is "provider-timeout" or "expired") Assert.DoesNotContain("still running", failure.Message, StringComparison.Ordinal);
            if (scenario is "expired" or "preflight" or "provider-timeout") Assert.Equal("switch_failed", first.ErrorCode);
        }
        Assert.Equal(expectedStops, daemon.Requests.Count(path => path.StartsWith("POST", StringComparison.Ordinal) &&
            path.Contains($"/{oldId}/stop?", StringComparison.Ordinal)));
        Assert.Equal(expectedStops * 3 + (scenario == "failed-start" ? 3 : 0), daemon.Requests.Count(path => !path.StartsWith("GET", StringComparison.Ordinal)));

        async Task<RouterTicket> Enqueue() => await router.EnqueueAsync("client", new InferenceRequest
        {
            Model = model, Body = """{"model":"test-model","messages":[{"role":"user","content":"hello"}]}"""u8.ToArray(),
            Options = new(),
        }, request.Token);
    }
}
