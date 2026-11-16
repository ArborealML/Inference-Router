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
