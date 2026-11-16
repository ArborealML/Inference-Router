using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;

namespace Arboreal.InferenceRouter.Benchmarks;

/// <summary>Measures what the router costs on top of an engine that is already replying instantly.
///
/// Every scenario talks to the same in-process fake engine, so the differences are the router's own
/// work: request validation, thread/priority bookkeeping, the model queue, the scheduler's dispatch
/// decision, and (for the HTTP scenarios) ASP.NET's request pipeline plus SSE framing.
///
/// Run: dotnet run -c Release --project bench/Arboreal.InferenceRouter.Benchmarks -- [requests] [warmup]
/// </summary>
internal static class Program
{
    private const string Model = "bench-model";
    private const int ThreadCount = 8;
    private const string AnswerText = "chatcmpl-bench";

    /// <summary>Concurrency levels every scenario is measured at (SPEC 14 asks for 1, 4 and 16).</summary>
    private static readonly int[] Concurrencies = [1, 4, 16];

    private static async Task<int> Main(string[] args)
    {
        var requests = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3000;
        var warmup = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 300;

        Console.WriteLine($"runtime  {RuntimeInformation.FrameworkDescription} {RuntimeInformation.OSDescription}");
        Console.WriteLine($"cpu      {Environment.ProcessorCount} logical cores; GC mode {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}");
        Console.WriteLine($"sample   {requests} requests per scenario, {warmup} warm-up, latencies in microseconds");
        Console.WriteLine();

        var results = new List<Measurement>();

        // 1) Engine with no router in front: the floor every other number is compared against.
        using var directHandler = new BenchEngineHandler();
        using (var http = new HttpClient(directHandler) { BaseAddress = new Uri("http://engine.invalid/v1/") })
        {
            foreach (var concurrency in Concurrencies)
            {
                await AddAsync(results, "engine direct (non-stream)", concurrency, warmup, requests,
                    DirectNonStream(http, RequestJson(stream: false))).ConfigureAwait(false);
                await AddAsync(results, "engine direct (stream)", concurrency, warmup, requests,
                    DirectStream(http, RequestJson(stream: true))).ConfigureAwait(false);
            }
        }

        // 2) RouterCore as a library: queue + scheduler + dispatch, no router-owned HTTP surface.
        using var libraryHandler = new BenchEngineHandler();
        var library = new RouterCore(BuildConfig(listen: null, libraryHandler), events: new EventRing());
        library.Start();
        for (var i = 0; i < ThreadCount; i++)
        {
            library.RegisterThread(ThreadId(i), PriorityFor(i));
        }

        await WaitAsync(() => library.Engines["fake"].Healthy, "engine health probe").ConfigureAwait(false);
        foreach (var concurrency in Concurrencies)
        {
            await AddAsync(results, "router enqueue (non-stream)", concurrency, warmup, requests,
                RouterEnqueue(library, stream: false)).ConfigureAwait(false);
            await AddAsync(results, "router enqueue (stream)", concurrency, warmup, requests,
                RouterEnqueue(library, stream: true)).ConfigureAwait(false);
        }

        await library.DisposeAsync().ConfigureAwait(false);

        // 3) Router over its own HTTP surface: the whole path a client actually pays for.
        var port = FreePort();
        using var serverHandler = new BenchEngineHandler();
        var app = ServerApp.Build(
            ["--Logging:LogLevel:Default=Warning", "--Logging:LogLevel:Microsoft.AspNetCore=Warning"],
            configOverride: BuildConfig($"http://127.0.0.1:{port}", serverHandler));
        await app.StartAsync().ConfigureAwait(false);
        using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") })
        {
            await WaitAsync(async () =>
            {
                try
                {
                    using var content = new StringContent(RequestJson(stream: false), Encoding.UTF8, "application/json");
                    using var response = await http.PostAsync("v1/chat/completions", content).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return false;
                    }

                    return Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Contains(AnswerText, StringComparison.Ordinal);
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            }, "router HTTP surface").ConfigureAwait(false);

            foreach (var concurrency in Concurrencies)
            {
                await AddAsync(results, "router http (non-stream)", concurrency, warmup, requests,
                    RouterHttp(http, "v1/chat/completions", RequestJson(stream: false), stream: false)).ConfigureAwait(false);
                await AddAsync(results, "router http (stream)", concurrency, warmup, requests,
                    RouterHttp(http, "v1/chat/completions", RequestJson(stream: true), stream: true)).ConfigureAwait(false);
            }
        }

        await app.StopAsync().ConfigureAwait(false);

        Print(results);
        return 0;
    }

