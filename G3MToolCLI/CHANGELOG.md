# Changelog

## [1.3.3] - 2026-11-15

### Changed

- Updated to G3MLib 1.1.3: patches that each add global scripts, tags, feature flags or rooms keep all of them when merged with `--properties`, and patching and merging write data files faster.
- Updated System.CommandLine to 3.0.0 and Roslyn scripting to 5.12.0.
- The G3MLib package version is set in one place (`G3MLibVersion` in `Directory.Build.props`). `-p:G3MLibProject=<path to G3MLib.csproj>` builds the CLI and the GUI against a local G3MLib checkout.

## [1.3.2] - 2026-11-04

### Changed

- Updated to G3MLib 1.1.2.

### Fixed

- Ctrl+C also cancels patch create, patch validate, and info. A second Ctrl+C ends the program at once.
- Batch create stops on Ctrl+C and exits with 130 instead of reporting failed items.
- A temporary folder that cannot be deleted no longer fails a finished command.
- Batch runs treat paths that differ only by case as different files on Linux and macOS.

## [1.3.1] - 2026-11-01

### Changed

- Updated to G3MLib 1.1.0 and System.CommandLine 2.0.14.
- Ctrl+C cancels patch apply, merge, compare, and batch runs. Existing outputs stay untouched, temporary files are removed, and the exit code is 130.

## [1.3.0] - 2026-09-12

### Changed

- Moved patching, merging, diffing, cache handling, and data-file operations to G3MLib. The CLI manages batch jobs and scripts.
- Reduced self-contained single-file release size while retaining platform-specific XDelta support.
- Updated bundled scripts to the G3MLib API and routed their diagnostics through the host.
- Added validated data-file writes and preservation of previous batch outputs.
- Kept default output files beside the published executable and preserved data, patches, and reports across repeated batch merges.
- Fixed nested script execution when the main script is launched with a relative path.
