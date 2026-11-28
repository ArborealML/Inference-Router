using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Arboreal.InferenceRouter.Tests;

public sealed class CliCompletionProcessTests
{
    private const string Delta = """{"choices":[{"delta":{"content":"café "}}]}""";
    private const string SecondDelta = """{"choices":[{"delta":{"content":"雪"}}]}""";
    private const string Finish = """{"choices":[{"delta":{},"finish_reason":"stop"}]}""";
    private const string UpstreamError = """{"error":{"message":"upstream interrupted","code":"engine_error"}}""";
    private const string HttpError = """{"error":{"message":"unavailable","code":"model_unavailable"}}""";
    private const string Completion = """
        { "choices": [ { "message": { "content": "café 雪" } } ] }
        """ + "\n";
    private const string Done = "data: [DONE]\n\n";
    private const string EofDiagnostic = "error: completion stream ended before [DONE]";
    private const string FirstStatus = """[router keepalive seq=0 status="waiting"]""";
    private const string RepeatedStatus = """[router keepalive seq=1 status="waiting"]""";
    private const string ChangedStatus = """[router keepalive seq=2 status="running"]""";
    private const string Progress = ": " + FirstStatus + "\n\n: " + RepeatedStatus + "\n\n: " + ChangedStatus + "\n\n";
    private static readonly string ProgressOutput = FirstStatus + Environment.NewLine + ChangedStatus + Environment.NewLine;
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public void StandaloneConfigOptionPreservesHostOptionValues()
    {
        var remaining = new List<string>();
        Assert.True(ServerApp.TryReadConfigOption(
            ["--config", "router.toml", "--example-path", "router.toml", "--example-mode", "serve"],
            out var path, remaining));
        Assert.Equal("router.toml", path);
        Assert.Equal(["--example-path", "router.toml", "--example-mode", "serve"], remaining.ToArray());
    }

    [Fact(Timeout = 30_000)]
    public async Task NamedPipeDockerCliConfigRefusesBeforeSuccessOutput()
    {
        const string configuration = $$"""
            [engine.fixture]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1/v1"
            lifecycle_owner = "router"

            [engine.fixture.docker]
            image = "fixture"
            router_id = "test"
            socket = "{{LifecycleAuthorityTests.NamedPipeDockerAddress}}"
            """;
        var directory = Directory.CreateTempSubdirectory("air-npipe-config-");
        try
        {
            var path = Path.Combine(directory.FullName, "router.toml");
            using var deadline = new CancellationTokenSource(RunLimit);
            await File.WriteAllTextAsync(path, configuration, deadline.Token);
            var result = await RunCliAsync("config", ["--config", path], null, deadline.Token);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Equal("config invalid: " + LifecycleAuthorityTests.NamedPipeDockerError + Environment.NewLine,
                Encoding.UTF8.GetString(result.Stderr));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    public enum ResponseCase
    {
        CompletionWithSentinel,
        SentinelOnly,
        HttpFailure,
        InBandErrorWithSentinel,
        InBandErrorWithEof,
        EmptyEof,
        PartialEofAfterFinishReason,
        ProgressThenCompletion,
        ProgressThenEof,
        NonStreamingCompletion,
    }

    public static TheoryData<string, bool, ResponseCase> Responses
    {
        get
        {
            var cases = new TheoryData<string, bool, ResponseCase>();
            foreach (var verb in new[] { "chat", "complete" })
            foreach (var json in new[] { false, true })
            foreach (var response in Enum.GetValues<ResponseCase>())
                cases.Add(verb, json, response);
            return cases;
        }
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(Responses))]
    public async Task CompletionExitAndOutputMatchTheResponse(string verb, bool json, ResponseCase response)
    {
        var scenario = ScenarioFor(response);
        var (request, result) = await RunCompletionAsync(verb, scenario, url =>
        {
            var args = new List<string> { "--server", url };
            if (json) args.Add("--json");
            if (!scenario.Stream) args.Add("--no-stream");
            args.Add("a prompt");
            return args.ToArray();
        });

        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal(scenario.Stream, request.Body.GetProperty("stream").GetBoolean());
        Assert.Null(request.Thread);
        Assert.Null(request.Priority);
        Assert.False(request.Body.TryGetProperty("model", out _));
        Assert.Equal("user", Assert.Single(request.Body.GetProperty("messages").EnumerateArray()).GetProperty("role").GetString());
        Assert.Equal(Encoding.UTF8.GetBytes(json ? scenario.JsonOut : scenario.TextOut), result.Stdout);
        Assert.Equal(scenario.ExitCode, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(json ? scenario.JsonError : scenario.TextError), result.Stderr);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("complete", false)]
    [InlineData("complete", true)]
    public async Task CompletionPromptContainsOnlyPositionalsOrStdin(string verb, bool fromStdin)
    {
        const string model = "fixture-model";
        const string thread = "fixture-thread";
        const string priority = "high:200";
        const string system = "system instructions only";
        const string stdin = " \tfirst line\nsecond line \n";
        var scenario = ScenarioFor(ResponseCase.NonStreamingCompletion);
        var (request, result) = await RunCompletionAsync(verb, scenario, url => fromStdin
            ? ["-m", "--system", system, "-s", url, "--model", model,
                "--thread", thread, "-p", priority, "--json", "--no-stream"]
            : ["café", "--server", url, "middle", "-m", model, "--model", "ignored-model",
                "--system", system, "雪", "-t", thread, "--priority", priority,
                "--since", "123", "--no-stream", "--json"], stdin);

        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal(model, request.Body.GetProperty("model").GetString());
        Assert.False(request.Body.GetProperty("stream").GetBoolean());
        Assert.Equal(thread, request.Thread);
        Assert.Equal(priority, request.Priority);
        Assert.Collection(request.Body.GetProperty("messages").EnumerateArray(),
            message => AssertMessage(message, "system", system),
            message => AssertMessage(message, "user", fromStdin ? "first line\nsecond line" : "café middle 雪"));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(Completion), result.Stdout);
        Assert.Empty(result.Stderr);
    }

