using System.Globalization;

namespace Arboreal.InferenceRouter;

/// <summary>One place for the lengths a router puts in front of a person — a feed reason, an event payload
/// field, or the sentence an API caller reads. The event ring caps <b>entries</b>, not characters, and every
/// value these paths interpolate is chosen upstream: an engine's <c>/models</c> answer, a provider's stderr,
/// or the model id in the request body. So the cap is the router's, and it is applied to the pieces before the
/// string is built rather than to the finished sentence afterwards (§5.5, §6.1).</summary>
internal static class WireText
{
    /// <summary>How much of one identifier — a model id, an engine name, a profile name — a router sentence or
    /// an event payload carries. Comfortably longer than any id in the shipped samples or the tests, and short
    /// enough that a caller who posts a four-hundred-character model id cannot choose the size of a feed record
    /// or an error body.</summary>
    internal const int IdentifierChars = 128;

    /// <summary>The ceiling for one feed reason. Every piece of a reason is already capped — identifiers at
    /// <see cref="IdentifierChars"/>, a provider's reason and action line, the quoted reported-name list — so
    /// this is a sum with room to spare rather than a cut applied to working text. It is applied in
    /// <c>EngineRuntime.Emit</c> so the documented bound stays true if a sentence grows later.</summary>
    internal const int ReasonChars = 1_600;

    /// <summary>Cuts a string to a fixed character budget and says how much was cut. Head-only: the half of a
    /// model id or a provider reason that distinguishes it is the front, and the marker keeps the true length
    /// visible so a reader knows the value was clipped rather than complete. Every budget here is above the
    /// 24 characters the marker needs at its longest, so the result always fits.</summary>
    internal static string Bound(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        var head = Math.Max(0, maxChars - 24);
        return value[..head] + "… (+" + (value.Length - head).ToString(CultureInfo.InvariantCulture) + "ch)";
    }

    /// <summary>An identifier at reporting length: <see cref="IdentifierChars"/> of head plus the cut marker.</summary>
    internal static string Ident(string value) => Bound(value, IdentifierChars);
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
