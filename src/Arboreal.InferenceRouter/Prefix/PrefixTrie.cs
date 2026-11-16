using System.Security.Cryptography;

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

    /// <summary>Normalized, hashed message chain: hash(parent, utf8(message)).</summary>
    private static uint Chain(uint parent, ReadOnlySpan<byte> message)
    {
        Span<byte> hash = stackalloc byte[32];
        Span<byte> input = stackalloc byte[36];
        unchecked
        {
            input[0] = (byte)parent; input[1] = (byte)(parent >> 8);
            input[2] = (byte)(parent >> 16); input[3] = (byte)(parent >> 24);
            message.CopyTo(input[4..]);
            MD5.HashData(input[..(4 + message.Length)], hash);   // fast, quality fine for affinity
            return hash[0] | ((uint)hash[1] << 8) | ((uint)hash[2] << 16) | ((uint)hash[3] << 24);
        }
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
                var h = Chain(parent, System.Text.Encoding.UTF8.GetBytes(msg));
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
                var h = Chain(parent, System.Text.Encoding.UTF8.GetBytes(msg));
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
                var h = Chain(parent, System.Text.Encoding.UTF8.GetBytes(msg));
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
        if (text.Length > 512)
            return System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();
        Span<char> buf = stackalloc char[512];
        var len = 0;
        var wasSpace = false;
        foreach (var c in text)
        {
            var ch = (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace && wasSpace) continue;
            if (len < buf.Length) buf[len] = isSpace ? ' ' : ch;
            len++;
            wasSpace = isSpace;
        }
        return new string(buf[..len]).Trim();
    }

    /// <summary>Hashed message sequence: one entry per non-empty normalized message.</summary>
    public static List<string> NormalizeMessages(IReadOnlyList<string> messages)
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
