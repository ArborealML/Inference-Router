using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record HostStagedPublication(
    Guid ClaimId, Func<HostClosedRouteStage, bool> TryPublishState, Action<HostClosedRouteStage> Complete,
    Action? AfterStageInstalledForTests = null, Action? AfterStateAcceptedForTests = null);

internal sealed record HostClosedRouteStage(int Version, Guid ClaimId, HostCheckpointLaunchEffect Launch, VerifiedInstanceRoute Route);

internal enum HostStagedVerdict { Refused, Expired, Stopped, Committed }

internal readonly record struct HostStagedDecision(HostStagedVerdict Verdict, SwitchServingProof? Proof = null)
{
    public LifecycleResult Result => Verdict switch
    {
        HostStagedVerdict.Committed => LifecycleResult.Ok(),
        HostStagedVerdict.Expired => LifecycleResult.Fail("health_timeout", "the original launch readiness deadline expired"),
        HostStagedVerdict.Stopped => LifecycleResult.Fail("router_stopped", "the engine stopped during readiness"),
        _ => LifecycleResult.Fail("host_publication_refused", "the host could not bind and publish the staged route"),
    };
}

internal readonly record struct HostStagedSwitchResult(
    LifecycleResult Result, HostStagedDecision Decision, bool Opened = false, Exception? DeliveryFailure = null);

internal sealed record HostStagedReadiness(HostClosedRouteStage Binding, string CanonicalModel, ServingCapacity Serving);

// ReadAsync owns its I/O and returns bound data. Settle and TryOpen are runtime guard entries.
// They take the transition gate while host admission is held and call their state-only store
// inside that joint decision. A missing observation permits an expiry/stop decision only.
internal sealed record HostStagedDeadlinePublication(
    Guid ClaimId, SwitchAttemptId ExpectedAttempt, SwitchSampleKey ExpectedTarget, TimeProvider Clock,
    Func<HostClosedRouteStage, CancellationToken, Task<HostStagedReadiness?>> ReadAsync,
    Func<HostClosedRouteStage, HostStagedReadiness?, ReadinessDeadline, Action<SwitchServingProof>, HostStagedDecision> Settle,
    Action<HostStagedDecision> Deliver,
    Func<HostClosedRouteStage, SwitchServingProof, Func<bool>, bool> TryOpen,
    Action Opened,
    Action<HostClosedRouteStage>? AfterStageInstalledForTests = null,
    Action<Task>? CaptureReadinessCleanupForTests = null,
    string? ExpectedPlanDigest = null)
{
    public bool MatchesLaunch(HostCheckpointPlan plan) =>
        ExpectedAttempt.RuntimeEpoch != Guid.Empty && ExpectedAttempt.Sequence > 0 &&
        ExpectedTarget.RuntimeEpoch == ExpectedAttempt.RuntimeEpoch &&
        ExpectedTarget.CanonicalModel == plan.Model && ExpectedTarget.EffectiveProfile == (plan.Tier ?? "") &&
        (ExpectedPlanDigest is null || plan.Digest() == ExpectedPlanDigest);
}

