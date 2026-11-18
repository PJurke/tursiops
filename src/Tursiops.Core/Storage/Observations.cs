namespace Tursiops.Core.Storage;

// Observation (E-31, E-32): how each attribute is used in one waking phase, from one sleep to the next.
// Observing never changes anything; the sleep reads these counts to decide on layouts (E-04, E-30).

/// <summary>What is counted per attribute (E-31). The names are stored in the database file.</summary>
internal enum Counter
{
    Write,
    LateWrite,
    ReadCurrent,
    ReadCurrentAll,
    ReadHistory,
}

internal static class Counters
{
    public static string Name(Counter counter) => counter switch
    {
        Counter.Write => "write",
        Counter.LateWrite => "late_write",
        Counter.ReadCurrent => "read_current",
        Counter.ReadCurrentAll => "read_current_all",
        Counter.ReadHistory => "read_history",
        _ => throw new ArgumentOutOfRangeException(nameof(counter), counter, null),
    };
}

/// <summary>How one attribute was used in one waking phase (E-31).</summary>
/// <param name="Writes">Values set, including late ones.</param>
/// <param name="LateWrites">Values set that were older than the current value (only with <c>@time</c> or <c>@history</c>).</param>
/// <param name="CurrentReads">Current value read for one entity.</param>
/// <param name="AllCurrentReads">Current values read for all entities at once, counted once per read (E-33).</param>
/// <param name="HistoryReads">History read for one entity.</param>
public sealed record AttributeObservations(
    long Writes,
    long LateWrites,
    long CurrentReads,
    long AllCurrentReads,
    long HistoryReads)
{
    /// <summary>Nothing observed.</summary>
    public static AttributeObservations None { get; } = new(0, 0, 0, 0, 0);

    internal static AttributeObservations From(IReadOnlyDictionary<Counter, long> counts)
    {
        long Get(Counter counter) => counts.GetValueOrDefault(counter);
        return new(
            Get(Counter.Write), Get(Counter.LateWrite), Get(Counter.ReadCurrent), Get(Counter.ReadCurrentAll), Get(Counter.ReadHistory));
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
