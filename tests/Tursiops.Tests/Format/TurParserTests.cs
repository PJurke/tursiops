using Tursiops.Core.Format;

namespace Tursiops.Tests.Format;

// Tests for the parser of ontology format v0.1 (grammar E-24, approach E-16).
// Error and warning tests check the kind, line and column, not the message wording (E-24).
public class TurParserTests
{
    // ---------- Valid files ----------

    [Fact]
    public void EmptyFile_HasNoEntities()
    {
        var ontology = Parse("");
        Assert.Empty(ontology.Entities);
    }

    [Fact]
    public void OnlyCommentsAndBlankLines_HasNoEntities()
    {
        var ontology = Parse("// just a comment\n\n   \n// another one\n");
        Assert.Empty(ontology.Entities);
    }

    [Fact]
    public void SingleEntity_WithAttributes()
    {
        var ontology = Parse("""
            Mensch {
              Name: text
              Körpergröße: number @time
            }
            """);

        var mensch = Assert.Single(ontology.Entities);
        Assert.Equal("Mensch", mensch.Name);
        Assert.Collection(mensch.Attributes,
            a => { Assert.Equal("Name", a.Name); Assert.Equal(BaseType.Text, a.Type); Assert.Empty(a.Annotations); Assert.Equal(AttributeTime.Timeless, a.Time); },
            a => { Assert.Equal("Körpergröße", a.Name); Assert.Equal(BaseType.Number, a.Type); Assert.Equal(["time"], a.Annotations); Assert.Equal(AttributeTime.CurrentOnly, a.Time); });
    }

    [Fact]
    public void Positions_AreOneBasedLineAndColumn()
    {
        var ontology = Parse("// header\nMensch {\n  Name: text\n}");

        var mensch = Assert.Single(ontology.Entities);
        Assert.Equal(new SourcePosition(2, 1), mensch.Position);
        Assert.Equal(new SourcePosition(3, 3), Assert.Single(mensch.Attributes).Position);
    }

    [Fact]
    public void MultipleEntities_WithBlankLinesAndComments()
    {
        var ontology = Parse("""
            // People
            Mensch {
              Name: text

              // height changes
              Körpergröße: number @time
            }


            Tier {
              Art: text
            }
            """);

        Assert.Equal(["Mensch", "Tier"], ontology.Entities.Select(e => e.Name));
        Assert.Equal(2, ontology.Entities[0].Attributes.Count);
        Assert.Single(ontology.Entities[1].Attributes);
    }

    [Fact]
    public void NamesMayContainUnicodeLettersDigitsAndUnderscore()
    {
        var ontology = Parse("Straße_2 {\n  Ölstand_1: number\n  Größe3: number @time\n}");

        var entity = Assert.Single(ontology.Entities);
        Assert.Equal("Straße_2", entity.Name);
        Assert.Equal(["Ölstand_1", "Größe3"], entity.Attributes.Select(a => a.Name));
    }

    [Fact]
    public void DecomposedUmlauts_AreTreatedLikeComposedOnes()
    {
        // "Größe" once with precomposed umlauts, once as letter + combining diaeresis.
        var decomposed = "Größe";
        var ontology = Parse($"Mensch {{\n  {decomposed}: number\n}}");

        Assert.Equal("Größe", Assert.Single(Assert.Single(ontology.Entities).Attributes).Name);
    }

    [Fact]
    public void WindowsLineEndingsAndTabs_AreAccepted()
    {
        var ontology = Parse("Mensch {\r\n\tName:\ttext\r\n\tKörpergröße: number\t@time\r\n}\r\n");

        Assert.Equal(2, Assert.Single(ontology.Entities).Attributes.Count);
    }

    [Fact]
    public void NamesAreCaseSensitive()
    {
        var ontology = Parse("Mensch {\n  name: text\n  Name: text\n}\nmensch {\n}");

        Assert.Equal(2, ontology.Entities.Count);
        Assert.Equal(2, ontology.Entities[0].Attributes.Count);
    }

