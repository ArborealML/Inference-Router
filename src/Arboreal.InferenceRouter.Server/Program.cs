using Arboreal.InferenceRouter.Server;

var args0 = args;
if (args0 is ["serve", ..] or [])
{
    var configArg = args0.GetCommandLineValue("--config", "-c");
    ServerApp.Run(args0.Length == 0 ? [] : args0.Where(a => !a.StartsWith('-') && a != "serve").ToArray(), configArg);
    return;
}
else
{
    Console.Error.WriteLine("usage: Arboreal.InferenceRouter.Server [serve] [--config|-c path]");
    return;
}

file static class ArgExtensions
{
    public static string? GetCommandLineValue(this string[] args, params string[] keys)
    {
        for (var i = 0; i < args.Length; i++)
            foreach (var k in keys)
                if (args[i] == k && i + 1 < args.Length)
                    return args[i + 1];
        return null;
    }
}
