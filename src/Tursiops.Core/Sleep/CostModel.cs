using Tursiops.Core.Storage;

namespace Tursiops.Core.Sleep;

// Cost model for the layout decision in sleep (E-34). What an operation costs depends on the target system (E-06),
// so each translator provides its own numbers, e.g. SqliteCosts.Measured.

/// <summary>The operations the layout decision weighs (E-31). Late writes are counted among writes.</summary>
public enum CountedOperation
{
    Write,
    CurrentRead,
    AllCurrentRead,
    HistoryRead,
}

/// <summary>Estimated cost of each counted operation in one layout, in microseconds.</summary>
/// <param name="Write">Setting one value. Late writes are counted among writes and cost the same.</param>
/// <param name="CurrentRead">Reading the current value of one entity.</param>
/// <param name="AllCurrentReadPerEntity">Reading the current values of all entities, per entity.</param>
/// <param name="HistoryRead">Reading the history of one entity.</param>
public sealed record OperationCosts(double Write, double CurrentRead, double AllCurrentReadPerEntity, double HistoryRead);

/// <summary>Costs per layout and the cost of changing between layouts, for one target system.</summary>
/// <param name="Layouts">Operation costs for each layout the sleep can choose.</param>
/// <param name="ChangePerValue">Changing the layout of an attribute, per stored value, in microseconds.</param>
public sealed record CostModel(IReadOnlyDictionary<AttributeLayout, OperationCosts> Layouts, double ChangePerValue)
{
    /// <summary>
    /// What a waking phase with these observations would have cost in a layout, in microseconds.
    /// Reading all entities costs more the more entities there are.
    /// </summary>
    /// <exception cref="ArgumentException">The model has no costs for that layout.</exception>
    public double Estimate(AttributeLayout layout, AttributeObservations observed, long entities)
    {
        return EstimateParts(layout, observed, entities).Values.Sum();
    }

    /// <summary>The estimate of <see cref="Estimate"/>, split by counted operation, so the log can name what mattered most.</summary>
    /// <exception cref="ArgumentException">The model has no costs for that layout.</exception>
    public IReadOnlyDictionary<CountedOperation, double> EstimateParts(AttributeLayout layout, AttributeObservations observed, long entities)
    {
        if (!Layouts.TryGetValue(layout, out var costs))
            throw new ArgumentException($"The cost model has no costs for layout {layout}.", nameof(layout));

        return new Dictionary<CountedOperation, double>
        {
            [CountedOperation.Write] = observed.Writes * costs.Write,
            [CountedOperation.CurrentRead] = observed.CurrentReads * costs.CurrentRead,
            [CountedOperation.AllCurrentRead] = observed.AllCurrentReads * entities * costs.AllCurrentReadPerEntity,
            [CountedOperation.HistoryRead] = observed.HistoryReads * costs.HistoryRead,
        };
    }

    /// <summary>What changing the layout of an attribute with this many stored values costs, in microseconds.</summary>
    public double EstimateChange(long values) => values * ChangePerValue;
}
