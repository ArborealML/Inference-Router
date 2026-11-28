using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Arboreal.InferenceRouter.Cli;

namespace Arboreal.InferenceRouter.Tests;

public sealed class AirTunePromptFitTests
{
    private static AirTuneTextGenerator Generator() => new(new string('1', 64), "32k", 1, 1, 1);

    [Fact]
    public async Task FreezesTheCountedBodyAndFitsWithinTheCallAndByteBudgets()
    {
        var bodies = new List<byte[]>();
        int TokenCount(byte[] body)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Length / 4 + 8;
        }
        var prompt = await AirTunePromptFit.FitAsync(Generator(), "model", 512, 32_097, 32_128,
            (body, _) =>
            {
                bodies.Add(body);
                return Task.FromResult(TokenCount(body));
            }, CancellationToken.None);

        Assert.NotNull(prompt);
        Assert.InRange(prompt.Tokens, 32_097, 32_128);
        Assert.Equal(prompt.Tokens, TokenCount(prompt.Body));
        Assert.Contains(bodies, body => ReferenceEquals(body, prompt.Body));
        Assert.InRange(bodies.Count, 1, 24);
        Assert.All(bodies, body => Assert.InRange(body.Length, 1, 349_525));
        Assert.Equal(bodies.Count, bodies.Select(Convert.ToBase64String).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task SearchesTheRecordTailWhenTheFullRecordExceedsTheWindow()
    {
        var generator = Generator();
        int calls = 0;
        long bytes = 0;
        int Count(byte[] body)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Length / 2 + 8;
        }
        Assert.Equal(32_095, Count(AirTunePromptFit.ChatBody("model", generator.Candidate(930, 0), 512)));
        Assert.Equal(32_130, Count(AirTunePromptFit.ChatBody("model", generator.Candidate(931, 0), 512)));

        var prompt = await AirTunePromptFit.FitAsync(generator, "model", 512, 32_097, 32_128,
            (body, _) =>
            {
                calls++;
                bytes += body.Length;
                Assert.InRange(body.Length, 1, 349_525);
                return Task.FromResult(Count(body));
            }, CancellationToken.None);

        Assert.NotNull(prompt);
        Assert.Equal(32_128, prompt.Tokens);
        Assert.Equal(930, prompt.Records);
        Assert.InRange(prompt.TailBytes, 66, 67);
        Assert.Equal(prompt.Tokens, Count(prompt.Body));
        Assert.InRange(calls, 1, 24);
        Assert.InRange(bytes, 1, 8_388_608);
    }

    [Fact]
    public async Task NoInBandPromptOrFailedCountCannotProduceAWorkload()
    {
        Assert.Null(await AirTunePromptFit.FitAsync(Generator(), "model", 512, 100, 131,
            (_, _) => Task.FromResult(132), CancellationToken.None));
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => AirTunePromptFit.FitAsync(Generator(), "model", 512, 100, 131,
            (_, _) => Task.FromResult(++calls == 1 ? 120 : 0), CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("wrong-context")]
    [InlineData("approximate")]
    [InlineData("redirect")]
    [InlineData("duplicate")]
    public async Task MayaCountsOnlyAtThePinnedOriginAndRequiresExactEvidence(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var received = new List<(string Path, string Authorization, byte[] Body)>();
        app.Run(async context =>
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes, deadline.Token);
            received.Add((context.Request.Method + " " + context.Request.Path,
                context.Request.Headers.Authorization.ToString(), bytes.ToArray()));
            if (mode == "redirect")
            {
                context.Response.Redirect("/unexpected");
                return;
            }
            var json = """{"approximate":false,"images":0,"max_context":32768,"tokens":32128}""";
            json = mode switch
            {
                "wrong-context" => json.Replace("32768", "16384", StringComparison.Ordinal),
                "approximate" => json.Replace("false", "true", StringComparison.Ordinal),
                "duplicate" => json.Replace("32128", "32128,\"tokens\":32128", StringComparison.Ordinal),
                _ => json
            };
            await context.Response.WriteAsync(json, deadline.Token);
        });
        await app.StartAsync(deadline.Token);
        var engine = new EngineDefinition { Name = "maya", Endpoint = new Uri(app.Urls.Single() + "/v1") };
        var coordinate = new WorkloadRequest("warmup", 1, 0, 1, "fixture");
        var tier = new WorkloadTier("32k", 32768, 1, 1, 32097, 32128, [coordinate]);
        var manifest = new WorkloadManifest("maya", "model", "32k", new string('1', 64), 1, [tier]);
        Task<FittedTunePrompt?> Count() => AirTunePromptFit.FitMayaAsync(engine, "fixture", manifest,
            tier, coordinate, deadline.Token);
        if (mode == "exact")
        {
            var result = await Count();
            Assert.NotNull(result);
            Assert.Equal(32128, result.Tokens);
            Assert.Equal(Assert.Single(received).Body, result.Body);
        }
        else if (mode == "duplicate") await Assert.ThrowsAnyAsync<JsonException>(Count);
        else await Assert.ThrowsAsync<InvalidDataException>(Count);
        var request = Assert.Single(received);
        Assert.Equal("POST /api/tokens", request.Path);
        Assert.Equal("Bearer fixture", request.Authorization);
    }
}
