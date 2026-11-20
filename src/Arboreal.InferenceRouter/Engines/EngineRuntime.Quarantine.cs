using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed class NativeSwitchQuarantine(DockerLifecycleConfig config, Guid definition)
{
    internal readonly record struct Target(string Engine, string Model, string Profile, Guid Definition);

    private readonly Dictionary<Target, EngineRuntime.SwitchResult> _failures = new();

    // Latch operations belong to the owning runtime's transition gate.
    internal Target Resolve(LifecycleTarget target)
    {
        var launch = config.TargetPlans?.Resolve(target);
        return new(target.EngineName, launch?.Model ?? target.Model,
            launch?.Profile ?? target.EffectiveProfile, definition);
    }

    internal EngineRuntime.SwitchResult? Read(Target target) => _failures.GetValueOrDefault(target);
    internal void Remember(Target target, EngineRuntime.SwitchResult failure) => _failures[target] = failure;
    internal bool Reset(Target target) => _failures.Remove(target);
}

public sealed partial class EngineRuntime
{
    private readonly NativeSwitchQuarantine? _nativeQuarantine;
    internal Action? BeforeNativeFailureEventForTests;

    // The router holds its generation gate before taking this runtime's transition gate.
    internal bool ResetNativeTargetUnderGate(string model, string effectiveProfile)
    {
        if (_stopped || _retiring || _switchState.Model is not null || _nativeQuarantine is not { } quarantine ||
            !_catalog.TryGetValue(model, out var entry))
            return false;

        var profile = GetProfile(model, effectiveProfile);
        if (profile is null && effectiveProfile.Length != 0 && effectiveProfile != entry.Profile)
            return false;

        var target = quarantine.Resolve(new LifecycleTarget(Name, model, profile, entry));
        return target.Model == model && target.Profile == effectiveProfile && quarantine.Reset(target);
    }
}
