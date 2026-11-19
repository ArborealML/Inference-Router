using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record HostStagedPublication(
    Guid ClaimId, Func<HostClosedRouteStage, bool> TryPublishState, Action<HostClosedRouteStage> Complete,
    Action? AfterStageInstalledForTests = null, Action? AfterStateAcceptedForTests = null);

internal sealed record HostClosedRouteStage(int Version, Guid ClaimId, HostCheckpointLaunchEffect Launch, VerifiedInstanceRoute Route);

/// <summary>Internal staged fixture authority. Only Open exposes an exact generation to the issuer.</summary>
internal sealed class HostClosedRouteStaging(
    Func<HostMutation, HostCheckpointLaunchEffect?> loadLaunch,
    Func<HostCheckpointLaunchEffect, long, VerifiedInstanceRoute> prepareRoute,
    VerifiedInstanceRoute? incumbentForTests = null)
{
    private readonly Lock _gate = new();
    private VerifiedInstanceRoute? _openRoute = incumbentForTests;
    private Attempt? _owner;
    private HostClosedRouteStage? _stage;
    private long _generation = incumbentForTests?.RouteIncarnation ?? 0;
    private bool _completed;

    public Action<HostClosedRouteStage, Func<HostClosedRouteStage, bool>>? BeforeOpenForTests { private get; set; }

    public bool CanMint(VerifiedInstanceRoute route)
    {
        lock (_gate) return _openRoute == route;
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

    public Attempt Begin(HostStagedPublication publication) => new(this, publication);

    internal sealed class Attempt(HostClosedRouteStaging authority, HostStagedPublication publication) : IDisposable
    {
        private HostClosedRouteStage? _completedStage;

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
            var launch = LoadExactLaunch(state, operation, containerId);
            if (launch is null || publication.ClaimId == Guid.Empty) return false;
            var last = HostClosedRouteStageFile.Read(reservationPath);
            long generation;
            lock (authority._gate)
            {
                if (authority._owner != this) return false;
                generation = checked(Math.Max(authority._generation, last?.Route.RouteIncarnation ?? 0) + 1);
                authority._generation = generation;
            }

            var route = authority.PrepareRoute(launch, generation);
            if (route.RouteIncarnation != generation || route.LaunchEpoch != operation.Epoch || route.Engine != operation.Engine ||
                route.ContainerId != containerId || route.EffectiveTarget != operation.PlanDigest || route.EnrollmentGeneration <= 0 ||
                string.IsNullOrWhiteSpace(route.HostGroup) || string.IsNullOrWhiteSpace(route.ServingIncarnation)) return false;

            var stage = new HostClosedRouteStage(1, publication.ClaimId, launch, route);
            HostClosedRouteStageFile.Install(reservationPath, stage);
            publication.AfterStageInstalledForTests?.Invoke();
            ct.ThrowIfCancellationRequested();
            lock (authority._gate)
            {
                if (authority._owner != this || authority._generation != generation || ct.IsCancellationRequested) return false;
                authority._stage = stage;
                // The injected operation commits fixture state only. It does no I/O or notifications.
                if (!publication.TryPublishState(stage) || authority._owner != this || authority._stage != stage) return false;
            }

            publication.AfterStateAcceptedForTests?.Invoke();
            ct.ThrowIfCancellationRequested();
            publication.Complete(stage);
            lock (authority._gate)
            {
                if (authority._owner != this || authority._stage != stage) return false;
                authority._completed = true;
                _completedStage = stage;
                return true;
            }
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
            return authority.Open(this, stage, ct);
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
