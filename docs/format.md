# Ontology format v0.1

Specification of the `.tur` format the parser in `src/Tursiops.Core/Format` implements. The reasons behind it are recorded in the project's decision log (E-07, E-09, E-11, E-16, E-24, E-25).

## Example

```
// Example ontology
Mensch {
  Name                    // timeless: only the last value written counts
  Wohnort @time           // only the current state counts, by the time it was valid
  Körpergröße @history    // every value is kept with the time it was valid
}
```

## Files

- Ontology files end with `.tur`. The extension `.turdb` is reserved for data files of a future storage engine.
- A file contains any number of entities. Text is read as UTF-8 and normalized to Unicode form C, so `ö` written as one or two code points is the same character.

## Grammar

```ebnf
file       = { entity } ;
entity     = name "{" ( "}" | line_end { attribute } "}" ) line_end ;
attribute  = name [ "in" unit ] { annotation } line_end ;
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
