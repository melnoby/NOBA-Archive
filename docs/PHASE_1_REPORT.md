# Phase 1 Report — Foundation

**Project:** NOBA Archive · **Date:** 2026-08-19 · **Status:** delivered, awaiting review
**Gate:** Phase 2 (ZIP) does not begin until this report is approved.

---

## 1. Validation status

Every line below was produced by running the command shown, in this session, and reading its output.
Nothing is reported as PASS that was not executed (rule 51).

```text
BUILD                          PASS
UNIT TESTS                     PASS   (188 tests across 5 suites)
ARCHITECTURE TESTS             PASS   (47 tests)
CLI CONTRACT TESTS             PASS   (25 tests)
PARITY FOUNDATION              PASS   (12 tests)
                                      272 tests total, 0 failures
```

Verbatim:

```text
$ dotnet --version
10.0.111

$ dotnet build NOBA.Archive.sln -c Release -p:NobaOfflineBuild=true
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:10.51

$ ./scripts/test.sh Release
NOBA.Archive.Core.Tests:           89/89 passed in 0.06s
NOBA.Archive.Security.Tests:       41/41 passed in 0.06s
NOBA.Archive.Application.Tests:    21/21 passed in 0.09s
NOBA.Archive.Infrastructure.Tests: 19/19 passed in 0.20s
NOBA.Archive.Formats.Tests:        18/18 passed in 0.06s
NOBA.Archive.Architecture.Tests:   47/47 passed in 0.04s
NOBA.Archive.Cli.Tests:            25/25 passed in 0.13s
NOBA.Archive.Parity.Tests:         12/12 passed in 0.12s

UNIT TESTS (Core)              PASS
UNIT TESTS (Security)          PASS
UNIT TESTS (Application)       PASS
UNIT TESTS (Infrastructure)    PASS
UNIT TESTS (Formats)           PASS
ARCHITECTURE TESTS             PASS
CLI CONTRACT TESTS             PASS
PARITY FOUNDATION              PASS
```

`0 Warning(s)` is a real result, not a quiet setting: `TreatWarningsAsErrors=true` and
`AnalysisLevel=latest-recommended` are on for every project, so the build would have failed otherwise.

**The one caveat, stated plainly.** The build environment available in this session is Linux and
**cannot reach nuget.org** (npm and PyPI are reachable; the NuGet endpoints are not). The runs above
therefore used `NobaOfflineBuild=true` — §6 explains exactly what that swaps and why it does not
weaken the result. **Before Phase 2 is approved, the same two commands must be re-run on the Windows
build machine without that flag**, restoring the pinned packages and running xUnit v3, and the output
pasted here. Until that happens, "PASS on Windows with the shipping dependency set" is unverified and is
not being claimed.

---

## 2. Repository state as found

Inspected before touching anything, over the connected-folder bridge:

| Check | Result |
|---|---|
| `C:\Users\m-m-e\Desktop\NOBA-Archive` | 24 files — the Phase 0 documentation set, exactly as delivered |
| Source code, solution, project files | none |
| Existing architecture to preserve | none |

Nothing was overwritten. The Phase 0 documents were updated only where Phase 1 changed a fact
(§5), and each change is listed.

---

## 3. Scope delivered

65 production files (4,760 lines) and 27 test files (3,235 lines). No `TODO`, no
`NotImplementedException`, no `#pragma warning disable`, no `SuppressMessage` anywhere in `src/`.

### 3.1 Solution and build infrastructure

`NOBA.Archive.sln` with 17 projects, `Directory.Build.props` (nullable, warnings-as-errors,
deterministic, analyzers), `Directory.Build.targets`, `Directory.Packages.props` (central pins),
`.editorconfig`, `.gitignore`, `scripts/build.ps1`, `scripts/test.ps1`, `scripts/test.sh`.

### 3.2 `NOBA.Archive.Core` — domain model and contracts, BCL only

