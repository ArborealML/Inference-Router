using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>§5.5, TASK-94 AC #14, decision-51: what a caller does when it arrives at an engine that is
/// <b>already switching</b>. The catalog guard used to sit ahead of the claim loop, so configuration state that
/// never governed the running switch answered a caller that had come to join it — and a refusal is terminal, so
/// that caller never learned what the switch decided. These pins hold the order: join first, and the catalog is
/// asked only of a caller about to claim.
/// They live in their own class on purpose: every one of them parks a lifecycle launch and runs two callers
/// against one engine, which is the shape TASK-87 is measuring elsewhere, and the standing rule while that row is
/// open is no new pins in the switch-claim or capability-publication classes.</summary>
public sealed class SwitchJoinTests
{
    /// <summary>A joiner whose catalog entry vanishes mid-switch. The switch keeps the entry it was authorised on
    /// (decision-49), and the caller that arrives afterwards is answered by that switch, not by the catalog it can
    /// no longer read: it waits, it takes the joined outcome, and the engine loads the model <b>once</b>.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AJoinerWhoseEntryVanishedMidSwitchIsAnsweredByTheSwitchItJoined()
    {
        using var reported = new FakeEngineHandler { LoadedModels = ["m"] };   // "extra" is not served yet
        var provider = new GatedExec();
        var feed = new EventRing();
        var engines = new Dictionary<string, EngineDefinition> { ["fake"] = Def(withExtra: true) };
        await using var engine = new EngineRuntime(engines["fake"], new RouterConfig { Engines = engines }, feed, reported, lifecycle: provider);

        var claims = 0;
        engine.AfterClaimForTests = () => claims++;                       // fires only at an actual claim

        var a = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);                   // claimed, and parked inside the launch

        // The configuration the next caller would read can no longer select the target at all.
        engines["fake"] = Def(withExtra: false);

        var b = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(500);
        Assert.False(b.IsCompleted);                    // red before the fix: it answered at the catalog guard

        reported.LoadedModels = ["m", "extra"];         // what the parked load will turn out to have done
        provider.ReleaseAll(LifecycleResult.Ok());
        var first = await a;
        var joined = await b;

