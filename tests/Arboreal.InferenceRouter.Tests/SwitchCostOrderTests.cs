namespace Arboreal.InferenceRouter.Tests;

public sealed class SwitchCostOrderTests
{
    [Theory]
    [InlineData(7, 12, 4)]
    [InlineData(12, 7, 5)]
    [InlineData(7, 7, 4)]
    public void CompleteEstimatesSelectLowerCostThenEnqueueSequence(int aCost, int bCost, int winner)
    {
        var order = new SwitchCostOrder<int>();
        order.Consider(5, 12, TimeSpan.Zero, TimeSpan.FromMilliseconds(bCost));
        order.Consider(4, 10, TimeSpan.Zero, TimeSpan.FromMilliseconds(aCost));

        Assert.Equal(winner, order.Selected);
    }

    [Fact]
    public void OneMissingEstimateRestoresEnqueueOrderForTheWholeGroup()
    {
        var order = new SwitchCostOrder<int>();
        order.Consider(0, 12, TimeSpan.Zero, null);
        order.Consider(1, 10, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        order.Consider(2, 11, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, order.Selected);
    }

    [Fact]
    public void AtThirtySecondsTheOldestAgedCandidateWinsOverEveryCost()
    {
        var threshold = TimeSpan.FromSeconds(30);
        var order = new SwitchCostOrder<int>();
        order.Consider(0, 12, threshold - TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(1));
        order.Consider(1, 11, threshold, TimeSpan.FromSeconds(6));
        order.Consider(2, 10, threshold, TimeSpan.FromSeconds(9));

        Assert.Equal(2, order.Selected);
    }

    [Fact]
    public void JustBelowThirtySecondsCostCanStillChangeTheWinner()
    {
        var order = new SwitchCostOrder<int>();
        order.Consider(0, 10, TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1), TimeSpan.FromSeconds(9));
        order.Consider(1, 11, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, order.Selected);
    }
}