| Area | Types |
|---|---|
| Value objects | `ArchivePath`, `SafeLocalPath`, `ByteSize`, `CompressionLevel`, `DictionarySize`, `OperationId`, `Password`, `PathRejection`, `PathSanitization` |
| Format model | `ArchiveFormat`, `ArchiveCapabilities`, `ArchiveEntry`, `ArchiveStatistics`, `ArchiveTestResult`, `FormatDetection`, `CompressionOptions`, `CompressionProfile`, `ExtractionOptions`, `ConflictPolicy`, `LinkPolicy`, `IllegalNamePolicy` |
| Operations | `OperationType`, `OperationState`, `OperationTrigger`, `OperationStateMachine`, `OperationProgress`, `ArchiveOperation` |
| Errors | `ArchiveErrorCode` (closed set), `ArchiveError`, `ArchiveOperationException` |
| Contracts | `IArchiveProvider`, `IArchiveSession`, `IArchiveBuilder`, `IArchiveProviderRegistry`, `IFormatDetector`, `IFileSystem`, `IExtractionWriter`, `IPathPolicy`, `IResourceGuard`, `IPasswordProvider`, `IConflictPrompt`, `IConflictResolutionService`, `IOperationQueue`, `IPauseToken`, `ITempWorkspace(+Factory)`, `IClock`, `IAppDataPathProvider`, `ISecretRedactor`, `IUpdateService` |

Three decisions in here are worth calling out, because they are where the design earns its keep:

- **`ArchivePath` can only be constructed through validation**, so holding one is proof the path passed
  the structural gate. `..`, rooted, UNC, device and alternate-data-stream forms are *rejected*, never
  "resolved" — resolving is how traversal bugs get written.
- **`ArchiveEntry.Path` is nullable and paired with `Rejection`.** A hostile entry stays listable and
  reportable instead of throwing, so opening a malicious archive *shows the malice* rather than failing.
- **`OperationProgress` totals are nullable.** Unknown stays unknown; the UI renders indeterminate. No
  invented percentage (ADR-0007).

### 3.3 `NOBA.Archive.Security`

`PathPolicy` implements the trust boundary, keeping two classes of bad path apart: escape attempts are
rejected and reported; Win32-illegal-but-harmless names (`aux.h` from a Linux tree, trailing dots) are
deterministically renamed and reported, because rejecting those would break legitimate archives.
Containment is separator-aware, so `C:\out-evil` is not inside `C:\out`. `ResourceGuard` implements the
bomb thresholds — all configurable, all fed from observed bytes rather than declared metadata.
`SecretRedactor` strips secrets on the way to a sink.

### 3.4 `NOBA.Archive.Application`

`OperationQueue` (concurrency limit, real pause gate, cancellation that reaches the work, typed failure
translation), `OperationIdFactory`, `ProgressAggregator`, `ConflictResolutionService` (one implementation
of Overwrite/Skip/Rename/Ask with "…All" latching), `CapabilityGate`, `DiskSpaceEstimator`, `PauseToken`.
It references `Core` and `Security` only — the architecture test proves it.

### 3.5 `NOBA.Archive.Infrastructure`

`PhysicalFileSystem` (the only `System.IO.File`/`Directory` in the product, with typed error
translation), `TempWorkspaceFactory` (per-operation directory, deterministic cleanup, orphan sweeper
keyed on the owning process), `SettingsStore` + `SettingsMigrator` (versioned JSON, atomic publish,
corrupt-file quarantine), `RedactingLoggerProvider`, `SystemClock`, `AppDataPathProvider`,
`DisabledUpdateService`.

### 3.6 `NOBA.Archive.Formats`

`ArchiveProviderRegistry` and `MagicByteFormatDetector` (signature table, content authoritative,
extension a hint). **No format implementation** — that is Phase 2 onward, and the registry works
correctly with zero providers registered, which is what let the contracts be validated on their own.

### 3.7 `NOBA.Archive.Windows`

`NativeMethods` (`GetFinalPathNameByHandle`, `SetDefaultDllDirectories`) via the `[LibraryImport]`
source generator, `WindowsPathVerifier`, `NativeLibraryHardening`. Declared and compiled; wired into the
extraction writer in Phase 2. The only project with `AllowUnsafeBlocks`, and only because the interop
generator requires it — there is no hand-written unsafe code.

### 3.8 `NOBA.Archive.Engine` and `NOBA.Archive.Cli`

`AddNobaArchiveEngine(...)` — the single registration NobaOS, the CLI, and (in Phase 5) the desktop app
all use — plus `InstallationContract` and the headless host services. `noba-ar.exe` implements `formats`,
`version`, and `help` with `--json`, the documented exit codes, and nothing else.

