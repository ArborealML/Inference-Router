using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Arboreal.InferenceRouter.Monitoring;

/// <summary>Current reported gauges. The monitor has no use for the status document's thread registry.</summary>
public sealed record MonitorGauges
{
    public required ImmutableArray<MonitorEngine> Engines { get; init; }
    public required ImmutableDictionary<string, int> QueueDepths { get; init; }
}

public sealed record MonitorEngine
{
    public required string Engine { get; init; }
    public required bool Healthy { get; init; }
    public required ImmutableArray<string> LoadedModels { get; init; }
    public required int Inflight { get; init; }
    public required string? SwitchingToward { get; init; }
    public string? ActiveProfile { get; init; }
    public long ActiveContextWindow { get; init; }
    public int ActiveSlots { get; init; }
}

public sealed record MonitorEntry(long Number, RouterEvent? Event, string? Boundary);
public sealed record MonitorSnapshot(long Revision, MonitorGauges? Gauges, DateTimeOffset? SnapshotTime,
    string? StatusError, string Feed, MonitorEntry[] History, long Evicted,
    string? TicketId, TicketSnapshot? Ticket, string? TicketError);

[JsonSerializable(typeof(MonitorGauges))]
[JsonSerializable(typeof(RouterEvent))]
[JsonSerializable(typeof(TicketSnapshot))]
internal sealed partial class MonitorJson : JsonSerializerContext;

/// <summary>One read-only client: serial gauge polls, one global feed, and the exact ticket the caller selected.</summary>
public sealed class RouterMonitor(HttpClient http) : IDisposable
{
    internal const int HistoryCapacity = 256;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refresh = new(0, 1);
    private readonly Queue<MonitorEntry> _history = new();
    private long _revision, _number, _evicted, _cursor;
    private MonitorGauges? _gauges;
    private DateTimeOffset? _snapshotTime;
    private string? _statusError, _ticketId, _ticketError;
    private TicketSnapshot? _ticket;
    private string _feed = "connecting";

    /// <summary>The feed comparison key for a full model name from status. Reporting keys can collide.</summary>
    public static string ReportedModel(string model) => WireText.Ident(model);

    public MonitorSnapshot Snapshot()
    {
        lock (_gate) return new(_revision, _gauges, _snapshotTime, _statusError, _feed, _history.ToArray(),
            _evicted, _ticketId, _ticket, _ticketError);
    }

    public void Follow(string? id)
    {
        lock (_gate)
        {
            if (string.Equals(id, _ticketId, StringComparison.Ordinal)) return;
            _ticketId = id;
            _ticket = null;
            _ticketError = null;
            _revision++;
            WakePoll();
        }
    }

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(PollAsync(ct), ReadFeedAsync(ct));

    private void WakePoll()
    {
        // Every writer holds the same gate. The reader can only decrease this count.
        if (_refresh.CurrentCount == 0) _refresh.Release();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct).ConfigureAwait(false);
            await _refresh.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var gauges = await GetAsync("admin/status", MonitorJson.Default.MonitorGauges, 1_048_576, ct).ConfigureAwait(false);
            if (gauges.Engines.IsDefault || gauges.QueueDepths is null ||
                gauges.Engines.Any(e => e is null || e.Engine is null || e.LoadedModels.IsDefault))
                throw new JsonException("Invalid engine gauges.");
            lock (_gate) { _gauges = gauges; _snapshotTime = DateTimeOffset.UtcNow; _statusError = null; _revision++; }
        }
        catch (Exception e) when (Recoverable(e, ct))
        {
            lock (_gate) { _statusError = ReadError(e); _revision++; }
        }

        string? id;
        lock (_gate) id = _ticketId;
        if (id is null) return;
        TicketSnapshot? found = null;
        string? error = null;
        bool unavailable = false;
        try
        {
            var item = await GetAsync("admin/tickets/" + Uri.EscapeDataString(id),
                MonitorJson.Default.TicketSnapshot, 65_536, ct).ConfigureAwait(false);
            if (!string.Equals(id, item.Id, StringComparison.Ordinal))
                throw new JsonException("Ticket identity mismatch.");
            found = item;
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            error = "unknown or expired";
        }
        catch (Exception e) when (Recoverable(e, ct)) { error = ReadError(e); unavailable = true; }
        lock (_gate)
        {
            if (!string.Equals(id, _ticketId, StringComparison.Ordinal)) return;
            if (!unavailable) _ticket = found;
            _ticketError = error;
            _revision++;
        }
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, int limit, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(limit, deadline.Token).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, type) ?? throw new JsonException("Empty monitor response.");
    }

    private static bool Recoverable(Exception e, CancellationToken ct) => !ct.IsCancellationRequested &&
        e is HttpRequestException or IOException or JsonException or OperationCanceledException;

    private static string ReadError(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } code } => FormattableString.Invariant($"HTTP {(int)code}"),
        JsonException => "invalid response",
        OperationCanceledException => "read timed out",
        _ => "read failed",
    };

    private async Task ReadFeedAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        bool reconnect = false;
        while (!ct.IsCancellationRequested)
        {
            bool opened = false;
            long since;
            lock (_gate) since = _cursor;
            try
            {
                var url = new Uri(FormattableString.Invariant($"admin/events?since={since}"), UriKind.Relative);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, handshake.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Expected an event stream.");
                opened = true;
                lock (_gate)
                {
                    _feed = "connected";
                    if (reconnect)
                    {
                        Add(null, "Reconnected; history continuity uncertain");
                        WakePoll();
                    }
                    _revision++;
                }
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                bool firstAfterReconnect = reconnect;
                await foreach (var e in MonitorEventReader.ReadAsync(stream, ct).ConfigureAwait(false))
                {
                    Observe(e, firstAfterReconnect);
                    firstAfterReconnect = false;
                    delay = TimeSpan.FromMilliseconds(250);
                }
                throw new EndOfStreamException();
            }
            catch (Exception e) when (Recoverable(e, ct))
            {
                lock (_gate)
                {
                    _feed = "disconnected (" + ReadError(e) + ")";
                    if (!reconnect || opened)
                        Add(null, "Connection lost; history continuity uncertain");
                    _revision++;
                    WakePoll();
                }
                reconnect = true;
            }
            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(5000, delay.TotalMilliseconds * 2));
        }
    }

    internal void Observe(RouterEvent item, bool firstAfterReconnect)
    {
        lock (_gate)
        {
            if (item.Seq <= 0 || item.Kind is null) throw new JsonException("Invalid feed event.");
            if (item.Seq <= _cursor)
            {
                // The server cursor is exclusive. A non-advancing first record may belong to a
                // restarted listener even if its complete payload matches an old record.
                if (!firstAfterReconnect) return;
                Add(null, "Event sequence reset; history incomplete");
                WakePoll();
            }
            else if (item.Seq - _cursor > 1)
            {
                Add(null, "Event gap; history incomplete");
                WakePoll();
            }
            _cursor = item.Seq;
            Add(item, null);
            _revision++;
        }
    }

    private void Add(RouterEvent? item, string? boundary)
    {
        if (_history.Count == HistoryCapacity) { _history.Dequeue(); _evicted++; }
        _history.Enqueue(new(++_number, item, boundary));
    }

    public void Dispose() => _refresh.Dispose();
}
