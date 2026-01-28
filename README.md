# G3MTool

Cross-platform tool for various actions with GameMaker data files.

## Features

- **xpatch** - Create and apply xdelta patches
- **execute** - Execute programs, scripts (.csx), or xdelta commands
- **patch** - Create, apply, and validate G3M patches
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

### xdelta Patches

```bash
# Create xdelta patch
G3MTool xpatch create original.win modified.win [output.xdelta]

# Apply xdelta patch
G3MTool xpatch apply original.win patch.xdelta [output.win]
```

### G3M Resource Patches

```bash
# Create G3M patch
G3MTool patch create original.win modified.win [output.zip]

# Apply G3M patch
G3MTool patch apply data.win patch.zip [output.win]

# Validate patch
G3MTool patch validate patch.zip --data data.win
```

### Execute Scripts

```bash
# Execute .csx script with data file
G3MTool execute script.csx --data data.win [--output modified.win]

# Execute external program
G3MTool execute program.exe arg1 arg2

# Passthrough to xdelta
G3MTool execute xdelta -d -s original.win patch.xdelta output.win
```

### Info & Diff

```bash
# Get info about data file
G3MTool info data.win [--verbose]

# Get info about patch
G3MTool info patch.zip

# Compare files (output directory optional)
G3MTool diff data1.win data2.win [diff_output/]
```

## Global Options

| Option | Description |
|--------|-------------|
| `--verbose`, `-v` | Enable verbose output |
| `--log [path]` | Enable logging (default: `logs/{command}_{timestamp}.log`) |
| `--json` | JSON output (for `info`, `patch validate`) |
