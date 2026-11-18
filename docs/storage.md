# Storage in SQLite

How the translation layer in `src/Tursiops.Core/Storage/Sqlite` lays out a Tursiops database in SQLite. The reasons behind it are recorded in the project's decision log (E-06, E-21, E-23, E-25 to E-35, E-37, E-38, E-40, E-41).

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
| `_tursiops_structure` | `entity`, `attribute`, `position`, `type`, `time`, `layout` | Per attribute: its declared base type (`number`, `text`, `date`, `bi`), its time behavior (`Timeless`, `CurrentOnly`, `History`) and its storage layout |
| `_tursiops_phases` | `phase`, `started_at`, `started_offset` | Waking phases, from one sleep to the next, and when each began (see [Observation](#observation)) |
| `_tursiops_observations` | `phase`, `entity`, `attribute`, `counter`, `count` | Per phase and attribute: how often each counter was hit (see [Observation](#observation)) |
| `_tursiops_sleep_log` | `entry`, `phase`, `logged_at`, `logged_offset`, `kind`, `entity`, `attribute`, `summary`, `details` | Every decision of the sleep in readable English, per evaluated phase (see [Sleep](#sleep)) |

A change of layout changes tables and catalog in one transaction. SQLite rolls back schema changes together with the transaction (covered by tests).

Files from before declared types (decision log E-41) have no column `type` in `_tursiops_structure`. Opening them fails with a message that asks to declare the types in the ontology and create the database again; Tursiops does not guess types from the stored values.

## Entity tables

One table per entity, named like the entity. `_id INTEGER PRIMARY KEY` is the internal key: an increasing integer handed out when the entity is created. It is not meant to be visible to applications.

Value columns have no declared type in SQLite; Tursiops checks each value against the attribute's base type before writing (`ValueTypes.Check`, decision log E-41). Numbers (any .NET number type) fit `number`, `string` fits `text`, `DateTimeOffset`, `DateTime` and `DateOnly` fit `date`, `bool` fits `bi`. A value of another type is rejected, not converted, and the failed operation is not counted. `null` is rejected only for `bi`. Other .NET types are always rejected.

SQLite keeps the type of each value as it was written, but returns booleans as numbers and dates as text. Reading returns what SQLite returns.

## Layouts

| Layout | For | Stored as |
|---|---|---|
| `Column` | no annotation | Column `<Attribute>` in the entity table. Writing replaces the value. |
| `ColumnWithTime` | `@time` | Columns `<Attribute>`, `<Attribute>._valid_at` and `<Attribute>._valid_offset` in the entity table. A write only replaces the value if its valid time is not older than the stored one. |
| `HistoryTable` (H2) | `@history` | History table `<Entity>.<Attribute>` (see below). Every write adds a row. The current value is the row with the latest valid time. Initial layout for `@history` (decision log E-38). |
| `CurrentColumnAndHistoryTable` (H1) | `@history` | Columns as in `ColumnWithTime` for the value with the latest valid time, older values in the history table. A write that is not older than the current value moves the current value into the history table and takes its place. An older value that arrives late goes straight into the history table. |

The history table `<Entity>.<Attribute>` has the columns `_entity` (references `_id`), `<Attribute>`, `_valid_at` and `_valid_offset`, plus index `<Entity>.<Attribute>._by_time` on `(_entity, _valid_at)`.

Among values with the same valid time, the one written last is the current one, in H1 and H2 alike. In the history table, rows with the same valid time keep their write order (`rowid`).

## Reading current values of all entities

`SqliteStore.GetCurrentValues` reads the current value of one attribute for all entities that have one, ordered by `_id`, i.e. in the order the entities were created (E-33). Each value is the one `GetCurrentValue` returns for that entity. Entities without a value are left out; for an attribute with `@time` or `@history`, a `null` set with a valid time is a value and is included.

| Layout | Read |
|---|---|
| `Column`, `ColumnWithTime`, H1 | the entity table, rows where the value (or its valid time) is set |
| H2 | per entity, the history row with the latest valid time (the last written among equal times), found through the index on `(_entity, _valid_at)` |

This is the operation in which H1 is faster than H2 (see [`concepts/time.md`](concepts/time.md)).

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
| `read_current_all` | the current values of all entities are read; counted once per read, not per entity |
| `read_history` | the history of one entity is read |

Creating an entity is not counted, nor is an operation that fails, e.g. a value of another type than declared. Changing the layout does not change any counter. Operations inside a scope opened with `SqliteStore.Unobserved()` are not counted either; it is meant for checks that are not use of the data, such as the demo comparing all values before and after each sleep (decision log E-40).

Counts belong to a waking phase, from one sleep to the next. The first phase is 1. `StartNewPhase` ends the current phase and starts the next one at zero; earlier phases are kept.

Counts are kept in memory and written to the file in one transaction when the store is closed and by `FlushObservations` (called before each sleep). If the program crashes, counts since the last write are lost; the stored information is not affected (decision log E-32).

## Sleep

`Sleeper.Run` (in `src/Tursiops.Core/Sleep`) is triggered by hand, e.g. with `tursiops sleep <database>` (decision log E-04). It

1. writes the counts kept in memory to the file,
2. reads the counts of the current waking phase and, from the tables, the number of entities and of stored values per attribute,
3. decides the layout of every attribute with `@history` (E-34, below),
4. changes the layouts, writes the log and starts the next waking phase, all in one transaction (`SqliteStore.InOneTransaction`). If a step fails, file and store stay as before.

### Layout decision

For each layout, the sleep estimates what the evaluated phase would have cost: per counted operation its number times its cost, for reading all entities also times the number of entities. Late writes are counted among writes and cost the same.

The layout changes only if another layout saves more than the cost of changing (per stored value) plus 20 % of the cost of the current layout. With fewer than 100 operations in the phase (writes and reads, late writes not counted twice) nothing is decided. Both thresholds are set in `LayoutRule.Default`.

Costs depend on the target system. For SQLite they are in `SqliteCosts.Measured`: the midpoints of the measured ranges in [`concepts/time.md`](concepts/time.md), in microseconds. Where the measurement found no consistent difference, both layouts get the same cost.

| Operation | H2 | H1 |
|---|---|---|
| Write | 1,200 | 1,350 |
| Read current, one entity | 15 | 15 |
| Read current of all entities, per entity | 0.935 | 0.63 |
| Read history, one entity | 31 | 39.5 |
| Change layout, per value | 0.55 | 0.55 |

With these numbers, H1 only wins when current values of all entities are read often. H2 wins back only when histories are read often: H1 writes about 12 % slower, less than the required 20 %, so writing alone does not change H1 back to H2.

### Log

`_tursiops_sleep_log` keeps one row per entry, in the order written (`entry`). `phase` is the waking phase the sleep evaluated, `logged_at` and `logged_offset` when the entry was written (stored like valid times). `entity` and `attribute` are empty for entries about the whole sleep. `summary` says in plain words what Tursiops did and what it means for the developer; `details` gives the reason in one sentence, then the numbers it rests on, and may be empty (decision log E-37). `kind` is one of:

| Kind | Entry |
|---|---|
| `sleep` | which phase was evaluated, how many layouts changed and stayed |
| `not_enough_data` | fewer operations than needed, no layout decision |
| `layout_kept` | the numbers of the decision, the layout stays |
| `layout_changed` | the numbers of the decision, the layout changed |

`SqliteStore.GetSleepLog` reads the log, `tursiops log <database>` prints it: the summary in bold when the output is a terminal (not when redirected or with `NO_COLOR` set), the details below. Both `sleep` and `log` end with a link to [`how-tursiops-decides.md`](how-tursiops-decides.md), which explains the principles for developers.

For a layout decision, the reason names the counted operation where the two layouts differ most in the direction of the saving (`LayoutDecision.Driver`).

## Valid times

- `_valid_at` holds the valid time in UTC as fixed-width text (`yyyy-MM-ddTHH:mm:ss.fffffffZ`), so text order equals time order.
- `_valid_offset` holds the original UTC offset in minutes, so the time is read back exactly as it was given.
- Only the valid time is stored, not the time of writing.

## Access

SQLite is accessed through `Microsoft.Data.Sqlite`, without an ORM.
