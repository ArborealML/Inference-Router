using System.Net;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>What <c>POST /admin/threads</c> admits for a supplied priority (§6, §6.1). The route has always
/// refused a class name outside the five, but it used to take the number beside it on trust: a
/// <c>sub</c> of <c>255.7</c>, <c>-5</c> or <c>1e10</c> was cast and clamped into a byte and answered 204, and
/// a <c>priority</c> that was not an object at all was ignored wholesale, registering the configured default
/// for a caller that asked for something specific. Every one of those spells a priority the
/// <c>X-Inference-Priority</c> header refuses with 400, so the admin surface now refuses exactly what the
/// header refuses: a present <c>priority</c> that is not an object, and a present <c>sub</c> that is not a JSON
/// integer written in decimal digits within 0-255. Both answers are 400 <c>invalid_request</c> in the existing
/// lowercase envelope — same keys, same order, no new reason code. An absent <c>sub</c> is still the whole
/// class at 0, which is what makes <c>{"class":"high"}</c> mean the same thing on both surfaces.</summary>
public sealed class AdminThreadPriorityTests
{
    private static async Task<(HttpStatusCode Status, string Body, string? ContentType)> PostAsync(
        HttpClient http, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await http.PostAsync("/admin/threads", content);
        return (res.StatusCode, await res.Content.ReadAsStringAsync(), res.Content.Headers.ContentType?.MediaType);
    }

    private const string NonObjectPriorityRefusal =
        """{"error":{"message":"priority must be an object or absent","code":"invalid_request","type":"air_error"}}""";

    /// <summary>Each refusal body pinned byte for byte, escapes included: the router's HTML encoder writes the
    /// apostrophes around a quoted value, and a double quote inside it, as backslash-u sequences, so a renamed
    /// key, a reordered field or a paraphrased message is a test failure rather than a wire break nobody
    /// noticed (§6.1). The last two rows are the class refusals: a name outside the five, which always answered
    /// 400, and a class that is not a string at all, which threw its way to a 500 before this revision.</summary>
    public static TheoryData<string, string> RefusedPriorityBodies => new()
    {
        { """{"threadId":"admin-priority-sub","priority":{"class":"high","sub":255.7}}""",
            """{"error":{"message":"priority.sub \u0027255.7\u0027 is not an integer from 0 to 255","code":"invalid_request","type":"air_error"}}""" },
        { """{"threadId":"admin-priority-sub","priority":{"class":"high","sub":-5}}""",
            """{"error":{"message":"priority.sub \u0027-5\u0027 is not an integer from 0 to 255","code":"invalid_request","type":"air_error"}}""" },
        { """{"threadId":"admin-priority-sub","priority":{"class":"high","sub":1e10}}""",
            """{"error":{"message":"priority.sub \u00271e10\u0027 is not an integer from 0 to 255","code":"invalid_request","type":"air_error"}}""" },
        { """{"threadId":"admin-priority-sub","priority":{"class":"high","sub":"10"}}""",
            """{"error":{"message":"priority.sub \u0027\u002210\u0022\u0027 is not an integer from 0 to 255","code":"invalid_request","type":"air_error"}}""" },
        { """{"threadId":"admin-priority-sub","priority":{"class":"high","sub":true}}""",
            """{"error":{"message":"priority.sub \u0027true\u0027 is not an integer from 0 to 255","code":"invalid_request","type":"air_error"}}""" },
        { """{"threadId":"admin-priority-sub","priority":{"class":"high","sub":[10]}}""",
            """{"error":{"message":"priority.sub \u0027[10]\u0027 is not an integer from 0 to 255","code":"invalid_request","type":"air_error"}}""" },
        { """{"threadId":"admin-priority-nonobject","priority":"high"}""", NonObjectPriorityRefusal },
        { """{"threadId":"admin-priority-nonobject","priority":null}""", NonObjectPriorityRefusal },
        { """{"threadId":"admin-priority-nonobject","priority":["high",10]}""", NonObjectPriorityRefusal },
        // The class refusals: a name outside the five predates this revision and stays byte-identical beside
        // the new ones; a class that is not a string used to answer 500.
        { """{"threadId":"admin-priority-class","priority":{"class":"HIGH"}}""",
            """{"error":{"message":"priority.class \u0027HIGH\u0027 unknown","code":"invalid_request","type":"air_error"}}""" },
        { """{"threadId":"admin-priority-class","priority":{"class":5,"sub":10}}""",
            """{"error":{"message":"priority.class \u0027\u0027 unknown","code":"invalid_request","type":"air_error"}}""" },
    };

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(RefusedPriorityBodies))]
    public async Task ASuppliedPriorityTheAdminSurfaceCannotReadIsRefusedWithThePinnedEnvelope(
        string body, string expectedBody)
    {
        var (app, fake, port) = await WireHarness.StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var (status, refusal, contentType) = await PostAsync(http, body);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("application/json", contentType);
        Assert.Equal(expectedBody, refusal);
        Assert.Equal(0, fake.CompletionCalls);

        // Nothing was registered: the caller asked for a priority and got neither that priority nor the
        // default one it did not ask for.
        using var stats = JsonDocument.Parse(await http.GetStringAsync("/admin/status"));
        Assert.DoesNotContain(stats.RootElement.GetProperty("Threads").EnumerateArray(),
            thread => thread.GetProperty("ThreadId").GetString()!.Contains("admin-priority", StringComparison.Ordinal));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("""{"class":"high"}""", ThreadPriorityClass.High, 0)]          // an absent sub is the whole class at 0
    [InlineData("""{"class":"high","sub":0}""", ThreadPriorityClass.High, 0)]
    [InlineData("""{"class":"interactive","sub":255}""", ThreadPriorityClass.Interactive, 255)]
    [InlineData("""{"class":"background","sub":7}""", ThreadPriorityClass.Background, 7)]
    public async Task EveryPriorityTheAdminSurfaceAdmitsRegistersExactlyWhatItSays(string priority, byte expectedClass, int expectedSub)
    {
        var thread = $"admin-priority-{expectedClass}-{expectedSub}";
        var (app, _, port) = await WireHarness.StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var (status, body, _) = await PostAsync(http, $$"""{"threadId":"{{thread}}","priority":{{priority}}}""");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Empty(body);
        var (cls, sub) = await RecordedPriorityAsync(http, thread);
        Assert.Equal(expectedClass, cls);
        Assert.Equal(expectedSub, sub);
    }

    [Fact(Timeout = 30_000)]
    public async Task ANoPriorityBodyKeepsTheConfiguredDefault()
    {
        var (app, _, port) = await WireHarness.StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var (status, _, _) = await PostAsync(http, """{"threadId":"admin-priority-default"}""");
        Assert.Equal(HttpStatusCode.NoContent, status);
        var (cls, sub) = await RecordedPriorityAsync(http, "admin-priority-default");
        Assert.Equal(ThreadPriorityClass.Normal, cls);
        Assert.Equal(0, sub);
    }

    /// <summary>The priority the router recorded for a thread, once it appears in the operator view.</summary>
    private static async Task<(byte Class, int Sub)> RecordedPriorityAsync(HttpClient http, string thread)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("/admin/status"));
            foreach (var entry in doc.RootElement.GetProperty("Threads").EnumerateArray())
            {
                if (!string.Equals(entry.GetProperty("ThreadId").GetString(), thread, StringComparison.Ordinal)) continue;
                var priority = entry.GetProperty("Priority");
                return (priority.GetProperty("Class").GetByte(), priority.GetProperty("Sub").GetInt32());
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"thread {thread} never appeared in /admin/status");
    }
}
