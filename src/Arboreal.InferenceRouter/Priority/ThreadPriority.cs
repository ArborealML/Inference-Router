namespace Arboreal.InferenceRouter;

/// <summary>Priority class ordering for threads and requests. Larger code = higher priority.
/// The encoding is open-ended: new classes may be inserted by number without breaking wire compat.</summary>
public static class ThreadPriorityClass
{
    public const byte Background = 1;
    public const byte Low = 2;
    public const byte Normal = 3;
    public const byte High = 4;
    public const byte Interactive = 5;

    public static bool TryParseClass(string name, out byte value)
    {
        value = name switch
        {
            "background" => Background,
            "low" => Low,
            "normal" => Normal,
            "high" => High,
            "interactive" => Interactive,
            _ => 0,
        };
        return value != 0;
    }

    public static string ClassName(byte value) => value switch
    {
        Background => "background",
        Low => "low",
        Normal => "normal",
        High => "high",
        Interactive => "interactive",
        _ => value >= Interactive ? "interactive+" : "custom",
    };
}

/// <summary>Immutable, comparable priority: class first, then sub-priority 0..255 (larger wins),
/// then enqueue sequence (FIFO) as the final tie-break.</summary>
public readonly struct ThreadPriority : IEquatable<ThreadPriority>, IComparable<ThreadPriority>
{
    public const byte MaxSubPriority = 255;

    public ThreadPriority(byte classCode, byte sub = 0)
    {
        ArgumentOutOfRangeException.ThrowIfZero(classCode);
        Class = classCode;
        Sub = sub;
    }

    public static ThreadPriority Interactive(byte sub = 0) => new(ThreadPriorityClass.Interactive, sub);
    public static ThreadPriority High(byte sub = 0) => new(ThreadPriorityClass.High, sub);
    public static ThreadPriority Normal(byte sub = 0) => new(ThreadPriorityClass.Normal, sub);
    public static ThreadPriority Low(byte sub = 0) => new(ThreadPriorityClass.Low, sub);
    public static ThreadPriority Background(byte sub = 0) => new(ThreadPriorityClass.Background, sub);

    /// <summary>Parse "class[:sub]" — e.g. "high:200". Returns false on malformed input.</summary>
    public static bool TryParse(string text, out ThreadPriority priority)
    {
        priority = Normal();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var span = text.AsSpan().Trim();
        var sep = span.IndexOf(':');
        byte sub = 0;
        if (sep >= 0)
        {
            if (!byte.TryParse(span[(sep + 1)..], out sub)) return false;
            span = span[..sep];
        }
        if (!ThreadPriorityClass.TryParseClass(span.ToString(), out var cls)) return false;
        priority = new ThreadPriority(cls, sub);
        return true;
    }

    public byte Class { get; }
    public byte Sub { get; }

    public int CompareTo(ThreadPriority other)
    {
        var c = Class.CompareTo(other.Class);
        return c != 0 ? c : Sub.CompareTo(other.Sub);
    }

    public override string ToString() => $"{ThreadPriorityClass.ClassName(Class)}:{Sub}";
    public bool Equals(ThreadPriority other) => Class == other.Class && Sub == other.Sub;
    public override bool Equals(object? obj) => obj is ThreadPriority p && Equals(p);
    public override int GetHashCode() => (Class << 8) | Sub;
    public static bool operator ==(ThreadPriority a, ThreadPriority b) => a.Equals(b);
    public static bool operator !=(ThreadPriority a, ThreadPriority b) => !a.Equals(b);
    public static bool operator <(ThreadPriority a, ThreadPriority b) => a.CompareTo(b) < 0;
    public static bool operator >(ThreadPriority a, ThreadPriority b) => a.CompareTo(b) > 0;
    public static bool operator <=(ThreadPriority a, ThreadPriority b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ThreadPriority a, ThreadPriority b) => a.CompareTo(b) >= 0;
}

/// <summary>Router-internal priority record carrying the sequence tie-break.</summary>
public sealed record PrioritizedRequest(RouterTicket Ticket, ThreadPriority Priority, long Sequence)
{
    public int CompareTo(PrioritizedRequest other)
    {
        var c = Priority.CompareTo(other.Priority);
        return c != 0 ? c : Sequence.CompareTo(other.Sequence);
    }
}