        Assert.True(first.IsReady);
        Assert.True(joined.IsReady);                    // the joined outcome, not a refusal about the config
        Assert.Single(provider.Launches);                 // one transaction for two callers
        Assert.Equal(1, claims);                        // one claim witness: the joiner never claimed
        Assert.Equal(1, feed.Replay(0).Count(e => e.Kind == "engine.switch_started"));
    }

    /// <summary>The same join, and the switch fails. The joiner is told what the switch it joined <b>did</b> —
    /// <c>launch_failed</c>, "retry later" — rather than <c>no_select_command</c>, "fix the configuration", which
    /// is a different caller action and was the answer the guard ahead of the loop gave for a transaction that was
    /// already running when the caller arrived.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AJoinerIsToldWhatTheFailedSwitchDidNotTheCatalogItCannotRead()
    {
        using var reported = new FakeEngineHandler { LoadedModels = ["m"] };
        var provider = new GatedExec();
        var feed = new EventRing();
        var engines = new Dictionary<string, EngineDefinition> { ["fake"] = Def(withExtra: true) };
        await using var engine = new EngineRuntime(engines["fake"], new RouterConfig { Engines = engines }, feed, reported, lifecycle: provider);

        var claims = 0;
        engine.AfterClaimForTests = () => claims++;

        var a = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);
        engines["fake"] = Def(withExtra: false);

        var b = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(500);
        Assert.False(b.IsCompleted);                    // it joined; it was not refused on the way in

        provider.ReleaseAll(LifecycleResult.Fail("selector exited 1", "the exec said no"));
        var first = await a;
        var joined = await b;

        Assert.False(first.IsReady);
        Assert.Equal("launch_failed", first.Detail);    // the attempt's own recorded name
        Assert.False(joined.IsReady);
        Assert.Equal("launch_failed", joined.Detail);   // and the joiner is answered by that attempt
        Assert.NotEqual("no_select_command", joined.Detail);
        Assert.Single(provider.Launches);
        Assert.Equal(1, claims);                        // the joiner never claimed, so no state write, no marker
        Assert.Equal(1, feed.Replay(0).Count(e => e.Kind == "engine.switch_started"));
    }

    /// <summary>Single-flight is per engine, not per target (decision-9), so a caller can join a switch aimed at
    /// somebody else's model. When that switch fails, the caller takes its own turn through the claim path — and
    /// <b>there</b> the catalog is asked of it, and refuses it. That refusal is correct and is asserted as correct
    /// rather than special-cased away: what changed is only that it now comes after the join, with the other
    /// caller's switch already on the feed.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AJoinerThatBecomesTheAcquirerIsRefusedByItsOwnAbsentEntry()
    {
        using var reported = new FakeEngineHandler { LoadedModels = ["m"] };
        var provider = new GatedExec();
        var feed = new EventRing();
        var engines = new Dictionary<string, EngineDefinition> { ["fake"] = Def(withExtra: false) };
        await using var engine = new EngineRuntime(engines["fake"], new RouterConfig { Engines = engines }, feed, reported, lifecycle: provider);

        var claims = 0;
        engine.AfterClaimForTests = () => claims++;

        // A can claim its target. B must still join A before its own absent target is refused.
        var a = engine.WaitForModelAsync("second", TimeSpan.FromSeconds(20), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);

        var b = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(500);
        Assert.False(b.IsCompleted);                    // parked behind A's switch, as single-flight requires

        provider.ReleaseAll(LifecycleResult.Fail("selector exited 1", "the exec said no"));
        var first = await a;
        var after = await b;

        Assert.Equal("launch_failed", first.Detail);
        Assert.Equal("no_select_command", after.Detail);   // its own attempt, its own absent entry
        Assert.Single(provider.Launches);
        Assert.Equal(1, claims);                        // the refusal wrote no state: no claim, no marker
        Assert.Equal(1, feed.Replay(0).Count(e => e.Kind == "engine.switch_started"));
        Assert.Equal(["second"], feed.Replay(0).Where(e => e.Kind == "engine.switch_started").Select(e => e.Model!).ToArray());
        Assert.Null(engine.SwitchingToward);            // and no marker was left up by the refused attempt
    }

    /// <summary>A budget that expires while the caller is <b>joined</b> is a timeout, not a refusal. The guard
    /// cannot turn one into the other: the deadline is set once, before the first lock wait, and the catalog is
    /// never asked of a caller that has not been refused its own claim. The switch the caller was parked behind is
    /// still running when it hangs up — giving up is not a reason to stop somebody else's transaction.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AJoinerOutOfItsOwnCeilingIsTimedOutNotRefused()
    {
        using var reported = new FakeEngineHandler { LoadedModels = ["m"] };
        var provider = new GatedExec();
        var feed = new EventRing();
        var engines = new Dictionary<string, EngineDefinition> { ["fake"] = Def(withExtra: false) };
        await using var engine = new EngineRuntime(engines["fake"], new RouterConfig { Engines = engines }, feed, reported, lifecycle: provider);

        var claims = 0;
        engine.AfterClaimForTests = () => claims++;

        var a = engine.WaitForModelAsync("second", TimeSpan.FromSeconds(20), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);

        var joined = await engine.WaitForModelAsync("extra", TimeSpan.FromMilliseconds(400), CancellationToken.None);
        Assert.False(joined.IsReady);
        Assert.NotNull(joined.Detail);
        Assert.Contains("still switching after", joined.Detail);
        Assert.DoesNotContain("no_select_command", joined.Detail);
        Assert.Equal("second", engine.SwitchingToward);          // the switch it left behind is still running
        Assert.DoesNotContain("engine.switch_failed", feed.Replay(0).Select(e => e.Kind));   // no refusal was recorded
        Assert.Equal(1, claims);                                // the joiner never claimed

        reported.LoadedModels = ["m", "second"];
        provider.ReleaseAll(LifecycleResult.Ok());
        var first = await a;
        Assert.True(first.IsReady);
    }

    // ── harness ───────────────────────────────────────────────────────────────

    private static EngineDefinition Def(bool withExtra)
    {
        var catalog = new Dictionary<string, EngineModelDefinition>
        {
            ["m"] = new() { Name = "m" },
            ["second"] = new() { Name = "second", Select = new LifecycleCommand { Exec = "/bin/true" } },
        };
        if (withExtra) catalog["extra"] = new() { Name = "extra", Select = new LifecycleCommand { Exec = "/bin/true" } };
        return new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Catalog = catalog,
        };
    }

    /// <summary>The lifecycle seam: every launch parks on a gate the test releases with the outcome it wants, so
    /// "the switch is still running" is a fact the pin controls rather than a race it hopes to win. The identity is
    /// the delegated exec, which is the one that puts the selector predicate in force.</summary>
    private sealed class GatedExec : IEngineLifecycleProvider
    {
        private readonly Queue<TaskCompletionSource<LifecycleResult>> _gates = new();
        public List<string> Launches { get; } = [];
        public string Kind => "exec";

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("fake"));

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Launches.Add(target.Model);
            TaskCompletionSource<LifecycleResult> gate;
            lock (_gates)
            {
                gate = new TaskCompletionSource<LifecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Enqueue(gate);
            }

            return await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public void ReleaseAll(LifecycleResult outcome)
        {
            TaskCompletionSource<LifecycleResult>[] all;
            lock (_gates)
            {
                all = [.. _gates];
                _gates.Clear();
            }

            foreach (var gate in all) gate.TrySetResult(outcome);
        }

        /// <summary>Launches parked and not yet released — the condition to wait on before releasing, because a
        /// launch is recorded a statement before its gate is queued.</summary>
        public int GateCount
        {
            get { lock (_gates) return _gates.Count; }
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(1));
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
}
