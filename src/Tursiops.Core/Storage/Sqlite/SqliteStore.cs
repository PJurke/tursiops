using Microsoft.Data.Sqlite;
using static Tursiops.Core.Storage.Sqlite.SqliteNames;

namespace Tursiops.Core.Storage.Sqlite;

/// <summary>
/// Translation layer to SQLite (E-06, E-20). Only this class knows tables and SQL;
/// everyone else works with entities, attributes and the structure model.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;

    private SqliteStore(SqliteConnection connection, StructureModel structure, TimeProvider time)
    {
        _connection = connection;
        _time = time;
        Structure = structure;
    }

    /// <summary>The current storage decisions, as recorded in the database file.</summary>
    public StructureModel Structure { get; }

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

    /// <summary>Opens an existing Tursiops database and restores its structure model (E-20).</summary>
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

    // ---------- Operations (E-20) ----------

    /// <summary>Creates a new entity, e.g. a person, and returns its reference.</summary>
    public EntityRef CreateEntity(string entity)
    {
        Structure.Entity(entity);
        var id = (long)Scalar($"INSERT INTO {Quote(EntityTable(entity))} DEFAULT VALUES RETURNING {Quote(IdColumn)}")!;
        return new EntityRef(entity, id);
    }

    /// <summary>Sets the value of an attribute without <c>@time</c>.</summary>
    public void SetValue(EntityRef entity, string attribute, object? value)
    {
        var target = Resolve(entity, attribute);
        if (target.IsTimed)
            throw new ArgumentException(
                $"'{entity.Entity}.{attribute}' is marked with @time, so its value needs a time. " +
                "Pass ValueTime.Now or ValueTime.At(...).", nameof(attribute));

        Execute(
            $"UPDATE {Quote(EntityTable(entity.Entity))} SET {Quote(attribute)} = $value WHERE {Quote(IdColumn)} = $id",
            ("$value", value), ("$id", entity.Id));
    }

    /// <summary>Sets the value of an attribute with <c>@time</c>, valid at the given time (E-22).</summary>
    public void SetValue(EntityRef entity, string attribute, object? value, ValueTime time)
    {
        var target = Resolve(entity, attribute);
        if (!target.IsTimed)
            throw new ArgumentException(
                $"'{entity.Entity}.{attribute}' is not marked with @time, so its value takes no time.", nameof(time));

        var validAt = time.Resolve(_time);
        var utc = ToSortableUtc(validAt);
        var offset = ToOffsetMinutes(validAt);

        if (target.Layout == AttributeLayout.History)
        {
            Execute(
                $"INSERT INTO {Quote(HistoryTable(entity.Entity, attribute))} " +
                $"({Quote(EntityColumn)}, {Quote(attribute)}, {Quote(ValidAtColumn)}, {Quote(ValidOffsetColumn)}) " +
                "VALUES ($id, $value, $validAt, $offset)",
                ("$id", entity.Id), ("$value", value), ("$validAt", utc), ("$offset", offset));
            return;
        }

        // Current value only: an older measurement that arrives late does not replace a newer one.
        var validAtColumn = Quote(CurrentValidAtColumn(attribute));
        Execute(
            $"UPDATE {Quote(EntityTable(entity.Entity))} " +
            $"SET {Quote(attribute)} = $value, {validAtColumn} = $validAt, {Quote(CurrentValidOffsetColumn(attribute))} = $offset " +
            $"WHERE {Quote(IdColumn)} = $id AND ({validAtColumn} IS NULL OR {validAtColumn} <= $validAt)",
            ("$id", entity.Id), ("$value", value), ("$validAt", utc), ("$offset", offset));
    }

    /// <summary>
    /// Reads the current value of an attribute, or <c>null</c> if it was never set.
    /// For attributes with <c>@time</c> this is the value with the latest valid time, not the last one written.
    /// </summary>
    public StoredValue? GetCurrentValue(EntityRef entity, string attribute)
    {
        var target = Resolve(entity, attribute);

        if (!target.IsTimed)
        {
            var value = Scalar(
                $"SELECT {Quote(attribute)} FROM {Quote(EntityTable(entity.Entity))} WHERE {Quote(IdColumn)} = $id",
                ("$id", entity.Id));
            return value is null or DBNull ? null : new StoredValue(value, null);
        }

        var latest = target.Layout == AttributeLayout.History
            ? ReadHistory(entity, attribute, newestFirst: true, limit: 1)
            : ReadCurrentTimed(entity, attribute);
        return latest.Count == 0 ? null : new StoredValue(latest[0].Value, latest[0].ValidAt);
    }

    /// <summary>
    /// Reads all stored values of an attribute with <c>@time</c>, oldest first.
    /// With the layout <see cref="AttributeLayout.CurrentValue"/> this is at most the one current value.
    /// </summary>
    public IReadOnlyList<TimedValue> GetHistory(EntityRef entity, string attribute)
    {
        var target = Resolve(entity, attribute);
        if (!target.IsTimed)
            throw new ArgumentException(
                $"'{entity.Entity}.{attribute}' is not marked with @time, so it has no history.", nameof(attribute));

        return target.Layout == AttributeLayout.History
            ? ReadHistory(entity, attribute, newestFirst: false, limit: null)
            : ReadCurrentTimed(entity, attribute);
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
            "timed INTEGER NOT NULL, layout TEXT NOT NULL, PRIMARY KEY (entity, attribute))");

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
                    $"INSERT INTO {Quote(StructureTable)} (entity, attribute, position, timed, layout) " +
                    "VALUES ($entity, $attribute, $position, $timed, $layout)",
                    ("$entity", entity.Name), ("$attribute", attribute.Name), ("$position", a),
                    ("$timed", attribute.IsTimed), ("$layout", attribute.Layout.ToString()));
            }
        }
    }

    private static StructureModel LoadCatalog(SqliteConnection connection)
    {
        var attributes = new Dictionary<string, List<AttributeStructure>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT entity, attribute, timed, layout FROM {Quote(StructureTable)} ORDER BY entity, position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var layout = Enum.Parse<AttributeLayout>(reader.GetString(3));
                var list = attributes.TryGetValue(reader.GetString(0), out var existing)
                    ? existing
                    : attributes[reader.GetString(0)] = [];
                list.Add(new AttributeStructure(reader.GetString(1), reader.GetBoolean(2), layout));
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
        foreach (var attribute in entity.Attributes.Where(a => a.Layout == AttributeLayout.CurrentValue))
        {
            columns.Add(Quote(attribute.Name));
            if (attribute.IsTimed)
            {
                columns.Add($"{Quote(CurrentValidAtColumn(attribute.Name))} TEXT");
                columns.Add($"{Quote(CurrentValidOffsetColumn(attribute.Name))} INTEGER");
            }
        }
        Execute(connection, transaction,
            $"CREATE TABLE {Quote(EntityTable(entity.Name))} ({string.Join(", ", columns)})");

        foreach (var attribute in entity.Attributes.Where(a => a.Layout == AttributeLayout.History))
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

    // ---------- SQL helpers ----------

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

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
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
