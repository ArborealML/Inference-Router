using System.Globalization;
using System.Text;
using Arboreal.InferenceRouter.Monitoring;

namespace Arboreal.InferenceRouter.Cli;

internal sealed class AirTop(RouterMonitor monitor)
{
    private string? _queue;
    private TopFrame? _frame;
    private long _revision = -1;
    private long _second = -1;

    internal static async Task<int> RunAsync(string[] args, bool json)
    {
        string server = Program.DefaultServer;
        string? ticket = null;
        for (int i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag is not ("-s" or "--server" or "--ticket") || ++i >= args.Length) return Usage();
            var value = args[i];
            if (string.IsNullOrWhiteSpace(value) || value[0] == '-') return Usage();
            if (flag == "--ticket") ticket = value;
            else server = value;
        }
        if (json || !Uri.TryCreate(server.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            ticket is "." or "..") return Usage();
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("air top requires an interactive terminal. Use air status for redirected output.");
            return 2;
        }

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { BaseAddress = uri };
        using var client = new RouterMonitor(http);
        using var lifetime = new CancellationTokenSource();
        client.Follow(ticket);
        var top = new AirTop(client);
        var work = client.RunAsync(lifetime.Token);
        try
        {
            TopScreen.Run(top.Read, top.Open, top.Back);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await work.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: air top [-s|--server URL] [--ticket ID]");
        return 2;
    }

    internal void Open(TopRow row)
    {
        if (row.Ticket is not null) monitor.Follow(row.Ticket);
        else if (row.Queue is not null) { _queue = row.Queue; _frame = null; }
    }

    internal void Back()
    {
        if (monitor.Snapshot().TicketId is not null) monitor.Follow(null);
        else { _queue = null; _frame = null; }
    }

    internal TopFrame Read()
    {
        var state = monitor.Snapshot();
        var now = DateTimeOffset.UtcNow;
        long second = now.ToUnixTimeSeconds();
        if (_frame is not null && _revision == state.Revision && _second == second) return _frame;
        _revision = state.Revision;
        _second = second;
        var age = state.SnapshotTime is { } t
            ? FormattableString.Invariant($"{Math.Max(0, second - t.ToUnixTimeSeconds())}s") : "unavailable";
        var status = $"feed {state.Feed} | status {age}" + (state.StatusError is null ? "" : $" stale ({state.StatusError})");
        var rows = new List<TopRow>();
        if (state.TicketId is { } id)
        {
            var item = state.Ticket;
            var phase = item?.State ?? state.TicketError ?? "looking up";
            rows.Add(new("followed", "Ticket", $"{Text(id)}  {Text(phase)}",
                $"Following {Text(id)}\nState {Text(phase)}\nModel {Text(item?.Model)}   Engine {Text(item?.Engine)}" +
                $"\nThread {Text(item?.ThreadId)}\nError {Text(item?.ErrorCode)} {Text(item?.Error, 1600)}"));
        }
        if (state.Gauges is { } gauges)
        {
            foreach (var engine in gauges.Engines.OrderByDescending(e => e.Inflight > 0 || !e.Healthy || e.SwitchingToward is not null)
                         .ThenBy(e => e.Engine, StringComparer.Ordinal))
            {
                var loaded = engine.LoadedModels.Length == 0 ? "none" : string.Join(", ", engine.LoadedModels.Take(16).Select(m => Text(m)));
                if (engine.LoadedModels.Length > 16) loaded += " …";
                var label = engine.Healthy ? "healthy" : "unhealthy";
                var summary = FormattableString.Invariant($"{Text(engine.Engine)}  {label}  inflight {engine.Inflight}  loaded {loaded}");
                if (engine.SwitchingToward is not null) summary += "  target " + Text(engine.SwitchingToward);
                rows.Add(new("engine/" + engine.Engine, "Engine", summary,
                    summary + $"\nSwitch target {Text(engine.SwitchingToward)}\nReported profile {Text(engine.ActiveProfile)}" +
                    $"   Context {Count(engine.ActiveContextWindow)}   Slots {Count(engine.ActiveSlots)}"));
            }
            foreach (var (model, depth) in gauges.QueueDepths.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
                rows.Add(new("queue/" + model, "Queue", FormattableString.Invariant($"{Text(model)}  {depth} queued"),
                    "Global queue depth from status. Enter filters observed activity for " + Text(model), Queue: model));
        }
        var scope = _queue is null ? "all models" : Text(_queue);
        rows.Add(new("history", "History", "Observed history · " + scope,
            FormattableString.Invariant($"Retained {state.History.Length} events and boundaries; evicted {state.Evicted}. Queue depths come from status.\nEnter follows a ticket or filters a queue. Esc returns. Tab focuses details. q quits.")));
        foreach (var entry in state.History.Reverse())
        {
            string key = entry.Number.ToString(CultureInfo.InvariantCulture);
            if (entry.Event is not { } e)
            {
                rows.Add(new("event/" + key, "History", entry.Boundary ?? "", entry.Boundary ?? ""));
                continue;
            }
            if (state.TicketId is { } selected && !string.Equals(selected, e.TicketId, StringComparison.Ordinal)) continue;
            if (state.TicketId is null && _queue is not null && !string.Equals(_queue, e.Model, StringComparison.Ordinal)) continue;
            string when = e.Timestamp.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string summary = $"{when} {Text(e.Kind)}  {Text(e.TicketId)}  {Text(e.Model)}";
            rows.Add(new("event/" + key, "Activity", summary,
                FormattableString.Invariant($"{e.Timestamp:O}  {Text(e.Kind)}\nTicket {Text(e.TicketId)}   Thread {Text(e.ThreadId)}\nEngine {Text(e.Engine)}   Model {Text(e.Model)}\nReason {Text(e.Reason, 1600)}"),
                Ticket: e.TicketId));
        }
        _frame = new("AIR | " + status + " | Enter open · Esc back · q quit", rows.ToArray());
        return _frame;
    }

    private static string Count(long value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "unreported";

    private static string Text(string? value, int max = 160)
    {
        if (string.IsNullOrEmpty(value)) return "—";
        var result = new StringBuilder(Math.Min(max + 1, value.Length));
        foreach (var rune in value.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > max) { result.Append('…'); break; }
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) result.Append(' ');
            else result.Append(rune);
        }
        return result.ToString();
    }
}
