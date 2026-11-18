using System.Globalization;
using Tursiops.Core.Storage;

namespace Tursiops.Core.Sleep;

// The layout decision in sleep (E-34): estimate what the last waking phase would have cost in each layout,
// and change only when another layout saves clearly more than the change costs.

/// <summary>The thresholds of the layout decision (E-34). Both are set, not derived (parking lot).</summary>
/// <param name="MinOperations">Fewer operations in a phase than this are too little to decide on.</param>
/// <param name="MinSavingShare">Share of the current cost another layout has to save on top of the cost of changing.</param>
public sealed record LayoutRule(long MinOperations, double MinSavingShare)
{
    /// <summary>At least 100 operations, at least 20 % saving on top of the change cost (E-34).</summary>
    public static LayoutRule Default { get; } = new(100, 0.20);
}

/// <summary>What the sleep decided for one attribute.</summary>
public enum LayoutOutcome
{
    /// <summary>Too few operations in the phase, nothing was decided.</summary>
    NotEnoughData,

    /// <summary>The layout stays, no other layout saves enough.</summary>
    Kept,

    /// <summary>Another layout saves enough, the attribute changes to it.</summary>
    Changed,
}

/// <summary>A layout decision for one attribute with every number it rests on, so the log can explain it (E-04).</summary>
/// <param name="Operations">All counted operations of the phase (writes and reads).</param>
/// <param name="Estimates">Estimated cost of the phase in each layout, in microseconds. Empty if not enough data.</param>
/// <param name="ChangeCost">Estimated cost of changing the layout, in microseconds.</param>
/// <param name="Required">Saving a layout needs to exceed: change cost plus the share of the current cost, in microseconds.</param>
/// <param name="Best">The alternative layout with the largest saving, or <c>null</c> if not enough data.</param>
/// <param name="Saving">What <paramref name="Best"/> saves against the current layout, in microseconds.</param>
public sealed record LayoutDecision(
    string Entity,
    string Attribute,
    LayoutOutcome Outcome,
    AttributeLayout Current,
    AttributeObservations Observed,
    long Operations,
    long Entities,
    long Values,
    IReadOnlyDictionary<AttributeLayout, double> Estimates,
    double ChangeCost,
    double Required,
    AttributeLayout? Best,
    double Saving,
    LayoutRule Rule)
{
    /// <summary>The layout after the decision.</summary>
    public AttributeLayout Result => Outcome == LayoutOutcome.Changed ? Best!.Value : Current;

    /// <summary>The decision in readable English, with the numbers it rests on (E-04).</summary>
    public string Describe()
    {
        var name = $"{Entity}.{Attribute}";
        if (Outcome == LayoutOutcome.NotEnoughData)
            return $"{name}: {Count(Operations)} operations in this phase, at least {Count(Rule.MinOperations)} are needed to decide. " +
                   $"Kept {LayoutNames.Describe(Current)}.";

        var observed =
            $"{Count(Observed.Writes)} writes ({Count(Observed.LateWrites)} of them late), " +
            $"{Count(Observed.CurrentReads)} reads of one current value, " +
            $"{Count(Observed.AllCurrentReads)} reads of all {Count(Entities)} current values, " +
            $"{Count(Observed.HistoryReads)} history reads";
        var estimates = string.Join(", ", Estimates.Select(e => $"{LayoutNames.Describe(e.Key)} {Ms(e.Value)}"));
        var bestName = LayoutNames.Short(Best!.Value);
        var required =
            $"A change has to save more than {Ms(Required)}: {Ms(ChangeCost)} for changing {Count(Values)} values " +
            $"plus {Percent(Rule.MinSavingShare)} of the current cost.";
        var verdict = Outcome == LayoutOutcome.Changed
            ? $"{bestName} saves {Ms(Saving)}. Changed to {LayoutNames.Describe(Best.Value)}."
            : Saving > 0
                ? $"{bestName} would save {Ms(Saving)}. Kept {LayoutNames.Describe(Current)}."
                : $"{bestName} would cost {Ms(-Saving)} more. Kept {LayoutNames.Describe(Current)}.";

        return $"{name}: {Count(Operations)} operations in this phase: {observed}. " +
               $"Estimated cost of the phase: {estimates}. {required} {verdict}";
    }

    private static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Ms(double micros) => (micros / 1000).ToString("N1", CultureInfo.InvariantCulture) + " ms";

    private static string Percent(double share) => (share * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %";
}

/// <summary>Decides the layout of one attribute with <c>@history</c> from the observations of one phase (E-34).</summary>
public static class LayoutDecider
{
    /// <param name="entities">Number of entities, the data shape the sleep reads from the tables (E-31).</param>
    /// <param name="values">Number of stored values of the attribute, for the cost of a change.</param>
    /// <exception cref="ArgumentException">The attribute has no layouts to choose from.</exception>
    public static LayoutDecision Decide(
        AttributeStructure attribute, string entity, AttributeObservations observed, long entities, long values,
        CostModel costs, LayoutRule rule)
    {
        var alternatives = AttributeLayouts.AllowedFor(attribute.Time).Where(l => l != attribute.Layout).ToList();
        if (alternatives.Count == 0)
            throw new ArgumentException($"'{entity}.{attribute.Name}' has only one possible layout, there is nothing to decide.",
                nameof(attribute));

        var operations = observed.Writes + observed.CurrentReads + observed.AllCurrentReads + observed.HistoryReads;
        if (operations < rule.MinOperations)
            return new LayoutDecision(entity, attribute.Name, LayoutOutcome.NotEnoughData, attribute.Layout, observed, operations,
                entities, values, new Dictionary<AttributeLayout, double>(), 0, 0, null, 0, rule);

        var estimates = AttributeLayouts.AllowedFor(attribute.Time)
            .ToDictionary(l => l, l => costs.Estimate(l, observed, entities));
        var current = estimates[attribute.Layout];
        var changeCost = costs.EstimateChange(values);
        var required = changeCost + rule.MinSavingShare * current;

        var best = alternatives.MaxBy(l => current - estimates[l]);
        var saving = current - estimates[best];
        var outcome = saving > required ? LayoutOutcome.Changed : LayoutOutcome.Kept;

        return new LayoutDecision(entity, attribute.Name, outcome, attribute.Layout, observed, operations, entities, values,
            estimates, changeCost, required, best, saving, rule);
    }
}

/// <summary>Readable names of layouts for the log.</summary>
public static class LayoutNames
{
    /// <summary>Short name, e.g. <c>H1</c>.</summary>
    public static string Short(AttributeLayout layout) => layout switch
    {
        AttributeLayout.HistoryTable => "H2",
        AttributeLayout.CurrentColumnAndHistoryTable => "H1",
        _ => layout.ToString(),
    };

    /// <summary>Short name with a description, e.g. <c>H1 (current value in entity row)</c>.</summary>
    public static string Describe(AttributeLayout layout) => layout switch
    {
        AttributeLayout.HistoryTable => "H2 (history table only)",
        AttributeLayout.CurrentColumnAndHistoryTable => "H1 (current value in entity row)",
        AttributeLayout.Column => "value column",
        AttributeLayout.ColumnWithTime => "value column with time",
        _ => layout.ToString(),
    };
}
