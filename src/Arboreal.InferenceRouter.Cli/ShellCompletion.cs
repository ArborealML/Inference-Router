using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Cli;

internal static class ShellCompletion
{
    private static readonly string[] Shells = ["bash", "powershell", "zsh", "fish"];
    private static readonly string[] Commands =
        ["serve", "stop", "chat", "complete", "models", "status", "top", "events", "threads", "switch", "config", "tickets", "tune", "completion", "help"];
    private static readonly string[] ServerOptions = ["-s", "--server"];
    private static string[] Options(string command) => command switch
    {
        "chat" or "complete" => [.. ServerOptions, "-m", "--model", "-t", "--thread", "-p", "--priority", "--system", "--since", "--no-stream"],
        "models" or "status" or "tickets" => ServerOptions,
        "top" => [.. ServerOptions, "--ticket"],
        "events" => [.. ServerOptions, "--since"],
        "threads" => [.. ServerOptions, "-i", "--id", "--thread", "-p", "--priority"],
        "switch" => [.. ServerOptions, "-e", "--engine", "-m", "--model"],
        "config" => ["-c", "--config"],
        "serve" => ["-c", "--config", "-l", "--listen", "--foreground"],
        "tune plan" => ["--config", "--engine", "--model", "--profile", "--seed", "--max-concurrency"],
        "tune compare" => ["--baseline", "--candidate"],
        _ => [],
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is [var shell] && Shells.Contains(shell, StringComparer.Ordinal))
        {
            using var script = typeof(ShellCompletion).Assembly.GetManifestResourceStream("AirCompletion/" + shell)
                ?? throw new InvalidOperationException("Missing packaged completion script.");
            using var output = Console.OpenStandardOutput();
            await script.CopyToAsync(output).ConfigureAwait(false);
            return 0;
        }
        if (args is ["candidates", var token, ..] && token.StartsWith("--current=", StringComparison.Ordinal))
        {
            var prefix = token["--current=".Length..];
            try
            {
                var candidates = await CandidatesAsync(args[2..], prefix).ConfigureAwait(false);
                foreach (var item in candidates.Where(value => value.Length is > 0 and <= 4096 && !value.Any(char.IsControl) &&
                             value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(512))
                    Console.WriteLine(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RouterConfigException or ArgumentException)
            {
                // A missing or invalid config/path leaves dynamic completion empty.
            }
            return 0;
        }
        Console.Error.WriteLine("usage: air completion bash|powershell|zsh|fish");
        return 2;
    }

    private static async Task<IEnumerable<string>> CandidatesAsync(string[] prior, string prefix)
    {
        if (prior.Contains("--", StringComparer.Ordinal)) return [];
        var words = prior.Where(w => w != "--json").ToArray();
        if (words.Length == 0) return [.. Commands, "--json", "--help", "-h"];
        string command = words[0];
        if (!Commands.Contains(command, StringComparer.Ordinal)) return [];
        if (command == "completion") return words.Length == 1 ? Shells : [];
        if (command == "tune")
        {
            if (words.Length == 1) return ["compare", "plan", "--json"];
            if (words[1] is not ("compare" or "plan")) return [];
            command += " " + words[1];
        }

        var options = Options(command);
        var previous = prior[^1];
        if (options.Contains(previous, StringComparer.Ordinal) && previous != "--no-stream" &&
            (prefix.Length == 0 || prefix[0] != '-'))
        {
            if (previous is "-c" or "--config" or "--baseline" or "--candidate")
                return PathCandidates(prefix);
            if (previous == "--max-concurrency")
                return Enumerable.Range(1, 8).Select(n => n.ToString(CultureInfo.InvariantCulture));
            if (previous is "-p" or "--priority") return ["background", "low", "normal", "high", "interactive"];
            if (previous is not ("-m" or "--model" or "-e" or "--engine" or "--profile")) return [];

            var engines = await CatalogAsync(Program.GetOpt(words, ServerOptions), Program.DefaultServer,
                Program.GetOpt(words, ["--config"]), command == "tune plan").ConfigureAwait(false);
            if (previous is "-e" or "--engine") return engines.Select(e => e.Name);
            var engine = Program.GetOpt(words, ["-e", "--engine"]);
            var models = engines.Where(e => engine is null || e.Name.Equals(engine, StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => e.Models);
            if (previous != "--profile") return models.SelectMany(model => model.Aliases.Prepend(model.Id));
            var name = Program.GetOpt(words, ["-m", "--model"]);
            return models.Where(model => name is not null && (model.Id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                model.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase))).SelectMany(model => model.Profiles.Select(profile => profile.Name));
        }
        return (command == "top" ? options : options.Append("--json")).Where(option => !prior.Contains(option, StringComparer.Ordinal));
    }

    private static IEnumerable<string> PathCandidates(string prefix)
    {
        if (prefix == "~")
        {
            yield return "~" + Path.DirectorySeparatorChar;
            yield break;
        }
        var directory = Path.GetDirectoryName(prefix);
        var start = prefix.Length - Path.GetFileName(prefix).Length;
        // Preserve the typed prefix in the result; only expand it when reading the directory.
        if (directory == "~" || (directory is { Length: >= 2 } && directory[0] == '~' &&
            (directory[1] == Path.DirectorySeparatorChar || directory[1] == Path.AltDirectorySeparatorChar)))
        {
            var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            if (!Path.IsPathFullyQualified(home)) yield break;
            directory = directory.Length > 2 ? Path.Join(home, directory.AsSpan(2)) : home;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(string.IsNullOrEmpty(directory) ? "." : directory))
            yield return prefix[..start] + Path.GetFileName(entry) + (Directory.Exists(entry) ? Path.DirectorySeparatorChar.ToString() : "");
    }

    internal static async Task<IEnumerable<CompletionEngine>> CatalogAsync(string? server, string defaultServer,
        string? configPath, bool offline)
    {
        if (!offline)
        {
            var live = await ReadCatalogAsync(server ?? defaultServer).ConfigureAwait(false);
            if (live is not null) return live;
        }
        var path = RouterConfigLoader.ResolveFilePath(configPath);
        var local = File.Exists(path);
        if (!local || new FileInfo(path).Length is <= 0 or > 4 * 1024 * 1024) return [];
        var config = RouterConfigLoader.LoadFile(path);
        return config.Engines.Select(engine => new CompletionEngine(engine.Key, engine.Value.Catalog.Select(model =>
            new CompletionModel(model.Key, [.. model.Value.Aliases], model.Value.ServingProfiles
                .Select(profile => new CompletionProfile(profile.Name)).ToArray())).ToArray()));
    }

    private static async Task<CompletionEngine[]?> ReadCatalogAsync(string server)
    {
        if (!Uri.TryCreate(server.TrimEnd('/') + "/admin/catalog", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return null;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(750), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        try
        {
            using var response = await http.GetAsync(uri).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var catalog = JsonSerializer.Deserialize(data, CompletionJsonContext.Default.CompletionCatalog);
            var engines = catalog?.Engines;
            return engines is not null && engines.All(e => e?.Name is not null && e.Models is not null &&
                e.Models.All(m => m?.Id is not null && m.Aliases is not null && m.Aliases.All(a => a is not null) &&
                m.Profiles is not null && m.Profiles.All(p => p?.Name is not null))) ? engines : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }
}

internal sealed record CompletionCatalog(CompletionEngine[] Engines);
internal sealed record CompletionEngine(string Name, CompletionModel[] Models);
internal sealed record CompletionModel(string Id, string[] Aliases, CompletionProfile[] Profiles);
internal sealed record CompletionProfile(string Name);

[JsonSerializable(typeof(CompletionCatalog))]
internal sealed partial class CompletionJsonContext : JsonSerializerContext;
