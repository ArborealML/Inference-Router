using Arboreal.InferenceRouter.Server;

// usage: Arboreal.InferenceRouter.Server [serve] [--config|-c path] [--urls http://…]
// "serve" is optional; remaining args pass through to the ASP.NET host (e.g. --urls).
var argv = new List<string>(Environment.GetCommandLineArgs()[1..]);
var configArg = GetCommandLineValue(argv, "--config", "-c");
argv.RemoveAll(a => a is "serve" or "--config" or "-c" || a == configArg);
await ServerApp.RunAsync(argv.ToArray(), configArg).ConfigureAwait(false);
return;

static string? GetCommandLineValue(List<string> argvList, params string[] keys)
{
    for (var i = 0; i < argvList.Count; i++)
        foreach (var k in keys)
            if (argvList[i] == k && i + 1 < argvList.Count)
                return argvList[i + 1];
    return null;
}
