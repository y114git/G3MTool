# Changelog

## [1.3.0] - 2026-09-12

### Changed

- Moved patching, merging, diffing, cache handling, and data-file operations to G3MLib. The CLI manages batch jobs and scripts.
- Reduced self-contained single-file release size while retaining platform-specific XDelta support.
- Updated bundled scripts to the G3MLib API and routed their diagnostics through the host.
- Added validated data-file writes and preservation of previous batch outputs.
- Kept default output files beside the published executable and preserved data, patches, and reports across repeated batch merges.
- Fixed nested script execution when the main script is launched with a relative path.
