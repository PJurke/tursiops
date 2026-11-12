namespace Tursiops.Core.Storage;

/// <summary>Reference to one stored entity, e.g. one person (E-23: internal integer key).</summary>
public readonly record struct EntityRef(string Entity, long Id);

/// <summary>The current value of an attribute. <see cref="ValidAt"/> is set for attributes with <c>@time</c>.</summary>
public sealed record StoredValue(object? Value, DateTimeOffset? ValidAt);

/// <summary>A value of an attribute with <c>@time</c> and the time it was valid.</summary>
public sealed record TimedValue(object? Value, DateTimeOffset ValidAt);

/// <summary>
/// When a value of an attribute with <c>@time</c> was valid (E-22).
/// Always explicit: either a given point in time or <see cref="Now"/>, filled in by the database.
/// </summary>
public readonly struct ValueTime
{
    private enum Kind { Missing, Now, At }

    private readonly Kind _kind;
    private readonly DateTimeOffset _at;

    private ValueTime(Kind kind, DateTimeOffset at)
    {
        _kind = kind;
        _at = at;
    }

    /// <summary>The database fills in the current time when the value is stored.</summary>
    public static ValueTime Now => new(Kind.Now, default);

    /// <summary>The value was valid at the given point in time.</summary>
    public static ValueTime At(DateTimeOffset at) => new(Kind.At, at);

    internal DateTimeOffset Resolve(TimeProvider time) => _kind switch
    {
        Kind.Now => time.GetUtcNow(),
        Kind.At => _at,
        _ => throw new ArgumentException("No time given. Use ValueTime.Now or ValueTime.At(...)."),
    };

    public override string ToString() => _kind switch
    {
        Kind.Now => "now",
        Kind.At => _at.ToString("O"),
        _ => "missing",
    };
}
