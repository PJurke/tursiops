using System.Text;

namespace Tursiops.Core.Format;

/// <summary>
/// Reads ontology files in format v0.1 (grammar E-24).
/// Hand-written; stops at the first error, collects warnings without stopping (E-16, E-24).
/// </summary>
public static class TurParser
{
    public const string FileExtension = ".tur";

    /// <summary>Reads a .tur file (E-11).</summary>
    /// <exception cref="ArgumentException">The file does not end with .tur.</exception>
    /// <exception cref="TurSyntaxException">The file contains an error.</exception>
    public static TurParseResult ParseFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), FileExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Ontology files must end with '{FileExtension}', but '{Path.GetFileName(path)}' does not.",
                nameof(path));

        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Reads the content of an ontology file.</summary>
    /// <exception cref="TurSyntaxException">The text contains an error.</exception>
    public static TurParseResult Parse(string source) => new Parser(source).ParseFile();

    private sealed class Parser
    {
        private readonly string _text;
        private readonly List<TurWarning> _warnings = [];
        private int _index;
        private int _line = 1;
        private int _column = 1;

        public Parser(string source)
        {
            // Uniform line endings and Unicode form, so that "ö" is always the same character.
            _text = source
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Normalize(NormalizationForm.FormC);
        }

        // ---------- Grammar ----------

        // file = { entity }
        public TurParseResult ParseFile()
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

            return new TurParseResult(new Ontology(entities), _warnings);
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

            // The opening brace may also be on a later line (E-24).
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
                // Empty entity on one line: "Mensch { }"
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
                    continue; // blank line or comment-only line

                if (Current == '}')
                {
                    Advance();
                    ExpectLineEnd("after '}'", "Start the next entity on a new line.");
                    return new EntityDefinition(name, attributes, position);
                }

                attributes.Add(ParseAttribute(name, seenAttributes));
            }
        }

        // attribute = name ":" type [ "in" unit ] { annotation } line_end
        private AttributeDefinition ParseAttribute(string entityName, Dictionary<string, SourcePosition> seenAttributes)
        {
            var position = Position;
            var name = ParseName("an attribute name");

            if (seenAttributes.TryGetValue(name, out var first))
                throw Error(TurErrorKind.DuplicateAttribute, position,
                    $"The entity '{entityName}' already has an attribute named '{name}' ({first}).");
            seenAttributes[name] = position;

            var type = ParseType(name);

            var annotations = new List<(string Name, SourcePosition Position)>();
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
                CheckAnnotation(annotation, atPosition);
                if (annotations.Any(a => a.Name == annotation))
                    Warn(TurWarningKind.DuplicateAnnotation, atPosition,
                        $"The attribute '{name}' already has '@{annotation}'. The second one has no effect and can be removed.");
                else
                    annotations.Add((annotation, atPosition));
            }

            WarnIfTimeIsRedundant(name, annotations);

            ExpectLineEnd($"after the attribute '{name}'", Current switch
            {
                '}' => "Put '}' on its own line.",
                _ when IsLetter(Current) => "Names cannot contain spaces, and each attribute needs its own line.",
                _ => "Only annotations like '@time' or '@history' or a comment ('//') may follow an attribute.",
            });

            return new AttributeDefinition(name, type, annotations.Select(a => a.Name).ToList(), position);
        }

        // ":" type, with type = "number" | "text" | "date" | "bi" (E-41)
        private BaseType ParseType(string attributeName)
        {
            SkipSpaces();
            if (Current != ':')
            {
                var hint = IsLetter(Current) && !IsKeywordIn()
                    ? " Names cannot contain spaces."
                    : "";
                throw Error(TurErrorKind.MissingType, Position,
                    $"Expected ':' and a type after the attribute '{attributeName}', but found {Describe(Current)}.{hint} " +
                    $"Every attribute declares its type, for example '{attributeName}: number'. Known types: {KnownTypes}.");
            }
            Advance();

            SkipSpaces();
            var position = Position;
            if (!IsLetter(Current))
                throw Error(TurErrorKind.MissingType, position,
                    $"Expected a type after '{attributeName}:', but found {Describe(Current)}. Known types: {KnownTypes}.");

            var name = ReadNameCharacters();
            if (BaseTypes.FromName(name) is { } type)
                return type;

            var sameLetters = BaseTypes.All.Select(BaseTypes.Name)
                .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            var hintText =
                sameLetters is not null ? $" Types are case-sensitive. Did you mean '{sameLetters}'?"
                : name.Equals("boolean", StringComparison.OrdinalIgnoreCase) || name.Equals("bool", StringComparison.OrdinalIgnoreCase)
                    ? $" Two-valued attributes use '{BaseTypes.Name(BaseType.Bi)}'."
                : "";
            throw Error(TurErrorKind.UnknownType, position,
                $"Unknown type '{name}'.{hintText} Known types: {KnownTypes}.");
        }

        private static string KnownTypes => string.Join(", ", BaseTypes.All.Select(BaseTypes.Name));

        // '@history' already includes time, so '@time' next to it says nothing new (E-24).
        private void WarnIfTimeIsRedundant(string attributeName, List<(string Name, SourcePosition Position)> annotations)
        {
            if (!annotations.Any(a => a.Name == Annotation.History))
                return;

            foreach (var time in annotations.Where(a => a.Name == Annotation.Time))
                Warn(TurWarningKind.RedundantTime, time.Position,
                    $"'@{Annotation.History}' already includes time, so '@{Annotation.Time}' on '{attributeName}' has no effect and can be removed.");
        }

        private static void CheckAnnotation(string annotation, SourcePosition position)
        {
            if (Annotation.Known.Contains(annotation, StringComparer.Ordinal))
                return;

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

        // ---------- Lines, whitespace, comments ----------

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

        // ---------- Characters ----------

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

        // "in" as a word of its own, not as the start of "inhalt".
        private bool IsKeywordIn() => Current == 'i' && Peek(1) == 'n' && !IsNameCharacter(Peek(2));

        private static bool IsLetter(char c) => c != EndOfFile && char.IsLetter(c);

        private static bool IsNameCharacter(char c) => c != EndOfFile && (char.IsLetter(c) || char.IsDigit(c) || c == '_');

        private string Describe(char c) => c switch
        {
            EndOfFile when AtEnd => "the end of the file",
            '\n' => "the end of the line",
            _ => $"'{ReadWordForMessage()}'",
        };

        // Show the whole word in messages ("Alter" instead of "A") without moving the position.
        private string ReadWordForMessage()
        {
            if (!IsNameCharacter(Current))
                return Current.ToString();

            var end = _index;
            while (end < _text.Length && IsNameCharacter(_text[end]))
                end++;
            return _text[_index..end];
        }

        private void Warn(TurWarningKind kind, SourcePosition position, string description) =>
            _warnings.Add(new TurWarning(kind, position, description));

        private static TurSyntaxException Error(TurErrorKind kind, SourcePosition position, string description) =>
            new(kind, position, description);
    }
}
