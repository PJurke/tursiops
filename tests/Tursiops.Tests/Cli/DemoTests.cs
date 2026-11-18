using Microsoft.Data.Sqlite;
using Tursiops.Cli;
using Tursiops.Core.Sleep;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Tests.Cli;

// The demo of the first prototype (E-36, E-39) and how the command line prints the log (E-37).
public class DemoTests : IDisposable
{
    private const AttributeLayout H1 = AttributeLayout.CurrentColumnAndHistoryTable;
    private const AttributeLayout H2 = AttributeLayout.HistoryTable;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tursiops-demo-{Guid.NewGuid():N}.db");

    [Fact]
    public void Demo_KeepsH2_ChangesToH1_KeepsH1_ChangesBackToH2_WithoutChangingInformation()
    {
        var output = new StringWriter();
        var demo = new Demo(new LogPrinter(output, bold: false));

        Assert.True(demo.Run(_path));

        Assert.Equal(
            [LayoutOutcome.Kept, LayoutOutcome.Changed, LayoutOutcome.Kept, LayoutOutcome.Changed],
            demo.Decisions.Select(d => d.Outcome));
        Assert.Equal([H2, H1, H1, H2], demo.Decisions.Select(d => d.Result));
        Assert.True(demo.Decisions[0].Saving < 0); // phase 1: H2 is already cheaper
        Assert.True(demo.Decisions[2].Saving > 0); // phase 3: H2 would be cheaper, but not by enough
        Assert.Equal(4, demo.Checks.Count);
        Assert.All(demo.Checks, Assert.True);
        Assert.DoesNotContain('\u001b', output.ToString());

        using var store = SqliteStore.Open(_path);
        Assert.Equal([1L, 2L, 3L, 4L], store.GetSleepLog().Select(r => r.Phase).Distinct());
        Assert.Equal(H2, store.Structure.Entity("Mensch").Attribute("Körpergröße").Layout);
    }

    [Fact]
    public void ChecksBeforeAndAfterEachSleep_AreNotCountedAsUse()
    {
        var demo = new Demo(new LogPrinter(TextWriter.Null, bold: false));
        demo.Run(_path);

        var observed = demo.Decisions.Select(d => d.Observed).ToList();
        Assert.Equal(Demo.People * Demo.MeasurementsEach, observed[0].Writes);
        Assert.Equal(0, observed[0].CurrentReads + observed[0].AllCurrentReads + observed[0].HistoryReads);
        Assert.Equal(Demo.ListsInPhase2, observed[1].AllCurrentReads);
        Assert.Equal(0, observed[1].Writes + observed[1].CurrentReads + observed[1].HistoryReads);
        Assert.Equal(Demo.NewMeasurementsInPhase3, observed[2].Writes);
        Assert.Equal(Demo.LookupsInPhase3, observed[2].CurrentReads);
        Assert.Equal(Demo.ListsInPhase3, observed[2].AllCurrentReads);
        Assert.Equal(Demo.HistoriesInPhase3, observed[2].HistoryReads);
        Assert.Equal(Demo.HistoriesInPhase4, observed[3].HistoryReads);
    }

    [Fact] // E-37
    public void Printer_MakesOnlyTheSummaryBold()
    {
        var output = new StringWriter();
        new LogPrinter(output, bold: true).Entry(new SleepLogEntry(SleepLogKind.Sleep, null, null, "Summary.", "Details."));

        Assert.Equal($"\u001b[1mSummary.\u001b[0m{Environment.NewLine}  Details.{Environment.NewLine}", output.ToString());
    }

    [Fact] // E-37
    public void Printer_WithoutBold_PrintsPlainText()
    {
        var output = new StringWriter();
        new LogPrinter(output, bold: false).Entry(new SleepLogEntry(SleepLogKind.Sleep, null, null, "Summary."));

        Assert.Equal($"Summary.{Environment.NewLine}", output.ToString());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
