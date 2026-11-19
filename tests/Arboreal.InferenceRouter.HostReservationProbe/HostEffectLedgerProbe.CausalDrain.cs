using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed partial class HostEffectLedgerProbe
{
    private string CurrentFrontierPath => Path.Combine(_root, CausalDrainFiles.Frontier);
    private string IssuerBindingPath => Path.Combine(_root, CausalDrainFiles.Binding);

    private CausalRetirementWitness? RequestCausalDrain(HostEffectCheckpointState checkpoint)
    {
        var binding = Read(IssuerBindingPath, Json.CausalRouteBinding);
        if (binding is null || binding.Incumbent != checkpoint.AppliedLaunch || binding.Scope != EnrolledScope) return null;

        var transition = checkpoint.Transition!;
        var request = new CausalDrainRequest(Guid.NewGuid(), Environment.ProcessId, binding, transition.Stop!, checkpoint.Epoch,
            transition.Plan);
        // The parent process owns transport and retirement. Neither issue nor the issuer gate is
        // held while this child waits for a reply. The parent is the fixture's subprocess watchdog.
        Console.WriteLine(JsonSerializer.Serialize(request, Json.CausalDrainRequest));
        Console.Out.Flush();
        var line = ReadControlFrame(Console.In);
        if (line is null) return null;
        var reply = JsonSerializer.Deserialize(line, Json.CausalDrainReply);
        return reply?.Request == request && reply is { Witness: { } witness } &&
            witness.CloseId == reply.CloseId && witness.Binding == binding ? witness : null;
    }

    private static string? ReadControlFrame(TextReader reader)
    {
        const int limit = 32 * 1024;
        var text = new StringBuilder();
        for (var count = 0; count < limit; count++)
        {
            var next = reader.Read();
            if (next < 0) return null;
            if (next == '\n') return text.ToString();
            text.Append((char)next);
        }
        throw new IOException("The fixture controller reply exceeded its frame bound.");
    }

    private bool WitnessMatches(PartitionQuery query, PartitionFacts facts, CausalRetirementWitness? witness)
    {
        if (witness is not { NoMoreWrites: true } || witness.CloseId == Guid.Empty ||
            Read(IssuerBindingPath, Json.CausalRouteBinding) != witness.Binding) return false;

        var binding = witness.Binding;
        var route = binding.Route;
        var use = witness.FrontierUse;
        return binding.Incumbent == query.Previous && binding.Scope == query.Scope && binding.IssuerId != Guid.Empty &&
            binding.OwnerGeneration == facts.OwnerGeneration && route.RouteIncarnation == facts.IngressGeneration &&
            route.HostGroup == query.Scope.Group && route.EnrollmentGeneration == query.Scope.EnrollmentGeneration &&
            route.Engine == query.Previous.Operation.Engine && route.EffectiveTarget == query.Previous.Plan.Digest() &&
            route.ContainerId == query.Previous.InstanceId && route.LaunchEpoch == query.Previous.Operation.Epoch &&
            !string.IsNullOrWhiteSpace(route.ServingIncarnation) && use.Invocation.Route == route &&
            use.Invocation.Id != Guid.Empty && use.GrantId != Guid.Empty && use.BackendId == binding.BackendId &&
            binding.BackendId != Guid.Empty && witness.Terminal == new CausalBackendTerminal(witness.CloseId, use);
    }
}

internal static class CausalDrainFiles
{
    public const string Binding = "causal-issuer-binding.json";
    public const string Frontier = "causal-closed-frontier.json";
}

internal sealed record CausalRouteBinding(PartitionScope Scope, long OwnerGeneration, HostCheckpointLaunchEffect Incumbent,
    VerifiedInstanceRoute Route, Guid IssuerId, Guid BackendId);
internal sealed record CausalDrainRequest(Guid Nonce, int ControllerPid, CausalRouteBinding Binding, HostMutation Stop,
    long Epoch, HostCheckpointPlan Successor);
internal sealed record CausalDrainReply(CausalDrainRequest Request, Guid CloseId, CausalRetirementWitness? Witness);
internal sealed record CausalFrontierUse(VerifiedInstanceInvocation Invocation, Guid GrantId, Guid BackendId);
internal sealed record CausalBackendTerminal(Guid CloseId, CausalFrontierUse Use);
internal sealed record CausalRetirementWitness(CausalRouteBinding Binding, Guid CloseId, CausalFrontierUse FrontierUse,
    CausalBackendTerminal Terminal, bool NoMoreWrites);
