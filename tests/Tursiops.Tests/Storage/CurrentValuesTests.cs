using Tursiops.Core.Format;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Storage;

// Reading the current value of one attribute for all entities (E-33). It reads the same as asking
// every entity on its own, in every layout, and is the operation where H1 is faster than H2 (E-30).
public class CurrentValuesTests
{
    private const string Source = """
        Mensch {
          Name: text
          Wohnort: text @time
          Körpergröße: number @history
        }
        """;

    private const AttributeLayout H1 = AttributeLayout.CurrentColumnAndHistoryTable;
    private const AttributeLayout H2 = AttributeLayout.HistoryTable;

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age10 = new(2016, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age12 = new(2018, 3, 3, 0, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset Age14 = new(2020, 3, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutAnnotation_ReturnsEntitiesWithAValue_InCreationOrder()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");
        var ben = store.CreateEntity("Mensch");
        var cleo = store.CreateEntity("Mensch");
        store.SetValue(cleo, "Name", "Cleo");
        store.SetValue(anna, "Name", "Anna");

        var values = store.GetCurrentValues("Mensch", "Name");

        Assert.Equal(
            [new EntityValue(anna, new StoredValue("Anna", null)), new EntityValue(cleo, new StoredValue("Cleo", null))],
            values);
        Assert.DoesNotContain(values, v => v.Entity == ben);
    }

    [Fact] // E-25: with @time a late older value does not replace a newer one
    public void WithTime_ReturnsTheValueWithTheLatestValidTime()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");
        store.SetValue(anna, "Wohnort", "Leipzig", ValueTime.At(Age12));
        store.SetValue(anna, "Wohnort", "Dresden", ValueTime.At(Age10)); // late

        var value = Assert.Single(store.GetCurrentValues("Mensch", "Wohnort"));
        Assert.Equal(new EntityValue(anna, new StoredValue("Leipzig", Age12)), value);
        Assert.Equal(Age12.Offset, value.Value.ValidAt!.Value.Offset);
    }

    [Theory]
    [InlineData(H1)]
    [InlineData(H2)]
    public void WithHistory_ReturnsTheLatestValuePerEntity(AttributeLayout layout)
    {
        using var store = Memory(layout);
        var anna = store.CreateEntity("Mensch");
        var ben = store.CreateEntity("Mensch");
        store.CreateEntity("Mensch"); // no height yet
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.SetValue(anna, "Körpergröße", 140L, ValueTime.At(Age10)); // late
        store.SetValue(ben, "Körpergröße", 150L, ValueTime.At(Age12));

        Assert.Equal(
            [new EntityValue(anna, new StoredValue(168L, Age14)), new EntityValue(ben, new StoredValue(150L, Age12))],
            store.GetCurrentValues("Mensch", "Körpergröße"));
    }

    [Theory] // same rule as for one entity: among equal valid times the value written last is current
    [InlineData(H1)]
    [InlineData(H2)]
    public void WithHistory_SameValidTime_TheValueWrittenLastIsCurrent(AttributeLayout layout)
    {
        using var store = Memory(layout);
        var anna = store.CreateEntity("Mensch");
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 156L, ValueTime.At(Age12));

        Assert.Equal(156L, Assert.Single(store.GetCurrentValues("Mensch", "Körpergröße")).Value.Value);
    }

    [Theory]
    [InlineData(H1)]
    [InlineData(H2)]
    public void ReadsTheSameAsAskingEveryEntity(AttributeLayout layout)
    {
        using var store = Memory(layout);
        var people = Enumerable.Range(0, 6).Select(_ => store.CreateEntity("Mensch")).ToList();
        for (var i = 0; i < people.Count; i++)
        {
            if (i % 3 == 0)
                continue; // some people have no values at all
            store.SetValue(people[i], "Name", i % 2 == 0 ? null : $"P{i}");
            store.SetValue(people[i], "Wohnort", $"W{i}", ValueTime.At(Age10.AddDays(i)));
            store.SetValue(people[i], "Körpergröße", 100L + i, ValueTime.At(Age14));
            store.SetValue(people[i], "Körpergröße", null, ValueTime.At(Age12.AddDays(i))); // late
            if (i % 2 == 0)
                store.SetValue(people[i], "Körpergröße", null, ValueTime.At(Now)); // null with a time is a value
        }

        foreach (var attribute in new[] { "Name", "Wohnort", "Körpergröße" })
        {
            var expected = people
                .Select(p => (Entity: p, Value: store.GetCurrentValue(p, attribute)))
                .Where(p => p.Value is not null)
                .Select(p => new EntityValue(p.Entity, p.Value!));
            Assert.Equal(expected, store.GetCurrentValues("Mensch", attribute));
        }
    }

    [Theory] // E-25: changing the layout does not change what is read
    [InlineData(H2, H1)]
    [InlineData(H1, H2)]
    public void ChangingTheLayout_KeepsTheResult(AttributeLayout from, AttributeLayout to)
    {
        using var store = Memory(from);
        var anna = store.CreateEntity("Mensch");
        var ben = store.CreateEntity("Mensch");
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.SetValue(ben, "Körpergröße", 150L, ValueTime.At(Age12));
        var before = store.GetCurrentValues("Mensch", "Körpergröße");

        store.ChangeLayout("Mensch", "Körpergröße", to);

        Assert.Equal(before, store.GetCurrentValues("Mensch", "Körpergröße"));
    }

    [Fact]
    public void NoEntities_ReturnsNothing()
    {
        using var store = Memory(H1);
        Assert.Empty(store.GetCurrentValues("Mensch", "Körpergröße"));
    }

    [Theory]
    [InlineData("Tier", "Name")]
    [InlineData("Mensch", "Gewicht")]
    public void UnknownEntityOrAttribute_IsAnError(string entity, string attribute)
    {
        using var store = Memory(H2);
        Assert.Throws<ArgumentException>(() => store.GetCurrentValues(entity, attribute));
    }

    // ---------- Helpers ----------

    private static SqliteStore Memory(AttributeLayout history) =>
        SqliteStore.Create(
            ":memory:",
            StructureModel.FromOntology(TurParser.Parse(Source).Ontology).WithLayout("Mensch", "Körpergröße", history),
            new FixedTimeProvider(Now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
