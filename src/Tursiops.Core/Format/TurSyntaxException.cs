namespace Tursiops.Core.Format;

/// <summary>Art eines Fehlers in einer .tur-Datei. Tests prüfen die Art, nicht den Wortlaut (E-17).</summary>
public enum TurErrorKind
{
    /// <summary>Ein Name wurde erwartet, etwa für eine Entität, ein Attribut oder nach <c>@</c>.</summary>
    ExpectedName,

    /// <summary><c>{</c> nach dem Entitätsnamen fehlt.</summary>
    ExpectedOpeningBrace,

    /// <summary>Die Datei endet, bevor die Entität mit <c>}</c> geschlossen wurde.</summary>
    ExpectedClosingBrace,

    /// <summary>Hier hätte die Zeile enden müssen.</summary>
    ExpectedLineEnd,

    /// <summary>Ein Name ist ein Schlüsselwort, etwa <c>in</c>.</summary>
    ReservedWord,

    /// <summary>Eine Entität dieses Namens gibt es in der Datei schon.</summary>
    DuplicateEntity,

    /// <summary>Ein Attribut dieses Namens gibt es in der Entität schon.</summary>
    DuplicateAttribute,

    /// <summary>Die Annotation ist nicht bekannt.</summary>
    UnknownAnnotation,

    /// <summary>Die Annotation steht an diesem Attribut schon.</summary>
    DuplicateAnnotation,

    /// <summary>Einheiten (<c>in cm</c>) sind vorgesehen, aber noch nicht unterstützt.</summary>
    UnitsNotSupported,
}

/// <summary>Fehler in einer .tur-Datei, mit Position und einer Meldung in einfacher Sprache.</summary>
public sealed class TurSyntaxException(TurErrorKind kind, SourcePosition position, string description)
    : Exception($"Line {position.Line}, column {position.Column}: {description}")
{
    public TurErrorKind Kind { get; } = kind;

    public SourcePosition Position { get; } = position;

    /// <summary>Die Meldung ohne Positionsangabe.</summary>
    public string Description { get; } = description;
}
