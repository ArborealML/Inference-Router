using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Server;

public static partial class ServerApp
{
    /// <summary>Run the protected in-process host when selected. Both executables retain their own inherited listener.</summary>
    public static async Task<int?> TryRunCommissionedAsync(string[] args)
    {
        string? directory = null;
        var hostArgs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--commissioned" || arg.StartsWith("--commissioned=", StringComparison.Ordinal))
            {
                if (directory is not null) return Usage();
                if (arg == "--commissioned" && (i + 1 == args.Length || IsOption(args[i + 1]))) return Usage();
                directory = arg == "--commissioned" ? args[++i] : arg["--commissioned=".Length..];
                if (string.IsNullOrWhiteSpace(directory)) return Usage();
            }
            else hostArgs.Add(arg);
        }

        if (directory is null) return null;
        if (hostArgs.Exists(static arg => arg is "--config" or "-c" || arg.StartsWith("--config=", StringComparison.Ordinal)))
            return Usage();
        for (var i = 0; i < hostArgs.Count; i++)
        {
            if (hostArgs[i] is not ("--listen" or "-l")) continue;
            if (i + 1 == hostArgs.Count || IsOption(hostArgs[i + 1])) return Usage();
            hostArgs[i] = "--urls=" + hostArgs[i + 1];
            hostArgs.RemoveAt(i + 1);
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += Cancel;
        try { return await CommissionedServer.RunAsync([.. hostArgs], directory, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error) ||
            error is InvalidOperationException or RouterConfigException or PlatformNotSupportedException or
                DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine("The commissioned server could not bind or retain the protected serving authority.");
            return 1;
        }
        finally { Console.CancelKeyPress -= Cancel; }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        }

        static bool IsOption(string arg) => arg.Length != 0 && arg[0] == '-';

        static int Usage()
        {
            Console.Error.WriteLine("Usage: serve --commissioned <protected-authority-directory>.");
            return 2;
        }
    }
}
