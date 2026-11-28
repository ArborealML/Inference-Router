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

    /// <summary>Parse "class[:sub]" — e.g. "high:200". The grammar is exact (§6): a class name from the table,
    /// case-sensitive with no surrounding whitespace, optionally followed by ':' and a sub-priority written as
    /// one to three plain ASCII decimal digits, unsigned, with no leading zero unless it is exactly "0".
    /// Anything else returns false, including " high ", "high: 12", "high:+12" and "high:001".</summary>
    public static bool TryParse(string text, out ThreadPriority priority)
    {
        priority = Normal();
        if (string.IsNullOrEmpty(text)) return false;
        var span = text.AsSpan();
        byte sub = 0;
        var sep = span.IndexOf(':');
        if (sep >= 0)
        {
            if (!TryParseSub(span[(sep + 1)..], out sub)) return false;
            span = span[..sep];
        }
        if (!ThreadPriorityClass.TryParseClass(span.ToString(), out var cls)) return false;
        priority = new ThreadPriority(cls, sub);
        return true;
    }

    /// <summary>The sub-priority as it must be written. <c>byte.TryParse</c> alone also takes " 12", "+12" and
    /// "007", and <see cref="char.IsDigit"/> takes non-ASCII decimal digits: spellings nothing on the wire
    /// writes, which would make two callers that sent different bytes share a queue position by accident.
    /// So the digits are checked one at a time, and the value has to fit <see cref="MaxSubPriority"/>.</summary>
    private static bool TryParseSub(ReadOnlySpan<char> text, out byte sub)
    {
        sub = 0;
        if (text.Length is < 1 or > 3) return false;
        if (text.Length > 1 && text[0] == '0') return false;         // "007" is not how "7" is sent
        var value = 0;
        foreach (var c in text)
        {
            if (c is not (>= '0' and <= '9')) return false;          // ASCII digits only: no sign, no space
            value = (value * 10) + (c - '0');
        }
        if (value > MaxSubPriority) return false;
        sub = (byte)value;
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
public sealed record PrioritizedRequest(RouterTicket Ticket, ThreadPriority Priority, long Sequence) : IComparable<PrioritizedRequest>
{
    public int CompareTo(PrioritizedRequest? other)
    {
        if (other is null) return 1;
        var c = Priority.CompareTo(other.Priority);
        return c != 0 ? c : Sequence.CompareTo(other.Sequence);
    }

    public static bool operator <(PrioritizedRequest? left, PrioritizedRequest? right)
        => left is null ? right is not null : left.CompareTo(right) < 0;
    public static bool operator >(PrioritizedRequest? left, PrioritizedRequest? right)
        => left is not null && left.CompareTo(right) > 0;
    public static bool operator <=(PrioritizedRequest? left, PrioritizedRequest? right)
        => left is null || left.CompareTo(right) <= 0;
    public static bool operator >=(PrioritizedRequest? left, PrioritizedRequest? right)
        => left is null ? right is null : left.CompareTo(right) >= 0;
}
