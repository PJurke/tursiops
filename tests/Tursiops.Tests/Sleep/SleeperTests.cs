using Microsoft.Data.Sqlite;
using Tursiops.Core.Format;
using Tursiops.Core.Sleep;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Sleep;

// The sleep (E-04) with a database file and the measured SQLite costs: evaluate the waking phase,
// decide on layouts (E-34), change them, log every decision in the file (E-35), start the next phase (E-31).
public class SleeperTests : IDisposable
{
    private const string Source = """
        Mensch {
          Name
          Wohnort @time
          Körpergröße @history
        }
        """;

    private const AttributeLayout H1 = AttributeLayout.CurrentColumnAndHistoryTable;
    private const AttributeLayout H2 = AttributeLayout.HistoryTable;

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset Birth = new(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly List<string> _files = [];

    [Fact]
    public void Sleep_StartsTheNextPhase_AndKeepsTheLogInTheFile()
    {
        var path = TempFile();
        SleepReport report;
        using (var store = Create(path))
        {
            Fill(store, people: 2, valuesEach: 2);
            report = Sleeper.Run(store);
            Assert.Equal(2, store.Phase);
        }

        Assert.Equal(1, report.Phase);
        Assert.Equal(2, report.NextPhase);
        using (var store = SqliteStore.Open(path))
        {
            var log = store.GetSleepLog();
            Assert.Equal(report.Entries, log.Select(r => r.Entry));
            Assert.All(log, r => Assert.Equal(1, r.Phase));
            Assert.All(log, r => Assert.Equal(Now, r.LoggedAt));
            Assert.Equal(SleepLogKind.Sleep, log[0].Entry.Kind);
            Assert.StartsWith("Sleep after waking phase 1", log[0].Entry.Message);
            Assert.Equal(2, store.Phase);
        }
    }

    [Fact]
    public void ReadingAllOften_ChangesToH1_WithoutChangingTheInformation() // E-25, E-34
    {
        using var store = Create(TempFile());
        var people = Fill(store, people: 20, valuesEach: 5);
        Sleeper.Run(store);
        var before = Snapshot(store, people);

        for (var i = 0; i < 200; i++)
            store.GetCurrentValues("Mensch", "Körpergröße");
        var report = Sleeper.Run(store);

        Assert.Equal(H1, store.Structure.Entity("Mensch").Attribute("Körpergröße").Layout);
        var decision = Assert.Single(report.Decisions);
        Assert.Equal(LayoutOutcome.Changed, decision.Outcome);
        Assert.Equal(20, decision.Entities);
        Assert.Equal(100, decision.Values);
        Assert.Contains(store.GetSleepLog(), r => r.Phase == 2 && r.Entry.Kind == SleepLogKind.LayoutChanged);
        Assert.Equal(before, Snapshot(store, people));
    }

    [Fact]
    public void WritingOnly_KeepsH2()
    {
        using var store = Create(TempFile());
        Fill(store, people: 20, valuesEach: 5);

        var report = Sleeper.Run(store);

        Assert.Equal(LayoutOutcome.Kept, Assert.Single(report.Decisions).Outcome);
        Assert.Equal(H2, store.Structure.Entity("Mensch").Attribute("Körpergröße").Layout);
        Assert.Contains(report.Entries, e => e.Kind == SleepLogKind.LayoutKept && e.Attribute == "Körpergröße");
    }

    [Fact]
    public void FewOperations_DecideNothing()
    {
        using var store = Create(TempFile());
        Fill(store, people: 1, valuesEach: 7);

        var report = Sleeper.Run(store);

        Assert.Equal(LayoutOutcome.NotEnoughData, Assert.Single(report.Decisions).Outcome);
        Assert.Contains(report.Entries, e => e.Kind == SleepLogKind.NotEnoughData && e.Message.Contains("7 operations"));
    }

    [Fact]
    public void OnlyAttributesWithHistory_GetALayoutDecision() // E-30
    {
        using var store = Create(TempFile());
        Fill(store, people: 3, valuesEach: 1);

        var report = Sleeper.Run(store);

        Assert.Equal("Körpergröße", Assert.Single(report.Decisions).Attribute);
    }

    [Fact]
    public void Types_AreLogged_AndMixedTypesAreNamed() // E-13
    {
        using var store = Create(TempFile());
        var anna = store.CreateEntity("Mensch");
        store.SetValue(anna, "Name", "Anna");
        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Birth));
        store.SetValue(anna, "Körpergröße", 155.5, ValueTime.At(Birth.AddYears(2)));
        store.SetValue(anna, "Körpergröße", "groß", ValueTime.At(Birth.AddYears(4)));

