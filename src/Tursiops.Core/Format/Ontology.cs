namespace Tursiops.Core.Format;

// Parser output: the world as the admin describes it (E-07).
// Contains only what the file says. Tursiops makes structural decisions on its own later.

/// <summary>Position in a .tur file; line and column start at 1.</summary>
public readonly record struct SourcePosition(int Line, int Column)
{
    public override string ToString() => $"line {Line}, column {Column}";
}

/// <summary>A parsed ontology file.</summary>
public sealed record Ontology(IReadOnlyList<EntityDefinition> Entities);

/// <summary>An entity such as <c>Mensch { … }</c>.</summary>
public sealed record EntityDefinition(
    string Name,
    IReadOnlyList<AttributeDefinition> Attributes,
    SourcePosition Position);

/// <summary>An attribute of an entity, such as <c>Körpergröße: number @history</c>.</summary>
/// <param name="Type">The base type the admin declared (E-41).</param>
/// <param name="Annotations">Annotation names without <c>@</c>, in file order, each at most once.</param>
public sealed record AttributeDefinition(
    string Name,
    BaseType Type,
    IReadOnlyList<string> Annotations,
    SourcePosition Position)
{
    /// <summary>How the attribute relates to time, as the admin described it (E-25).</summary>
    public AttributeTime Time =>
        Annotations.Contains(Annotation.History) ? AttributeTime.History
        : Annotations.Contains(Annotation.Time) ? AttributeTime.CurrentOnly
        : AttributeTime.Timeless;
}

/// <summary>
/// The base type of an attribute (E-41). The admin declares it; Tursiops never derives it.
/// It is content, like time (E-25): values of another type are rejected, not converted.
/// </summary>
public enum BaseType
{
    /// <summary><c>number</c>: whole numbers and decimals.</summary>
    Number,

    /// <summary><c>text</c>: text, even if it looks like a number.</summary>
    Text,

    /// <summary><c>date</c>: a point in time.</summary>
    Date,

    /// <summary><c>bi</c>: two-valued, only <c>true</c> or <c>false</c>.</summary>
    Bi,
}

/// <summary>How base types are written in format v0.1 (E-41).</summary>
public static class BaseTypes
{
    /// <summary>The name of a base type in a .tur file.</summary>
    public static string Name(BaseType type) => type switch
    {
        BaseType.Number => "number",
        BaseType.Text => "text",
        BaseType.Date => "date",
        BaseType.Bi => "bi",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>All base types in the order the documentation lists them.</summary>
    public static IReadOnlyList<BaseType> All { get; } = [BaseType.Number, BaseType.Text, BaseType.Date, BaseType.Bi];

    /// <summary>The base type with this exact name, or <c>null</c>. Names are case-sensitive.</summary>
    public static BaseType? FromName(string name) =>
        All.Cast<BaseType?>().FirstOrDefault(t => Name(t!.Value) == name);
}

/// <summary>
/// What information an attribute carries with respect to time (E-25).
/// The user decides this in the ontology; Tursiops never changes it.
/// </summary>
public enum AttributeTime
{
    /// <summary>No annotation: only the last value written counts.</summary>
    Timeless,

    /// <summary><c>@time</c>: only the current state counts, i.e. the value with the latest valid time.</summary>
    CurrentOnly,

    /// <summary><c>@history</c>: every value is kept with its valid time.</summary>
    History,
}

/// <summary>Known annotations in format v0.1 (E-24).</summary>
public static class Annotation
{
    public const string Time = "time";
    public const string History = "history";

    public static IReadOnlyList<string> Known { get; } = [Time, History];
}

/// <summary>Kind of warning in a .tur file. Tests check the kind, not the wording (E-24).</summary>
public enum TurWarningKind
{
    /// <summary>The same annotation appears twice on one attribute, e.g. <c>@time @time</c>.</summary>
    DuplicateAnnotation,

    /// <summary><c>@time</c> together with <c>@history</c>; <c>@history</c> already includes time.</summary>
    RedundantTime,
}

/// <summary>Something in a .tur file that is clear in meaning but unnecessary. Does not stop parsing (E-24).</summary>
public sealed record TurWarning(TurWarningKind Kind, SourcePosition Position, string Description)
{
    public override string ToString() => $"Line {Position.Line}, column {Position.Column}: {Description}";
}

/// <summary>Result of parsing: the ontology and any warnings, in file order (E-24).</summary>
public sealed record TurParseResult(Ontology Ontology, IReadOnlyList<TurWarning> Warnings);