```text
$ noba-ar formats --json
{ "product": "NOBA Archive", "version": "0.1.0", "formats": [] }

$ noba-ar version
NOBA Archive 0.1.0 (.NET 10.0.11) - Phase 1 foundation
```

An empty `formats` list is the honest answer for a build with no providers. A Phase 1 that printed
ZIP/7Z/RAR here would be claiming support that does not exist.

### 3.9 Test infrastructure

`NOBA.Archive.TestKit` (fakes: `FakeClock`, `RecordingLoggerProvider`, `FakeArchiveProvider`,
`ScriptedConflictPrompt`, `ScriptedPasswordProvider`; plus the offline harness) and eight suites.

---

## 4. What the tests actually prove

Not "272 tests pass" — *these* properties hold:

| Property | Where |
|---|---|
| Traversal, rooted, UNC, device and ADS entry paths are refused; Arabic, CJK, emoji and spaces are not | `ArchivePathTests`, `PathPolicyTests` |
| A sibling directory (`out-evil`) is not inside the destination (`out`) | `PathPolicyTests` |
| Reserved device names are renamed and reported, not silently accepted or bluntly blocked | `PathPolicyTests` |
| A link target that leaves the destination is refused | `PathPolicyTests` |
| Bombs trip; a legitimate 120 GB archive does not; thresholds are configurable | `ResourceGuardTests` |
| **A password written through `ILogger` never reaches the sink**, and `Password.ToString()` is `***` | `RedactionTests`, `PasswordTests` |
| Every state × trigger pair is exhaustively checked; terminal states accept nothing | `OperationStateMachineTests` |
| Cancellation reaches the work; pause/resume are real; the concurrency limit holds | `OperationQueueTests` |
| An unexpected exception becomes a typed failure with an operation id, never an escaping exception | `OperationQueueTests` |
| "…All" latches, and does not leak into the next operation | `ConflictResolutionTests` |
| A corrupt settings file is quarantined and startup continues on defaults | `SettingsStoreTests` |
| A v1 settings file cannot re-enable link creation through the old permissive flag | `SettingsStoreTests` |
| A crashed run's temp workspace is swept; a live one is not | `TempWorkspaceTests` |
| Content beats a misleading extension; the contradiction is detectable | `FormatDetectionTests` |
| The registry routes to the provider that has the needed capability, not merely the format | `ProviderRegistryTests` |
| A hostile entry is listed with its rejection instead of throwing | `ProviderContractTests` |
| The layering table is enforced; `Core` depends on the BCL alone | `DependencyDirectionTests` |
| No `System.IO.File`/`Directory` outside Infrastructure; no `Console` in the engine; no blocking on tasks | `MemberLevelRuleTests` |
| **The CLI implements no engine interface**, and **the engine reads no environment variable** | `MemberLevelRuleTests` |
| Exit codes are stable and every `ArchiveErrorCode` maps to a defined one | `ExitCodeContractTests` |
| In `--json` mode stdout carries only the JSON document | `CliContractTests` |
| A password cannot be passed as a command-line option value | `CliContractTests` |
| **Both surfaces resolve the same engine implementations and reach identical path verdicts** | `ParityFoundationTests` |
| A headless host configured to prompt fails at configuration time, not at run time | `ParityFoundationTests` |

The parity suite is the one that matters most for ADR-0009. It is a foundation, not the finished
`PAR-###` set: when ZIP lands in Phase 2, the same harness runs each `SEC-###` hostile archive through
both surfaces and compares outcomes.

---

## 5. Deviations from the Phase 0 plan, and why

Each of these is a change to an approved document. None was made silently.

