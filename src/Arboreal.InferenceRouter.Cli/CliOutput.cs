using System.Globalization;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Cli;

internal enum ResponseView { Models, Status, Ticket, Switch, Event, Thread }

internal static class CliOutput
{
    internal static void Print(string body, ResponseView view, bool json, int status)
    {
        if (json)
        {
            Console.WriteLine(body);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"HTTP {status}: {Display(root)}");
                return;
            }
            if (status >= 400)
            {
                var error = Field(root, "error");
                var reason = error.ValueKind == JsonValueKind.Object
                    ? $"{Text(error, "code")}  {Text(error, "message")}"
                    : Display(root);
                Console.WriteLine($"HTTP {status}: {reason}");
                return;
            }
            switch (view)
            {
                case ResponseView.Models:
                    var models = Field(root, "data");
                    if (!Rows(models)) { Console.WriteLine("No loaded models."); break; }
                    Console.WriteLine("MODEL  ENGINE  CONTEXT (tokens)");
                    foreach (var model in models.EnumerateArray())
                        Console.WriteLine($"{Text(model, "id")}  {Text(model, "owned_by")}  {Text(model, "context_window")}");
                    break;
                case ResponseView.Status:
                    Status(root);
                    break;
                case ResponseView.Event:
                    var fields = root.EnumerateObject().Where(p => p.Name is not "Timestamp" and not "Kind" &&
                        p.Value.ValueKind != JsonValueKind.Null).Select(p => $"{Clean(p.Name)}={Display(p.Value)}");
                    Console.WriteLine($"{Text(root, "Timestamp")}  {Text(root, "Kind")}  {string.Join("  ", fields)}");
                    break;
                case ResponseView.Switch:
                    Console.WriteLine($"Switch accepted (HTTP {status})");
                    Detail(root);
                    break;
                default:
                    Detail(root);
                    break;
            }
        }
        catch (JsonException)
        {
            Console.WriteLine($"HTTP {status}: {Clean(body)}");
        }
    }

    private static void Status(JsonElement root)
    {
        Console.WriteLine("Engines");
        var engines = Field(root, "Engines");
        if (!Rows(engines)) Console.WriteLine("  None");
        else foreach (var engine in engines.EnumerateArray())
        {
            var health = Field(engine, "Healthy").ValueKind switch
            {
                JsonValueKind.True => "healthy", JsonValueKind.False => "unhealthy", _ => "unknown health",
            };
            Console.WriteLine($"  {Text(engine, "Engine")}  {health}  loaded: {Text(engine, "LoadedModels")}");
            Console.WriteLine($"    Profile: {Text(engine, "ActiveProfile")}  context: {Text(engine, "ActiveContextWindow")} tokens" +
                $"  slots: {Text(engine, "ActiveSlots")}  in flight: {Text(engine, "Inflight")}");
            if (Field(engine, "SwitchingToward").ValueKind == JsonValueKind.String)
                Console.WriteLine($"    Switching to {Text(engine, "SwitchingToward")}");
        }
        Console.WriteLine("Queues");
        var queues = Field(root, "QueueDepths");
        if (queues.ValueKind == JsonValueKind.Object && queues.EnumerateObject().Any())
            foreach (var queue in queues.EnumerateObject())
                Console.WriteLine($"  {Clean(queue.Name)}  {Display(queue.Value)} queued");
        else Console.WriteLine("  None");
        Console.WriteLine("Threads");
        var threads = Field(root, "Threads");
        if (!Rows(threads)) Console.WriteLine("  None");
        else foreach (var thread in threads.EnumerateArray())
            Console.WriteLine($"  {Text(thread, "ThreadId")}  priority: {Text(thread, "Priority")}  weight: {Text(thread, "Weight")}" +
                $"  enqueued: {Text(thread, "Enqueued")}  completed: {Text(thread, "Completed")}");
        Console.WriteLine($"Feed subscriber faults: {Text(root, "FeedSubscriberFaults")}");
    }

    private static void Detail(JsonElement root)
    {
        foreach (var field in root.EnumerateObject())
            if (field.Value.ValueKind != JsonValueKind.Null)
                Console.WriteLine($"{Clean(field.Name)}: {Display(field.Value)}");
    }

    private static bool Rows(JsonElement e) => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0;
    private static JsonElement Field(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement e, string key) => Display(Field(e, key));
    private static string Display(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "—",
        JsonValueKind.Array => Rows(e) ? string.Join(", ", e.EnumerateArray().Select(Display)) : "none",
        JsonValueKind.Object => string.Join(", ", e.EnumerateObject().Select(p => $"{Clean(p.Name)}={Display(p.Value)}")),
        _ => Clean(e.ToString()),
    };

    private static string Clean(string text) => !text.Any(char.IsControl) ? text : string.Concat(text.Select(c =>
        char.IsControl(c) ? "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) : c.ToString()));
}
