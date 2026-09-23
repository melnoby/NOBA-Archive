# NOBA Archive

A professional archive manager for Windows — fast creation, safe extraction, reliable inspection.

**Minimum technical compatibility:** Windows 10 version 1809 (build 17763), x64.
**Tested and recommended:** Windows 10 22H2 and Windows 11.
Older Windows 10 editions no longer receive security updates from Microsoft; the compatibility floor is
about what the app runs on, not an endorsement of running an unsupported OS. See
[ADR-0010](docs/adr/ADR-0010-minimum-os.md).

> **Current status: Phase 5 — Windows shell integration, not yet installable.**
> 879 executed test cases across 11 suites, a clean warnings-as-errors build. ZIP/TAR/GZIP (managed)
> and 7Z/RAR-read (via the bundled 7-Zip backend) all create/extract/test; the WinUI 3 app and the
> `noba-ar` CLI run on the same engine. The Explorer context menu (classic and Windows 11 modern) is
> registered by scripts against a DEV-ONLY self-signed certificate — there is no installer yet, no
> production code-signing certificate, and no `LICENSE`/`NOTICE.txt`. Not a release build; see the
> phase reports and [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) before running it on a machine you
> care about.

---

## What this will be

A local, native Windows desktop archiver with no telemetry, no cloud dependency, and no background
services — designed to be embeddable into NobaOS as a UI-free engine and a scriptable CLI.

**Planned for 1.0** (planned ≠ working — see [docs/FORMAT_SUPPORT.md](docs/FORMAT_SUPPORT.md)):

| | |
|---|---|
| Create / extract / test | ZIP, 7Z, TAR, GZIP, BZIP2, XZ |
| Extract / test only | RAR & RAR5 — creation is legally excluded ([why](docs/adr/ADR-0004-rar-scope.md)) |
| Encryption | AES-256 for ZIP and 7Z; encrypted filenames for 7Z |
| Safety | hardened extraction writer, path-traversal and symlink defence, archive-bomb guards, Mark-of-the-Web propagation |
| UX | WinUI 3 Fluent UI, light/dark/system, English + Arabic (RTL), full keyboard access, screen-reader support |
| Integration | Explorer context menu, file associations, drag & drop, and `noba-ar.exe` — a scriptable CLI installed with the app, running on the **same** engine as the UI ([ADR-0009](docs/adr/ADR-0009-cli-distribution.md)) |

## Documentation

| Document | Purpose |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Layering, domain model, archive-engine abstraction, concurrency, progress |
| [FORMAT_SUPPORT.md](docs/FORMAT_SUPPORT.md) | Backend evaluation, capability matrix, RAR licensing |
| [THREAT_MODEL.md](docs/THREAT_MODEL.md) | Assets, trust boundaries, threats, mitigations |
| [SECURITY.md](SECURITY.md) | Security posture and vulnerability reporting |
| [DEPENDENCIES.md](docs/DEPENDENCIES.md) | Every dependency with licence, version, and risk |
| [TESTING.md](docs/TESTING.md) | Test pyramid, fixture generation, test catalogue, validation commands |
| [DEVELOPMENT.md](docs/DEVELOPMENT.md) | Prerequisites, conventions, phase workflow |
| [NOBAOS_INTEGRATION.md](docs/NOBAOS_INTEGRATION.md) | Engine library and CLI surfaces for NobaOS |
| [adr/](docs/adr/) | Architecture decision records (11 records) |
| [PHASE_0_REPORT.md](docs/PHASE_0_REPORT.md) | What Phase 0 delivered, risks, open decisions |
| [PHASE_1_REPORT.md](docs/PHASE_1_REPORT.md) | What Phase 1 built, what the tests prove, deviations, gate status |
| [SPIKE_7Z_INTEROP.md](docs/SPIKE_7Z_INTEROP.md) | Can a native backend be driven so we own every destination stream? (yes — with evidence) |

## Roadmap

| Phase | Scope | Status |
|---|---|---|
| 0 | Architecture & feasibility | **Delivered** |
| 1 | Solution, DI, logging, config, core model, abstractions, test infrastructure, CLI shell | **Delivered** |
| 2 | ZIP + the hardened extraction writer | **Delivered** |
| 3 | 7Z (via the bundled 7-Zip backend) | **Delivered** |
| 4 | RAR (read-only) | **Delivered** |
| 5 | Application UI, Explorer context menu (classic + modern) | **In progress** — UI and both menu paths work end to end on a signed-and-registered dev build; no installer |
| 6 | Windows integration (installer, real signing, NOBA OS handoff) | Not started |
| 7 | Security hardening | Partially covered by Phase 0–5 work; a dedicated pass has not run |
| 8 | Performance | Not started |
| 9 | Packaging & installer | Not started — no MSI/MSIX installer project exists yet |
| 10 | Release candidate | Not started |

## Building

Requires the .NET 10 SDK; the desktop app additionally requires Windows and the Windows App SDK
workload (see [DEVELOPMENT.md](docs/DEVELOPMENT.md)).

```powershell
.\scripts\build.ps1            # restore + build, Release
.\scripts\test.ps1             # all suites, with the phase-gate summary
```

## Third-party components

NOBA Archive links dynamically against 7-Zip's `7z.dll` (LGPL-2.1-or-later, with BSD and unRAR
provisions) for 7Z and RAR; ZIP, TAR, and GZIP are handled by managed code in this repository.
`LICENSE` and `NOTICE.txt` do not exist yet — tracked before any public release.

**RAR notice:** the UnRAR-derived code used for RAR extraction may not be used to develop a
RAR (WinRAR) compatible archiver. NOBA Archive does not and will not create RAR archives.

## Privacy

No telemetry. No analytics. No cloud uploads. No network access required for any archive operation.
Auto-update is disabled in 1.0 and no update URL exists in the codebase.
