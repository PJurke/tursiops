using System.Text.RegularExpressions;

namespace Tursiops.Tests.Docs;

/// <summary>Reads the developer documentation for the documentation tests (E-47). Just enough Markdown for our own files.</summary>
public static partial class Markdown
{
    /// <summary>The repository root: the directory that holds Tursiops.slnx.</summary>
    public static string RepositoryRoot { get; } = FindRoot();

    /// <summary>The README and every Markdown file under docs/, as paths relative to the repository root.</summary>
    public static IReadOnlyList<string> Files { get; } =
        new[] { "README.md" }
            .Concat(Directory.GetFiles(Path.Combine(RepositoryRoot, "docs"), "*.md", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal))
            .ToList();

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    /// <summary>Inline code spans outside fenced blocks, e.g. <c>LayoutRule.Default</c>.</summary>
    public static IEnumerable<string> CodeSpans(string text) =>
        InlineCode().Matches(WithoutFences(text)).Select(m => m.Groups[1].Value);

    /// <summary>The contents of every fenced block with the given language, e.g. <c>mermaid</c>.</summary>
    public static IEnumerable<string> FencedBlocks(string text, string language) =>
        Fence().Matches(text).Where(m => m.Groups[1].Value == language).Select(m => m.Groups[2].Value);

    /// <summary>Node ids in a Mermaid diagram: an identifier directly followed by a node shape, e.g. <c>Database[...]</c>.</summary>
    public static IEnumerable<string> MermaidNodeIds(string diagram) =>
        MermaidNode().Matches(MermaidLabel().Replace(diagram, "")).Select(m => m.Groups[1].Value).Distinct();

    /// <summary>Link targets outside fenced blocks, e.g. <c>storage.md#sleep</c>.</summary>
    public static IEnumerable<string> LinkTargets(string text) =>
        Link().Matches(WithoutFences(text)).Select(m => m.Groups[1].Value);

    /// <summary>The anchors GitHub creates for the headings of a file.</summary>
    public static ISet<string> Anchors(string text) =>
        Heading().Matches(WithoutFences(text)).Select(m => Slug(m.Groups[1].Value)).ToHashSet();

    /// <summary>GitHub's anchor for a heading: lower case, punctuation removed, spaces as hyphens.</summary>
    public static string Slug(string heading)
    {
        var plain = heading.Trim().Replace("`", "").ToLowerInvariant();
        var kept = new string(plain.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray());
        return kept.Replace(' ', '-');
    }

    /// <summary>The rows of the first table whose header starts with the given cell, as cells without the header.</summary>
    public static IReadOnlyList<string[]> Table(string text, string firstHeaderCell, out string[] header)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(l => l.StartsWith('|') && Cells(l).FirstOrDefault() == firstHeaderCell);
        if (start < 0)
            throw new InvalidOperationException($"No table with the header '{firstHeaderCell}'.");
        header = Cells(lines[start]);
        return lines.Skip(start + 2).TakeWhile(l => l.StartsWith('|')).Select(Cells).ToList();
    }

    private static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private static string WithoutFences(string text) => Fence().Replace(text, "");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tursiops.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Tursiops.slnx not found above the test directory.");
    }

    [GeneratedRegex(@"^```(\w*)[^\n]*\n(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"(?<!`)`([^`\n]+)`(?!`)")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\[[^\]]*\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    /// <summary>Quoted labels and edge labels, which may contain any word.</summary>
    [GeneratedRegex(@"""[^""]*""|\|[^|\n]*\|")]
    private static partial Regex MermaidLabel();

    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:\[|\(\(|\(|\{)")]
    private static partial Regex MermaidNode();
}
