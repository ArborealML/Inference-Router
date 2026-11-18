using System.Globalization;
using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>§10, TASK-93 AC #3–#5: the arithmetic behind the lengths the router puts in front of a person. The
/// budgets are stated in SPEC and asserted by behaviour elsewhere; what lives here is the part that only fails in a
/// corner — a cut that lands inside a surrogate pair, a marker that outgrows the reserve the cap claims, and two
/// identifiers that become the same string once cut.</summary>
public class WireTextBudgetTests
{
    [Fact]
    public void AValueWithinBudgetIsReturnedUnchanged()
    {
        var value = "qwen3-coder-32b";
        Assert.Same(value, WireText.Ident(value));
        Assert.Equal(value, WireText.Bound(value, 32 + WireText.MarkerReserve));
    }

    [Fact]
    public void ACutNeverExceedsTheBudgetItWasGiven()
    {
        // Every budget the funnel and the sentence sites can pass, plus a value far longer than any of them: the
        // point is that the result is never longer than the number the caller asked for, which is what lets a
        // documented budget be quoted as a bound.
        var value = new string('x', 5_000);
        for (var budget = WireText.MarkerReserve + 1; budget <= WireText.MarkerReserve + 60; budget++)
        {
            var cut = WireText.Bound(value, budget);
            Assert.True(cut.Length <= budget,
                $"a budget of {budget} produced {cut.Length} characters: {cut}");
            Assert.Contains("… (+", cut);
        }

        Assert.True(WireText.Ident(value).Length <= WireText.IdentifierChars,
            $"an identifier at reporting length carries {WireText.Ident(value).Length} characters");
    }

    [Fact]
    public void ACutCannotSplitASurrogatePair()
    {
        // U+1F600 is two chars in UTF-16. Placed so the head boundary falls between the high and the low surrogate,
        // a naive cut would end the head with a lone high surrogate: text that cannot be encoded, printed, or
        // compared back to the value it came from. The head has to back off by one, and the marker has to start
        // where the head stops.
        var filler = new string('a', WireText.IdentifierChars - WireText.MarkerReserve - 1);
        var value = filler + "\U0001F600" + new string('b', 200);
        var cut = WireText.Ident(value);
        var markerAt = cut.IndexOf("… (+", StringComparison.Ordinal);
        Assert.True(markerAt > 0, $"no cut marker in {cut}");
        Assert.Equal(filler.Length, markerAt);                       // one short of the pair, not on it
        Assert.False(char.IsHighSurrogate(cut[markerAt - 1]), $"the head ends in a lone high surrogate: {cut}");
        Assert.DoesNotContain("\U0001F600", cut);
        for (var i = 0; i < cut.Length; i++)
        {
            if (char.IsHighSurrogate(cut[i]))
            {
                Assert.True(i + 1 < cut.Length && char.IsLowSurrogate(cut[i + 1]),
                    $"an unpaired high surrogate at {i} in {cut}");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(cut[i]), $"an unpaired low surrogate at {i} in {cut}");
            }
        }

        // The positive control: a pair that ends before the boundary is kept whole, so the rule protects the
        // encoding rather than throwing away anything that fits.
        var whole = new string('a', WireText.IdentifierChars - WireText.MarkerReserve - 2) + "\U0001F600"
            + new string('b', 200);
        Assert.Contains("\U0001F600", WireText.Ident(whole));
    }

    [Fact]
    public void TheMarkerFitsInsideTheReserveTheCapClaims()
    {
        // The dropped count is up to ten digits for any string .NET can hold, so the worst case is measurable
        // without allocating two billion characters. `Bound` reserves MarkerReserve before cutting; if the marker
        // outgrows it, the cut result is longer than the budget and every documented bound above it is a lie.
        var worst = WireText.CutMarker(new string('x', 10), int.MaxValue);
        Assert.True(worst.Length <= WireText.MarkerReserve,
            $"the marker costs {worst.Length} characters against a reserve of {WireText.MarkerReserve}: {worst}");
        Assert.Contains(int.MaxValue.ToString(CultureInfo.InvariantCulture), worst);

        // A budget that leaves no room for a head is a caller error, not a request for an empty string.
        var refused = false;
        try { WireText.Bound(new string('x', 500), WireText.MarkerReserve); }
        catch (ArgumentOutOfRangeException) { refused = true; }
        Assert.True(refused, "a budget that leaves no room for a head was accepted");
    }

    [Fact]
    public void TwoIdentifiersThatCutToTheSameHeadStayDistinguishable()
    {
        // A caller chooses the head of their own model id, so two ids that share a prefix cut to the same head and
        // would be the same string in the feed — indistinguishable to the operator reading the record. The marker
        // fingerprints the whole value, so the record itself says which one it is.
        var shared = "org/" + new string('x', 300);
        var one = shared + "-one";
        var two = shared + "-two";
        var cutOne = WireText.Ident(one);
        var cutTwo = WireText.Ident(two);

        Assert.Equal(cutOne[..96], cutTwo[..96]);                     // same head: the prefix is all the head sees
        Assert.NotEqual(cutOne, cutTwo);
        Assert.True(cutOne.Length <= WireText.IdentifierChars, $"{cutOne.Length} characters for an identifier");
        Assert.True(cutTwo.Length <= WireText.IdentifierChars, $"{cutTwo.Length} characters for an identifier");
        Assert.Contains("… (+", cutOne);
        Assert.DoesNotContain(shared, cutOne);                        // neither carries the caller's whole text
        Assert.Equal(WireText.Fingerprint(one), WireText.Fingerprint(one));   // stable for a given value
        Assert.NotEqual(WireText.Fingerprint(one), WireText.Fingerprint(two));
    }
}
