using System.Globalization;

namespace Tursiops.Core.Storage.Sqlite;

// Names of tables and columns in SQLite (E-21).
// Ontology names never contain dots and always start with a letter (E-24),
// so names with a dot or a leading underscore can never collide with them.
internal static class SqliteNames
{
    public const string StructureTable = "_tursiops_structure";
    public const string EntitiesTable = "_tursiops_entities";
    public const string PhasesTable = "_tursiops_phases";
    public const string ObservationsTable = "_tursiops_observations";

    public const string IdColumn = "_id";
    public const string EntityColumn = "_entity";
    public const string ValidAtColumn = "_valid_at";
    public const string ValidOffsetColumn = "_valid_offset";

    /// <summary>Table of an entity, e.g. <c>Mensch</c>.</summary>
    public static string EntityTable(string entity) => entity;

    /// <summary>History table of an attribute, e.g. <c>Mensch.Körpergröße</c>.</summary>
    public static string HistoryTable(string entity, string attribute) => $"{entity}.{attribute}";

    public static string HistoryIndex(string entity, string attribute) => $"{entity}.{attribute}._by_time";

    /// <summary>In the entity table: when the current value was valid, e.g. <c>Körpergröße._valid_at</c>.</summary>
    public static string CurrentValidAtColumn(string attribute) => $"{attribute}.{ValidAtColumn}";

    public static string CurrentValidOffsetColumn(string attribute) => $"{attribute}.{ValidOffsetColumn}";

    /// <summary>Quotes a name for SQL. Every name is quoted (E-21).</summary>
    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    // Valid times are stored in UTC with a fixed width, so text order equals time order.
    // The original offset is kept in a second column, so nothing is lost.
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string ToSortableUtc(DateTimeOffset time) =>
        time.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);

    public static long ToOffsetMinutes(DateTimeOffset time) => (long)time.Offset.TotalMinutes;

    public static DateTimeOffset FromStored(string utc, long offsetMinutes)
    {
        var instant = DateTime.ParseExact(utc, UtcFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return new DateTimeOffset(instant, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(offsetMinutes));
    }
}
