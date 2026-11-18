using Tursiops.Core.Format;
using Tursiops.Core.Sleep;
using Microsoft.Data.Sqlite;
using static Tursiops.Core.Storage.Sqlite.SqliteNames;

namespace Tursiops.Core.Storage.Sqlite;

/// <summary>
/// Translation layer to SQLite (E-06, E-33). Only this class knows tables and SQL;
/// everyone else works with entities, attributes and the structure model.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;
    private SqliteTransaction? _transaction;
    private readonly PendingObservations _pending = new();
    private int _unobserved;

    private SqliteStore(SqliteConnection connection, StructureModel structure, TimeProvider time, long phase)
    {
        _connection = connection;
        _time = time;
        Structure = structure;
        Phase = phase;
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

            time ??= TimeProvider.System;
            using var transaction = connection.BeginTransaction();
            CreateCatalog(connection, transaction, structure);
            foreach (var entity in structure.Entities)
                CreateEntityTables(connection, transaction, entity);
            var phase = PrepareObservations(connection, transaction, time);
            PrepareSleepLog(connection, transaction);
            transaction.Commit();

            return new SqliteStore(connection, structure, time, phase);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Opens an existing Tursiops database and restores its structure model (E-33).</summary>
    /// <exception cref="InvalidOperationException">The file is not a Tursiops database.</exception>
    public static SqliteStore Open(string path, TimeProvider? time = null)
    {
        var connection = Connect(path, SqliteOpenMode.ReadWrite);
        try
        {
            if (!TableExists(connection, StructureTable))
                throw new InvalidOperationException($"'{path}' is not a Tursiops database.");
            if (!ColumnExists(connection, StructureTable, "type"))
                throw new InvalidOperationException(
                    $"'{path}' was created by an older version of Tursiops, in which attributes had no declared types. " +
                    "Tursiops does not guess the types from the stored values. " +
                    "Add a type to each attribute in the ontology (for example 'Körpergröße: number @history') and create the database again.");

            time ??= TimeProvider.System;
            using var transaction = connection.BeginTransaction();
            var phase = PrepareObservations(connection, transaction, time);
            PrepareSleepLog(connection, transaction);
            transaction.Commit();

            return new SqliteStore(connection, LoadCatalog(connection), time, phase);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Writes pending observations to the file (E-32) and closes the database.</summary>
    public void Dispose()
    {
        try
        {
            FlushObservations();
        }
        finally
        {
            _connection.Dispose();
        }
    }

    // ---------- Operations (E-33) ----------

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
        ValueTypes.Check(entity.Entity, attribute, target.Type, value);

        Execute(
            $"UPDATE {Quote(EntityTable(entity.Entity))} SET {Quote(attribute)} = $value WHERE {Quote(IdColumn)} = $id",
            ("$value", value), ("$id", entity.Id));

        ObserveWrite(entity.Entity, attribute, late: false);
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
        ValueTypes.Check(entity.Entity, attribute, target.Type, value);

        var validAt = time.Resolve(_time);
        var utc = ToSortableUtc(validAt);
        var offset = ToOffsetMinutes(validAt);
        bool late;

        switch (target.Layout)
        {
            case AttributeLayout.HistoryTable:
                // Late means older than the current value, i.e. some stored value is newer (E-31).
                late = Scalar(
                    $"SELECT 1 FROM {Quote(HistoryTable(entity.Entity, attribute))} " +
                    $"WHERE {Quote(EntityColumn)} = $id AND {Quote(ValidAtColumn)} > $validAt LIMIT 1",
                    ("$id", entity.Id), ("$validAt", utc)) is not null;
                InsertIntoHistory(entity, attribute, value, utc, offset);
                break;

            case AttributeLayout.ColumnWithTime:
                late = !ReplaceCurrentIfNotOlder(entity, attribute, value, utc, offset);
                break;

            case AttributeLayout.CurrentColumnAndHistoryTable:
                // H1: a value that is not older than the current one becomes current and pushes the old one
                // into the history table; an older value that arrives late goes straight into the history table.
                var replaced = false;
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
                    replaced = ReplaceCurrentIfNotOlder(entity, attribute, value, utc, offset);
                    if (!replaced)
                        InsertIntoHistory(entity, attribute, value, utc, offset);
                });
                late = !replaced;
                break;

            default:
                throw new InvalidOperationException($"Layout {target.Layout} cannot hold a timed value.");
        }

        ObserveWrite(entity.Entity, attribute, late);
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
            Observe(entity.Entity, attribute, Counter.ReadCurrent);
            return value is null or DBNull ? null : new StoredValue(SqliteValues.Read(value, target.Type), null);
        }

        var latest = target.Layout == AttributeLayout.HistoryTable
            ? ReadHistory(entity, attribute, newestFirst: true, limit: 1)
            : ReadCurrentTimed(entity, attribute);
        Observe(entity.Entity, attribute, Counter.ReadCurrent);
        return latest.Count == 0 ? null : new StoredValue(latest[0].Value, latest[0].ValidAt);
    }

    /// <summary>
    /// Reads the current value of an attribute for all entities that have one, in the order they were created (E-33).
    /// Each value is the one <see cref="GetCurrentValue"/> returns for that entity; entities without a value are left out.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown entity or attribute.</exception>
    public IReadOnlyList<EntityValue> GetCurrentValues(string entity, string attribute)
    {
        var target = Structure.Entity(entity).Attribute(attribute);
        var table = Quote(EntityTable(entity));
        var id = Quote(IdColumn);

        var sql = target.Layout switch
        {
            AttributeLayout.Column =>
                $"SELECT {id}, {Quote(attribute)} FROM {table} WHERE {Quote(attribute)} IS NOT NULL ORDER BY {id}",

            AttributeLayout.ColumnWithTime or AttributeLayout.CurrentColumnAndHistoryTable =>
                $"SELECT {id}, {Quote(attribute)}, {Quote(CurrentValidAtColumn(attribute))}, {Quote(CurrentValidOffsetColumn(attribute))} " +
                $"FROM {table} WHERE {Quote(CurrentValidAtColumn(attribute))} IS NOT NULL ORDER BY {id}",

            // H2: per entity the row with the latest valid time, the one written last among equal times.
            AttributeLayout.HistoryTable =>
                $"SELECT e.{id}, h.{Quote(attribute)}, h.{Quote(ValidAtColumn)}, h.{Quote(ValidOffsetColumn)} " +
                $"FROM {table} AS e JOIN {Quote(HistoryTable(entity, attribute))} AS h ON h.rowid = " +
                $"(SELECT l.rowid FROM {Quote(HistoryTable(entity, attribute))} AS l WHERE l.{Quote(EntityColumn)} = e.{id} " +
                $"ORDER BY l.{Quote(ValidAtColumn)} DESC, l.rowid DESC LIMIT 1) " +
                $"ORDER BY e.{id}",

            _ => throw new InvalidOperationException($"Layout {target.Layout} is not known."),
        };

        var values = new List<EntityValue>();
        using (var command = Command(sql))
        using (var reader = command.ExecuteReader())
        {
            var timed = target.Layout != AttributeLayout.Column;
            while (reader.Read())
            {
                var value = SqliteValues.Read(reader.GetValue(1), target.Type);
                var validAt = timed ? FromStored(reader.GetString(2), reader.GetInt64(3)) : (DateTimeOffset?)null;
                values.Add(new EntityValue(new EntityRef(entity, reader.GetInt64(0)), new StoredValue(value, validAt)));
            }
        }

        Observe(entity, attribute, Counter.ReadCurrentAll);
        return values;
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

        var history = target.Layout switch
        {
            AttributeLayout.HistoryTable => ReadHistory(entity, attribute, newestFirst: false, limit: null),
            AttributeLayout.CurrentColumnAndHistoryTable => ReadSplitHistory(entity, attribute),
            _ => throw new InvalidOperationException($"Layout {target.Layout} cannot hold a history."),
        };
        Observe(entity.Entity, attribute, Counter.ReadHistory);
        return history;
    }

    // ---------- Observation (E-31, E-32) ----------

    /// <summary>The current waking phase: from one sleep to the next. The first phase is 1.</summary>
    public long Phase { get; private set; }

    /// <summary>When a phase began.</summary>
    /// <exception cref="ArgumentException">There is no such phase.</exception>
    public DateTimeOffset PhaseStartedAt(long phase)
    {
        using var command = Command(
            $"SELECT started_at, started_offset FROM {Quote(PhasesTable)} WHERE phase = $phase", ("$phase", phase));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new ArgumentException($"There is no phase {phase}.", nameof(phase));
        return FromStored(reader.GetString(0), reader.GetInt64(1));
    }

    /// <summary>
    /// How an attribute was used in a phase, by default the current one.
    /// For the current phase this includes counts not yet written to the file.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown entity or attribute.</exception>
    public AttributeObservations GetObservations(string entity, string attribute, long? phase = null)
    {
        Structure.Entity(entity).Attribute(attribute);
        var which = phase ?? Phase;

        var counts = new Dictionary<Counter, long>();
        using (var command = Command(
            $"SELECT counter, count FROM {Quote(ObservationsTable)} " +
            "WHERE phase = $phase AND entity = $entity AND attribute = $attribute",
            ("$phase", which), ("$entity", entity), ("$attribute", attribute)))
        using (var reader = command.ExecuteReader())
        {
            var byName = Enum.GetValues<Counter>().ToDictionary(Counters.Name);
            while (reader.Read())
                if (byName.TryGetValue(reader.GetString(0), out var counter))
                    counts[counter] = reader.GetInt64(1);
        }

        if (which == Phase)
            foreach (var counter in Enum.GetValues<Counter>())
                counts[counter] = counts.GetValueOrDefault(counter) + _pending.Get(entity, attribute, counter);

        return AttributeObservations.From(counts);
    }

    /// <summary>Writes the counts kept in memory to the file (E-32). Called on closing and before each sleep.</summary>
    public void FlushObservations()
    {
        if (_pending.IsEmpty)
            return;

        InTransaction(() =>
        {
            foreach (var (entity, attribute, counter, count) in _pending.All())
                Execute(
                    $"INSERT INTO {Quote(ObservationsTable)} (phase, entity, attribute, counter, count) " +
                    "VALUES ($phase, $entity, $attribute, $counter, $count) " +
                    "ON CONFLICT (phase, entity, attribute, counter) DO UPDATE SET count = count + excluded.count",
                    ("$phase", Phase), ("$entity", entity), ("$attribute", attribute),
                    ("$counter", Counters.Name(counter)), ("$count", count));
        });
        _pending.Clear();
    }

    /// <summary>
    /// Ends the current phase and begins a new one with all counts at zero. The sleep calls this (E-31).
    /// Earlier phases are kept.
    /// </summary>
    /// <returns>The number of the new phase.</returns>
    public long StartNewPhase()
    {
        FlushObservations();
        var next = Phase + 1;
        var now = _time.GetUtcNow();
        Execute(
            $"INSERT INTO {Quote(PhasesTable)} (phase, started_at, started_offset) VALUES ($phase, $at, $offset)",
            ("$phase", next), ("$at", ToSortableUtc(now)), ("$offset", ToOffsetMinutes(now)));
        Phase = next;
        return next;
    }

    /// <summary>
    /// Within the returned scope, operations are not counted (E-31). For checks that are not use of the data,
    /// such as the demo comparing all values before and after a sleep (E-36). Scopes may be nested.
    /// </summary>
    public IDisposable Unobserved()
    {
        _unobserved++;
        return new UnobservedScope(this);
    }

    private sealed class UnobservedScope(SqliteStore store) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            store._unobserved--;
        }
    }

    private void Observe(string entity, string attribute, Counter counter)
    {
        if (_unobserved == 0)
            _pending.Add(entity, attribute, counter);
    }

    private void ObserveWrite(string entity, string attribute, bool late)
    {
        Observe(entity, attribute, Counter.Write);
        if (late)
            Observe(entity, attribute, Counter.LateWrite);
    }

    // Creates the observation tables if missing and returns the current phase.
    private static long PrepareObservations(SqliteConnection connection, SqliteTransaction transaction, TimeProvider time)
    {
        Execute(connection, transaction,
            $"CREATE TABLE IF NOT EXISTS {Quote(PhasesTable)} (" +
            "phase INTEGER PRIMARY KEY, started_at TEXT NOT NULL, started_offset INTEGER NOT NULL)");
        Execute(connection, transaction,
            $"CREATE TABLE IF NOT EXISTS {Quote(ObservationsTable)} (" +
            "phase INTEGER NOT NULL REFERENCES " + Quote(PhasesTable) + " (phase), " +
            "entity TEXT NOT NULL, attribute TEXT NOT NULL, counter TEXT NOT NULL, count INTEGER NOT NULL, " +
            "PRIMARY KEY (phase, entity, attribute, counter))");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT max(phase) FROM {Quote(PhasesTable)}";
        if (command.ExecuteScalar() is long phase)
            return phase;

        var now = time.GetUtcNow();
        Execute(connection, transaction,
            $"INSERT INTO {Quote(PhasesTable)} (phase, started_at, started_offset) VALUES (1, $at, $offset)",
            ("$at", ToSortableUtc(now)), ("$offset", ToOffsetMinutes(now)));
        return 1;
    }

    // ---------- Sleep (E-34, E-35) ----------

    /// <summary>Number of entities, e.g. persons. Part of the data shape the sleep reads from the tables (E-31).</summary>
    /// <exception cref="ArgumentException">Unknown entity.</exception>
    public long CountEntities(string entity)
    {
        Structure.Entity(entity);
        return (long)Scalar($"SELECT count(*) FROM {Quote(EntityTable(entity))}")!;
    }

    /// <summary>
    /// Number of stored values of an attribute: for <c>@history</c> every value in the history, otherwise one per entity
    /// that has a value. The sleep uses it to estimate the cost of a layout change (E-34).
    /// </summary>
    /// <exception cref="ArgumentException">Unknown entity or attribute.</exception>
    public long CountValues(string entity, string attribute)
    {
        var target = Structure.Entity(entity).Attribute(attribute);
        var table = Quote(EntityTable(entity));
        var sql = target.Layout switch
        {
            AttributeLayout.Column => $"SELECT count(*) FROM {table} WHERE {Quote(attribute)} IS NOT NULL",
            AttributeLayout.ColumnWithTime => $"SELECT count(*) FROM {table} WHERE {Quote(CurrentValidAtColumn(attribute))} IS NOT NULL",
            AttributeLayout.HistoryTable => $"SELECT count(*) FROM {Quote(HistoryTable(entity, attribute))}",
            AttributeLayout.CurrentColumnAndHistoryTable =>
                $"SELECT (SELECT count(*) FROM {Quote(HistoryTable(entity, attribute))}) + " +
                $"(SELECT count(*) FROM {table} WHERE {Quote(CurrentValidAtColumn(attribute))} IS NOT NULL)",
            _ => throw new InvalidOperationException($"Layout {target.Layout} is not known."),
        };
        return (long)Scalar(sql)!;
    }

    /// <summary>
    /// Runs several steps as one unit, e.g. a whole sleep: layout changes, log entries and the start of a new phase.
    /// If a step fails, the file and this store are left as before. Counts that are not yet written to the file
    /// are lost on failure if a step wrote them, so the sleep writes them before.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called inside another unit.</exception>
    public void InOneTransaction(Action action)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A transaction is already running.");

        var structure = Structure;
        var phase = Phase;
        try
        {
            InTransaction(action);
        }
        catch
        {
            Structure = structure;
            Phase = phase;
            throw;
        }
    }

    /// <summary>Appends entries to the sleep log in the file (E-35), for the phase they evaluated.</summary>
    public void WriteSleepLog(long phase, IEnumerable<SleepLogEntry> entries)
    {
        var now = _time.GetUtcNow();
        InTransaction(() =>
        {
            foreach (var entry in entries)
                Execute(
                    $"INSERT INTO {Quote(SleepLogTable)} (phase, logged_at, logged_offset, kind, entity, attribute, summary, details) " +
                    "VALUES ($phase, $at, $offset, $kind, $entity, $attribute, $summary, $details)",
                    ("$phase", phase), ("$at", ToSortableUtc(now)), ("$offset", ToOffsetMinutes(now)),
                    ("$kind", SleepLogKinds.Name(entry.Kind)), ("$entity", entry.Entity), ("$attribute", entry.Attribute),
                    ("$summary", entry.Summary), ("$details", entry.Details));
        });
    }

    /// <summary>The whole sleep log, in the order it was written.</summary>
    public IReadOnlyList<SleepLogRecord> GetSleepLog()
    {
        using var command = Command(
            $"SELECT phase, logged_at, logged_offset, kind, entity, attribute, summary, details FROM {Quote(SleepLogTable)} ORDER BY entry");
        using var reader = command.ExecuteReader();
        var records = new List<SleepLogRecord>();
        while (reader.Read())
            records.Add(new SleepLogRecord(
                reader.GetInt64(0),
                FromStored(reader.GetString(1), reader.GetInt64(2)),
                new SleepLogEntry(
                    SleepLogKinds.Parse(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7))));
        return records;
    }

    // Creates the log table if missing.
    private static void PrepareSleepLog(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction,
            $"CREATE TABLE IF NOT EXISTS {Quote(SleepLogTable)} (" +
            "entry INTEGER PRIMARY KEY, " +
            "phase INTEGER NOT NULL REFERENCES " + Quote(PhasesTable) + " (phase), " +
            "logged_at TEXT NOT NULL, logged_offset INTEGER NOT NULL, " +
            "kind TEXT NOT NULL, entity TEXT, attribute TEXT, summary TEXT NOT NULL, details TEXT NOT NULL DEFAULT '')");
    }

    // ---------- Changing the layout ----------

    /// <summary>
    /// Changes how the values of one attribute are stored. The information stays the same (E-25):
    /// every value, its valid time and the current value read the same before and after.
    /// Tables and structure model change in one transaction (E-33).
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
            entity, attribute);
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
            entity, attribute);

    private List<TimedValue> ReadCurrentTimed(EntityRef entity, string attribute) =>
        ReadTimed(
            $"SELECT {Quote(attribute)}, {Quote(CurrentValidAtColumn(attribute))}, {Quote(CurrentValidOffsetColumn(attribute))} " +
            $"FROM {Quote(EntityTable(entity.Entity))} " +
            $"WHERE {Quote(IdColumn)} = $id AND {Quote(CurrentValidAtColumn(attribute))} IS NOT NULL",
            entity, attribute);

    private List<TimedValue> ReadTimed(string sql, EntityRef entity, string attribute)
    {
        var type = Structure.Entity(entity.Entity).Attribute(attribute).Type;
        var id = entity.Id;
        using var command = Command(sql, ("$id", id));
        using var reader = command.ExecuteReader();
        var values = new List<TimedValue>();
        while (reader.Read())
        {
            var value = SqliteValues.Read(reader.GetValue(0), type);
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
            "type TEXT NOT NULL, time TEXT NOT NULL, layout TEXT NOT NULL, PRIMARY KEY (entity, attribute))");

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
                    $"INSERT INTO {Quote(StructureTable)} (entity, attribute, position, type, time, layout) " +
                    "VALUES ($entity, $attribute, $position, $type, $time, $layout)",
                    ("$entity", entity.Name), ("$attribute", attribute.Name), ("$position", a),
                    ("$type", BaseTypes.Name(attribute.Type)),
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
                $"SELECT entity, attribute, type, time, layout FROM {Quote(StructureTable)} ORDER BY entity, position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var type = BaseTypes.FromName(reader.GetString(2))
                    ?? throw new InvalidOperationException($"Unknown base type '{reader.GetString(2)}' in the database file.");
                var layout = Enum.Parse<AttributeLayout>(reader.GetString(4));
                var list = attributes.TryGetValue(reader.GetString(0), out var existing)
                    ? existing
                    : attributes[reader.GetString(0)] = [];
                list.Add(new AttributeStructure(reader.GetString(1), type, Enum.Parse<AttributeTime>(reader.GetString(3)), layout));
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
        // Untyped value columns: SQLite keeps the type of each value, Tursiops checks it on writing (E-23, E-41).
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

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return command.ExecuteScalar() is not null;
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
