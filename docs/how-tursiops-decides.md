# How Tursiops decides

Tursiops stores your data and decides on its own how to store it. This page explains what happens in the background and how to read the sleep log. The exact rules are specified in [`storage.md`](storage.md).

## You decide what, Tursiops decides how

The ontology says which information exists. `Körpergröße: number @history` means: every height is a number, and every height is kept together with the time it was valid. That is the information, and Tursiops never changes it.

How the values are stored, in which tables and columns, is up to Tursiops. When it changes the storage, every value, every valid time and every current value reads exactly as before.

## Layouts

For an attribute with `@history`, Tursiops chooses between two layouts:

| Layout | Stored as | Good for |
|---|---|---|
| H2 | history table only: every value is one row | writing values, reading histories. A new attribute starts here. |
| H1 | current value in the row of the entity, older values in the history table | listing the current value of all entities at once |

Reading the current value of a single entity costs the same in both.

## Waking phases and sleep

While awake, Tursiops counts per attribute how the data is used: writes, reads of one current value, reads of the current values of all entities, history reads. Counting changes nothing.

A sleep (`tursiops sleep <database>`) looks at the waking phase since the last sleep, decides on the layout of each attribute, changes it if needed, writes the log and starts the next waking phase. All of this happens in one transaction: if something fails, the database stays as it was.

## How it decides

For each layout, Tursiops estimates what the waking phase would have cost: the number of each operation times its cost. The costs come from measurements on the target system; for SQLite they are listed in [`storage.md`](storage.md#layout-decision). The estimate is not a measured time, it is what the counted use would cost by these numbers. Reading the current values of all entities costs more the more entities there are.

Tursiops changes the layout only if the other layout

- saves more than the change itself costs (copying the stored values), and
- saves a set share of the cost in the current layout on top of that,

and only if the attribute had a minimum number of operations in the phase. The two layouts are often close. Without this margin, Tursiops would change back and forth on small differences. Both thresholds are set by hand for now; their values are in [`storage.md`](storage.md#layout-decision).

## Reading the log

Each entry starts with one line that says what happened and what it means for you. In a terminal, this line is bold. Below it:

1. the reason, in one sentence,
2. the counted operations of the phase,
3. the estimated cost of the phase in each layout,
4. the saving a change has to exceed,
5. what the other layout saves, or would cost more.

Every number can be recomputed. An example from the demo, after a phase with 200 reads of the current heights of all 300 people:

| | |
|---|---|
| H2 | 200 × 300 × 0.935 µs = 56.1 ms |
| H1 | 200 × 300 × 0.63 µs = 37.8 ms |
| Cost of the change | 1,500 values × 0.55 µs = 0.8 ms |
| Required saving | 0.8 ms + 20 % of 56.1 ms = 12.0 ms |
| Saving of H1 | 56.1 ms − 37.8 ms = 18.3 ms, more than 12.0 ms: the layout changes to H1 |

`tursiops log <database>` shows the log again; it is kept in the database file.

## Try it

```
tursiops demo demo.db
```

The demo creates `demo.db` from [`examples/mensch.tur`](../examples/mensch.tur), runs four waking phases with a sleep after each, and checks before and after every sleep that all values read the same. The use in the demo is exaggerated on purpose, so that the effect is visible: the two layouts differ only a little, and real use rarely looks this one-sided.
