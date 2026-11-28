using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Fact]
    public async Task StockProcessSeedUsesTheVerifiedSupervisedReceipt()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seed = await SeedStockProcessAsync(Path.Combine(_directory.FullName, "ingress"), guard.Token);
        Assert.Equal(seed.Initial.Operation, seed.Checkpoint.State.AppliedLaunch?.Operation);
        Assert.Equal(SupervisedLaunchJournal.LastRevision, seed.OperationHead.Revision);
    }

    private async Task<StockSeed> SeedStockProcessAsync(string ingress, CancellationToken ct)
    {
        var (original, pairClaim, grants) = EnrolledPairTopologyFixture(ingress);
        var document = ReadDeepSeekQwenDocument(original);
        document["auto_switch"] = true;
        foreach (var member in pairClaim.Members)
        {
            var engine = DeepSeekQwenEngineTable(document, member.Engine);
            Assert.IsType<TomlTable>(engine["docker"])["api_version"] = SupervisedApiVersion;
            var models = Assert.IsType<TomlTable>(engine["catalog"]);
            var model = Assert.IsType<TomlTable>(models[member.Model]);
            var tiers = Assert.IsType<TomlTableArray>(model["serving"]);
            model["serving"] = new TomlTableArray { Assert.Single(tiers, tier => Equals(tier["name"], member.Tier)) };
            model["default_profile"] = member.Tier;
            engine["catalog"] = new TomlTable { [member.Model] = model };
        }

        var toml = WriteDeepSeekQwenDocument(document);
        var pair = RouterConfigLoader.PrepareEnrolledPair(toml, pairClaim.Head.Slot, pairClaim, _ => grants);
        var approvals = FixtureCommissionApprovals(pair);
        var scope = HostReservation.ProvisionEnrolled(ReservationPath, new(grants.Head.Partition, grants.Head.Generation));
        var commission = new SupervisedLaunchCommission(scope, grants, approvals);
        var first = Assert.Single(pair.Members, member => member.Plan.Engine == DeepSeekEngineName);
        var frozen = Assert.IsType<SupervisedLaunchApproval>(commission.Approval(first.Plan.Engine));
        var raw = Convert.FromBase64String(frozen.Intent.CreateBytes);
        var create = Assert.IsType<DockerCreateRequest>(JsonSerializer.Deserialize(raw, DockerJsonContext.Default.DockerCreateRequest));
        var inventory = new SupervisedImageAtSocket(grants.Head.Daemon, ingress, SupervisedApiVersion, first.Image.Id, first.Image);
        var creation = new SupervisedDaemonCreateSeed(ingress, SupervisedApiVersion, ReceiptInstance, raw, first.Image, create,
            first.Plan.BackendRoute, first.Endpoint, grants.Devices);
        var daemon = new SupervisedFixtureDaemon(scope, grants.Head, inventory, creation, ReceiptInstance, SupervisedApiVersion);
        var observer = new SupervisedFixtureObserver(commission, daemon);
        var head = new SupervisedFixtureHead();
        var journal = new SupervisedLaunchJournal(scope, head);
        var port = new SupervisedFixturePort(journal, daemon, raw);
        var launcher = new SupervisedInitialLaunch(scope, grants.Head.Slot, head, observer, port, new ReceiptOnlyProvider(), ReceiptStaging());
        launcher.ProvisionJournal();
        var applied = Assert.IsType<HostAppliedIngressClosed>(await launcher.RunAsync(first.Plan.Engine, toml, pairClaim, ct)).Receipt;
        var operationHead = Assert.IsType<SupervisedLaunchJournalHead>(head.Current);
        var initial = new HostCheckpointLaunchEffect(applied.Operation, applied.Intent.Plan, applied.InstanceId);
        var checkpointLock = Path.Combine(_directory.FullName, "checkpoint.lock");
        HostEffectCheckpoint.ProvisionNew(checkpointLock);
        var resident = new HostEffectCheckpointState(HostEffectCheckpointState.FormatVersion, 1, 1, HostCheckpointPhase.Resident, initial);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(resident, HostEffectCheckpointJsonContext.Default.HostEffectCheckpointState);
        await File.WriteAllBytesAsync(checkpointLock + ".checkpoint", [.. SHA256.HashData(bytes), .. bytes], ct);
        await File.WriteAllBytesAsync(Path.Combine(scope.AuthorityDirectory, "supervised-launch.successors"), [], ct);

        var catalog = new EnrolledCatalogAuthority(grants.Head,
            [.. grants.Members.Select(member => new EnrolledCatalogEngineGrant(member.Member.Engine, grants.Head.Daemon, member.SocketPath))],
            [.. pair.Members.Select(member =>
            {
                var plan = member.Plan;
                return new EnrolledCatalogApproval(
                    new(new(plan.Engine, plan.Model, plan.Tier!), plan.Configuration, plan.BackendRoute, member.Endpoint, plan.Digest()),
                    Assert.IsType<SupervisedLaunchApproval>(commission.Approval(plan.Engine)));
            })], grants.Devices, []);
        var claim = new EnrolledCatalogClaim(catalog.Head, catalog.Engines, [.. catalog.Approvals.Select(approval => approval.Plan)], catalog.Devices);
        var checkedConfiguration = RouterConfigLoader.PrepareEnrolledConfiguration(toml, grants.Head.Slot, claim, catalog);
        Assert.Equal(2, checkedConfiguration.Catalog.Targets.Length);
        var snapshot = new CatalogAuthoritySnapshot(new(scope, catalog.Head.Slot), catalog);
        return new(toml, new(scope, catalog), snapshot, claim, checkedConfiguration, checkpointLock,
            HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock), initial, operationHead);
    }

    private sealed record StockSeed(string Toml, EnrolledSuccessorScope Scope, CatalogAuthoritySnapshot Catalog,
        EnrolledCatalogClaim Claim, CheckedEnrolledConfiguration Configuration, string CheckpointPath,
        HostEffectCheckpointCommitment Checkpoint, HostCheckpointLaunchEffect Initial, SupervisedLaunchJournalHead OperationHead);

    private static void WriteFrame<T>(string path, T record, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> metadata)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(record, metadata);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        file.Write(length);
        file.Write(payload);
    }
}
