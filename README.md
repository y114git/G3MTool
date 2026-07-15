# G3MTool

G3MTool is the command-line tool and reference implementation for the `.g3mpatch` format. It works with GameMaker data files, creates and applies `.g3mpatch` patches, batch-processes patch jobs, merges patches, inspects files, compares files, runs bundled/import scripts, and works with xdelta/csx patches.

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
G3MTool patch create <original> <input> [output] [--xdelta] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]
```

`input` can be `.g3mpatch`, `.xdelta`, `.vcdiff`, `.csx`, or a data file. G3MTool materializes the input against `original` and validates the resulting data before creating the patch.

The default output is `.g3mpatch`. `--xdelta` creates `.xdelta` instead. `--xdelta-fallback` embeds an xdelta fallback inside `.g3mpatch`; it cannot be combined with `--xdelta`.

`--cache <dir>` reads and writes `.g3mcache` analysis files for real data-file inputs. The cache stores reusable metadata, resource hashes, duplicate-name counts, and order-sensitive resource names. It does not replace resource payloads and is ignored when the source file size or stored MD5 no longer matches.

### patch apply

```bash
G3MTool patch apply <data> <patch> [output] [--xdelta-fallback] [--xdelta-path <path>]
```

`patch` can be `.g3mpatch`, `.xdelta`, `.vcdiff`, `.csx`, or a data file. A `.csx` script receives `data` through `ScriptGlobals.Data`. G3MTool saves and reopens the script result before using it.

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

Merges two or more inputs using `original` as context. Input order is low to high priority. Every input is derived independently from the same original before resource merge.

Options:

| Option | Meaning |
| --- | --- |
| `-a`, `--apply <path>` | Write the merged data file |
| `-o`, `--out <path>` | Also keep the merged `.g3mpatch` |
| `--code` | Enable 3-way merge for GML code files |
| `--properties` | Enable deep merge for JSON property files |
| `-r`, `--report <path>` | Write a Markdown merge report |
| `--cache <dir>` | Reuse `.g3mcache` analysis files while converting data-file or `.xdelta` inputs |

If `--apply` is not set, G3MTool writes the merged data file to the current directory as `<original>_merged<ext>`. Add `--out` when you also want to keep the intermediate merged `.g3mpatch`.

### patch batch

Batch commands run multiple independent patch jobs against the same original data file. They hash inputs before work starts, skip repeated identical jobs, and copy the already-produced output under the next generated name.

```bash
G3MTool patch batch apply <original> <patches...> --out-dir <dir> [--cache <dir>] [--continue-on-error] [--xdelta-fallback]
G3MTool patch batch create <original> <modified...> --out-dir <dir> [--cache <dir>] [--continue-on-error] [--xdelta-fallback]
G3MTool patch batch merge <original> <sets...> [--apply <data-dir>] [--out <patch-dir>] [--cache <dir>] [--continue-on-error] [--code] [--properties] [--report]
```

`batch apply` applies each supported input independently to the original data file. `batch create` creates one `.g3mpatch` per input. `batch merge` runs independent mixed-format merge jobs; each set is a quoted comma-separated list in low-to-high priority order:

Independent batch jobs run in isolated processes with automatic CPU and memory limits. Hashing, patch normalization, and patch-container loading also use bounded parallelism; priority-dependent merge and DATA mutation remain ordered.

```bash
G3MTool patch batch merge game.win "base_patch.xdelta,ui_patch.g3mpatch" "mod_a.win,mod_b.xdelta,mod_c.g3mpatch" --apply data --out patches
```

Batch merge writes patched data outputs using the original file extension. By default those files go to the current directory; use `--apply <data-dir>` to choose the data output folder. Add `--out <patch-dir>` when you also want to keep each merged `.g3mpatch`. Use `--code`, `--properties`, and `--report` to apply those merge options to every set.

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
