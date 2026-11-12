namespace Tursiops.Core.Format;

// Ergebnis des Parsers: die Welt, wie der Admin sie beschreibt (E-07).
// Enthält nur, was in der Datei steht. Strukturentscheidungen trifft Tursiops später selbst.

/// <summary>Position in einer .tur-Datei, Zeile und Spalte ab 1.</summary>
public readonly record struct SourcePosition(int Line, int Column)
{
    public override string ToString() => $"line {Line}, column {Column}";
}

/// <summary>Eine geparste Ontologie-Datei.</summary>
public sealed record Ontology(IReadOnlyList<EntityDefinition> Entities);

/// <summary>Eine Entität wie <c>Mensch { … }</c>.</summary>
public sealed record EntityDefinition(
    string Name,
    IReadOnlyList<AttributeDefinition> Attributes,
    SourcePosition Position);

/// <summary>Ein Attribut einer Entität, etwa <c>Körpergröße @time</c>.</summary>
/// <param name="Annotations">Namen der Annotationen ohne <c>@</c>, in der Reihenfolge der Datei.</param>
public sealed record AttributeDefinition(
    string Name,
    IReadOnlyList<string> Annotations,
    SourcePosition Position)
{
    /// <summary>Das Attribut ist zeitlich veränderlich (<c>@time</c>, E-03).</summary>
    public bool IsTimed => Annotations.Contains(Annotation.Time);
}

/// <summary>Bekannte Annotationen. In Format v0.1 nur <c>@time</c> (E-17).</summary>
public static class Annotation
{
    public const string Time = "time";

    public static IReadOnlyList<string> Known { get; } = [Time];
}
