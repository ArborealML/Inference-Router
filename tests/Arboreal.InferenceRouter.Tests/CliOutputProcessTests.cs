using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Arboreal.InferenceRouter.Tests;

public sealed class CliOutputProcessTests
{
    [Theory(Timeout = 30_000)]
    [InlineData("models", "/v1/models", 200, """{"data":[{"id":"example","owned_by":"cpu","context_window":32768}]}""", "example  cpu  32768")]
    [InlineData("status", "/admin/status", 200, """{"Engines":[],"Threads":[],"QueueDepths":{"example":2},"FeedSubscriberFaults":0}""", "example  2 queued")]
    [InlineData("tickets", "/admin/tickets/sample", 200, """{"Id":"sample","State":"Queued"}""", "State: Queued")]
    [InlineData("switch", "/admin/switch", 202, """{"OperationId":"op","Engine":"cpu","Model":"example"}""", "OperationId: op")]
    [InlineData("events", "/admin/events", 200, """{"Timestamp":"now","Seq":1,"Kind":"ticket.done","TicketId":"sample"}""", "now  ticket.done  Seq=1")]
    [InlineData("events", "/admin/events", 503, """{"error":{"code":"too_many_subscribers","message":"feed full"}}""", "HTTP 503: too_many_subscribers  feed full")]
    [InlineData("status", "/admin/status", 200, """{"Engines":[{"Engine":"cpu","ActiveContextWindow":0,"ActiveSlots":0}]}""", "context: unreported  slots: fallback (max_inflight_per_engine)")]
    [InlineData("status", "/admin/status", 200, """{"Threads":[{"ThreadId":"sample","Priority":{"Class":4,"Sub":200}}]}""", "priority: high:200")]
    [InlineData("tickets", "/admin/tickets/sample", 404, """{"error":{"code":"not_found","message":"no such ticket"}}""", "HTTP 404: not_found  no such ticket")]
    public async Task OperatorAndMachineModes(string verb, string path, int status, string json, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.StatusCode = context.Request.Path == path ? status : 400;
            var body = verb == "events" && status == 200 ? $"data: {json}\n\n" : json;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body), deadline.Token);
        });
        await app.StartAsync(deadline.Token);
        try
        {
            string[] options = verb switch
            {
                "tickets" => ["--server", app.Urls.Single(), "sample"],
                "switch" => ["--server", app.Urls.Single(), "-e", "cpu", "-m", "example"],
                _ => ["--server", app.Urls.Single()],
            };
            var human = await CliCompletionProcessTests.RunCliAsync(verb, options, null, deadline.Token);
            Assert.Equal(status >= 400 ? 1 : 0, human.ExitCode);
            Assert.Contains(expected, Encoding.UTF8.GetString(human.Stdout), StringComparison.Ordinal);

            var machine = await CliCompletionProcessTests.RunCliAsync("--json", [verb, .. options], null, deadline.Token);
            Assert.Equal(human.ExitCode, machine.ExitCode);
            Assert.Equal(json + Environment.NewLine, Encoding.UTF8.GetString(machine.Stdout));
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ConfigJsonDescribesProbesAndSelectorWithoutExposingApiKey()
    {
        var dir = Directory.CreateTempSubdirectory("air-cli-config-json-");
        try
        {
            var file = Path.Combine(dir.FullName, "router.toml");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await File.WriteAllTextAsync(file, """
                [engine.example]
                kind = "openai-compatible"
                endpoint = "http://127.0.0.1:1/v1"
                api_key = "fixture-sensitive-value"

                [engine.example.health_probe]
                path = "/state"
                context_path = "window"
                slots_path = "workers"

                [engine.example.catalog.model.select]
                exec = "fixture-never-execute"
                args = ["--model", "{{model}}"]
                """, deadline.Token);
            var result = await CliCompletionProcessTests.RunCliAsync("config", ["--json", "-c", file], null, deadline.Token);
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("fixture-sensitive-value", Encoding.UTF8.GetString(result.Stdout), StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(result.Stdout);
            var engine = doc.RootElement.GetProperty("Engines")[0];
            Assert.Equal("OpenAiCompatible", engine.GetProperty("Kind").GetString());
            Assert.Equal("Delegated", engine.GetProperty("LifecycleOwner").GetString());
            Assert.Equal("exec", engine.GetProperty("Provider").GetString());
            Assert.Equal("inline", engine.GetProperty("ApiKeySource").GetString());
            var probe = engine.GetProperty("HealthProbe");
            Assert.Equal("/state", probe.GetProperty("Path").GetString());
            Assert.Equal("window", probe.GetProperty("ContextPath").GetString());
            Assert.Equal("workers", probe.GetProperty("SlotsPath").GetString());
            var select = engine.GetProperty("Models")[0].GetProperty("Select");
            Assert.Equal("fixture-never-execute", select.GetProperty("Exec").GetString());
            Assert.Equal("{{model}}", select.GetProperty("Args")[1].GetString());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task CompareJsonPreservesTextReport()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var dir = Path.Combine(AppContext.BaseDirectory, "AirTuneFixtures");
        string[] args = ["compare", "--baseline", Path.Combine(dir, "baseline.json"), "--candidate", Path.Combine(dir, "candidate.json")];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var text = await CliCompletionProcessTests.RunCliAsync("tune", args, null, deadline.Token);
        var json = await CliCompletionProcessTests.RunCliAsync("tune", [.. args, "--json"], null, deadline.Token);
        Assert.Equal(0, text.ExitCode);
        Assert.Equal(0, json.ExitCode);
        using var report = JsonDocument.Parse(json.Stdout);
        Assert.Equal(Encoding.UTF8.GetString(text.Stdout), report.RootElement.GetProperty("Report").GetString());
    }
}
