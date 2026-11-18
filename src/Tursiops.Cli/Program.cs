using System.Globalization;
using Microsoft.Data.Sqlite;
using Tursiops.Core.Sleep;
using Tursiops.Core.Storage.Sqlite;

// Command line of Tursiops. Sleep is triggered by hand (E-04), its log is kept in the database file (E-35).
// The demo scenario (E-02) is added with card 10.

const string Usage = """
    Usage:
      tursiops sleep <database>   Evaluate the waking phase, decide on layouts, log every decision.
      tursiops log <database>     Show the sleep log.
    """;

if (args.Length != 2 || args[0] is not ("sleep" or "log"))
{
    Console.Error.WriteLine(Usage);
    return 1;
}

var path = args[1];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"'{path}' does not exist.");
    return 1;
}

try
{
    using var store = SqliteStore.Open(path);
    if (args[0] == "sleep")
    {
        var report = Sleeper.Run(store);
        foreach (var entry in report.Entries)
            Console.WriteLine(entry.Message);
        Console.WriteLine($"Waking phase {report.NextPhase} has begun.");
    }
    else
    {
        var log = store.GetSleepLog();
        if (log.Count == 0)
            Console.WriteLine("The database has not slept yet.");
        foreach (var phase in log.GroupBy(r => r.Phase))
        {
            Console.WriteLine($"[waking phase {phase.Key}, slept {phase.First().LoggedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}]");
            foreach (var record in phase)
                Console.WriteLine(record.Entry.Message);
            Console.WriteLine();
        }
    }
    return 0;
}
catch (Exception e) when (e is InvalidOperationException or SqliteException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
