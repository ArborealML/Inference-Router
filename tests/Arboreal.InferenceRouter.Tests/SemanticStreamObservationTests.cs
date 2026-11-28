using System.Text;

namespace Arboreal.InferenceRouter.Tests;

public class SemanticStreamObservationTests
{
    private const string Done = "data: [DONE]\r\n\r\n";
    private const string Finish0 = "data: {\"choices\":[{\"index\":0,\"finish_reason\":\"stop\"}]}\n\n";
    private const string Finish1 = "data: {\"choices\":[{\"index\":1,\"finish_reason\":\"length\"}]}\n\n";
    private const string Error = "data: {\"error\":{\"message\":\"fixture capacity warning\"}}\n\n";

    [Fact]
    public void Split_Multiline_Data_Preserves_Bytes_And_Separates_Timings_From_Identity_And_Usage()
    {
        var clock = new ReadinessDeadlineTests.ManualClock();
        var observation = new SemanticStreamObserver(clock, "engine-model", expectedChoices: 1);
        var blocks = new SseBlockBuffer(observer: observation);
        const string keepalive = ": upstream keepalive\r\n\r\n";
        const string role = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}],\"usage\":null}\n\n";
        const string model = "data: {\"model\":\"engine\\u002dmodel\"}\r\r";
        const string semantic = "event: message\r\ndata: {\"choices\":[\r\ndata: {\"index\":0,\"delta\":{\"reasoning_content\":\"forêt\"}}]}\r\n\r\n";
        const string usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":3,\"total_tokens\":3}}\n\n";
        using var relayed = new MemoryStream();

        clock.AdvanceTo(7);
        observation.ObserveRawByte();
        Send(keepalive);
        clock.AdvanceTo(11);
        Send(role);
        Assert.Null(observation.Snapshot().FirstSemanticOutput);
        Assert.Equal(StreamModelIdentity.Unknown, observation.Snapshot().ModelIdentity);
        Assert.Equal(StreamUsageAvailability.Unavailable, observation.Snapshot().UsageAvailability);

        clock.AdvanceTo(17);
        Send(model);
        Assert.Equal(StreamModelIdentity.Matched, observation.Snapshot().ModelIdentity);
        Assert.Null(observation.Snapshot().FirstSemanticOutput);
        clock.AdvanceTo(23);
        Send(semantic);
        Send(usage + Done);
        observation.End(StreamInputEnd.Eof);

        var result = observation.Snapshot();
        Assert.Equal(TimeSpan.FromMilliseconds(7), result.FirstRawByte);
        Assert.Equal(TimeSpan.FromMilliseconds(23), result.FirstSemanticOutput);
        Assert.Equal("engine-model", result.ObservedModel);
        Assert.Equal(StreamUsageAvailability.Available, result.UsageAvailability);
        Assert.Equal(0L, result.PromptTokens);
        Assert.Equal(3L, result.CompletionTokens);
        Assert.Equal(SemanticStreamOutcome.Complete, result.Outcome);
        Assert.Equal(SemanticStreamEndReason.Complete, result.EndReason);
        Assert.Equal(Encoding.UTF8.GetBytes(keepalive + role + model + semantic + usage + Done), relayed.ToArray());

        void Send(string wire)
        {
            var bytes = Encoding.UTF8.GetBytes(wire);
            for (var i = 0; i < bytes.Length; i++)
            {
                var (complete, overLimit) = blocks.Append(bytes.AsSpan(i, 1));
                Assert.False(overLimit);
                relayed.Write(complete.Span);
            }
        }
    }

    [Theory]
    [InlineData("{\"role\":\"assistant\"}", false)]
    [InlineData("{\"content\":\"\",\"reasoning_content\":null}", false)]
    [InlineData("{\"tool_calls\":[{\"index\":0,\"type\":\"function\",\"function\":{}}]}", false)]
    [InlineData("{\"content\":\"answer\"}", true)]
    [InlineData("{\"refusal\":\"no\"}", true)]
    [InlineData("{\"function_call\":{\"name\":\"lookup\"}}", true)]
    [InlineData("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\"}}]}", true)]
    public void Only_Content_Bearing_Deltas_Count_As_Semantic_Output(string delta, bool meaningful)
    {
        var observer = new SemanticStreamObserver(TimeProvider.System, "expected", expectedChoices: 1);
        var blocks = new SseBlockBuffer(observer: observer);
        var wire = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"index\":0,\"delta\":" + delta + "}]}\n\n");
        Assert.Equal(wire, blocks.Append(wire).Complete.ToArray());
        Assert.Equal(meaningful, observer.Snapshot().FirstSemanticOutput.HasValue);
    }

    [Theory]
    [InlineData("", "{}", "incomplete", "upstream_closed_without_terminator")]
    [InlineData("data: [DONE]\n", "{}", "incomplete", "stream_incomplete")]
    [InlineData(Finish0, "{}", "complete", "complete")]
    [InlineData(Finish0, "{\"n\":2}", "incomplete", "upstream_closed_without_terminator")]
    [InlineData(Finish0 + Finish1, "{\"n\":2}", "complete", "complete")]
    [InlineData(Finish0, "{\"n\":1,\"n\":1}", "incomplete", "upstream_closed_without_terminator")]
    [InlineData(Finish0, "{\"n\":65}", "incomplete", "upstream_closed_without_terminator")]
    [InlineData("data: [DONE]\ndata:\n\n", "{}", "unknown", "unknown")]
    [InlineData("data: {malformed}\n\n" + Done, "{}", "unknown", "unknown")]
    [InlineData("data: {malformed}\n\n" + Error + Done, "{}", "failed", "failed")]
    [InlineData(Error + Done, "{}", "failed", "failed")]
    [InlineData(Done + Error, "{}", "failed", "failed")]
    [InlineData(Done, "{}", "complete", "complete")]
    public void Terminators_Require_A_Complete_Observed_Block_And_Finish_Requires_All_Requested_Choices(
        string wire, string request, string outcome, string reason)
    {
        _ = ModelRewriter.Rewrite(Encoding.UTF8.GetBytes(request), "expected", stream: true, out var expectedChoices);
        var observer = new SemanticStreamObserver(TimeProvider.System, "expected", expectedChoices);
        var blocks = new SseBlockBuffer(observer: observer);
        blocks.Append(Encoding.UTF8.GetBytes(wire));
        observer.End(blocks.HasIncompleteBlock ? StreamInputEnd.Torn : StreamInputEnd.Eof);
        var result = observer.Snapshot();
        Assert.Equal(outcome, result.OutcomeName);
        Assert.Equal(reason, result.EndReasonName);
    }

    [Theory]
    [InlineData("{\"prompt_tokens\":0}")]
    [InlineData("{\"completion_tokens\":0}")]
    [InlineData("{\"prompt_tokens\":0,\"prompt_tokens\":0,\"completion_tokens\":0}")]
    [InlineData("{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":1}")]
    [InlineData("{\"prompt_tokens\":9223372036854775807,\"completion_tokens\":1,\"total_tokens\":9223372036854775807}")]
    [InlineData("{\"prompt_tokens\":-1,\"completion_tokens\":0}")]
    [InlineData("{\"prompt_tokens\":0,\"completion_tokens\":\"0\"}")]
    [InlineData("{\"prompt_tokens\":1,\"completion_tokens\":0}")]
    public void Invalid_Or_Conflicting_Usage_Retires_Previous_Counts(string usage)
    {
        var observer = new SemanticStreamObserver(TimeProvider.System, "expected", expectedChoices: 1);
        observer.ObserveData("{\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0}}"u8);
        Assert.Equal(StreamUsageAvailability.Available, observer.Snapshot().UsageAvailability);
        observer.ObserveData(Encoding.UTF8.GetBytes("{\"usage\":" + usage + "}"));
        observer.ObserveData("{\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0}}"u8);
        var result = observer.Snapshot();
        Assert.Equal(StreamUsageAvailability.Invalid, result.UsageAvailability);
        Assert.Null(result.PromptTokens);
        Assert.Null(result.CompletionTokens);
    }

    [Fact]
    public void Delayed_Unexpected_Identity_And_Conflicting_Reports_Are_Visible()
    {
        var observer = new SemanticStreamObserver(TimeProvider.System, "expected", expectedChoices: 1);
        observer.ObserveData("{\"model\":\"wrong\"}"u8);
        var wrong = observer.Snapshot();
        Assert.Equal("wrong", wrong.ObservedModel);
        Assert.Equal(StreamModelIdentity.Mismatch, wrong.ModelIdentity);
        Assert.Null(wrong.FirstSemanticOutput);

        observer.ObserveData("{\"model\":\"expected\",\"choices\":[{\"index\":0,\"text\":\"answer\"}]}"u8);
        var conflicting = observer.Snapshot();
        Assert.Equal(StreamModelIdentity.Conflicting, conflicting.ModelIdentity);
        Assert.NotNull(conflicting.FirstSemanticOutput);
    }

    [Fact]
    public void Observation_Bounds_Keep_Unverified_Identity_And_Deep_Data_From_Qualifying()
    {
        var observer = new SemanticStreamObserver(TimeProvider.System, "expected", expectedChoices: 1);
        var model = new string('x', SemanticStreamObserver.MaxModelUtf8Bytes + 1);
        observer.ObserveData(Encoding.UTF8.GetBytes("{\"model\":\"" + model + "\"}"));
        Assert.Null(observer.Snapshot().ObservedModel);
        Assert.Equal(StreamModelIdentity.Unknown, observer.Snapshot().ModelIdentity);
        observer.ObserveData(Encoding.UTF8.GetBytes("{\"extension\":" + new string('[', 40) + "0" + new string(']', 40) + "}"));
        observer.ObserveData("[DONE]"u8);
        observer.End(StreamInputEnd.Eof);
        Assert.Equal(SemanticStreamOutcome.Unknown, observer.Snapshot().Outcome);
    }

    [Fact]
    public void Winning_Cancellation_Freezes_Observation_Across_Late_Producer_End()
    {
        var observer = new SemanticStreamObserver(TimeProvider.System, "expected", expectedChoices: 1);
        var ticket = new RouterTicket("ticket", "thread", "expected", DateTimeOffset.UnixEpoch) { IsStreamingRequest = true };
        ticket.AttachStreamObserver(observer);
        observer.ObserveRawByte();
        Assert.True(ticket.TryEnterTerminal());
        ticket.StoreTerminalState(TicketState.Cancelled);
        var frozen = Assert.IsType<SemanticStreamObservation>(ticket.StreamObservation);
        Assert.Equal(SemanticStreamOutcome.Unknown, frozen.Outcome);
        Assert.NotNull(frozen.FirstRawByte);

        observer.ObserveData("[DONE]"u8);
        observer.End(StreamInputEnd.Eof);
        Assert.Equal(SemanticStreamOutcome.Complete, observer.Snapshot().Outcome);
        Assert.False(ticket.TryEnterTerminal());
        Assert.Same(frozen, ticket.StreamObservation);
        Assert.Equal(TicketState.Cancelled, ticket.State);
    }
}