    [Fact]
    public void OpeningBrace_MayStandOnNextLine()
    {
        var ontology = Parse("Mensch\n{\n  Name: text\n}");

        Assert.Single(Assert.Single(ontology.Entities).Attributes);
    }

    [Fact]
    public void OpeningBrace_MayFollowAfterBlankAndCommentLines()
    {
        var ontology = Parse("Mensch // a person\n\n// the brace comes next\n{\n  Name: text\n}");

        Assert.Single(Assert.Single(ontology.Entities).Attributes);
    }

    [Theory]
    [InlineData("Mensch { }")]
    [InlineData("Mensch {}")]
    [InlineData("Mensch {\n}")]
    [InlineData("Mensch {\n\n  // nothing yet\n}")]
    public void EmptyEntity_IsAllowed(string source)
    {
        var entity = Assert.Single(Parse(source).Entities);
        Assert.Empty(entity.Attributes);
    }

    [Fact]
    public void CommentsAtEndOfAnyLine_AreAllowed()
    {
        var ontology = Parse("""
            Mensch { // entity
              Name: text // first and last name
              Körpergröße: number @time // changes
            } // end
            """);

        Assert.Equal(2, Assert.Single(ontology.Entities).Attributes.Count);
    }

    [Fact]
    public void ClosingBrace_MayBeIndented()
    {
        var ontology = Parse("Mensch {\n  Name: text\n  }");
        Assert.Single(ontology.Entities);
    }

    [Fact]
    public void ExampleFile_Parses()
    {
        var ontology = TurParser.ParseFile(Path.Combine(RepositoryRoot(), "examples", "mensch.tur")).Ontology;

        var mensch = Assert.Single(ontology.Entities);
        Assert.Equal("Mensch", mensch.Name);
        var height = mensch.Attributes.Single(a => a.Name == "Körpergröße");
        Assert.Equal(AttributeTime.History, height.Time);
        Assert.Equal(BaseType.Number, height.Type);
    }

    // ---------- Errors: braces and lines ----------

    [Fact]
    public void MissingOpeningBrace_AtNextAttribute()
    {
        AssertError("Mensch\n  Name\n}", TurErrorKind.ExpectedOpeningBrace, 2, 3);
    }

    [Fact]
    public void MissingOpeningBrace_AtEndOfFile()
    {
        AssertError("Mensch", TurErrorKind.ExpectedOpeningBrace, 1, 7);
    }

    [Fact]
    public void MissingClosingBrace_AtEndOfFile()
    {
        AssertError("Mensch {\n  Name: text\n", TurErrorKind.ExpectedClosingBrace, 3, 1);
    }

    [Fact]
    public void AttributeOnSameLineAsOpeningBrace_IsAnError()
    {
        AssertError("Mensch { Name }", TurErrorKind.ExpectedLineEnd, 1, 10);
    }

    [Fact]
    public void ClosingBraceOnAttributeLine_IsAnError()
    {
        AssertError("Mensch {\n  Name: text }", TurErrorKind.ExpectedLineEnd, 2, 14);
    }

    [Fact]
    public void TextAfterClosingBrace_IsAnError()
    {
        AssertError("Mensch {\n} Tier {\n}", TurErrorKind.ExpectedLineEnd, 2, 3);
    }

    [Fact]
    public void TwoWordsAsName_IsAnError_WhereTheTypeShouldFollow()
    {
        var error = AssertError("Mensch {\n  Körper Größe: number\n}", TurErrorKind.MissingType, 2, 10);
        Assert.Contains("spaces", error.Message);
    }

    [Fact]
    public void WordAfterTheType_IsAnError()
    {
        AssertError("Mensch {\n  Körpergröße: number groß\n}", TurErrorKind.ExpectedLineEnd, 2, 23);
    }

    [Fact]
    public void SingleSlash_IsNotAComment()
    {
        AssertError("Mensch {\n  Name: text / comment\n}", TurErrorKind.ExpectedLineEnd, 2, 14);
    }

