using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>§5.5, TASK-94 AC #1–#3: the caller-facing half of every refusal is one name from a closed set, at
/// <b>every</b> exit rather than at a selection of them. v0.12.37 pinned seven switch exits; v0.12.42 closes the two
/// exits that answered with a sentence — a remote engine, and a switch task that never started — and stops the
/// readiness window from naming a clock when the last poll had already seen a fixed mismatch. This class is a
/// separate class from the switch-claim and capability-publication pins on purpose: those two are not re-run safely
/// while TASK-87's flake is open, so the walk that grows lives here, and only amendments to existing assertions
/// were made there.</summary>
/// <remarks>
/// The literals in <see cref="WireSet"/> are written out here rather than read from <c>SwitchReason</c>, and the
/// member names in <see cref="NamedMembers"/> likewise. If the expected names came from the production constants the
/// walk would only prove the source agrees with itself: swapping two assignments — handing a caller
/// <c>health_timeout</c> where the engine answered and named nothing — would keep every assertion green while the
/// caller was told the wrong thing and pointed at the wrong fix. Mutation control for that is exactly this swap: the
/// per-exit checks go red and the set equality stays green.
/// </remarks>
public class RefusalCategoryTests
{
    /// <summary>The set as the wire states it, and what SPEC §5.5 tells a caller to do with each name: retry the
    /// request, retry somewhere else, or fix the configuration. A name whose action is "fix the configuration" is
    /// not the same refusal as one whose action is "retry later", which is the distinction AC #2 exists for.</summary>
    /// <summary>The four things SPEC §5.5 tells a caller to do. Every name's action is exactly one of these: a
    /// name whose action is "fix the configuration" is not the refusal whose action is "retry later", which is the
    /// distinction AC #2 exists for, and the source of each is the sentence the caller reads.</summary>
    private static readonly string[] CallerActions =
    [
        "fix the configuration", "retry", "retry later", "retry elsewhere",
    ];

    private static readonly (string Name, string Action)[] WireSet =
    [
        ("no_select_command", "fix the configuration"),
        ("launch_failed", "retry later"),
        ("health_timeout", "retry later"),
        ("refresh_omits", "fix the configuration"),
        ("refresh_unanswered", "retry later"),
        ("window_too_small", "fix the configuration"),
        ("switch_error", "retry"),
        ("no_lifecycle_owner", "retry elsewhere"),
    ];

    /// <summary>The same eight names as the source spells them. <see cref="SourceAuditCoversEveryFailedExit"/>
    /// checks the engine against this list rather than against itself.</summary>
    private static readonly string[] NamedMembers =
    [
        "NoSelectCommand", "LaunchFailed", "HealthTimeout", "RefreshOmits",
        "RefreshUnanswered", "WindowTooSmall", "SwitchError", "NoLifecycleOwner",
    ];

    /// <summary>One constructed exit per name, and the caller's half of each is the literal it has to be.</summary>
    [Fact(Timeout = 180_000)]
    public async Task EveryRefusalExitAnswersTheCallerWithANameFromTheSet()
    {
        var seen = new List<string>();
        var readinessWindowExits = new List<string>();

        // 1. No catalog entry for the model: the catalog was consulted and nothing was attempted.
        {
            using var handler = new EngineAnswers("m");
            await using var engine = Engine(Def(), handler, new Scripted { ProveHealth = false });
            var ghost = await engine.WaitForModelAsync("ghost", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(ghost.IsReady);
            seen.Add(Expect("no_select_command", ghost));
        }

        // 2. The provider refused the launch. Its own sentence names a socket path: that is the operator's half.
        {
            using var handler = new EngineAnswers("m");
            var provider = new Scripted
            {
                ProveHealth = false,
                LaunchOutcome = LifecycleResult.Fail("selector exited 1", "connect refused: /run/engine/sockets/engine.sock"),
            };
            var feed = new EventRing();
            await using var engine = Engine(Def(), handler, provider, feed);
            var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("launch_failed", failed));
            Assert.DoesNotContain("engine.sock", failed.Detail);
            Assert.Contains("engine.sock", feed.Replay(0).Last(e => e.Kind == "engine.switch_failed").Reason);
        }

        // 3. The readiness window expired on an engine that never said it was healthy: a wait, not a mismatch.
        {
            using var handler = new EngineAnswers("m") { PropsStatus = 503 };   // never healthy
            var feed = new EventRing();
            var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
            await using var engine = Engine(def, handler, new Scripted { ReadinessMs = 300 }, feed);
            var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("health_timeout", failed));
            readinessWindowExits.Add(failed.Detail!);
            var shown = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
            Assert.Contains("health_timeout after 300ms", shown.Reason);       // the window stays where it is read
            Assert.DoesNotContain("300", failed.Detail);
        }

