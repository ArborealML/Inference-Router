using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>The priority option as the CLI's own argument, checked before a connection opens (§6). Two
/// separate defects. A value outside the §6 grammar: <c>air threads</c> refuses it locally, because the admin
/// thread route takes class and sub as separate JSON fields and used to answer 204 for the default priority
/// nobody asked for; <c>air chat</c> is the deliberate opposite case — it forwards the raw header, so the
/// router's own 400 is what that caller reads. And the option itself: <c>-p</c> given an empty string, given
/// another option, given nothing, or given twice used to read exactly like an absent <c>-p</c>, so the command
/// sent no priority, registered the default and exited 0 on a value the operator had typed. An absent
/// <c>-p</c> stays the only way to ask for the default. The stub counts arrivals ahead of reading any body, so
/// "no request was made" means no request, not "no request the stub could parse".</summary>
public sealed class CliThreadPriorityTests
{
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(20);
    private const string CompletionBody = """{"choices":[{"message":{"content":"pong"}}]}""" + "\n";

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
        var (app, arrivals, observed) = await StartRouterStubAsync(deadline.Token);
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
            Assert.Equal(0, arrivals.Count);
            Assert.False(observed.Task.IsCompleted);
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    /// <summary>The option present and unusable: each of these used to be indistinguishable from an absent
    /// <c>-p</c>, and each is visible in the raw arguments. The last row is a repeated option, where the first
    /// value silently won. The second column is the fragment the refusal prints after <c>--priority </c>,
    /// offending token included: it pins the option name, the defect and the value the operator typed, so a
    /// paraphrased message or a dropped value fails rather than passing on a different refusal.</summary>
    public static TheoryData<string[], string> UnusablePriorityOption => new()
    {
        { ["-i", "fixture-thread", "-p"], "given with no value" },
        { ["-i", "fixture-thread", "-p", ""], "given an empty value" },
        { ["-i", "fixture-thread", "-p", "-5"], "given \"-5\", which reads as another option" },
        { ["-i", "fixture-thread", "-p", "high", "-p", "high:+12"], "given 2 times, expected one value" },
    };

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(UnusablePriorityOption))]
    public async Task ThreadsRefusesAnUnusablePriorityOptionBeforeAnyRequest(string[] optionArgs, string namedDefect)
        => await AssertRefusedLocallyAsync("threads", optionArgs, namedDefect);

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(UnusablePriorityOption))]
    public async Task ChatRefusesAnUnusablePriorityOptionBeforeAnyRequest(string[] optionArgs, string namedDefect)
        => await AssertRefusedLocallyAsync("chat", [ "ping", "--no-stream", .. optionArgs ], namedDefect);

    /// <summary>The same option defect on either verb: exit 2, the grammar on stderr, and no request at all.
    /// <c>air chat</c> forwards a value it cannot read (below) but never forwards an option it cannot read.
    /// <c>--server</c> leads the argument list so a row that ends in <c>-p</c> really is the option given no
    /// value at all, rather than one followed by the server URL.</summary>
    private static async Task AssertRefusedLocallyAsync(string verb, string[] args, string namedDefect)
    {
        using var deadline = new CancellationTokenSource(RunLimit);
        var (app, arrivals, observed) = await StartRouterStubAsync(deadline.Token);
        try
        {
            var options = new List<string> { "--server", app.Urls.Single() };
            options.AddRange(args);
            var result = await CliCompletionProcessTests.RunCliAsync(verb, [.. options], null, deadline.Token);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Stdout);
            var stderr = Encoding.UTF8.GetString(result.Stderr);
            Assert.Contains("invalid priority", stderr, StringComparison.Ordinal);
            Assert.Contains($"--priority {namedDefect}", stderr, StringComparison.Ordinal);
            foreach (var name in new[] { "background", "low", "normal", "high", "interactive" })
                Assert.Contains(name, stderr, StringComparison.Ordinal);
            Assert.Equal(0, arrivals.Count);
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
        var (app, arrivals, observed) = await StartRouterStubAsync(deadline.Token);
        try
        {
            var result = await CliCompletionProcessTests.RunCliAsync("threads",
                ["--id", "fixture-thread", "--priority", priority, "--server", app.Urls.Single()], null, deadline.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, arrivals.Count);
            Assert.Equal("thread fixture-thread registered" + Environment.NewLine,
                Encoding.UTF8.GetString(result.Stdout));
            var request = await observed.Task.WaitAsync(deadline.Token);
            Assert.Equal("POST", request.Method);
            Assert.Equal("/admin/threads", request.Path);
            using var doc = JsonDocument.Parse(request.Body);
            Assert.Equal("fixture-thread", doc.RootElement.GetProperty("threadId").GetString());
            var sent = doc.RootElement.GetProperty("priority");
            Assert.Equal(expectedClass, sent.GetProperty("class").GetString());
            Assert.Equal(expectedSub, sent.GetProperty("sub").GetInt32());
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ThreadsWithoutThePriorityOptionAsksForTheRoutersDefault()
    {
        // An absent option is the only way to ask for nothing: the body names no priority at all, so the
        // route keeps its configured default instead of a value the CLI invented.
        using var deadline = new CancellationTokenSource(RunLimit);
        var (app, arrivals, observed) = await StartRouterStubAsync(deadline.Token);
        try
        {
            var result = await CliCompletionProcessTests.RunCliAsync("threads",
                ["-i", "fixture-thread", "--server", app.Urls.Single()], null, deadline.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, arrivals.Count);
            var request = await observed.Task.WaitAsync(deadline.Token);
            using var doc = JsonDocument.Parse(request.Body);
            Assert.False(doc.RootElement.TryGetProperty("priority", out _));
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ChatWithoutThePriorityOptionSendsNoPriorityHeader()
    {
        using var deadline = new CancellationTokenSource(RunLimit);
        var (app, arrivals, observed) = await StartRouterStubAsync(deadline.Token);
        try
        {
            var result = await CliCompletionProcessTests.RunCliAsync("chat",
                ["ping", "--no-stream", "-t", "fixture-thread", "--server", app.Urls.Single()], null, deadline.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, arrivals.Count);
            var request = await observed.Task.WaitAsync(deadline.Token);
            Assert.Equal("/v1/chat/completions", request.Path);
            Assert.Null(request.PriorityHeader);
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ChatForwardsAPriorityTheCliWouldRefuseSoTheRouterRefusesIt()
    {
        // The asymmetry is deliberate: the inference surface owns the priority grammar, so an operator who
        // asks the router with `air chat -p` reads the router's answer, not the CLI's paraphrase (§6).
        using var deadline = new CancellationTokenSource(RunLimit);
        var (app, arrivals, observed) = await StartRouterStubAsync(deadline.Token);
        try
        {
            var result = await CliCompletionProcessTests.RunCliAsync("chat",
                ["ping", "--no-stream", "-t", "fixture-thread", "-p", "high:+12", "--server", app.Urls.Single()],
                null, deadline.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, arrivals.Count);
            var request = await observed.Task.WaitAsync(deadline.Token);
            Assert.Equal("high:+12", request.PriorityHeader);
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    /// <summary>A stub router: it counts every arrival in a pipeline stage ahead of the handler, never parses
    /// the body it forwards to the test, and answers the two routes the priority verbs use. So a malformed or
    /// unexpected request still counts as a request, and a refusal test that asserts zero arrivals cannot pass
    /// on a request the stub failed to read.</summary>
    private static async Task<(WebApplication App, ArrivalCounter Arrivals, TaskCompletionSource<ObservedPost> Observed)>
        StartRouterStubAsync(CancellationToken ct)
    {
        var observed = new TaskCompletionSource<ObservedPost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = new ArrivalCounter();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            arrivals.Arrive();
            await next();
        });
        app.Run(async context =>
        {
            var completion = context.Request.Path.Equals("/v1/chat/completions", StringComparison.Ordinal);
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync(ct);
            observed.TrySetResult(new ObservedPost(context.Request.Method, context.Request.Path.Value ?? string.Empty,
                context.Request.Headers.TryGetValue("X-Inference-Priority", out var priority) ? priority.ToString() : null,
                body));
            if (!completion)
            {
                context.Response.StatusCode = 204;
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(CompletionBody);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, ct);
        });
        await app.StartAsync(ct);
        return (app, arrivals, observed);
    }

    private sealed record ObservedPost(string Method, string Path, string? PriorityHeader, string Body);

    /// <summary>Arrivals, counted ahead of any body read: the "no request was made" assertion needs the fact
    /// of a request, which a parse-and-complete signal does not give.</summary>
    private sealed class ArrivalCounter
    {
        private int _arrivals;
        public void Arrive() => Interlocked.Increment(ref _arrivals);
        public int Count => Volatile.Read(ref _arrivals);
    }
}