    [Fact]
    public void StrayClosingBrace_AtTopLevel()
    {
        AssertError("}", TurErrorKind.ExpectedName, 1, 1);
    }

    // ---------- Errors: names ----------

    [Theory]
    [InlineData("1Mensch {\n}", 1, 1)]
    [InlineData("_Mensch {\n}", 1, 1)]
    [InlineData("Mensch {\n  2Name\n}", 2, 3)]
    [InlineData("Mensch {\n  _Name\n}", 2, 3)]
    public void NameMustStartWithLetter(string source, int line, int column)
    {
        AssertError(source, TurErrorKind.ExpectedName, line, column);
    }

    [Fact]
    public void DuplicateEntityName_PointsToSecondOccurrence()
    {
        AssertError("Mensch {\n}\nMensch {\n}", TurErrorKind.DuplicateEntity, 3, 1);
    }

    [Fact]
    public void DuplicateAttributeName_PointsToSecondOccurrence()
    {
        AssertError("Mensch {\n  Name: text\n  Name: text\n}", TurErrorKind.DuplicateAttribute, 3, 3);
    }

    [Fact]
    public void SameAttributeNameInDifferentEntities_IsAllowed()
    {
        var ontology = Parse("Mensch {\n  Name: text\n}\nTier {\n  Name: text\n}");
        Assert.Equal(2, ontology.Entities.Count);
    }

    [Theory]
    [InlineData("in {\n}", 1, 1)]
    [InlineData("Mensch {\n  in\n}", 2, 3)]
    public void KeywordIn_CannotBeAName(string source, int line, int column)
    {
        AssertError(source, TurErrorKind.ReservedWord, line, column);
    }

    [Fact]
    public void NamesThatOnlyStartWithIn_AreAllowed()
    {
        var ontology = Parse("Inventar {\n  inhalt: text\n  in_Arbeit: bi\n}");
        Assert.Equal(2, Assert.Single(ontology.Entities).Attributes.Count);
    }

    // ---------- Base types (E-41) ----------

    [Fact]
    public void BaseTypes_AreParsed()
    {
        var attributes = Assert.Single(Parse("Mensch {\n  Name: text\n  Größe: number\n  Geburtstag: date\n  Lebt: bi\n}").Entities).Attributes;

        Assert.Equal([BaseType.Text, BaseType.Number, BaseType.Date, BaseType.Bi], attributes.Select(a => a.Type));
    }

    [Theory]
    [InlineData("Mensch {\n  Name:text\n}")]
    [InlineData("Mensch {\n  Name : text\n}")]
    [InlineData("Mensch {\n  Name:\ttext\n}")]
    public void SpacesAroundTheColon_AreOptional(string source)
    {
        Assert.Equal(BaseType.Text, Assert.Single(Assert.Single(Parse(source).Entities).Attributes).Type);
    }

    [Fact]
    public void TypeIsFollowedByAnnotations()
    {
        var attribute = Assert.Single(Assert.Single(Parse("Mensch {\n  Körpergröße: number @history // cm\n}").Entities).Attributes);

        Assert.Equal(BaseType.Number, attribute.Type);
        Assert.Equal(AttributeTime.History, attribute.Time);
    }

    [Theory]
    [InlineData("Mensch {\n  Name\n}", 2, 7)]          // line ends after the name
    [InlineData("Mensch {\n  Name // comment\n}", 2, 8)]
    [InlineData("Mensch {\n  Größe @time\n}", 2, 9)]   // annotation instead of the type
    [InlineData("Mensch {\n  Größe in cm\n}", 2, 9)]   // unit instead of the type
    [InlineData("Mensch {\n  Name:\n}", 2, 8)]         // colon without a type
    [InlineData("Mensch {\n  Name: @time\n}", 2, 9)]
    [InlineData("Mensch {\n  Name\n  Alter: number\n}", 2, 7)]
    public void MissingType_IsAnError(string source, int line, int column)
    {
        AssertError(source, TurErrorKind.MissingType, line, column);
    }

