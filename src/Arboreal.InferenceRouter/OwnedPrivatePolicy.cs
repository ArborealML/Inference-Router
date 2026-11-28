using System.Collections.Frozen;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter;

[Flags]
public enum OwnedInferencePermission
{
    None = 0,
    Inference = 1,
    Read = 2,
    ControlThread = 4,
    Cancel = 8,
    Administer = 16,
    EscalateSensitivity = 32,
}

[Flags]
public enum PrivateDestinationCapability
{
    None = 0,
    Chat = 1,
    Text = 2,
    Vision = 4,
    Count = 8,
    Tokenize = 16,
    ThreadCache = 32,
}

public sealed record PrivateDestinationEnrollment(string Engine, Uri Endpoint, string ModelsPath, string ServingProbePath,
    PrivateDestinationCapability Capabilities);

/// <summary>Values supplied by the deployment's verified credential adapter.</summary>
public sealed class VerifiedInferenceScope
{
    public VerifiedInferenceScope(string owner, OwnedInferencePermission permissions, string? newRootIntent = null,
        string? existingGroup = null, IReadOnlyList<string>? allow = null, IReadOnlyList<string>? deny = null)
    {
        if (!OwnedPrivatePolicy.IsIdentifier(owner)) throw new ArgumentException("Invalid verified owner.", nameof(owner));
        if (newRootIntent is not null && !OwnedPrivatePolicy.IsIdentifier(newRootIntent))
            throw new ArgumentException("Invalid verified root intent.", nameof(newRootIntent));
        if (existingGroup is not null && (newRootIntent is not null || !Guid.TryParseExact(existingGroup, "N", out _)))
            throw new ArgumentException("Invalid verified group association.", nameof(existingGroup));
        if ((permissions & ~(OwnedInferencePermission.Inference | OwnedInferencePermission.Read |
            OwnedInferencePermission.ControlThread | OwnedInferencePermission.Cancel | OwnedInferencePermission.Administer |
            OwnedInferencePermission.EscalateSensitivity)) != OwnedInferencePermission.None)
            throw new ArgumentException("Invalid verified permissions.", nameof(permissions));
        Owner = owner;
        Permissions = permissions;
        NewRootIntent = newRootIntent;
        ExistingGroup = existingGroup is null ? null : Guid.ParseExact(existingGroup, "N").ToString("N");
        Allow = allow is null ? null : [.. allow];
        Deny = deny is null ? [] : [.. deny];
    }

    public string Owner { get; }
    public OwnedInferencePermission Permissions { get; }
    internal string? NewRootIntent { get; }
    internal string? ExistingGroup { get; }
    internal string[]? Allow { get; }
    internal string[] Deny { get; }

    internal bool Permits(OwnedInferencePermission operation) => (Permissions & operation) == operation;
}

internal readonly record struct PolicyGroupKey(string Owner, string Group);

internal sealed record OwnedPolicyGroup([property: JsonRequired] string Owner, [property: JsonRequired] string Group,
    [property: JsonRequired] string RootIntent, [property: JsonRequired] bool Sensitive,
    [property: JsonRequired] long Revision, [property: JsonRequired] string[]? Allow, [property: JsonRequired] string[] Deny)
{
    internal PolicyGroupKey Key => new(Owner, Group);
}

internal sealed record OwnedTicketPolicy(OwnedPolicyGroup Group, VerifiedInferenceScope Scope, PrivateDestinationCapability Capability)
{
    internal PolicyGroupKey Key => Group.Key;
}

internal sealed class OwnedPrivatePolicy(OwnedPolicyStore store, IEnumerable<PrivateDestinationEnrollment> destinations)
{
    private readonly FrozenDictionary<string, PrivateDestinationEnrollment> _destinations =
        destinations.ToFrozenDictionary(static destination => destination.Engine, StringComparer.Ordinal);

    internal OwnedPolicyStore Store => store;
    private readonly byte[] _fingerprintKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    internal Func<OwnedPrivateAttempt, Task>? BeforeHandoffForTests;
    internal Func<OwnedPrivateAttempt, Task>? AfterHandoffClaimForTests;

    internal OwnedPrivateAttempt FreezeAttempt(RouterTicket ticket, EngineRuntime engine, Func<HttpRequestMessage> project,
        byte[] body) => new(ticket, engine, project, body, _fingerprintKey);

