namespace Arboreal.InferenceRouter.Tests;

public sealed class ColdListenerReadinessTests
{
    [Fact(Timeout = 15_000)]
    public async Task ReadinessRetriesConnectionRefusalWithoutRelaunching()
    {
        using var handler = new ColdHandler { LoadedModels = ["target"] };
        var provider = new RecordingLifecycleProvider { Kind = "docker" };
        await using var engine = new EngineRuntime(new EngineDefinition
        {
            Name = "fixture", Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test" },
            Catalog = new Dictionary<string, EngineModelDefinition> { ["target"] = new() { Name = "target" } },
            Health = new HealthProbeDefinition { Path = "/props" },
        }, new RouterConfig(), new EventRing(), handler, lifecycle: provider);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await engine.WaitForModelAsync("target", TimeSpan.FromSeconds(8), deadline.Token);
        Assert.True(result.IsReady);
        Assert.Equal(1, handler.Refusals);
        Assert.Single(provider.Calls, call => call == "launch");
        Assert.Contains("target", engine.LoadedModels);
    }

    private sealed class ColdHandler : FakeEngineHandler
    {
        private int _calls;
        internal int Refusals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Refusals++;
                throw new HttpRequestException("Connection refused while the listener is starting.");
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
