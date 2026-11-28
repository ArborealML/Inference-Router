using System.Text.Json;

namespace Arboreal.InferenceRouter;

public sealed partial class RouterCore
{
    private const int OwnedDefaultStreamBufferLimitBytes = 256 * 1024;
    internal bool OwnedPolicyEnabled => _ownedPolicy is not null;
    internal EventRing EventFeed => _events;

    internal async ValueTask<OwnedPolicyGroup> ResolveOwnedPolicyAsync(VerifiedInferenceScope scope, string? requestedGroup,
        OwnedInferencePermission operation, bool mayCreate, CancellationToken cancellationToken)
    {
        if (_ownedPolicy is not { } policy || !scope.Permits(operation)) throw ScopeRequired();
        if (!policy.ValidRestrictions(scope))
            throw new RouterRequestException("policy_restriction_invalid", "the verified scope has an invalid destination restriction");
        var group = await policy.Store.ResolveAsync(scope, requestedGroup, mayCreate, cancellationToken).ConfigureAwait(false);
        if (group is null || !policy.ValidRestrictions(group))
        {
            SignalDispatch();
            throw new RouterRequestException("policy_group_unavailable", "the authorized policy group could not be resolved");
        }
        return group;
    }

    internal ValueTask<RouterTicket> EnqueueOwnedHttpAsync(string? threadId, InferenceRequest request,
        OwnedTicketPolicy policy, CancellationToken cancellationToken)
    {
        if (!policy.Scope.Permits(OwnedInferencePermission.Inference) || policy.Scope.Owner != policy.Group.Owner ||
            (policy.Scope.ExistingGroup is { } group ? group != policy.Group.Group : policy.Scope.NewRootIntent != policy.Group.RootIntent))
            throw ScopeRequired();
        return EnqueueAsync(threadId, request, request.Stream, cancellationToken, policy);
    }

    internal static RouterRequestException ScopeRequired() => new("verified_scope_required", "a verified owned inference scope is required");

    private static string ScopedThreadKey(PolicyGroupKey key, string thread) => key.Owner + "/" + key.Group + "/" + thread;

    internal bool AllowsOwnedRoute(OwnedTicketPolicy policy, string engine) => _ownedPolicy is { } authority &&
        authority.IsCurrent(policy) && authority.Allows(policy, engine);

    private bool AllowsOwnedRoute(RouterTicket ticket, string engine) => _ownedPolicy is null ||
        ticket.OwnedPolicy is { } policy && _ownedPolicy.IsCurrent(policy) && _ownedPolicy.Allows(policy, engine);

    private string ResolveOwnedModel(string requested, ConfigurationGeneration generation, OwnedTicketPolicy policy)
    {
        string? canonical = null;
        foreach (var engine in generation.Engines.Values)
        {
            if (!AllowsOwnedRoute(policy, engine.Name)) continue;
            foreach (var model in engine.Definition.Catalog.Values)
            {
                if (!string.Equals(model.Name, requested, StringComparison.OrdinalIgnoreCase) &&
                    !model.Aliases.Contains(requested, StringComparer.OrdinalIgnoreCase)) continue;
                if (canonical is not null && !string.Equals(canonical, model.Name, StringComparison.OrdinalIgnoreCase))
                    throw new RouterRequestException("policy_route_denied", "the requested model has no unambiguous allowed private route");
                canonical = model.Name;
            }
            foreach (var reported in engine.Capability.ModelRoster)
                if (canonical is null && string.Equals(reported, requested, StringComparison.OrdinalIgnoreCase)) canonical = reported;
        }
        return canonical ?? throw new RouterRequestException("policy_route_denied", "the requested model has no allowed private route");
    }

    internal void RegisterOwnedThread(OwnedPolicyGroup group, string threadId, ThreadPriority priority, RouterThreadOptions? options)
    {
        if (_ownedPolicy?.Store.IsCurrent(group) != true) throw ScopeRequired();
        ApplyThreadPriority(ScopedThreadKey(group.Key, threadId), priority, options, expected: null, group.Key, threadId);
    }

    internal bool HasOwnedEvent(PolicyGroupKey key, string ticketId) => _events.Replay(0)
        .Any(e => e.OwnerGroup == key && e.TicketId == ticketId);

