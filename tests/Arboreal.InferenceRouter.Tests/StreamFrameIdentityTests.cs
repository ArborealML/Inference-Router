using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Arboreal.InferenceRouter.Tests;

public class StreamFrameIdentityTests
{
    private const string Request = """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"ping"}]}""";
    private static readonly byte[] ShortFixture =
        "data: {\"delta\":\"é🌲\"}\r\n\r\n: upstream comment\nid: 02\ndata: second\ndata: line two\n\ndata: [DONE]\r\n\r\n"u8.ToArray();

    [Fact]
    public Task Every_Byte_Split_Preserves_The_Short_Stream()
        => CheckSplitsAsync(ShortFixture, Enumerable.Range(0, ShortFixture.Length + 1), ShortFixture.Length);

    [Fact]
    public Task Every_64_Byte_Alignment_Preserves_The_Long_Stream()
    {
        var payload = new string('x', 45);
        var frames = Enumerable.Range(0, 320).Select(i =>
            FormattableString.Invariant($"id: {i}\r\ndata: {{\"delta\":\"{i} é🌲 {payload}\"}}\r\n\r\n"));
        var fixture = Encoding.UTF8.GetBytes(string.Concat(frames) + "data: [DONE]\r\n\r\n");
        return CheckSplitsAsync(fixture, Enumerable.Range(0, 64), 64);
    }

    [Fact]
    public async Task Queued_Keepalives_Precede_An_Upstream_Frame_Delivered_In_Two_Reads()
    {
        using var fake = new FragmentedEngine();
        var (app, _, port) = await WireHarness.StartAsync(maxInflight: 1, fake: fake);
        await using var server = app;
        using var http = Client(port);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = bound.Token;

        var releaseSlot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var occupyingBody = new FragmentedBody(ShortFixture, 0, ShortFixture.Length, releaseSlot.Task);
        fake.Responses.Enqueue(occupyingBody);
        var (_, occupyingResponse) = await WireHarness.StartStreamAsync(http, "occupying", Request);
        using var occupying = occupyingResponse;
        // The fake has been read, so this request owns the only slot before the next request starts.
        await occupyingBody.AtGate.Task.WaitAsync(ct);

        var split = ShortFixture.AsSpan().IndexOf("🌲"u8) + 1;
        var releaseSuffix = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pendingBody = new FragmentedBody(ShortFixture, split, ShortFixture.Length, releaseSuffix.Task);
        fake.Responses.Enqueue(pendingBody);
        var (_, pendingResponse) = await WireHarness.StartStreamAsync(http, "queued", Request);
        using var pending = pendingResponse;
        await using var clientBody = await pending.Content.ReadAsStreamAsync(ct);

        // Sequence 2 requires an actual periodic tick. Keep the slot occupied until the client
        // sees it, so admission cannot win a race against the timer.
        var queuedFrames = ("data: [router keepalive seq=0 status=\"queued model=test-model\"]\n\n" +
                            "data: [router keepalive seq=1]\n\n" +
                            "data: [router keepalive seq=2]\n\n");
        await ReadExpectedAsync(clientBody, Encoding.UTF8.GetBytes(queuedFrames), ct);

        releaseSlot.SetResult();
        var prefix = ShortFixture.AsMemory(0, split);
        var start = new byte["data: {"u8.Length];
        var nextSequence = 3;
        while (true)
        {
            await clientBody.ReadExactlyAsync(start, ct);
            if (prefix.Span.StartsWith(start)) break;

            // Admission can race another tick after the slot is released. Accept only complete,
            // consecutive keepalives before the first upstream byte, including a status change.
            var keepalive = Encoding.UTF8.GetBytes(FormattableString.Invariant($"data: [router keepalive seq={nextSequence++}"));
            Assert.Equal(keepalive[..start.Length], start);
            await ReadExpectedAsync(clientBody, keepalive.AsMemory(start.Length), ct);
            var end = await ReadKeepaliveEndAsync(clientBody, ct);
            Assert.True(end is "]" or " status=\"engine=\"]" or " status=\"engine=fake\"]",
                $"The relay emitted an unexpected keepalive ending: {end}");
        }

        // The client must see the incomplete frame before the fake can return its suffix.
        await ReadExpectedAsync(clientBody, prefix[start.Length..], ct);
        await pendingBody.AtGate.Task.WaitAsync(ct);
        releaseSuffix.SetResult();
        using var tail = new MemoryStream();
        await clientBody.CopyToAsync(tail, ct);
        Assert.True(ShortFixture.AsSpan(split).SequenceEqual(tail.ToArray()),
            $"The relay tore, changed, duplicated, or inserted a keepalive into the upstream frame split at byte {split}.");
    }

