using System.Globalization;

namespace Arboreal.InferenceRouter;

/// <summary>One place for the lengths a router puts in front of a person — a feed reason, an event payload
/// field, or the sentence an API caller reads. The event ring caps <b>entries</b>, not characters, and every
/// value these paths interpolate is chosen upstream: an engine's <c>/models</c> answer, a provider's stderr,
/// or the model id in the request body. So the cap is the router's, and it is applied to the pieces before the
/// string is built rather than to the finished sentence afterwards (§5.5, §6.1, §10).</summary>
internal static class WireText
{
    /// <summary>How much of one identifier — a model id, an engine name, a profile name, a thread id — a router
    /// sentence or an event payload carries. Comfortably longer than any id in the shipped samples or the tests,
    /// and short enough that a caller who posts a four-hundred-character model id cannot choose the size of a feed
    /// record or an error body.</summary>
    internal const int IdentifierChars = 128;

    /// <summary>The ceiling for one <b>sentence</b> a person reads: a feed reason, or the message inside a caller's
    /// error body (§10, §6.1). Every piece of a reason is already capped — identifiers at
    /// <see cref="IdentifierChars"/>, a provider's reason and action line, the quoted reported-name list — so
    /// this is a sum with room to spare rather than a cut applied to working text. It is applied at the event
    /// ring's one append funnel, so the ceiling holds for <b>every</b> producer, including the request path,
    /// which builds its sentences rather than going through the engine's emitter; and where a ticket becomes
    /// terminal and in the HTTP error writer, which is what makes it true of a sentence the router did not write —
    /// an upstream client's exception text arrives already long.</summary>
    internal const int ReasonChars = 1_600;

    /// <summary>What the cut marker costs, worst case: <c>"… (+"</c>, a dropped count of up to ten digits, a
    /// space, <c>"ch #"</c>, an eight-hex-character fingerprint, and <c>")"</c>. <see cref="Bound"/> reserves
    /// this much before it cuts, so the result of a cut is never longer than the budget it was given, and a
    /// budget this size or smaller is refused rather than silently producing a head of nothing.</summary>
    internal const int MarkerReserve = 32;

    /// <summary>Cuts a string to a fixed character budget and says how much was cut. Head-only: the half of a
    /// model id or a provider reason that distinguishes it is the front, and the marker keeps the true length
    /// visible so a reader knows the value was clipped rather than complete. The cut never lands between the two
    /// <see cref="char"/>s of a surrogate pair: an identifier may carry an astral character, and a head ending in
    /// a lone high surrogate is text that cannot be encoded, printed, or compared back to anything.</summary>
    internal static string Bound(string value, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, MarkerReserve + 1);
        if (value.Length <= maxChars) return value;
        var head = maxChars - MarkerReserve;
        if (head > 0 && char.IsHighSurrogate(value[head - 1])) head--;
        return value[..head] + CutMarker(value, dropped: value.Length - head);
    }

    /// <summary>An identifier at reporting length: <see cref="IdentifierChars"/> of head plus the cut marker.</summary>
    internal static string Ident(string value) => Bound(value, IdentifierChars);

    /// <summary>Text whose <b>head</b> is the thing a reader needs — an engine's reported model name in a refusal —
    /// cut so exactly <paramref name="headChars"/> characters survive and the marker carries the true length. The
    /// budget for the caller is a head, not a total: the total is <c>headChars + MarkerReserve</c>, which is what
    /// keeps the bound statable while letting a sentence promise how much of a name an operator can act on.</summary>
    internal static string Head(string value, int headChars)
        => Bound(value, checked(headChars + MarkerReserve));

    /// <summary>The marker itself, exposed so its worst case is testable without allocating a string of two
    /// billion characters. The dropped count tells a reader how much they did not see; the fingerprint is what
    /// keeps two values that share a head <b>distinguishable in the record</b>, because a caller chooses the head
    /// and two different four-hundred-character model ids cut to the same first 96 characters. It is a
    /// distinguisher for an operator reading a feed, not an integrity check: FNV-1a over the whole value, eight
    /// hex characters, stable for a given string and meaningless as a security property.</summary>
    internal static string CutMarker(string value, int dropped)
        => "… (+" + dropped.ToString(CultureInfo.InvariantCulture) + "ch #" + Fingerprint(value) + ")";

    /// <summary>FNV-1a, 64-bit, over the UTF-16 sequence; folded to eight hex characters. The multiply wraps by
    /// definition — this project builds with checked arithmetic, and FNV-1a is defined on wrapping 64-bit
    /// arithmetic, so the wrap is the algorithm rather than an overflow to report.</summary>
    internal static string Fingerprint(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        unchecked
        {
            foreach (var c in value)
            {
                hash ^= c;
                hash *= prime;
            }
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture)[..8];
    }
}

/// <summary>The closed set of failure categories a caller can be told (§5.5). A caller gets one of these tokens
/// and nothing else from the attempt: the parameters that made the attempt fail — an engine's reported names, a
/// window comparison, a provider's sentence, a port — stay on the feed, where an operator reads them. Naming the
/// set here rather than scattering literals is what makes "one of a named finite set" checkable: the test walks
/// the exits and the set is this type.</summary>
internal static class SwitchReason
{
    /// <summary>The catalog has no select command for that model, so nothing was attempted.</summary>
    internal const string NoSelectCommand = "no_select_command";

    /// <summary>The provider refused to launch the target; its own words are on the feed.</summary>
    internal const string LaunchFailed = "launch_failed";

    /// <summary>The engine never became ready inside the configured readiness window; the window is on the feed.</summary>
    internal const string HealthTimeout = "health_timeout";

    /// <summary>The engine answered <c>/models</c> after the launch and none of its names is the requested
    /// model. What it did answer is on the feed, because the fix is the catalog key.</summary>
    internal const string RefreshOmits = "refresh_omits";

    /// <summary>The engine did not answer <c>/models</c> after the launch, so nothing confirms the model.</summary>
    internal const string RefreshUnanswered = "refresh_unanswered";

    /// <summary>The engine's reported context window is smaller than the requested profile's; both numbers are
    /// on the feed.</summary>
    internal const string WindowTooSmall = "window_too_small";

    /// <summary>The attempt threw. The exception text is provider-visible and stays on the feed.</summary>
    internal const string SwitchError = "switch_error";
}
