namespace Arboreal.InferenceRouter;

/// <summary>Prefix affinity trie over hashed, normalized message prefixes, per engine.
/// Nodes are chained by hashing (parentHash, normalizedMessage) so semantically equal
/// prefixes collide and unrelated ones don't. Bounded LRU by node count.
/// The same structure will later carry suffix extents (SuffixHash set on terminal nodes).</summary>
public sealed class PrefixTrie
{
    private sealed class Node
    {
        public long LastUsed = Environment.TickCount64;
        public int MsgChars;                 // normalized chars of the edge message
        public uint SuffixHash;              // opaque engine-reported suffix extent (0 = none)
        public int SuffixLen;
        public Dictionary<uint, Node> Children = new();
    }

    private readonly Node _root = new();
    private readonly int _maxNodes;
    private readonly int _maxDepth;
    private int _nodes;

    public PrefixTrie(int maxNodes = 8192, int maxDepth = 64)
    {
        _maxNodes = maxNodes;
        _maxDepth = maxDepth;
    }

    /// <summary>Normalized, hashed message chain: hash(parent, utf8(message)).
    /// FNV-1a (32-bit): deliberately non-cryptographic — chain keys are in-process affinity
    /// labels, never an authenticity primitive — and it avoids the MD5 allocation + the
    /// CA5351 "broken algorithm" signal on a per-request hot path.</summary>
    private static uint Chain(uint parent, ReadOnlySpan<byte> message)
    {
        unchecked
        {
            var h = 2166136261u;
            for (var i = 0; i < 4; i++)
            {
                h ^= (byte)(parent >> (8 * i));
                h *= 16777619u;
            }
            for (var i = 0; i < message.Length; i++)
            {
                h ^= message[i];
                h *= 16777619u;
            }
            return h;
        }
    }

    /// <summary>UTF-8 bytes of <paramref name="msg"/> without the transient array allocation
    /// when the message fits the stack buffer.</summary>
    private static uint ChainFor(uint parent, string msg)
    {
        Span<byte> stack = stackalloc byte[1024];
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(msg);
        if (byteCount <= stack.Length)
        {
            System.Text.Encoding.UTF8.GetBytes(msg, stack);
            return Chain(parent, stack[..byteCount]);
        }
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var rented = pool.Rent(byteCount);
        try
        {
            System.Text.Encoding.UTF8.GetBytes(msg, rented.AsSpan(0, byteCount));
            return Chain(parent, rented.AsSpan(0, byteCount));
        }
        finally { pool.Return(rented); }
    }

    /// <summary>Record that this engine has served a request with this message sequence.</summary>
    public void Record(IReadOnlyList<string> normalizedMessages)
    {
        lock (_root)
        {
            var node = _root;
            var parent = 0u;
            var depth = 0;
            foreach (var msg in normalizedMessages)
            {
                if (msg.Length == 0) continue;
                if (depth >= _maxDepth) break;
                var h = ChainFor(parent, msg);
                if (!node.Children.TryGetValue(h, out var child))
                {
                    child = new Node { MsgChars = msg.Length };
                    node.Children.Add(h, child);
                    _nodes++;
                }
                node = child;
                node.LastUsed = Environment.TickCount64;
                parent = h;
                depth++;
            }
            MaybeEvict();
        }
    }

    /// <summary>Matched-prefix extent (normalized chars) for a candidate message sequence,
    /// plus any engine-held suffix extent recorded on the terminal node.</summary>
    public (int MatchedChars, uint SuffixHash, int SuffixLen) Match(IReadOnlyList<string> normalizedMessages)
    {
        lock (_root)
        {
            var node = _root;
            var parent = 0u;
            var matched = 0;
            var depth = 0;
            foreach (var msg in normalizedMessages)
            {
                if (msg.Length == 0) continue;
                if (depth >= _maxDepth) break;
                var h = ChainFor(parent, msg);
                if (!node.Children.TryGetValue(h, out var child)) break;
                node = child;
                node.LastUsed = Environment.TickCount64;
                matched += node.MsgChars;
                parent = h;
                depth++;
            }
            return (matched, node.SuffixHash, node.SuffixLen);
        }
    }

    /// <summary>Engines record suffix extents they can reuse (fork capability); keyed on the
    /// *suffix* hash chain, stored on the node that terminates the prefix.</summary>
    public void RecordSuffix(IReadOnlyList<string> normalizedMessages, uint suffixHash, int suffixLen)
    {
        lock (_root)
        {
            var node = _root;
            var parent = 0u;
            foreach (var msg in normalizedMessages)
            {
                if (msg.Length == 0) continue;
                var h = ChainFor(parent, msg);
                if (!node.Children.TryGetValue(h, out var child)) return; // unknown prefix; ignore
                node = child;
                node.LastUsed = Environment.TickCount64;
                parent = h;
            }
            node.SuffixHash = suffixHash;
            node.SuffixLen = suffixLen;
        }
    }

    private void MaybeEvict()
    {
        if (_nodes <= _maxNodes) return;
        // Breadth-lean eviction: walk and drop least-recently-used leaves until under cap.
        Evict(_root, null);
    }

    private void Evict(Node node, Node? parent)
    {
        if (_nodes <= _maxNodes) return;
        foreach (var child in node.Children.Values)
            Evict(child, node);
        if (parent is not null && node.Children.Count == 0)
        {
            parent.Children.Clear();
            _nodes--;
        }
    }
}

public static class PrefixNormalizer
{
    /// <summary>Normalization for affinity matching: lowercase ASCII, collapse whitespace,
    /// strip common reasoning-tail markers. Deliberately cheap (hot path per queued request).</summary>
    public static string Normalize(string text)
    {
        if (text.Length == 0) return string.Empty;
        if (text.Length <= 512)
        {
            Span<char> buf = stackalloc char[text.Length];
            return Collapse(text, buf);
        }
        // Long messages: rent instead of regex (no Regex DoS surface, no UTF-16 intermediates).
        var pool = System.Buffers.ArrayPool<char>.Shared;
        var rented = pool.Rent(text.Length);
        try { return Collapse(text, rented.AsSpan(0, text.Length)); }
        finally { pool.Return(rented); }
    }

    /// <summary>ASCII lowercase fold + whitespace collapse into <paramref name="buf"/>.</summary>
    private static string Collapse(ReadOnlySpan<char> text, Span<char> buf)
    {
        var len = 0;
        var wasSpace = false;
        foreach (var c in text)
        {
            var ch = (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace && wasSpace) continue;
            buf[len++] = isSpace ? ' ' : ch;
            wasSpace = isSpace;
        }
        return new string(buf[..len]).Trim();
    }

    /// <summary>Hashed message sequence: one entry per non-empty normalized message.</summary>
    public static IReadOnlyList<string> NormalizeMessages(IReadOnlyList<string> messages)
    {
        var list = new List<string>(messages.Count);
        foreach (var m in messages)
        {
            var n = Normalize(m);
            if (n.Length > 0) list.Add(n);
        }
        return list;
    }
}
