using Microsoft.Data.Sqlite;
using Tursiops.Core.Format;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Storage;

// Tests for the translation layer to SQLite (E-21, E-23, E-25 to E-27).
public class SqliteStoreTests : IDisposable
{
    private const string Source = """
        Mensch {
          Name
          Wohnort @time
          Körpergröße @history
        }
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age10 = new(2016, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age12 = new(2018, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age14 = new(2020, 3, 3, 0, 0, 0, TimeSpan.Zero);

    private readonly List<string> _files = [];

    // ---------- Structure model ----------

    [Fact]
    public void StructureFromOntology_FollowsTheInformationTheUserDescribed() // E-25, docs/storage.md
    {
        var mensch = Assert.Single(Model().Entities);

        Assert.Equal(new AttributeStructure("Name", AttributeTime.Timeless, AttributeLayout.Column), mensch.Attribute("Name"));
        Assert.Equal(new AttributeStructure("Wohnort", AttributeTime.CurrentOnly, AttributeLayout.ColumnWithTime), mensch.Attribute("Wohnort"));
        Assert.Equal(new AttributeStructure("Körpergröße", AttributeTime.History, AttributeLayout.HistoryTable), mensch.Attribute("Körpergröße"));
    }

    [Theory] // E-25: a layout never changes the information
    [InlineData("Name", AttributeLayout.HistoryTable)]
    [InlineData("Wohnort", AttributeLayout.HistoryTable)]
    [InlineData("Körpergröße", AttributeLayout.ColumnWithTime)]
    [InlineData("Körpergröße", AttributeLayout.Column)]
    public void WithLayout_RejectsLayoutsThatDoNotFitTheInformation(string attribute, AttributeLayout layout)
    {
        Assert.Throws<ArgumentException>(() => Model().WithLayout("Mensch", attribute, layout));
    }

    [Fact]
    public void WithLayout_ReturnsChangedCopy_ForAnAllowedLayout()
    {
        var model = Model();
        var changed = model.WithLayout("Mensch", "Körpergröße", AttributeLayout.HistoryTable);

        Assert.Equal(model.Entity("Mensch").Attribute("Körpergröße"), changed.Entity("Mensch").Attribute("Körpergröße"));
        Assert.NotSame(model, changed);
    }

    // ---------- Creating and opening ----------

    [Fact]
    public void Create_UsesOntologyNamesForTables() // E-21
    {
        var path = TempFile();
        using (SqliteStore.Create(path, Model())) { }

        var tables = Tables(path);
        Assert.Contains("Mensch", tables);
        Assert.Contains("Mensch.Körpergröße", tables);
        Assert.DoesNotContain("Mensch.Wohnort", tables); // @time keeps no history (E-25)
        Assert.Contains("_tursiops_structure", tables);
    }

    [Fact]
    public void Create_RefusesAnExistingTursiopsDatabase()
    {
        var path = TempFile();
        using (SqliteStore.Create(path, Model())) { }

        Assert.Throws<InvalidOperationException>(() => SqliteStore.Create(path, Model()));
    }

    [Fact]
    public void Open_RestoresStructureModelFromTheFile() // E-33
    {
        var path = TempFile();
        using (SqliteStore.Create(path, Model())) { }

        using var store = SqliteStore.Open(path);
        Assert.Equal(Model().Entities.Single().Attributes, store.Structure.Entity("Mensch").Attributes);
    }

    [Fact]
    public void Open_RefusesAFileWithoutTursiopsStructure()
    {
        var path = TempFile();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE other (x)";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.Throws<InvalidOperationException>(() => SqliteStore.Open(path));
    }

    [Fact]
    public void Values_SurviveClosingAndOpening()
    {
        var path = TempFile();
        EntityRef anna;
        using (var store = SqliteStore.Create(path, Model()))
        {
            anna = store.CreateEntity("Mensch");
            store.SetValue(anna, "Name", "Anna");
            store.SetValue(anna, "Wohnort", "Leipzig", ValueTime.At(Age10));
            store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));
        }

        using (var store = SqliteStore.Open(path))
        {
            Assert.Equal("Anna", store.GetCurrentValue(anna, "Name")!.Value);
            Assert.Equal("Leipzig", store.GetCurrentValue(anna, "Wohnort")!.Value);
            Assert.Single(store.GetHistory(anna, "Körpergröße"));
        }
    }

    // ---------- Entities ----------

    [Fact]
    public void CreateEntity_HandsOutIncreasingIds() // E-23
    {
        using var store = Memory(Model());

        var first = store.CreateEntity("Mensch");
        var second = store.CreateEntity("Mensch");

        Assert.Equal("Mensch", first.Entity);
        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public void UnknownEntity_IsAnError()
    {
        using var store = Memory(Model());
        Assert.Throws<ArgumentException>(() => store.CreateEntity("Tier"));
    }

    [Fact]
    public void UnknownAttribute_IsAnError()
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");
        Assert.Throws<ArgumentException>(() => store.SetValue(anna, "Gewicht", 60L));
    }

    [Fact]
    public void EntityThatDoesNotExist_IsAnError()
    {
        using var store = Memory(Model());
        var ghost = new EntityRef("Mensch", 999);
        Assert.Throws<ArgumentException>(() => store.SetValue(ghost, "Name", "Geist"));
    }

    // ---------- Values without @time ----------

    [Fact]
    public void UntimedValue_IsSetAndRead()
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Name", "Anna");
        store.SetValue(anna, "Name", "Anna Lena");

        var current = store.GetCurrentValue(anna, "Name");
        Assert.Equal("Anna Lena", current!.Value);
        Assert.Null(current.ValidAt);
    }

    [Fact]
    public void ValueNeverSet_ReadsAsNull()
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        Assert.Null(store.GetCurrentValue(anna, "Name"));
        Assert.Null(store.GetCurrentValue(anna, "Wohnort"));
        Assert.Null(store.GetCurrentValue(anna, "Körpergröße"));
    }

    [Fact]
    public void UntimedValue_RejectsATime() // E-27
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        Assert.Throws<ArgumentException>(() => store.SetValue(anna, "Name", "Anna", ValueTime.Now));
    }

    [Theory] // E-25: only @history keeps a history as part of the information
    [InlineData("Name")]
    [InlineData("Wohnort")]
    public void History_OfAttributeWithoutHistory_IsAnError(string attribute)
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        var error = Assert.Throws<ArgumentException>(() => store.GetHistory(anna, attribute));
        Assert.Contains("@history", error.Message);
    }

    [Fact]
    public void ValuesKeepTheirType() // E-13
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Name", 42L);
        Assert.Equal(42L, store.GetCurrentValue(anna, "Name")!.Value);

        store.SetValue(anna, "Name", 4.2);
        Assert.Equal(4.2, store.GetCurrentValue(anna, "Name")!.Value);

        store.SetValue(anna, "Name", "Anna");
        Assert.Equal("Anna", store.GetCurrentValue(anna, "Name")!.Value);
    }

    // ---------- Values with @time and @history ----------

    [Theory] // E-27
    [InlineData("Wohnort")]
    [InlineData("Körpergröße")]
    public void TimedValue_RequiresAnExplicitTime(string attribute)
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        var error = Assert.Throws<ArgumentException>(() => store.SetValue(anna, attribute, 142L));
        Assert.Contains("ValueTime", error.Message);
    }

    [Theory] // E-27
    [InlineData("Wohnort")]
    [InlineData("Körpergröße")]
    public void ValueTimeNow_IsFilledInByTheDatabase(string attribute)
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, attribute, 142L, ValueTime.Now);

        Assert.Equal(Now, store.GetCurrentValue(anna, attribute)!.ValidAt);
    }

    [Theory] // E-25: a late entry of an older measurement does not replace the current one
    [InlineData("Wohnort")]
    [InlineData("Körpergröße")]
    public void CurrentValue_IsTheLatestByValidTime_NotByWriteOrder(string attribute)
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, attribute, 155L, ValueTime.At(Age12));
        store.SetValue(anna, attribute, 168L, ValueTime.At(Age14));
        store.SetValue(anna, attribute, 142L, ValueTime.At(Age10));

        var current = store.GetCurrentValue(anna, attribute);
        Assert.Equal(168L, current!.Value);
        Assert.Equal(Age14, current.ValidAt);
    }

    [Fact]
    public void Time_KeepsOnlyTheLatestValue_InTheDatabase() // E-25: @time
    {
        var path = TempFile();
        using (var store = SqliteStore.Create(path, Model()))
        {
            var anna = store.CreateEntity("Mensch");
            store.SetValue(anna, "Wohnort", "Dresden", ValueTime.At(Age10));
            store.SetValue(anna, "Wohnort", "Leipzig", ValueTime.At(Age12));
        }

        // The older value is gone from the file itself, not just hidden.
        Assert.Equal(0L, Count(path, "SELECT count(*) FROM \"Mensch\" WHERE \"Wohnort\" = 'Dresden'"));
        Assert.Equal(1L, Count(path, "SELECT count(*) FROM \"Mensch\" WHERE \"Wohnort\" = 'Leipzig'"));
    }

    [Fact]
    public void History_KeepsAllValues_OrderedByValidTime() // E-25: @history
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));

        var history = store.GetHistory(anna, "Körpergröße");
        Assert.Equal([142L, 155L, 168L], history.Select(h => h.Value));
        Assert.Equal([Age10, Age12, Age14], history.Select(h => h.ValidAt));
    }

    [Fact]
    public void HistoryOfDifferentEntities_IsSeparate()
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");
        var ben = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));
        store.SetValue(ben, "Körpergröße", 150L, ValueTime.At(Age10));
        store.SetValue(ben, "Körpergröße", 160L, ValueTime.At(Age12));

        Assert.Single(store.GetHistory(anna, "Körpergröße"));
        Assert.Equal(2, store.GetHistory(ben, "Körpergröße").Count);
    }

    [Theory]
    [InlineData("Wohnort")]
    [InlineData("Körpergröße")]
    public void ValidTimes_KeepTheirOffset(string attribute)
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");
        var berlin = new DateTimeOffset(2016, 3, 3, 9, 30, 0, TimeSpan.FromHours(1));

        store.SetValue(anna, attribute, 142L, ValueTime.At(berlin));

        var stored = store.GetCurrentValue(anna, attribute)!.ValidAt!.Value;
        Assert.Equal(berlin, stored);
        Assert.Equal(berlin.Offset, stored.Offset);
    }

    // ---------- Helpers ----------

    private static StructureModel Model() => StructureModel.FromOntology(TurParser.Parse(Source).Ontology);

    private static SqliteStore Memory(StructureModel model) =>
        SqliteStore.Create(":memory:", model, new FixedTimeProvider(Now));

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tursiops-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static long Count(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static List<string> Tables(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
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
