using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class SuccessorObservationExchangeTests
{
    [Theory(Timeout = 25_000)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task CommissionedObserverBindsTheInstalledCheckpointAcrossTheExchange(bool replaceCheckpoint, bool closed,
        bool replaceRetirement)
    {
        if (!OperatingSystem.IsLinux()) return;

        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = lifetime.Token;
        var scratch = Directory.CreateTempSubdirectory("air-observer-");
        try
        {
            var reservation = new HostReservationScope(Path.Combine(scratch.FullName, "scope"), Guid.NewGuid(), new("partition", 4));
            var head = new EnrolledPairHead("slot", "partition", "daemon", "router", 4, 12, new string('A', 64));
            var image = new DockerImageIdentity("sha256:" + new string('1', 64),
                "registry.invalid/engine@sha256:" + new string('2', 64), "linux/amd64");
            var first = new HostCheckpointPlan("engine", "model", "first", "configuration", 3000, "route", new string('A', 64));
            var plan = first with { Tier = "second", LaunchSettingsDigest = new string('B', 64) };
            var approved = HostInitialLaunchIntent.Freeze(plan, "fixture-approved-bytes"u8);
            var key = new EnrolledPlanKey(plan.Engine, plan.Model, "second");
            var endpoint = new Uri("http://127.0.0.1:28066/v1");
            var target = new CheckedEnrolledTarget(key, new(plan.Engine, plan.Model, key.Profile, plan.Digest(), endpoint),
                new(plan.Engine, head.Daemon, "/fixture/docker.sock"), approved, image, "v1.45");
            var approval = new EnrolledCatalogApproval(new(key, plan.Configuration, plan.BackendRoute, endpoint, plan.Digest()),
                new(approved, image, target.ApiVersion));
            var scope = new EnrolledSuccessorScope(reservation, new(head, [target.EngineGrant], [approval], ["device"], []));
            var checkpointPath = Path.Combine(scratch.FullName, "checkpoint-lock");
            HostEffectCheckpoint.ProvisionNew(checkpointPath);
            using var checkpoint = HostEffectCheckpoint.Open(checkpointPath);
            var launch = checkpoint.PrepareInitialLaunch(first);
            checkpoint.MarkLaunchDispatching(launch);
            var resident = new HostCheckpointLaunchEffect(launch, first, new string('a', 64));
            checkpoint.RecordAppliedLaunch(new(launch, HostMutationStatus.Applied, resident.InstanceId), resident);
            var route = new VerifiedInstanceRoute(head.Partition, head.Generation, first.Engine, first.Digest(), resident.InstanceId,
                "serving-process", launch.Epoch, 3);
            EnrolledSuccessorRetirement? frontier = null;
            if (closed)
            {
                var stop = checkpoint.PrepareSuccessor(plan);
                checkpoint.MarkStopDispatching(stop);
                checkpoint.RecordAppliedStop(new(stop, HostMutationStatus.Applied, resident.InstanceId),
                    new(stop, resident.Operation, resident.InstanceId));
                frontier = new(Guid.NewGuid(), Guid.NewGuid(), resident, stop, 10, 3, route, target.Key, target.PlanDigest, approved.CreateDigest);
            }
            var installed = HostEffectCheckpoint.ReadInstalledCommitment(checkpointPath);
            var query = new EnrolledSuccessorQuery(Guid.NewGuid(), scope, installed.State, target, route, frontier);

            using var keyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var certificateRequest = new CertificateRequest("CN=fixture-successor-observer", keyPair, HashAlgorithmName.SHA256);
            using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var address = Path.Combine(scratch.FullName, "observer.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(address));
            listener.Listen(1);
            var peer = new SuccessorAuthorityPeer(address, certificate.GetCertHashString(HashAlgorithmName.SHA256));
            var observer = new CommissionedSuccessorObserver(checkpointPath, scope, peer,
                static (_, _) => throw new InvalidOperationException("An occupancy read cannot seal ingress."));
            var current = new EnrolledSuccessorObservation(query.Id, scope, 10, 2, 3, CompleteCoverage: true,
                IngressClosed: closed, closed ? HostOccupancyKind.Vacant : HostOccupancyKind.Owned,
                closed ? null : resident, closed ? null : route);

            var answer = ServeAsync();
            var observed = await observer.ObserveAsync(query, ct);
            await answer.WaitAsync(ct);
            if (replaceCheckpoint || replaceRetirement) Assert.Null(observed);
            else
            {
                var readback = Assert.IsType<EnrolledSuccessorObservation>(observed);
                Assert.Equal(current.ResidentRoute, readback.ResidentRoute);
                Assert.Equal(frontier, readback.Retirement);
            }

            async Task ServeAsync()
            {
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                using var accepted = await listener.AcceptAsync(ct);
                await SuccessorObservationExchange.ServeAsync(accepted, HeadReadV1Tests.EffectiveUid(),
                    certificate, scope, (request, _) =>
                    {
                        Assert.Equal(SuccessorObservationKind.Occupancy, request.Kind);
                        Assert.Equal(query.Id, request.QueryId);
                        Assert.Equal(installed.Digest, request.CheckpointDigest);
                        Assert.Equal(installed.State.Revision, request.CheckpointRevision);
                        Assert.Equal(approved.CreateDigest, request.CreateDigest);
                        Assert.Equal<VerifiedInstanceRoute?>(route, request.ExpectedIncumbentRoute);
                        Assert.Equal(frontier?.CloseId, request.CloseId);
                        if (replaceCheckpoint) checkpoint.PrepareSuccessor(plan);
                        var reported = replaceRetirement
                            ? frontier! with { QueryId = Guid.NewGuid() }
                            : frontier;
                        return ValueTask.FromResult<SuccessorObservationReply?>(new(request, current, Retirement: reported));
                    }, ct);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }
}
