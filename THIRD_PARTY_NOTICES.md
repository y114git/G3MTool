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
  - `G3MToolCLI/Assets/bin/linux-arm64/xdelta`
  - `G3MToolCLI/Assets/bin/linux-x64/xdelta`
  - `G3MToolCLI/Assets/bin/mac-arm64/xdelta`
  - `G3MToolCLI/Assets/bin/mac-x64/xdelta`
  - `G3MToolCLI/Assets/bin/win-arm64/xdelta.exe`
  - `G3MToolCLI/Assets/bin/win-x64/xdelta.exe`
  - `G3MToolGUI/Assets/bin/linux-arm64/xdelta`
  - `G3MToolGUI/Assets/bin/linux-x64/xdelta`
  - `G3MToolGUI/Assets/bin/mac-arm64/xdelta`
  - `G3MToolGUI/Assets/bin/mac-x64/xdelta`
  - `G3MToolGUI/Assets/bin/win-arm64/xdelta.exe`
  - `G3MToolGUI/Assets/bin/win-x64/xdelta.exe`
- License text: `licenses/Apache-2.0.txt`

### G3MLib

- NuGet package: `G3MLib`
- License: GPL-3.0-only
- Includes UndertaleModLib-derived code under GPL-3.0 and Underanalyzer-derived
  code under MPL-2.0.
- License texts: `LICENSE`, `licenses/GPL-3.0.txt`, and `licenses/MPL-2.0.txt`.

### System.CommandLine

- Version: `2.0.12`
- License: MIT
- License text: `licenses/MIT.txt`

### Microsoft.CodeAnalysis.CSharp.Scripting

- Version: `5.9.0`
- License: MIT
- License text: `licenses/MIT.txt`

### Magick.NET-Q8-AnyCPU

- Version: `14.17.1`
- License: Apache-2.0
- License text: `licenses/Apache-2.0.txt`

### Avalonia

- Version: `12.1.2`
- Used by G3MTool GUI.
- License: MIT
- License text: `licenses/MIT.txt`

### SharpZipLib

- Version: `1.4.2`
- License: MIT
- License text: `licenses/MIT.txt`

## Notes

- Test-only packages are not listed here.
- Self-contained releases include .NET runtime components and may carry
  additional upstream notices.
