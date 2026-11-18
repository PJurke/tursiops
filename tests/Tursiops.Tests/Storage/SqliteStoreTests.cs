using Microsoft.Data.Sqlite;
using Tursiops.Core.Format;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Storage;

// Tests for the translation layer to SQLite (E-21, E-23, E-25 to E-27, E-41, E-42).
public class SqliteStoreTests : IDisposable
{
    private const string Source = """
        Mensch {
          Name: text
          Wohnort: text @time
          Körpergröße: number @history
        }
        """;

    private const string TypesSource = """
        Probe {
          Zahl: number
          Wort: text
          Tag: date
          Wahr: bi
          Messung: number @history
          Termin: date @history
          Erfüllt: bi @history
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

        Assert.Equal(new AttributeStructure("Name", BaseType.Text, AttributeTime.Timeless, AttributeLayout.Column), mensch.Attribute("Name"));
        Assert.Equal(new AttributeStructure("Wohnort", BaseType.Text, AttributeTime.CurrentOnly, AttributeLayout.ColumnWithTime), mensch.Attribute("Wohnort"));
        Assert.Equal(new AttributeStructure("Körpergröße", BaseType.Number, AttributeTime.History, AttributeLayout.HistoryTable), mensch.Attribute("Körpergröße"));
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

    // ---------- Base types (E-41) ----------

    [Fact]
    public void BaseTypes_SurviveClosingAndOpening()
    {
        var path = TempFile();
        var model = TypesModel();
        using (SqliteStore.Create(path, model)) { }

        using var store = SqliteStore.Open(path);
        Assert.Equal(model.Entity("Probe").Attributes, store.Structure.Entity("Probe").Attributes);
        Assert.Equal(BaseType.Bi, store.Structure.Entity("Probe").Attribute("Wahr").Type);
    }

    [Theory]
    [MemberData(nameof(FittingValues))]
    public void ValuesOfTheDeclaredType_AreAccepted(string attribute, object value)
    {
        using var store = Memory(TypesModel());
        var probe = store.CreateEntity("Probe");

        store.SetValue(probe, attribute, value);

        Assert.NotNull(store.GetCurrentValue(probe, attribute));
    }

    public static TheoryData<string, object> FittingValues() => new()
    {
        { "Zahl", 42 }, { "Zahl", 42L }, { "Zahl", 4.2 }, { "Zahl", 1.81m },
        { "Wort", "Anna" }, { "Wort", "180" },
        { "Tag", Age10 }, { "Tag", new DateOnly(2016, 3, 3) },
        { "Wahr", true }, { "Wahr", false },
    };

    [Theory]
    [MemberData(nameof(ValuesOfAnotherType))]
    public void ValuesOfAnotherType_AreRejected_NotConverted(string attribute, object value, string typeName)
    {
        using var store = Memory(TypesModel());
        var probe = store.CreateEntity("Probe");

        var error = Assert.Throws<ArgumentException>(() => store.SetValue(probe, attribute, value));

        Assert.Contains($"Probe.{attribute}", error.Message);
        Assert.Contains(typeName, error.Message);
        Assert.Null(store.GetCurrentValue(probe, attribute));
    }

    public static TheoryData<string, object, string> ValuesOfAnotherType() => new()
    {
        { "Zahl", "181", "number" }, { "Zahl", true, "number" }, { "Zahl", Age10, "number" },
        { "Wort", 181L, "text" }, { "Wort", false, "text" },
        { "Tag", "2016-03-03", "date" }, { "Tag", 20160303L, "date" },
        { "Wahr", 1L, "bi" }, { "Wahr", "true", "bi" },
    };

    [Fact] // E-42: DateTime has no time zone, so the stored value would not say what was meant
    public void DateTime_IsRejected()
    {
        using var store = Memory(TypesModel());
        var probe = store.CreateEntity("Probe");

        var error = Assert.Throws<ArgumentException>(() => store.SetValue(probe, "Tag", Age10.UtcDateTime));

        Assert.Contains("DateTimeOffset", error.Message);
        Assert.Null(store.GetCurrentValue(probe, "Tag"));
    }

    [Theory] // E-42: values come back in their base type, not as SQLite keeps them
    [MemberData(nameof(ValuesAndHowTheyComeBack))]
    public void ValuesComeBackInTheirBaseType(string attribute, object written, object expected)
    {
        var path = TempFile();
        EntityRef probe;
        using (var store = SqliteStore.Create(path, TypesModel()))
        {
            probe = store.CreateEntity("Probe");
            store.SetValue(probe, attribute, written);
            AssertSameValue(expected, store.GetCurrentValue(probe, attribute)!.Value);
        }

        using var reopened = SqliteStore.Open(path);
        AssertSameValue(expected, reopened.GetCurrentValue(probe, attribute)!.Value);
        AssertSameValue(expected, Assert.Single(reopened.GetCurrentValues("Probe", attribute)).Value.Value);
    }

    public static TheoryData<string, object, object> ValuesAndHowTheyComeBack() => new()
    {
        { "Zahl", 42L, 42L },
        { "Zahl", 42, 42L },          // whole numbers come back as long
        { "Zahl", 4.2, 4.2 },
        { "Zahl", 1.81m, 1.81m },     // decimal stays decimal, SQLite keeps it as text
        { "Zahl", 0.1m + 0.2m, 0.3m },
        { "Wort", "180", "180" },
        { "Tag", Berlin, Berlin },    // with its offset
        { "Tag", new DateTimeOffset(2016, 3, 3, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2016, 3, 3, 0, 0, 0, TimeSpan.Zero) },
        { "Tag", new DateOnly(2016, 3, 3), new DateOnly(2016, 3, 3) },
        { "Wahr", true, true },
        { "Wahr", false, false },
    };

    [Theory] // E-42: also in the history, in both layouts and after a layout change
    [InlineData(AttributeLayout.HistoryTable)]
    [InlineData(AttributeLayout.CurrentColumnAndHistoryTable)]
    public void HistoryValuesComeBackInTheirBaseType(AttributeLayout layout)
    {
        var model = TypesModel()
            .WithLayout("Probe", "Messung", layout)
            .WithLayout("Probe", "Termin", layout)
            .WithLayout("Probe", "Erfüllt", layout);
        using var store = Memory(model);
        var probe = store.CreateEntity("Probe");
        store.SetValue(probe, "Messung", 1.81m, ValueTime.At(Age10));
        store.SetValue(probe, "Messung", 182L, ValueTime.At(Age12));
        store.SetValue(probe, "Termin", new DateOnly(2016, 3, 3), ValueTime.At(Age10));
        store.SetValue(probe, "Termin", Berlin, ValueTime.At(Age12));
        store.SetValue(probe, "Erfüllt", false, ValueTime.At(Age10));
        store.SetValue(probe, "Erfüllt", true, ValueTime.At(Age12));

        void Check()
        {
            AssertValues([1.81m, 182L], store.GetHistory(probe, "Messung"));
            AssertValues([new DateOnly(2016, 3, 3), Berlin], store.GetHistory(probe, "Termin"));
            AssertValues([false, true], store.GetHistory(probe, "Erfüllt"));
            AssertSameValue(true, store.GetCurrentValue(probe, "Erfüllt")!.Value);
        }

        Check();
        var other = layout == AttributeLayout.HistoryTable ? AttributeLayout.CurrentColumnAndHistoryTable : AttributeLayout.HistoryTable;
        foreach (var attribute in new[] { "Messung", "Termin", "Erfüllt" })
            store.ChangeLayout("Probe", attribute, other);
        Check();
    }

    [Fact]
    public void TimedValueOfAnotherType_IsRejected()
    {
        using var store = Memory(TypesModel());
        var probe = store.CreateEntity("Probe");

        Assert.Throws<ArgumentException>(() => store.SetValue(probe, "Messung", "181", ValueTime.At(Age10)));

        Assert.Empty(store.GetHistory(probe, "Messung"));
    }

    [Theory]
    [InlineData("Zahl")]
    [InlineData("Wort")]
    [InlineData("Tag")]
    public void Null_IsAllowed_ExceptForBi(string attribute)
    {
        using var store = Memory(TypesModel());
        var probe = store.CreateEntity("Probe");

        store.SetValue(probe, attribute, null);

        Assert.Null(store.GetCurrentValue(probe, attribute));
    }

    [Fact]
    public void Null_IsRejectedForBi()
    {
        using var store = Memory(TypesModel());
        var probe = store.CreateEntity("Probe");
        store.SetValue(probe, "Wahr", true);

        var error = Assert.Throws<ArgumentException>(() => store.SetValue(probe, "Wahr", null));

        Assert.Contains("bi", error.Message);
        Assert.NotNull(store.GetCurrentValue(probe, "Wahr")); // the earlier value stays
    }

    [Fact]
    public void BiThatWasNeverSet_HasNoValue()
    {
        using var store = Memory(TypesModel());
        var probe = store.CreateEntity("Probe");

        Assert.Null(store.GetCurrentValue(probe, "Wahr"));
    }

    [Fact]
    public void DatabaseWithoutDeclaredTypes_IsRejectedOnOpening() // E-41: not converted
    {
        var path = TempFile();
        using (SqliteStore.Create(path, Model())) { }
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE \"_tursiops_structure\" DROP COLUMN type";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var error = Assert.Throws<InvalidOperationException>(() => SqliteStore.Open(path));
        Assert.Contains("types", error.Message);
    }

    // ---------- Values with @time and @history ----------

    [Theory] // E-27
    [InlineData("Wohnort")]
    [InlineData("Körpergröße")]
    public void TimedValue_RequiresAnExplicitTime(string attribute)
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        var error = Assert.Throws<ArgumentException>(() => store.SetValue(anna, attribute, Typed(attribute, 142)));
        Assert.Contains("ValueTime", error.Message);
    }

    [Theory] // E-27
    [InlineData("Wohnort")]
    [InlineData("Körpergröße")]
    public void ValueTimeNow_IsFilledInByTheDatabase(string attribute)
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, attribute, Typed(attribute, 142), ValueTime.Now);

        Assert.Equal(Now, store.GetCurrentValue(anna, attribute)!.ValidAt);
    }

    [Theory] // E-25: a late entry of an older measurement does not replace the current one
    [InlineData("Wohnort")]
    [InlineData("Körpergröße")]
    public void CurrentValue_IsTheLatestByValidTime_NotByWriteOrder(string attribute)
    {
        using var store = Memory(Model());
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, attribute, Typed(attribute, 155), ValueTime.At(Age12));
        store.SetValue(anna, attribute, Typed(attribute, 168), ValueTime.At(Age14));
        store.SetValue(anna, attribute, Typed(attribute, 142), ValueTime.At(Age10));

        var current = store.GetCurrentValue(anna, attribute);
        Assert.Equal(Typed(attribute, 168), current!.Value);
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

        store.SetValue(anna, attribute, Typed(attribute, 142), ValueTime.At(berlin));

        var stored = store.GetCurrentValue(anna, attribute)!.ValidAt!.Value;
        Assert.Equal(berlin, stored);
        Assert.Equal(berlin.Offset, stored.Offset);
    }

    // ---------- Helpers ----------

    private static StructureModel Model() => StructureModel.FromOntology(TurParser.Parse(Source).Ontology);

    private static readonly DateTimeOffset Berlin = new(2016, 3, 3, 9, 30, 15, 250, TimeSpan.FromHours(1));

    // Same value and same .NET type; for dates also the same offset.
    private static void AssertSameValue(object expected, object? actual)
    {
        Assert.NotNull(actual);
        Assert.IsType(expected.GetType(), actual);
        Assert.Equal(expected, actual);
        if (expected is DateTimeOffset date)
            Assert.Equal(date.Offset, ((DateTimeOffset)actual).Offset);
    }

    private static void AssertValues(object[] expected, IReadOnlyList<TimedValue> history)
    {
        Assert.Equal(expected.Length, history.Count);
        for (var i = 0; i < expected.Length; i++)
            AssertSameValue(expected[i], history[i].Value);
    }

    private static StructureModel TypesModel() => StructureModel.FromOntology(TurParser.Parse(TypesSource).Ontology);

    // A value of the declared type (E-41): Wohnort is text, Körpergröße a number.
    private static object Typed(string attribute, long n) => attribute == "Wohnort" ? $"Ort {n}" : n;

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
