using System.Text.Json;
using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>Lifecycle command plumbing: template expansion, argv quoting for the process
/// argument string, and profile-gauge parsing from a probe payload.</summary>
public class LifecycleCommandTests
{
    [Fact]
    public void Quotes_Args_For_Argv_Resplit()
    {
        Assert.Equal("a", EngineRuntime.BuildArgumentString(["a"]));
        Assert.Equal("\"a b\"", EngineRuntime.BuildArgumentString(["a b"]));
        Assert.Equal("\"\"", EngineRuntime.BuildArgumentString([""]));
        Assert.Equal("\"printf 1 > /tmp/x\"", EngineRuntime.BuildArgumentString(["printf 1 > /tmp/x"]));
        Assert.Equal("\"say \\\"hi\\\"\"", EngineRuntime.BuildArgumentString(["say \"hi\""]));
        Assert.Equal("\"back\\slash\"", EngineRuntime.BuildArgumentString(["back\\slash"]));
    }

    [Fact]
    public void Joins_Multiple_Args_With_Spaces()
        => Assert.Equal("/bin/sh -c \"select-model strata big\"",
            EngineRuntime.BuildArgumentString(["/bin/sh", "-c", "select-model strata big"]));

    [Fact]
    public async Task RoundTrips_Arguments_Through_A_Real_Shell()
    {
        // The quoting must survive .NET's argv re-split AND the shell's own parsing:
        // the marker file only appears if the redirect stayed inside the -c script.
        var marker = Path.Combine(Path.GetTempPath(), $"air-args-{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = EngineRuntime.BuildArgumentString(["-c", $"printf ok > {marker}"]),
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync();
            Assert.Equal(0, proc.ExitCode);
            Assert.Equal("ok", File.ReadAllText(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public void Gauge_Parsing_Reads_Dotted_Paths_From_Numbers_And_Strings()
    {
        using var doc = JsonDocument.Parse("""{"n_ctx": 1048576, "slots": {"active": "2"}, "missing": null}""");
        Assert.True(ProbeExpect.TryResolvePath(doc.RootElement, "n_ctx", out var nctx));
        Assert.Equal(1048576, nctx.GetInt64());
        Assert.True(ProbeExpect.TryResolvePath(doc.RootElement, "slots.active", out var slots));
        Assert.Equal("2", slots.GetString());
        Assert.False(ProbeExpect.TryResolvePath(doc.RootElement, "missing", out _));
        Assert.False(ProbeExpect.TryResolvePath(doc.RootElement, "nope.deep", out _));
    }
}
