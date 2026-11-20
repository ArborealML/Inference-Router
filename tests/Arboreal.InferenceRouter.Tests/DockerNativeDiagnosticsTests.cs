using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class DockerNativeDiagnosticsTests
{
    private const string ContainerId = DockerLifecycleProviderTests.FullPeerId;
    private const string ExecId = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string ContainerPath = "/v1.44/containers/" + ContainerId;
    private const string ExecPath = "/v1.44/exec/" + ExecId;
    private const string Inspect = "GET " + ContainerPath + "/json";
    private const string Create = "POST " + ContainerPath + "/exec";
    private const string Start = "POST " + ExecPath + "/start";
    private const string Terminal = "GET " + ExecPath + "/json";
    private const string Logs = "GET " + ContainerPath + "/logs?follow=false&stdout=true&stderr=true&tail=256";
    private const string Raw = "application/vnd.docker.raw-stream";
    private const string Multiplexed = "application/vnd.docker.multiplexed-stream";
    private const int WireCeiling = 1024 * 1024;
    private const int RetainedBytes = 64 * 1024;
    private static readonly string[] Argv = ["check", "argument with space", "$(literal)"];
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("zero", 0)]
    [InlineData("nonzero", 7)]
    [InlineData("exit-omitted", null)]
    [InlineData("exit-null", null)]
    [InlineData("running-omitted", null)]
    [InlineData("running", null)]
    [InlineData("negative", null)]
    [InlineData("wrong-exec", null)]
    [InlineData("wrong-container", null)]
    public async Task ExecRequiresIndependentTerminalEvidence(string evidence, int? exitCode)
    {
        byte[] stdout = [0xff, 0, 0x80];
        byte[] stderr = [0xc3, 0x28];
        byte[] tail = [0x78];
        using var daemon = new DiagnosticDaemon
        {
            Override = key => key switch
            {
                Start => new(HttpStatusCode.OK, [.. Frame(1, stdout), .. Frame(2, stderr), .. Frame(1, tail)], Raw, Fragment: 2),
                Terminal => Json(HttpStatusCode.OK, TerminalJson(evidence)),
                _ => null,
            },
        };
        using var provider = Provider(daemon);

        var result = await provider.ExecAsync(ContainerId, Argv, CancellationToken.None);

        Assert.Equal(exitCode == 0, result.Result.Succeeded);
        Assert.Equal(exitCode, result.ExitCode);
        if (exitCode is not null)
        {
            var capture = Assert.IsType<DockerOutputCapture>(result.Capture);
            Assert.False(capture.Truncated);
            Assert.Equal(new byte[] { 1, 2, 1 }, capture.Frames.Select(frame => frame.Channel));
            Assert.Equal(stdout, capture.Frames[0].Payload.ToArray());
            Assert.Equal(stderr, capture.Frames[1].Payload.ToArray());
            Assert.Equal(tail, capture.Frames[2].Payload.ToArray());
        }
        Assert.Equal([Inspect, Create, Start, Terminal], daemon.Requests);
        Assert.All(daemon.Responses, AssertDisposed);
    }

    [Theory]
    [InlineData("in-band-error", "native_exec_nonzero", 126)]
    [InlineData("not-attached", "http_409", null)]
    [InlineData("wrong-status", "http_201", null)]
    [InlineData("wrong-mime", "native_diagnostics_unconfirmed", null)]
    [InlineData("upgraded", "http_101", null)]
    public async Task StartResponseCannotCertifyExit(string mode, string failure, int? exitCode)
    {
        var message = Encoding.UTF8.GetBytes("exec start failed");
        using var daemon = new DiagnosticDaemon
        {
            Override = key => key switch
            {
                Start => mode switch
                {
                    "not-attached" => Json(HttpStatusCode.Conflict, "{}"),
                    "wrong-status" => new(HttpStatusCode.Created, Frame(1, message), Raw),
                    "wrong-mime" => new(HttpStatusCode.OK, Frame(1, message), "text/plain"),
                    "upgraded" => new(HttpStatusCode.SwitchingProtocols, []),
                    _ => new(HttpStatusCode.OK, Frame(1, message), Raw),
                },
                Terminal => Json(HttpStatusCode.OK, TerminalJson("in-band-error")),
                _ => null,
            },
        };
        using var provider = Provider(daemon);

        var result = await provider.ExecAsync(ContainerId, Argv, CancellationToken.None);

        Assert.False(result.Result.Succeeded);
        Assert.Equal(failure, result.Result.Reason);
        Assert.Equal(exitCode, result.ExitCode);
        if (exitCode is not null)
        {
            var capture = Assert.IsType<DockerOutputCapture>(result.Capture);
            var frame = Assert.Single(capture.Frames);
            Assert.Equal(1, frame.Channel);
            Assert.Equal(message, frame.Payload.ToArray());
        }
        Assert.Equal(exitCode is null ? [Inspect, Create, Start] : new[] { Inspect, Create, Start, Terminal }, daemon.Requests);
        Assert.All(daemon.Responses, AssertDisposed);
    }

    [Theory]
    [InlineData("owned", true)]
    [InlineData("tty", false)]
    [InlineData("tty-omitted", false)]
    [InlineData("wrong-router", false)]
    [InlineData("wrong-engine", false)]
    [InlineData("wrong-instance", false)]
    public async Task LogsRequireExactOwnedNonTtyInstance(string evidence, bool succeeds)
    {
        using var daemon = new DiagnosticDaemon
        {
            Override = key => key == Inspect ? Json(HttpStatusCode.OK, ContainerJson(evidence)) : null,
        };
        using var provider = Provider(daemon);

        var result = await provider.ReadLogsAsync(ContainerId, CancellationToken.None);

        Assert.Equal(succeeds, result.Result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Equal(succeeds ? [Inspect, Logs] : new[] { Inspect }, daemon.Requests);
        if (succeeds)
        {
            var capture = Assert.IsType<DockerOutputCapture>(result.Capture);
            Assert.False(capture.Truncated);
            Assert.Equal(new byte[] { 1, 2 }, capture.Frames.Select(frame => frame.Channel));
            Assert.Equal(new byte[] { 0xff }, capture.Frames[0].Payload.ToArray());
            Assert.Equal(new byte[] { 0xfe }, capture.Frames[1].Payload.ToArray());
        }
    }

    [Fact]
    public async Task ExecRefusesShortIdBeforeAnyDaemonRequest()
    {
        using var daemon = new DiagnosticDaemon();
        using var provider = Provider(daemon);

        var result = await provider.ExecAsync(ContainerId[..12], Argv, CancellationToken.None);

        Assert.Equal("native_peer_id_unverifiable", result.Result.Reason);
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("{", "native_diagnostics_unconfirmed")]
    [InlineData("{\"Id\":\"fedcba\"}", "native_exec_id_unverifiable")]
    public async Task ExecRefusesUnprovedCreateIdBeforeStart(string reply, string failure)
    {
        using var daemon = new DiagnosticDaemon
        {
            Override = key => key == Create ? Json(HttpStatusCode.Created, reply) : null,
        };
        using var provider = Provider(daemon);

        var result = await provider.ExecAsync(ContainerId, Argv, CancellationToken.None);

        Assert.Equal(failure, result.Result.Reason);
        Assert.Null(result.ExitCode);
        Assert.Equal([Inspect, Create], daemon.Requests);
        Assert.All(daemon.Responses, AssertDisposed);
    }

    [Theory]
    [InlineData("bytes", -1)]
    [InlineData("bytes", 0)]
    [InlineData("bytes", 1)]
    [InlineData("frames", -1)]
    [InlineData("frames", 0)]
    [InlineData("frames", 1)]
    public async Task RetainedLimitDropsOnlyExcessAndDrainsToEof(string axis, int delta)
    {
        var byBytes = axis == "bytes";
        byte[] wire = byBytes
            ? [.. Frame(2, Enumerable.Repeat((byte)0xfd, RetainedBytes + delta).ToArray()), .. Frame(1, [])]
            : Enumerable.Range(0, 256 + delta).SelectMany(index =>
                Frame(index % 2 == 0 ? (byte)1 : (byte)2, [0xfc])).ToArray();
        using var daemon = new DiagnosticDaemon
        {
            Override = key => key == Logs ? new(HttpStatusCode.OK, wire, Multiplexed) : null,
        };
        using var provider = Provider(daemon);

        var result = await provider.ReadLogsAsync(ContainerId, CancellationToken.None);

        Assert.True(result.Result.Succeeded);
        var capture = Assert.IsType<DockerOutputCapture>(result.Capture);
        Assert.Equal(delta > 0, capture.Truncated);
        if (byBytes)
        {
            var frame = Assert.Single(capture.Frames);
            Assert.Equal(2, frame.Channel);
            Assert.Equal(RetainedBytes + Math.Min(0, delta), frame.Payload.Length);
            Assert.Equal(0xfd, frame.Payload.Span[^1]);
        }
        else
        {
            Assert.Equal(256 + Math.Min(0, delta), capture.Frames.Length);
            Assert.Equal(delta < 0 ? 1 : 2, capture.Frames[^1].Channel);
            Assert.Equal(new byte[] { 0xfc }, capture.Frames[^1].Payload.ToArray());
        }
        var response = daemon.Responses[^1];
        Assert.Equal(wire.Length, response.Body.BytesRead);
        Assert.True(response.Body.SawEof);
        Assert.Equal([Inspect, Logs], daemon.Requests);
        AssertDisposed(response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactWireLimitNeedsEofAndRefusesOneMoreByte(bool over)
    {
        var frame = Frame(1, new byte[WireCeiling - 8]);
        byte[] wire = over ? [.. frame, 0x01] : frame;
        using var daemon = new DiagnosticDaemon
        {
            Override = key => key == Start ? new(HttpStatusCode.OK, wire) : null,
        };
        using var provider = Provider(daemon);

        var result = await provider.ExecAsync(ContainerId, Argv, CancellationToken.None);

        Assert.Equal(!over, result.Result.Succeeded);
        Assert.Equal(over ? null : (int?)0, result.ExitCode);
        Assert.Equal(over ? [Inspect, Create, Start] : new[] { Inspect, Create, Start, Terminal }, daemon.Requests);
        var response = daemon.Responses[2];
        Assert.Equal(WireCeiling + (over ? 1 : 0), response.Body.BytesRead);
        Assert.Equal(!over, response.Body.SawEof);
        if (!over)
        {
            var capture = Assert.IsType<DockerOutputCapture>(result.Capture);
            Assert.True(capture.Truncated);
            Assert.Equal(RetainedBytes, Assert.Single(capture.Frames).Payload.Length);
        }
        AssertDisposed(response);
    }

    [Theory]
    [InlineData("short-header")]
    [InlineData("short-payload")]
    [InlineData("after-truncation")]
    [InlineData("daemon-error")]
    [InlineData("stdin")]
    [InlineData("reserved")]
    [InlineData("huge")]
    public async Task InvalidFrameNeverRequestsTerminalInspect(string mode)
    {
        byte[] wire = mode switch
        {
            "short-header" => [1, 0],
            "short-payload" => Frame(1, [0xff, 0xfe])[..^1],
            "after-truncation" => [.. Frame(2, new byte[RetainedBytes + 1]), 1, 0],
            "daemon-error" => Frame(3, [0xff]),
            "stdin" => Frame(0, [0xff]),
            "reserved" => [1, 0, 1, 0, 0, 0, 0, 1, 0xff],
            _ => [1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff, 0xff],
        };
        using var daemon = new DiagnosticDaemon
        {
            Override = key => key == Start ? new(HttpStatusCode.OK, wire, Multiplexed, Fragment: 5) : null,
        };
        using var provider = Provider(daemon);

        var result = await provider.ExecAsync(ContainerId, Argv, CancellationToken.None);

        Assert.Equal("native_diagnostics_unconfirmed", result.Result.Reason);
        Assert.Null(result.ExitCode);
        Assert.Equal([Inspect, Create, Start], daemon.Requests);
        if (mode == "huge") Assert.Equal(8, daemon.Responses[2].Body.BytesRead);
        Assert.All(daemon.Responses, AssertDisposed);
    }

    [Theory]
    [InlineData("container", false)]
    [InlineData("container", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("terminal", false)]
    [InlineData("terminal", true)]
    [InlineData("error", false)]
    [InlineData("error", true)]
    public async Task MetadataAndErrorCeilingsProbeOneByte(string leg, bool over)
    {
        var (key, status, json, ceiling, index) = leg switch
        {
            "container" => (Inspect, HttpStatusCode.OK, ContainerJson(), 256 * 1024, 0),
            "create" => (Create, HttpStatusCode.Created, CreateJson(), 4 * 1024, 1),
            "terminal" => (Terminal, HttpStatusCode.OK, TerminalJson(), 16 * 1024, 3),
            _ => (Start, HttpStatusCode.Conflict, "{}", 4 * 1024, 2),
        };
        var wire = Encoding.UTF8.GetBytes(json.PadRight(ceiling + (over ? 1 : 0)));
        using var daemon = new DiagnosticDaemon
        {
            Override = request => request == key ? new(status, wire, "application/json") : null,
        };
        using var provider = Provider(daemon);

        var result = await provider.ExecAsync(ContainerId, Argv, CancellationToken.None);

        Assert.Equal(!over && leg != "error", result.Result.Succeeded);
        Assert.Equal(leg == "error" ? "http_409" : over ? "native_diagnostics_unconfirmed" : null, result.Result.Reason);
        Assert.Equal(!over && leg != "error" ? (int?)0 : null, result.ExitCode);
        Assert.Equal(over || leg == "error" ? index + 1 : 4, daemon.Requests.Count);
        var response = daemon.Responses[index];
        Assert.Equal(wire.Length, response.Body.BytesRead);
        Assert.Equal(!over, response.Body.SawEof);
        AssertDisposed(response);
    }

    [Fact]
    public async Task GateWaitConsumesBudgetBeforeFirstSend()
    {
        var clock = new ReadinessDeadlineTests.ManualClock();
        using var daemon = new DiagnosticDaemon();
        using var provider = Provider(daemon, clock);
        await provider.EnterOperationAsync(CancellationToken.None);
        try
        {
            var pending = provider.ExecAsync(ContainerId, Argv, CancellationToken.None).AsTask();
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, clock.ActiveTimers);
            clock.AdvanceTo(100);
            var result = await pending.WaitAsync(Watchdog);

            Assert.Equal("native_diagnostics_timeout", result.Result.Reason);
            Assert.Empty(daemon.Requests);
        }
        finally
        {
            provider.ExitOperation();
        }
    }

    [Fact]
    public async Task TerminalInspectUsesRemainderOfOriginalBudget()
    {
        var clock = new ReadinessDeadlineTests.ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        using var daemon = new DiagnosticDaemon
        {
            Override = key =>
            {
                if (key == Start) clock.AdvanceTo(70);
                return key == Terminal
                    ? Json(HttpStatusCode.OK, TerminalJson()) with { HoldRead = entered }
                    : null;
            },
        };
        using var provider = Provider(daemon, clock);

        var pending = provider.ExecAsync(ContainerId, Argv, caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            Assert.Equal([Inspect, Create, Start, Terminal], daemon.Requests);
            AssertDisposed(daemon.Responses[2]);
            clock.AdvanceTo(99);
            Assert.False(pending.IsCompleted);
            clock.AdvanceTo(100);

            var result = await pending.WaitAsync(Watchdog);
            Assert.Equal("native_diagnostics_timeout", result.Result.Reason);
            Assert.Null(result.ExitCode);
            Assert.All(daemon.Responses, AssertDisposed);
            Assert.Equal([Inspect, Create, Start, Terminal], daemon.Requests);
        }
        finally
        {
            await caller.CancelAsync();
        }
    }

    [Fact]
    public async Task CallerCancellationDisposesAttachmentAndReleasesGateAndBorrowedHandler()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        using var daemon = new DiagnosticDaemon
        {
            Override = key => key == Start ? new(HttpStatusCode.OK, [], Raw, HoldRead: entered) : null,
        };
        using var provider = Provider(daemon);
        var pending = provider.ExecAsync(ContainerId, Argv, caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(Watchdog);
        }
        finally
        {
            await caller.CancelAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(Watchdog));
        Assert.Equal([Inspect, Create, Start], daemon.Requests);
        Assert.All(daemon.Responses, AssertDisposed);

        Assert.True((await provider.ReadLogsAsync(ContainerId, CancellationToken.None).AsTask().WaitAsync(Watchdog)).Result.Succeeded);
        provider.Dispose();
        Assert.False(daemon.IsDisposed);
        using var next = Provider(daemon);
        Assert.True((await next.ReadLogsAsync(ContainerId, CancellationToken.None).AsTask().WaitAsync(Watchdog)).Result.Succeeded);
        Assert.Equal([Inspect, Create, Start, Inspect, Logs, Inspect, Logs], daemon.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingStopOrDeleteRefusesExecWithoutDaemonRequest(bool delete)
    {
        using var daemon = new DiagnosticDaemon
        {
            Override = key =>
            {
                if (key.StartsWith("POST " + ContainerPath + "/stop?", StringComparison.Ordinal))
                    return delete ? new(HttpStatusCode.NoContent, []) : throw new HttpRequestException("lost stop reply");
                if (key.StartsWith("DELETE " + ContainerPath, StringComparison.Ordinal))
                    throw new HttpRequestException("lost deletion reply");
                return null;
            },
        };
        using var provider = Provider(daemon);
        var target = new LifecycleTarget("strata", "incumbent", null, new EngineModelDefinition { Name = "incumbent" });

        var stop = await provider.StopAsync(target, [new(ContainerId, "image", "incumbent", OwnedByUs: true)], CancellationToken.None);
        Assert.False(stop.Succeeded);
        Assert.Equal(delete ? 2 : 1, daemon.Requests.Count);
        var before = daemon.Requests.ToArray();

        var result = await provider.ExecAsync(ContainerId, Argv, CancellationToken.None);

        Assert.Equal("native_peer_mutation_unresolved", result.Result.Reason);
        Assert.Null(result.ExitCode);
        Assert.Equal(before, daemon.Requests);
    }

    private static DockerLifecycleProvider Provider(DiagnosticDaemon daemon, TimeProvider? clock = null) =>
        new(DockerLifecycleProviderTests.Cfg(callTimeoutMs: clock is null ? 15_000 : 100),
            "strata", daemon)
        { DiagnosticsClock = clock ?? TimeProvider.System };

    private static Reply Json(HttpStatusCode status, string json) =>
        new(status, Encoding.UTF8.GetBytes(json), "application/json");

    private static string ContainerJson(string evidence = "owned")
    {
        var id = evidence == "wrong-instance" ? ExecId : ContainerId;
        var router = evidence == "wrong-router" ? "R1" : "r1";
        var engine = evidence == "wrong-engine" ? "Strata" : "strata";
        var tty = evidence switch { "tty" => ""","Tty":true""", "tty-omitted" => "", _ => ""","Tty":false""" };
        return $$"""{"Id":"{{id}}","Config":{"Labels":{"arboreal.router":"{{router}}","arboreal.engine":"{{engine}}"}{{tty}} } }""";
    }

    private static string CreateJson() => $$"""{"Id":"{{ExecId}}"}""";

    private static string TerminalJson(string evidence = "zero")
    {
        var id = evidence == "wrong-exec" ? ContainerId : ExecId;
        var container = evidence == "wrong-container" ? ExecId : ContainerId;
        var running = evidence switch { "running" => ""","Running":true""", "running-omitted" => "", _ => ""","Running":false""" };
        var exit = evidence switch
        {
            "nonzero" => ""","ExitCode":7""",
            "in-band-error" => ""","ExitCode":126""",
            "exit-omitted" => "",
            "exit-null" => ""","ExitCode":null""",
            "negative" => ""","ExitCode":-1""",
            _ => ""","ExitCode":0""",
        };
        return $$"""{"ID":"{{id}}","ContainerID":"{{container}}"{{running}}{{exit}} }""";
    }

    private static byte[] Frame(byte channel, byte[] payload)
    {
        var wire = new byte[8 + payload.Length];
        wire[0] = channel;
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(wire, 8);
        return wire;
    }

    private static void AssertDisposed(ObservedReply response)
    {
        Assert.Equal(1, response.Disposals);
        Assert.True(response.ContentWasDisposed);
        Assert.True(response.Body.IsDisposed);
    }

    private sealed record Reply(HttpStatusCode Status, byte[] Bytes, string? Media = null, int Fragment = int.MaxValue,
        TaskCompletionSource? HoldRead = null);

    private sealed class DiagnosticDaemon : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<ObservedReply> Responses { get; } = [];
        public Func<string, Reply?>? Override { get; init; }
        public bool IsDisposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.False(IsDisposed);
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            Requests.Add(key);
            await AssertRequestAsync(request, key, ct);
            var scripted = Override?.Invoke(key) ?? (key switch
            {
                Inspect => Json(HttpStatusCode.OK, ContainerJson()),
                Create => Json(HttpStatusCode.Created, CreateJson()),
                Start => new(HttpStatusCode.OK, Frame(1, [0xff]), Raw),
                Terminal => Json(HttpStatusCode.OK, TerminalJson()),
                Logs => new(HttpStatusCode.OK, [.. Frame(1, [0xff]), .. Frame(2, [0xfe])], Multiplexed),
                _ => throw new InvalidOperationException($"Unexpected request {key}"),
            });
            var response = new ObservedReply(scripted);
            Responses.Add(response);
            return response;
        }

        private static async Task AssertRequestAsync(HttpRequestMessage request, string key, CancellationToken ct)
        {
            if (key is not (Create or Start)) return;
            var content = Assert.IsAssignableFrom<HttpContent>(request.Content);
            Assert.Equal("application/json", content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await content.ReadAsByteArrayAsync(ct));
            var body = json.RootElement;
            var keys = body.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal);
            if (key == Create)
            {
                Assert.Equal(["AttachStderr", "AttachStdin", "AttachStdout", "Cmd", "Privileged", "Tty"], keys);
                Assert.Equal(Argv, body.GetProperty("Cmd").EnumerateArray().Select(argument => argument.GetString()));
                Assert.True(body.GetProperty("AttachStdout").GetBoolean());
                Assert.True(body.GetProperty("AttachStderr").GetBoolean());
                Assert.False(body.GetProperty("AttachStdin").GetBoolean());
                Assert.False(body.GetProperty("Privileged").GetBoolean());
            }
            else
            {
                Assert.Equal(["Detach", "Tty"], keys);
                Assert.False(body.GetProperty("Detach").GetBoolean());
                Assert.Equal(HttpVersion.Version11, request.Version);
                Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
                Assert.Empty(request.Headers.Upgrade);
                Assert.DoesNotContain(request.Headers.Connection, header =>
                    string.Equals(header, "Upgrade", StringComparison.OrdinalIgnoreCase));
            }
            Assert.False(body.GetProperty("Tty").GetBoolean());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedReply : HttpResponseMessage
    {
        private readonly ObservedContent _content;
        public ObservedStream Body { get; }
        public int Disposals { get; private set; }
        public bool ContentWasDisposed => _content.IsDisposed;

        public ObservedReply(Reply scripted) : base(scripted.Status)
        {
            Version = HttpVersion.Version11;
            Body = new ObservedStream(scripted);
            _content = new ObservedContent(Body);
            Content = _content;
            if (scripted.Media is { } media) Content.Headers.ContentType = new MediaTypeHeaderValue(media);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedContent(ObservedStream body) : StreamContent(body)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedStream(Reply scripted) : Stream
    {
        public int BytesRead { get; private set; }
        public bool SawEof { get; private set; }
        public bool IsDisposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.False(IsDisposed);
            cancellationToken.ThrowIfCancellationRequested();
            if (scripted.HoldRead is { } entered)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var count = Math.Min(scripted.Fragment, Math.Min(buffer.Length, scripted.Bytes.Length - BytesRead));
            if (count == 0) SawEof = true;
            scripted.Bytes.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return count;
        }

        public override bool CanRead => !IsDisposed;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
