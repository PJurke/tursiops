using System.Globalization;
using Tursiops.Core.Format;

namespace Tursiops.Core.Storage.Sqlite;

/// <summary>
/// Reads values back in their declared base type (E-42). SQLite keeps only integers, reals and text:
/// Microsoft.Data.Sqlite stores <c>bool</c> as 0 or 1, and dates and <c>decimal</c> as text,
/// and returns them that way. Only the translator knows this.
/// </summary>
internal static class SqliteValues
{
    // The formats Microsoft.Data.Sqlite writes (https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/types).
    private const string DateOnlyFormat = "yyyy-MM-dd";
    private const string DateTimeOffsetFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz";

    /// <summary>The stored value as the declared type, or <c>null</c>.</summary>
    /// <exception cref="InvalidOperationException">The file holds a value that cannot be of this type.</exception>
    public static object? Read(object? stored, BaseType type) => (type, stored) switch
    {
        (_, null or DBNull) => null,
        (BaseType.Text, string text) => text,
        (BaseType.Number, long whole) => whole,
        (BaseType.Number, double real) => real,
        (BaseType.Number, string exact) => decimal.Parse(exact, NumberStyles.Float, CultureInfo.InvariantCulture),
        (BaseType.Bi, long flag) => flag != 0,
        (BaseType.Date, string { Length: 10 } day) => DateOnly.ParseExact(day, DateOnlyFormat, CultureInfo.InvariantCulture),
        (BaseType.Date, string moment) => DateTimeOffset.ParseExact(moment, DateTimeOffsetFormat, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(
            $"The database file holds the value {stored} ({stored.GetType().Name}) for an attribute declared as " +
            $"'{BaseTypes.Name(type)}'. The file may have been changed outside Tursiops."),
    };
}
