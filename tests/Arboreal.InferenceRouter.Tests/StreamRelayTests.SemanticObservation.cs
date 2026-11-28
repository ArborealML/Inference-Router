using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public partial class StreamRelayTests
{
    private static ReadOnlySpan<byte> CapacityError =>
        "data: {\"error\":{\"message\":\"fixture capacity warning\",\"type\":\"fixture_error\"}}\r\n\r\n"u8;

    [Theory]
    [InlineData(0, 0, true, false)]
    [InlineData(Credit, 0, true, false)]
    [InlineData(0, 300_000, true, false)]
    [InlineData(Credit, 300_000, false, false)]
    [InlineData(0, 0, true, true)]
    [InlineData(Credit, 0, true, true)]
    public async Task Upstream_Error_Fails_Once_Without_Changing_Http_Bytes(int credit, int idle, bool terminator,
        bool overDepthExtension)
    {
        var error = overDepthExtension ? Encoding.UTF8.GetBytes(
            "data: {\"error\":{\"message\":\"fixture capacity warning\"},\"extension\":" +
            new string('[', 40) + "0" + new string(']', 40) + "}\n\n") : CapacityError.ToArray();
        var expected = terminator ? error.Concat(Done).ToArray() : error;
        using var fake = new StreamEngine(new MemoryStream(expected));
        var (app, _, port) = await WireHarness.StartAsync(fake: fake, streamBufferLimit: credit, streamIdleTimeoutMs: idle);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var router = app.Services.GetRequiredService<RouterCore>();

        var (id, response) = await WireHarness.StartStreamAsync(client, "in-band-error", Chat, bound.Token);
        using var stream = response;
        Assert.Equal(expected, WithoutComments(await stream.Content.ReadAsByteArrayAsync(bound.Token)));
        using var snapshot = JsonDocument.Parse(await client.GetByteArrayAsync($"/admin/tickets/{id}", bound.Token));
        Assert.Equal("Failed", snapshot.RootElement.GetProperty("State").GetString());
        Assert.Equal("engine_error", snapshot.RootElement.GetProperty("ErrorCode").GetString());
        Assert.Equal("failed", snapshot.RootElement.GetProperty("UpstreamOutcome").GetString());
        Assert.Equal("failed", snapshot.RootElement.GetProperty("UpstreamEndReason").GetString());
        AssertFailedUpstream(router, id);
    }

    [Fact]
    public async Task Library_InBand_Error_And_Done_Fail_Once_And_Relay_Original_Bytes()
    {
        var expected = CapacityError.ToArray().Concat(Done).ToArray();
        using var fake = new StreamEngine(new MemoryStream(expected));
        var (app, _, _) = await WireHarness.StartAsync(fake: fake, streamBufferLimit: Credit);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        var ticket = await router.EnqueueAsync("library-error", Request(), bound.Token);

        var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(bound.Token));
        Assert.Equal("engine_error", error.Code);
        Assert.Equal(TicketState.Failed, ticket.State);
        var observation = Assert.IsType<SemanticStreamObservation>(ticket.StreamObservation);
        Assert.Equal(SemanticStreamOutcome.Failed, observation.Outcome);
        Assert.NotNull(observation.FirstRawByte);
        Assert.Null(observation.FirstSemanticOutput);
        Assert.Equal(StreamUsageAvailability.Unavailable, observation.UsageAvailability);
        using var bytes = new MemoryStream();
        await foreach (var chunk in ticket.StreamChannel!.Reader.ReadAllAsync(bound.Token))
            bytes.Write(chunk.Span);
        Assert.Equal(expected, bytes.ToArray());
        AssertFailedUpstream(router, ticket.Id);
    }

    private static void AssertFailedUpstream(RouterCore router, string id)
    {
        var terminal = Assert.Single(router.Events.Replay(0), row => row.TicketId == id &&
            row.Kind is "request.failed" or "request.cancelled" or "request.completed");
        Assert.Equal("request.failed", terminal.Kind);
        Assert.Equal("failed", terminal.UpstreamOutcome);
        Assert.Equal("failed", terminal.UpstreamEndReason);
    }
}
