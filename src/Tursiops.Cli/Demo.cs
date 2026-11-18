using System.Globalization;
using System.Reflection;
using Tursiops.Core.Format;
using Tursiops.Core.Sleep;
using Tursiops.Core.Storage;
using Tursiops.Core.Storage.Sqlite;

namespace Tursiops.Cli;

/// <summary>
/// The demo of the first prototype (E-36, E-39): one attribute, Mensch.Körpergröße with <c>@history</c>,
/// four waking phases with deliberately shaped use, each followed by a sleep. Values come from a fixed seed,
/// so every run decides the same. Before and after each sleep the demo compares every current value and every
/// history entry (E-25); these checks are not counted as use.
/// </summary>
/// <remarks>
/// The numbers are chosen with the measured SQLite costs (docs/storage.md, "Layout decision") so that the four sleeps
/// keep H2, change to H1, keep H1 although H2 is a little cheaper, and change back to H2.
/// </remarks>
public sealed class Demo(LogPrinter printer, TimeProvider? time = null)
{
    public const int People = 300;
    public const int MeasurementsEach = 5;
    public const int ListsInPhase2 = 200;
    public const int NewMeasurementsInPhase3 = 100;
    public const int LookupsInPhase3 = 200;
    public const int ListsInPhase3 = 20;
    public const int HistoriesInPhase3 = 20;
    public const int HistoriesInPhase4 = 4_000;
    private const int Seed = 18;

    private const string Entity = "Mensch";
    private const string Attribute = "Körpergröße";
    private const string NameAttribute = "Name";
    private static readonly TimeSpan Offset = TimeSpan.FromHours(1);

    private readonly List<LayoutDecision> _decisions = [];
    private readonly List<bool> _checks = [];
    private TextWriter Out => printer.Output;

    /// <summary>The decision of each sleep about Mensch.Körpergröße, in order.</summary>
    public IReadOnlyList<LayoutDecision> Decisions => _decisions;

    /// <summary>For each sleep: did every current value and history entry read the same before and after?</summary>
    public IReadOnlyList<bool> Checks => _checks;

    /// <summary>Plays the demo into a new database file. Returns whether every check passed.</summary>
    public bool Run(string path)
    {
        var ontology = TurParser.Parse(ExampleOntology()).Ontology;
        using var store = SqliteStore.Create(path, StructureModel.FromOntology(ontology), time);
        var random = new Random(Seed);

        Out.WriteLine(printer.Bold("Tursiops demo"));
        Out.WriteLine($"Database: {path}, created from examples/mensch.tur. The demo uses {Entity}.{Attribute} @history:");
        Out.WriteLine($"{Number(People)} people, {MeasurementsEach} height measurements each, four waking phases, a sleep after each.");
        printer.Explanation();

        Phase(1, "load the data",
            $"{Number(People)} names and {Number(People * MeasurementsEach)} heights are written.");
        var (people, heights) = Load(store, random);
        SleepAndCheck(store);

        Phase(2, $"list everyone's current height, {Number(ListsInPhase2)} times",
            "Exaggerated on purpose, so that one layout clearly wins.");
        for (var i = 0; i < ListsInPhase2; i++)
            store.GetCurrentValues(Entity, Attribute);
        SleepAndCheck(store);

        Phase(3, "mixed use",
            $"{Number(NewMeasurementsInPhase3)} new measurements, {Number(LookupsInPhase3)} single lookups, " +
            $"{Number(ListsInPhase3)} lists of everyone, {Number(HistoriesInPhase3)} histories.");
        MixedUse(store, people, heights, random);
        SleepAndCheck(store);

        Phase(4, $"read histories, {Number(HistoriesInPhase4)} times",
            "Exaggerated on purpose: H1 reads histories only a little slower than H2, so it takes many reads to justify a change.");
        for (var i = 0; i < HistoriesInPhase4; i++)
            store.GetHistory(people[i % people.Count], Attribute);
        SleepAndCheck(store);

        Out.WriteLine();
        Out.WriteLine(printer.Bold(
            $"Layouts after each sleep: {string.Join(", ", _decisions.Select(d => LayoutNames.Short(d.Result)))}. " +
            (_checks.All(c => c) ? "Every check passed: no sleep changed any information." : "A check failed, see above.")));
        Out.WriteLine($"Read the log again with: tursiops log {path}");
        printer.Explanation();
        return _checks.All(c => c);
    }

