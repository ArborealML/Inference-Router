using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Arboreal.InferenceRouter.Lifecycle;

internal readonly record struct DockerDiagnosticResult(
    LifecycleResult Result, DockerOutputCapture? Capture = null, int? ExitCode = null);

internal sealed record DockerOutputCapture(ImmutableArray<DockerOutputFrame> Frames, bool Truncated);
internal readonly record struct DockerOutputFrame(byte Channel, ReadOnlyMemory<byte> Payload);

public sealed partial class DockerLifecycleProvider
{
    private const int ContainerInspectCeiling = 256 * 1024;
    private const int ExecCreateCeiling = 4 * 1024;
    private const int ExecInspectCeiling = 16 * 1024;
    private const int ErrorBodyCeiling = 4 * 1024;

    internal TimeProvider DiagnosticsClock { get; init; } = TimeProvider.System;

    internal ValueTask<DockerDiagnosticResult> ExecAsync(
        string containerId, IReadOnlyList<string> argv, CancellationToken ct) =>
        RunDiagnosticsAsync(containerId, argv.ToArray(), ct);

    internal ValueTask<DockerDiagnosticResult> ReadLogsAsync(string containerId, CancellationToken ct) =>
        RunDiagnosticsAsync(containerId, null, ct);

    private async ValueTask<DockerDiagnosticResult> RunDiagnosticsAsync(
        string containerId, string[]? command, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(_cfg.CallTimeoutMs), DiagnosticsClock);
        using var sequence = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = sequence.Token;
        try
        {
            await EnterOperationAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if ((command is not null || Volatile.Read(ref _disposed) != 0) && PeerWriteRefusal() is { } refusal)
                    return new(refusal);
                if (!IsFullContainerId(containerId))
                    return DiagnosticsFailure("native_peer_id_unverifiable", "Use the full Docker container ID.");

                var path = $"{_cfg.ApiVersion}/containers/{containerId}";
                DockerDiagnosticContainerResponse observed;
                using (var inspect = new HttpRequestMessage(HttpMethod.Get, $"{path}/json"))
                {
                    observed = await ReadDiagnosticsJsonAsync(inspect, HttpStatusCode.OK, ContainerInspectCeiling,
                        DockerJsonContext.Default.DockerDiagnosticContainerResponse, token).ConfigureAwait(false);
                }
                if (!MatchesOwnedContainer(observed.Id, observed.Config?.Labels, containerId))
                    return DiagnosticsFailure("native_diagnostics_unowned", "Docker did not prove the exact owned container.");

                if (command is not null)
                    return await ExecUnderGateAsync(containerId, path, command, token).ConfigureAwait(false);
                if (observed.Config?.Tty is not false)
                    return DiagnosticsFailure("native_diagnostics_tty_unverified", "Docker did not prove non-TTY logs.");

                using var logs = new HttpRequestMessage(HttpMethod.Get,
                    $"{path}/logs?follow=false&stdout=true&stderr=true&tail=256");
                var capture = await ReadDiagnosticsFramesAsync(logs, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new(LifecycleResult.Ok(), capture);
            }
            finally
            {
                ExitOperation();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return DiagnosticsFailure("native_diagnostics_timeout", "The Docker diagnostics sequence exhausted its budget.");
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            return DiagnosticsFailure(ex.StatusCode is { } status ? $"http_{(int)status}" : "daemon_unreachable", ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or SocketException)
        {
            ct.ThrowIfCancellationRequested();
            return DiagnosticsFailure("native_diagnostics_unconfirmed", ex.Message);
        }
    }

    private async ValueTask<DockerDiagnosticResult> ExecUnderGateAsync(
        string containerId, string containerPath, string[] command, CancellationToken ct)
    {
        DockerCreateResponse created;
        using (var create = new HttpRequestMessage(HttpMethod.Post, $"{containerPath}/exec"))
        {
            create.Content = JsonContent.Create(new DockerExecCreateRequest(command), DockerJsonContext.Default.DockerExecCreateRequest);
            created = await ReadDiagnosticsJsonAsync(create, HttpStatusCode.Created, ExecCreateCeiling,
                DockerJsonContext.Default.DockerCreateResponse, ct).ConfigureAwait(false);
        }
        if (!IsFullContainerId(created.Id))
            return DiagnosticsFailure("native_exec_id_unverifiable", "Docker did not return a full exec ID.");

        var path = $"{_cfg.ApiVersion}/exec/{created.Id}";
        DockerOutputCapture capture;
        using (var start = new HttpRequestMessage(HttpMethod.Post, $"{path}/start")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        })
        {
            start.Content = JsonContent.Create(new DockerExecStartRequest(), DockerJsonContext.Default.DockerExecStartRequest);
            capture = await ReadDiagnosticsFramesAsync(start, ct).ConfigureAwait(false);
        }

        using var inspect = new HttpRequestMessage(HttpMethod.Get, $"{path}/json");
        var terminal = await ReadDiagnosticsJsonAsync(inspect, HttpStatusCode.OK, ExecInspectCeiling,
            DockerJsonContext.Default.DockerExecInspectResponse, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (terminal.Id != created.Id || terminal.ContainerId != containerId || terminal.Running is not false ||
            terminal.ExitCode is not int exitCode || exitCode < 0)
            return DiagnosticsFailure("native_exec_unconfirmed", "Docker did not prove the terminal result of the exact exec.");

        return new(exitCode == 0 ? LifecycleResult.Ok() :
            LifecycleResult.Fail("native_exec_nonzero", $"Docker exec exited with code {exitCode}."), capture, exitCode);
    }

    private async ValueTask<T> ReadDiagnosticsJsonAsync<T>(
        HttpRequestMessage request, HttpStatusCode requiredStatus, int ceiling, JsonTypeInfo<T> type,
        CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await RequireDiagnosticsStatusAsync(response, requiredStatus, ct).ConfigureAwait(false);
        var body = await ReadDiagnosticsBodyAsync(response.Content, ceiling, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body.AsSpan(), type) ?? throw new InvalidDataException("Docker returned no metadata.");
    }

    private async ValueTask<DockerOutputCapture> ReadDiagnosticsFramesAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await RequireDiagnosticsStatusAsync(response, HttpStatusCode.OK, ct).ConfigureAwait(false);
        if (request.Method == HttpMethod.Post && (response.Version != HttpVersion.Version11 ||
            response.Headers.Connection.Contains("Upgrade", StringComparer.OrdinalIgnoreCase) || response.Headers.Upgrade.Count != 0))
            throw new InvalidDataException("Docker exec did not use HTTP/1.1 attachment.");
        if (response.Content.Headers.ContentType?.MediaType is { } mediaType
            && !string.Equals(mediaType, "application/vnd.docker.multiplexed-stream", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mediaType, "application/vnd.docker.raw-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Docker did not return an attached output stream.");

        // HttpContent caches this stream and disposes it with the response.
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await DockerOutputReader.ReadAsync(stream, ct).ConfigureAwait(false);
    }

    private static async ValueTask RequireDiagnosticsStatusAsync(
        HttpResponseMessage response, HttpStatusCode requiredStatus, CancellationToken ct)
    {
        if (response.StatusCode == requiredStatus) return;
        var status = (int)response.StatusCode;
        string detail;
        try
        {
            var body = await ReadDiagnosticsBodyAsync(response.Content, ErrorBodyCeiling, ct).ConfigureAwait(false);
            detail = Encoding.UTF8.GetString(body, 0, Math.Min(180, body.Length));
        }
        catch (InvalidDataException ex)
        {
            detail = ex.Message;
        }
        throw new HttpRequestException($"Unexpected Docker HTTP {status}: {detail}", null, response.StatusCode);
    }

    private static async ValueTask<byte[]> ReadDiagnosticsBodyAsync(HttpContent content, int ceiling, CancellationToken ct)
    {
        // The enclosing response owns the stream cached by its content.
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var body = new MemoryStream();
        var scratch = new byte[Math.Min(8192, ceiling)];
        while (body.Length < ceiling)
        {
            var count = Math.Min(scratch.Length, ceiling - (int)body.Length);
            var read = await stream.ReadAsync(scratch.AsMemory(0, count), ct).ConfigureAwait(false);
            if (read == 0) return body.ToArray();
            body.Write(scratch, 0, read);
        }
        if (await stream.ReadAsync(scratch.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Docker response exceeded its byte limit.");
        return body.ToArray();
    }

    private static DockerDiagnosticResult DiagnosticsFailure(string reason, string detail) =>
        new(LifecycleResult.Fail(reason, detail));
}
