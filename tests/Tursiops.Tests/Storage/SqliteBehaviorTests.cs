using Microsoft.Data.Sqlite;

namespace Tursiops.Tests.Storage;

// Verifies the SQLite behavior that decisions E-21 and E-26 rely on.
// If one of these fails, the decision needs to be revisited, not the test.
public class SqliteBehaviorTests
{
    [Fact]
    public void QuotedNames_MayContainUmlautsAndDots() // E-21
    {
        using var connection = Open();
        Execute(connection, """CREATE TABLE "Mensch.Körpergröße" ("Größe" , "Größe.valid_at" TEXT)""");
        Execute(connection, """INSERT INTO "Mensch.Körpergröße" ("Größe", "Größe.valid_at") VALUES (142, '2016-03-03')""");

        using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Größe" FROM "Mensch.Körpergröße" """;
        Assert.Equal(142L, command.ExecuteScalar());
    }

    [Fact]
    public void SchemaChanges_AreRolledBackWithTheTransaction() // E-26
    {
        using var connection = Open();
        Execute(connection, """CREATE TABLE "Mensch" ("_id" INTEGER PRIMARY KEY, "Körpergröße")""");

        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection, """CREATE TABLE "Mensch.Körpergröße" ("_entity" INTEGER, "value")""", transaction);
            Execute(connection, """ALTER TABLE "Mensch" DROP COLUMN "Körpergröße" """, transaction);
            transaction.Rollback();
        }

        Assert.False(TableExists(connection, "Mensch.Körpergröße"));
        Assert.Contains("Körpergröße", Columns(connection, "Mensch"));
    }

    [Fact]
    public void UntypedColumns_KeepTheTypeOfEachValue() // E-13, E-23
    {
        using var connection = Open();
        Execute(connection, """CREATE TABLE "t" ("v")""");
        Execute(connection, """INSERT INTO "t" ("v") VALUES (142), (1.42), ('groß')""");

        using var command = connection.CreateCommand();
        command.CommandText = """SELECT "v" FROM "t" ORDER BY rowid""";
        using var reader = command.ExecuteReader();
        var values = new List<object>();
        while (reader.Read())
            values.Add(reader.GetValue(0));

        Assert.Equal([142L, 1.42, "groß"], values);
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! == 1;
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }
}
