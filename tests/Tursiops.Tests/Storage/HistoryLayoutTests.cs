using Microsoft.Data.Sqlite;
using Tursiops.Core.Format;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Storage;

// Layouts for @history (E-30): H1 keeps the current value in the entity row and older values in the history table,
// H2 keeps everything in the history table. Changing between them never changes the information (E-25).
public class HistoryLayoutTests : IDisposable
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

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age10 = new(2016, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age12 = new(2018, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age14 = new(2020, 3, 3, 0, 0, 0, TimeSpan.Zero);

    private readonly List<string> _files = [];

    // ---------- Structure model ----------

    [Fact]
    public void History_AllowsH1AndH2_StartingWithH2() // E-30
    {
        Assert.Equal([H2, H1], AttributeLayouts.AllowedFor(AttributeTime.History));
        Assert.Equal(H2, Model().Entity("Mensch").Attribute("Körpergröße").Layout);
    }

    [Theory] // E-25: H1 holds a history, so it does not fit attributes without one
    [InlineData("Name")]
    [InlineData("Wohnort")]
    public void H1_IsRejectedForAttributesWithoutHistory(string attribute)
    {
        Assert.Throws<ArgumentException>(() => Model().WithLayout("Mensch", attribute, H1));
    }

    // ---------- Same information in both layouts ----------

    [Theory]
    [InlineData(H1)]
    [InlineData(H2)]
    public void CurrentValue_IsTheLatestByValidTime_NotByWriteOrder(AttributeLayout layout)
    {
        using var store = Memory(layout);
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));

        Assert.Equal(new StoredValue(168L, Age14), store.GetCurrentValue(anna, "Körpergröße"));
        Assert.Equal(
            [new TimedValue(142L, Age10), new TimedValue(155L, Age12), new TimedValue(168L, Age14)],
            store.GetHistory(anna, "Körpergröße"));
    }

    [Theory] // Same valid time: the value written last is current, in both layouts alike (open question in the parking lot)
    [InlineData(H1)]
    [InlineData(H2)]
    public void SameValidTime_LastWrittenIsCurrent(AttributeLayout layout)
    {
        using var store = Memory(layout);
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 156L, ValueTime.At(Age12));

        Assert.Equal(156L, store.GetCurrentValue(anna, "Körpergröße")!.Value);
        Assert.Equal([155L, 156L], store.GetHistory(anna, "Körpergröße").Select(h => h.Value));
    }

    [Theory]
    [InlineData(H1)]
    [InlineData(H2)]
    public void NullIsAValue_AndNeverSetIsNoValue(AttributeLayout layout)
    {
        using var store = Memory(layout);
        var anna = store.CreateEntity("Mensch");
        var ben = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));
        store.SetValue(anna, "Körpergröße", null, ValueTime.At(Age12));

        Assert.Equal(new StoredValue(null, Age12), store.GetCurrentValue(anna, "Körpergröße"));
        Assert.Equal(2, store.GetHistory(anna, "Körpergröße").Count);
        Assert.Null(store.GetCurrentValue(ben, "Körpergröße"));
        Assert.Empty(store.GetHistory(ben, "Körpergröße"));
    }

    [Theory]
    [InlineData(H1)]
    [InlineData(H2)]
    public void ValidTimes_KeepTheirOffset(AttributeLayout layout)
    {
        using var store = Memory(layout);
        var anna = store.CreateEntity("Mensch");
        var berlin = new DateTimeOffset(2016, 3, 3, 9, 30, 0, TimeSpan.FromHours(1));
        var tokyo = new DateTimeOffset(2018, 3, 3, 9, 30, 0, TimeSpan.FromHours(9));

        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(berlin));
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(tokyo));

        Assert.Equal([berlin.Offset, tokyo.Offset], store.GetHistory(anna, "Körpergröße").Select(h => h.ValidAt.Offset));
        Assert.Equal(tokyo.Offset, store.GetCurrentValue(anna, "Körpergröße")!.ValidAt!.Value.Offset);
    }

    // ---------- H1 on disk ----------

    [Fact]
    public void H1_KeepsCurrentValueInEntityRow_AndOlderValuesInHistoryTable()
    {
        var path = TempFile();
        using (var store = SqliteStore.Create(path, Model(H1)))
        {
            var anna = store.CreateEntity("Mensch");
            store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
            store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14)); // newer: 155 moves to the history table
            store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10)); // late and older: straight into the history table
        }

        Assert.Equal(168L, Scalar(path, "SELECT \"Körpergröße\" FROM \"Mensch\""));
        Assert.Equal([142L, 155L], Longs(path, "SELECT \"Körpergröße\" FROM \"Mensch.Körpergröße\" ORDER BY \"_valid_at\""));
    }

    [Fact]
    public void H2_HasNoValueColumnInEntityRow()
    {
        var path = TempFile();
        using (SqliteStore.Create(path, Model(H2))) { }

        Assert.DoesNotContain("Körpergröße", Columns(path, "Mensch"));
        Assert.Contains("Mensch.Körpergröße", Tables(path));
    }

    // ---------- Changing the layout ----------

    [Theory] // E-25: no loss of information in either direction
    [InlineData(H2, H1)]
    [InlineData(H1, H2)]
    public void ChangeLayout_KeepsEveryValue(AttributeLayout from, AttributeLayout to)
    {
        using var store = Memory(from);
        var people = Fill(store);
        var before = Snapshot(store, people);

        store.ChangeLayout("Mensch", "Körpergröße", to);

        Assert.Equal(to, store.Structure.Entity("Mensch").Attribute("Körpergröße").Layout);
        Assert.Equal(before, Snapshot(store, people));
    }

    [Theory] // Writing after a change behaves like writing in that layout from the start
    [InlineData(H2, H1)]
    [InlineData(H1, H2)]
    public void ChangeLayout_ThenWrite_KeepsTheRules(AttributeLayout from, AttributeLayout to)
    {
        using var store = Memory(from);
        var anna = store.CreateEntity("Mensch");
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));

        store.ChangeLayout("Mensch", "Körpergröße", to);
        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10)); // late and older
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14)); // newer

        Assert.Equal(168L, store.GetCurrentValue(anna, "Körpergröße")!.Value);
        Assert.Equal([142L, 155L, 168L], store.GetHistory(anna, "Körpergröße").Select(h => h.Value));
    }

    [Fact]
    public void ChangeLayout_ThereAndBack_KeepsEveryValue()
    {
        using var store = Memory(H2);
        var people = Fill(store);
        var before = Snapshot(store, people);

        store.ChangeLayout("Mensch", "Körpergröße", H1);
        store.ChangeLayout("Mensch", "Körpergröße", H2);
        store.ChangeLayout("Mensch", "Körpergröße", H1);

        Assert.Equal(before, Snapshot(store, people));
    }

    [Fact]
    public void ChangeLayout_IsRecordedInTheFile() // E-33
    {
        var path = TempFile();
        List<EntityRef> people;
        List<object> before;
        using (var store = SqliteStore.Create(path, Model(H2)))
        {
            people = Fill(store);
            before = Snapshot(store, people);
            store.ChangeLayout("Mensch", "Körpergröße", H1);
        }

        using (var store = SqliteStore.Open(path))
        {
            Assert.Equal(H1, store.Structure.Entity("Mensch").Attribute("Körpergröße").Layout);
            Assert.Equal(before, Snapshot(store, people));
        }
        Assert.Contains("Körpergröße", Columns(path, "Mensch"));
    }

    [Fact]
    public void ChangeLayout_ToH2_RemovesTheColumnsFromTheEntityRow()
    {
        var path = TempFile();
        using (var store = SqliteStore.Create(path, Model(H1)))
        {
            Fill(store);
            store.ChangeLayout("Mensch", "Körpergröße", H2);
        }

        Assert.DoesNotContain(Columns(path, "Mensch"), c => c.StartsWith("Körpergröße", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangeLayout_ToTheSameLayout_ChangesNothing()
    {
        using var store = Memory(H2);
        var people = Fill(store);
        var before = Snapshot(store, people);

        store.ChangeLayout("Mensch", "Körpergröße", H2);

        Assert.Equal(before, Snapshot(store, people));
    }

    [Theory] // E-25: a layout that does not fit the information is refused, nothing changes
    [InlineData("Name", H1)]
    [InlineData("Wohnort", H2)]
    [InlineData("Körpergröße", AttributeLayout.Column)]
    public void ChangeLayout_RejectsLayoutsThatDoNotFitTheInformation(string attribute, AttributeLayout layout)
    {
        using var store = Memory(H2);
        var structure = store.Structure;

        Assert.Throws<ArgumentException>(() => store.ChangeLayout("Mensch", attribute, layout));
        Assert.Same(structure, store.Structure);
    }

    [Fact]
    public void ChangeLayout_LeavesOtherAttributesAlone()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");
        store.SetValue(anna, "Name", "Anna");
        store.SetValue(anna, "Wohnort", "Leipzig", ValueTime.At(Age10));

        store.ChangeLayout("Mensch", "Körpergröße", H1);
        store.ChangeLayout("Mensch", "Körpergröße", H2);

        Assert.Equal("Anna", store.GetCurrentValue(anna, "Name")!.Value);
        Assert.Equal(new StoredValue("Leipzig", Age10), store.GetCurrentValue(anna, "Wohnort"));
    }

    // A failure in the last step of a change rolls back every earlier step: tables, catalog and structure model (E-33).
    // The failure is injected from outside: H2 -> H1 ends with a delete from the history table, which a trigger refuses;
    // H1 -> H2 ends with dropping the current columns, which SQLite refuses while a view uses them.
    [Theory]
    [InlineData(H2, H1, """CREATE TRIGGER "fail" BEFORE DELETE ON "Mensch.Körpergröße" BEGIN SELECT RAISE(ABORT, 'injected'); END""")]
    [InlineData(H1, H2, """CREATE VIEW "fail" AS SELECT "Körpergröße" FROM "Mensch" """)]
    public void ChangeLayout_FailingInTheLastStep_RollsBackEverything(AttributeLayout from, AttributeLayout to, string sabotage)
    {
        var path = TempFile();
        using var store = SqliteStore.Create(path, Model(from), new FixedTimeProvider(Now));
        var people = Fill(store);
        var before = Snapshot(store, people);
        var structure = store.Structure;
        var columns = Columns(path, "Mensch");
        var historyRows = Scalar(path, """SELECT count(*) FROM "Mensch.Körpergröße" """);
        Execute(path, sabotage);

        Assert.ThrowsAny<SqliteException>(() => store.ChangeLayout("Mensch", "Körpergröße", to));

        Assert.Same(structure, store.Structure);
        Assert.Equal(from.ToString(), Scalar(path,
            """SELECT layout FROM "_tursiops_structure" WHERE entity = 'Mensch' AND attribute = 'Körpergröße'"""));
        Assert.Equal(columns, Columns(path, "Mensch"));
        Assert.Equal(historyRows, Scalar(path, """SELECT count(*) FROM "Mensch.Körpergröße" """));
        Assert.Equal(before, Snapshot(store, people));
    }

    // ---------- Helpers ----------

    private static StructureModel Model(AttributeLayout? history = null)
    {
        var model = StructureModel.FromOntology(TurParser.Parse(Source).Ontology);
        return history is { } layout ? model.WithLayout("Mensch", "Körpergröße", layout) : model;
    }

    private static SqliteStore Memory(AttributeLayout history) =>
        SqliteStore.Create(":memory:", Model(history), new FixedTimeProvider(Now));

    // Several people: out-of-order writes, equal valid times, a null value, different offsets, one without any value.
    private static List<EntityRef> Fill(SqliteStore store)
    {
        var anna = store.CreateEntity("Mensch");
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));

        var ben = store.CreateEntity("Mensch");
        store.SetValue(ben, "Körpergröße", 150L, ValueTime.At(Age12));
        store.SetValue(ben, "Körpergröße", 151L, ValueTime.At(Age12));
        store.SetValue(ben, "Körpergröße", 149L, ValueTime.At(Age12.ToOffset(TimeSpan.FromHours(2))));

        var carla = store.CreateEntity("Mensch");
        store.SetValue(carla, "Körpergröße", 160.5, ValueTime.At(Age10));
        store.SetValue(carla, "Körpergröße", null, ValueTime.At(Age14));

        var dora = store.CreateEntity("Mensch");

        return [anna, ben, carla, dora];
    }

    private static List<object> Snapshot(SqliteStore store, IEnumerable<EntityRef> people) =>
        people.SelectMany<EntityRef, object>(p =>
        [
            store.GetCurrentValue(p, "Körpergröße") ?? (object)"none",
            .. store.GetHistory(p, "Körpergröße").Select(h => (object)(h.Value ?? "null", h.ValidAt, h.ValidAt.Offset)),
            "end",
        ]).ToList();

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tursiops-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = Connect(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Connect(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<long> Longs(string path, string sql)
    {
        using var connection = Connect(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        while (reader.Read())
            values.Add(reader.GetInt64(0));
        return values;
    }

    private static List<string> Columns(string path, string table)
    {
        using var connection = Connect(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static List<string> Tables(string path)
    {
        using var connection = Connect(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
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