    internal void ValidateTopology(RouterConfig config, bool unqualifiedComposition)
    {
        if (!config.OwnedPrivatePolicy || unqualifiedComposition || config.HttpHandlerFactory is not null || config.Threads.Count != 0 ||
            config.Engines.Count == 0 || config.Engines.Count > 32)
            throw new RouterConfigException("Owned private policy requires enrolled private built-in transports and no legacy threads.");

        foreach (var (name, engine) in config.Engines)
        {
            var endpoint = engine.Endpoint;
            if (!_destinations.TryGetValue(name, out var enrollment) || engine.Name != name || !IsIdentifier(name) ||
                engine.Kind != EngineKind.OpenAiCompatible || engine.ResolveLifecycleOwner() != EngineLifecycleOwner.External ||
                engine.LifecycleGroup is not null ||
                !IsPrivateLiteral(endpoint) || endpoint != enrollment.Endpoint ||
                engine.ModelsEndpoint != enrollment.ModelsPath || EngineRuntime.ServingProbePath(engine) != enrollment.ServingProbePath ||
                !IsProbePath(engine.ModelsEndpoint) || !IsProbePath(enrollment.ServingProbePath) ||
                engine.Catalog.Count > 128 || engine.Catalog.Values.Any(static model => model.Name.Length > 128 ||
                    model.Aliases.Length > 16 || model.Aliases.Any(static alias => alias.Length > 128) ||
                    model.ServingProfiles.Count > 16 || model.ServingProfiles.Any(static profile => profile.Name.Length > 96)) ||
                (enrollment.Capabilities & (PrivateDestinationCapability.Chat | PrivateDestinationCapability.Text)) == PrivateDestinationCapability.None)
                throw new RouterConfigException("An owned private engine has no matching qualified destination enrollment.");
        }
    }

    internal bool ValidRestrictions(VerifiedInferenceScope scope) => ValidNames(scope.Allow) && ValidNames(scope.Deny);

    internal bool ValidRestrictions(OwnedPolicyGroup group) => ValidNames(group.Allow) && ValidNames(group.Deny);

    internal bool Allows(OwnedTicketPolicy policy, string engine)
    {
        return _destinations.TryGetValue(engine, out var destination) &&
            (destination.Capabilities & policy.Capability) == policy.Capability &&
            Within(policy.Group.Allow, policy.Group.Deny, engine) && Within(policy.Scope.Allow, policy.Scope.Deny, engine);
    }

    internal bool IsCurrent(OwnedTicketPolicy policy) => store.IsCurrent(policy.Group);

    internal bool SendsThreadCacheHint(string engine) => _destinations.TryGetValue(engine, out var destination) &&
        (destination.Capabilities & PrivateDestinationCapability.ThreadCache) != PrivateDestinationCapability.None;

    private bool ValidNames(string[]? items) => items is null || items.Length <= 32 &&
        items.All(item => item is not null && _destinations.ContainsKey(item)) &&
        items.Distinct(StringComparer.Ordinal).Count() == items.Length;

    private static bool Within(string[]? allow, string[] deny, string engine) =>
        (allow is null || allow.Contains(engine, StringComparer.Ordinal)) && !deny.Contains(engine, StringComparer.Ordinal);

    internal static bool IsIdentifier(string? value) => value is { Length: >= 1 and <= 96 } &&
        value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    internal static bool IsRecoverableAuthorityFailure(Exception error) => error switch
    {
        OutOfMemoryException or StackOverflowException or AccessViolationException or SEHException => false,
        AggregateException aggregate => aggregate.InnerExceptions.All(IsRecoverableAuthorityFailure),
        _ => error.InnerException is not { } inner || IsRecoverableAuthorityFailure(inner),
    };

    private static bool IsPrivateLiteral(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        IPAddress.TryParse(uri.IdnHost, out var address) && IPAddress.IsLoopback(address);

    private static bool IsProbePath(string path) => path.Length is >= 1 and <= 256 &&
        !path.StartsWith("//", StringComparison.Ordinal) && path.AsSpan().IndexOfAny('\\', ':', '#') < 0 && !path.Any(char.IsControl);
}