    private static async Task AddAsync(List<Measurement> results, string name, int concurrency, int warmup, int requests, Func<int, Task> op)
    {
        var m = await MeasureAsync(name, concurrency, warmup, requests, op).ConfigureAwait(false);
        results.Add(m);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "  done: {0,-32} conc {1}  p50 {2:F1} us  p99 {3:F1} us  {4} B/req  {5:F0} req/s",
            m.Name, m.Concurrency, m.P50, m.P99, m.BytesPerRequest, m.RequestsPerSecond));
    }

    private static string ThreadId(int index) => "bench-" + index.ToString(CultureInfo.InvariantCulture);

    private static ThreadPriority PriorityFor(int index) => (index % 4) switch
    {
        0 => ThreadPriority.Interactive(),
        1 => ThreadPriority.High(),
        2 => ThreadPriority.Normal(),
        _ => ThreadPriority.Low(),
    };

    private static string RequestJson(bool stream)
        => $$"""{"model":"{{Model}}","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"system","content":"You are a careful assistant. {{Prompt}}"},{"role":"user","content":"Summarize the result."}],"temperature":0.2,"max_tokens":96}""";

    /// <summary>Padding so the prompt is a realistic size (~1 KB) rather than a toy.</summary>
    private const string Prompt =
        "Consider the deployment notes, the queue state, and the serving ladder that follows; weigh latency against the effect on other in-flight requests before answering.";

    private static RouterConfig BuildConfig(string? listen, HttpMessageHandler handler)
    {
        var config = new RouterConfig
        {
            DefaultModel = Model,
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 8, MaxQueuePerModel = 1024 },
            HttpHandlerFactory = _ => handler,
        };

        if (listen is not null)
        {
            config.Listen = listen;
        }

        config.Engines = new Dictionary<string, EngineDefinition>
        {
            ["fake"] = new EngineDefinition
            {
                Name = "fake",
                Kind = EngineKind.OpenAiCompatible,
                Endpoint = new Uri("http://engine.invalid/v1/"),
                Catalog = new Dictionary<string, EngineModelDefinition>
                {
                    [Model] = new EngineModelDefinition { Name = Model },
                },
            },
        };
        return config;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Func<int, Task> DirectNonStream(HttpClient http, string json) => async _ =>
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("chat/completions", content).ConfigureAwait(false);
        EnsureSuccess(response);
        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Expect(text.Contains(AnswerText, StringComparison.Ordinal), "engine answer missing from the direct path");
    };

    private static Func<int, Task> DirectStream(HttpClient http, string json) => async _ =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var counter = new FrameCounter();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            counter.Add(buffer.AsSpan(0, read));
        }

        ExpectStream(counter, "direct stream");
    };

    private static Func<int, Task> RouterHttp(HttpClient http, string path, string json, bool stream) => async _ =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        EnsureSuccess(response);
        if (stream)
        {
            await using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var counter = new FrameCounter();
            var preview = new StringBuilder(600);
            var buffer = new byte[16384];
            int read;
            while ((read = await body.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                counter.Add(buffer, read);
                if (preview.Length < 1500)
                {
                    preview.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }

            ExpectStream(counter, $"router http stream [{response.Content.Headers.ContentType}] {preview}");
            return;
        }

        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Expect(text.Contains(AnswerText, StringComparison.Ordinal), "router lost the completion body");
    };

    private static Func<int, Task> RouterEnqueue(RouterCore router, bool stream) => async index =>
    {
        var request = new InferenceRequest
        {
            Model = Model,
            Stream = stream,
            Body = Encoding.UTF8.GetBytes(RequestJson(stream)),
            Options = new RouterRequestOptions(),
        };

        var ticket = await router.EnqueueAsync(ThreadId(index % ThreadCount), request).ConfigureAwait(false);
        if (!stream)
        {
            var body = await ticket.CompletionTask.ConfigureAwait(false);
            Expect(Encoding.UTF8.GetString(body).Contains(AnswerText, StringComparison.Ordinal), "router lost the completion body");
            return;
        }

        var channel = ticket.StreamChannel ?? await ticket.StreamReady.ConfigureAwait(false)
            ?? throw new InvalidOperationException("stream request produced no channel");
        var counter = new FrameCounter();
        while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var chunk))
            {
                counter.Add(chunk.Span);
            }
        }

        ExpectStream(counter, "router enqueue stream");
    };

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "unexpected status {0} {1}",
                (int)response.StatusCode,
                response.ReasonPhrase));
        }
    }

    private static void ExpectStream(FrameCounter counter, string what)
    {
        Expect(counter.ContentFrames == BenchEngineHandler.DeltaCount, $"{what}: expected {BenchEngineHandler.DeltaCount} content deltas, got {counter.ContentFrames}");
        Expect(counter.DoneMarkers == 1, $"{what}: expected one [DONE], got {counter.DoneMarkers}");
        Expect(counter.Frames >= counter.ContentFrames + counter.DoneMarkers, $"{what}: frame tally below delta tally");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task WaitAsync(Func<bool> ready, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (!ready() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }

        Expect(ready(), $"{what} never became ready");
    }

    private static async Task WaitAsync(Func<Task<bool>> ready, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (await ready().ConfigureAwait(false) == false && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }

        Expect(await ready().ConfigureAwait(false), $"{what} never became ready");
    }

    private static async Task<Measurement> MeasureAsync(
        string name, int concurrency, int warmup, int requests, Func<int, Task> op)
    {
        for (var i = 0; i < warmup; i++)
        {
            await op(i).ConfigureAwait(false);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var samples = new double[requests];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        for (var start = 0; start < requests; start += concurrency)
        {
            var count = Math.Min(concurrency, requests - start);
            var running = new Task[count];
            for (var i = 0; i < count; i++)
            {
                running[i] = TimedAsync(op, start + i, samples, start + i);
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }

        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(samples);
        return new Measurement(
            name,
            concurrency,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            allocated / requests,
            requests / clock.Elapsed.TotalSeconds);
    }

    private static async Task TimedAsync(Func<int, Task> op, int index, double[] sink, int slot)
    {
        var started = Stopwatch.GetTimestamp();
        await op(index).ConfigureAwait(false);
        sink[slot] = (Stopwatch.GetTimestamp() - started) * 1_000_000d / Stopwatch.Frequency;
    }

    private static double Percentile(double[] sorted, double fraction)
        => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1)];

    private static void Print(List<Measurement> results)
    {
        Console.WriteLine($"{"scenario",-32} {"conc",4} {"p50",9} {"p95",9} {"p99",9} {"alloc B/req",12} {"req/s",9}");
        foreach (var m in results)
        {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0,-32} {1,4} {2,9:F1} {3,9:F1} {4,9:F1} {5,12} {6,9:F0}",
                m.Name, m.Concurrency, m.P50, m.P95, m.P99, m.BytesPerRequest, m.RequestsPerSecond));
        }

        Console.WriteLine();
        foreach (var concurrency in Concurrencies)
        {
            var floor = Find(results, "engine direct (non-stream)", concurrency);
            var enqueue = Find(results, "router enqueue (non-stream)", concurrency);
            var enqueueStream = Find(results, "router enqueue (stream)", concurrency);
            var floorStream = Find(results, "engine direct (stream)", concurrency);
            var http = Find(results, "router http (non-stream)", concurrency);
            var httpStream = Find(results, "router http (stream)", concurrency);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "concurrency {0}: routing adds {1:F1} us p50 / {2} B per request; the full HTTP path adds {3:F1} us p50 / {4} B; streaming relay adds {5:F1} us p50 / {6} B",
                concurrency,
                enqueue.P50 - floor.P50,
                enqueue.BytesPerRequest - floor.BytesPerRequest,
                http.P50 - floor.P50,
                http.BytesPerRequest - floor.BytesPerRequest,
                enqueueStream.P50 - floorStream.P50,
                enqueueStream.BytesPerRequest - floorStream.BytesPerRequest));
        }
    }

    private static Measurement Find(List<Measurement> results, string name, int concurrency)
        => results.First(m => m.Name == name && m.Concurrency == concurrency);
}

