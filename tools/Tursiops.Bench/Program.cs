// Measures the cost of the @history layouts H1 and H2 on SQLite (E-30).
// Results and their interpretation: docs/concepts/time.md, section "Measurements on SQLite".
//
// Usage: dotnet run -c Release --project tools/Tursiops.Bench [entities] [values per entity]
// Defaults: 1000 entities with 10 values each. Database files go to a temporary folder.

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Tursiops.Core.Format;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

const string Ontology = "Mensch {\n  Körpergröße @history\n}\n";
const string Entity = "Mensch";
const string Attribute = "Körpergröße";

var model = StructureModel.FromOntology(TurParser.Parse(Ontology).Ontology);
var start = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
var dir = Path.Combine(Path.GetTempPath(), "tursiops-bench");
Directory.CreateDirectory(dir);

int entities = int.Parse(args.ElementAtOrDefault(0) ?? "1000");
int valuesPerEntity = int.Parse(args.ElementAtOrDefault(1) ?? "10");
const int Reads = 5000;

Console.WriteLine($"Part 1: the operations of the translation layer, {entities} entities x {valuesPerEntity} values, one transaction per write");

// Round 0 warms up the JIT and is not reported.
for (int round = 0; round < 2; round++)
{
    foreach (var layout in new[] { AttributeLayout.HistoryTable, AttributeLayout.CurrentColumnAndHistoryTable })
    {
        var name = Short(layout);
        var path = FreshFile($"ops-{name}.db");
        using var store = SqliteStore.Create(path, model);
        if (layout != AttributeLayout.HistoryTable) store.ChangeLayout(Entity, Attribute, layout);
        var refs = Enumerable.Range(0, entities).Select(_ => store.CreateEntity(Entity)).ToArray();
        var report = round == 1;

        var sw = Stopwatch.StartNew();
        for (int k = 0; k < valuesPerEntity; k++)
            foreach (var e in refs)
                store.SetValue(e, Attribute, 150 + k, ValueTime.At(start.AddDays(10 * k + 10)));
        if (report) Report(name, "write newer value", entities * valuesPerEntity, sw.Elapsed);

        sw.Restart();
        foreach (var e in refs)
            store.SetValue(e, Attribute, 100, ValueTime.At(start.AddDays(5)));
        if (report) Report(name, "write late older value", entities, sw.Elapsed);

        var random = new Random(1);
        sw.Restart();
        for (int i = 0; i < Reads; i++) store.GetCurrentValue(refs[random.Next(entities)], Attribute);
        if (report) Report(name, "read current (one entity)", Reads, sw.Elapsed);

        sw.Restart();
        for (int i = 0; i < Reads; i++) store.GetHistory(refs[random.Next(entities)], Attribute);
        if (report) Report(name, "read history (one entity)", Reads, sw.Elapsed);
    }
}

// Part 2: the current value of all entities. There is no such operation yet (E-26),
// so this part reads with SQL directly. Data is bulk-loaded in one transaction.
Console.WriteLine();
Console.WriteLine($"Part 2: current value of all entities, {valuesPerEntity} values each, best of 5");

foreach (var n in new[] { 10_000, 100_000 })
{
    var path = FreshFile($"all-{n}.db");
    using (SqliteStore.Create(path, model)) { }
    BulkLoad(path, n, valuesPerEntity);

    var h2 = BestOfFive(path,
        $"SELECT m._id, (SELECT h.\"{Attribute}\" FROM \"{Entity}.{Attribute}\" h WHERE h._entity = m._id " +
        $"ORDER BY h._valid_at DESC, h.rowid DESC LIMIT 1) FROM \"{Entity}\" m");

    var sw = Stopwatch.StartNew();
    using (var store = SqliteStore.Open(path))
        store.ChangeLayout(Entity, Attribute, AttributeLayout.CurrentColumnAndHistoryTable);
    var change = sw.Elapsed.TotalMilliseconds;

    var h1 = BestOfFive(path, $"SELECT _id, \"{Attribute}\" FROM \"{Entity}\"");

    Console.WriteLine($"{n,7} entities: H2 {h2,8:F1} ms  H1 {h1,8:F1} ms  H2/H1 {h2 / h1,4:F1}  (change H2 -> H1: {change:F0} ms)");
}

static string Short(AttributeLayout layout) => layout == AttributeLayout.HistoryTable ? "H2" : "H1";

static void Report(string layout, string operation, int count, TimeSpan elapsed) =>
    Console.WriteLine($"{layout,-3} {operation,-27} {count,7} ops {elapsed.TotalMilliseconds,10:F1} ms {elapsed.TotalMilliseconds * 1000 / count,9:F1} µs/op");

string FreshFile(string name)
{
    var path = Path.Combine(dir, name);
    foreach (var file in Directory.GetFiles(dir, name + "*")) File.Delete(file);
    return path;
}

void BulkLoad(string path, int n, int perEntity)
{
    using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
    connection.Open();
    using var transaction = connection.BeginTransaction();
    var insert = connection.CreateCommand();
    insert.Transaction = transaction;

    insert.CommandText = $"INSERT INTO \"{Entity}\" DEFAULT VALUES";
    for (int i = 0; i < n; i++) insert.ExecuteNonQuery();

    insert.CommandText =
        $"INSERT INTO \"{Entity}.{Attribute}\" (_entity, \"{Attribute}\", _valid_at, _valid_offset) VALUES ($e, $v, $t, 0)";
    var entity = insert.Parameters.Add("$e", SqliteType.Integer);
    var value = insert.Parameters.Add("$v", SqliteType.Integer);
    var time = insert.Parameters.Add("$t", SqliteType.Text);
    for (int k = 0; k < perEntity; k++)
        for (int i = 1; i <= n; i++)
        {
            entity.Value = i;
            value.Value = 150 + k;
            time.Value = start.AddDays(10 * k + 10).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
            insert.ExecuteNonQuery();
        }
    transaction.Commit();
}

static double BestOfFive(string path, string sql)
{
    using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
    connection.Open();
    var command = connection.CreateCommand();
    command.CommandText = sql;
    var best = double.MaxValue;
    for (int rep = 0; rep < 5; rep++)
    {
        var sw = Stopwatch.StartNew();
        long sum = 0;
        using (var reader = command.ExecuteReader())
            while (reader.Read()) sum += reader.GetInt64(1);
        best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
    }
    return best;
}