    private static async Task CheckSplitsAsync(byte[] fixture, IEnumerable<int> splits, int stride)
    {
        using var fake = new FragmentedEngine();
        var (app, _, port) = await WireHarness.StartAsync(fake: fake);
        await using var server = app;
        using var http = Client(port);
        foreach (var split in splits)
            await CheckSplitAsync(http, fake, fixture, split, stride);
    }

    private static async Task CheckSplitAsync(HttpClient http, FragmentedEngine fake, byte[] fixture, int split, int stride)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new FragmentedBody(fixture, split, stride);
        fake.Responses.Enqueue(upstream);
        var (_, response) = await WireHarness.StartStreamAsync(http, "identity", Request);
        using var res = response;
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        var received = await res.Content.ReadAsByteArrayAsync(bound.Token);
        var upstreamStart = LeadingKeepaliveLength(received);
        Assert.True(fixture.AsSpan().SequenceEqual(received.AsSpan(upstreamStart)),
            $"SSE bytes changed at split {split} with stride {stride}. Expected {fixture.Length} upstream bytes, received {received.Length - upstreamStart}.");
    }

    private static int LeadingKeepaliveLength(ReadOnlySpan<byte> bytes)
    {
        var start = 0;
        // Strip only the contiguous router prefix. Any extra bytes after the first upstream
        // byte, including an injected keepalive at an SSE boundary, fail full byte equality.
        while (bytes[start..].StartsWith("data: [router keepalive seq="u8))
        {
            var end = bytes[start..].IndexOf("\n\n"u8);
            Assert.True(end >= 0, "The relay emitted an unterminated keepalive.");
            start += end + 2;
        }
        Assert.True(start > 0, "The relay omitted the initial keepalive.");
        return start;
    }

    private static HttpClient Client(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static async Task ReadExpectedAsync(Stream stream, ReadOnlyMemory<byte> expected, CancellationToken ct)
    {
        var received = new byte[expected.Length];
        await stream.ReadExactlyAsync(received, ct);
        Assert.Equal(expected.ToArray(), received);
    }

    private static async Task<string> ReadKeepaliveEndAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[32];
        for (var length = 1; length < buffer.Length; length++)
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(length - 1, 1), ct);
            if (length >= 2 && buffer.AsSpan(0, length).EndsWith("\n\n"u8))
                return Encoding.UTF8.GetString(buffer, 0, length - 2);
        }
        throw new InvalidDataException("The relay did not finish its keepalive before upstream data.");
    }

    private sealed class FragmentedEngine : FakeEngineHandler
    {
        public ConcurrentQueue<Stream> Responses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post) return base.SendAsync(request, cancellationToken);

            Assert.True(Responses.TryDequeue(out var body), "No scripted upstream response for this request.");
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    // The HTTP handler supplies this stream directly to the upstream pump, so TCP coalescing
    // cannot erase the scripted boundaries. A zero-length partition is skipped: a zero read is EOF.
    private sealed class FragmentedBody(byte[] bytes, int split, int stride, Task? gate = null) : Stream
    {
        private int _offset;
        private int _nextBoundary = split == 0 ? stride : split;
        public TaskCompletionSource AtGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (gate is not null && _offset == split)
            {
                AtGate.TrySetResult();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var end = Math.Min(_nextBoundary, bytes.Length);
            var count = Math.Min(end - _offset, buffer.Length);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == _nextBoundary) _nextBoundary += stride;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