        // 4. The window expired on an engine that stayed up and never named the target: the catalog key is wrong,
        //    and that is not a timeout. v0.12.42's fix; before it this exit answered like block 3.
        {
            using var handler = new EngineAnswers("m");                        // healthy, never names "extra"
            var feed = new EventRing();
            var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
            await using var engine = Engine(def, handler, new Scripted { ReadinessMs = 300 }, feed);
            var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("refresh_omits", failed));
            readinessWindowExits.Add(failed.Detail!);
            var shown = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
            Assert.Contains("refresh_omits_extra", shown.Reason);
            Assert.Contains("health_timeout after 300ms", shown.Reason);       // the operator keeps both halves
            Assert.DoesNotContain("300", failed.Detail);
        }

        // 5. The window expired on an engine serving the model in too small a window: a ladder fix, not a wait.
        {
            using var handler = new EngineAnswers("m");                        // healthy, names the target
            var feed = new EventRing();
            var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
            await using var engine = Engine(def, handler, new Scripted { ReadinessMs = 300 }, feed);
            var failed = await engine.WaitForProfileAsync(
                "m", new ServingProfile { Name = "huge", MaxContext = 16_384 },
                TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("window_too_small", failed));
            readinessWindowExits.Add(failed.Detail!);
            var shown = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
            Assert.Contains("window_8192_lt_16384", shown.Reason);
            Assert.DoesNotContain("16384", failed.Detail);
            Assert.DoesNotContain("8192", failed.Detail);
        }

        // The three ways the readiness window can expire are three different refusals. Under the clock alone they
        // were one name, and the caller could not tell a fixed mismatch from a wait that may answer next time.
        Assert.Equal(3, readinessWindowExits.Distinct(StringComparer.Ordinal).Count());

        // 6. wait_health = false: the engine answered the post-launch refresh and named something else.
        {
            using var handler = new EngineAnswers("m", "stranger");
            var feed = new EventRing();
            await using var engine = Engine(Def(), handler, new Scripted { ProveHealth = false }, feed);
            var failed = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("refresh_omits", failed));
            Assert.DoesNotContain("stranger", failed.Detail);                   // the engine's words are config-side
            Assert.Contains("refresh_omits_second", feed.Replay(0).Last(e => e.Kind == "engine.switch_failed").Reason);
        }

        // 7. wait_health = false: the engine would not answer at all after the launch.
        {
            using var handler = new EngineAnswers("m") { ModelsStatus = 503 };
            await using var engine = Engine(Def(), handler, new Scripted { ProveHealth = false });
            var failed = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("refresh_unanswered", failed));
        }

        // 8. wait_health = false, and the window is the thing that does not fit: both numbers stay on the feed.
        {
            using var handler = new EngineAnswers("m");
            var feed = new EventRing();
            var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 5_000 });
            await using var engine = Engine(def, handler, new Scripted { ProveHealth = false }, feed);
            using var loopCts = new CancellationTokenSource();
            engine.StartHealthLoop(loopCts.Token);
            await WaitUntil(() => engine.ActiveContextWindow == 8_192);        // the gauge is the router's evidence
            var failed = await engine.WaitForProfileAsync(
                "m", new ServingProfile { Name = "huge", MaxContext = 16_384 },
                TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("window_too_small", failed));
            var shown = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
            Assert.Contains("window_8192_lt_16384", shown.Reason);
        }

        // 9. The attempt threw: the provider's sentence never travels to the caller.
        {
            using var handler = new EngineAnswers("m");
            var provider = new Scripted
            {
                ProveHealth = false,
                ThrowOnLaunch = "connect refused: /run/engine/sockets/engine.sock",
            };
            var feed = new EventRing();
            await using var engine = Engine(Def(), handler, provider, feed);
            var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("switch_error", failed));
            Assert.DoesNotContain("engine.sock", failed.Detail);
            Assert.Contains("engine.sock", feed.Replay(0).Last(e => e.Kind == "engine.switch_failed").Reason);
        }

        // 10. A remote engine: the router owns no loading authority for it, so the refusal is a name rather than a
        //     sentence about how a remote engine loads its own models. The name is the relation, not the config
        //     flag, so the reach/ownership split (TASK-77) will not have to rename it.
        {
            using var handler = new EngineAnswers("m");
            await using var engine = Engine(Def(remote: true), handler, new Scripted { ProveHealth = false });
            var failed = await engine.WaitForModelAsync("m", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("no_lifecycle_owner", failed));
            Assert.Equal("no_lifecycle_owner", failed.Detail);                  // the name, and nothing but the name
        }

        // 11. The switch task never started. The caller used to be told *which* switch could not start — another
        //     request's target inside this caller's error body — and now gets the catch-all name, because the
        //     action is the same as for an attempt that threw. The phase, which is what an operator needs, is on
        //     the feed (pinned in TheNeverStartedAttemptIsOnTheFeedBeforeItsWaiterWakes).
        {
            using var handler = new EngineAnswers("m");
            var feed = new EventRing();
            var engine = Engine(Def(), handler, new Scripted { ProveHealth = false }, feed);
            await using var _ = engine;
            engine.BeforeScheduleSwitchForTests = () => throw new InvalidOperationException("the pool refused the work");
            var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            seen.Add(Expect("switch_error", failed));
            Assert.DoesNotContain("extra", failed.Detail);
            Assert.Null(engine.SwitchingToward);                                // put back, not left switching
        }

        // The walk is the assertion: eleven exits, eight names, no exit answering with prose and no name left
        // unreachable by the exits.
        Assert.Equal(11, seen.Count);
        Assert.Equal(WireSet.Length, seen.Distinct(StringComparer.Ordinal).Count());
        foreach (var (name, _) in WireSet)
            Assert.Contains(name, seen);

        // …and the set the source declares is this set, in both directions: a name added without an exit, or an
        // exit answering a name this test does not know, fails here.
        Assert.Equal(
            WireSet.Select(entry => entry.Name).OrderBy(n => n, StringComparer.Ordinal),
            SwitchReason.All.OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(WireSet, entry => Assert.Contains(entry.Action, CallerActions));
        // The pair a caller acts on: a fixed mismatch points at the configuration and an expired window points at
        // patience. Swapping the two tokens between exits leaves this green and the per-exit walk above red.
        Assert.Equal("fix the configuration", WireSet.Single(e => e.Name == "refresh_omits").Action);
        Assert.Equal("retry later", WireSet.Single(e => e.Name == "health_timeout").Action);
    }

    /// <summary>The phase-only half of AC #2, kept where a caller reads it: the same token from two different paths
    /// means the same fix, and the measurement that distinguishes the two paths stays on the feed.</summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSameNameFromTwoPathsCarriesTheSameFix()
    {
        using var handler = new EngineAnswers("m");
        var feed = new EventRing();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        await using var engine = Engine(def, handler, new Scripted { ReadinessMs = 300 }, feed);
        var viaWindow = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal("refresh_omits", Expect("refresh_omits", viaWindow));

        using var answered = new EngineAnswers("m", "stranger");
        await using var relaunched = Engine(Def(), answered, new Scripted { ProveHealth = false });
        var viaRefresh = await relaunched.WaitForModelAsync("second", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal("refresh_omits", Expect("refresh_omits", viaRefresh));

        // Neither caller is quoted the engine's answer, and neither is quoted the window they were made to wait.
        Assert.DoesNotContain("stranger", viaRefresh.Detail);
        Assert.DoesNotContain("300", viaWindow.Detail);
    }

    /// <summary>An attempt that ended before the provider was asked anything used to be invisible to an operator,
    /// and its feed record has to exist before the waiter wakes: the ordering rule on <c>EndSwitch</c> is what
    /// keeps a failed request and its explanation in the same instant. The subscriber runs inside the ring's
    /// append, so it sees the engine exactly as the emitter left it — while the switch is still marked — and it
    /// records rather than asserts, because an observer that throws reaches the emitter (its isolation is a change
    /// of its own).</summary>
    [Fact(Timeout = 120_000)]
    public async Task TheNeverStartedAttemptIsOnTheFeedBeforeItsWaiterWakes()
    {
        using var handler = new EngineAnswers("m");
        var feed = new EventRing();
        var engine = Engine(Def(), handler, new Scripted { ProveHealth = false }, feed);
        await using var _ = engine;

        var recorded = new List<(string Kind, bool Switching)>();
        feed.Subscribe(e => recorded.Add((e.Kind, engine.Switching)));

        engine.BeforeScheduleSwitchForTests = () => throw new InvalidOperationException("the pool refused the work");
        var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(failed.IsReady);
        Assert.Equal("switch_error", failed.Detail);
        var shown = recorded.Where(r => r.Kind == "engine.switch_failed").ToList();
        Assert.Single(shown);
        Assert.True(shown[0].Switching,
            "the record has to be written while the switch is still marked; an Emit after EndSwitch lets a waiter " +
            "wake onto a refusal with no explanation on the feed yet");
        var onFeed = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
        Assert.Contains("switch_not_started", onFeed.Reason);
        Assert.DoesNotContain("extra", failed.Detail);
        Assert.Null(engine.SwitchingToward);
    }

    /// <summary>A wait that ran out is not an attempt that ended, so it carries no name from the closed set: the
    /// set says why an attempt stopped, and this attempt is still running. What the waiter is quoted is its own
    /// ceiling and the engine's state, never the target of the switch it was parked behind (§5.5).</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnExpiredWaitIsNotAnAttemptAndCarriesNoName()
    {
        using var handler = new EngineAnswers("m");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Scripted { ProveHealth = false, LaunchGate = gate };
        await using var engine = Engine(Def(), handler, provider);
        engine.AfterClaimForTests = () => claimed.TrySetResult();

        var first = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        await claimed.Task;

        var parked = await engine.WaitForModelAsync("m", TimeSpan.FromMilliseconds(700), CancellationToken.None);
        Assert.Equal(EngineRuntime.SwitchOutcome.TimedOut, parked.Outcome);
        Assert.NotNull(parked.Detail);
        Assert.Contains("700 ms", parked.Detail);                    // their own ceiling, in their own sentence
        Assert.Contains("another request", parked.Detail);            // the engine's state, never a target
        Assert.DoesNotContain("extra", parked.Detail);
        foreach (var (name, _) in WireSet)
            Assert.DoesNotContain(name, parked.Detail);               // no name from the set: nothing ended

        gate.TrySetResult();
        await first;
    }

    /// <summary>The door: a prose category cannot be built into a refusal, so a tenth exit that invents a name fails
    /// at the construction site instead of putting a sentence on the wire that no caller can act on.</summary>
    [Fact]
    public void AProseCategoryCannotBeBuiltIntoARefusal()
    {
        foreach (var name in SwitchReason.All)
            Assert.Equal(name, EngineRuntime.SwitchResult.Failed(name).Detail);

        var ex = Assert.Throws<ArgumentException>(
            () => EngineRuntime.SwitchResult.Failed("still switching after 30000 ms"));
        Assert.Contains("not one of the named categories", ex.Message);
        Assert.Contains("still switching", ex.Message);           // the developer sees what was wrong

        // …and the recorded-reason arm keeps the name it was given, falling back to the catch-all only when the
        // attempt recorded nothing at all.
        Assert.Equal("launch_failed", EngineRuntime.SwitchResult.FailedFromRecord("launch_failed").Detail);
        Assert.Equal("switch_error", EngineRuntime.SwitchResult.FailedFromRecord(null).Detail);
        Assert.Equal("switch_error", EngineRuntime.SwitchResult.FailedFromRecord("").Detail);
        var recorded = Assert.Throws<ArgumentException>(
            () => EngineRuntime.SwitchResult.FailedFromRecord("the engine on 127.0.0.1:8080 said no"));
        Assert.Contains("not one of the named categories", recorded.Message);
    }

    /// <summary>The audit AC #1 asks for: every failed switch answer in the engine leaves through the door with a
    /// named member, every name the source mentions is one of the eight, and every one of the eight is produced by
    /// an exit. A tenth exit answering with prose, or a name added with nothing to produce it, fails here. The
    /// source is read as text because the audit is about the shape of the file: it is the check that a future
    /// <c>return</c> nobody walked cannot pass silently. Two doors, and the shape of each call is checked: a caller
    /// is told either a member named at the call site, or exactly what the attempt recorded, and the fallback to the
    /// catch-all exists in one arm of one expression.</summary>
    [Fact]
    public void SourceAuditCoversEveryFailedExit()
    {
        var path = RepoFile("src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs");
        Assert.NotNull(path);
        var source = File.ReadAllText(path!);

        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("state.FailureReason =", StringComparison.Ordinal))
                Assert.Contains("SwitchReason.", line);
            if (line.Contains("SwitchResult.Failed(", StringComparison.Ordinal) &&
                !line.StartsWith("///", StringComparison.Ordinal))
                Assert.Matches(@"SwitchResult\.Failed\(SwitchReason\.[A-Za-z]+\)", line);
            if (line.Contains("SwitchResult.FailedFromRecord(", StringComparison.Ordinal) &&
                !line.StartsWith("///", StringComparison.Ordinal))
                Assert.Matches(@"SwitchResult\.FailedFromRecord\(state\.FailureReason\)", line);
        }

        // The fallback is not something a new exit may invent at its own call site: it lives in the recorded-reason
        // door, in one line, and that line names the catch-all member rather than a sentence.
        var fallback = Regex.Count(source, @"^\s*Failed\(recorded", RegexOptions.Multiline);
        Assert.Equal(1, fallback);
        Assert.All(
            source.Split('\n').Where(l => l.Contains("Failed(recorded is", StringComparison.Ordinal)),
            l => Assert.Contains("SwitchReason.SwitchError", l));

        // …and the door is the only route: a Failed outcome built anywhere else in the engine sidesteps the set
        // entirely, so exactly one line in the file constructs one and it is the door's own line. This is the check
        // that a bypass fails, not merely a wrong name: the mutation that puts the old sentence back at the resume
        // exit — building the result directly — reddens here and in SwitchClaimTests, and nothing else.
        var constructed = source.Split('\n')
            .Where(l => l.Contains("SwitchOutcome.Failed", StringComparison.Ordinal) &&
                        !l.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(constructed);
        Assert.Matches(@"return new SwitchResult\(SwitchOutcome\.Failed, category\);", constructed[0]);

        var mentioned = Regex.Matches(source, @"SwitchReason\.([A-Za-z]+)")
            .Select(m => m.Groups[1].Value)
            // `SwitchReason.All` is the set itself, the array this audit compares the source against, not a name a
            // caller can be told. It is the one member of the type that is not a member of the wire set.
            .Where(name => name != "All")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.All(mentioned, name => Assert.Contains(name, NamedMembers));
        foreach (var member in NamedMembers)
            Assert.Contains(member, mentioned);

        // The members this test names are the set the source declares, and the set is the wire set.
        Assert.Equal(NamedMembers.Length, SwitchReason.All.Length);
        Assert.Equal(NamedMembers.Length, WireSet.Length);
    }

    /// <summary>A catalog entry that names no selector is a fixed configuration gap. Asked of the provider it comes
    /// back as a preflight refusal, the caller is told <c>launch_failed</c>, and the engine's last proof is thrown
    /// away for a transaction that never began. The runtime answers it before the transaction, with the name the
    /// caller can act on, and leaves the capability alone (§5.5).</summary>
    [Fact(Timeout = 120_000)]
    public async Task ACatalogEntryWithoutASelectorRefusesWithANameAndKeepsItsProof()
    {
        using var handler = new EngineAnswers("m");
        var feed = new EventRing();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 5_000 });
        await using var engine = Engine(def, handler, lifecycle: null, feed: feed);   // the provider config picks
        Assert.Equal("exec", engine.LifecycleProvider);

        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => engine.Capability.Healthy);
        Assert.True(engine.Capability.Healthy);                   // the pin is not vacuous: there is a proof to keep

        // The model is in the catalog, the engine never reports it, and nothing names a selector for it: the
        // transaction would be refused by the provider, one step and one capability publication late.
        var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(failed.IsReady);
        Assert.Equal("no_select_command", Expect("no_select_command", failed));
        Assert.Contains("no usable select exec",
            feed.Replay(0).Last(e => e.Kind == "engine.switch_failed").Reason);
        Assert.True(engine.Capability.Healthy);                   // refusing a refusal costs the engine nothing
        Assert.Null(engine.SwitchingToward);
    }

    // ── harness ───────────────────────────────────────────────────────────────

    /// <summary>The caller's name is the tail of the sentence the request path builds around it, so the message is
    /// their own identifiers plus this and nothing else. Asserting against a literal written in this file — rather
    /// than against a production constant — is what makes the walk evidence.</summary>
    private static string Expect(string literal, EngineRuntime.SwitchResult result)
    {
        Assert.False(result.IsReady);
        Assert.NotNull(result.Detail);
        var cut = result.Detail!.LastIndexOf(": ", StringComparison.Ordinal);
        var observed = cut < 0 ? result.Detail! : result.Detail![(cut + 2)..];
        Assert.Equal(literal, observed);
        return observed;
    }

    private static EngineRuntime Engine(
        EngineDefinition def, HttpMessageHandler handler, IEngineLifecycleProvider? lifecycle, EventRing? feed = null)
        => new(def, new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { ["fake"] = def } },
            feed ?? new EventRing(), handler, lifecycle: lifecycle);

    private static EngineDefinition Def(HealthProbeDefinition? health = null, bool remote = false) => new()
    {
        Name = "fake",
        Kind = EngineKind.OpenAiCompatible,
        Endpoint = new Uri("http://127.0.0.1:9/v1"),
        Health = health,
        Remote = remote,
        Catalog = new Dictionary<string, EngineModelDefinition>
        {
            ["m"] = new() { Name = "m" },
            ["extra"] = new() { Name = "extra" },
            ["second"] = new() { Name = "second" },
        },
    };

    private static string? RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new TimeoutException("condition not met within " + timeoutMs + " ms");
    }

    /// <summary>An engine that answers <c>/models</c> with a chosen list and <c>/props</c> with a window of 8192,
    /// either of which can be made unable to answer.</summary>
    private sealed class EngineAnswers : HttpMessageHandler
    {
        private readonly string[] _models;
        public int ModelsStatus { get; set; } = 200;
        public int PropsStatus { get; set; } = 200;

        public EngineAnswers(params string[] models) => _models = models;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            if (path == "models" || path.EndsWith("/models", StringComparison.Ordinal))
                return Task.FromResult(path == "models" && ModelsStatus != 200
                    ? Status(ModelsStatus)
                    : Json("{\"object\":\"list\",\"data\":[" + string.Join(",", _models.Select(
                        m => "{\"id\":\"" + m + "\",\"status\":{\"value\":\"loaded\"}}")) + "]}"));
            if (path == "props" || path.EndsWith("/props", StringComparison.Ordinal))
                return Task.FromResult(PropsStatus != 200 ? Status(PropsStatus) : Json("""{"total_slots": 4, "n_ctx": 8192}"""));
            return Task.FromResult(Status(404));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Status(int code) =>
            new((HttpStatusCode)code) { Content = new StringContent("{}") };
    }

    /// <summary>The lifecycle seam: launch succeeds, fails, throws, or waits to be released, and the readiness rule
    /// says whether health has to be proved and for how long.</summary>
    private sealed class Scripted : IEngineLifecycleProvider
    {
        public string Kind => "scripted";
        public LifecycleResult LaunchOutcome { get; set; } = LifecycleResult.Ok();
        public bool ProveHealth { get; set; } = true;
        public int ReadinessMs { get; set; } = 10_000;
        public string? ThrowOnLaunch { get; set; }
        public TaskCompletionSource? LaunchGate { get; set; }

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("fake"));

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            if (ThrowOnLaunch is { } boom) throw new InvalidOperationException(boom);
            if (LaunchGate is { } gate) await gate.Task.ConfigureAwait(false);
            return LaunchOutcome;
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) =>
            new(ProveHealth, TimeSpan.FromMilliseconds(ReadinessMs));
    }
}
