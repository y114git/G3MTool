# Security Policy

## Supported Versions

Security fixes are provided on a best-effort basis for the latest `main` branch
and the latest published release.

| Version | Supported |
| --- | --- |
| latest release | yes |
| `main` | yes |
| older releases | no |

## Reporting a Vulnerability

Please report security issues privately.

1. Preferred channel: GitHub Security Advisories (private vulnerability report).
2. If advisory reporting is unavailable, open a private contact first and do
   not publish exploit details in public issues.
3. Include:
   - affected G3MTool version (`--version`)
   - OS and architecture
   - reproducible steps
   - impact assessment
   - minimal PoC (without proprietary game assets)

## Response Expectations

- Initial acknowledgement: best effort within 72 hours.
- Triage and severity assessment: best effort within 14 days.
- Fix timeline depends on severity and reproducibility.

## Disclosure Guidelines

- Do not publish full exploit details before a fix or mitigation is available.
- Coordinate disclosure timing with maintainers.

## Scope Notes

- CLI `execute` and the GUI script/program workflows run with user permissions.
  Treat untrusted scripts and programs as untrusted code execution.
- G3MTool runs locally. Neither application implements telemetry or automatic
  updates.
