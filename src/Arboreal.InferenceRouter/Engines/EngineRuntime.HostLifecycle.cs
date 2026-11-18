using System.Collections.Immutable;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed record HostEngineEnrollment(string RouterId, string Engine, HostLifecycleCoordinator Controller);

public sealed partial class EngineRuntime
{
    private readonly HostEngineEnrollment? _hostEnrollment;

    private async ValueTask<EngineLifecycleCoordinator.SwitchReport> SwitchEnrolledHostAsync(
        HostEngineEnrollment enrollment, SwitchState state, LifecycleTarget target, CancellationToken ct)
    {
        var publication = new HostSwitchPublication(
            proof => TryPublishProof(state, new ModelsObservation(ImmutableList.Create(proof.Target.Model),
                ImmutableDictionary<string, long>.Empty)),
            () =>
            {
                Emit("engine.switch_completed", model: target.Model,
                    reason: state.Profile is null ? null : $"profile={WireText.Ident(state.Profile)}",
                    value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
                EndSwitch(state, true);
            });
        var result = await enrollment.Controller.SwitchAsync(target, ct, publication).ConfigureAwait(false);

        // This slice carries no incumbent instance proof to restore. Physical preflight refusal
        // still leaves the incumbent running, and the legacy path retains its own restoration check.
        return new EngineLifecycleCoordinator.SwitchReport(result, RefusedBeforeMutation: false);
    }
}
