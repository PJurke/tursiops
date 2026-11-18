namespace Tursiops.Core.Sleep;

// The sleep log (E-04, E-35): every decision in readable English, kept in the database file.

/// <summary>What a log entry is about. The names are stored in the database file.</summary>
public enum SleepLogKind
{
    /// <summary>Which phase the sleep evaluated.</summary>
    Sleep,

    /// <summary>Too few operations to decide on the layout (E-34).</summary>
    NotEnoughData,

    /// <summary>The layout stays (E-34).</summary>
    LayoutKept,

    /// <summary>The layout changed (E-34).</summary>
    LayoutChanged,
}

/// <summary>
/// One entry of the sleep log. <see cref="Entity"/> and <see cref="Attribute"/> are null for the whole sleep.
/// E-37: <see cref="Summary"/> says in plain words what Tursiops did and what it means for the developer;
/// <see cref="Details"/> gives the reason in one sentence, then the numbers to recompute it.
/// </summary>
public sealed record SleepLogEntry(SleepLogKind Kind, string? Entity, string? Attribute, string Summary, string Details = "")
{
    /// <summary>Summary and details as one text.</summary>
    public string Message => Details.Length == 0 ? Summary : $"{Summary} {Details}";
}

/// <summary>A stored entry: which waking phase it evaluated and when it was written.</summary>
public sealed record SleepLogRecord(long Phase, DateTimeOffset LoggedAt, SleepLogEntry Entry);

internal static class SleepLogKinds
{
    public static string Name(SleepLogKind kind) => kind switch
    {
        SleepLogKind.Sleep => "sleep",
        SleepLogKind.NotEnoughData => "not_enough_data",
        SleepLogKind.LayoutKept => "layout_kept",
        SleepLogKind.LayoutChanged => "layout_changed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static SleepLogKind Parse(string name) =>
        Enum.GetValues<SleepLogKind>().First(k => Name(k) == name);
}
