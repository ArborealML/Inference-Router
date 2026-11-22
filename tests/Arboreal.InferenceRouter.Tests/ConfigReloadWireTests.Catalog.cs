using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ConfigReloadWireTests
{
    [Fact(Timeout = 30_000)]
    public async Task CatalogContainsColdChoicesAndChangesOnlyAfterAppliedReload()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var path = Path.Combine(_dir.FullName, "router.toml");
        var source = Initial + External("one", "a", "cold") + """
            aliases = ["alternative"]
            profile = "small"
            serving = [{ name = "small", max_context = 1024, max_slots = 2 }]
            """;
        await File.WriteAllTextAsync(path, source, ct);
        using var fake = new FakeEngineHandler { LoadedModels = ["a"] };
        await using var app = await StartAsync(path, _ => fake, ct);
        using var http = Client(app);
        var core = app.Services.GetRequiredService<RouterCore>();
        using var initial = await http.GetAsync("/admin/catalog", ct);
        initial.EnsureSuccessStatusCode();
        var oldTag = initial.Headers.ETag!.Tag;
        using var json = JsonDocument.Parse(await initial.Content.ReadAsByteArrayAsync(ct));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("Generation").GetInt64());
        Assert.Equal("a", root.GetProperty("DefaultModel").GetString());
        var engine = Assert.Single(root.GetProperty("Engines").EnumerateArray());
        Assert.Equal("one", engine.GetProperty("Name").GetString());
        Assert.False(engine.TryGetProperty("Endpoint", out _));
        var model = Assert.Single(engine.GetProperty("Models").EnumerateArray(), item => item.GetProperty("Id").GetString() == "cold");
        Assert.Equal("alternative", Assert.Single(model.GetProperty("Aliases").EnumerateArray()).GetString());
        Assert.Equal("small", model.GetProperty("DefaultProfile").GetString());
        var tier = Assert.Single(model.GetProperty("Profiles").EnumerateArray());
        Assert.Equal("small", tier.GetProperty("Name").GetString());
        Assert.Equal(1024, tier.GetProperty("MaxContext").GetInt64());
        Assert.Equal(2, tier.GetProperty("MaxSlots").GetInt32());

        var (reloadTag, _) = await ReadGenerationAsync(http, ct);
        using var unchanged = await ApplyAsync(http, reloadTag, ct);
        unchanged.EnsureSuccessStatusCode();
        await File.WriteAllTextAsync(path, "[malformed", ct);
        using var rejected = await ApplyAsync(http, reloadTag, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.DoesNotContain(core.Events.Replay(0), item => item.Kind == "catalog.changed");
        using var same = await ReadConditionalAsync(http);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);
        Assert.Empty(await same.Content.ReadAsByteArrayAsync(ct));

        await File.WriteAllTextAsync(path, source.Replace("default_model = \"a\"", "default_model = \"cold\"", StringComparison.Ordinal), ct);
        using var changed = await ApplyAsync(http, reloadTag, ct);
        changed.EnsureSuccessStatusCode();
        using var fresh = await ReadConditionalAsync(http);
        fresh.EnsureSuccessStatusCode();
        Assert.NotEqual(oldTag, fresh.Headers.ETag!.Tag);
        Assert.Single(core.Events.Replay(0), item => item.Kind == "catalog.changed");
        using var restartedBackend = new FakeEngineHandler();
        await using var restart = await StartAsync(path, _ => restartedBackend, ct);
        using var other = Client(restart);
        using var newProcess = await ReadConditionalAsync(other);
        newProcess.EnsureSuccessStatusCode();
        Assert.NotEqual(oldTag, newProcess.Headers.ETag!.Tag);

        async Task<HttpResponseMessage> ReadConditionalAsync(HttpClient client)
        {
            using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/catalog");
            read.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(oldTag));
            return await client.SendAsync(read, ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task NamedProfileUsesCompatibleEngineAndRejectsUnknownOrUndersizedChoices()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var path = Path.Combine(_dir.FullName, "router.toml");
        var source = Initial + External("one", "a") + "aliases = [\"alias\"]\nserving = [{name=\"big\", max_context=1024, max_slots=1}]\n" +
            External("two", "a") + "serving = [{name=\"tiny\", max_context=8, max_slots=2},{name=\"ambiguous\", max_context=100, max_slots=1},{name=\"ambiguous\", max_context=200, max_slots=2}]\n";
        await File.WriteAllTextAsync(path, source, ct);
        using var first = new FakeEngineHandler { LoadedModels = ["a"], NCtx = 1024, TotalSlots = 1 };
        using var second = new FakeEngineHandler { LoadedModels = ["a"], NCtx = 8, TotalSlots = 2 };
        await using var app = await StartAsync(path, engine => engine.Name == "one" ? first : second, ct);
        using var http = Client(app);
        var core = app.Services.GetRequiredService<RouterCore>();
        await ProbeAllAsync(core, ct);
        using var match = await PostAsync("tiny", "ok");
        match.EnsureSuccessStatusCode();
        foreach (var (profile, prompt, floor) in new[] {
            ("ambiguous", "ok", "1"), ("Tiny", "ok", "1"), ("other", "ok", "1"), ("tiny,big", "ok", "1"),
            ("tiny", "ok", "9"), ("tiny", new string('z', 1000), "1") })
        {
            using var rejected = await PostAsync(profile, prompt, floor);
            await AssertErrorAsync(rejected, HttpStatusCode.BadRequest, "invalid_profile", ct);
        }
        var selected = Gate();
        var finish = Gate();
        core.Engines["two"].AfterForwardAdmissionForTests = () =>
        {
            selected.TrySetResult();
            return finish.Task;
        };
        var drifting = PostAsync("tiny", "ok");
        try
        {
            await selected.Task.WaitAsync(ct);
            second.TotalSlots = 1;
            await core.Engines["two"].ProbeHealthOnceAsync(ct);
        }
        finally { finish.TrySetResult(); }
        using var refused = await drifting;
        await AssertErrorAsync(refused, HttpStatusCode.ServiceUnavailable, "switch_failed", ct);
        Assert.Equal(0, first.CompletionCalls);
        Assert.Equal(1, second.CompletionCalls);

        async Task<HttpResponseMessage> PostAsync(string profile, string prompt, string floor = "1")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/completions");
            request.Headers.Add("X-Inference-Profile", profile);
            request.Headers.Add("X-Inference-Required-Context", floor);
            request.Content = new StringContent($$"""{"model":"alias","prompt":"{{prompt}}"}""", Encoding.UTF8, "application/json");
            return await http.SendAsync(request, ct);
        }
    }
}
