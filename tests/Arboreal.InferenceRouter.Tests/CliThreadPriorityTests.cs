using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Arboreal.InferenceRouter.Tests;

/// <summary><c>air threads -p</c> checks its own value against the §6 grammar before it opens a
/// connection. The admin thread route takes a class name and a number, so a value the CLI cannot parse
/// used to travel to the router as normal/0 and come back as 204: a priority nobody asked for, accepted
/// silently, for a spelling the request path refuses with 400 <c>invalid_priority</c>. A malformed value
/// now stops locally with exit 2 and the grammar on stderr. <c>air chat -p</c> is the deliberate
/// opposite case — it forwards the raw header, so the router's own refusal is what that caller reads.</summary>
public sealed class CliThreadPriorityTests
{
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(20);

    [Theory(Timeout = 30_000)]
    [InlineData("high:+12", false)]
    [InlineData("high:+12", true)]
    [InlineData("high: 12", false)]
    [InlineData(" high ", false)]
    [InlineData("high:007", false)]
    [InlineData("high:0255", false)]
    [InlineData("high:256", false)]
    [InlineData("HIGH", false)]
    [InlineData("hi:gh:1", false)]
    [InlineData(":1", false)]
    public async Task ThreadsRefusesAMalformedPriorityBeforeAnyRequest(string priority, bool json)
    {
        using var deadline = new CancellationTokenSource(RunLimit);
        var (app, observed) = await StartThreadStubAsync(deadline.Token);
        try
        {
            var options = new List<string> { "-i", "fixture-thread", "-p", priority, "--server", app.Urls.Single() };
            if (json) options.Add("--json");
            var result = await CliCompletionProcessTests.RunCliAsync("threads", [.. options], null, deadline.Token);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Stdout);
            var stderr = Encoding.UTF8.GetString(result.Stderr);
            Assert.Contains($"invalid priority \"{priority}\"", stderr, StringComparison.Ordinal);
            // The refusal names what would have been accepted, so the operator can fix the value rather
            // than guess it.
            foreach (var name in new[] { "background", "low", "normal", "high", "interactive" })
                Assert.Contains(name, stderr, StringComparison.Ordinal);
            Assert.False(observed.Task.IsCompleted);
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("high", "high", 0)]
    [InlineData("high:0", "high", 0)]
    [InlineData("high:200", "high", 200)]
    [InlineData("interactive:255", "interactive", 255)]
    [InlineData("background:7", "background", 7)]
    public async Task ThreadsSendsTheParsedPriorityToTheAdminRoute(string priority, string expectedClass, int expectedSub)
    {
        using var deadline = new CancellationTokenSource(RunLimit);
        var (app, observed) = await StartThreadStubAsync(deadline.Token);
        try
        {
            var result = await CliCompletionProcessTests.RunCliAsync("threads",
                ["--id", "fixture-thread", "--priority", priority, "--server", app.Urls.Single()], null, deadline.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("thread fixture-thread registered" + Environment.NewLine,
                Encoding.UTF8.GetString(result.Stdout));
            var request = await observed.Task.WaitAsync(deadline.Token);
            Assert.Equal("POST", request.Method);
            Assert.Equal("/admin/threads", request.Path);
            Assert.Equal("fixture-thread", request.Body.GetProperty("threadId").GetString());
            var sent = request.Body.GetProperty("priority");
            Assert.Equal(expectedClass, sent.GetProperty("class").GetString());
            Assert.Equal(expectedSub, sent.GetProperty("sub").GetInt32());
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    /// <summary>The stub records the first request it sees and answers the admin route's 204, so a test can
    /// tell "refused locally" from "accepted by somebody".</summary>
    private static async Task<(WebApplication App, TaskCompletionSource<ObservedPost> Observed)> StartThreadStubAsync(
        CancellationToken ct)
    {
        var observed = new TaskCompletionSource<ObservedPost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Run(async context =>
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            observed.TrySetResult(new ObservedPost(context.Request.Method, context.Request.Path.Value ?? string.Empty,
                doc.RootElement.Clone()));
            context.Response.StatusCode = 204;
        });
        await app.StartAsync(ct);
        return (app, observed);
    }

    private sealed record ObservedPost(string Method, string Path, JsonElement Body);
}
