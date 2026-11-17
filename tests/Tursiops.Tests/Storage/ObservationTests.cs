using Microsoft.Data.Sqlite;
using Tursiops.Core.Format;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Storage;

// Observation (E-31, E-32): Tursiops counts per attribute and waking phase how it is used and which
// technical types its values have, without changing anything. Counters live in memory and are written
// to the database file when it is closed and before each sleep.
public class ObservationTests : IDisposable
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

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age10 = new(2016, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age12 = new(2018, 3, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Age14 = new(2020, 3, 3, 0, 0, 0, TimeSpan.Zero);

    private readonly List<string> _files = [];

    // ---------- Counting operations ----------

    [Theory] // test 1
    [InlineData(H1)]
    [InlineData(H2)]
    public void Operations_AreCountedPerAttribute(AttributeLayout layout)
    {
        using var store = Memory(layout);
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Name", "Anna");
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.GetCurrentValue(anna, "Körpergröße");
        store.GetCurrentValue(anna, "Körpergröße");
        store.GetCurrentValue(anna, "Körpergröße");
        store.GetHistory(anna, "Körpergröße");
        store.GetCurrentValue(anna, "Name");

        var height = store.GetObservations("Mensch", "Körpergröße");
        Assert.Equal(2, height.Writes);
        Assert.Equal(3, height.CurrentReads);
        Assert.Equal(1, height.HistoryReads);

        var name = store.GetObservations("Mensch", "Name");
        Assert.Equal(1, name.Writes);
        Assert.Equal(1, name.CurrentReads);
        Assert.Equal(0, name.HistoryReads);

        Assert.Equal(AttributeObservations.None, store.GetObservations("Mensch", "Wohnort"));
    }

    [Theory] // test 2: older than the current value means late; the same valid time is not older
    [InlineData("Wohnort", null)]
    [InlineData("Körpergröße", H1)]
    [InlineData("Körpergröße", H2)]
    public void LateWrites_AreCountedAsWritesAndAsLate(string attribute, AttributeLayout? layout)
    {
        using var store = Memory(layout ?? H2);
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, attribute, 1L, ValueTime.At(Age12));
        store.SetValue(anna, attribute, 2L, ValueTime.At(Age14));
        store.SetValue(anna, attribute, 3L, ValueTime.At(Age10)); // late
        store.SetValue(anna, attribute, 4L, ValueTime.At(Age14)); // same time as current, not late

        var observed = store.GetObservations("Mensch", attribute);
        Assert.Equal(4, observed.Writes);
        Assert.Equal(1, observed.LateWrites);
    }

    [Fact] // test 2: lateness is per entity
    public void LateWrites_ComparePerEntity()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");
        var ben = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
        store.SetValue(ben, "Körpergröße", 142L, ValueTime.At(Age10));

        Assert.Equal(0, store.GetObservations("Mensch", "Körpergröße").LateWrites);
    }

    [Fact] // test 3
    public void CreatingEntities_IsNotCounted()
    {
        using var store = Memory(H2);
        store.CreateEntity("Mensch");
        store.CreateEntity("Mensch");

        foreach (var attribute in new[] { "Name", "Wohnort", "Körpergröße" })
            Assert.Equal(AttributeObservations.None, store.GetObservations("Mensch", attribute));
    }

    [Fact] // test 4
    public void FailedOperations_AreNotCounted()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");
        var ghost = new EntityRef("Mensch", 999);

        Assert.ThrowsAny<ArgumentException>(() => store.SetValue(ghost, "Name", "Geist"));
        Assert.ThrowsAny<ArgumentException>(() => store.GetCurrentValue(ghost, "Name"));
        Assert.ThrowsAny<ArgumentException>(() => store.SetValue(anna, "Name", "Anna", ValueTime.Now));
        Assert.ThrowsAny<ArgumentException>(() => store.SetValue(anna, "Wohnort", "Leipzig"));
        Assert.ThrowsAny<ArgumentException>(() => store.GetHistory(anna, "Wohnort"));

        Assert.Equal(AttributeObservations.None, store.GetObservations("Mensch", "Name"));
        Assert.Equal(AttributeObservations.None, store.GetObservations("Mensch", "Wohnort"));
    }

    [Fact]
    public void UnknownAttribute_HasNoObservations()
    {
        using var store = Memory(H2);
        Assert.ThrowsAny<ArgumentException>(() => store.GetObservations("Mensch", "Schuhgröße"));
    }

    // ---------- Technical types (E-13, E-31) ----------

    [Fact] // test 5
    public void ValueTypes_AreCountedPerAttribute_NullIsNot()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Name", 42);              // Number (int)
        store.SetValue(anna, "Name", 42L);             // Number (long)
        store.SetValue(anna, "Name", 1.81);            // Number (double)
        store.SetValue(anna, "Name", 1.81m);           // Number (decimal)
        store.SetValue(anna, "Name", "Anna");          // Text
        store.SetValue(anna, "Name", Age10);           // Date (DateTimeOffset)
        store.SetValue(anna, "Name", Age10.UtcDateTime); // Date (DateTime)
        store.SetValue(anna, "Name", true);            // Boolean
        store.SetValue(anna, "Name", null);            // no type

        var observed = store.GetObservations("Mensch", "Name");
        Assert.Equal(9, observed.Writes);
        Assert.Equal(4, observed.Numbers);
        Assert.Equal(1, observed.Texts);
        Assert.Equal(2, observed.Dates);
        Assert.Equal(1, observed.Booleans);
    }

    [Fact] // test 5: timed values are typed the same way
    public void ValueTypes_AreCountedForTimedValues()
    {
        using var store = Memory(H1);
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.SetValue(anna, "Körpergröße", "1,68 m", ValueTime.At(Age14));

        var observed = store.GetObservations("Mensch", "Körpergröße");
        Assert.Equal(1, observed.Numbers);
        Assert.Equal(1, observed.Texts);
    }

    [Fact] // test 6
    public void TextThatLooksLikeANumber_IsText()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Name", "180");

        var observed = store.GetObservations("Mensch", "Name");
        Assert.Equal(1, observed.Texts);
        Assert.Equal(0, observed.Numbers);
    }

    [Theory] // test 7
    [MemberData(nameof(UnsupportedValues))]
    public void UnsupportedValueTypes_AreRejected_AndNotCounted(object value)
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");

        Assert.Throws<ArgumentException>(() => store.SetValue(anna, "Name", value));
        Assert.Throws<ArgumentException>(() => store.SetValue(anna, "Körpergröße", value, ValueTime.At(Age10)));

        Assert.Equal(AttributeObservations.None, store.GetObservations("Mensch", "Name"));
        Assert.Equal(AttributeObservations.None, store.GetObservations("Mensch", "Körpergröße"));
        Assert.Null(store.GetCurrentValue(anna, "Name"));
    }

    public static TheoryData<object> UnsupportedValues() =>
        [new byte[] { 1, 2 }, Guid.Empty, TimeSpan.FromHours(1), 'x'];

    [Fact] // test 8: why the type has to be recognized when a value comes in
    public void Sqlite_ReturnsBooleansAsNumbers_AndDatesAsText()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");

        store.SetValue(anna, "Name", true);
        Assert.IsType<long>(store.GetCurrentValue(anna, "Name")!.Value);

        store.SetValue(anna, "Name", Age10);
        Assert.IsType<string>(store.GetCurrentValue(anna, "Name")!.Value);
    }

    // ---------- Writing counters to the file (E-32) ----------

    [Fact] // test 9
    public void Counters_AreWrittenWhenTheStoreIsClosed_NotPerOperation()
    {
        var path = TempFile();
        using (var store = SqliteStore.Create(path, Model(H2), new FixedTimeProvider(Now)))
        {
            var anna = store.CreateEntity("Mensch");
            store.SetValue(anna, "Name", "Anna");
            store.GetCurrentValue(anna, "Name");

            Assert.Equal(0L, Scalar(path, "SELECT count(*) FROM _tursiops_observations"));
        }

        Assert.Equal(1L, Scalar(path,
            "SELECT count FROM _tursiops_observations WHERE entity = 'Mensch' AND attribute = 'Name' AND counter = 'write'"));
        Assert.Equal(1L, Scalar(path,
            "SELECT count FROM _tursiops_observations WHERE entity = 'Mensch' AND attribute = 'Name' AND counter = 'read_current'"));
    }

    [Fact] // test 9: before a sleep, the counters can be written explicitly
    public void FlushObservations_WritesPendingCounters()
    {
        var path = TempFile();
        using var store = SqliteStore.Create(path, Model(H2), new FixedTimeProvider(Now));
        var anna = store.CreateEntity("Mensch");
        store.SetValue(anna, "Name", "Anna");

        store.FlushObservations();
        store.FlushObservations(); // nothing pending, nothing added

        Assert.Equal(1L, Scalar(path, "SELECT count FROM _tursiops_observations WHERE counter = 'write'"));
        Assert.Equal(1, store.GetObservations("Mensch", "Name").Writes);
    }

    [Fact] // test 10
    public void Reopening_ContinuesTheSamePhase()
    {
        var path = TempFile();
        EntityRef anna;
        using (var store = SqliteStore.Create(path, Model(H2), new FixedTimeProvider(Now)))
        {
            anna = store.CreateEntity("Mensch");
            store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
            Assert.Equal(1, store.Phase);
        }

        using (var store = SqliteStore.Open(path, new FixedTimeProvider(Now)))
        {
            Assert.Equal(1, store.Phase);
            store.SetValue(anna, "Körpergröße", 168L, ValueTime.At(Age14));
            store.SetValue(anna, "Körpergröße", 142L, ValueTime.At(Age10)); // late, compared with the stored value

            var observed = store.GetObservations("Mensch", "Körpergröße");
            Assert.Equal(3, observed.Writes);
            Assert.Equal(1, observed.LateWrites);
            Assert.Equal(3, observed.Numbers);
        }
    }

    [Fact] // test 11
    public void NewPhase_StartsAtZero_AndKeepsTheOldOne()
    {
        var path = TempFile();
        using (var store = SqliteStore.Create(path, Model(H2), new FixedTimeProvider(Now)))
        {
            var anna = store.CreateEntity("Mensch");
            store.SetValue(anna, "Name", "Anna");

            Assert.Equal(2, store.StartNewPhase());
            Assert.Equal(2, store.Phase);
            Assert.Equal(AttributeObservations.None, store.GetObservations("Mensch", "Name"));
            Assert.Equal(1, store.GetObservations("Mensch", "Name", phase: 1).Writes);

            store.GetCurrentValue(anna, "Name");
        }

        using (var store = SqliteStore.Open(path))
        {
            Assert.Equal(2, store.Phase);
            Assert.Equal(1, store.GetObservations("Mensch", "Name", phase: 1).Writes);
            Assert.Equal(0, store.GetObservations("Mensch", "Name", phase: 1).CurrentReads);
            Assert.Equal(1, store.GetObservations("Mensch", "Name").CurrentReads);
        }
    }

    [Fact] // test 11: when a phase began is kept for the log
    public void Phases_RecordWhenTheyBegan()
    {
        var path = TempFile();
        var later = Now.AddDays(1);
        using (var store = SqliteStore.Create(path, Model(H2), new FixedTimeProvider(Now)))
            Assert.Equal(Now, store.PhaseStartedAt(1));

        using (var store = SqliteStore.Open(path, new FixedTimeProvider(later)))
        {
            store.StartNewPhase();
            Assert.Equal(Now, store.PhaseStartedAt(1));
            Assert.Equal(later, store.PhaseStartedAt(2));
        }
    }

    [Fact] // test 12
    public void ChangingTheLayout_DoesNotChangeCounters()
    {
        using var store = Memory(H2);
        var anna = store.CreateEntity("Mensch");
        store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
        store.GetCurrentValue(anna, "Körpergröße");
        var before = store.GetObservations("Mensch", "Körpergröße");

        store.ChangeLayout("Mensch", "Körpergröße", H1);
        Assert.Equal(before, store.GetObservations("Mensch", "Körpergröße"));

        store.ChangeLayout("Mensch", "Körpergröße", H2);
        Assert.Equal(before, store.GetObservations("Mensch", "Körpergröße"));
    }

    [Theory] // test 13: reading current values of all entities counts once per call, apart from single reads (E-31, E-33)
    [InlineData(H1)]
    [InlineData(H2)]
    public void ReadingCurrentValuesOfAllEntities_IsCountedOncePerCall(AttributeLayout layout)
    {
        var path = TempFile();
        using (var store = SqliteStore.Create(path, Model(layout), new FixedTimeProvider(Now)))
        {
            var anna = store.CreateEntity("Mensch");
            var ben = store.CreateEntity("Mensch");
            store.SetValue(anna, "Körpergröße", 155L, ValueTime.At(Age12));
            store.SetValue(ben, "Körpergröße", 150L, ValueTime.At(Age12));

            store.GetCurrentValues("Mensch", "Körpergröße");
            store.GetCurrentValues("Mensch", "Körpergröße");

            var observed = store.GetObservations("Mensch", "Körpergröße");
            Assert.Equal(2, observed.AllCurrentReads);
            Assert.Equal(0, observed.CurrentReads);
        }

        using (var store = SqliteStore.Open(path))
            Assert.Equal(2, store.GetObservations("Mensch", "Körpergröße").AllCurrentReads);
        Assert.Equal(2L, Scalar(path,
            "SELECT count FROM _tursiops_observations WHERE attribute = 'Körpergröße' AND counter = 'read_current_all'"));
    }

    // ---------- Helpers ----------

    private static StructureModel Model(AttributeLayout history) =>
        StructureModel.FromOntology(TurParser.Parse(Source).Ontology).WithLayout("Mensch", "Körpergröße", history);

    private static SqliteStore Memory(AttributeLayout history) =>
        SqliteStore.Create(":memory:", Model(history), new FixedTimeProvider(Now));

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tursiops-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
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
