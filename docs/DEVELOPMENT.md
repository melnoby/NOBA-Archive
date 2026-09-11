# NOBA Archive — Development Guide

> **Status:** Phase 1 delivered. The solution exists and builds; see `PHASE_1_REPORT.md`.
> **Date:** 2026-08-19

## 1. Prerequisites

| Requirement | Why |
|---|---|
| .NET 10 SDK (LTS) | Target framework. Sufficient on its own for the engine, the CLI, and every current test suite |
| Windows 11 (or Windows 10 1809+) x64 | Required for `NOBA.Archive.Windows`, the desktop app, the installer, and any run that claims a shipping-configuration PASS |
| Visual Studio 2026 with **.NET Desktop** + **Windows App SDK / WinUI** workloads, or the standalone Windows App SDK + MSBuild | XAML compiler and packaging targets |
| Windows SDK 10.0.22621.0+ | Manifest, packaging, interop |
| Developer Mode enabled | Sparse-package deployment during development |
| WiX Toolset v6/v7 (`WixToolset.Sdk`) | Installer (Phase 9) |
| 7-Zip 26.02 `7z.dll` (x64) | Placed in `src/NOBA.Archive.App/native/x64/` by `scripts/fetch-native.ps1` |

> **What builds where.** The engine projects target `net10.0` and build and test on any OS, which is
> deliberate: it is what makes the engine genuinely embeddable and lets the unit suites run on any CI
> image. `NOBA.Archive.Windows` targets `net10.0-windows`; it compiles cross-platform but its Win32
> calls only execute on Windows. WinUI 3 (Phases 5+) and the installer (Phase 9) are Windows-only, and
> **any phase report claiming a shipping-configuration PASS must have been produced on Windows** with
> the toolchain above. See PHASE_1_REPORT.md §1.

> **Note on "(rule N)" citations.** `(rule N)` refers to numbered section N of the *NOBA Archive
> Master Build Prompt* — the product brief this project answers to.

## 2. Repository conventions

- `Directory.Build.props` sets, for all projects: `net10.0` by default (`NOBA.Archive.Windows` and the
  architecture tests override to `net10.0-windows`), `LangVersion=latest`,
  `Nullable=enable`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`,
  `AnalysisLevel=latest-recommended`, `Deterministic=true`, `ContinuousIntegrationBuild` in CI.
- `Directory.Packages.props` holds every package version. A `<PackageReference>` with a `Version`
  attribute fails the build.
- `.editorconfig` promotes the analyzer rules that matter (async correctness, disposal, span usage,
  `ConfigureAwait` in library code) to **error**.
- Nullable warnings are errors. `#pragma warning disable` requires a comment explaining why, and a
  reviewer sign-off.

## 3. Coding rules (enforced where possible)

| Rule | Enforcement |
|---|---|
| No `catch (Exception)` without typed handling or rethrow | analyzer + IL architecture test |
| No `.Result`, `.Wait()`, `GetAwaiter().GetResult()` in product code | IL architecture test |
| No `System.IO.File`/`Directory` outside `Infrastructure` | IL architecture test |
| No archive library outside `Formats` | IL architecture test |
| No user-facing string literal outside `.resw` | analyzer |
| No static mutable state | IL architecture test |
| No `TODO` in a production-critical path | build-time scan on `src/**` excluding `// TODO(phase-N):` in scaffolding |
| `CancellationToken` is the last parameter of every async method | analyzer |
| Every `IDisposable`/`IAsyncDisposable` is disposed | analyzer |

## 3.1 Building and testing

```powershell
.\scripts\build.ps1                     # restore + build, Release
.\scripts\test.ps1                      # every suite, with the phase-gate summary
dotnet build NOBA.Archive.sln -c Release
dotnet test  NOBA.Archive.sln -c Release      # xUnit v3 via Microsoft.Testing.Platform
```

`scripts\test.ps1` invokes each suite with `dotnet run` rather than `dotnet test`. xUnit v3 test
projects are self-executing console apps, so a single command covers both the shipping and offline
configurations — one code path, no test-host adapter to misconfigure, and a real exit code either way.

The scripts run on **Windows PowerShell 5.1** — what ships with Windows — as well as PowerShell 7+.
A build script that needs a shell the developer has to install first is a build script that gets
skipped.

**Offline profile.** `-p:NobaOfflineBuild=true` (or `NOBA_OFFLINE_BUILD=1`) swaps the
`Microsoft.Extensions.*` packages for the `Microsoft.AspNetCore.App` shared framework and xUnit v3 for
the harness in `NOBA.Archive.TestKit`, so the solution builds and tests where nuget.org is unreachable.
Test sources are identical in both modes. **It is not the shipping configuration** — a phase report may
only claim PASS for the Windows build machine after running without the flag there.

## 4. Branching and commits

`main` is always green. Work happens on `phase/<n>-<topic>` branches. A branch merges only when
build + unit + architecture + security suites pass. Commit messages state the phase and the change:
`phase-2(zip): stream-based extraction writer with post-open handle re-check`.

## 5. Phase workflow (rule 46)

Every phase follows the same twelve steps, and **stops** at the end:

1. Inspect the repository as it actually is.
2. Read the affected files.
3. Identify dependencies and blast radius.
4. Produce an implementation plan.
5. Implement only the approved scope.
6. Build.
7. Run tests.
8. Fix failures.
9. Review against the architecture rules.
10. Report exactly what changed (files, why, risks).
11. List remaining risks and open decisions.
12. Stop and wait for review.

No phase is skipped, and no phase silently absorbs the next one's work.

## 6. Phase report template

```markdown
## Phase N report — <title>
Scope delivered:      <bullet list, file by file>
Out of scope / deferred: <bullet list with reasons>

Build:                PASS | FAIL | NOT RUN (<reason>)
Unit tests:           PASS (n/n) | FAIL (n failed) | NOT RUN
Architecture tests:   PASS | FAIL | NOT RUN
Integration tests:    PASS | FAIL | NOT RUN
Security tests:       PASS | FAIL | NOT RUN
UI tests:             PASS | FAIL | NOT RUN

Commands run:         <verbatim, with output excerpts>
Architecture review:  <violations found / none>
Risks:                <what could bite us later>
Open decisions:       <what needs a human>
Next phase entry criteria: <what must be true before starting N+1>
```

## 7. Documentation duties

A change that alters behaviour updates the relevant document in the same commit:
`ARCHITECTURE.md` (structure/contracts), `FORMAT_SUPPORT.md` (a capability changes state only when its
test passes), `THREAT_MODEL.md` (new attack surface), `TESTING.md` (new test IDs), `CHANGELOG.md`
(user-visible change), and a new `docs/adr/` record for any decision worth remembering.
