namespace Tursiops.Core.Format;

/// <summary>Kind of error in a .tur file. Tests check the kind, not the wording (E-17).</summary>
public enum TurErrorKind
{
    /// <summary>A name was expected, e.g. for an entity, an attribute or after <c>@</c>.</summary>
    ExpectedName,

    /// <summary><c>{</c> is missing after the entity name.</summary>
    ExpectedOpeningBrace,

    /// <summary>The file ends before the entity was closed with <c>}</c>.</summary>
    ExpectedClosingBrace,

    /// <summary>The line should have ended here.</summary>
    ExpectedLineEnd,

    /// <summary>A name is a keyword, such as <c>in</c>.</summary>
    ReservedWord,

    /// <summary>An entity with this name already exists in the file.</summary>
    DuplicateEntity,

    /// <summary>An attribute with this name already exists in the entity.</summary>
    DuplicateAttribute,

    /// <summary>The annotation is unknown.</summary>
    UnknownAnnotation,

    /// <summary>The attribute already has this annotation.</summary>
    DuplicateAnnotation,

    /// <summary>Units (<c>in cm</c>) are planned but not supported yet.</summary>
    UnitsNotSupported,
}

/// <summary>Error in a .tur file, with its position and a message in plain language.</summary>
public sealed class TurSyntaxException(TurErrorKind kind, SourcePosition position, string description)
    : Exception($"Line {position.Line}, column {position.Column}: {description}")
{
    public TurErrorKind Kind { get; } = kind;

    public SourcePosition Position { get; } = position;

    /// <summary>The message without the position.</summary>
    public string Description { get; } = description;
}
