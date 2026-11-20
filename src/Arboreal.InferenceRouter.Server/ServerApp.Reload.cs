using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Arboreal.InferenceRouter.Server;

public static partial class ServerApp
{
    private sealed class FileReloadEndpoint(string path, string credential)
    {
        private readonly byte[] _credential = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        private readonly string _instance = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        internal Task GetAsync(HttpContext context, RouterCore router)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Authorized(context)) return UnauthorizedAsync(context);
            return router.Closing
                ? ErrorAsync(context, ConfigReloadStatus.Closing)
                : WriteGenerationAsync(context, router.CurrentGeneration.Number);
        }

        internal async Task ApplyAsync(HttpContext context, RouterCore router)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Authorized(context))
            {
                await UnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.ContentLength is > 0 ||
                context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
                !TryReadTag(context, out var instance, out var generation))
            {
                await WriteErrorAsync(context, 400, "invalid_reload_request",
                    "Send one current reload ETag in If-Match and leave the body empty.").ConfigureAwait(false);
                return;
            }

            if (!string.Equals(instance, _instance, StringComparison.Ordinal) ||
                generation != router.CurrentGeneration.Number)
            {
                await ErrorAsync(context, ConfigReloadStatus.Stale).ConfigureAwait(false);
                return;
            }
            if (router.Closing)
            {
                await ErrorAsync(context, ConfigReloadStatus.Closing).ConfigureAwait(false);
                return;
            }

            ConfigReloadStatus status;
            long applied;
            try
            {
                using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, router.ShutdownToken);
                var text = await File.ReadAllTextAsync(path, readLifetime.Token).ConfigureAwait(false);
                var candidate = RouterConfigLoader.LoadForReload(text, path);
                (status, applied) = await router.ApplyConfigurationAsync(candidate, generation, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (router.Closing)
            {
                await ErrorAsync(context, ConfigReloadStatus.Closing).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is RouterConfigException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
            {
                await ErrorAsync(context, ConfigReloadStatus.Invalid).ConfigureAwait(false);
                return;
            }

            if (status is ConfigReloadStatus.Applied or ConfigReloadStatus.Unchanged)
                await WriteGenerationAsync(context, applied).ConfigureAwait(false);
            else if (status is not ConfigReloadStatus.Cancelled)
                await ErrorAsync(context, status).ConfigureAwait(false);
        }

        private bool Authorized(HttpContext context)
        {
            const string scheme = "Bearer ";
            if (!context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var header) || header.Count != 1 ||
                header[0] is not { } value || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ||
                value.Length == scheme.Length || value.Contains(',', StringComparison.Ordinal))
                return false;
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value[scheme.Length..]));
            return CryptographicOperations.FixedTimeEquals(digest, _credential);
        }

        private static Task UnauthorizedAsync(HttpContext context)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return WriteErrorAsync(context, 401, "reload_unauthorized", "A reload credential is required.");
        }

        private static bool TryReadTag(HttpContext context, out string instance, out long generation)
        {
            const string prefix = "\"reload-";
            const int nonceLength = 32;
            const int indexOfSeparator = 8 + nonceLength;
            instance = string.Empty;
            generation = 0;
            if (!context.Request.Headers.TryGetValue(HeaderNames.IfMatch, out var tags) || tags.Count != 1 ||
                tags[0] is not { } tag || tag.Length <= indexOfSeparator + 2 ||
                !tag.StartsWith(prefix, StringComparison.Ordinal) || tag[^1] != '"' || tag[indexOfSeparator] != '-')
                return false;

            var nonce = tag.AsSpan(prefix.Length, nonceLength);
            foreach (var digit in nonce)
                if (digit is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
            var digits = tag.AsSpan(indexOfSeparator + 1, tag.Length - indexOfSeparator - 2);
            if (digits[0] == '0' || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out generation) ||
                generation <= 0)
                return false;
            instance = nonce.ToString();
            return true;
        }

        private Task WriteGenerationAsync(HttpContext context, long generation)
        {
            context.Response.StatusCode = 200;
            context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"reload-{_instance}-{generation}\"");
            return AirJson.WriteAsync(context.Response, new ReloadAck(generation),
                AirServerJson.AirServerContext.Default.ReloadAck, context.RequestAborted);
        }

        private static Task ErrorAsync(HttpContext context, ConfigReloadStatus status)
        {
            var (http, code, message) = status switch
            {
                ConfigReloadStatus.Stale => (412, "reload_stale", "The reload ETag is stale. Read the current generation and retry."),
                ConfigReloadStatus.Busy => (409, "reload_busy", "The router cannot apply this change while work is active. Retry after it becomes idle."),
                ConfigReloadStatus.RestartRequired => (422, "reload_requires_restart", "The configuration changes require a router restart."),
                ConfigReloadStatus.Closing => (503, "reload_unavailable", "The router is shutting down."),
                _ => (422, "reload_invalid_config", "The startup configuration file could not be read or validated."),
            };
            return WriteErrorAsync(context, http, code, message);
        }
    }
}
