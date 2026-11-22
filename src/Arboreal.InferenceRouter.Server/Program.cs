using Arboreal.InferenceRouter.Server;

// usage: Arboreal.InferenceRouter.Server [serve] [--config|-c path] [--urls http://…]
// "serve" is optional; remaining args pass through to the ASP.NET host (e.g. --urls).
var hostArgs = new List<string>();
if (!ServerApp.TryReadConfigOption(args.AsSpan(args is ["serve", ..] ? 1 : 0), out var configPath, hostArgs))
    return 2;
return await ServerApp.RunAsync(hostArgs.ToArray(), configPath).ConfigureAwait(false);
