namespace Arboreal.InferenceRouter;

/// <summary>Ranks candidates already known to belong to one comparable switch group.</summary>
internal struct SwitchCostOrder<T> where T : struct
{
    private static readonly TimeSpan AgeThreshold = TimeSpan.FromSeconds(30);
    private RankedCandidate? _oldest;
    private RankedCandidate? _oldestAged;
    private RankedCandidate? _leastCost;
    private bool _missingCost;

    private readonly record struct RankedCandidate(T Value, long Sequence, TimeSpan Estimate);

    internal readonly T? Selected =>
        (_oldestAged ?? (_missingCost ? _oldest : _leastCost))?.Value;

    internal void Consider(T value, long enqueuedSeq, TimeSpan age, TimeSpan? estimate)
    {
        var next = new RankedCandidate(value, enqueuedSeq, estimate.GetValueOrDefault());
        if (_oldest is not { } oldest || enqueuedSeq < oldest.Sequence) _oldest = next;
        if (age >= AgeThreshold && (_oldestAged is not { } aged || enqueuedSeq < aged.Sequence))
            _oldestAged = next;

        if (estimate is not { } cost)
        {
            _missingCost = true;
            return;
        }
        if (_leastCost is not { } cheapest || cost < cheapest.Estimate ||
            (cost == cheapest.Estimate && enqueuedSeq < cheapest.Sequence))
            _leastCost = next;
    }
}
