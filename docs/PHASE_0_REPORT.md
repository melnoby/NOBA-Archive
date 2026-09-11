# Phase 0 Report — Architecture & Feasibility

**Project:** NOBA Archive · **Date:** 2026-08-19 · **Status:** delivered, awaiting review
**Update 2026-08-19:** open decisions **D6 and D7 are now resolved** by the project owner and recorded in
ADR-0009 and ADR-0010. The Phase 1 entry criteria in §8 are updated accordingly.
**Gate:** Phase 1 does not begin until this report is approved.

---

## 1. Repository state as found

| Check | How | Result |
|---|---|---|
| `C:\Users\m-m-e\Desktop\NOBA-Archive` | recursive directory listing over the connected-folder bridge, 2026-08-19 | **empty** — zero entries returned |
| Existing solution / projects / code | same listing | none |
| Existing architecture or APIs to preserve | same listing | none |
| Git repository | not separately verified; the listing returned no entries at all | assume none |

Nothing was assumed about the machine's toolchain: no shell access to the Windows device was available
in this session, so .NET SDK / Visual Studio presence is listed as a Phase 1 entry criterion (§8) rather
than reported as a fact.

Nothing was assumed and nothing was overwritten. Phase 0 adds documentation only; no source code,
no solution file, no project files were created (rule 46 — Phase 1 owns that).

## 2. Scope delivered

| Deliverable (per the brief) | Document |
|---|---|
| 1. Architecture proposal | [ARCHITECTURE.md](ARCHITECTURE.md) |
| 2. Technology decision | [ARCHITECTURE.md §2](ARCHITECTURE.md) + [adr/ADR-0001](adr/ADR-0001-ui-stack.md) |
| 3. Archive backend candidates | [FORMAT_SUPPORT.md §1](FORMAT_SUPPORT.md) + [adr/ADR-0002](adr/ADR-0002-archive-backends.md) |
| 4. ZIP/7Z/RAR capability matrix | [FORMAT_SUPPORT.md §2](FORMAT_SUPPORT.md) |
| 5. RAR licensing considerations | [FORMAT_SUPPORT.md §3](FORMAT_SUPPORT.md) + [adr/ADR-0004](adr/ADR-0004-rar-scope.md) |
| 6. Security threat model | [THREAT_MODEL.md](THREAT_MODEL.md) |
| 7. Project structure | [ARCHITECTURE.md §3](ARCHITECTURE.md) |
| 8. Phase 0 acceptance criteria | §4 below |
| 9. Risks and unresolved decisions | §5 and §6 below |
| Dependency matrix | [DEPENDENCIES.md](DEPENDENCIES.md) |
| Testing strategy | [TESTING.md](TESTING.md) |
| NobaOS integration design | [NOBAOS_INTEGRATION.md](NOBAOS_INTEGRATION.md) |
| Decision records | [adr/](adr/) |

## 3. Decisions taken in this phase

| # | Decision | Where |
|---|---|---|
| 1 | .NET 10 LTS + WinUI 3 / Windows App SDK 2.x, MVVM, DI | ADR-0001 |
| 2 | Hybrid backend — managed ZIP/TAR, native `7z.dll` 26.02 for 7Z/RAR/bonus | ADR-0002 |
| 3 | 7z interop approach — provisional own P/Invoke layer, decided by spike | ADR-0003 |
| 4 | RAR read-only, permanently; not offered in the Create dialog | ADR-0004 |
| 5 | **Providers decode, only our writer touches the filesystem** | ADR-0005 |
| 6 | Unpackaged EXE + WiX MSI (WixToolset.Sdk v6/v7) + sparse MSIX for the Windows 11 context menu | ADR-0006 |
| 7 | Nullable progress totals, capability-based pause — no faking | ADR-0007 |
| 8 | NobaOS integration = engine assemblies + `noba-ar.exe`; IPC designed, not built | ADR-0008 |
| 9 | **`noba-ar.exe` ships in the standard installer as a thin host over the same engine — no second archive engine** | ADR-0009 |
| 10 | **Minimum Windows 10 1809 (17763); tested baseline Windows 10 22H2 + Windows 11** | ADR-0010 |

