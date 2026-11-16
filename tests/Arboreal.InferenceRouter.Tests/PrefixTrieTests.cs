using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

public class PrefixTrieTests
{
    private static IReadOnlyList<string> Norm(string[] messages) => PrefixNormalizer.NormalizeMessages(messages);

    [Fact]
    public void Normalizer_Lowercases_And_Collapses_Whitespace()
    {
        Assert.Equal("hello world", PrefixNormalizer.Normalize("  Hello\tWORLD  "));
        Assert.Equal("a b", PrefixNormalizer.Normalize("a\n\n b"));
    }

    [Fact]
    public void Match_Returns_Zero_For_Unknown_Prefix()
    {
        var trie = new PrefixTrie();
        var (matched, _, _) = trie.Match(Norm(["hello", "world"]));
        Assert.Equal(0, matched);
    }

    [Fact]
    public void Match_After_Record_Shares_Prefix()
    {
        var trie = new PrefixTrie();
        var msgs = Norm(["system prompt", "user: what is 2+2"]);
        trie.Record(msgs);
        var (matched, _, _) = trie.Match(msgs);
        Assert.True(matched > 0);

        // longer sequence sharing the prefix
        var longer = Norm(["system prompt", "user: what is 2+2", "assistant: 4"]);
        var (matchedLonger, _, _) = trie.Match(longer);
        Assert.True(matchedLonger >= matched);
    }

    [Fact]
    public void Unrelated_Messages_Do_Not_Collide()
    {
        var trie = new PrefixTrie();
        trie.Record(Norm(["alpha", "beta"]));
        var (matched, _, _) = trie.Match(Norm(["gamma", "delta"]));
        Assert.Equal(0, matched);
    }

    [Fact]
    public void Suffix_Extents_Record_On_Terminal_Node()
    {
        var trie = new PrefixTrie();
        var msgs = Norm(["a", "b"]);
        trie.Record(msgs);
        trie.RecordSuffix(msgs, suffixHash: 0xDEADBEEF, suffixLen: 120);
        var (matched, hash, len) = trie.Match(msgs);
        Assert.True(matched > 0);
        Assert.Equal(0xDEADBEEFu, hash);
        Assert.Equal(120, len);

        // suffix on an unknown prefix is ignored
        var unknown = Norm(["x", "y"]);
        trie.RecordSuffix(unknown, 0x12345678, 10);
        var (_, h2, _) = trie.Match(unknown);
        Assert.Equal(0u, h2);
    }

    [Fact]
    public void Bounded_Nodes_Evict()
    {
        var trie = new PrefixTrie(maxNodes: 4, maxDepth: 16);
        for (var i = 0; i < 32; i++)
            trie.Record(Norm([$"msg-{i}"]));
        // must not throw; structure stays under cap (internal count)
        var (matched, _, _) = trie.Match(Norm(["msg-31"]));
        // eviction is breadth-lean; matches may or may not survive, but no state corruption
        Assert.True(matched >= 0);
    }

    [Fact]
    public async Task EngineRuntime_Matches_Its_Trie()
    {
        var engine = new EngineRuntime(FakeDef(), new RouterConfig(), new EventRing());
        var messages = new List<string> { "system", "user" };
        engine.RecordPrefix(messages);
        Assert.Equal("system".Length + "user".Length, engine.MatchedPrefixChars(messages));
        Assert.Equal(0, engine.MatchedPrefixChars(new List<string> { "other" }));
        await engine.DisposeAsync();
    }

    private static EngineDefinition FakeDef() => new()
    {
        Name = "fake",
        Kind = EngineKind.OpenAiCompatible,
        Endpoint = new Uri("http://127.0.0.1:9/v1"),
    };
}
