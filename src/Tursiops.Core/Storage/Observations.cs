namespace Tursiops.Core.Storage;

// Observation (E-31, E-32): how each attribute is used in one waking phase, from one sleep to the next.
// Observing never changes anything; the sleep reads these counts to decide on layouts (E-04, E-30).

/// <summary>Technical type of a value, recognized when the value comes in (E-13).</summary>
public enum TechnicalType
{
    Number,
    Text,
    Date,
    Boolean,
}

/// <summary>Which technical types Tursiops accepts and how it recognizes them.</summary>
public static class TechnicalTypes
{
    /// <summary>
    /// The technical type of a value, or <c>null</c> for a missing value.
    /// The type has to be recognized here: SQLite returns booleans as numbers and dates as text.
    /// Text stays text, even if it looks like a number.
    /// </summary>
    /// <exception cref="ArgumentException">The value has a type Tursiops does not accept.</exception>
    public static TechnicalType? Of(object? value) => value switch
    {
        null or DBNull => null,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => TechnicalType.Number,
        string => TechnicalType.Text,
        DateTimeOffset or DateTime or DateOnly => TechnicalType.Date,
        bool => TechnicalType.Boolean,
        _ => throw new ArgumentException(
            $"Values of type {value.GetType().Name} are not supported. " +
            "Use a number, text, a date (DateTimeOffset, DateTime, DateOnly) or a boolean.", nameof(value)),
    };
}

/// <summary>What is counted per attribute (E-31). The names are stored in the database file.</summary>
internal enum Counter
{
    Write,
    LateWrite,
    ReadCurrent,
    ReadHistory,
    TypeNumber,
    TypeText,
    TypeDate,
    TypeBoolean,
}

internal static class Counters
{
    public static string Name(Counter counter) => counter switch
    {
        Counter.Write => "write",
        Counter.LateWrite => "late_write",
        Counter.ReadCurrent => "read_current",
        Counter.ReadHistory => "read_history",
        Counter.TypeNumber => "type_number",
        Counter.TypeText => "type_text",
        Counter.TypeDate => "type_date",
        Counter.TypeBoolean => "type_boolean",
        _ => throw new ArgumentOutOfRangeException(nameof(counter), counter, null),
    };

    public static Counter For(TechnicalType type) => type switch
    {
        TechnicalType.Number => Counter.TypeNumber,
        TechnicalType.Text => Counter.TypeText,
        TechnicalType.Date => Counter.TypeDate,
        TechnicalType.Boolean => Counter.TypeBoolean,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

/// <summary>How one attribute was used in one waking phase (E-31).</summary>
/// <param name="Writes">Values set, including late ones.</param>
/// <param name="LateWrites">Values set that were older than the current value (only with <c>@time</c> or <c>@history</c>).</param>
/// <param name="CurrentReads">Current value read for one entity.</param>
/// <param name="HistoryReads">History read for one entity.</param>
/// <param name="Numbers">Values set that were numbers.</param>
/// <param name="Texts">Values set that were text.</param>
/// <param name="Dates">Values set that were dates.</param>
/// <param name="Booleans">Values set that were booleans.</param>
public sealed record AttributeObservations(
    long Writes,
    long LateWrites,
    long CurrentReads,
    long HistoryReads,
    long Numbers,
    long Texts,
    long Dates,
    long Booleans)
{
    /// <summary>Nothing observed.</summary>
    public static AttributeObservations None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    internal static AttributeObservations From(IReadOnlyDictionary<Counter, long> counts)
    {
        long Get(Counter counter) => counts.GetValueOrDefault(counter);
        return new(
            Get(Counter.Write), Get(Counter.LateWrite), Get(Counter.ReadCurrent), Get(Counter.ReadHistory),
            Get(Counter.TypeNumber), Get(Counter.TypeText), Get(Counter.TypeDate), Get(Counter.TypeBoolean));
    }
}

/// <summary>Counters of the current phase that are not yet written to the database file (E-32).</summary>
internal sealed class PendingObservations
{
    private readonly Dictionary<(string Entity, string Attribute, Counter Counter), long> _counts = [];

    public bool IsEmpty => _counts.Count == 0;

    public void Add(string entity, string attribute, Counter counter) =>
        _counts[(entity, attribute, counter)] = _counts.GetValueOrDefault((entity, attribute, counter)) + 1;

    public long Get(string entity, string attribute, Counter counter) =>
        _counts.GetValueOrDefault((entity, attribute, counter));

    public IEnumerable<(string Entity, string Attribute, Counter Counter, long Count)> All() =>
        _counts.Select(c => (c.Key.Entity, c.Key.Attribute, c.Key.Counter, c.Value));

    public void Clear() => _counts.Clear();
}
