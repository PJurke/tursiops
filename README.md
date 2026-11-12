# Tursiops

Eine Datenbank, die sich selbst organisiert. Der Admin beschreibt die Welt in Konzepten, Tursiops entscheidet über Strukturen und Optimierung.

Der Name kommt vom Großen Tümmler (*Tursiops truncatus*). Delfine schlafen mit einer Gehirnhälfte, und wie sie soll Tursiops in Ruhephasen über seine eigene Organisation entscheiden.

## Stand

Erster Prototyp in Arbeit. Er zeigt ein einziges Konzept, Zeit: Ein Attribut ist als zeitlich veränderlich beschrieben, Tursiops beobachtet die Daten und baut im Schlaf von „nur aktueller Wert" auf „Wert mit Historie" um, wenn die Daten es nahelegen. Jede Entscheidung steht in einem lesbaren Log.

## Ontologie-Format (v0.1)

Dateien enden auf `.tur`:

```
Mensch {
  Name
  Körpergröße @time
}
```

Beispiel: [`examples/mensch.tur`](examples/mensch.tur)

## Aufbau

| Pfad | Inhalt |
|---|---|
| `src/Tursiops.Core` | Kern: Parser für `.tur`, eigene Strukturbegriffe, Übersetzung nach SQLite, Beobachtung, Schlaf |
| `src/Tursiops.Cli` | Kommandozeile, etwa um den Schlaf auszulösen und das Demo-Szenario abzuspielen |
| `tests/Tursiops.Tests` | Tests (xUnit) |
| `examples` | Beispiel-Ontologien |

## Bauen

Voraussetzung: .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/Tursiops.Cli
```
