# Concept: Time

Known methods for storing time-dependent attributes, and how far Tursiops implements them. The methods are building blocks: a storage layout combines one choice per axis. Which layouts Tursiops actually uses is specified in [`../storage.md`](../storage.md). Why, is recorded in the project's decision log (E-25 to E-29).

What the user declares decides the content (no annotation, `@time`, `@history`). The methods below only decide how that content is stored. A change of method never changes the information (E-25).

Cost notes are first estimates from the sources and from reasoning, not measurements.

## Overview

Status on SQLite: ✓ implemented, ○ possible, not implemented yet, ✗ not possible on SQLite (needs an own engine).

| Method | Axis | Good for | SQLite |
|---|---|---|---|
| [Value column](#value-column) | where the current value lives | timeless attributes | ✓ |
| [Value column with time](#value-column-with-time) | where the current value lives | `@time`: current state only | ✓ |
| [History table only (H2)](#history-table-only-h2) | where the current value lives | write-heavy attributes, reading history | ✓ |
| [Current value in entity row (H1)](#current-value-in-entity-row-h1) | where the current value lives | reading current values of many entities | ○ |
| [Partition current and history](#partition-current-and-history) | where the current value lives | separating current from past inside one table | ✗ |
| [Start time only](#start-time-only) | time representation | late-arriving values, cheap writes | ✓ |
| [Start and end time](#start-and-end-time) | time representation | reading the value at a point in time | ○ |
| [Insertion order plus index](#insertion-order-plus-index) | physical order of history | default, no tuning | ✓ |
| [Clustered by entity and time](#clustered-by-entity-and-time) | physical order of history | finding the latest value of one entity | ○ |
| [Time-split B-tree](#time-split-b-tree) | physical order of history | very large histories | ✗ |

## Axis: where the current value lives

### Value column

The value is a column in the entity table. Writing replaces it. Used for attributes without annotation. Tursiops layout: `Column`.

### Value column with time

Value and valid time are columns in the entity table. A write only replaces the value if its valid time is not older than the stored one, so a late-arriving older measurement does not win. Used for `@time`. Tursiops layout: `ColumnWithTime`.

### History table only (H2)

All values with their valid time are rows of one table per attribute (`Entity.Attribute`). The current value is the row with the latest valid time.

- Write: one insert.
- Read current, one entity: one index lookup.
- Read current, many entities: the latest row has to be found per entity (group by entity), the most expensive case.
- Read history: one range scan.
- Space: one row per value, nothing duplicated.

Tursiops layout: `HistoryTable`, currently the only layout for `@history` (E-28).

Comparable: MariaDB stores system-versioned history in the same table as current data unless partitioned; its documentation notes that this slows down queries on current data. Source: [MariaDB, System-Versioned Tables](https://mariadb.com/docs/server/reference/sql-structure/temporal-tables/system-versioned-tables).

### Current value in entity row (H1)

The value with the latest valid time is a column in the entity table, older values are rows in `Entity.Attribute`.

- Write, newer than current: move the current value to the history table, write the new one into the row (two writes).
- Write, older than current (late arrival): one insert into the history table.
- Read current, one or many entities: read the entity table, no history involved. This is where H1 pays off.
- Read history: entity row plus history table.
- Space: as H2.

Comparable: SQL Server implements system-versioned temporal tables as a pair of a current table and a history table, per row instead of per attribute. Datomic keeps separate indexes for the current state and for history. Sources: [SQL Server temporal tables](https://learn.microsoft.com/en-us/sql/relational-databases/tables/temporal-tables), [Datomic indexes](https://docs.datomic.com/query/indexes.html).

Difference to these systems: they version by transaction time, which only grows. Tursiops uses valid time and accepts late-arriving older values (E-27), hence the extra case for writes.

### Partition current and history

One table, physically split into a partition for current rows and one or more for history. Queries on current data only touch the current partition. SQLite has no table partitioning, H1 emulates the effect with two tables. Source: [MariaDB, partitioning by SYSTEM_TIME](https://mariadb.com/docs/server/reference/sql-structure/temporal-tables/system-versioned-tables).

## Axis: time representation

### Start time only

Each value stores when it started to be valid. It ends where the next value starts. A late-arriving value is a single insert, no neighbour changes.

Comparable: historized attributes in Anchor Modeling store only the start of the interval (`ChangedAt`), one table per attribute in sixth normal form. Source: [Anchor Modeling](https://www.anchormodeling.com/) (overview: [Wikipedia](https://en.wikipedia.org/wiki/Anchor_modeling)).

Tursiops stores start times only (E-27).

### Start and end time

Each value stores start and end of its validity, as in SQL:2011 periods. Reading the value at a point in time is a simple range condition. Every write also updates the end of the preceding value, a late arrival changes two rows. Source: [Kulkarni, Michels: Temporal features in SQL:2011](https://old.dbs.uni-leipzig.de/file/Temporal%20features%20in%20SQL2011.pdf).

## Axis: physical order of history

### Insertion order plus index

The history table is an ordinary SQLite table ordered by its hidden `rowid`, plus an index on `(_entity, _valid_at)`. A lookup needs two searches: index, then table.

### Clustered by entity and time

The history table is declared `WITHOUT ROWID` with primary key `(_entity, _valid_at)`, so rows are stored in that order in a single B-tree and a lookup needs one search. SQLite recommends this for composite keys and small rows. Anchor Modeling recommends the same order (key ascending, time descending) so that the latest value comes first. Open point: two values of one entity with the same valid time would violate the key. Source: [SQLite, WITHOUT ROWID](https://www.sqlite.org/withoutrowid.html).

### Time-split B-tree

An index structure that splits nodes by time as well as by key and moves superseded versions to separate, append-only storage. Not available in SQLite. Sources: [Salzberg, Tsotras: Comparison of Access Methods for Time-Evolving Data (1999)](https://www.vislab.ucr.edu/IGERT/papers/temporal-index-survey.pdf), [Salzberg et al.: A Framework for Access Methods for Versioned Data (2004)](https://www.microsoft.com/en-us/research/?p=155695).

## Related, not yet covered

- Timestamping per row instead of per attribute: all attributes of an entity versioned together. Pays off when attributes mostly change together. Source: [Jensen, Soo, Snodgrass: Unifying temporal data models via a conceptual model (1994)](https://smsf.cs.arizona.edu/~rts/pubs/ISDec94.pdf).
