using System.Net;
using System.Text;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed class AdminJsonBodyTests
{
    private const string NonObject =
        """{"error":{"message":"body must be a JSON object","code":"invalid_request","type":"air_error"}}""";
    private const string InvalidSyntax =
        """{"error":{"message":"body is not valid JSON","code":"invalid_request","type":"air_error"}}""";
    private const string ThreadRequired =
        """{"error":{"message":"threadId is required","code":"invalid_request","type":"air_error"}}""";
    private const string EngineModelRequired =
        """{"error":{"message":"engine and model are required (engine must exist)","code":"invalid_request","type":"air_error"}}""";

    [Theory]
    [InlineData("[]", NonObject)]
    [InlineData("\"thread\"", NonObject)]
    [InlineData("42", NonObject)]
    [InlineData("true", NonObject)]
    [InlineData("false", NonObject)]
    [InlineData("null", NonObject)]
    [InlineData("{", InvalidSyntax)]
    public async Task Invalid_Roots_Refuse_Before_Thread_Or_Switch_Effects(string body, string expected)
    {
        var (app, _, port) = await WireHarness.StartAsync();
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var router = app.Services.GetRequiredService<RouterCore>();
        var operations = app.Services.GetRequiredService<AdminSwitchOperations>();
        var submissions = 0;
        operations.BeforeScheduleForTests = () => Interlocked.Increment(ref submissions);

        await AssertRefusedAsync(http, "/admin/threads", body, expected, bound.Token);
        await AssertRefusedAsync(http, "/admin/switch", body, expected, bound.Token);

        Assert.Empty(router.GetStats().Threads);
        Assert.Equal(0, Volatile.Read(ref submissions));
        AssertSwitchCapacityUnused(operations);
    }

    [Theory]
    [InlineData("/admin/threads", """{"threadId":[]}""", ThreadRequired)]
    [InlineData("/admin/threads", """{"threadId":null}""", ThreadRequired)]
    [InlineData("/admin/threads", """{"threadId":"must-not-register","modelAffinity":42}""",
        """{"error":{"message":"modelAffinity must be a string or null","code":"invalid_request","type":"air_error"}}""")]
    [InlineData("/admin/threads", """{"threadId":"must-not-register","priority":{"class":true}}""",
        """{"error":{"message":"priority.class \u0027\u0027 unknown","code":"invalid_request","type":"air_error"}}""")]
    [InlineData("/admin/switch", """{"engine":42,"model":"test-model"}""", EngineModelRequired)]
    [InlineData("/admin/switch", """{"engine":"fake","model":{}}""", EngineModelRequired)]
    [InlineData("/admin/switch", """{"engine":"fake","model":null}""", EngineModelRequired)]
    [InlineData("/admin/switch", """{"engine":"fake","model":"test-model","profile":false}""",
        """{"error":{"message":"profile must be a string or null","code":"invalid_request","type":"air_error"}}""")]
    public async Task Wrong_String_Member_Kinds_Use_The_Routes_Pinned_Refusals(string path, string body, string expected)
    {
        var (app, _, port) = await WireHarness.StartAsync();
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var router = app.Services.GetRequiredService<RouterCore>();
        var operations = app.Services.GetRequiredService<AdminSwitchOperations>();
        var submissions = 0;
        operations.BeforeScheduleForTests = () => Interlocked.Increment(ref submissions);

        await AssertRefusedAsync(http, path, body, expected, bound.Token);

        Assert.Empty(router.GetStats().Threads);
        Assert.Equal(0, Volatile.Read(ref submissions));
        AssertSwitchCapacityUnused(operations);
    }

    [Fact]
    public async Task Null_Optional_Strings_Still_Allow_A_Valid_Thread_And_Switch()
    {
        var (app, _, port) = await WireHarness.StartAsync();
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var threads = Content("""{"threadId":"ok","modelAffinity":null}""");
        using var registered = await http.PostAsync("/admin/threads", threads, bound.Token);
        Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        var router = app.Services.GetRequiredService<RouterCore>();
        Assert.Equal("ok", Assert.Single(router.GetStats().Threads).ThreadId);

        using var switches = Content("""{"engine":"fake","model":"test-model","profile":null}""");
        using var ack = await http.PostAsync("/admin/switch", switches, bound.Token);
        Assert.Equal(HttpStatusCode.Accepted, ack.StatusCode);
    }

    private static async Task AssertRefusedAsync(HttpClient http, string path, string body, string expected,
        CancellationToken ct)
    {
        using var content = Content(body);
        using var response = await http.PostAsync(path, content, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await response.Content.ReadAsByteArrayAsync(ct));
    }

    private static StringContent Content(string body) => new(body, Encoding.UTF8, "application/json");

    private static void AssertSwitchCapacityUnused(AdminSwitchOperations operations)
    {
        for (var i = 0; i < AdminSwitchOperations.PendingLimit; i++)
            Assert.True(operations.TryReserve(out _));
        Assert.False(operations.TryReserve(out _));
    }
}
