using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Tursiops.Core.Format;
using Tursiops.Core.Sleep;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Docs;

// The developer documentation stays true to the code (E-47). Each fact has one place in the docs; where the docs state
// something the code also states, these tests compare the two, so a change in one place cannot go unnoticed.
public class DocumentationTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // How the docs name the layouts in tables (decision log E-46 renames them with card 16).
    private static readonly Dictionary<string, AttributeLayout> LayoutColumns = new()
    {
        ["H2"] = AttributeLayout.HistoryTable,
        ["H1"] = AttributeLayout.CurrentColumnAndHistoryTable,
    };

    // Code spans that look like code names but are not: each with the reason.
    private static readonly Dictionary<string, string> NotCodeNames = new()
    {
        ["Mensch"] = "entity name in examples/mensch.tur",
        ["Number"] = "misspelled type in an error example",
        ["Entity.Attribute"] = "placeholder for names from the ontology",
        ["Microsoft.Data.Sqlite"] = "NuGet package",
        ["ChangedAt"] = "column name in Anchor Modeling, a source in the method catalog",
    };

    // ---- Numbers: the cost table and the thresholds stand in storage.md only, and match the code.

    [Fact]
    public void CostTableInStorage_MatchesSqliteCosts()
    {
        var rows = Markdown.Table(Markdown.Read("docs/storage.md"), "Operation", out var header);
        var model = SqliteCosts.Measured;

        var columns = header.Skip(1).Select(name => LayoutColumns.TryGetValue(name, out var layout)
            ? layout
            : throw new Xunit.Sdk.XunitException($"docs/storage.md: unknown layout column '{name}'.")).ToList();
        Assert.Equal(model.Layouts.Keys.Order(), columns.Order());

        var expected = new Dictionary<string, Func<AttributeLayout, double>>
        {
            ["Write"] = l => model.Layouts[l].Write,
            ["Read current, one entity"] = l => model.Layouts[l].CurrentRead,
            ["Read current of all entities, per entity"] = l => model.Layouts[l].AllCurrentReadPerEntity,
            ["Read history, one entity"] = l => model.Layouts[l].HistoryRead,
            ["Change layout, per value"] = _ => model.ChangePerValue,
        };
        Assert.Equal(expected.Keys.Order(), rows.Select(r => r[0]).Order());

        foreach (var row in rows)
            for (var i = 0; i < columns.Count; i++)
                Assert.True(expected[row[0]](columns[i]) == Number(row[i + 1]),
                    $"docs/storage.md: '{row[0]}' in {header[i + 1]} says {row[i + 1]}, " +
                    $"SqliteCosts.Measured says {expected[row[0]](columns[i]).ToString(Invariant)}.");
    }

    [Fact]
    public void ThresholdsInStorage_MatchLayoutRule()
    {
        var text = Markdown.Read("docs/storage.md");
        var rule = LayoutRule.Default;

        var share = Regex.Match(text, @"plus ([\d.]+) % of the cost of the current layout");
        var operations = Regex.Match(text, @"fewer than ([\d,]+) operations");

        Assert.True(share.Success, "docs/storage.md: the minimum saving share is not stated.");
        Assert.True(operations.Success, "docs/storage.md: the minimum number of operations is not stated.");
        Assert.Equal(rule.MinSavingShare * 100, Number(share.Groups[1].Value), precision: 9);
        Assert.Equal(rule.MinOperations, (long)Number(operations.Groups[1].Value));
    }

    [Fact]
    public void ExampleInHowTursiopsDecides_IsWhatTheSleepComputes()
    {
        var text = Markdown.Read("docs/how-tursiops-decides.md");
        var phase = Regex.Match(text, @"after a phase with ([\d,]+) reads of the current heights of all ([\d,]+) people");
        var values = Regex.Match(text, @"\| ([\d,]+) values ×");
        Assert.True(phase.Success && values.Success, "docs/how-tursiops-decides.md: the example's phase is not stated.");

        var reads = (long)Number(phase.Groups[1].Value);
        var entities = (long)Number(phase.Groups[2].Value);
        var stored = (long)Number(values.Groups[1].Value);
        var h2 = AttributeLayout.HistoryTable;
        var h1 = AttributeLayout.CurrentColumnAndHistoryTable;
        var costs = SqliteCosts.Measured;
        var rule = LayoutRule.Default;
        var decision = LayoutDecider.Decide(new AttributeStructure("Körpergröße", BaseType.Number, AttributeTime.History, h2),
            "Mensch", AttributeObservations.None with { AllCurrentReads = reads }, entities, stored, costs, rule);

        Assert.Equal(LayoutOutcome.Changed, decision.Outcome);
        Assert.Equal(h1, decision.Best);
        var lines = new[]
        {
            $"| H2 | {Count(reads)} × {Count(entities)} × {Us(costs.Layouts[h2].AllCurrentReadPerEntity)} µs = {Ms(decision.Estimates[h2])} |",
            $"| H1 | {Count(reads)} × {Count(entities)} × {Us(costs.Layouts[h1].AllCurrentReadPerEntity)} µs = {Ms(decision.Estimates[h1])} |",
            $"| Cost of the change | {Count(stored)} values × {Us(costs.ChangePerValue)} µs = {Ms(decision.ChangeCost)} |",
            $"| Required saving | {Ms(decision.ChangeCost)} + {Us(rule.MinSavingShare * 100)} % of {Ms(decision.Estimates[h2])} = {Ms(decision.Required)} |",
            $"| Saving of H1 | {Ms(decision.Estimates[h2])} − {Ms(decision.Estimates[h1])} = {Ms(decision.Saving)}, " +
            $"more than {Ms(decision.Required)}: the layout changes to H1 |",
        };
        foreach (var line in lines)
            Assert.True(text.Contains(line), $"docs/how-tursiops-decides.md: expected the line\n{line}");
    }

    // ---- Names: every code name the docs mention exists.

    public static TheoryData<string> Files() => new(Markdown.Files);

    [Theory]
    [MemberData(nameof(Files))]
    public void CodeNamesInText_Exist(string file)
    {
        var unknown = Markdown.CodeSpans(Markdown.Read(file))
            .Where(LooksLikeCodeName).Distinct().Where(name => !CodeNameExists(name)).ToList();

        Assert.True(unknown.Count == 0, $"{file}: no such type, member or file: {string.Join(", ", unknown)}");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void NodesInDiagrams_AreTypes(string file)
    {
        // Node ids in lower case stand for something that is not a type, e.g. the database file.
        var unknown = Markdown.FencedBlocks(Markdown.Read(file), "mermaid")
            .SelectMany(Markdown.MermaidNodeIds)
            .Where(id => char.IsUpper(id[0]) && !Code.Types.ContainsKey(id)).ToList();

        Assert.True(unknown.Count == 0, $"{file}: diagram nodes without a type: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void NodeIds_AreFoundInADiagram()
    {
        const string diagram = """
            flowchart TD
              Database["Database: checks, counts (Phase)"] --> Sleeper
              Sleeper(Sleep) -->|uses Costs| file[(SQLite file)]
            """;

        Assert.Equal(["Database", "Sleeper", "file"], Markdown.MermaidNodeIds(diagram).Order(StringComparer.Ordinal));
    }

    // ---- Links: every relative link leads to a file or directory, and to a heading if it names one.

    [Theory]
    [MemberData(nameof(Files))]
    public void RelativeLinks_Resolve(string file)
    {
        var text = Markdown.Read(file);
        var directory = Path.GetDirectoryName(Path.Combine(Markdown.RepositoryRoot, file))!;
        var broken = new List<string>();

        foreach (var target in Markdown.LinkTargets(text).Where(t => !t.Contains("://") && !t.StartsWith("mailto:")))
        {
            var parts = target.Split('#', 2);
            var path = parts[0].Length == 0 ? Path.Combine(Markdown.RepositoryRoot, file) : Path.GetFullPath(Path.Combine(directory, parts[0]));
            if (!File.Exists(path) && !Directory.Exists(path))
                broken.Add(target);
            else if (parts.Length == 2 && path.EndsWith(".md") && !Markdown.Anchors(File.ReadAllText(path)).Contains(parts[1]))
                broken.Add(target);
        }

        Assert.True(broken.Count == 0, $"{file}: broken links: {string.Join(", ", broken)}");
    }

    [Theory]
    [InlineData("History table only (H2)", "history-table-only-h2")]
    [InlineData("Layout decision", "layout-decision")]
    [InlineData("Axis: where the current value lives", "axis-where-the-current-value-lives")]
    public void Slug_IsGitHubsAnchor(string heading, string anchor) => Assert.Equal(anchor, Markdown.Slug(heading));

    // ---- Helpers

    private static bool LooksLikeCodeName(string span) =>
        Regex.IsMatch(span, @"^[A-Z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*(\(\))?$") || span.EndsWith(".cs");

    private static bool CodeNameExists(string span)
    {
        if (NotCodeNames.ContainsKey(span))
            return true;
        if (span.EndsWith(".cs"))
            return Directory.GetFiles(Markdown.RepositoryRoot, span, SearchOption.AllDirectories).Length > 0;

        var parts = span.TrimEnd('(', ')').Split('.');
        if (parts.Length == 1)
            return Code.Types.ContainsKey(parts[0]) || Code.Members.Contains(parts[0])
                || typeof(object).Assembly.GetType("System." + parts[0]) != null;
        return Code.Types.TryGetValue(parts[0], out var type) && parts.Skip(1).All(member =>
        {
            var found = type.GetMember(member, Code.All).FirstOrDefault();
            type = found as Type ?? type;
            return found != null;
        });
    }

    private static double Number(string text) => double.Parse(text, NumberStyles.Number, Invariant);

    private static string Count(long n) => n.ToString("N0", Invariant);

    private static string Us(double micros) => micros.ToString("0.###", Invariant);

    private static string Ms(double micros) => (micros / 1000).ToString("N1", Invariant) + " ms";

    /// <summary>Types and member names of Tursiops' own assemblies.</summary>
    private static class Code
    {
        public const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly Type[] AllTypes =
            new[] { typeof(LayoutRule).Assembly, typeof(Tursiops.Cli.Demo).Assembly }
                .SelectMany(a => a.GetTypes())
                .Where(t => !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
                .ToArray();

        public static Dictionary<string, Type> Types { get; } =
            AllTypes.GroupBy(t => t.Name.Split('`')[0]).ToDictionary(g => g.Key, g => g.First());

        public static HashSet<string> Members { get; } =
            AllTypes.SelectMany(t => t.GetMembers(All)).Select(m => m.Name).ToHashSet();
    }
}
