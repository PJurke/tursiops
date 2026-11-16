# Storage in SQLite

How the translation layer in `src/Tursiops.Core/Storage/Sqlite` lays out a Tursiops database in SQLite. The reasons behind it are recorded in the project's decision log (E-06, E-13, E-21, E-23, E-25 to E-30).

Only the translation layer knows tables and SQL. Everything else works with entities, attributes and storage layouts. Which layouts exist for which attribute is described in `StructureModel.cs`.

## Names

- Tables and columns carry the names from the ontology, e.g. `Mensch` and `Körpergröße`, so a person opening the file sees what is where.
- Every name is quoted in SQL. Quoted names with umlauts and dots work in SQLite (covered by tests).
- Names Tursiops adds itself can never collide with ontology names, because ontology names start with a letter and contain no dots:
  - Tursiops' own tables start with `_tursiops_`.
  - Tursiops' own columns start with `_` (e.g. `_id`) or contain a dot (e.g. `Wohnort._valid_at`).
  - History tables are named `Entity.Attribute`, e.g. `Mensch.Körpergröße`.

## Catalog

The structure model is stored in the same file, so a database stays a single file:

| Table | Columns | Contents |
|---|---|---|
| `_tursiops_entities` | `entity`, `position` | Entities in ontology order |
| `_tursiops_structure` | `entity`, `attribute`, `position`, `time`, `layout` | Per attribute: its time behavior (`Timeless`, `CurrentOnly`, `History`) and its storage layout |
| `_tursiops_phases` | `phase`, `started_at`, `started_offset` | Waking phases, from one sleep to the next, and when each began (see [Observation](#observation)) |
| `_tursiops_observations` | `phase`, `entity`, `attribute`, `counter`, `count` | Per phase and attribute: how often each counter was hit (see [Observation](#observation)) |

A change of layout changes tables and catalog in one transaction. SQLite rolls back schema changes together with the transaction (covered by tests).

## Entity tables

One table per entity, named like the entity. `_id INTEGER PRIMARY KEY` is the internal key: an increasing integer handed out when the entity is created. It is not meant to be visible to applications.

Value columns have no declared type. SQLite keeps the type of each value as it was written. Accepted values are numbers, text, dates (`DateTimeOffset`, `DateTime`, `DateOnly`) and booleans; other types are rejected. SQLite returns booleans as numbers and dates as text, so the technical type of a value is only known when it comes in (see [Observation](#observation)).

## Layouts

| Layout | For | Stored as |
|---|---|---|
| `Column` | no annotation | Column `<Attribute>` in the entity table. Writing replaces the value. |
| `ColumnWithTime` | `@time` | Columns `<Attribute>`, `<Attribute>._valid_at` and `<Attribute>._valid_offset` in the entity table. A write only replaces the value if its valid time is not older than the stored one. |
| `HistoryTable` (H2) | `@history` | History table `<Entity>.<Attribute>` (see below). Every write adds a row. The current value is the row with the latest valid time. Initial layout for `@history`. |
| `CurrentColumnAndHistoryTable` (H1) | `@history` | Columns as in `ColumnWithTime` for the value with the latest valid time, older values in the history table. A write that is not older than the current value moves the current value into the history table and takes its place. An older value that arrives late goes straight into the history table. |

The history table `<Entity>.<Attribute>` has the columns `_entity` (references `_id`), `<Attribute>`, `_valid_at` and `_valid_offset`, plus index `<Entity>.<Attribute>._by_time` on `(_entity, _valid_at)`.

Among values with the same valid time, the one written last is the current one, in H1 and H2 alike. In the history table, rows with the same valid time keep their write order (`rowid`).

## Changing the layout

`SqliteStore.ChangeLayout` changes the layout of one attribute. Tables and catalog change in one transaction. Every value, its valid time and the current value read the same before and after (E-25).

| From | To | Steps |
|---|---|---|
| H2 | H1 | Add the three columns to the entity table, copy the latest row per entity into them, delete that row from the history table. |
| H1 | H2 | Insert the current values into the history table, drop the three columns. The inserted rows get the highest `rowid`, so they stay the ones written last among equal valid times. |

Known methods for time-dependent attributes and their status: [`concepts/time.md`](concepts/time.md).

## Observation

Tursiops counts per attribute how it is used, without changing anything. The sleep reads these counts to choose layouts and names them in its log (decision log E-04, E-31).

| Counter | Counted when |
|---|---|
| `write` | a value is set |
| `late_write` | a value is set that is older than the current value (only with `@time` or `@history`; the same valid time is not older) |
| `read_current` | the current value of one entity is read |
| `read_history` | the history of one entity is read |
| `type_number`, `type_text`, `type_date`, `type_boolean` | a value of that technical type is set; `null` has no type. Text stays text, even if it looks like a number |

Creating an entity is not counted, nor is an operation that fails. Changing the layout does not change any counter.

Counts belong to a waking phase, from one sleep to the next. The first phase is 1. `StartNewPhase` ends the current phase and starts the next one at zero; earlier phases are kept.

Counts are kept in memory and written to the file in one transaction when the store is closed and by `FlushObservations` (called before each sleep). If the program crashes, counts since the last write are lost; the stored information is not affected (decision log E-32).

## Valid times

- `_valid_at` holds the valid time in UTC as fixed-width text (`yyyy-MM-ddTHH:mm:ss.fffffffZ`), so text order equals time order.
- `_valid_offset` holds the original UTC offset in minutes, so the time is read back exactly as it was given.
- Only the valid time is stored, not the time of writing.

## Access

SQLite is accessed through `Microsoft.Data.Sqlite`, without an ORM.
