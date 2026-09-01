# Third-Party Notices

G3MTool is licensed under GPL-3.0-only. See [`LICENSE`](LICENSE).

This repository redistributes third-party code, libraries, and native binaries.
If you redistribute a release archive, include:

- `LICENSE`
- `THIRD_PARTY_NOTICES.md`
- the `licenses/` directory

## Bundled components

### xdelta3

- Used for xdelta patch create/apply support.
- Upstream project: <https://github.com/jmacd/xdelta>
- Upstream license: Apache-2.0
- Local binaries bundled in:
  - `G3MToolCLI/Assets/bin/linux/xdelta`
  - `G3MToolCLI/Assets/bin/mac-arm64/xdelta`
  - `G3MToolCLI/Assets/bin/mac-x64/xdelta`
  - `G3MToolCLI/Assets/bin/win/xdelta.exe`
- License text: `licenses/Apache-2.0.txt`

### Underanalyzer

- Local source path: `G3MToolCLI/Lib/Underanalyzer`
- License: MPL-2.0
- License text: `licenses/MPL-2.0.txt`

### UndertaleModLib

- Local source path: `G3MToolCLI/Lib/UndertaleModLib`
- Version: `0.8.4.1`
- License: GPL-3.0
- License text: `licenses/GPL-3.0.txt`

### System.CommandLine

- Version: `2.0.0-beta4.22272.1`
- License: MIT
- License text: `licenses/MIT.txt`

### Microsoft.CodeAnalysis.CSharp.Scripting

- Version: `4.12.0`
- License: MIT
- License text: `licenses/MIT.txt`

### Magick.NET-Q8-AnyCPU

- Version: `14.11.0`
- License: Apache-2.0
- License text: `licenses/Apache-2.0.txt`

### SharpZipLib

- Version: `1.4.2`
- License: MIT
- License text: `licenses/MIT.txt`

## Notes

- Test-only packages are not listed here.
- Self-contained releases include .NET runtime components and may carry
  additional upstream notices.
