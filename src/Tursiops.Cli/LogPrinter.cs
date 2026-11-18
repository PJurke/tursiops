using Tursiops.Core.Sleep;

namespace Tursiops.Cli;

/// <summary>
/// Prints sleep log entries (E-37): the summary first, in bold when the output is a terminal, then the details.
/// The stored entries stay plain text; bold is only for display.
/// </summary>
public sealed class LogPrinter(TextWriter output, bool bold)
{
    /// <summary>Where the principles behind the log are explained (E-37).</summary>
    public const string ExplanationUrl = "https://github.com/PJurke/tursiops/blob/main/docs/how-tursiops-decides.md";

    public TextWriter Output => output;

    /// <summary>Bold only in a terminal and when NO_COLOR is not set, so redirected output has no control characters.</summary>
    public static LogPrinter ForConsole() =>
        new(Console.Out, !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")));

    public void Entry(SleepLogEntry entry)
    {
        output.WriteLine(Bold(entry.Summary));
        if (entry.Details.Length > 0)
            output.WriteLine("  " + entry.Details);
    }

    public void Explanation() => output.WriteLine($"How Tursiops decides: {ExplanationUrl}");

    public string Bold(string text) => bold ? $"\u001b[1m{text}\u001b[0m" : text;
}