    internal IAsyncEnumerable<RouterEvent> SubscribeOwnedEvents(RouterEventFilter filter, PolicyGroupKey key, CancellationToken ct) =>
        new EventEnumerable(_events, filter with { OwnerGroup = key }, ct, _shutdown.Token);

    internal async ValueTask<OwnedSensitivityOutcome> EscalateOwnedSensitivityAsync(OwnedPolicyGroup group, string operation,
        long expectedRevision, CancellationToken cancellationToken)
    {
        if (_ownedPolicy is not { } authority) throw ScopeRequired();
        try { return await authority.Store.EscalateAsync(group, operation, expectedRevision, cancellationToken).ConfigureAwait(false); }
        finally { SignalDispatch(); }
    }

    internal ScopedRouterStats GetOwnedStats(OwnedPolicyGroup group, VerifiedInferenceScope scope)
    {
        var key = group.Key;
        lock (_gate)
        {
            var engines = CurrentGeneration.Engines.Values
                .Where(engine => CanSeeOwnedEngine(group, scope, engine.Name))
                .Select(engine => new ScopedEngineStats(engine.Name, OwnedInflight(key, engine.Name))).ToArray();
            var queues = _queues.Values.Select(queue => (queue.Model, Count: queue.CaptureRound().Requests.Count(
                    request => request.Ticket.OwnedPolicy?.Key == key)))
                .Where(static row => row.Count > 0).Take(128)
                .ToDictionary(static row => row.Model, static row => row.Count, StringComparer.Ordinal);
            var threads = _threads.Values.Where(thread => thread.OwnerGroup == key).Take(128).Select(ReadThreadStats).ToArray();
            return new ScopedRouterStats(engines, queues, threads, new ScopedPolicyStatus(group.Revision, group.Sensitive, _ownedPolicy?.Store.ReadEffectivePolicy(group.Key)));
        }
    }

    internal bool CanSeeOwnedEngine(OwnedPolicyGroup group, VerifiedInferenceScope scope, string engine) =>
        AllowsOwnedRoute(new OwnedTicketPolicy(group, scope, PrivateDestinationCapability.Chat), engine) ||
        AllowsOwnedRoute(new OwnedTicketPolicy(group, scope, PrivateDestinationCapability.Text), engine);

    internal static PrivateDestinationCapability CheckOwnedWorkload(JsonElement root, bool chat)
    {
        // Only the text completion dialects are qualified by this composition. An input which
        // would change paths or give the backend tool/media work is refused at ingress.
        if (root.ValueKind != JsonValueKind.Object) throw WorkloadRefused();
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            var allowed = property.Name switch
            {
                "model" or "reasoning_effort" => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 },
                "stream" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "max_tokens" or "max_completion_tokens" or "seed" or "n" or "temperature" or "top_p" => value.ValueKind == JsonValueKind.Number,
                "stop" => value.ValueKind == JsonValueKind.String || value.ValueKind == JsonValueKind.Array &&
                    value.EnumerateArray().All(static stop => stop.ValueKind == JsonValueKind.String),
                "stream_options" => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(static option =>
                    option.Name == "include_usage" && option.Value.ValueKind is JsonValueKind.True or JsonValueKind.False),
                _ => false,
            };
            if (allowed) continue;
            if (chat && property.Name == "messages" || !chat && property.Name == "prompt") continue;
            throw WorkloadRefused();
        }
        if (chat)
        {
            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array ||
                messages.GetArrayLength() == 0) throw WorkloadRefused();
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.String || !message.TryGetProperty("role", out var role) ||
                    role.ValueKind != JsonValueKind.String || role.GetString() is not ("system" or "user" or "assistant") ||
                    !message.EnumerateObject().All(static property => property.Name is "content" or "role")) throw WorkloadRefused();
            }
            return PrivateDestinationCapability.Chat;
        }
        if (!root.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String) throw WorkloadRefused();
        return PrivateDestinationCapability.Text;

        static RouterRequestException WorkloadRefused() => new("policy_capability_unavailable", "the request is outside the qualified private text dialect");
    }
}

internal sealed record ScopedPolicyStatus(long Revision, bool Sensitive,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    OwnedEffectivePolicy? Effective = null);
internal sealed record ScopedEngineStats(string Engine, int Inflight);
internal sealed record ScopedRouterStats(ScopedEngineStats[] Engines, IReadOnlyDictionary<string, int> QueueDepths,
    ThreadStats[] Threads, ScopedPolicyStatus Policy);