/// <summary>Counts what a streamed response actually contains: SSE frames, content deltas, and the
/// terminal [DONE]. Chunk boundaries can split a marker, so the tail of each chunk is re-joined with
/// the next. The router is allowed to add its own status frames, hence three separate tallies.</summary>
internal sealed class FrameCounter
{
    private const string FrameMarker = "data: ";
    private const string ContentMarker = "\"content\":\"tok";
    private const string DoneMarker = "[DONE]";
    private static readonly int Longest = ContentMarker.Length;
    private string _tail = string.Empty;

    /// <summary>SSE frames seen (engine deltas plus any router status frames).</summary>
    public int Frames { get; private set; }

    /// <summary>Delta frames carrying generated text.</summary>
    public int ContentFrames { get; private set; }

    /// <summary>Terminal markers seen; a well-formed stream has exactly one.</summary>
    public int DoneMarkers { get; private set; }

    public void Add(ReadOnlySpan<byte> chunk) => AddText(Encoding.UTF8.GetString(chunk));

    public void Add(byte[] buffer, int length) => AddText(Encoding.UTF8.GetString(buffer, 0, length));

    private void AddText(string text)
    {
        var joined = _tail.Length == 0 ? text : _tail + text;
        Frames += Count(joined, FrameMarker);
        ContentFrames += Count(joined, ContentMarker);
        DoneMarkers += Count(joined, DoneMarker);
        _tail = joined.Length <= Longest - 1 ? joined : joined.Substring(joined.Length - (Longest - 1));
    }

    private static int Count(string text, string marker)
    {
        var found = 0;
        var at = 0;
        while ((at = text.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
        {
            found++;
            at += marker.Length;
        }

        return found;
    }
}

internal sealed record Measurement(
    string Name,
    int Concurrency,
    double P50,
    double P95,
    double P99,
    long BytesPerRequest,
    double RequestsPerSecond);
