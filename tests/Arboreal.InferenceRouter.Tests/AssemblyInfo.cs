using Xunit;

// These are integration tests: each class runs an in-process router (and several run a real HTTP
// server), and the lifetime tests wait on dispatch progress. Under parallel execution they starve
// each other's thread-pool work and time out for reasons unrelated to the code under test, so the
// suite runs one class at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
