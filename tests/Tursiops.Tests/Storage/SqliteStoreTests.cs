using Microsoft.Data.Sqlite;
using Tursiops.Core.Format;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Storage;

// Tests for the translation layer to SQLite (E-20 to E-23).
public class SqliteStoreTests : IDisposable
{
    private const string Source = """
        Mensch {
          Name
          Körpergröße @time
        }
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age10 = new(2016, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age12 = new(2018, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age14 = new(2020, 3, 3, 0, 0, 0, TimeSpan.Zero);

    private readonly List<string> _files = [];

    // ---------- Structure model ----------

    [Fact]
    public void StructureFromOntology_StartsWithCurrentValueEverywhere() // E-02: start simple
    {
        var model = StructureModel.FromOntology(TurParser.Parse(Source));

        var mensch = Assert.Single(model.Entities);
        Assert.All(mensch.Attributes, a => Assert.Equal(AttributeLayout.CurrentValue, a.Layout));
        Assert.True(mensch.Attribute("Körpergröße").IsTimed);
        Assert.False(mensch.Attribute("Name").IsTimed);
    }

    [Fact]
    public void HistoryLayout_IsOnlyAllowedForTimedAttributes() // E-03
    {
        var model = StructureModel.FromOntology(TurParser.Parse(Source));

        var error = Assert.Throws<ArgumentException>(() => model.WithLayout("Mensch", "Name", AttributeLayout.History));
        Assert.Contains("@time", error.Message);
    }

    [Fact]
    public void WithLayout_ReturnsChangedCopy()
    {
        var model = StructureModel.FromOntology(TurParser.Parse(Source));
        var changed = model.WithLayout("Mensch", "Körpergröße", AttributeLayout.History);

        Assert.Equal(AttributeLayout.CurrentValue, model.Entity("Mensch").Attribute("Körpergröße").Layout);
        Assert.Equal(AttributeLayout.History, changed.Entity("Mensch").Attribute("Körpergröße").Layout);
    }

    // ---------- Creating and opening ----------

    [Fact]
    public void Create_UsesOntologyNamesForTables() // E-21
    {
        var path = TempFile();
        using (SqliteStore.Create(path, History())) { }

        Assert.Contains("Mensch", Tables(path));
        Assert.Contains("Mensch.Körpergröße", Tables(path));
        Assert.Contains("_tursiops_structure", Tables(path));
    }

    [Fact]
    public void Create_RefusesAnExistingTursiopsDatabase()
    {
        var path = TempFile();
        using (SqliteStore.Create(path, Current())) { }

        Assert.Throws<InvalidOperationException>(() => SqliteStore.Create(path, Current()));
    }

    [Fact]
    public void Open_RestoresStructureModelFromTheFile() // E-20
    {
        var path = TempFile();
        using (SqliteStore.Create(path, History())) { }

        using var store = SqliteStore.Open(path);
        Assert.Equal(AttributeLayout.History, store.Structure.Entity("Mensch").Attribute("Körpergröße").Layout);
        Assert.Equal(AttributeLayout.CurrentValue, store.Structure.Entity("Mensch").Attribute("Name").Layout);
        Assert.True(store.Structure.Entity("Mensch").Attribute("Körpergröße").IsTimed);
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
        using (var store = SqliteStore.Create(path, History()))
        {
            anna = store.CreateEntity("Mensch");
            store.SetValue(anna, "Name", "Anna");
            store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));
        }

        using (var store = SqliteStore.Open(path))
        {
            Assert.Equal("Anna", store.GetCurrentValue(anna, "Name")!.Value);
            Assert.Single(store.GetHistory(anna, "Körpergröße"));
        }
    }

    // ---------- Entities ----------

    [Fact]
    public void CreateEntity_HandsOutIncreasingIds() // E-23
    {
        using var store = Memory(Current());

        var first = store.CreateEntity("Mensch");
        var second = store.CreateEntity("Mensch");

        Assert.Equal("Mensch", first.Entity);
        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public void UnknownEntity_IsAnError()
    {
        using var store = Memory(Current());
        Assert.Throws<ArgumentException>(() => store.CreateEntity("Tier"));
    }

    [Fact]
    public void UnknownAttribute_IsAnError()
    {
        using var store = Memory(Current());
        var anna = store.CreateEntity("Mensch");
        Assert.Throws<ArgumentException>(() => store.SetValue(anna, "Gewicht", 60L));
    }

    [Fact]
    public void EntityThatDoesNotExist_IsAnError()
    {
        using var store = Memory(Current());
        var ghost = new EntityRef("Mensch", 999);
        Assert.Throws<ArgumentException>(() => store.SetValue(ghost, "Name", "Geist"));
    }

    // ---------- Values without @time ----------

    [Fact]
    public void UntimedValue_IsSetAndRead()
    {
        using var store = Memory(Current());
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
        using var store = Memory(Current());
        var anna = store.CreateEntity("Mensch");

        Assert.Null(store.GetCurrentValue(anna, "Name"));
        Assert.Null(store.GetCurrentValue(anna, "Körpergröße"));
    }

    [Fact]
    public void UntimedValue_RejectsATime() // E-22
    {
        using var store = Memory(Current());
        var anna = store.CreateEntity("Mensch");

        Assert.Throws<ArgumentException>(() => store.SetValue(anna, "Name", "Anna", ValueTime.Now));
    }

    [Fact]
    public void History_OfUntimedAttribute_IsAnError()
    {
        using var store = Memory(Current());
        var anna = store.CreateEntity("Mensch");

        Assert.Throws<ArgumentException>(() => store.GetHistory(anna, "Name"));
    }

    [Fact]
    public void ValuesKeepTheirType() // E-13
    {
        using var store = Memory(Current());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Name", 42L);
        Assert.Equal(42L, store.GetCurrentValue(anna, "Name")!.Value);

        store.SetValue(anna, "Name", 4.2);
        Assert.Equal(4.2, store.GetCurrentValue(anna, "Name")!.Value);

        store.SetValue(anna, "Name", "Anna");
        Assert.Equal("Anna", store.GetCurrentValue(anna, "Name")!.Value);
    }

    // ---------- Values with @time ----------

    [Fact]
    public void TimedValue_RequiresAnExplicitTime() // E-22
    {
        using var store = Memory(Current());
        var anna = store.CreateEntity("Mensch");

        var error = Assert.Throws<ArgumentException>(() => store.SetValue(anna, "Körpergröße", 142L));
        Assert.Contains("ValueTime", error.Message);
    }

    [Fact]
    public void ValueTimeNow_IsFilledInByTheDatabase() // E-22
    {
        using var store = Memory(Current());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 142L, ValueTime.Now);

        Assert.Equal(Now, store.GetCurrentValue(anna, "Körpergröße")!.ValidAt);
    }

    [Theory]
    [InlineData(AttributeLayout.CurrentValue)]
    [InlineData(AttributeLayout.History)]
    public void CurrentValue_IsTheLatestByValidTime_NotByWriteOrder(AttributeLayout layout)
    {
        using var store = Memory(layout == AttributeLayout.History ? History() : Current());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10)); // late entry of an older measurement

        var current = store.GetCurrentValue(anna, "Körpergröße");
        Assert.Equal(168L, current!.Value);
        Assert.Equal(Age14, current.ValidAt);
    }

    [Fact]
    public void HistoryLayout_KeepsAllValues_OrderedByValidTime()
    {
        using var store = Memory(History());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));

        var history = store.GetHistory(anna, "Körpergröße");
        Assert.Equal([142L, 155L, 168L], history.Select(h => h.Value));
        Assert.Equal([Age10, Age12, Age14], history.Select(h => h.ValidAt));
    }

    [Fact]
    public void CurrentValueLayout_KeepsOnlyTheLatest()
    {
        using var store = Memory(Current());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));

        var history = Assert.Single(store.GetHistory(anna, "Körpergröße"));
        Assert.Equal(155L, history.Value);
    }

    [Fact]
    public void HistoryOfDifferentEntities_IsSeparate()
    {
        using var store = Memory(History());
        var anna = store.CreateEntity("Mensch");
        var ben = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10));
        store.SetValue(ben, "Körpergröße", 150L, ValueTime.At(Age10));
        store.SetValue(ben, "Körpergröße", 160L, ValueTime.At(Age12));

        Assert.Single(store.GetHistory(anna, "Körpergröße"));
        Assert.Equal(2, store.GetHistory(ben, "Körpergröße").Count);
    }

    [Fact]
    public void ValidTimes_KeepTheirOffset()
    {
        using var store = Memory(History());
        var anna = store.CreateEntity("Mensch");
        var berlin = new DateTimeOffset(2016, 3, 3, 9, 30, 0, TimeSpan.FromHours(1));

        store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(berlin));

        var stored = Assert.Single(store.GetHistory(anna, "Körpergröße")).ValidAt;
        Assert.Equal(berlin, stored);
        Assert.Equal(berlin.Offset, stored.Offset);
    }

    // ---------- Helpers ----------

    private static StructureModel Current() => StructureModel.FromOntology(TurParser.Parse(Source));

    private static StructureModel History() =>
        Current().WithLayout("Mensch", "Körpergröße", AttributeLayout.History);

    private static SqliteStore Memory(StructureModel model) =>
        SqliteStore.Create(":memory:", model, new FixedTimeProvider(Now));

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tursiops-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
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
