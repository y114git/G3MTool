# G3MTool

G3MTool is the command-line tool and reference implementation for the `.g3mpatch` format. It works with GameMaker data files, creates and applies `.g3mpatch` patches, merges patches, inspects files, compares files, runs bundled/import scripts, and works with xdelta patches.

Supported data-file extensions are `.win`, `.ios`, `.droid`, and `.unx`.

## Commands

```bash
G3MTool [command] [options]
```

When no arguments are provided, G3MTool starts an interactive prompt. Output paths are optional for most commands; when omitted, files are written next to the executable or to the command-specific default directory.

Global options:

| Option | Meaning |
| --- | --- |
| `-v`, `--verbose` | Print verbose command output |
| `-l`, `--log <path>` | Write a log file. Use `--log default` for `logs/{command}_{timestamp}.log` next to the executable |
| `--json` | Machine-readable JSON output for supported CLI commands |
| `--version`, `-V` | Print the application version |

## patch

Create, apply, validate, or merge `.g3mpatch` files.

### patch create

```bash
G3MTool patch create <original> <modified> [output] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]
```

`original` is the source data file. `modified` can be a modified data file or an `.xdelta` patch; `.xdelta` input is applied to the original first and then converted to `.g3mpatch`.

By default, the patch stores G3MTool resource changes and does not embed xdelta data. `--xdelta-fallback` also stores an xdelta fallback built from `original` and the modified data file. This increases `.g3mpatch` size.

`--cache <dir>` reads and writes `.g3mcache` analysis files for real data-file inputs. The cache stores reusable metadata, resource hashes, duplicate-name counts, and order-sensitive resource names. It does not replace resource payloads and is ignored when the source file size or stored MD5 no longer matches.

### patch apply

```bash
G3MTool patch apply <data> <patch> [output] [--xdelta-fallback] [--xdelta-path <path>]
```

`patch` can be `.g3mpatch`, `.xdelta`, or a data file. `.xdelta` input is applied directly. Data-file input is converted to `.g3mpatch` using `data` as the reference.

For `.g3mpatch` input, the default order is normal `.g3mpatch` apply first, then the embedded xdelta copy if normal apply fails and the patch contains one. With `--xdelta-fallback`, G3MTool tries the embedded xdelta copy first; if that fails, it continues with normal `.g3mpatch` apply.

### patch validate

```bash
G3MTool patch validate <patch> [--data <data-file>]
```

Validates the `.g3mpatch` file and manifest. With `--data`, also checks compatibility information against a data file when available.
`--cache <dir>` can reuse cached data-file identity when checking `--data`.

### patch merge

```bash
G3MTool patch merge <original> <patch1> <patch2> [patch3...] [options]
```

Merges two or more patches using `original` as context. Patch order is low to high priority. Inputs can be `.g3mpatch`, `.xdelta`, or data files.

Options:

| Option | Meaning |
| --- | --- |
| `-o`, `--out <path>` | Write the merged `.g3mpatch` |
| `-a`, `--apply <path>` | Apply the merged patch and write a data file |
| `--code` | Enable 3-way merge for GML code files |
| `--properties` | Enable deep merge for JSON property files |
| `-r`, `--report <path>` | Write a Markdown merge report |
| `--cache <dir>` | Reuse `.g3mcache` analysis files while converting data-file or `.xdelta` inputs |

If neither `--out` nor `--apply` is set, G3MTool writes a merged `.g3mpatch` to the default output path.

## diff

```bash
G3MTool diff <file1> <file2> [output-dir] [--full] [--cache <dir>]
```

Compares data files and/or `.g3mpatch` files and writes a Markdown report.

Default mode reports resource-level changes, changed text-file counts, resource counts, asset-order/index differences, sprite frame differences, and selected reference checks without unified text/code/JSON hunks. `--full` includes the exact text/code/JSON diffs for changed files and deeper TPI/reference/asset-order detail; it is slower and can produce much larger reports.

For data-vs-data reports, `--cache <dir>` can skip repeated resource hash analysis when matching `.g3mcache` files already exist.

With global `--json`, `diff` writes a single JSON object to stdout with output path, mode, total difference counts, per-resource-type counts, text-diff count, and warnings. The Markdown report is still written to disk.

## info

```bash
G3MTool info <target> [-v] [--json] [--cache <dir>]
```

Shows metadata for a data file or `.g3mpatch`.

Without `-v`, data-file output includes resource counts, GeneralInfo, and short breakdowns. With `-v`, it prints detailed per-resource listings. For `.g3mpatch`, verbose output includes resource counts by type from the manifest.
For data files, `--cache <dir>` stores and reuses the standard non-verbose info snapshot. Verbose mode still reads the data file because it prints full resource listings.

## xpatch

```bash
G3MTool xpatch create <original> <modified> [output]
G3MTool xpatch apply <original> <patch> [output]
```

Creates or applies binary xdelta patches. This is separate from `.g3mpatch`.
Pass `--xdelta-path <path>` to use a specific xdelta executable instead of the bundled one. This is useful on systems where the bundled binary is blocked or incompatible.

## execute

```bash
G3MTool execute <target> [args]
G3MTool execute <script.csx> [args] --data <data-file> --output <output-file>
G3MTool execute <script.csx> --data <data-file> --input <directory> --output <output-file>
G3MTool execute xdelta <args>
```

Runs an external program, a `.csx` script, or xdelta. For `.csx` scripts, `--data` loads a data file and `--output` writes the modified result. `--input` passes an input directory as the first script argument.
When the `target` is `xdelta`, pass `--xdelta-path <path>` to use a specific xdelta executable.

Bundled scripts are in `G3MToolCLI/Assets/scripts`.

## Build

```bash
dotnet publish G3MToolCLI -c Release -r <runtime>
```

Common runtimes: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

## Notes

`.g3mpatch` is G3MTool's patch format. It stores resource changes so patches can be inspected, applied to compatible data files, and merged. It does not guarantee byte-identical output for every data file. For exact binary fallback behavior, create patches with `--xdelta-fallback`; this increases patch size and depends on the input data matching the xdelta requirements.

## Legal

- [`LICENSE`](LICENSE)
- [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)
- [`SECURITY.md`](SECURITY.md)
