namespace Arboreal.InferenceRouter.Lifecycle;

// The receiver shares this original monotonic origin and deadline with the caller.
internal readonly record struct CommissionReadBudget(TimeProvider Clock, long StartedAt, TimeSpan Duration)
{
    internal TimeSpan Remaining
    {
        get
        {
            var elapsed = Clock.GetElapsedTime(StartedAt);
            return elapsed < Duration ? Duration - elapsed : TimeSpan.Zero;
        }
    }
}

internal interface ICommissioningHeadReader
{
    // Read the independently provisioned commission within the supplied cutoff. Return null for unavailable authority.
    ValueTask<SupervisedLaunchCommission?> ReadCurrentAsync(string trustedSlot, CommissionReadBudget budget, CancellationToken ct);
}
