# Changelog

## [1.0.4] - 2026-11-19

### Changed

- Updated to G3MLib 1.1.4: data files, patches and project files are written through a temporary file, so a failed write keeps the old file.
- Scripts run through the G3MLib script runner. A script that uses `#load` sees edits to the loaded files on the next run. File pickers receive the script's filter.
- Closing the window during an operation cancels it first, then closes.

### Fixed

- Cancel also ends a script that is waiting in a dialog or picker.
- Cancel now stops scripts, XDelta operations, and external programs. XDelta and program processes are killed with their children, and a half-written XDelta output is removed.
- A batch apply or merge keeps its progress and log between items instead of completing the progress bar after each one.
- The bundled xdelta is extracted when an operation needs it, not at startup, so a platform without one can still open the window. Scripts that use xdelta through the library get it too.

## [1.0.3] - 2026-11-15

### Changed

- Updated to G3MLib 1.1.3: patches that each add global scripts, tags, feature flags or rooms keep all of them when merged with property merge on, and patching and merging write data files faster.
- Updated Avalonia to 12.1.6 and Roslyn scripting to 5.12.0.

## [1.0.2] - 2026-11-04

### Changed

- Updated to G3MLib 1.1.2.
- Inspect reads the cache folder when one is set, and accepts .zip patches.

### Fixed

- Cancel now stops patch create, validate, and inspect.
- A cancelled batch reports Cancelled with the files made so far, not a failed item.
- A temporary folder that cannot be deleted no longer turns a finished operation into an error.
- The error for a missing patch file names the file.
- A long log no longer freezes the window; it is trimmed in chunks.

## [1.0.1] - 2026-11-01

### Added

- Cancel button for running operations.

### Changed

- Updated to G3MLib 1.1.0.

## [1.0.0] - 2026-09-12

### Added

- First release of the cross-platform graphical interface for G3MTool.
- Patch, merge, comparison, inspection, validation, batch, XDelta, script, and external-program workflows powered by G3MLib.
- Live progress and selectable colour-coded activity logs.
- Script questions, text input, and native file and folder pickers.
- Validated data-file writes with recovery when replacing output files fails.
- Operation-specific form resets, correct merge output selection, and file-picker support for batch merge sets.