/// <summary>Internal staged fixture authority. Only Open exposes an exact generation to the issuer.</summary>
internal sealed class HostClosedRouteStaging(
    Func<HostMutation, HostCheckpointLaunchEffect?> loadLaunch,
    Func<HostCheckpointLaunchEffect, long, VerifiedInstanceRoute> prepareRoute,
    VerifiedInstanceRoute? incumbentForTests = null,
    ExactTicketAdmission.DomainIdentity? enrolledDomain = null)
{
    private readonly Lock _gate = new();
    private VerifiedInstanceRoute? _openRoute = incumbentForTests;
    private Attempt? _owner;
    private HostClosedRouteStage? _stage;
    private long _generation = incumbentForTests?.RouteIncarnation ?? 0;
    private bool _completed;
    private bool _opening;

    public Action<HostClosedRouteStage, Func<HostClosedRouteStage, bool>>? BeforeOpenForTests { private get; set; }

    private ExactTicketAdmission.DomainIdentity? Enrollment => enrolledDomain;
    internal bool MatchesEnrollment(ExactTicketAdmission.DomainIdentity domain) => Enrollment == domain;

    public bool CanMint(VerifiedInstanceRoute route)
    {
        lock (_gate) return !_opening && _openRoute == route;
    }

    private bool Open(Attempt owner, HostClosedRouteStage expected, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ct.IsCancellationRequested || !_completed || _owner != owner || _stage != expected ||
                expected.Route.RouteIncarnation != _generation || _openRoute is not null) return false;

            _openRoute = expected.Route;
            return true;
        }
    }

    public Attempt Begin(HostStagedPublication publication) => new(this, publication.ClaimId, publication, null);

    public Attempt Begin(HostStagedDeadlinePublication publication) => new(this, publication.ClaimId, null, publication);

    internal sealed class Attempt(HostClosedRouteStaging authority, Guid claimId, HostStagedPublication? plain,
        HostStagedDeadlinePublication? deadlinePublication) : IDisposable
    {
        private HostClosedRouteStage? _completedStage;
        public bool UsesDeadline => deadlinePublication is not null;
        public string? ExpectedPlanDigest => deadlinePublication?.ExpectedPlanDigest;
        public HostStagedDecision Decision { get; private set; }
        public bool Opened { get; private set; }

        public void Close()
        {
            lock (authority._gate)
            {
                authority._owner = this;
                authority._openRoute = null;
                authority._stage = null;
                authority._completed = false;
            }
        }

        public bool StageAndComplete(string reservationPath, HostReservationState state, HostMutation operation, string containerId,
            CancellationToken ct)
        {
            var publication = plain!;
            var launch = LoadExactLaunch(state, operation, containerId);
            if (launch is null || ReserveClosedStage(reservationPath, launch) is not { } stage) return false;
            HostClosedRouteStageFile.Install(reservationPath, stage);
            publication.AfterStageInstalledForTests?.Invoke();
            ct.ThrowIfCancellationRequested();
            lock (authority._gate)
            {
                if (authority._owner != this || authority._generation != stage.Route.RouteIncarnation ||
                    ct.IsCancellationRequested) return false;
                authority._stage = stage;
                // The injected operation commits fixture state only. It does no I/O or notifications.
                if (!publication.TryPublishState(stage) || authority._owner != this || authority._stage != stage) return false;
            }

            publication.AfterStateAcceptedForTests?.Invoke();
            ct.ThrowIfCancellationRequested();
            publication.Complete(stage);
            return MarkCompleted(stage);
        }

        public async ValueTask<LifecycleResult> VerifyAndPublishAsync(string reservationPath, HostReservationState state,
            HostMutation operation, string containerId, Func<CancellationToken, Task<bool>> verifyIdentity, CancellationToken ct)
        {
            var publication = deadlinePublication!;
            var launch = LoadExactLaunch(state, operation, containerId);
            if (launch is null || !publication.MatchesLaunch(launch.Plan)) return Refused();

            using var deadline = ReadinessDeadline.Start(publication.Clock,
                TimeSpan.FromMilliseconds(launch.Plan.ReadinessTimeoutMs), ct);
            if (ReserveClosedStage(reservationPath, launch) is not { } stage) return Refused();

            var identity = await deadline.ObserveAsync(verifyIdentity).ConfigureAwait(false);
            if (identity.Outcome != ReadinessStepOutcome.Completed)
                return SettleDeadline(publication, stage, null, deadline);
            if (!identity.Observation)
                return LifecycleResult.Fail("host_occupancy_unproven", "the saved instance is no longer verified");

            var read = publication.ReadAsync;
            var readiness = await deadline.ObserveAsync(token => read(stage, token),
                captureCleanupForTests: publication.CaptureReadinessCleanupForTests).ConfigureAwait(false);
            if (readiness.Outcome != ReadinessStepOutcome.Completed)
                return SettleDeadline(publication, stage, null, deadline);
            if (readiness.Observation is not { } evidence || evidence.Binding != stage ||
                evidence.CanonicalModel != launch.Plan.Model)
                return SettleDeadline(publication, stage, null, deadline);

            HostClosedRouteStageFile.Install(reservationPath, stage);
            publication.AfterStageInstalledForTests?.Invoke(stage);
            return SettleDeadline(publication, stage, evidence, deadline);
        }

        private LifecycleResult SettleDeadline(HostStagedDeadlinePublication publication, HostClosedRouteStage stage,
            HostStagedReadiness? evidence, ReadinessDeadline deadline)
        {
            lock (authority._gate)
            {
                if (authority._owner != this || authority._generation != stage.Route.RouteIncarnation) return Refused();
                var callback = new SynchronousGuardCallback();
                SwitchServingProof? storedProof = null;
                HostStagedDecision proposed;
                try
                {
                    // The runtime CAS and this exact assignment share the host and transition gates.
                    proposed = publication.Settle(stage, evidence, deadline, proof =>
                    {
                        if (evidence is not null && callback.TryUse())
                        {
                            storedProof = proof;
                            authority._stage = stage;
                        }
                    });
                }
                finally { callback.End(); }

                if (!IsValidDecision(publication, proposed, stage, storedProof))
                {
                    if (ReferenceEquals(authority._stage, stage)) authority._stage = null;
                    return Refused();
                }
                Decision = proposed;
            }

            if (Decision.Verdict != HostStagedVerdict.Refused) publication.Deliver(Decision);
            if (Decision.Verdict == HostStagedVerdict.Committed && !MarkCompleted(stage))
                throw new InvalidOperationException("The committed staged proof lost its closed route binding.");
            return Decision.Result;
        }

        private static LifecycleResult Refused() => default(HostStagedDecision).Result;

        private bool IsValidDecision(HostStagedDeadlinePublication publication, HostStagedDecision proposed,
            HostClosedRouteStage stage, SwitchServingProof? storedProof) =>
            authority._owner == this && stage.ClaimId == publication.ClaimId &&
            authority._generation == stage.Route.RouteIncarnation &&
            (proposed.Verdict switch
            {
                HostStagedVerdict.Committed => ReferenceEquals(authority._stage, stage) &&
                    proposed.Proof is { Version: > 0 } proof && ReferenceEquals(proof, storedProof) &&
                    proof.Attempt == publication.ExpectedAttempt && proof.Target == publication.ExpectedTarget,
                HostStagedVerdict.Expired or HostStagedVerdict.Stopped or HostStagedVerdict.Refused =>
                    authority._stage is null && proposed.Proof is null,
                _ => false,
            });

        private bool MarkCompleted(HostClosedRouteStage stage)
        {
            lock (authority._gate)
            {
                if (authority._owner != this || authority._stage != stage) return false;
                authority._completed = true;
                _completedStage = stage;
                return true;
            }
        }

        private HostClosedRouteStage? ReserveClosedStage(string reservationPath, HostCheckpointLaunchEffect launch)
        {
            if (claimId == Guid.Empty) return null;
            var last = HostClosedRouteStageFile.Read(reservationPath);
            long generation;
            lock (authority._gate)
            {
                if (authority._owner != this) return null;
                generation = checked(Math.Max(authority._generation, last?.Route.RouteIncarnation ?? 0) + 1);
                authority._generation = generation;
            }

            var route = authority.PrepareRoute(launch, generation);
            var operation = launch.Operation;
            if (route.RouteIncarnation != generation || route.LaunchEpoch != operation.Epoch || route.Engine != operation.Engine ||
                route.ContainerId != launch.InstanceId || route.EffectiveTarget != operation.PlanDigest || route.EnrollmentGeneration <= 0 ||
                string.IsNullOrWhiteSpace(route.HostGroup) || string.IsNullOrWhiteSpace(route.ServingIncarnation)) return null;
            if (authority.Enrollment is { } domain &&
                (route.HostGroup != domain.Name || route.EnrollmentGeneration != domain.Generation)) return null;

            return new HostClosedRouteStage(1, claimId, launch, route);
        }

        private HostCheckpointLaunchEffect? LoadExactLaunch(HostReservationState state, HostMutation operation, string containerId)
        {
            if (state.Phase != HostMutationPhase.LaunchApplied || state.Pending != operation ||
                state.Epoch != operation.Epoch || state.Owner != operation.Engine || state.LaunchedContainerId != containerId)
                return null;

            var launch = authority.LoadLaunch(operation);
            return launch is not null && launch.IsValid() && launch.Operation == operation && launch.InstanceId == containerId
                ? launch : null;
        }

        public bool Open(CancellationToken ct)
        {
            if (_completedStage is not { } stage) return false;
            // The fixture can try stale proposals but cannot open the current stage from the callback.
            authority.BeforeOpenForTests?.Invoke(stage, stale => stale != stage && authority.Open(this, stale, ct));
            if (deadlinePublication is not { } publication) return authority.Open(this, stage, ct);
            if (Decision.Proof is not { } proof) return false;
            lock (authority._gate)
            {
                if (Opened || authority._opening) return false;
                var callback = new SynchronousGuardCallback();
                var exposed = false;
                authority._opening = true;
                try
                {
                    var accepted = publication.TryOpen(stage, proof, () =>
                        callback.TryUse() && (exposed = authority.Open(this, stage, ct)));
                    Opened = accepted && exposed && authority._owner == this && authority._openRoute == stage.Route;
                    return Opened;
                }
                finally
                {
                    callback.End();
                    // A contradictory guard cannot expose the route, including to reentrant mint checks.
                    if (!Opened && exposed && authority._openRoute == stage.Route) authority._openRoute = null;
                    authority._opening = false;
                }
            }
        }

        public void Dispose()
        {
            lock (authority._gate)
            {
                if (authority._owner != this) return;
                authority._owner = null;
                authority._stage = null;
                authority._completed = false;
            }
        }
    }

    private HostCheckpointLaunchEffect? LoadLaunch(HostMutation operation) => loadLaunch(operation);
    private VerifiedInstanceRoute PrepareRoute(HostCheckpointLaunchEffect launch, long generation) => prepareRoute(launch, generation);

    private sealed class SynchronousGuardCallback
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _available = true;

        public bool TryUse()
        {
            if (Environment.CurrentManagedThreadId != _thread || !_available) return false;
            _available = false;
            return true;
        }

        public void End() => _available = false;
    }
}

