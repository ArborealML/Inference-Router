using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
                request.RootElement.GetProperty("stream").GetBoolean()));
            context.Response.StatusCode = scenario.Status;
            context.Response.ContentType = scenario.Stream && scenario.Status == 200 ? "text/event-stream" : "application/json";
            // Every response has complete HTTP framing, even when it lacks an SSE sentinel.
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, deadline.Token);
        });

        await app.StartAsync(deadline.Token);
        try
        {
            var result = await RunCliAsync(app.Urls.Single(), verb, json, scenario.Stream, deadline.Token);
            var request = await received.Task.WaitAsync(deadline.Token);
            Assert.Equal(new ObservedRequest("POST", "/v1/chat/completions", scenario.Stream), request);
            Assert.Equal(Encoding.UTF8.GetBytes(json ? scenario.JsonOut : scenario.TextOut), result.Stdout);
            Assert.Equal(scenario.ExitCode, result.ExitCode);
            Assert.Equal(Encoding.UTF8.GetBytes(json ? scenario.JsonError : scenario.TextError), result.Stderr);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(CleanupLimit);
            await app.StopAsync(cleanup.Token);
        }
    }

    private static async Task<ProcessResult> RunCliAsync(string url, string verb, bool json, bool stream, CancellationToken ct)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "air.dll");
        Assert.True(File.Exists(dll), $"CLI project output missing from test directory: {dll}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add("--server");
        start.ArgumentList.Add(url);
        if (json) start.ArgumentList.Add("--json");
        if (!stream) start.ArgumentList.Add("--no-stream");
        start.ArgumentList.Add("a prompt");

        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI.");
        try
        {
            var stdout = ReadBytesAsync(child.StandardOutput.BaseStream, ct);
            var stderr = ReadBytesAsync(child.StandardError.BaseStream, ct);
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
    private sealed record ObservedRequest(string Method, string? Path, bool Stream);
    private sealed record ProcessResult(int ExitCode, byte[] Stdout, byte[] Stderr);
}
