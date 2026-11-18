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
                if (AttributeLayouts.AllowedFor(attribute.Time).Count < 2)
                    continue;

                var observed = store.GetObservations(entity.Name, attribute.Name);

                var decision = LayoutDecider.Decide(attribute, entity.Name, observed, entityCount,
                    store.CountValues(entity.Name, attribute.Name), costs, rule);
                decisions.Add(decision);
                entries.Add(new SleepLogEntry(Kind(decision.Outcome), entity.Name, attribute.Name, decision.Summary(), decision.Details()));
            }
        }

        var started = store.PhaseStartedAt(phase);
        var changed = decisions.Count(d => d.Outcome == LayoutOutcome.Changed);
        entries.Insert(0, new SleepLogEntry(SleepLogKind.Sleep, null, null,
            changed == 0
                ? $"Tursiops slept after waking phase {phase} and kept every layout."
                : $"Tursiops slept after waking phase {phase} and changed how {Count(changed, "attribute")} {(changed == 1 ? "is" : "are")} stored.",
            $"The phase began {started.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}. " +
            $"{Count(changed, "layout")} changed, {Count(decisions.Count - changed, "layout")} kept."));

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

    private static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Count(long n, string noun) => $"{Number(n)} {noun}{(n == 1 ? "" : "s")}";
}
