# NOBA Archive

A professional archive manager for Windows — fast creation, safe extraction, reliable inspection.

**Minimum technical compatibility:** Windows 10 version 1809 (build 17763), x64.
**Tested and recommended:** Windows 10 22H2 and Windows 11.
Older Windows 10 editions no longer receive security updates from Microsoft; the compatibility floor is
about what the app runs on, not an endorsement of running an unsupported OS. See
[ADR-0010](docs/adr/ADR-0010-minimum-os.md).

> **Current status: Phase 1 delivered — the foundation is built and tested.**
> 17 projects, 272 passing tests, a clean warnings-as-errors build. There is still **no archive format
> support**: Phase 1 is the domain model, contracts, DI, configuration, logging, security foundations,
> test infrastructure, and the `noba-ar` CLI shell. Formats begin in Phase 2.
> See [docs/PHASE_1_REPORT.md](docs/PHASE_1_REPORT.md).

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
| [adr/](docs/adr/) | Architecture decision records (10 records) |
| [PHASE_0_REPORT.md](docs/PHASE_0_REPORT.md) | What Phase 0 delivered, risks, open decisions |
| [PHASE_1_REPORT.md](docs/PHASE_1_REPORT.md) | What Phase 1 built, what the tests prove, deviations, gate status |
| [SPIKE_7Z_INTEROP.md](docs/SPIKE_7Z_INTEROP.md) | Can a native backend be driven so we own every destination stream? (yes — with evidence) |

## Roadmap

| Phase | Scope | Status |
|---|---|---|
| 0 | Architecture & feasibility | **Delivered — awaiting review** |
| 1 | Solution, DI, logging, config, core model, abstractions, test infrastructure, CLI shell | **Delivered — awaiting review** |
| 2 | ZIP + the hardened extraction writer | Not started |
| 3 | 7Z | Not started |
| 4 | RAR (read-only) | Not started |
| 5 | Application UI | Not started |
| 6 | Windows integration | Not started |
| 7 | Security hardening | Not started |
| 8 | Performance | Not started |
| 9 | Packaging & installer | Not started |
| 10 | Release candidate | Not started |

## Building

Requires the .NET 10 SDK; the desktop app additionally requires Windows and the Windows App SDK
workload (see [DEVELOPMENT.md](docs/DEVELOPMENT.md)).

```powershell
.\scripts\build.ps1            # restore + build, Release
.\scripts\test.ps1             # all suites, with the phase-gate summary
```

## Third-party components

NOBA Archive will link dynamically against 7-Zip's `7z.dll` (LGPL-2.1-or-later, with BSD and unRAR
provisions) and use SharpCompress (MIT). Full notices ship with the product in `NOTICE.txt`.

**RAR notice:** the UnRAR-derived code used for RAR extraction may not be used to develop a
RAR (WinRAR) compatible archiver. NOBA Archive does not and will not create RAR archives.

## Privacy

No telemetry. No analytics. No cloud uploads. No network access required for any archive operation.
Auto-update is disabled in 1.0 and no update URL exists in the codebase.
