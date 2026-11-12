# Storage in SQLite

How the translation layer in `src/Tursiops.Core/Storage/Sqlite` lays out a Tursiops database in SQLite. The reasons behind it are recorded in the project's decision log (E-06, E-13, E-21, E-23, E-25 to E-28).

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

A change of layout changes tables and catalog in one transaction. SQLite rolls back schema changes together with the transaction (covered by tests).

## Entity tables

One table per entity, named like the entity. `_id INTEGER PRIMARY KEY` is the internal key: an increasing integer handed out when the entity is created. It is not meant to be visible to applications.

Value columns have no declared type. SQLite keeps the type of each value as it was written.

## Layouts

| Layout | For | Stored as |
|---|---|---|
| `Column` | no annotation | Column `<Attribute>` in the entity table. Writing replaces the value. |
| `ColumnWithTime` | `@time` | Columns `<Attribute>`, `<Attribute>._valid_at` and `<Attribute>._valid_offset` in the entity table. A write only replaces the value if its valid time is not older than the stored one. |
| `HistoryTable` (H2) | `@history` | Table `<Entity>.<Attribute>` with `_entity` (references `_id`), `<Attribute>`, `_valid_at` and `_valid_offset`, plus index `<Entity>.<Attribute>._by_time` on `(_entity, _valid_at)`. Every write adds a row. The current value is the row with the latest valid time. |

## Valid times

- `_valid_at` holds the valid time in UTC as fixed-width text (`yyyy-MM-ddTHH:mm:ss.fffffffZ`), so text order equals time order.
- `_valid_offset` holds the original UTC offset in minutes, so the time is read back exactly as it was given.
- Only the valid time is stored, not the time of writing.

## Access

SQLite is accessed through `Microsoft.Data.Sqlite`, without an ORM.
