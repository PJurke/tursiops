using Tursiops.Core.Sleep;

namespace Tursiops.Core.Storage.Sqlite;

/// <summary>Costs of operations on SQLite for the layout decision in sleep (E-34).</summary>
public static class SqliteCosts
{
    /// <summary>
    /// Midpoints of the ranges measured with tools/Tursiops.Bench (docs/concepts/time.md, "Measurements on SQLite").
    /// Where the measurement found no consistent difference, both layouts get the same cost.
    /// The machine was not the target hardware, so only the ratios between the numbers are meaningful.
    /// </summary>
    public static CostModel Measured { get; } = new(
        new Dictionary<AttributeLayout, OperationCosts>
        {
            // H2: write 0.8 to 1.6 ms, history 24 to 38 µs, all of 100,000 entities 90 to 97 ms.
            [AttributeLayout.HistoryTable] = new(Write: 1200, CurrentRead: 15, AllCurrentReadPerEntity: 0.935, HistoryRead: 31),
            // H1: write 0.9 to 1.8 ms, history 33 to 46 µs, all of 100,000 entities 61 to 65 ms.
            [AttributeLayout.CurrentColumnAndHistoryTable] = new(Write: 1350, CurrentRead: 15, AllCurrentReadPerEntity: 0.63, HistoryRead: 39.5),
        },
        // Changing H2 to H1 for 1 million values: 0.5 to 0.6 s. Assumed the same in the other direction.
        ChangePerValue: 0.55);
}
