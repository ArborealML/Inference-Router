using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

// This fixture gives the controller, worker journal, and fake create effect independent state.
// A second process starts with no in-memory receipts or ownership information.
internal sealed class RestartHostWorker(string directory, string mode) : IHostLifecycleProvider
{
    private static readonly WorkerJsonContext Json = WorkerJsonContext.Default;
    private string CreateEffectPath => Path.Combine(directory, "created");

    public ValueTask<HostOccupancy> ObserveAsync(Guid queryId, CancellationToken ct)
    {
        var created = ReadCreateEffect();
        return ValueTask.FromResult(created.Operation is { } launch && created.ContainerId is { } id
            ? new HostOccupancy(queryId, HostOccupancyKind.Owned, new HostResident(launch.Engine, id))
            : new HostOccupancy(queryId, HostOccupancyKind.Vacant));
    }

    public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) =>
        ValueTask.FromResult(LifecycleResult.Ok());

    public ValueTask<IHostDrain> CloseAndDrainAsync(HostResident? incumbent, CancellationToken ct)
    {
        FixtureDrain? untransferred = null;
        try
        {
            untransferred = new FixtureDrain(mode == "reenter-after-publication");
            var result = ValueTask.FromResult<IHostDrain>(untransferred);
            // The coordinator owns and disposes the guard after receiving the result.
            untransferred = null;
            return result;
        }
        finally
        {
            untransferred?.Dispose();
        }
    }

    public string GetLaunchPlanDigest(LifecycleTarget target)
    {
        using var bytes = new MemoryStream();
        using (var plan = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            plan.Write("fixture-v1");
            plan.Write(target.EngineName);
            plan.Write(target.Model);
            plan.Write(target.Profile?.Name ?? target.Definition.Profile ?? "");
            plan.Write(target.Profile?.MaxContext ?? 0);
            plan.Write(target.Profile?.MaxSlots ?? 0);
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    public ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken ct)
    {
        if (mode != "reenter-after-publication") throw new InvalidOperationException("Unexpected fixture stop request.");
        var created = ReadCreateEffect();
        if (operation.Kind != HostMutationKind.Stop || operation.Target != created.ContainerId ||
            operation.Engine != created.Operation?.Engine)
            throw new InvalidOperationException("The stop request did not name the exact fake incumbent.");

        // Record receipt of the request. The fixture refuses it before issuing a stop effect.
        AppendOperation("stop-requests", operation);
        return ValueTask.FromResult(new HostMutationOutcome(operation, HostMutationStatus.RejectedBeforeIssue));
    }

    public ValueTask<HostMutationOutcome> LaunchAsync(HostMutation operation, LifecycleTarget target, CancellationToken ct)
    {
        var planDigest = GetLaunchPlanDigest(target);
        if (operation.PlanDigest != planDigest)
            throw new InvalidOperationException("The launch plan does not match the accepted operation.");

        WriteOnce(AcceptedPath(operation), operation, Json.HostMutation);
        if (mode == "reject")
        {
            var rejected = new HostMutationOutcome(operation, HostMutationStatus.RejectedBeforeIssue);
            WriteOnce(ReceiptPath(operation), rejected, Json.HostMutationOutcome);
            return ValueTask.FromResult(rejected);
        }

        var applied = Create(operation);
        if (mode == "duplicate-create-control") _ = Create(operation);
        // The uncertain case issued create but has no durable terminal evidence.
        if (mode == "uncertain")
            return ValueTask.FromResult(new HostMutationOutcome(operation, HostMutationStatus.UnknownAfterIssue));

        var receipt = mode switch
        {
            "mismatched-applied" => applied with
            {
                Operation = operation with { PlanDigest = AlternateDigest(planDigest) },
            },
            "wrong-instance-applied" => applied with { ContainerId = "wrong-instance" },
            _ => applied,
        };
        WriteOnce(ReceiptPath(operation), receipt, Json.HostMutationOutcome);
        if (mode is "lost-reply" or "mismatched-applied" or "wrong-instance-applied")
            throw new IOException("The fake worker reply was lost.");
        return ValueTask.FromResult(receipt);
    }

    public ValueTask<HostMutationOutcome> ReconcileAsync(HostMutation operation, CancellationToken ct)
    {
        var accepted = ReadIfPresent(AcceptedPath(operation), Json.HostMutation);
        return ValueTask.FromResult(accepted == operation
            ? ReadIfPresent(ReceiptPath(operation), Json.HostMutationOutcome)
            : default);
    }

    public ValueTask<LifecycleResult> VerifyReadyAsync(HostMutation launch, string containerId, CancellationToken ct)
    {
        var applied = ReadCreateEffect();
        if (applied.Status != HostMutationStatus.Applied || applied.Operation != launch || applied.ContainerId != containerId)
            throw new InvalidOperationException("The readiness proof does not match the exact fake create.");
        return ValueTask.FromResult(LifecycleResult.Ok(containerId));
    }

    private HostMutationOutcome Create(HostMutation operation)
    {
        if (ReadIfPresent(AcceptedPath(operation), Json.HostMutation) != operation)
            throw new InvalidOperationException("The fake daemon requires a durable acceptance before create.");
        AppendOperation("create-attempts", operation);
        var applied = new HostMutationOutcome(operation, HostMutationStatus.Applied, $"id-{operation.Id:N}");
        WriteOnce(CreateEffectPath, applied, Json.HostMutationOutcome);
        return applied;
    }

    private HostMutationOutcome ReadCreateEffect() => ReadIfPresent(CreateEffectPath, Json.HostMutationOutcome);
    private string AcceptedPath(HostMutation operation) => Path.Combine(directory, $"{operation.Id:N}.accepted");
    private string ReceiptPath(HostMutation operation) => Path.Combine(directory, $"{operation.Id:N}.receipt");

    private static string AlternateDigest(string digest) => (digest[0] == '0' ? "1" : "0") + digest[1..];

    private void AppendOperation(string name, HostMutation operation)
    {
        using var file = new FileStream(Path.Combine(directory, name), FileMode.Append, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, operation, Json.HostMutation);
        file.WriteByte((byte)'\n');
        file.Flush(flushToDisk: true);
    }

    private static T? ReadIfPresent<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        if (!File.Exists(path)) return default;
        using var file = File.OpenRead(path);
        return JsonSerializer.Deserialize(file, typeInfo);
    }

    private static void WriteOnce<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value, typeInfo);
        file.Flush(flushToDisk: true);
    }

    private sealed class FixtureDrain(bool allowWithdrawal) : IHostDrain
    {
        public LifecycleResult Result => LifecycleResult.Ok();
        public void WithdrawIncumbent()
        {
            if (!allowWithdrawal) throw new InvalidOperationException("Unexpected fixture withdrawal.");
        }
        // This process fixture models acceptance by its return value and has no route store.
        public bool CommitVerifiedRoute(HostServingProof proof, Func<bool> publish, Func<Action, bool> tryAccept) =>
            publish() && tryAccept(static () => { });
        public void Dispose() { }
    }
}

[JsonSerializable(typeof(HostMutation))]
[JsonSerializable(typeof(HostMutationOutcome))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext;