        var entries = Sleeper.Run(store).Entries;

        var name = Assert.Single(entries, e => e.Attribute == "Name");
        Assert.Equal(SleepLogKind.TypeObserved, name.Kind);
        Assert.Contains("is a text", name.Message);
        var height = Assert.Single(entries, e => e.Attribute == "Körpergröße" && e.Kind == SleepLogKind.TypesMixed);
        Assert.Contains("2 numbers, 1 text", height.Message);
        Assert.Contains("1 value of other types", height.Message);
        Assert.Equal("groß", store.GetCurrentValue(anna, "Körpergröße")!.Value);
    }

    [Fact]
    public void ObservationsOfTheEvaluatedPhase_AreKept() // E-31
    {
        using var store = Create(TempFile());
        Fill(store, people: 2, valuesEach: 3);

        Sleeper.Run(store);

        Assert.Equal(6, store.GetObservations("Mensch", "Körpergröße", phase: 1).Writes);
        Assert.Equal(AttributeObservations.None, store.GetObservations("Mensch", "Körpergröße"));
    }

    [Fact]
    public void Failure_LeavesEverythingAsBefore() // E-33: one transaction for the whole sleep
    {
        var path = TempFile();
        using var store = Create(path);
        var people = Fill(store, people: 20, valuesEach: 5);
        Sleeper.Run(store);
        for (var i = 0; i < 200; i++)
            store.GetCurrentValues("Mensch", "Körpergröße");
        var before = Snapshot(store, people);
        var logBefore = store.GetSleepLog();
        Execute(path, """CREATE TRIGGER "fail" BEFORE DELETE ON "Mensch.Körpergröße" BEGIN SELECT RAISE(ABORT, 'injected'); END""");

        Assert.ThrowsAny<SqliteException>(() => Sleeper.Run(store));

        Assert.Equal(2, store.Phase);
        Assert.Equal(H2, store.Structure.Entity("Mensch").Attribute("Körpergröße").Layout);
        Assert.Equal(logBefore, store.GetSleepLog());
        Assert.Equal(before, Snapshot(store, people));
        Assert.Equal(200, store.GetObservations("Mensch", "Körpergröße").AllCurrentReads);
    }

    [Fact]
    public void CountsForTheDecision_AreTheSameInH1AndH2() // E-34: data shape read from the tables
    {
        using var store = Create(TempFile());
        Fill(store, people: 4, valuesEach: 3);
        store.CreateEntity("Mensch");

        Assert.Equal(5, store.CountEntities("Mensch"));
        Assert.Equal(12, store.CountValues("Mensch", "Körpergröße"));
        Assert.Equal(4, store.CountValues("Mensch", "Name"));
        Assert.Equal(4, store.CountValues("Mensch", "Wohnort"));

        store.ChangeLayout("Mensch", "Körpergröße", H1);
        Assert.Equal(12, store.CountValues("Mensch", "Körpergröße"));
    }

    // ---------- Helpers ----------

    private static SqliteStore Create(string path) =>
        SqliteStore.Create(path, StructureModel.FromOntology(TurParser.Parse(Source).Ontology), new FixedTimeProvider(Now));

    // People with a name, a place of residence and several heights, one per year.
    private static List<EntityRef> Fill(SqliteStore store, int people, int valuesEach)
    {
        var refs = new List<EntityRef>();
        for (var p = 0; p < people; p++)
        {
            var person = store.CreateEntity("Mensch");
            store.SetValue(person, "Name", $"Person {p}");
            store.SetValue(person, "Wohnort", "Leipzig", ValueTime.At(Birth));
            for (var v = 0; v < valuesEach; v++)
                store.SetValue(person, "Körpergröße", 100L + p + v, ValueTime.At(Birth.AddYears(v)));
            refs.Add(person);
        }
        return refs;
    }

    private static List<object> Snapshot(SqliteStore store, IEnumerable<EntityRef> people) =>
        people.SelectMany<EntityRef, object>(p =>
        [
            store.GetCurrentValue(p, "Körpergröße") ?? (object)"none",
            .. store.GetHistory(p, "Körpergröße").Select(h => (object)(h.Value ?? "null", h.ValidAt)),
        ]).ToList();

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tursiops-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _files)
            File.Delete(file);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
