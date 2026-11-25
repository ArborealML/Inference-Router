using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.Hosting;

namespace Arboreal.InferenceRouter.Cli;

internal static class AirBackgroundService
{
    private const string Unit = "arboreal-inference-router.service";

    internal static async Task<int> ServeAsync(string[] args)
    {
        string? config = null;
        string? listen = null;
        var foreground = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--foreground" when !foreground:
                    foreground = true;
                    break;
                case "-c" or "--config" when config is null && i + 1 < args.Length && args[i + 1] is { Length: > 0 } value && value[0] != '-':
                    config = args[++i];
                    break;
                case "-l" or "--listen" when listen is null && i + 1 < args.Length && args[i + 1] is { Length: > 0 } url && url[0] != '-':
                    listen = args[++i];
                    break;
                default:
                    Console.Error.WriteLine("usage: air serve [--foreground] [--config FILE] [--listen URL]");
                    return 2;
            }
        }
        if (foreground || !OperatingSystem.IsLinux())
        {
            string[] runArgs = listen is null ? [] : [$"--urls={listen}"];
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("NOTIFY_SOCKET") is not { Length: > 0 } address)
                return await ServerApp.RunAsync(runArgs, config).ConfigureAwait(false);
            await using var app = ServerApp.Build(runArgs, config);
            await app.StartAsync().ConfigureAwait(false);
            using (var notify = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified))
            {
                var endpoint = new UnixDomainSocketEndPoint(address[0] == '@' ? "\0" + address[1..] : address);
                notify.SendTo("READY=1"u8, SocketFlags.None, endpoint);
            }
            await app.WaitForShutdownAsync().ConfigureAwait(false);
            return 0;
        }
        var start = new ProcessStartInfo("/usr/bin/systemd-run") { UseShellExecute = false };
        foreach (var argument in new[] { "--user", "--collect", "--quiet", "--service-type=notify",
            "--expand-environment=no", "--property=TimeoutStartSec=90s", "--unit=" + Unit,
            "--working-directory=" + Environment.CurrentDirectory })
            start.ArgumentList.Add(argument);
        foreach (var name in new[] { "ARBOREAL_ROUTER_RELOAD_TOKEN", "ARBOREAL_ROUTER_TARGET_RESET_ENABLED", "ASPNETCORE_URLS" })
            if (Environment.GetEnvironmentVariable(name) is not null) start.ArgumentList.Add("--setenv=" + name);
        start.ArgumentList.Add("--");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate AIR executable.");
        start.ArgumentList.Add(executable);
        if (string.Equals(Path.GetFileName(executable), "dotnet", StringComparison.Ordinal))
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--foreground");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(Path.GetFullPath(RouterConfigLoader.ResolveFilePath(config)));
        if (listen is not null)
        {
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add(listen);
        }
        return await RunAsync(start, "AIR is ready. Use air stop to shut it down.").ConfigureAwait(false);
    }

    internal static Task<int> StopAsync(string[] args)
    {
        var force = args is ["--force"];
        if (!OperatingSystem.IsLinux() || (args.Length != 0 && !force))
        {
            Console.Error.WriteLine("usage: air stop [--force] (Linux background user service)");
            return Task.FromResult(2);
        }
        var stop = new ProcessStartInfo("/usr/bin/systemctl") { UseShellExecute = false };
        stop.ArgumentList.Add("--user");
        if (force)
        {
            stop.ArgumentList.Add("kill");
            stop.ArgumentList.Add("--signal=SIGKILL");
        }
        else
        {
            stop.ArgumentList.Add("stop");
        }
        stop.ArgumentList.Add(Unit);
        return RunAsync(stop, force ? "Sent SIGKILL to the AIR service." : "AIR stopped.");
    }

    private static async Task<int> RunAsync(ProcessStartInfo command, string success)
    {
        try
        {
            using var child = Process.Start(command) ?? throw new InvalidOperationException("Could not launch the service manager.");
            await child.WaitForExitAsync().ConfigureAwait(false);
            if (child.ExitCode == 0) Console.WriteLine(success);
            else Console.Error.WriteLine("Inspect the AIR service with journalctl --user -u " + Unit);
            return child.ExitCode;
        }
        catch (Win32Exception)
        {
            Console.Error.WriteLine("User systemd is unavailable. Use air serve --foreground.");
            return 1;
        }
    }
}