    [Fact]
    public void MissingType_ShowsHowToDeclareIt()
    {
        var error = AssertError("Mensch {\n  Größe @time\n}", TurErrorKind.MissingType, 2, 9);
        Assert.Contains("Größe: number", error.Message);
        Assert.Contains("number, text, date, bi", error.Message);
    }

    [Fact]
    public void UnknownType_ListsKnownOnes()
    {
        var error = AssertError("Mensch {\n  Name: string\n}", TurErrorKind.UnknownType, 2, 9);
        Assert.Contains("number, text, date, bi", error.Message);
    }

    [Theory]
    [InlineData("Mensch {\n  Größe: Number\n}", "'number'")]
    [InlineData("Mensch {\n  Größe: TEXT\n}", "'text'")]
    [InlineData("Mensch {\n  Größe: Bi\n}", "'bi'")]
    public void TypeWithWrongCase_IsUnknown_AndSuggestsCorrectSpelling(string source, string suggestion)
    {
        var error = AssertError(source, TurErrorKind.UnknownType, 2, 10);
        Assert.Contains(suggestion, error.Message);
    }

    [Theory]
    [InlineData("boolean")]
    [InlineData("bool")]
    public void BooleanSpelling_SuggestsBi(string type)
    {
        var error = AssertError($"Mensch {{\n  Lebt: {type}\n}}", TurErrorKind.UnknownType, 2, 9);
        Assert.Contains("'bi'", error.Message);
    }

    [Fact]
    public void TypeNamesMayBeUsedAsNames()
    {
        var attribute = Assert.Single(Assert.Single(Parse("Zähler {\n  number: number\n}").Entities).Attributes);
        Assert.Equal("number", attribute.Name);
    }

    // ---------- Errors: annotations ----------

    [Fact]
    public void UnknownAnnotation_PointsToAtSign()
    {
        AssertError("Mensch {\n  Name: text @unique\n}", TurErrorKind.UnknownAnnotation, 2, 14);
    }

    [Fact]
    public void AnnotationWithWrongCase_IsUnknown_AndSuggestsCorrectSpelling()
    {
        var error = AssertError("Mensch {\n  Größe: number @Time\n}", TurErrorKind.UnknownAnnotation, 2, 17);
        Assert.Contains("@time", error.Message);
    }

    [Fact]
    public void AnnotationWithWrongCase_SuggestsHistory()
    {
        var error = AssertError("Mensch {\n  Größe: number @History\n}", TurErrorKind.UnknownAnnotation, 2, 17);
        Assert.Contains("@history", error.Message);
    }

    [Fact]
    public void UnknownAnnotation_ListsKnownOnes()
    {
        var error = AssertError("Mensch {\n  Name: text @unique\n}", TurErrorKind.UnknownAnnotation, 2, 14);
        Assert.Contains("@time", error.Message);
        Assert.Contains("@history", error.Message);
    }

    [Fact]
    public void AtSignWithoutName_IsAnError()
    {
        AssertError("Mensch {\n  Größe: number @ time\n}", TurErrorKind.ExpectedName, 2, 18);
    }

    // ---------- Time: @time and @history (E-24, E-25) ----------

