<!-- markdownlint-disable MD013 MD033 MD041 -->

<p align="center">
  <img src="G3MToolGUI/Assets/images/G3MTool_logo.png" alt="G3MTool logo" width="650">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/CLI-1.3.0-3F8F3F?style=for-the-badge" alt="G3MTool CLI 1.3.0">
  <img src="https://img.shields.io/badge/GUI-1.0.0-3F8F3F?style=for-the-badge" alt="G3MTool GUI 1.0.0">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10">
  <a href="https://github.com/y114git/G3MTool/releases"><img src="https://img.shields.io/github/downloads/y114git/G3MTool/total?style=for-the-badge" alt="Total downloads"></a>
</p>

<h1 align="center">G3MTool</h1>

G3MTool provides command-line and graphical front ends for G3MLib. Both work with GameMaker data files and support `.g3mpatch` and XDelta workflows.

- **G3MTool CLI** is the command-line interface for automation, scripts, and interactive terminal use.
- **G3MTool GUI** is the cross-platform graphical interface for patching, merging, comparison, inspection, batch work, scripts, and external programs.

Release archives contain one self-contained executable plus the required license notices. Choose the archive matching both the application and your operating system/architecture.

Supported data-file extensions are `.win`, `.ios`, `.droid`, and `.unx`.

## GUI

G3MTool GUI exposes G3MLib-based patch, merge, comparison, inspection, validation, batch, XDelta, script, and external-program workflows without command-line arguments. It shows live progress and selectable, colour-coded activity logs.

The GUI executable is named `G3MToolGUI.exe` on Windows and `G3MToolGUI` on Linux and macOS.

## CLI - Commands

```bash
G3MTool [command] [options]
```

CLI release archives contain `G3MTool.exe` on Windows and `G3MTool` on Linux and macOS.

When no arguments are provided, G3MTool CLI starts an interactive prompt. Output paths are optional for most commands; when omitted, files are written next to the executable or to the command-specific default directory.

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

`input` can be `.g3mpatch`, `.xdelta`, `.vcdiff`, `.csx`, or a data file. G3MTool CLI materializes the input against `original` and validates the resulting data before creating the patch.

The default output is `.g3mpatch`. `--xdelta` creates `.xdelta` instead. `--xdelta-fallback` embeds an xdelta fallback inside `.g3mpatch`; it cannot be combined with `--xdelta`.

`--cache <dir>` reads and writes `.g3mcache` analysis files for real data-file inputs. The cache stores reusable metadata, resource hashes, duplicate-name counts, and order-sensitive resource names. It does not replace resource payloads and is ignored when the source file size or stored MD5 no longer matches.

### patch apply

```bash
G3MTool patch apply <data> <patch> [output] [--xdelta-fallback] [--xdelta-path <path>]
```

`patch` can be `.g3mpatch`, `.xdelta`, `.vcdiff`, `.csx`, or a data file. A `.csx` script receives `data` through `ScriptGlobals.Data`. G3MTool CLI saves and reopens the script result before using it.

For `.g3mpatch` input, the default order is normal `.g3mpatch` apply first, then the embedded xdelta copy if normal apply fails and the patch contains one. With `--xdelta-fallback`, G3MTool CLI tries the embedded xdelta copy first; if that fails, it continues with normal `.g3mpatch` apply.

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

Without `--apply`, G3MTool CLI writes a merged `.g3mpatch`. Use `--out` to choose its path and `--apply` to write the merged data file.

### patch batch

Batch commands run multiple independent patch jobs against the same original data file. They hash inputs before work starts, skip repeated identical jobs, and copy the already-produced output under the next generated name.

```bash
G3MTool patch batch apply <original> <patches...> --out-dir <dir> [--cache <dir>] [--continue-on-error] [--xdelta-fallback]
G3MTool patch batch create <original> <modified...> --out-dir <dir> [--xdelta] [--cache <dir>] [--continue-on-error] [--xdelta-fallback]
G3MTool patch batch merge <original> <sets...> [--apply <data-dir>] [--out <patch-dir>] [--cache <dir>] [--continue-on-error] [--code] [--properties] [--report]
```

`batch apply` applies each supported input independently to the original data file. `batch create` creates one `.g3mpatch` per input, or one `.xdelta` per input with `--xdelta`. `batch merge` runs independent mixed-format merge jobs; each set is a quoted comma-separated list in low-to-high priority order:

Batch jobs run sequentially in the CLI process. Patch normalization and resource analysis may use bounded parallelism inside G3MLib; merge priority and data mutations remain ordered.

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
G3MTool execute xdelta -- <args>
```

Runs an external program, a `.csx` script, or xdelta. For `.csx` scripts, `--data` loads a data file and `--output` writes the modified result. `--input` passes an input directory as the first script argument.
Put `--` before external-program or xdelta arguments so their options are passed through unchanged. When the `target` is `xdelta`, place `--xdelta-path <path>` before the separator to use a specific xdelta executable.

Bundled scripts are in `G3MToolCLI/Assets/scripts`.

GUI scripts can ask questions, request text, and open file or folder pickers. Cancelled dialogs return no selection. Script work runs outside the UI thread; closing the main window waits for the current operation to finish.

## Build

```bash
dotnet publish G3MToolCLI -c Release -r <runtime>
dotnet publish G3MToolGUI -c Release -r <runtime>
```

Common runtimes: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

The `Build and Release G3MTool` workflow is manual. It can build CLI, GUI, or both; publishing is optional. When publishing, its tag defaults to the current UTC date in `YYYY.MM.DD` form, unless a custom tag is supplied.

## Notes

`.g3mpatch` is G3MLib's resource-aware patch format, used by both G3MTool applications. It stores resource changes so patches can be inspected, applied to compatible data files, and merged. It does not guarantee byte-identical output for every data file. For exact binary fallback behavior, create patches with `--xdelta-fallback`; this increases patch size and depends on the input data matching the xdelta requirements.

## Legal

- [`LICENSE`](LICENSE)
- [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)
- [`SECURITY.md`](SECURITY.md)
