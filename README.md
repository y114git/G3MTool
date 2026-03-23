# G3MTool

Cross-platform tool for various actions with GameMaker data files.

## Features

- **xpatch** - Create and apply xdelta patches
- **execute** - Execute programs, scripts (.csx), or xdelta commands
- **patch** - Create, apply, merge and validate G3M patches
- **info** - Display information about data files or patches
- **diff** - Compare data files or patches

## Installation

Download the latest release for your platform from the [Releases](https://github.com/y114git/G3MTool/releases) page.

Or build from source:

```bash
dotnet publish G3MToolCLI -c Release -r <platform>
```

**Platforms:** `win-x64` / `linux-x64` / `linux-arm64` / `osx-x64` / `osx-arm64`

## Usage

Output files are saved next to the G3MTool executable by default.

### xpatch - xdelta Patches

Create or apply binary xdelta patches.

```bash
# Create xdelta patch
(G3MTool) xpatch create <original> <modified> [output]

# Apply xdelta patch
(G3MTool) xpatch apply <original> <patch> [output]
```

**Arguments:**
- `original` - Path to original file
- `modified` - Path to modified file (create) or patch file (apply)
- `output` - (Optional) Output file path. Default: next to executable

### patch - G3M Patches

Create, apply, validate, or merge G3M resource patches.

#### patch create

```bash
(G3MTool) patch create <original> <modified> [output]
```

**Arguments:**
- `original` - Path to original data.win
- `modified` - Path to modified data.win or .xdelta file (auto-applied)
- `output` - (Optional) Output .g3mpatch path. Default: `patch_{timestamp}.g3mpatch`

#### patch apply

```bash
(G3MTool) patch apply <data> <patch> [output]
```

**Arguments:**
- `data` - Path to original data.win
- `patch` - Path to patch (.g3mpatch, .xdelta, or data file - auto-converted)
- `output` - (Optional) Output data.win path. Default: next to executable

#### patch validate

```bash
(G3MTool) patch validate <patch> [--data <data.win>]
```

**Arguments:**
- `patch` - Path to G3M patch file (.g3mpatch)

**Options:**
- `--data`, `-d` - (Optional) Path to data.win to check compatibility

#### patch merge

```bash
(G3MTool) patch merge <original> <patch1> <patch2> [patch3...] [options]
```

**Arguments:**
- `original` - Path to original data.win (required as context)
- `patches` - 2+ patch files (low → high priority). Accepts .g3mpatch, .xdelta, or data files

**Options:**
- `--out`, `-o` - Output path for merged .g3mpatch (default mode)
- `--apply`, `-a` - Apply merged patch and save resulting data file to this path
- `--code` - Enable deep merge for GML code files
- `--properties` - Enable deep merge for JSON property files
- `--report`, `-r` - Path for merge report (Markdown)

**Note:** If no flags specified, creates merged .g3mpatch by default.

### execute - Scripts & Programs

Execute .csx scripts, external programs, or xdelta commands.

```bash
# Execute .csx script
(G3MTool) execute <script.csx> [args] --data <data.win> --output <output.win>

# Execute with input directory (e.g., ImportSprites)
(G3MTool) execute <script.csx> --data <data.win> --input <sprites/> --output <output.win>

# Execute external program
(G3MTool) execute <program.exe> [args...]

# Passthrough to xdelta
(G3MTool) execute xdelta -d -s original.win patch.xdelta output.win
```

**Arguments:**
- `target` - Program, script (.csx), or 'xdelta'
- `args` - Arguments to pass to the target

**Options:**
- `--data`, `-d` - Path to data.win file (optional for .csx scripts)
- `--output`, `-o` - Output file path (required when --data is used)
- `--input`, `-i` - Input directory for scripts (e.g., sprites folder)

**Built-in scripts:** [Assets/scripts](https://github.com/y114git/G3MTool/tree/main/G3MToolCLI/Assets/scripts)

### info - File Information

Display information about data.win or patch files.

```bash
(G3MTool) info <target> [--verbose]
```

**Arguments:**
- `target` - Path to data.win or .g3mpatch

**Options:**
- `--verbose`, `-v` - Show detailed per-resource listing (every item with all properties)

**Without -v:** Resource counts, GeneralInfo, breakdowns  
**With -v:** Full per-resource detailed listing

### diff - Compare Files

Compare two data.win or patch files and generate a diff report.

```bash
(G3MTool) diff <file1> <file2> [output-dir]
```

**Arguments:**
- `file1` - First file (data.win or .g3mpatch)
- `file2` - Second file (data.win or .g3mpatch)
- `output-dir` - (Optional) Output directory for diff report. Default: `./diff/`

**Output:** Markdown diff report with resource-level changes

## Global Options

| Option | Description |
|--------|-------------|
| `--verbose`, `-v` | Enable verbose output |
| `--log [path]` | Enable logging (default: `logs/{command}_{timestamp}.log`) |
| `--json` | JSON output (for `info`, `patch validate`) |
