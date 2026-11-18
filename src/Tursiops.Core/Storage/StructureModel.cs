using Tursiops.Core.Format;

namespace Tursiops.Core.Storage;

// Tursiops' own structure terms (E-33): for each attribute, its storage layout.
// Says nothing about tables; the translator for a target system decides that.
// The user decides what information exists (AttributeTime), Tursiops decides how it is stored (E-25).

/// <summary>How the values of an attribute are stored (docs/storage.md, docs/concepts/time.md).</summary>
public enum AttributeLayout
{
    /// <summary>The value as a column of the entity. For attributes without annotation.</summary>
    Column,

    /// <summary>The value and its valid time as columns of the entity. For attributes with <c>@time</c>.</summary>
    ColumnWithTime,

    /// <summary>
    /// H2: every value with its valid time in a history table; the current value is looked up there.
    /// For attributes with <c>@history</c>.
    /// </summary>
    HistoryTable,

    /// <summary>
    /// H1: the value with the latest valid time as columns of the entity, older values in a history table.
    /// For attributes with <c>@history</c>.
    /// </summary>
    CurrentColumnAndHistoryTable,
}

/// <summary>Which layouts can hold which information (E-25). A layout never changes the information.</summary>
public static class AttributeLayouts
{
    /// <summary>The layouts Tursiops may choose from for an attribute, the first one being the initial choice.</summary>
    public static IReadOnlyList<AttributeLayout> AllowedFor(AttributeTime time) => time switch
    {
        AttributeTime.Timeless => [AttributeLayout.Column],
        AttributeTime.CurrentOnly => [AttributeLayout.ColumnWithTime],
        // E-30: sleep chooses between H2 and H1 by usage.
        AttributeTime.History => [AttributeLayout.HistoryTable, AttributeLayout.CurrentColumnAndHistoryTable],
        _ => throw new ArgumentOutOfRangeException(nameof(time), time, null),
    };
}

/// <summary>Storage decision for one attribute, with the content it stores: base type (E-41) and time (E-25).</summary>
public sealed record AttributeStructure(string Name, BaseType Type, AttributeTime Time, AttributeLayout Layout);

/// <summary>Storage decisions for one entity.</summary>
public sealed record EntityStructure(string Name, IReadOnlyList<AttributeStructure> Attributes)
{
    /// <exception cref="ArgumentException">The entity has no such attribute.</exception>
    public AttributeStructure Attribute(string name) =>
        Attributes.FirstOrDefault(a => a.Name == name)
        ?? throw new ArgumentException($"The entity '{Name}' has no attribute named '{name}'.", nameof(name));
}

/// <summary>All storage decisions of a database.</summary>
public sealed record StructureModel(IReadOnlyList<EntityStructure> Entities)
{
    /// <summary>Initial structure for an ontology: each attribute gets the first layout allowed for its information.</summary>
    public static StructureModel FromOntology(Ontology ontology) =>
        new(ontology.Entities
            .Select(e => new EntityStructure(
                e.Name,
                e.Attributes
                    .Select(a => new AttributeStructure(a.Name, a.Type, a.Time, AttributeLayouts.AllowedFor(a.Time)[0]))
                    .ToList()))
            .ToList());

    /// <exception cref="ArgumentException">There is no such entity.</exception>
    public EntityStructure Entity(string name) =>
        Entities.FirstOrDefault(e => e.Name == name)
        ?? throw new ArgumentException($"There is no entity named '{name}'.", nameof(name));

    /// <summary>Returns a copy of the model in which one attribute has a different layout.</summary>
    /// <exception cref="ArgumentException">
    /// Unknown entity or attribute, or a layout that cannot hold the attribute's information (E-25).
    /// </exception>
    public StructureModel WithLayout(string entity, string attribute, AttributeLayout layout)
    {
        var target = Entity(entity).Attribute(attribute);
        if (!AttributeLayouts.AllowedFor(target.Time).Contains(layout))
            throw new ArgumentException(
                $"'{entity}.{attribute}' cannot be stored as {layout}: that layout does not fit {Describe(target.Time)}.",
                nameof(layout));

        return new StructureModel(Entities
            .Select(e => e.Name != entity
                ? e
                : e with
                {
                    Attributes = e.Attributes
                        .Select(a => a.Name == attribute ? a with { Layout = layout } : a)
                        .ToList(),
                })
            .ToList());
    }

    internal static string Describe(AttributeTime time) => time switch
    {
        AttributeTime.Timeless => "an attribute without time",
        AttributeTime.CurrentOnly => "an attribute marked with @time",
        AttributeTime.History => "an attribute marked with @history",
        _ => time.ToString(),
    };
}