## 4. Phase 0 acceptance criteria

- [x] Repository inspected; actual state established rather than assumed.
- [x] Technology stack chosen with dated evidence for currency and support windows.
- [x] Layering defined with an explicit dependency-direction table.
- [x] Dependency direction specified in a **machine-checkable form**, with the enforcement mechanism
      named per rule class (assembly references vs IL inspection). The test itself is Phase 1 work —
      nothing is checked yet.
- [x] Archive engine abstraction defined with explicit, per-format capability flags.
- [x] Backend candidates compared on licence, maintenance, capability, and security.
      *Performance was not measured* — no benchmark was run, and the performance remarks in
      FORMAT_SUPPORT.md §1 are expectations, not data. Measurement is Phase 8.
- [x] RAR licensing assessed against the controlling text; scope decided and justified.
- [x] Capability matrix produced, with "planned" clearly distinguished from "supported".
- [x] Threat model covering assets, trust boundaries, twelve threat classes, and mitigations.
- [x] Every mitigation mapped to a named test ID.
- [x] Test strategy including deterministic fixture generation and hostile-archive builders.
- [x] Project structure defined, including the CLI and TestKit projects the brief implies.
- [x] NobaOS integration surface designed without adding unused infrastructure.
- [x] Risks and unresolved decisions listed with owners and deadlines (§5, §6).
- [x] No implementation performed — the Phase 1 gate is respected.

**Not claimed:** any build or test result. See §7.

## 5. Risks

| # | Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|---|
| ~~R1~~ | `7z.dll` interop unable to give per-entry stream control | **High** | — | **CLOSED 2026-08-19.** The spike was run early and passed every scenario, including solid and encrypted-header archives, entry refusal, and zero backend disk writes. See [SPIKE_7Z_INTEROP.md](SPIKE_7Z_INTEROP.md) |
| R2 | SharpCompress is pre-1.0; a minor version introduces breaking changes | Medium | Medium | Exact pin, central package management, isolation behind `IArchiveProvider`, round-trip tests per format |
| R3 | Windows App SDK stable releases carry ~12 months of servicing | Medium | High (certain) | Budget an annual SDK bump; keep SDK usage confined to `App`/`UI` so the bump cannot reach the engine |
| R4 | Native `7z.dll` CVEs land after we ship | Medium | Medium–High | ADR-0005 removes the traversal class structurally; plus version+hash manifest, release-time CVE review, and a documented DLL-swap procedure |
| R5 | Sparse-package signing not arranged in time for Phase 9 | Medium | Medium | Start certificate procurement at Phase 6, not Phase 9; classic registry fallback keeps Windows 10 working meanwhile |
| R6 | UI virtualisation for >1M entries proves harder than expected | Medium | Medium | Flattened virtual index designed in Phase 5; measured in Phase 8 with a 1M-entry fixture built in Phase 2 |
| R7 | `FluentAssertions` licence terms unacceptable for the pinned version | Low | Medium | Verify in Phase 1; substitute Shouldly or plain asserts — a one-day change if caught early |
| R8 | Arabic/RTL treated as a late "translation pass" and breaking layout | Medium | Medium | `ar-EG` resources and `FlowDirection` exist from the first Phase 5 view, and an RTL UI test runs from day one |
| R9 | LGPL packaging constraints discovered at installer time | Medium | Low | Recorded now in DEPENDENCIES.md §5: no single-file publish, no ILMerge, no trimming for LGPL assemblies |
| R10 | Scope creep from the "bonus" read-only format tier | Low | Medium | A format is listed in the UI only after it has a passing round-trip test |
| R11 | The CLI quietly grows its own extraction or validation logic and drifts from the UI | **High** — would void ADR-0005 for NobaOS-driven work | Medium | Architecture rule forbidding engine interfaces in `Cli`, plus the `PAR-###` dual-surface parity suite that runs the hostile `SEC-###` archives through both paths and compares outcomes |
| R12 | A Windows 10 1809 validation image is unavailable at release time | Medium | Medium | `PKG-006` is a release gate where practical; when it cannot run, the release notes say so explicitly instead of implying coverage |