internal static class HostClosedRouteStageFile
{
    private const int HashLength = 32;
    private const int MaximumLength = 64 * 1024;

    public static HostClosedRouteStage? Read(string reservationPath)
    {
        try
        {
            using var file = File.OpenRead(StagePath(reservationPath));
            var length = file.Length;
            if (length <= HashLength || length > MaximumLength) throw new IOException("Invalid closed route stage length.");
            var record = new byte[(int)length];
            file.ReadExactly(record);
            var body = record.AsSpan(HashLength);
            if (!CryptographicOperations.FixedTimeEquals(record.AsSpan(0, HashLength), SHA256.HashData(body)))
                throw new IOException("Invalid closed route stage checksum.");
            return JsonSerializer.Deserialize(body, HostClosedRouteStageJsonContext.Default.HostClosedRouteStage)
                is { Version: 1 } stage ? stage : throw new IOException("Unknown closed route stage format.");
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException error) { throw new IOException("Unreadable closed route stage.", error); }
    }

    public static void Install(string reservationPath, HostClosedRouteStage stage)
    {
        var path = StagePath(reservationPath);
        // The caller holds the stable host reservation. Reuse the scratch path after a failed install.
        var temporary = path + ".new";
        var body = JsonSerializer.SerializeToUtf8Bytes(stage, HostClosedRouteStageJsonContext.Default.HostClosedRouteStage);
        if (body.Length > MaximumLength - HashLength) throw new IOException("The closed route stage exceeds its size bound.");
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(SHA256.HashData(body));
            file.Write(body);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string StagePath(string reservationPath) => Path.GetFullPath(reservationPath) + ".closed-route";
}

[JsonSerializable(typeof(HostClosedRouteStage))]
internal sealed partial class HostClosedRouteStageJsonContext : JsonSerializerContext;
