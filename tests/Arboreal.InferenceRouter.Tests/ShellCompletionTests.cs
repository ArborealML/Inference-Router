using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using static Arboreal.InferenceRouter.Tests.CliCompletionProcessTests;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ShellCompletionTests
{
    private const string Config = """
        [engine."engine one"]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:1/v1"
        remote = true
        [engine."engine one".catalog."cold model"]
        aliases = ["cold-alias", "literal$(touch marker)"]
        default_profile = "big"
        [[engine."engine one".catalog."cold model".serving]]
        name = "big"
        max_context = 65536
        max_slots = 1
        [engine.other]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:1/v1"
        remote = true
        [engine.other.catalog.unrelated]
        aliases = ["cold-alias"]
        """;

    [Fact(Timeout = 30_000)]
    public async Task OfflineCandidatesFollowGrammarAndExplicitConfig()
    {
        var directory = Directory.CreateTempSubdirectory("air-shell-");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var config = Path.Combine(directory.FullName, "catalog with spaces.toml");
            await File.WriteAllTextAsync(config, Config, deadline.Token);
            Task<ProcessResult> Query(string prefix, params string[] words) =>
                RunCliAsync("completion", ["candidates", "--current=" + prefix, .. words], null, deadline.Token, process =>
                {
                    process.WorkingDirectory = directory.FullName;
                    process.Environment["ARBOREAL_ROUTER_CONFIG"] = config;
                    process.Environment["AIR_ROUTER_URL"] = "http://127.0.0.1:1";
                });
            Assert.Equal(["switch"], Lines(await Query("sw")));
            Assert.Equal(["--engine"], Lines(await Query("--en", "--json", "switch")));
            Assert.Empty(Lines(await Query("--en", "models")));
            Assert.Equal(["cold model", "cold-alias", "literal$(touch marker)"],
                Lines(await Query("", "switch", "-e", "engine one", "-m")));
            Assert.Equal(["cold model", "cold-alias"], Lines(await Query("cold", "complete", "--model")));
            Assert.Empty(Lines(await Query("cold", "chat", "--system")));
            Assert.Empty(Lines(await Query("cold", "tickets")));
            Assert.Empty(Lines(await Query("--model", "chat", "--")));
            Assert.Equal(["big"], Lines(await Query("", "tune", "plan", "--config", config, "--model", "cold-alias",
                "--engine", "engine one", "--profile")));
            Assert.Empty(Lines(await Query("", "tune", "plan", "--config", Path.Combine(directory.FullName, "missing"), "--engine")));
            Assert.False(File.Exists(Path.Combine(directory.FullName, "marker")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AppliedCatalogIsReadOnlyAndKeepsColdAliases()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var observations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        app.Run(async context =>
        {
            observations.Enqueue(context.Request.Method + " " + context.Request.Path);
            await context.Response.Body.WriteAsync("""
                {"Engines":[{"Name":"remote","Models":[{"Id":"cold","Aliases":["cold alias","bad\nrow"],"Profiles":[]}]}]}
                """u8.ToArray(), deadline.Token);
        });
        await app.StartAsync(deadline.Token);
        try
        {
            var result = await RunCliAsync("completion",
                ["candidates", "--current=", "chat", "--server", app.Urls.Single(), "-m"], null, deadline.Token);
            Assert.Equal(["cold", "cold alias"], Lines(result));
            Assert.Equal(["GET /admin/catalog"], observations.ToArray());
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DefaultClientUsesAppliedCatalogWhileTuneUsesDisk()
    {
        var directory = Directory.CreateTempSubdirectory("air-shell-default-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var gets = 0;
        var status = 200;
        app.Run(async context =>
        {
            Assert.Equal("GET", context.Request.Method);
            Assert.Equal("/admin/catalog", context.Request.Path);
            int generation = Interlocked.Increment(ref gets);
            context.Response.StatusCode = Volatile.Read(ref status);
            if (context.Response.StatusCode == 200)
            {
                var snapshot = $$$"""{"Engines":[{"Name":"applied-{{{generation}}}","Models":[]}]}""";
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(snapshot), deadline.Token);
            }
        });
        await app.StartAsync(deadline.Token);
        try
        {
            var config = Path.Combine(directory.FullName, "stale.toml");
            await File.WriteAllTextAsync(config, Config, deadline.Token);
            var listener = app.Urls.Single();
            async Task<string[]> Engines(bool offline) => (await Cli.ShellCompletion.CatalogAsync(null, listener, config, offline))
                .Select(engine => engine.Name).ToArray();
            Assert.Equal(["applied-1"], await Engines(false));
            Assert.Equal(["applied-2"], await Engines(false));
            Assert.Equal(["engine one", "other"], await Engines(true));
            Assert.Equal(2, Volatile.Read(ref gets));
            Volatile.Write(ref status, 503);
            Assert.Equal(["engine one", "other"], await Engines(false));
            Assert.Equal(3, Volatile.Read(ref gets));
        }
        finally
        {
            await app.StopAsync(deadline.Token);
            directory.Delete(recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task FileCandidatesPreserveHomeRelativeSpelling()
    {
        var home = Directory.CreateTempSubdirectory("air-shell-home-");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sub = home.CreateSubdirectory("nested");
            await File.WriteAllTextAsync(Path.Combine(sub.FullName, "sample input.json"), "{}", deadline.Token);
            var result = await RunCliAsync("completion", ["candidates", "--current=~/nested/sam", "tune", "compare", "--baseline"],
                null, deadline.Token, process =>
                {
                    process.Environment["HOME"] = home.FullName;
                    process.Environment["USERPROFILE"] = home.FullName;
                });
            Assert.Equal(["~/nested/sample input.json"], Lines(result));
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("bash", "complete -o")]
    [InlineData("powershell", "Register-ArgumentCompleter -Native")]
    [InlineData("zsh", "compdef _air_complete air")]
    [InlineData("fish", "complete -c air")]
    public async Task ShippedRegistrationUsesCandidateProtocol(string shell, string registration)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await RunCliAsync("completion", [shell], null, deadline.Token);
        Assert.Equal(0, result.ExitCode);
        var script = Encoding.UTF8.GetString(result.Stdout);
        Assert.Contains(registration, script, StringComparison.Ordinal);
        Assert.Contains("air completion candidates", script, StringComparison.Ordinal);
    }

    private static string[] Lines(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        return Encoding.UTF8.GetString(result.Stdout).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }
}
