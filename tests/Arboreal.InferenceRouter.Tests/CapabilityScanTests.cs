namespace Arboreal.InferenceRouter.Tests;

/// <summary>The refusal rule, pinned at the seam rather than through a running router: "nothing here could
/// ever serve this model" is a claim about one coherent capability state, and the tests below are the cases
/// where a plausible implementation would make it true at no instant that ever existed (§7).</summary>
public sealed class CapabilityScanTests
{
    private sealed class FakeSource : ICapabilitySource
    {
        private readonly Queue<EngineCapability> _script = new();
        private EngineCapability _last;

        public FakeSource(params EngineCapability[] snapshots)
        {
            foreach (var s in snapshots) _script.Enqueue(s);
            _last = snapshots.Length > 0 ? snapshots[^1] : EngineCapability.Initial;
        }

        /// <summary>Hand back the next scripted snapshot, then repeat the last one forever: a source that
        /// has stopped publishing is a source whose version stops moving, which is what the scan tests for.</summary>
        public EngineCapability Capability => _script.Count > 0 ? (_last = _script.Dequeue()) : _last;

        public List<string> Declared { get; } = [];

        public bool DeclaresModel(string model) => Declared.Contains(model, StringComparer.OrdinalIgnoreCase);

        public static EngineCapability Absent(long version) => new(version, true, null, ["other-model"]);
        public static EngineCapability Serving(long version, string model) => new(version, true, null, [model]);
        public static EngineCapability SwitchingToward(long version, string model) => new(version, true, model, ["other-model"]);
        public static EngineCapability Unprobed(long version) => new(version, false, null, []);
    }

    [Fact]
    public void Stable_Known_Engines_That_None_Serves_Is_Hopeless()
    {
        var engines = new FakeSource[] { new(FakeSource.Absent(3)), new(FakeSource.Absent(8)) };
        Assert.True(CapabilityScan.DefinitelyHopeless(engines, "not-here"));
    }

    [Fact]
    public void A_Publication_Mid_Scan_Voids_The_Answer_And_The_Retry_Continues_The_Wait()
    {
        // The interleaving that motivated the rule: engine B is read as absent, then B publishes that it
        // serves the model. A single-pass scan would refuse, and the model was served the whole time.
        var a = new FakeSource(FakeSource.Absent(7));
        var b = new FakeSource(FakeSource.Absent(1), FakeSource.Serving(2, "not-here"));
        Assert.False(CapabilityScan.DefinitelyHopeless(new[] { a, b }, "not-here"));
    }

    [Fact]
    public void A_Scan_That_Never_Settles_Is_Doubt_Not_A_Refusal()
    {
        // Both engines publish on every read: two passes cannot prove a single state, so the answer is
        // "wait", never "hopeless" — doubt has to be safe in this direction.
        var a = new AlwaysPublishing(5);
        var b = new AlwaysPublishing(11);
        Assert.False(CapabilityScan.DefinitelyHopeless(new ICapabilitySource[] { a, b }, "not-here"));
    }

    private sealed class AlwaysPublishing : ICapabilitySource
    {
        private long _version;
        public AlwaysPublishing(long start) => _version = start;
        public EngineCapability Capability => new(_version++, true, null, ["other-model"]);
        public bool DeclaresModel(string model) => false;
    }

    [Fact]
    public void An_Engine_That_Has_Never_Answered_The_Probe_Makes_Nothing_Hopeless()
    {
        var engines = new FakeSource[] { new(FakeSource.Absent(1)), new(FakeSource.Unprobed(2)) };
        Assert.False(CapabilityScan.DefinitelyHopeless(engines, "not-here"));
    }

    [Fact]
    public void A_Catalog_Declaration_Is_Enough_Even_When_Nothing_Is_Reported()
    {
        var a = new FakeSource(FakeSource.Absent(1));
        a.Declared.Add("Not-Here");           // case-insensitive, like the scheduler
        Assert.False(CapabilityScan.DefinitelyHopeless(new[] { a }, "not-here"));
    }

    [Fact]
    public void A_Switch_In_Flight_Toward_The_Model_Is_Not_An_Absence()
    {
        // The switch has started and the model is not reported yet; refusing here would kill the request
        // that is waiting for the very switch it triggered.
        var engines = new FakeSource[] { new(FakeSource.SwitchingToward(4, "not-here")) };
        Assert.False(CapabilityScan.DefinitelyHopeless(engines, "not-here"));
    }

    [Fact]
    public void A_Reported_Model_Outside_Any_Catalog_Is_Servable()
    {
        var engines = new FakeSource[] { new(FakeSource.Serving(4, "not-here")) };
        Assert.False(CapabilityScan.DefinitelyHopeless(engines, "not-here"));
    }

    [Fact]
    public void No_Engines_Is_Never_Hopeless()
    {
        // Vacuous truth is still a lie. Config validation forbids this; the scan does not rely on that.
        Assert.False(CapabilityScan.DefinitelyHopeless(Array.Empty<ICapabilitySource>(), "not-here"));
    }

    [Fact]
    public void A_Switch_Toward_Another_Model_Does_Not_Protect_This_One()
    {
        var engines = new FakeSource[] { new(FakeSource.SwitchingToward(4, "something-else")) };
        Assert.True(CapabilityScan.DefinitelyHopeless(engines, "not-here"));
    }
}