    [Fact]
    public void History_IsParsed()
    {
        var result = TurParser.Parse("Mensch {\n  Körpergröße: number @history\n}");

        var attribute = Assert.Single(Assert.Single(result.Ontology.Entities).Attributes);
        Assert.Equal(["history"], attribute.Annotations);
        Assert.Equal(AttributeTime.History, attribute.Time);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ValidFile_HasNoWarnings()
    {
        var result = TurParser.Parse("Mensch {\n  Name: text\n  Wohnort: text @time\n  Körpergröße: number @history\n}");
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("Mensch {\n  Größe: number @time @time\n}", AttributeTime.CurrentOnly, 2, 23)]
    [InlineData("Mensch {\n  Größe: number @history @history\n}", AttributeTime.History, 2, 26)]
    public void DuplicateAnnotation_IsAWarning_AtSecondOccurrence(string source, AttributeTime time, int line, int column)
    {
        var result = TurParser.Parse(source);

        var attribute = Assert.Single(Assert.Single(result.Ontology.Entities).Attributes);
        Assert.Equal(time, attribute.Time);
        Assert.Single(attribute.Annotations); // kept only once
        AssertWarning(Assert.Single(result.Warnings), TurWarningKind.DuplicateAnnotation, line, column);
    }

    [Theory]
    [InlineData("Mensch {\n  Größe: number @time @history\n}", 2, 17)]
    [InlineData("Mensch {\n  Größe: number @history @time\n}", 2, 26)]
    public void TimeTogetherWithHistory_IsAWarning_AtTheTimeAnnotation(string source, int line, int column)
    {
        var result = TurParser.Parse(source);

        var attribute = Assert.Single(Assert.Single(result.Ontology.Entities).Attributes);
        Assert.Equal(AttributeTime.History, attribute.Time);
        AssertWarning(Assert.Single(result.Warnings), TurWarningKind.RedundantTime, line, column);
    }

    [Fact]
    public void Warnings_DoNotStopParsing_AndAreCollectedInFileOrder()
    {
        var result = TurParser.Parse("Mensch {\n  Wohnort: text @time @time\n  Körpergröße: number @time @history\n}\nTier {\n  Gewicht: number @history @history\n}");

        Assert.Equal(2, result.Ontology.Entities.Count);
        Assert.Collection(result.Warnings,
            w => AssertWarning(w, TurWarningKind.DuplicateAnnotation, 2, 23),
            w => AssertWarning(w, TurWarningKind.RedundantTime, 3, 23),
            w => AssertWarning(w, TurWarningKind.DuplicateAnnotation, 6, 28));
    }

    [Fact]
    public void ErrorAfterAWarning_StillStopsParsing()
    {
        AssertError("Mensch {\n  Größe: number @time @time\n  Name: text @unique\n}", TurErrorKind.UnknownAnnotation, 3, 14);
    }

    [Fact]
    public void WarningMessage_StartsWithLineAndColumn()
    {
        var warning = Assert.Single(TurParser.Parse("Mensch {\n  Größe: number @time @time\n}").Warnings);
        Assert.StartsWith("Line 2, column 23: ", warning.ToString());
    }

    // ---------- Units (recognized, but not supported yet) ----------

    [Theory]
    [InlineData("Mensch {\n  Körpergröße: number in cm\n}", 2, 23)]
    [InlineData("Mensch {\n  Körpergröße: number in cm @time\n}", 2, 23)]
    [InlineData("Wetter {\n  Temperatur: number in °C @time\n}", 2, 22)]
    public void Units_AreRecognized_ButNotYetSupported(string source, int line, int column)
    {
        var error = AssertError(source, TurErrorKind.UnitsNotSupported, line, column);
        Assert.Contains("not supported yet", error.Message);
    }

    // ---------- Messages and files ----------

    [Fact]
    public void ErrorMessage_StartsWithLineAndColumn()
    {
        var error = Assert.Throws<TurSyntaxException>(() => Parse("Mensch {\n  Name: text\n  Name: text\n}"));
        Assert.StartsWith("Line 3, column 3: ", error.Message);
    }

    [Fact]
    public void ParseFile_RejectsOtherExtensions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tursiops-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "Mensch {\n}");
        try
        {
            var error = Assert.Throws<ArgumentException>(() => TurParser.ParseFile(path));
            Assert.Contains(".tur", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- Helpers ----------

    private static Ontology Parse(string source) => TurParser.Parse(source).Ontology;

    private static void AssertWarning(TurWarning warning, TurWarningKind kind, int line, int column)
    {
        Assert.Equal(kind, warning.Kind);
        Assert.Equal(new SourcePosition(line, column), warning.Position);
    }

    private static TurSyntaxException AssertError(string source, TurErrorKind kind, int line, int column)
    {
        var error = Assert.Throws<TurSyntaxException>(() => Parse(source));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(new SourcePosition(line, column), error.Position);
        return error;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tursiops.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Tursiops.slnx not found.");
    }
}
