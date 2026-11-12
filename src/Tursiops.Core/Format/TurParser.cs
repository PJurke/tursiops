using System.Text;

namespace Tursiops.Core.Format;

/// <summary>
/// Liest Ontologie-Dateien im Format v0.1 (Grammatik E-17).
/// Von Hand geschrieben und bricht beim ersten Fehler ab (E-16).
/// </summary>
public static class TurParser
{
    public const string FileExtension = ".tur";

    /// <summary>Liest eine .tur-Datei (E-11).</summary>
    /// <exception cref="ArgumentException">Die Datei endet nicht auf .tur.</exception>
    /// <exception cref="TurSyntaxException">Die Datei enthält einen Fehler.</exception>
    public static Ontology ParseFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), FileExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Ontology files must end with '{FileExtension}', but '{Path.GetFileName(path)}' does not.",
                nameof(path));

        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Liest den Inhalt einer Ontologie-Datei.</summary>
    /// <exception cref="TurSyntaxException">Der Text enthält einen Fehler.</exception>
    public static Ontology Parse(string source) => new Parser(source).ParseFile();

    private sealed class Parser
    {
        private readonly string _text;
        private int _index;
        private int _line = 1;
        private int _column = 1;

        public Parser(string source)
        {
            // Einheitliche Zeilenenden und Unicode-Form, damit "ö" immer dasselbe Zeichen ist.
            _text = source
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Normalize(NormalizationForm.FormC);
        }

        // ---------- Grammatik ----------

        // file = { entity }
        public Ontology ParseFile()
        {
            var entities = new List<EntityDefinition>();
            var seen = new Dictionary<string, SourcePosition>(StringComparer.Ordinal);

            SkipBlankLinesAndComments();
            while (!AtEnd)
            {
                var entity = ParseEntity(seen);
                entities.Add(entity);
                SkipBlankLinesAndComments();
            }

            return new Ontology(entities);
        }

        // entity = name "{" ( "}" | line_end { attribute } "}" ) line_end
        private EntityDefinition ParseEntity(Dictionary<string, SourcePosition> seenEntities)
        {
            var position = Position;
            var name = ParseName("an entity name");

            if (seenEntities.TryGetValue(name, out var first))
                throw Error(TurErrorKind.DuplicateEntity, position,
                    $"There is already an entity named '{name}' ({first}). Each entity name may appear only once per file.");
            seenEntities[name] = position;

            // Die öffnende Klammer darf auch in einer späteren Zeile stehen (E-17).
            SkipBlankLinesAndComments();
            if (Current != '{')
                throw Error(TurErrorKind.ExpectedOpeningBrace, Position,
                    $"Expected '{{' after the entity name '{name}', but found {Describe(Current)}.");
            Advance();

            var attributes = new List<AttributeDefinition>();
            var seenAttributes = new Dictionary<string, SourcePosition>(StringComparer.Ordinal);

            SkipSpaces();
            if (Current == '}')
            {
                // Leere Entität in einer Zeile: "Mensch { }"
                Advance();
                ExpectLineEnd("after '}'", "Start the next entity on a new line.");
                return new EntityDefinition(name, attributes, position);
            }

            ExpectLineEnd("after '{'", "Put each attribute on its own line.");

            while (true)
            {
                SkipSpaces();

                if (AtEnd)
                    throw Error(TurErrorKind.ExpectedClosingBrace, Position,
                        $"The file ended, but the entity '{name}' ({position}) was not closed. Add '}}' on its own line.");

                if (TryConsumeLineEnd())
                    continue; // Leerzeile oder reine Kommentarzeile

                if (Current == '}')
                {
                    Advance();
                    ExpectLineEnd("after '}'", "Start the next entity on a new line.");
                    return new EntityDefinition(name, attributes, position);
                }

                attributes.Add(ParseAttribute(name, seenAttributes));
            }
        }

        // attribute = name [ "in" unit ] { annotation } line_end
        private AttributeDefinition ParseAttribute(string entityName, Dictionary<string, SourcePosition> seenAttributes)
        {
            var position = Position;
            var name = ParseName("an attribute name");

            if (seenAttributes.TryGetValue(name, out var first))
                throw Error(TurErrorKind.DuplicateAttribute, position,
                    $"The entity '{entityName}' already has an attribute named '{name}' ({first}).");
            seenAttributes[name] = position;

            var annotations = new List<string>();
            while (true)
            {
                SkipSpaces();

                if (IsKeywordIn())
                    throw Error(TurErrorKind.UnitsNotSupported, Position,
                        $"Units like 'in cm' are planned but not supported yet. Remove the unit from '{name}' for now.");

                if (Current != '@')
                    break;

                var atPosition = Position;
                Advance();
                if (!IsLetter(Current))
                    throw Error(TurErrorKind.ExpectedName, Position,
                        $"Expected an annotation name directly after '@', but found {Describe(Current)}.");

                var annotation = ReadNameCharacters();
                CheckAnnotation(annotation, atPosition, name, annotations);
                annotations.Add(annotation);
            }

            ExpectLineEnd($"after the attribute '{name}'", Current switch
            {
                '}' => "Put '}' on its own line.",
                _ when IsLetter(Current) => "Names cannot contain spaces, and each attribute needs its own line.",
                _ => "Only annotations like '@time' or a comment ('//') may follow an attribute.",
            });

            return new AttributeDefinition(name, annotations, position);
        }

        private static void CheckAnnotation(string annotation, SourcePosition position, string attributeName, List<string> existing)
        {
            if (Annotation.Known.Contains(annotation, StringComparer.Ordinal))
            {
                if (existing.Contains(annotation, StringComparer.Ordinal))
                    throw Error(TurErrorKind.DuplicateAnnotation, position,
                        $"The attribute '{attributeName}' already has '@{annotation}'. Use each annotation only once.");
                return;
            }

            var suggestion = Annotation.Known.FirstOrDefault(k => string.Equals(k, annotation, StringComparison.OrdinalIgnoreCase));
            var hint = suggestion is not null
                ? $" Annotations are case-sensitive. Did you mean '@{suggestion}'?"
                : $" Known annotations: {string.Join(", ", Annotation.Known.Select(k => "@" + k))}.";

            throw Error(TurErrorKind.UnknownAnnotation, position, $"Unknown annotation '@{annotation}'.{hint}");
        }

        // name = letter { letter | digit | "_" }
        private string ParseName(string expected)
        {
            var position = Position;
            if (!IsLetter(Current))
                throw Error(TurErrorKind.ExpectedName, position,
                    $"Expected {expected}, but found {Describe(Current)}. Names start with a letter.");

            var name = ReadNameCharacters();
            if (name == "in")
                throw Error(TurErrorKind.ReservedWord, position,
                    "'in' is a keyword (used for units) and cannot be used as a name.");

            return name;
        }

        private string ReadNameCharacters()
        {
            var start = _index;
            while (IsNameCharacter(Current))
                Advance();
            return _text[start.._index];
        }

        // ---------- Zeilen, Leerraum, Kommentare ----------

        // line_end = [ comment ] ( newline | end_of_file )
        private void ExpectLineEnd(string where, string hint)
        {
            if (!TryConsumeLineEnd())
                throw Error(TurErrorKind.ExpectedLineEnd, Position,
                    $"Unexpected {Describe(Current)} {where}. {hint}");
        }

        private bool TryConsumeLineEnd()
        {
            SkipSpaces();

            if (IsCommentStart())
                while (!AtEnd && Current != '\n')
                    Advance();

            if (AtEnd)
                return true;

            if (Current == '\n')
            {
                Advance();
                return true;
            }

            return false;
        }

        private void SkipBlankLinesAndComments()
        {
            while (true)
            {
                SkipSpaces();
                if (AtEnd || !TryConsumeLineEnd())
                    return;
            }
        }

        private void SkipSpaces()
        {
            while (Current is ' ' or '\t')
                Advance();
        }

        // ---------- Zeichen ----------

        private const char EndOfFile = '\0';

        private bool AtEnd => _index >= _text.Length;

        private char Current => AtEnd ? EndOfFile : _text[_index];

        private char Peek(int offset) => _index + offset < _text.Length ? _text[_index + offset] : EndOfFile;

        private SourcePosition Position => new(_line, _column);

        private void Advance()
        {
            if (AtEnd)
                return;

            if (_text[_index] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _index++;
        }

        private bool IsCommentStart() => Current == '/' && Peek(1) == '/';

        // "in" als eigenes Wort, nicht als Anfang von "inhalt".
        private bool IsKeywordIn() => Current == 'i' && Peek(1) == 'n' && !IsNameCharacter(Peek(2));

        private static bool IsLetter(char c) => c != EndOfFile && char.IsLetter(c);

        private static bool IsNameCharacter(char c) => c != EndOfFile && (char.IsLetter(c) || char.IsDigit(c) || c == '_');

        private string Describe(char c) => c switch
        {
            EndOfFile when AtEnd => "the end of the file",
            '\n' => "the end of the line",
            _ => $"'{ReadWordForMessage()}'",
        };

        // Für Meldungen das ganze Wort zeigen ("Alter" statt "A"), ohne die Position zu verändern.
        private string ReadWordForMessage()
        {
            if (!IsNameCharacter(Current))
                return Current.ToString();

            var end = _index;
            while (end < _text.Length && IsNameCharacter(_text[end]))
                end++;
            return _text[_index..end];
        }

        private static TurSyntaxException Error(TurErrorKind kind, SourcePosition position, string description) =>
            new(kind, position, description);
    }
}
