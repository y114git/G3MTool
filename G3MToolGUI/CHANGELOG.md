# Changelog

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
