using Tursiops.Core.Format;

namespace Tursiops.Tests.Format;

// Tests für den Parser des Ontologie-Formats v0.1 (Grammatik E-17, Vorgehen E-16).
// Fehlertests prüfen Fehlerart, Zeile und Spalte, nicht den Wortlaut der Meldung (E-17).
public class TurParserTests
{
    // ---------- Gültige Dateien ----------

    [Fact]
    public void EmptyFile_HasNoEntities()
    {
        var ontology = TurParser.Parse("");
        Assert.Empty(ontology.Entities);
    }

    [Fact]
    public void OnlyCommentsAndBlankLines_HasNoEntities()
    {
        var ontology = TurParser.Parse("// nur ein Kommentar\n\n   \n// noch einer\n");
        Assert.Empty(ontology.Entities);
    }

    [Fact]
    public void SingleEntity_WithAttributes()
    {
        var ontology = TurParser.Parse("""
            Mensch {
              Name
              Körpergröße @time
            }
            """);

        var mensch = Assert.Single(ontology.Entities);
        Assert.Equal("Mensch", mensch.Name);
        Assert.Collection(mensch.Attributes,
            a => { Assert.Equal("Name", a.Name); Assert.Empty(a.Annotations); Assert.False(a.IsTimed); },
            a => { Assert.Equal("Körpergröße", a.Name); Assert.Equal(["time"], a.Annotations); Assert.True(a.IsTimed); });
    }

    [Fact]
    public void Positions_AreOneBasedLineAndColumn()
    {
        var ontology = TurParser.Parse("// Kopf\nMensch {\n  Name\n}");

        var mensch = Assert.Single(ontology.Entities);
        Assert.Equal(new SourcePosition(2, 1), mensch.Position);
        Assert.Equal(new SourcePosition(3, 3), Assert.Single(mensch.Attributes).Position);
    }

    [Fact]
    public void MultipleEntities_WithBlankLinesAndComments()
    {
        var ontology = TurParser.Parse("""
            // Menschen
            Mensch {
              Name

              // Größe ändert sich
              Körpergröße @time
            }


            Tier {
              Art
            }
            """);

        Assert.Equal(["Mensch", "Tier"], ontology.Entities.Select(e => e.Name));
        Assert.Equal(2, ontology.Entities[0].Attributes.Count);
        Assert.Single(ontology.Entities[1].Attributes);
    }

    [Fact]
    public void NamesMayContainUnicodeLettersDigitsAndUnderscore()
    {
        var ontology = TurParser.Parse("Straße_2 {\n  Ölstand_1\n  Größe3 @time\n}");

        var entity = Assert.Single(ontology.Entities);
        Assert.Equal("Straße_2", entity.Name);
        Assert.Equal(["Ölstand_1", "Größe3"], entity.Attributes.Select(a => a.Name));
    }

    [Fact]
    public void DecomposedUmlauts_AreTreatedLikeComposedOnes()
    {
        // "Größe" einmal aus fertigen Umlauten, einmal aus Buchstabe + Trema zusammengesetzt.
        var decomposed = "Größe";
        var ontology = TurParser.Parse($"Mensch {{\n  {decomposed}\n}}");

        Assert.Equal("Größe", Assert.Single(Assert.Single(ontology.Entities).Attributes).Name);
    }

    [Fact]
    public void WindowsLineEndingsAndTabs_AreAccepted()
    {
        var ontology = TurParser.Parse("Mensch {\r\n\tName\r\n\tKörpergröße\t@time\r\n}\r\n");

        Assert.Equal(2, Assert.Single(ontology.Entities).Attributes.Count);
    }

    [Fact]
    public void NamesAreCaseSensitive()
    {
        var ontology = TurParser.Parse("Mensch {\n  name\n  Name\n}\nmensch {\n}");

        Assert.Equal(2, ontology.Entities.Count);
        Assert.Equal(2, ontology.Entities[0].Attributes.Count);
    }

    [Fact]
    public void OpeningBrace_MayStandOnNextLine()
    {
        var ontology = TurParser.Parse("Mensch\n{\n  Name\n}");

        Assert.Single(Assert.Single(ontology.Entities).Attributes);
    }

    [Fact]
    public void OpeningBrace_MayFollowAfterBlankAndCommentLines()
    {
        var ontology = TurParser.Parse("Mensch // ein Mensch\n\n// gleich kommt die Klammer\n{\n  Name\n}");

        Assert.Single(Assert.Single(ontology.Entities).Attributes);
    }

    [Theory]
    [InlineData("Mensch { }")]
    [InlineData("Mensch {}")]
    [InlineData("Mensch {\n}")]
    [InlineData("Mensch {\n\n  // noch nichts\n}")]
    public void EmptyEntity_IsAllowed(string source)
    {
        var entity = Assert.Single(TurParser.Parse(source).Entities);
        Assert.Empty(entity.Attributes);
    }

