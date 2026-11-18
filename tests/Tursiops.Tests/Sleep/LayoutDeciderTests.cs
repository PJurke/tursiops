using Tursiops.Core.Format;
using Tursiops.Core.Sleep;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Sleep;

// The layout decision in sleep (E-34), without a database: cost model, minimum saving, minimum number of operations.
// Round costs, so every expected number can be checked by hand.
public class LayoutDeciderTests
{
    private const AttributeLayout H1 = AttributeLayout.CurrentColumnAndHistoryTable;
    private const AttributeLayout H2 = AttributeLayout.HistoryTable;

    // H1 writes a bit slower, reads all entities at half the cost and reads history at twice the cost.
    private static readonly CostModel Costs = new(
        new Dictionary<AttributeLayout, OperationCosts>
        {
            [H2] = new(Write: 10, CurrentRead: 1, AllCurrentReadPerEntity: 1, HistoryRead: 1),
            [H1] = new(Write: 11, CurrentRead: 1, AllCurrentReadPerEntity: 0.5, HistoryRead: 2),
        },
        ChangePerValue: 0.1);

    private static readonly LayoutRule Rule = LayoutRule.Default;

    [Fact]
    public void Estimate_AddsUpEveryOperation_AndReadingAllGrowsWithTheEntities()
    {
        var observed = Observed(writes: 3, currentReads: 4, allReads: 2, historyReads: 5);

        Assert.Equal(3 * 10 + 4 * 1 + 2 * 100 * 1 + 5 * 1, Costs.Estimate(H2, observed, entities: 100));
        Assert.Equal(3 * 11 + 4 * 1 + 2 * 100 * 0.5 + 5 * 2, Costs.Estimate(H1, observed, entities: 100));
    }

    [Fact]
    public void FewerThan100Operations_DecidesNothing()
    {
        var decision = Decide(H2, Observed(allReads: 99), entities: 10_000, values: 10);

        Assert.Equal(LayoutOutcome.NotEnoughData, decision.Outcome);
        Assert.Equal(99, decision.Operations);
        Assert.Equal(H2, decision.Result);
    }

    [Fact]
    public void LateWrites_AreNotCountedTwice()
    {
        var decision = Decide(H2, Observed(writes: 60, lateWrites: 50), entities: 1, values: 60);

        Assert.Equal(60, decision.Operations);
        Assert.Equal(LayoutOutcome.NotEnoughData, decision.Outcome);
    }

    [Fact]
    public void Exactly100Operations_IsEnough()
    {
        Assert.NotEqual(LayoutOutcome.NotEnoughData, Decide(H2, Observed(writes: 100), entities: 1, values: 100).Outcome);
    }

    [Fact]
    public void ReadingAllEntitiesOften_ChangesH2ToH1()
    {
        // H2: 100 * 1,000 * 1 = 100,000; H1: 50,000. Change: 2,000 values * 0.1 = 200. Required: 200 + 20,000.
        var decision = Decide(H2, Observed(allReads: 100), entities: 1_000, values: 2_000);

        Assert.Equal(LayoutOutcome.Changed, decision.Outcome);
        Assert.Equal(H1, decision.Result);
        Assert.Equal(100_000, decision.Estimates[H2]);
        Assert.Equal(50_000, decision.Estimates[H1]);
        Assert.Equal(200, decision.ChangeCost, precision: 9);
        Assert.Equal(20_200, decision.Required, precision: 9);
        Assert.Equal(50_000, decision.Saving);
    }

    [Fact]
    public void OnlyWrites_KeepsH2()
    {
        var decision = Decide(H2, Observed(writes: 1_000), entities: 100, values: 1_000);

        Assert.Equal(LayoutOutcome.Kept, decision.Outcome);
        Assert.Equal(H1, decision.Best);
        Assert.True(decision.Saving < 0);
        Assert.Contains("H1 would cost 1.0 ms more. Kept H2", decision.Describe());
    }

