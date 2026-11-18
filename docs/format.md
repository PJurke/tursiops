# Ontology format v0.1

Specification of the `.tur` format the parser in `src/Tursiops.Core/Format` implements. The reasons behind it are recorded in the project's decision log (E-07, E-09, E-11, E-16, E-24, E-25, E-41).

## Example

```
// Example ontology
Mensch {
  Name: text                      // timeless: only the last value written counts
  Wohnort: text @time             // only the current state counts, by the time it was valid
  Körpergröße: number @history    // every value is kept with the time it was valid
}
```

## Files

- Ontology files end with `.tur`. The extension `.turdb` is reserved for data files of a future storage engine.
- A file contains any number of entities. Text is read as UTF-8 and normalized to Unicode form C, so `ö` written as one or two code points is the same character.

## Grammar

```ebnf
file       = { entity } ;
entity     = name "{" ( "}" | line_end { attribute } "}" ) line_end ;
attribute  = name ":" type [ "in" unit ] { annotation } line_end ;
type       = "number" | "text" | "date" | "bi" ;
annotation = "@" name ;
unit       = unit_char { unit_char } ;
name       = letter { letter | digit | "_" } ;
line_end   = [ comment ] ( newline | end_of_file ) ;
comment    = "//" { any_char } ;
```

- `letter` is any Unicode letter.
- `unit_char` is any character except whitespace, `@`, `{`, `}` and `//`, so units like `m²`, `km/h` or `°C` are possible.
- Spaces and tabs are allowed anywhere between the parts. Blank lines and comment-only lines are allowed wherever a new line begins.

## Rules

- **Braces:** The opening `{` may stand on the line of the entity name or on a later line. After `{` the line ends: one attribute per line, so `Mensch { Name }` is an error. An empty entity may be written on one line: `Mensch { }`.
- **Comments:** `//` starts a comment that runs to the end of the line. It may end any line.
- **Types:** Every attribute declares a base type after a colon, before unit and annotations: `Körpergröße: number @history`. Spaces around the colon are optional.
  - `number`: whole numbers and decimals.
  - `text`: text, even if it looks like a number.
  - `date`: a point in time.
  - `bi`: two-valued, only `true` or `false`.
  - Types are case-sensitive; for `Number` the error message suggests `number`, for `boolean` or `bool` it suggests `bi`. A missing or unknown type is an error. Type names are not reserved and may be used as names.
  - The type is part of the information (decision log E-41). Tursiops rejects values of another type when they are written and never converts them, so `"181"` is rejected for a `number`. `null` is accepted for `number`, `text` and `date`, not for `bi`. Any attribute may have no value because it was never set.
- **Names:** contain no spaces, start with a letter and are case-sensitive. Entity names are unique per file, attribute names unique per entity. `in` is a keyword and cannot be a name.
- **Annotations:** `@time` and `@history` are known. Annotations are case-sensitive; for `@Time` the error message suggests `@time`. Any other annotation is an error.
- **Meaning of the annotations:**
  - no annotation: timeless, only the last value written counts.
  - `@time`: only the current state counts, i.e. the value with the latest valid time.
  - `@history`: every value is kept with its valid time. `@history` includes time.
- **Units:** `in <unit>` is recognized but not supported yet; the parser reports this as an error in plain language.

## Errors and warnings

- The parser stops at the first error and reports exactly that one.
- Redundant annotations are warnings, not errors, and do not stop parsing:
  - the same annotation twice, e.g. `@time @time` (warning at the second one),
  - `@time` together with `@history` (warning at `@time`).
- Errors and warnings carry line and column (both starting at 1) and a message in English that says in plain language what was expected. Tests check the kind, line and column, not the wording.
