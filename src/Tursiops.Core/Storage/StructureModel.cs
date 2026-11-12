using Tursiops.Core.Format;

namespace Tursiops.Core.Storage;

// Tursiops' own structure terms (E-20): for each attribute, how it is stored.
// Says nothing about tables; the translator for a target system decides that.

/// <summary>How the values of an attribute are stored (E-03).</summary>
public enum AttributeLayout
{
    /// <summary>Only the latest value is kept.</summary>
    CurrentValue,

    /// <summary>Every value is kept together with the time it was valid. Only for attributes with <c>@time</c>.</summary>
    History,
}

/// <summary>Storage decision for one attribute.</summary>
public sealed record AttributeStructure(string Name, bool IsTimed, AttributeLayout Layout);

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
    /// <summary>
    /// Initial structure for an ontology: every attribute keeps only its current value.
    /// Tursiops starts simple and changes the layout during sleep when the data suggests it (E-02).
    /// </summary>
    public static StructureModel FromOntology(Ontology ontology) =>
        new(ontology.Entities
            .Select(e => new EntityStructure(
                e.Name,
                e.Attributes
                    .Select(a => new AttributeStructure(a.Name, a.IsTimed, AttributeLayout.CurrentValue))
                    .ToList()))
            .ToList());

    /// <exception cref="ArgumentException">There is no such entity.</exception>
    public EntityStructure Entity(string name) =>
        Entities.FirstOrDefault(e => e.Name == name)
        ?? throw new ArgumentException($"There is no entity named '{name}'.", nameof(name));

    /// <summary>Returns a copy of the model in which one attribute has a different layout.</summary>
    /// <exception cref="ArgumentException">Unknown entity or attribute, or history for an attribute without <c>@time</c>.</exception>
    public StructureModel WithLayout(string entity, string attribute, AttributeLayout layout)
    {
        var target = Entity(entity).Attribute(attribute);
        if (layout == AttributeLayout.History && !target.IsTimed)
            throw new ArgumentException(
                $"'{entity}.{attribute}' cannot keep a history because it is not marked with @time.", nameof(layout));

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
}