    [Fact]
    public void CommentsAtEndOfAnyLine_AreAllowed()
    {
        var ontology = TurParser.Parse("""
            Mensch { // Entität
              Name // Vor- und Nachname
              Körpergröße @time // ändert sich
            } // Ende
            """);

        Assert.Equal(2, Assert.Single(ontology.Entities).Attributes.Count);
    }

    [Fact]
    public void ClosingBrace_MayBeIndented()
    {
        var ontology = TurParser.Parse("Mensch {\n  Name\n  }");
        Assert.Single(ontology.Entities);
    }

    [Fact]
    public void ExampleFile_Parses()
    {
        var ontology = TurParser.ParseFile(Path.Combine(RepositoryRoot(), "examples", "mensch.tur"));

        var mensch = Assert.Single(ontology.Entities);
        Assert.Equal("Mensch", mensch.Name);
        Assert.True(mensch.Attributes.Single(a => a.Name == "Körpergröße").IsTimed);
    }

    // ---------- Fehler: Klammern und Zeilen ----------

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
        AssertError("Mensch {\n  Name\n", TurErrorKind.ExpectedClosingBrace, 3, 1);
    }

    [Fact]
    public void AttributeOnSameLineAsOpeningBrace_IsAnError()
    {
        AssertError("Mensch { Name }", TurErrorKind.ExpectedLineEnd, 1, 10);
    }

    [Fact]
    public void ClosingBraceOnAttributeLine_IsAnError()
    {
        AssertError("Mensch {\n  Name }", TurErrorKind.ExpectedLineEnd, 2, 8);
    }

    [Fact]
    public void TextAfterClosingBrace_IsAnError()
    {
        AssertError("Mensch {\n} Tier {\n}", TurErrorKind.ExpectedLineEnd, 2, 3);
    }

    [Fact]
    public void TwoWordsAsName_IsAnError()
    {
        AssertError("Mensch {\n  Körper Größe\n}", TurErrorKind.ExpectedLineEnd, 2, 10);
    }

    [Fact]
    public void SingleSlash_IsNotAComment()
    {
        AssertError("Mensch {\n  Name / Kommentar\n}", TurErrorKind.ExpectedLineEnd, 2, 8);
    }

    [Fact]
    public void StrayClosingBrace_AtTopLevel()
    {
        AssertError("}", TurErrorKind.ExpectedName, 1, 1);
    }

    // ---------- Fehler: Namen ----------

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
        AssertError("Mensch {\n  Name\n  Name\n}", TurErrorKind.DuplicateAttribute, 3, 3);
    }

    [Fact]
    public void SameAttributeNameInDifferentEntities_IsAllowed()
    {
        var ontology = TurParser.Parse("Mensch {\n  Name\n}\nTier {\n  Name\n}");
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
        var ontology = TurParser.Parse("Inventar {\n  inhalt\n  in_Arbeit\n}");
        Assert.Equal(2, Assert.Single(ontology.Entities).Attributes.Count);
    }

    // ---------- Fehler: Annotationen ----------

    [Fact]
    public void UnknownAnnotation_PointsToAtSign()
    {
        AssertError("Mensch {\n  Name @unique\n}", TurErrorKind.UnknownAnnotation, 2, 8);
    }

    [Fact]
    public void AnnotationWithWrongCase_IsUnknown_AndSuggestsCorrectSpelling()
    {
        var error = AssertError("Mensch {\n  Größe @Time\n}", TurErrorKind.UnknownAnnotation, 2, 9);
        Assert.Contains("@time", error.Message);
    }

    [Fact]
    public void DuplicateAnnotation_PointsToSecondOccurrence()
    {
        AssertError("Mensch {\n  Größe @time @time\n}", TurErrorKind.DuplicateAnnotation, 2, 15);
    }

    [Fact]
    public void AtSignWithoutName_IsAnError()
    {
        AssertError("Mensch {\n  Größe @ time\n}", TurErrorKind.ExpectedName, 2, 10);
    }

    // ---------- Einheiten (erkannt, aber noch nicht unterstützt) ----------

    [Theory]
    [InlineData("Mensch {\n  Körpergröße in cm\n}", 2, 15)]
    [InlineData("Mensch {\n  Körpergröße in cm @time\n}", 2, 15)]
    [InlineData("Wetter {\n  Temperatur in °C @time\n}", 2, 14)]
    public void Units_AreRecognized_ButNotYetSupported(string source, int line, int column)
    {
        var error = AssertError(source, TurErrorKind.UnitsNotSupported, line, column);
        Assert.Contains("not supported yet", error.Message);
    }

    // ---------- Meldungen und Dateien ----------

    [Fact]
    public void ErrorMessage_StartsWithLineAndColumn()
    {
        var error = Assert.Throws<TurSyntaxException>(() => TurParser.Parse("Mensch {\n  Name\n  Name\n}"));
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

    // ---------- Hilfen ----------

    private static TurSyntaxException AssertError(string source, TurErrorKind kind, int line, int column)
    {
        var error = Assert.Throws<TurSyntaxException>(() => TurParser.Parse(source));
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
