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

/// <summary>An attribute of an entity, such as <c>Körpergröße @time</c>.</summary>
/// <param name="Annotations">Annotation names without <c>@</c>, in file order.</param>
public sealed record AttributeDefinition(
    string Name,
    IReadOnlyList<string> Annotations,
    SourcePosition Position)
{
    /// <summary>The attribute changes over time (<c>@time</c>, E-03).</summary>
    public bool IsTimed => Annotations.Contains(Annotation.Time);
}

/// <summary>Known annotations. Format v0.1 only has <c>@time</c> (E-17).</summary>
public static class Annotation
{
    public const string Time = "time";

    public static IReadOnlyList<string> Known { get; } = [Time];
}
