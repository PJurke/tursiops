using Tursiops.Core.Format;
using Microsoft.Data.Sqlite;
using static Tursiops.Core.Storage.Sqlite.SqliteNames;

namespace Tursiops.Core.Storage.Sqlite;

/// <summary>
/// Translation layer to SQLite (E-06, E-26). Only this class knows tables and SQL;
/// everyone else works with entities, attributes and the structure model.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;
    private SqliteTransaction? _transaction;

    private SqliteStore(SqliteConnection connection, StructureModel structure, TimeProvider time)
    {
        _connection = connection;
        _time = time;
        Structure = structure;
    }

    /// <summary>The current storage decisions, as recorded in the database file.</summary>
    public StructureModel Structure { get; private set; }

    // ---------- Creating and opening ----------

    /// <summary>Creates a new Tursiops database with the given structure.</summary>
    /// <param name="path">File path, or <c>:memory:</c> for a database that lives only as long as the store.</param>
    /// <exception cref="InvalidOperationException">The file already contains a Tursiops database.</exception>
    public static SqliteStore Create(string path, StructureModel structure, TimeProvider? time = null)
    {
        var connection = Connect(path, SqliteOpenMode.ReadWriteCreate);
        try
        {
            if (TableExists(connection, StructureTable))
                throw new InvalidOperationException($"'{path}' already contains a Tursiops database.");

            using var transaction = connection.BeginTransaction();
            CreateCatalog(connection, transaction, structure);
            foreach (var entity in structure.Entities)
                CreateEntityTables(connection, transaction, entity);
            transaction.Commit();

            return new SqliteStore(connection, structure, time ?? TimeProvider.System);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Opens an existing Tursiops database and restores its structure model (E-26).</summary>
    /// <exception cref="InvalidOperationException">The file is not a Tursiops database.</exception>
    public static SqliteStore Open(string path, TimeProvider? time = null)
    {
        var connection = Connect(path, SqliteOpenMode.ReadWrite);
        try
        {
            if (!TableExists(connection, StructureTable))
                throw new InvalidOperationException($"'{path}' is not a Tursiops database.");

            return new SqliteStore(connection, LoadCatalog(connection), time ?? TimeProvider.System);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose() => _connection.Dispose();

    // ---------- Operations (E-26) ----------

    /// <summary>Creates a new entity, e.g. a person, and returns its reference.</summary>
    public EntityRef CreateEntity(string entity)
    {
        Structure.Entity(entity);
        var id = (long)Scalar($"INSERT INTO {Quote(EntityTable(entity))} DEFAULT VALUES RETURNING {Quote(IdColumn)}")!;
        return new EntityRef(entity, id);
    }

    /// <summary>Sets the value of an attribute without time. Only the last value written counts (E-25).</summary>
    public void SetValue(EntityRef entity, string attribute, object? value)
    {
        var target = Resolve(entity, attribute);
        if (target.Time != AttributeTime.Timeless)
            throw new ArgumentException(
                $"'{entity.Entity}.{attribute}' is {StructureModel.Describe(target.Time)}, so its value needs a time. " +
                "Pass ValueTime.Now or ValueTime.At(...).", nameof(attribute));

        Execute(
            $"UPDATE {Quote(EntityTable(entity.Entity))} SET {Quote(attribute)} = $value WHERE {Quote(IdColumn)} = $id",
            ("$value", value), ("$id", entity.Id));
    }

    /// <summary>
    /// Sets the value of an attribute with <c>@time</c> or <c>@history</c>, valid at the given time (E-27).
    /// With <c>@time</c> only the value with the latest valid time is kept; with <c>@history</c> every value (E-25).
    /// </summary>
    public void SetValue(EntityRef entity, string attribute, object? value, ValueTime time)
    {
        var target = Resolve(entity, attribute);
        if (target.Time == AttributeTime.Timeless)
            throw new ArgumentException(
                $"'{entity.Entity}.{attribute}' is {StructureModel.Describe(target.Time)}, so its value takes no time.", nameof(time));

        var validAt = time.Resolve(_time);
        var utc = ToSortableUtc(validAt);
        var offset = ToOffsetMinutes(validAt);

        switch (target.Layout)
        {
            case AttributeLayout.HistoryTable:
                InsertIntoHistory(entity, attribute, value, utc, offset);
                break;

            case AttributeLayout.ColumnWithTime:
                ReplaceCurrentIfNotOlder(entity, attribute, value, utc, offset);
                break;

            case AttributeLayout.CurrentColumnAndHistoryTable:
                // H1: a value that is not older than the current one becomes current and pushes the old one
                // into the history table; an older value that arrives late goes straight into the history table.
                InTransaction(() =>
                {
                    var validAtColumn = Quote(CurrentValidAtColumn(attribute));
                    Execute(
                        $"INSERT INTO {Quote(HistoryTable(entity.Entity, attribute))} " +
                        $"({Quote(EntityColumn)}, {Quote(attribute)}, {Quote(ValidAtColumn)}, {Quote(ValidOffsetColumn)}) " +
                        $"SELECT {Quote(IdColumn)}, {Quote(attribute)}, {validAtColumn}, {Quote(CurrentValidOffsetColumn(attribute))} " +
                        $"FROM {Quote(EntityTable(entity.Entity))} " +
                        $"WHERE {Quote(IdColumn)} = $id AND {validAtColumn} IS NOT NULL AND {validAtColumn} <= $validAt",
                        ("$id", entity.Id), ("$validAt", utc));
                    if (!ReplaceCurrentIfNotOlder(entity, attribute, value, utc, offset))
                        InsertIntoHistory(entity, attribute, value, utc, offset);
                });
                break;

            default:
                throw new InvalidOperationException($"Layout {target.Layout} cannot hold a timed value.");
        }
    }

    private void InsertIntoHistory(EntityRef entity, string attribute, object? value, string utc, long offset) =>
        Execute(
            $"INSERT INTO {Quote(HistoryTable(entity.Entity, attribute))} " +
            $"({Quote(EntityColumn)}, {Quote(attribute)}, {Quote(ValidAtColumn)}, {Quote(ValidOffsetColumn)}) " +
            "VALUES ($id, $value, $validAt, $offset)",
            ("$id", entity.Id), ("$value", value), ("$validAt", utc), ("$offset", offset));

    // An older measurement that arrives late does not replace a newer one (E-25). Returns whether the value was replaced.
    private bool ReplaceCurrentIfNotOlder(EntityRef entity, string attribute, object? value, string utc, long offset)
    {
        var validAtColumn = Quote(CurrentValidAtColumn(attribute));
        return Execute(
            $"UPDATE {Quote(EntityTable(entity.Entity))} " +
            $"SET {Quote(attribute)} = $value, {validAtColumn} = $validAt, {Quote(CurrentValidOffsetColumn(attribute))} = $offset " +
            $"WHERE {Quote(IdColumn)} = $id AND ({validAtColumn} IS NULL OR {validAtColumn} <= $validAt)",
            ("$id", entity.Id), ("$value", value), ("$validAt", utc), ("$offset", offset)) > 0;
    }

    /// <summary>
    /// Reads the current value of an attribute, or <c>null</c> if it was never set.
    /// For attributes with <c>@time</c> or <c>@history</c> this is the value with the latest valid time,
    /// not the last one written.
    /// </summary>
    public StoredValue? GetCurrentValue(EntityRef entity, string attribute)
    {
        var target = Resolve(entity, attribute);

        if (target.Layout == AttributeLayout.Column)
        {
            var value = Scalar(
                $"SELECT {Quote(attribute)} FROM {Quote(EntityTable(entity.Entity))} WHERE {Quote(IdColumn)} = $id",
                ("$id", entity.Id));
            return value is null or DBNull ? null : new StoredValue(value, null);
        }

        var latest = target.Layout == AttributeLayout.HistoryTable
            ? ReadHistory(entity, attribute, newestFirst: true, limit: 1)
            : ReadCurrentTimed(entity, attribute);
        return latest.Count == 0 ? null : new StoredValue(latest[0].Value, latest[0].ValidAt);
    }

    /// <summary>
    /// Reads all values of an attribute with <c>@history</c>, oldest first.
    /// Other attributes have no history as part of their information (E-25), so asking for it is an error.
    /// </summary>
    public IReadOnlyList<TimedValue> GetHistory(EntityRef entity, string attribute)
    {
        var target = Resolve(entity, attribute);
        if (target.Time != AttributeTime.History)
            throw new ArgumentException(
                $"'{entity.Entity}.{attribute}' is {StructureModel.Describe(target.Time)}, so it keeps no history. " +
                "Mark it with @history in the ontology if its history is needed.", nameof(attribute));

        return target.Layout switch
        {
            AttributeLayout.HistoryTable => ReadHistory(entity, attribute, newestFirst: false, limit: null),
            AttributeLayout.CurrentColumnAndHistoryTable => ReadSplitHistory(entity, attribute),
            _ => throw new InvalidOperationException($"Layout {target.Layout} cannot hold a history."),
        };
    }

    // ---------- Changing the layout ----------

    /// <summary>
    /// Changes how the values of one attribute are stored. The information stays the same (E-25):
    /// every value, its valid time and the current value read the same before and after.
    /// Tables and structure model change in one transaction (E-26).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Unknown entity or attribute, or a layout that cannot hold the attribute's information (E-25).
    /// </exception>
    public void ChangeLayout(string entity, string attribute, AttributeLayout layout)
    {
        var changed = Structure.WithLayout(entity, attribute, layout);
        var from = Structure.Entity(entity).Attribute(attribute).Layout;
        if (from == layout)
            return;

        InTransaction(() =>
        {
            switch (from, layout)
            {
                case (AttributeLayout.HistoryTable, AttributeLayout.CurrentColumnAndHistoryTable):
                    MoveLatestIntoEntityRow(entity, attribute);
                    break;
                case (AttributeLayout.CurrentColumnAndHistoryTable, AttributeLayout.HistoryTable):
                    MoveCurrentIntoHistoryTable(entity, attribute);
                    break;
                default:
                    throw new InvalidOperationException($"No change from {from} to {layout} is known.");
            }

            Execute(
                $"UPDATE {Quote(StructureTable)} SET layout = $layout WHERE entity = $entity AND attribute = $attribute",
                ("$layout", layout.ToString()), ("$entity", entity), ("$attribute", attribute));
        });

        Structure = changed;
    }

    // H2 -> H1: per entity, the row with the latest valid time moves from the history table into the entity row.
    // Among equal valid times the row written last is the current one, as in H2.
    private void MoveLatestIntoEntityRow(string entity, string attribute)
    {
        var table = Quote(EntityTable(entity));
        var history = Quote(HistoryTable(entity, attribute));
        var latestRowid =
            $"(SELECT h.rowid FROM {history} h WHERE h.{Quote(EntityColumn)} = e.{Quote(IdColumn)} " +
            $"ORDER BY h.{Quote(ValidAtColumn)} DESC, h.rowid DESC LIMIT 1)";

        foreach (var column in CurrentColumns(attribute))
            Execute($"ALTER TABLE {table} ADD COLUMN {column}");

        Execute(
            $"UPDATE {table} AS e SET " +
            $"({Quote(attribute)}, {Quote(CurrentValidAtColumn(attribute))}, {Quote(CurrentValidOffsetColumn(attribute))}) = " +
            $"(SELECT {Quote(attribute)}, {Quote(ValidAtColumn)}, {Quote(ValidOffsetColumn)} FROM {history} WHERE rowid = {latestRowid})");
        Execute($"DELETE FROM {history} WHERE rowid IN (SELECT {latestRowid} FROM {table} AS e)");
    }

    // H1 -> H2: the current values move from the entity rows into the history table.
    // They get the highest rowids, so among equal valid times they stay the ones written last.
    private void MoveCurrentIntoHistoryTable(string entity, string attribute)
    {
        var table = Quote(EntityTable(entity));
        var validAtColumn = Quote(CurrentValidAtColumn(attribute));
        Execute(
            $"INSERT INTO {Quote(HistoryTable(entity, attribute))} " +
            $"({Quote(EntityColumn)}, {Quote(attribute)}, {Quote(ValidAtColumn)}, {Quote(ValidOffsetColumn)}) " +
            $"SELECT {Quote(IdColumn)}, {Quote(attribute)}, {validAtColumn}, {Quote(CurrentValidOffsetColumn(attribute))} " +
            $"FROM {table} WHERE {validAtColumn} IS NOT NULL ORDER BY {Quote(IdColumn)}");

        foreach (var column in new[] { attribute, CurrentValidAtColumn(attribute), CurrentValidOffsetColumn(attribute) })
            Execute($"ALTER TABLE {table} DROP COLUMN {Quote(column)}");
    }

    // ---------- Reading timed values ----------

    private List<TimedValue> ReadHistory(EntityRef entity, string attribute, bool newestFirst, int? limit)
    {
        var direction = newestFirst ? "DESC" : "ASC";
        return ReadTimed(
            $"SELECT {Quote(attribute)}, {Quote(ValidAtColumn)}, {Quote(ValidOffsetColumn)} " +
            $"FROM {Quote(HistoryTable(entity.Entity, attribute))} WHERE {Quote(EntityColumn)} = $id " +
            $"ORDER BY {Quote(ValidAtColumn)} {direction}, rowid {direction}" +
            (limit is null ? "" : $" LIMIT {limit}"),
            entity.Id);
    }

    // H1: older values from the history table, the current value from the entity row.
    // The current value comes last among equal valid times, because it was written last.
    private List<TimedValue> ReadSplitHistory(EntityRef entity, string attribute) =>
        ReadTimed(
            $"SELECT {Quote(attribute)}, {Quote(ValidAtColumn)}, {Quote(ValidOffsetColumn)}, 0, rowid " +
            $"FROM {Quote(HistoryTable(entity.Entity, attribute))} WHERE {Quote(EntityColumn)} = $id " +
            "UNION ALL " +
            $"SELECT {Quote(attribute)}, {Quote(CurrentValidAtColumn(attribute))}, {Quote(CurrentValidOffsetColumn(attribute))}, 1, 0 " +
            $"FROM {Quote(EntityTable(entity.Entity))} " +
            $"WHERE {Quote(IdColumn)} = $id AND {Quote(CurrentValidAtColumn(attribute))} IS NOT NULL " +
            "ORDER BY 2, 4, 5",
            entity.Id);

    private List<TimedValue> ReadCurrentTimed(EntityRef entity, string attribute) =>
        ReadTimed(
            $"SELECT {Quote(attribute)}, {Quote(CurrentValidAtColumn(attribute))}, {Quote(CurrentValidOffsetColumn(attribute))} " +
            $"FROM {Quote(EntityTable(entity.Entity))} " +
            $"WHERE {Quote(IdColumn)} = $id AND {Quote(CurrentValidAtColumn(attribute))} IS NOT NULL",
            entity.Id);

    private List<TimedValue> ReadTimed(string sql, long id)
    {
        using var command = Command(sql, ("$id", id));
        using var reader = command.ExecuteReader();
        var values = new List<TimedValue>();
        while (reader.Read())
        {
            var value = reader.IsDBNull(0) ? null : reader.GetValue(0);
            values.Add(new TimedValue(value, FromStored(reader.GetString(1), reader.GetInt64(2))));
        }
        return values;
    }

    // ---------- Checks ----------

    private AttributeStructure Resolve(EntityRef entity, string attribute)
    {
        var target = Structure.Entity(entity.Entity).Attribute(attribute);

        var exists = Scalar(
            $"SELECT 1 FROM {Quote(EntityTable(entity.Entity))} WHERE {Quote(IdColumn)} = $id",
            ("$id", entity.Id));
        if (exists is null)
            throw new ArgumentException($"There is no '{entity.Entity}' with id {entity.Id}.", nameof(entity));

        return target;
    }

    // ---------- Schema ----------

    private static void CreateCatalog(SqliteConnection connection, SqliteTransaction transaction, StructureModel structure)
    {
        Execute(connection, transaction,
            $"CREATE TABLE {Quote(EntitiesTable)} (entity TEXT PRIMARY KEY, position INTEGER NOT NULL)");
        Execute(connection, transaction,
            $"CREATE TABLE {Quote(StructureTable)} (" +
            "entity TEXT NOT NULL, attribute TEXT NOT NULL, position INTEGER NOT NULL, " +
            "time TEXT NOT NULL, layout TEXT NOT NULL, PRIMARY KEY (entity, attribute))");

        for (var e = 0; e < structure.Entities.Count; e++)
        {
            var entity = structure.Entities[e];
            Execute(connection, transaction,
                $"INSERT INTO {Quote(EntitiesTable)} (entity, position) VALUES ($entity, $position)",
                ("$entity", entity.Name), ("$position", e));

            for (var a = 0; a < entity.Attributes.Count; a++)
            {
                var attribute = entity.Attributes[a];
                Execute(connection, transaction,
                    $"INSERT INTO {Quote(StructureTable)} (entity, attribute, position, time, layout) " +
                    "VALUES ($entity, $attribute, $position, $time, $layout)",
                    ("$entity", entity.Name), ("$attribute", attribute.Name), ("$position", a),
                    ("$time", attribute.Time.ToString()), ("$layout", attribute.Layout.ToString()));
            }
        }
    }

    private static StructureModel LoadCatalog(SqliteConnection connection)
    {
        var attributes = new Dictionary<string, List<AttributeStructure>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT entity, attribute, time, layout FROM {Quote(StructureTable)} ORDER BY entity, position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var layout = Enum.Parse<AttributeLayout>(reader.GetString(3));
                var list = attributes.TryGetValue(reader.GetString(0), out var existing)
                    ? existing
                    : attributes[reader.GetString(0)] = [];
                list.Add(new AttributeStructure(reader.GetString(1), Enum.Parse<AttributeTime>(reader.GetString(2)), layout));
            }
        }

        var entities = new List<EntityStructure>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT entity FROM {Quote(EntitiesTable)} ORDER BY position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                entities.Add(new EntityStructure(name, attributes.GetValueOrDefault(name) ?? []));
            }
        }

        return new StructureModel(entities);
    }

    private static void CreateEntityTables(SqliteConnection connection, SqliteTransaction transaction, EntityStructure entity)
    {
        // Untyped value columns: SQLite keeps the type of each value (E-13, E-23).
        var columns = new List<string> { $"{Quote(IdColumn)} INTEGER PRIMARY KEY" };
        foreach (var attribute in entity.Attributes)
        {
            if (attribute.Layout == AttributeLayout.Column)
                columns.Add(Quote(attribute.Name));
            else if (attribute.Layout is AttributeLayout.ColumnWithTime or AttributeLayout.CurrentColumnAndHistoryTable)
                columns.AddRange(CurrentColumns(attribute.Name));
        }
        Execute(connection, transaction,
            $"CREATE TABLE {Quote(EntityTable(entity.Name))} ({string.Join(", ", columns)})");

        foreach (var attribute in entity.Attributes.Where(a => a.Layout is AttributeLayout.HistoryTable or AttributeLayout.CurrentColumnAndHistoryTable))
        {
            var table = Quote(HistoryTable(entity.Name, attribute.Name));
            Execute(connection, transaction,
                $"CREATE TABLE {table} (" +
                $"{Quote(EntityColumn)} INTEGER NOT NULL REFERENCES {Quote(EntityTable(entity.Name))} ({Quote(IdColumn)}), " +
                $"{Quote(attribute.Name)}, " +
                $"{Quote(ValidAtColumn)} TEXT NOT NULL, " +
                $"{Quote(ValidOffsetColumn)} INTEGER NOT NULL)");
            Execute(connection, transaction,
                $"CREATE INDEX {Quote(HistoryIndex(entity.Name, attribute.Name))} " +
                $"ON {table} ({Quote(EntityColumn)}, {Quote(ValidAtColumn)})");
        }
    }

    // Columns in the entity table for a current value with its valid time.
    private static string[] CurrentColumns(string attribute) =>
    [
        Quote(attribute),
        $"{Quote(CurrentValidAtColumn(attribute))} TEXT",
        $"{Quote(CurrentValidOffsetColumn(attribute))} INTEGER",
    ];

    // ---------- SQL helpers ----------

    // Runs several statements as one unit; joins a transaction that is already open.
    private void InTransaction(Action action)
    {
        if (_transaction is not null)
        {
            action();
            return;
        }

        using var transaction = _connection.BeginTransaction();
        _transaction = transaction;
        try
        {
            action();
            transaction.Commit();
        }
        finally
        {
            _transaction = null;
        }
    }

    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! == 1;
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