    private void Phase(int number, string title, string description)
    {
        Out.WriteLine();
        Out.WriteLine(printer.Bold($"Waking phase {number}: {title}"));
        Out.WriteLine("  " + description);
        Out.WriteLine();
        Out.WriteLine("Sleep:");
    }

    // Phase 1: every person gets a name and yearly height measurements from 2021 to 2025.
    private static (List<EntityRef> People, List<double> Heights) Load(SqliteStore store, Random random)
    {
        var people = new List<EntityRef>();
        var heights = new List<double>();
        for (var i = 0; i < People; i++)
        {
            var person = store.CreateEntity(Entity);
            store.SetValue(person, NameAttribute, $"Person {i + 1:000}");
            var height = 150.0 + random.Next(0, 450) / 10.0;
            for (var year = 0; year < MeasurementsEach; year++)
            {
                height = Math.Round(height + random.Next(-10, 11) / 10.0, 1);
                store.SetValue(person, Attribute, height, ValueTime.At(MeasuredAt(2021 + year, random)));
            }
            people.Add(person);
            heights.Add(height);
        }
        return (people, heights);
    }

    // Phase 3: new measurements in 2026, single lookups, a few lists and histories.
    // The last heights are kept by the demo itself, so that looking them up is not counted as use.
    private static void MixedUse(SqliteStore store, List<EntityRef> people, List<double> heights, Random random)
    {
        for (var i = 0; i < NewMeasurementsInPhase3; i++)
        {
            heights[i] = Math.Round(heights[i] + random.Next(-10, 11) / 10.0, 1);
            store.SetValue(people[i], Attribute, heights[i], ValueTime.At(MeasuredAt(2026, random)));
        }
        for (var i = 0; i < LookupsInPhase3; i++)
            store.GetCurrentValue(people[random.Next(people.Count)], Attribute);
        for (var i = 0; i < ListsInPhase3; i++)
            store.GetCurrentValues(Entity, Attribute);
        for (var i = 0; i < HistoriesInPhase3; i++)
            store.GetHistory(people[random.Next(people.Count)], Attribute);
    }

    private static DateTimeOffset MeasuredAt(int year, Random random) =>
        new(year, random.Next(1, 13), random.Next(1, 29), 9, 0, 0, Offset);

    private void SleepAndCheck(SqliteStore store)
    {
        var before = Snapshot(store);
        var report = Sleeper.Run(store);
        foreach (var entry in report.Entries)
            printer.Entry(entry);
        var after = Snapshot(store);

        _decisions.Add(report.Decisions.Single(d => d.Entity == Entity && d.Attribute == Attribute));
        var same = before.Current.SequenceEqual(after.Current) && before.History.SequenceEqual(after.History);
        _checks.Add(same);
        Out.WriteLine(same
            ? $"Check: all {Number(after.Current.Count)} current values and {Number(after.History.Count)} history entries " +
              "read the same before and after the sleep."
            : "Check FAILED: values read differently after the sleep.");
    }

    // Every current value and every history entry of the attribute, read without counting it as use.
    private static (List<string> Current, List<string> History) Snapshot(SqliteStore store)
    {
        using var _ = store.Unobserved();
        var current = new List<string>();
        var history = new List<string>();
        foreach (var value in store.GetCurrentValues(Entity, Attribute))
        {
            current.Add(Format(value.Entity.Id, value.Value.Value, value.Value.ValidAt!.Value));
            history.AddRange(store.GetHistory(value.Entity, Attribute).Select(h => Format(value.Entity.Id, h.Value, h.ValidAt)));
        }
        return (current, history);
    }

    private static string Format(long id, object? value, DateTimeOffset validAt) =>
        string.Create(CultureInfo.InvariantCulture, $"{id}|{value}|{validAt:O}");

    private static string ExampleOntology()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("mensch.tur")
            ?? throw new InvalidOperationException("The example ontology is missing from the program.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