| # | Deviation | Reason |
|---|---|---|
| D1 | **New project `NOBA.Archive.Engine`** | `Application` may not reference `Infrastructure`, so neither could host `AddNobaArchiveEngine(...)`. Without a facade, NobaOS would have to wire the container itself — and then "one engine" would be a convention, not a fact. Recorded in ARCHITECTURE.md §3. |
| D2 | **Engine projects target `net10.0`, not `net10.0-windows`** | Only `NOBA.Archive.Windows` needs Win32. Keeping the rest platform-neutral makes the compiler enforce embeddability and lets the unit suites run on any CI image. `Infrastructure` picks up the Windows TFM in Phase 2 with the extraction writer. |
| D3 | **`FluentAssertions` dropped** (risk R7 closed) | Its licensing moved to a commercial model for some uses. Plain xUnit asserts cover what the suites need. One fewer dependency and one fewer licence to track. |
| D4 | **Extra test projects** (`Infrastructure.Tests`, `Formats.Tests`) | Phase 1 delivers configuration, logging, and format detection; shipping them untested would have been the wrong kind of scope discipline. |
| D5 | **Offline build profile** (`NobaOfflineBuild`) | §6. |
| D6 | **`PATH` demoted to an optional installer feature** | Your refinement, applied before implementation: discovery goes through `InstallationContract` (registry), `PATH` is opt-in and off by default, and an architecture test asserts the engine reads no environment variable at all. |
| D7 | **Two analyzer configurations, not suppressions** | `CA1711` allows the `Queue` suffix (the domain term); `CA1707`/`CA1822`/`CA1873` are off *for tests only*, because underscored names read better in failure output, the harness needs instance methods, and the redaction test must log a literal secret to prove it never surfaces. All in `.editorconfig`, each with the reason next to it. |

**Not implemented, deliberately:** the extraction writer (ADR-0005) is Phase 2's centrepiece and needs a
real format to be meaningful — `IExtractionWriter` exists as the contract. No ZIP, 7Z, or RAR code. No UI.

---

## 6. The offline build profile

`NobaOfflineBuild=true` changes exactly two things:

| Shipping build (default) | Offline build |
|---|---|
| `Microsoft.Extensions.*` from the pinned NuGet packages | the same assemblies from the `Microsoft.AspNetCore.App` shared framework already on the machine |
| xUnit v3 | the dependency-free harness in `NOBA.Archive.TestKit` |

What it does **not** change: the production code (identical), the test source (byte-identical — the
harness deliberately mirrors the `[Fact]` / `[Theory]` / `[InlineData]` / `Assert` surface, so no test
contains a `#if`), the analyzers, or warnings-as-errors.

It exists because this environment cannot reach nuget.org, and the alternative was to hand over
unexecuted code with "NOT RUN" against every suite. It is a validation aid, not the shipping
configuration, and `scripts/build.ps1` / `scripts/test.ps1` default to the real one.

---

## 7. Risks

| # | Risk | Status |
|---|---|---|
| R7 | `FluentAssertions` licensing | **Closed** — dependency not taken (D3) |
| R13 | Phase 1 has not been built on Windows with the shipping dependency set | **Open — blocks Phase 2 approval.** Re-run both commands there and paste the output (§1) |
| R14 | The offline harness could drift from xUnit's semantics | Low: it is used only where the two agree, no test contains a conditional, and the Windows run is the arbiter |
| ~~R1~~ | 7z interop may not give per-entry stream control (ADR-0005) | **Closed** — spike run early and passed on every scenario; ADR-0003 accepted as option B. See [SPIKE_7Z_INTEROP.md](SPIKE_7Z_INTEROP.md) |
| R11 | CLI drifting into a second engine | Actively guarded now: the architecture rule and the parity suite both exist and pass |

---

## 8. Architecture review

Checked against ARCHITECTURE.md §3.1 and the forbidden list in the brief (rule 45):

- UI calling a compression library — not applicable yet; the layering test will catch it when `UI` exists.
- Business logic in code-behind — no XAML exists.
- Static mutable archive state — none; every service is DI-resolved.
- Blocking calls — architecture test passes.
- Passwords in logs — test proves they are not.
- Unvalidated extraction paths — nothing extracts yet; `IPathPolicy` is the only route to a `SafeLocalPath`.
- Hard-coded paths — none; `IAppDataPathProvider` owns every location.
- Fake progress — totals are nullable and no code synthesises one.
- `catch (Exception)` without handling — one site, in `OperationQueue`, which logs and converts to a typed
  `BackendFailure` so a defect still yields an operation id rather than a lost operation. That is
  meaningful handling, and it is tested.

**No violations found.**

---

## 9. Phase 2 entry criteria

1. This report reviewed and approved.
2. **Build and tests re-run on Windows without `NobaOfflineBuild`, with the output recorded (R13).**
3. Confirmation of where the repository lives (local folder only, or a git remote) — still open from Phase 0.

Phase 2 scope will be exactly: production-grade ZIP — open, list, extract, create, add, test, encryption
where supported, progress, cancellation, large files — **plus the extraction writer from ADR-0005**,
which ZIP is the first consumer of, and the `SEC-###` / `INT-###` suites those make possible.
