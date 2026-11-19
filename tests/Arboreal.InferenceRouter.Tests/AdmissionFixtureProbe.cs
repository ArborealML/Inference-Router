using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Arboreal.InferenceRouter.Tests;

internal sealed class AdmissionFixtureBarrier : IDisposable
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _advance = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task;

    public async Task HoldAsync(CancellationToken fixtureLifetime)
    {
        _reached.TrySetResult();
        await _advance.Task.WaitAsync(fixtureLifetime).ConfigureAwait(false);
    }

    public void Release() => _advance.TrySetResult();

    public void Dispose() => Release();
}

/// <summary>Fixture-owned backend witness. The production grant observer and queue events are separate.</summary>
internal sealed class AdmissionFixtureSink
{
    private readonly ConcurrentQueue<byte[]> _received = new();
    private readonly Func<Task<HttpResponseMessage>> _respond;
    private int _calls;

    public AdmissionFixtureSink(Func<Task<HttpResponseMessage>>? respond = null)
    {
        _respond = respond ?? RespondOkAsync;
    }

    public int Calls => Volatile.Read(ref _calls);
    public byte[][] ReceivedBodies => _received.ToArray();

    // The fixture owns its discovery handler independently of each HTTP wrapper.
    public HttpMessageHandler CreateHandler(HttpMessageHandler discovery)
        => new Handler(discovery, this);

    // An adapter can call this directly. Caller cancellation and disposal of any one engine
    // client's HTTP wrapper cannot disable observations at this fixture-owned sink.
    public async Task<HttpResponseMessage> AcceptAsync(HttpRequestMessage request)
    {
        Interlocked.Increment(ref _calls);
        var content = request.Content ?? throw new InvalidOperationException("Expected a completion payload.");
        var bytes = await content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
        _received.Enqueue(bytes);
        return await _respond().ConfigureAwait(false);
    }

    private static Task<HttpResponseMessage> RespondOkAsync()
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""",
                Encoding.UTF8, "application/json"),
        });

    private sealed class Handler(HttpMessageHandler discovery, AdmissionFixtureSink sink) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _discovery = new(discovery, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Only the managed request under test uses POST. Existing discovery/proof fixtures own GET.
            return request.Method == HttpMethod.Post
                ? sink.AcceptAsync(request)
                : _discovery.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _discovery.Dispose();
            base.Dispose(disposing);
        }
    }
}
