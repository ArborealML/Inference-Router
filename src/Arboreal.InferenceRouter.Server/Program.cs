using Arboreal.InferenceRouter.Server;

// usage: Arboreal.InferenceRouter.Server [serve] [--config|-c path] [--urls http://…]
// "serve" is optional; remaining args pass through to the ASP.NET host (e.g. --urls).
var argv = new List<string>(Environment.GetCommandLineArgs()[1..]);
if (!ServerApp.TryReadConfigOption(argv.ToArray(), out var configArg))
    return 2;
argv.RemoveAll(a => a is "serve" or "--config" or "-c" || a == configArg);
await ServerApp.RunAsync(argv.ToArray(), configArg).ConfigureAwait(false);
return 0;
