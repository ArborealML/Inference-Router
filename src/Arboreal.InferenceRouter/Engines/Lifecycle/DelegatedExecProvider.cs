using System.Diagnostics;
using System.Text;

namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>The delegated provider: a selector program does the loading, and the router's part is to run
/// it, wait for it, and believe its exit code. This is what <c>select = { exec = … }</c> has always
/// meant; wrapping it in the seam is what lets the same switch run against the Docker Engine API, or
/// against a fake in a test, without the routing path knowing the difference.</summary>
/// <remarks>
/// Ownership here is deliberately empty. The selector owns the transaction — it is the thing that stops
/// one model and loads another — so the router does not claim to know which units it stopped, and the
/// coordinator therefore has nothing of ours to stop. That is also why a delegated model and a native
/// (label-managed) model must not be configured on the same engine: only one of them can prove ownership.
/// </remarks>
public sealed class DelegatedExecProvider : IEngineLifecycleProvider
{
    public const string ProviderKind = "exec";

    public string Kind => ProviderKind;

    /// <summary>Does this catalog entry name a selector the router can actually run? One owner for the
    /// predicate: the runtime asks it before the transaction starts, so a missing selector is answered with
    /// <c>no_select_command</c> and costs the engine nothing, and preflight asks it as the backstop for a
    /// provider a caller handed in rather than one the config picked. The two answers cannot drift because
    /// they are the same method (§5.5).</summary>
    internal static bool HasUsableSelector(EngineModelDefinition definition) =>
        definition.Select is { } select && !string.IsNullOrWhiteSpace(select.Exec);

    public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
    {
        if (HasUsableSelector(target.Definition))
            return ValueTask.FromResult(LifecycleResult.Ok(ProviderKind));
        var cmd = target.Definition.Select;
        if (cmd is null)
            return ValueTask.FromResult(LifecycleResult.Fail(
                "no_select_command",
                $"engine {target.EngineName} has no select command for {target.Model}: add select = {{ exec = … }}, give it a native lifecycle provider, or mark it remote"));
        if (string.IsNullOrWhiteSpace(cmd.Exec))
            return ValueTask.FromResult(LifecycleResult.Fail(
                "empty_exec",
                $"engine {target.EngineName} model {target.Model} has a select block with no exec"));
        return ValueTask.FromResult(LifecycleResult.Ok(ProviderKind));
    }

    public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

    public ReadinessPolicy Readiness(LifecycleTarget target)
    {
        var cmd = target.Definition.Select;
        return cmd is null
            ? new ReadinessPolicy(true, TimeSpan.Zero)
            : new ReadinessPolicy(cmd.WaitHealth, TimeSpan.FromMilliseconds(cmd.WaitTimeoutMs));
    }

    public ValueTask<LifecycleResult> StopAsync(
        LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) =>
        ValueTask.FromResult(LifecycleResult.Fail(
            "delegated_provider_cannot_stop",
            $"the selector program owns stopping for {target.EngineName}; do not configure native stop for this model"));

    public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
    {
        var cmd = target.Definition.Select!;
        var profileName = target.Profile?.Name ?? target.Definition.Profile;
        var args = ExpandArgs(cmd.Args, target.Model, profileName);

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cmd.Exec,
                Arguments = BuildArgumentString(args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        using var exitCts = new CancellationTokenSource(cmd.WaitTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, exitCts.Token);
        if (!proc.Start())
            return LifecycleResult.Fail($"failed_to_start {cmd.Exec}", $"the select exec for {target.Model} could not be started: check that {cmd.Exec} exists and is executable");

        // Drain both pipes concurrently: chatty lifecycle commands print progress, and a full pipe
        // buffer would block the child while we wait for it to exit.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        var exited = true;
        try { await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { exited = false; }

        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }   // already exited
            return LifecycleResult.Fail(
                $"timeout_after_{cmd.WaitTimeoutMs}ms",
                $"the select command for {target.Model} did not finish within {cmd.WaitTimeoutMs} ms and was killed; raise select.wait_timeout_ms or fix the selector");
        }

        var stderr = string.Empty;
        try { stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { /* pipe held by a grandchild: proceed without stderr */ }
        try { await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { /* stdout is only drained to avoid pipe deadlock */ }

        if (proc.ExitCode != 0)
        {
            var tail = stderr.Trim();
            if (tail.Length > 140) tail = tail[..140];
            return LifecycleResult.Fail(
                $"exit={proc.ExitCode} {tail}",
                $"the select command for {target.Model} exited {proc.ExitCode}: {(tail.Length == 0 ? "it printed nothing on stderr" : tail)}");
        }

        return LifecycleResult.Ok($"exit=0");
    }

    internal static string[] ExpandArgs(string[] args, string model, string? profile)
    {
        var expanded = new string[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            expanded[i] = args[i]
                .Replace("{{model}}", model, StringComparison.Ordinal)
                .Replace("{{profile}}", profile ?? "", StringComparison.Ordinal)
                .Replace("{{suffixCache}}", "", StringComparison.Ordinal);
        }
        return expanded;
    }

    /// <summary>Build the single argument string that .NET re-splits into argv. Args are quoted with
    /// CommandLineToArgvW rules (the parser .NET uses on Unix too) so lifecycle commands can carry
    /// spaces, e.g. exec=/bin/sh args=["-c", "select-model strata {{profile}}"].</summary>
    internal static string BuildArgumentString(string[] args)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            AppendQuoted(sb, a);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length == 0)
        {
            sb.Append("\"\"");
            return;
        }
        var needsQuotes = false;
        foreach (var c in arg)
        {
            if (c is ' ' or '\t' or '"' or '\\') { needsQuotes = true; break; }
        }
        if (!needsQuotes)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else { if (backslashes > 0) sb.Append('\\', backslashes); sb.Append(c); }
            backslashes = 0;
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}
