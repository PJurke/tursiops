using Tursiops.Core.Format;

namespace Tursiops.Core.Storage;

/// <summary>Reference to one stored entity, e.g. one person (E-23: internal integer key).</summary>
public readonly record struct EntityRef(string Entity, long Id);

/// <summary>The current value of an attribute. <see cref="ValidAt"/> is set for attributes with <c>@time</c> or <c>@history</c>.</summary>
public sealed record StoredValue(object? Value, DateTimeOffset? ValidAt);

/// <summary>The current value of an attribute for one entity, as read for all entities at once (E-33).</summary>
public sealed record EntityValue(EntityRef Entity, StoredValue Value);

/// <summary>A value of a time-dependent attribute and the time it was valid.</summary>
public sealed record TimedValue(object? Value, DateTimeOffset ValidAt);

/// <summary>
/// When a value of an attribute with <c>@time</c> or <c>@history</c> was valid (E-27).
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

/// <summary>Checks values against the base type the admin declared (E-41). Values are never converted.</summary>
public static class ValueTypes
{
    /// <summary>
    /// The base type a value has, or <c>null</c> for a missing value.
    /// Text stays text, even if it looks like a number.
    /// </summary>
    /// <exception cref="ArgumentException">The value has a .NET type Tursiops does not accept.</exception>
    public static BaseType? Of(object? value) => value switch
    {
        null or DBNull => null,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => BaseType.Number,
        string => BaseType.Text,
        DateTimeOffset or DateOnly => BaseType.Date,
        bool => BaseType.Bi,
        // E-42: a DateTime would be stored without its time zone, so it could not be read back as meant.
        DateTime => throw new ArgumentException(
            "Values of type DateTime are not supported, because they would be stored without a time zone. " +
            "Use DateTimeOffset for a point in time, or DateOnly if only the day is known.", nameof(value)),
        _ => throw new ArgumentException(
            $"Values of type {value.GetType().Name} are not supported. " +
            "Use a number, text, a date (DateTimeOffset, DateOnly) or a boolean.", nameof(value)),
    };

    /// <summary>Rejects a value that does not fit the declared type of <paramref name="entity"/>.<paramref name="attribute"/>.</summary>
    /// <exception cref="ArgumentException">The value has another type, or it is null for a <c>bi</c> attribute.</exception>
    public static void Check(string entity, string attribute, BaseType declared, object? value)
    {
        var type = Of(value);
        var name = BaseTypes.Name(declared);

        if (type is null)
        {
            if (declared == BaseType.Bi)
                throw new ArgumentException(
                    $"'{entity}.{attribute}' is declared as '{name}' and only takes true or false, not null. " +
                    "Leave it unset if the value is not known.", nameof(value));
            return;
        }

        if (type != declared)
            throw new ArgumentException(
                $"'{entity}.{attribute}' is declared as '{name}', but the value {Show(value)} is {Article(type.Value)}. " +
                $"Tursiops does not convert values; pass {Article(declared)}.", nameof(value));
    }

    private static string Article(BaseType type) => type switch
    {
        BaseType.Number => "a number",
        BaseType.Text => "text",
        BaseType.Date => "a date",
        BaseType.Bi => "true or false",
        _ => type.ToString(),
    };

    private static string Show(object? value) => value switch
    {
        string text => $"\"{text}\"",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "null",
    };
}
