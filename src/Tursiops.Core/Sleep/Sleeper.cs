using System.Globalization;
using Tursiops.Core.Format;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Core.Sleep;

/// <summary>What one sleep evaluated, decided and logged.</summary>
/// <param name="Phase">The waking phase that was evaluated.</param>
/// <param name="NextPhase">The waking phase that began with this sleep.</param>
public sealed record SleepReport(
    long Phase, long NextPhase, IReadOnlyList<LayoutDecision> Decisions, IReadOnlyList<SleepLogEntry> Entries);

/// <summary>
/// The sleep (E-04): triggered by hand, it evaluates the waking phase, decides on layouts (E-34),
/// changes them and logs every decision in readable English (E-35).
/// </summary>
public static class Sleeper
{
    /// <summary>
    /// Runs one sleep. Layout changes, log entries and the start of the next phase happen in one transaction:
    /// if anything fails, the database is left as before and the exception is passed on.
    /// </summary>
    public static SleepReport Run(SqliteStore store, CostModel? costs = null, LayoutRule? rule = null)
    {
        costs ??= SqliteCosts.Measured;
        rule ??= LayoutRule.Default;

        store.FlushObservations();
        var phase = store.Phase;
        var decisions = new List<LayoutDecision>();
        var entries = new List<SleepLogEntry>();

        foreach (var entity in store.Structure.Entities)
        {
            var entityCount = store.CountEntities(entity.Name);
            foreach (var attribute in entity.Attributes)
            {
                var observed = store.GetObservations(entity.Name, attribute.Name);
                if (DescribeTypes(entity.Name, attribute.Name, observed) is { } typeEntry)
                    entries.Add(typeEntry);

                if (AttributeLayouts.AllowedFor(attribute.Time).Count < 2)
                    continue;

                var decision = LayoutDecider.Decide(attribute, entity.Name, observed, entityCount,
                    store.CountValues(entity.Name, attribute.Name), costs, rule);
                decisions.Add(decision);
                entries.Add(new SleepLogEntry(Kind(decision.Outcome), entity.Name, attribute.Name, decision.Describe()));
            }
        }

        var started = store.PhaseStartedAt(phase);
        entries.Insert(0, new SleepLogEntry(SleepLogKind.Sleep, null, null,
            $"Sleep after waking phase {phase}, which began {started.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}. " +
            $"{Count(decisions.Count(d => d.Outcome == LayoutOutcome.Changed), "layout")} changed, " +
            $"{Count(decisions.Count(d => d.Outcome != LayoutOutcome.Changed), "layout")} kept."));

        long next = 0;
        store.InOneTransaction(() =>
        {
            foreach (var decision in decisions.Where(d => d.Outcome == LayoutOutcome.Changed))
                store.ChangeLayout(decision.Entity, decision.Attribute, decision.Result);
            store.WriteSleepLog(phase, entries);
            next = store.StartNewPhase();
        });

        return new SleepReport(phase, next, decisions, entries);
    }

    private static SleepLogKind Kind(LayoutOutcome outcome) => outcome switch
    {
        LayoutOutcome.NotEnoughData => SleepLogKind.NotEnoughData,
        LayoutOutcome.Kept => SleepLogKind.LayoutKept,
        LayoutOutcome.Changed => SleepLogKind.LayoutChanged,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    // E-13: which technical types the values of this phase had. Nothing is rejected or converted,
    // a value of another type than most is named as a possible mistake.
    private static SleepLogEntry? DescribeTypes(string entity, string attribute, AttributeObservations observed)
    {
        var types = new (TechnicalType Type, long Count)[]
            {
                (TechnicalType.Number, observed.Numbers),
                (TechnicalType.Text, observed.Texts),
                (TechnicalType.Date, observed.Dates),
                (TechnicalType.Boolean, observed.Booleans),
            }
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Count)
            .ToList();
        if (types.Count == 0)
            return null;

        var name = $"{entity}.{attribute}";
        var total = types.Sum(t => t.Count);
        if (types.Count == 1)
            return new SleepLogEntry(SleepLogKind.TypeObserved, entity, attribute,
                $"{name}: {(total == 1 ? "the value" : $"all {Number(total)} values")} set in this phase " +
                $"{(total == 1 ? "is a" : "are")} {TypeName(types[0].Type, total)}.");

        var most = types[0];
        var others = total - most.Count;
        return new SleepLogEntry(SleepLogKind.TypesMixed, entity, attribute,
            $"{name}: values set in this phase have different types: " +
            string.Join(", ", types.Select(t => $"{Number(t.Count)} {TypeName(t.Type, t.Count)}")) + ". " +
            $"Most are {TypeName(most.Type, 2)}. Every value is kept as it was given; " +
            $"the {Count(others, "value")} of other types may be mistakes.");
    }

    private static string TypeName(TechnicalType type, long count) => (type, count == 1) switch
    {
        (TechnicalType.Number, true) => "number",
        (TechnicalType.Number, false) => "numbers",
        (TechnicalType.Text, true) => "text",
        (TechnicalType.Text, false) => "texts",
        (TechnicalType.Date, true) => "date",
        (TechnicalType.Date, false) => "dates",
        (TechnicalType.Boolean, true) => "boolean",
        (TechnicalType.Boolean, false) => "booleans",
        _ => type.ToString(),
    };

    private static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Count(long n, string noun) => $"{Number(n)} {noun}{(n == 1 ? "" : "s")}";
}