    [Fact]
    public void ReadingHistoryOften_ChangesH1ToH2()
    {
        // H1: 1,000 * 2 = 2,000; H2: 1,000. Saving 1,000 > 10 * 0.1 + 400.
        var decision = Decide(H1, Observed(historyReads: 1_000), entities: 10, values: 10);

        Assert.Equal(LayoutOutcome.Changed, decision.Outcome);
        Assert.Equal(H2, decision.Result);
    }

    [Fact]
    public void SavingBelowTheMinimumShare_KeepsTheLayout()
    {
        // H2: 100 writes * 10 + 10 reads of 100 entities = 2,000; H1: 1,100 + 500 = 1,600. Saving 400, required 0 + 400.
        var decision = Decide(H2, Observed(writes: 100, allReads: 10), entities: 100, values: 0);

        Assert.Equal(400, decision.Saving, precision: 9);
        Assert.Equal(400, decision.Required, precision: 9);
        Assert.Equal(LayoutOutcome.Kept, decision.Outcome);
    }

    [Fact]
    public void ChangeCost_CanOutweighTheSaving()
    {
        // Same saving as above with more headroom, but 100,000 values cost 10,000 to change.
        var decision = Decide(H2, Observed(allReads: 100), entities: 100, values: 100_000);

        Assert.Equal(5_000, decision.Saving);
        Assert.Equal(LayoutOutcome.Kept, decision.Outcome);
    }

    [Theory] // Only @history has layouts to choose from (E-30)
    [InlineData(AttributeTime.Timeless, AttributeLayout.Column)]
    [InlineData(AttributeTime.CurrentOnly, AttributeLayout.ColumnWithTime)]
    public void AttributesWithOneLayout_AreRefused(AttributeTime time, AttributeLayout layout)
    {
        Assert.Throws<ArgumentException>(() => LayoutDecider.Decide(
            new AttributeStructure("Name", time, layout), "Mensch", Observed(writes: 1_000), 1, 1, Costs, Rule));
    }

    [Fact]
    public void Describe_NamesTheNumbers()
    {
        var text = Decide(H2, Observed(writes: 1_200, lateWrites: 3, allReads: 100), entities: 1_000, values: 2_000).Describe();

        Assert.Contains("Mensch.Körpergröße", text);
        Assert.Contains("1,300 operations", text);
        Assert.Contains("1,200 writes (3 of them late)", text);
        Assert.Contains("100 reads of all 1,000 current values", text);
        Assert.Contains("20 %", text);
        Assert.Contains("Changed to H1", text);
    }

    [Fact]
    public void Describe_NotEnoughData_NamesTheMinimum()
    {
        var text = Decide(H2, Observed(writes: 7), entities: 1, values: 7).Describe();

        Assert.Contains("7 operations", text);
        Assert.Contains("at least 100", text);
        Assert.Contains("Kept H2", text);
    }

    [Fact]
    public void MeasuredSqliteCosts_FavorH1OnlyForReadingAll() // E-33, E-34: the measurement behind the rule
    {
        var sqlite = SqliteCosts.Measured;
        var write = Observed(writes: 1);
        var readAll = Observed(allReads: 1);

        Assert.True(sqlite.Estimate(H1, write, 1) > sqlite.Estimate(H2, write, 1));
        Assert.True(sqlite.Estimate(H1, readAll, 1_000) < sqlite.Estimate(H2, readAll, 1_000));
    }

    private static LayoutDecision Decide(AttributeLayout layout, AttributeObservations observed, long entities, long values) =>
        LayoutDecider.Decide(new AttributeStructure("Körpergröße", AttributeTime.History, layout), "Mensch",
            observed, entities, values, Costs, Rule);

    private static AttributeObservations Observed(
        long writes = 0, long lateWrites = 0, long currentReads = 0, long allReads = 0, long historyReads = 0) =>
        AttributeObservations.None with
        {
            Writes = writes,
            LateWrites = lateWrites,
            CurrentReads = currentReads,
            AllCurrentReads = allReads,
            HistoryReads = historyReads,
        };
}
