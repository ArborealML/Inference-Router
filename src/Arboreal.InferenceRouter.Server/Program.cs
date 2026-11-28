using Arboreal.InferenceRouter.Server;

// usage: Arboreal.InferenceRouter.Server [serve] [--config|-c path] [--urls http://…]
// "serve" is optional; remaining args pass through to the ASP.NET host (e.g. --urls).
var serveArgs = args is ["serve", ..] ? args[1..] : args;
if (await ServerApp.TryRunCommissionedAsync(serveArgs).ConfigureAwait(false) is { } commissioned) return commissioned;
var hostArgs = new List<string>();
if (!ServerApp.TryReadConfigOption(serveArgs, out var configPath, hostArgs))
    return 2;
return await ServerApp.RunAsync(hostArgs.ToArray(), configPath).ConfigureAwait(false);