    private static void AssertMessage(JsonElement message, string role, string text)
    {
        Assert.Equal(role, message.GetProperty("role").GetString());
        Assert.Equal(text, message.GetProperty("content").GetString());
    }

    private static async Task<(ObservedRequest Request, ProcessResult Result)> RunCompletionAsync(
        string verb, Scenario scenario, Func<string, string[]> arguments, string? stdin = null)
    {
        using var deadline = new CancellationTokenSource(RunLimit);
        var received = new TaskCompletionSource<ObservedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var body = Encoding.UTF8.GetBytes(scenario.Body);
        app.Run(async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: deadline.Token);
            received.TrySetResult(new ObservedRequest(context.Request.Method, context.Request.Path.Value,
                context.Request.Headers.TryGetValue("X-Inference-Thread", out var thread) ? thread.ToString() : null,
                context.Request.Headers.TryGetValue("X-Inference-Priority", out var priority) ? priority.ToString() : null,
                request.RootElement.Clone()));
            context.Response.StatusCode = scenario.Status;
            context.Response.ContentType = scenario.Stream && scenario.Status == 200 ? "text/event-stream" : "application/json";
            // Every response has complete HTTP framing, even when it lacks an SSE sentinel.
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, deadline.Token);
        });

        await app.StartAsync(deadline.Token);
        try
        {
            var result = await RunCliAsync(verb, arguments(app.Urls.Single()), stdin, deadline.Token);
            var request = await received.Task.WaitAsync(deadline.Token);
            return (request, result);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(CleanupLimit);
            await app.StopAsync(cleanup.Token);
        }
    }

    internal static async Task<ProcessResult> RunCliAsync(string verb, string[] args, string? stdin, CancellationToken ct,
        Action<ProcessStartInfo>? configure = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "air.dll");
        Assert.True(File.Exists(dll), $"CLI project output missing from test directory: {dll}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(verb);
        foreach (var arg in args) start.ArgumentList.Add(arg);

        configure?.Invoke(start);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI.");
        try
        {
            var stdout = ReadBytesAsync(child.StandardOutput.BaseStream, ct);
            var stderr = ReadBytesAsync(child.StandardError.BaseStream, ct);
            if (stdin is not null) await child.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            child.StandardInput.Close();
            await Task.WhenAll(child.WaitForExitAsync(ct), stdout, stderr);
            return new ProcessResult(child.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (child.HasExited) { }
                using var cleanup = new CancellationTokenSource(CleanupLimit);
                await child.WaitForExitAsync(cleanup.Token);
            }
        }
    }

    private static async Task<byte[]> ReadBytesAsync(Stream source, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        await source.CopyToAsync(bytes, ct);
        return bytes.ToArray();
    }

    private static Scenario ScenarioFor(ResponseCase response)
    {
        var line = Environment.NewLine;
        var eof = EofDiagnostic + line;
        var error = UpstreamError + line;
        return response switch
        {
            ResponseCase.CompletionWithSentinel => Streaming(Frame(Delta) + Done, 0, "café ", Delta),
            ResponseCase.SentinelOnly => Streaming(Done, 0),
            ResponseCase.HttpFailure => new Scenario(503, true, HttpError, 1, "", "", $"error 503: {HttpError}{line}", $"error 503: {HttpError}{line}"),
            ResponseCase.InBandErrorWithSentinel => Streaming(Frame(Delta) + Frame(UpstreamError) + Done, 1, "café ", Delta + UpstreamError, error),
            ResponseCase.InBandErrorWithEof => Streaming(Frame(Delta) + Frame(UpstreamError), 1, "café ", Delta + UpstreamError, error),
            ResponseCase.EmptyEof => Streaming("", 1, textError: eof, jsonError: eof),
            ResponseCase.PartialEofAfterFinishReason => Streaming(Frame(Delta) + Frame(SecondDelta) + Frame(Finish),
                1, "café 雪", Delta + SecondDelta + Finish, eof, eof),
            ResponseCase.ProgressThenCompletion => Streaming(Progress + Frame(Delta) + Done, 0, "café ", Delta, ProgressOutput),
            ResponseCase.ProgressThenEof => Streaming(Progress, 1, textError: ProgressOutput + eof, jsonError: eof),
            ResponseCase.NonStreamingCompletion => new Scenario(200, false, Completion, 0, "café 雪" + line, Completion, "", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(response)),
        };
    }

    private static string Frame(string payload) => $"data: {payload}\n\n";

    private static Scenario Streaming(string body, int exitCode, string text = "", string json = "", string textError = "", string jsonError = "")
        => new(200, true, body, exitCode, text + Environment.NewLine, json, textError, jsonError);

    private sealed record Scenario(int Status, bool Stream, string Body, int ExitCode, string TextOut, string JsonOut,
        string TextError, string JsonError);
    private sealed record ObservedRequest(string Method, string? Path, string? Thread, string? Priority, JsonElement Body);
    internal sealed record ProcessResult(int ExitCode, byte[] Stdout, byte[] Stderr);
}