## 6. Decisions resolved and still open

**Resolved on 2026-08-19 — no longer blocking Phase 1:**

| # | Question | Decision | Recorded in |
|---|---|---|---|
| ~~D6~~ | Is `noba-ar.exe` part of the 1.0 installer? | **Yes** — installed alongside the desktop app and the native archive dependencies. It is a *thin CLI host* over the same engine: no second archive engine, no duplicated compression/extraction logic. NobaOS invokes the installed executable directly. | [ADR-0009](adr/ADR-0009-cli-distribution.md) |
| ~~D7~~ | Minimum OS? | **Windows 10 1809 (build 17763)** for technical compatibility; **Windows 10 22H2 and Windows 11** as the tested and recommended baseline. The floor rises only if a concrete dependency or required feature makes 1809 technically impossible — never for convenience. | [ADR-0010](adr/ADR-0010-minimum-os.md) |

**Still open (need a human):**

| # | Question | Needed by | Recommendation |
|---|---|---|---|
| ~~D1~~ | 7z interop: own P/Invoke (B) or SharpSevenZip (A)? | ~~Phase 3~~ | **RESOLVED 2026-08-19: option B.** The spike proved per-entry stream control; ADR-0003 is now Accepted |
| D2 | Is a code-signing certificate available, and in whose name? | Phase 6 | Required for the sparse package; SmartScreen reputation also depends on it |
| D3 | Which extensions do we claim by default on install? | Phase 6 | Claim none by default; offer a checklist during install. Silent association-grabbing is how utilities lose trust |
| D4 | ARM64: ship in 1.0 or keep the code clean and defer? | Phase 9 | Defer shipping; keep code architecture-neutral. `7z.dll` has an ARM64 build if this changes |
| D5 | Ship a portable (no-install) build in 1.0? | Phase 9 | Defer; `IAppDataPathProvider` makes it a small later change |

## 7. Validation status — reported honestly

| Suite | Result |
|---|---|
| Build | **NOT RUN** — no code exists in Phase 0, and the current working environment is Linux with no .NET SDK |
| Unit tests | **NOT RUN** — none exist yet |
| Architecture tests | **NOT RUN** — none exist yet |
| Integration tests | **NOT RUN** — none exist yet |
| Security tests | **NOT RUN** — none exist yet |
| UI tests | **NOT RUN** — none exist yet |

**Environment constraint that must be resolved before Phase 1 can be validated:** WinUI 3 and the
Windows App SDK can only be built and tested on Windows. Phase 1 code can be authored anywhere, but
`dotnet build` / `dotnet test` must be executed on the `noby` machine (or a Windows CI runner), and
their real output pasted into the Phase 1 report. No PASS will be claimed for a command that was not run.

## 8. Phase 1 entry criteria

Phase 1 (Foundation) may begin when:

1. This report is reviewed and approved.
2. ~~D6 and D7 are answered~~ — **done 2026-08-19** (ADR-0009, ADR-0010).
3. The Windows toolchain is confirmed present on the build machine: .NET 10 SDK, Visual Studio 2026 with
   the Windows App SDK workload, Windows SDK 10.0.22621.0+, Developer Mode on.
4. A decision is made on where the repository lives (local folder only, or a git remote).

Phase 1 scope will be exactly: solution + projects (**including `NOBA.Archive.Cli`**) +
`Directory.Build.props`/`Directory.Packages.props` + DI (`AddNobaArchiveEngine`) + logging with
redaction + versioned configuration + core domain model + `IArchiveProvider` and operation abstractions
+ `IFileSystem`/`IExtractionWriter`/`ITempWorkspace` + xUnit infrastructure + the architecture tests,
including the new rule that `Cli` may not implement engine interfaces.
**No format implementation, no UI.** The CLI project exists in Phase 1 only as a shell that wires the
container and answers `noba-ar formats`; it gains real commands as each format lands.
