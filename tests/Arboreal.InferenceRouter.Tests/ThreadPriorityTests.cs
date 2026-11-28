using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

public class ThreadPriorityTests
{
    [Fact]
    public void Parses_ClassAndSub()
    {
        Assert.True(ThreadPriority.TryParse("high:200", out var p));
        Assert.Equal(ThreadPriorityClass.High, p.Class);
        Assert.Equal(200, p.Sub);

        Assert.True(ThreadPriority.TryParse("interactive", out var q));
        Assert.Equal(ThreadPriorityClass.Interactive, q.Class);
        Assert.Equal(0, q.Sub);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("high:999")]
    [InlineData("high:notanumber")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_Malformed(string text) => Assert.False(ThreadPriority.TryParse(text, out _));

    /// <summary>The accepted grammar, row by row (§6): a class name spelled exactly as the table spells it —
    /// case-sensitive, no surrounding whitespace — then optionally ':' and a sub-priority of one to three plain
    /// ASCII decimal digits, unsigned, no leading zero unless it is exactly "0". The lenient spellings are
    /// refused because this parser is also what `air threads -p` and the admin thread vocabulary go through:
    /// " high ", "high: 12", "high:+12" and "high:001" accepted there would mean callers writing bytes the
    /// router cannot echo back with ToString.</summary>
    [Theory]
    [InlineData(" high ", false, (byte)0, 0)]
    [InlineData("high: 12", false, (byte)0, 0)]
    [InlineData("high:+12", false, (byte)0, 0)]
    [InlineData("high:001", false, (byte)0, 0)]
    [InlineData("high:0", true, ThreadPriorityClass.High, 0)]
    [InlineData("high:007", false, (byte)0, 0)]
    [InlineData("high:00", false, (byte)0, 0)]                  // a leading zero however short
    [InlineData("high:000", false, (byte)0, 0)]
    [InlineData("high:255", true, ThreadPriorityClass.High, 255)]
    [InlineData("high:1234", false, (byte)0, 0)]              // four digits, and over 255: neither is a sub
    [InlineData("high:256", false, (byte)0, 0)]
    [InlineData("high:999", false, (byte)0, 0)]
    [InlineData("high:0255", false, (byte)0, 0)]            // four digits, and a leading zero: neither is a sub
    [InlineData("high:１２", false, (byte)0, 0)]             // full-width digits are not ASCII digits
    [InlineData("high:1:2", false, (byte)0, 0)]            // one separator only: the tail is what has to be the sub
    [InlineData(":1", false, (byte)0, 0)]                 // a sub with no class
    [InlineData("0", false, (byte)0, 0)]                  // a bare number is not a class
    [InlineData("hi:gh:1", false, (byte)0, 0)]
    [InlineData("high", true, ThreadPriorityClass.High, 0)]   // class alone is the whole class
    [InlineData("high:", false, (byte)0, 0)]
    [InlineData("", false, (byte)0, 0)]
    [InlineData("   ", false, (byte)0, 0)]
    [InlineData("HIGH", false, (byte)0, 0)]
    [InlineData("high:٢٥٥", false, (byte)0, 0)]   // Arabic-Indic digits: digits to char.IsDigit, not to the grammar
    public void Grammar_Is_Exact_PerSpelling(string text, bool accepted, byte expectedClass, int expectedSub)
    {
        var ok = ThreadPriority.TryParse(text, out var parsed);
        Assert.Equal(accepted, ok);
        if (!accepted) return;
        Assert.Equal(expectedClass, parsed.Class);
        Assert.Equal(expectedSub, parsed.Sub);
    }

    [Theory]
    [InlineData("background:0")]
    [InlineData("low:1")]
    [InlineData("normal:10")]
    [InlineData("high:100")]
    [InlineData("interactive:255")]
    public void EverySpelling_TheGrammarAdmits_Is_Accepted(string text)
        => Assert.True(ThreadPriority.TryParse(text, out _));

    [Fact]
    public void Orders_ClassThenSub()
    {
        var bg = ThreadPriority.Background();
        var normal = ThreadPriority.Normal();
        var high = ThreadPriority.High();
        var highSub = ThreadPriority.High(200);
        Assert.True(bg < normal);
        Assert.True(normal < high);
        Assert.True(high < highSub);
        Assert.True(ThreadPriority.High(100) < ThreadPriority.High(250));
    }

    [Fact]
    public void Equal_WhenClassAndSubMatch()
    {
        Assert.Equal(ThreadPriority.High(5), ThreadPriority.High(5));
        Assert.NotEqual(ThreadPriority.High(5), ThreadPriority.High(6));
    }

    [Fact]
    public void RoundTrips_ToString()
    {
        Assert.True(ThreadPriority.TryParse("low:42", out var p));
        Assert.True(ThreadPriority.TryParse(p.ToString(), out var p2));
        Assert.Equal(p, p2);
    }
}
