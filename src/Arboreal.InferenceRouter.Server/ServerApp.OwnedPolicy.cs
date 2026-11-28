using Microsoft.AspNetCore.Http;

namespace Arboreal.InferenceRouter.Server;

/// <summary>Supplies a verified authority independently of the router's HTTP configuration.</summary>
public sealed class OwnedPrivateServerComposition
{
    public OwnedPrivateServerComposition(OwnedPolicyStore store, IEnumerable<PrivateDestinationEnrollment> destinations,
        Func<HttpContext, CancellationToken, ValueTask<VerifiedInferenceScope?>> verify)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentNullException.ThrowIfNull(verify);
        Policy = new OwnedPrivatePolicy(store, destinations);
        Verify = verify;
    }

    internal OwnedPrivatePolicy Policy { get; }
    internal Func<HttpContext, CancellationToken, ValueTask<VerifiedInferenceScope?>> Verify { get; }
}

public static partial class ServerApp
{
    private const string PolicyGroupHeader = "X-Inference-Policy-Group";
    private static readonly object OwnedRequestKey = new();

    /// <summary>Builds a server bound to the deployment's verified identity and private destination authority.
    /// The deployment retains ownership of the policy store until the application has stopped.</summary>
    public static WebApplication BuildOwnedPrivate(string[] args, OwnedPrivateServerComposition composition,
        string? configPath = null, RouterConfig? configOverride = null)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return Build(args, configPath, configOverride, TimeProvider.System, ownedComposition: composition);
    }

    private sealed record OwnedHttpRequest(VerifiedInferenceScope Scope, string? RequestedGroup, OwnedPolicyGroup? Group);

    private static OwnedHttpRequest? ReadOwnedRequest(HttpContext ctx) =>
        ctx.Items.TryGetValue(OwnedRequestKey, out var item) ? item as OwnedHttpRequest : null;

    private static Task OwnedScopeError(HttpContext ctx) => WriteErrorAsync(ctx, 403, "verified_scope_required",
        "a verified owned inference scope is required");

    private static async Task AuthorizeOwnedRequestAsync(HttpContext ctx, RequestDelegate next,
        OwnedPrivateServerComposition composition)
    {
        var path = new PathString(ctx.Request.Path.Value?.TrimEnd('/'));
        if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx).ConfigureAwait(false);
            return;
        }

        var operation = RequestedOperation(ctx.Request, path);
        var router = ctx.RequestServices.GetRequiredService<RouterCore>();
        ctx.Response.Headers.CacheControl = "no-store";
        try
        {
            var scope = await composition.Verify(ctx, ctx.RequestAborted).ConfigureAwait(false);
            if (scope is null || !scope.Permits(operation)) throw RouterCore.ScopeRequired();
            if (operation != OwnedInferencePermission.Administer)
            {
                var groupId = ReadPolicyGroupHeader(ctx);
                OwnedPolicyGroup? group = null;
                if (operation != OwnedInferencePermission.Inference || scope.ExistingGroup is not null)
                    group = await router.ResolveOwnedPolicyAsync(scope, groupId, operation, false, ctx.RequestAborted).ConfigureAwait(false);
                else if (scope.NewRootIntent is null || groupId is not null)
                    throw new RouterRequestException("policy_group_unavailable", "the authorized policy group could not be resolved");
                ctx.Items[OwnedRequestKey] = new OwnedHttpRequest(scope, groupId, group);
            }
        }
        catch (RouterRequestException error)
        {
            await WriteErrorAsync(ctx, MapStatus(error.Code), error.Code, error.Message).ConfigureAwait(false);
            return;
        }
        catch (Exception error) when (!ctx.RequestAborted.IsCancellationRequested && OwnedPrivatePolicy.IsRecoverableAuthorityFailure(error))
        {
            await WriteErrorAsync(ctx, 403, "verified_scope_required", "a verified owned inference scope is required").ConfigureAwait(false);
            return;
        }
        await next(ctx).ConfigureAwait(false);
    }

    private static string? ReadPolicyGroupHeader(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue(PolicyGroupHeader, out var value)) return null;
        if (value.Count == 1 && value[0] is { Length: 32 } text &&
            Guid.TryParseExact(text, "N", out var id) && text == id.ToString("N")) return text;
        throw new RouterRequestException("policy_group_unavailable", "the authorized policy group could not be resolved");
    }

    private static OwnedInferencePermission RequestedOperation(HttpRequest request, PathString path)
    {
        if (path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/v1/completions", StringComparison.OrdinalIgnoreCase)) return OwnedInferencePermission.Inference;
        if (path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/admin/catalog", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/admin/status", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/admin/events", StringComparison.OrdinalIgnoreCase)) return OwnedInferencePermission.Read;
        if (path.Equals("/admin/threads", StringComparison.OrdinalIgnoreCase)) return OwnedInferencePermission.ControlThread;
        if (path.StartsWithSegments("/admin/tickets", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsDelete(request.Method) ? OwnedInferencePermission.Cancel : OwnedInferencePermission.Read;
        return OwnedInferencePermission.Administer;
    }
}
