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

/// <summary>An attribute of an entity, such as <c>Körpergröße @history</c>.</summary>
/// <param name="Annotations">Annotation names without <c>@</c>, in file order, each at most once.</param>
public sealed record AttributeDefinition(
    string Name,
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
