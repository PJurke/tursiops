# Tursiops

A database that organizes itself. The admin describes the world in concepts; Tursiops decides on structures and optimization.

The name comes from the common bottlenose dolphin (*Tursiops truncatus*). Dolphins sleep with one half of their brain at a time, and like them, Tursiops is meant to decide on its own organization during rest phases.

## Status

First prototype in progress. It demonstrates a single concept, time. The admin decides what information exists: whether an attribute is timeless, has a current state over time (`@time`) or keeps its full history (`@history`). Tursiops decides how to store it, observes how the data is used and, during sleep, rebuilds the storage when the data suggests it, without ever changing what information is kept. Every decision is recorded in a readable log.

## Ontology format (v0.1)

Files end with `.tur`:

```
Mensch {
  Name                    // timeless: only the last value written counts
  Wohnort @time           // only the current state counts, by the time it was valid
  Körpergröße @history    // every value is kept with the time it was valid
}
```

Entity and attribute names are free to choose, in any language; keywords and annotations such as `@time` are English.

Redundant annotations such as `@time @time`, or `@time` next to `@history`, are reported as warnings and do not stop parsing.

Example: [`examples/mensch.tur`](examples/mensch.tur)

Full specification: [`docs/format.md`](docs/format.md)

## Layout

| Path | Contents |
|---|---|
| `src/Tursiops.Core` | Core: parser for `.tur`, Tursiops' own structure model, translation to SQLite, observation, sleep |
| `src/Tursiops.Cli` | Command line, e.g. to trigger sleep and play the demo scenario |
| `tests/Tursiops.Tests` | Tests (xUnit) |
| `tools/Tursiops.Bench` | Benchmark of storage layouts on SQLite (results in `docs/concepts/time.md`) |
| `examples` | Example ontologies |
| `docs` | Developer documentation: how Tursiops decides, format specification, storage layout, known methods per concept (`docs/concepts`) |

## Build

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/Tursiops.Cli
```

Commands:

```
tursiops demo <database>    Play the demo: create the database, four waking phases, a sleep after each.
tursiops sleep <database>   Evaluate the waking phase, decide on layouts, log every decision.
tursiops log <database>     Show the sleep log.
```

To see Tursiops at work, start with the demo:

```
dotnet run --project src/Tursiops.Cli -- demo demo.db
```

How Tursiops decides and how to read its log: [`docs/how-tursiops-decides.md`](docs/how-tursiops-decides.md). The exact rules: [`docs/storage.md`](docs/storage.md#sleep).

Benchmark of the `@history` layouts:

```
dotnet run -c Release --project tools/Tursiops.Bench
```
