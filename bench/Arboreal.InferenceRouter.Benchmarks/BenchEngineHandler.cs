using System.Net;
using System.Text;

namespace Arboreal.InferenceRouter.Benchmarks;

/// <summary>An in-process stand-in for an OpenAI-compatible engine (no sockets). It answers the
/// health probes (<c>/models</c>, <c>/props</c>) and serves both a fixed completion and a fixed
/// SSE stream. The payloads are shared, so every scenario pays the same engine-side cost and the
/// differences between scenarios are the router's own work.</summary>
internal sealed class BenchEngineHandler : HttpMessageHandler
{
    /// <summary>Delta frames the streaming answer contains (excluding the terminal [DONE]).</summary>
    public const int DeltaCount = 64;

    private static readonly byte[] ModelsBody = Encoding.UTF8.GetBytes("""{"data":[{"id":"bench-model"}]}""");
    private static readonly byte[] PropsBody = Encoding.UTF8.GetBytes("""{"total_slots":4,"n_ctx":131072}""");

    /// <summary>A completion body of realistic size (~300 bytes: a short answer plus metadata).</summary>
    private static readonly byte[] CompletionBody = Encoding.UTF8.GetBytes(
        """{"id":"chatcmpl-bench","object":"chat.completion","created":1730000000,"model":"bench-model","choices":[{"index":0,"message":{"role":"assistant","content":"The quick brown fox jumps over the lazy dog and keeps the benchmark payload realistic in size."},"finish_reason":"stop"}],"usage":{"prompt_tokens":280,"completion_tokens":24,"total_tokens":304}}""");

    private static readonly byte[] StreamBody = BuildStreamBody();

    /// <summary>Slots advertised by <c>/props</c>; the router will not run more than this many
    /// requests concurrently against the engine.</summary>
    public int TotalSlots { get; init; } = 4;

    private static byte[] BuildStreamBody()
    {
        var sb = new StringBuilder(DeltaCount * 110);
        for (var i = 0; i < DeltaCount; i++)
        {
            sb.Append("data: {\"id\":\"chatcmpl-bench\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"tok")
              .Append(i)
              .Append("\"}}]}\n\n");
        }
        sb.Append("data: [DONE]\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        byte[] body;
        var contentType = "application/json";

        if (path.EndsWith("models", StringComparison.Ordinal))
        {
            body = ModelsBody;
        }
        else if (path.EndsWith("props", StringComparison.Ordinal))
        {
            body = PropsBody;
        }
        else if (path.EndsWith("chat/completions", StringComparison.Ordinal) || path.EndsWith("completions", StringComparison.Ordinal))
        {
            var json = ReadJson(request);
            if (json.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                body = StreamBody;
                contentType = "text/event-stream";
            }
            else
            {
                body = CompletionBody;
            }
        }
        else
        {
            body = ModelsBody;
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return Task.FromResult(response);
    }

    private static string ReadJson(HttpRequestMessage request)
    {
        if (request.Content is null)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
    }
}
