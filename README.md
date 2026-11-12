# Tursiops

A database that organizes itself. The admin describes the world in concepts; Tursiops decides on structures and optimization.

The name comes from the common bottlenose dolphin (*Tursiops truncatus*). Dolphins sleep with one half of their brain at a time, and like them, Tursiops is meant to decide on its own organization during rest phases.

## Status

First prototype in progress. It demonstrates a single concept, time: an attribute is described as changing over time, Tursiops observes the data and, during sleep, rebuilds it from "current value only" to "value with history" when the data suggests it. Every decision is recorded in a readable log.

## Ontology format (v0.1)

Files end with `.tur`:

```
Mensch {
  Name
  Körpergröße @time
}
```

Entity and attribute names are free to choose, in any language; keywords and annotations such as `@time` are English.

Example: [`examples/mensch.tur`](examples/mensch.tur)

## Layout

| Path | Contents |
|---|---|
| `src/Tursiops.Core` | Core: parser for `.tur`, Tursiops' own structure model, translation to SQLite, observation, sleep |
| `src/Tursiops.Cli` | Command line, e.g. to trigger sleep and play the demo scenario |
| `tests/Tursiops.Tests` | Tests (xUnit) |
| `examples` | Example ontologies |

## Build

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/Tursiops.Cli
```
