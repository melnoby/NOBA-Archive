# NOBA Archive ↔ NobaOS Integration

> **Status:** Phase 0 proposal.
> **Requirement (as stated):** *برنامج منفصل يمكن دمجه إلى NobaOS* — a **standalone program** that
> **can be integrated into NobaOS**.
> **Date:** 2026-08-19

## 1. Principle

NOBA Archive is a complete product on its own. NobaOS integration must never become a reason for the
desktop app to carry hosting machinery it does not need, and must never let NobaOS concerns leak into the
archive engine.

Two integration surfaces are in scope for this milestone, chosen in the Phase 0 review:

1. **Embeddable engine library** — in-process, for .NET-based parts of NobaOS.
2. **Command-line interface (`noba-ar.exe`)** — out-of-process, language-agnostic, scriptable.

A local IPC/named-pipe service is *designed for* but **not built** in 1.0 (rule 54).

## 2. Surface A — embeddable engine

```text
NobaOS (.NET)
   │  references
   ▼
NOBA.Archive.Application     ← use cases, operation queue, policies
NOBA.Archive.Core            ← domain model, contracts
NOBA.Archive.Security        ← path policy, bomb guards
NOBA.Archive.Formats         ← providers (+ native/x64/7z.dll alongside)
NOBA.Archive.Infrastructure  ← filesystem, extraction writer, temp workspace, settings store
NOBA.Archive.Windows         ← Win32 primitives: GetFinalPathNameByHandle, Mark of the Web, DLL search
```

`NOBA.Archive.Windows` is part of the engine set, not the app: without it the embedded engine and the
CLI could not propagate Mark of the Web or perform the post-open handle re-check, and `SEC-018`/`SEC-005`
would fail. It contains interop only — no UI, no shell registration UI, no dialogs.

Guarantees this imposes on the architecture — and the reason the layering in ARCHITECTURE.md §3.1 is
non-negotiable:

- **No WinUI, no Windows App SDK, no XAML** anywhere below `NOBA.Archive.UI`. The engine assemblies
  target plain `net10.0-windows` and can be consumed by a console host, a service, or NobaOS.
- **No `Console.` output, no `MessageBox`, no dialogs** in the engine. User interaction is expressed as
  interfaces the host implements: `IPasswordProvider`, `IConflictPrompt`, `IProgress<OperationProgress>`.
  A headless host supplies non-interactive implementations (fail-fast password, fixed conflict policy).
- **No process-global state**, no static caches, no `Environment.Exit`. Everything is resolved from a DI
  container the host owns.
- **Settings and logging are injected.** `IAppDataPathProvider` lets NobaOS place configuration, logs and
  temp workspaces wherever it wants — this is also what enables portable mode later.

Registration is a single call:

```csharp
services.AddNobaArchiveEngine(options =>
{
    options.NativeLibraryDirectory = @"C:\NobaOS\lib\noba-archive\native\x64";
    options.AppDataRoot            = @"C:\NobaOS\state\noba-archive";
    options.MaxConcurrentOperations = 2;
});
```

Versioning: the engine assemblies follow semantic versioning independently of the desktop app.
Public types in `Core` are the compatibility contract; changing them is a major-version event.

## 3. Surface B — `noba-ar.exe`

**Decided (ADR-0009):** `noba-ar.exe` ships **in the standard NOBA Archive installer**, installed
alongside the desktop application and the native archive dependencies.

**Discovery contract — explicit, never `PATH`.** The installer writes a per-machine registry key and a
host reads it. Resolution order:

| # | Source |
|---|---|
| 1 | A path NobaOS was configured with explicitly |
| 2 | `HKLM\SOFTWARE\NOBA\Archive\CliPath` |
| 3 | `HKLM\SOFTWARE\NOBA\Archive\InstallPath` + `noba-ar.exe` |
| 4 | Fail with "NOBA Archive is not installed" — **never** fall back to a `PATH` lookup |

`PATH` is an **optional installer feature, opt-in and off by default**, for people who want `noba-ar`
from any shell. It is not a runtime dependency of the engine, the CLI, or NobaOS integration: a machine
with it disabled is fully supported. The constants live in `NOBA.Archive.Engine.InstallationContract`
so the installer and every host share one definition, and an architecture test asserts the engine reads
no environment variable at all.

> **There is exactly one archive engine.** `NOBA.Archive.Cli` contains argument parsing, output shaping,
> exit-code mapping, and non-interactive `IPasswordProvider` / `IConflictPrompt` implementations —
> nothing else. No compression, extraction, path validation, conflict resolution, progress, queueing, or
> bomb-guard logic may be implemented there. It resolves the same `Application` handlers, `Formats`
> providers, `Security` path policy and `IExtractionWriter` as the desktop UI, via the same
> `AddNobaArchiveEngine(...)` call. Enforced by an architecture rule and by integration tests that run
> each scenario — including the hostile `SEC-###` archives — through both surfaces and compare outcomes.

Consequence worth stating plainly: every security control the desktop UI has applies to NobaOS-driven
operations automatically, because there is only one implementation to get right.

```text
noba-ar list    <archive> [--json] [--password-stdin | --password-file <path>]
noba-ar extract <archive> [entries...] -o <dir> [--conflict overwrite|skip|rename|fail]
                                       [--password-stdin] [--no-links] [--json]
noba-ar create  <archive> <inputs...>  [--format zip|7z|tar|tgz|txz|tbz2]
                                       [--profile fast|balanced|max|smallest]
                                       [--level 0-9] [--solid] [--encrypt]
                                       [--encrypt-names] [--split <size>] [--password-stdin]
noba-ar test    <archive> [--json] [--password-stdin]
noba-ar info    <archive> [--json]
noba-ar formats [--json]                      # capability matrix of this build
```

Contract rules that make it safe to script:

| Rule | Detail |
|---|---|
| Passwords | **only** via `--password-stdin`, or `--password-file <path>` when the file's ACL grants the current user alone. Never a command-line argument, and **never an environment variable** — on Windows another process running as the same user can read both a process's command line and its environment block, and the environment is additionally inherited by every child process. |
| Machine output | `--json` emits one JSON document on stdout; human text goes to stderr, so pipes stay clean |
| Progress | `--json` streams newline-delimited progress objects to **stderr** when a TTY is absent |
| Exit codes | stable and documented: `0` success · `1` usage error · `2` archive not found · `3` unsupported operation/format · `4` password required or wrong · `5` corrupt archive · `6` blocked by security policy · `7` insufficient disk space · `8` cancelled · `9` I/O failure · `10` backend failure |
| Determinism | no interactive prompt ever appears unless `--interactive` is passed; the default is fully non-interactive |
| Safety | the same `IPathPolicy` and bomb guards apply. There is no `--yolo`. Limits can be raised explicitly with `--max-ratio`, `--max-entries`, and each raise is logged. |

Example:

```powershell
# PowerShell has no '<' redirection operator — pipe instead.
Get-Content -Raw .\password.txt |
    noba-ar extract .\payload.7z -o C:\NobaOS\staging --conflict rename --password-stdin --json
```

## 4. Surface C — local IPC (designed, not built)

If NobaOS later needs to drive a *running* NOBA Archive instance (shared queue, shared progress UI), the
seam is `IOperationHost` over a named pipe with a versioned message contract. Nothing in 1.0 depends on
it, and no pipe is opened by default. Building it prematurely would add an always-on listener — an attack
surface with no current user (rules 53, 54).

## 5. What NobaOS must supply

| Interface | Headless default provided | Notes |
|---|---|---|
| `IPasswordProvider` | fail with `PasswordRequired` | NobaOS may supply from its own secret store |
| `IConflictPrompt` | fixed policy from options | `Ask` is invalid headless and is rejected at configuration time |
| `ILoggerFactory` | none — host supplies | redaction wrapper is applied by the engine regardless |
| `IAppDataPathProvider` | app-local default | required for portable/embedded layouts |

## 6. Non-goals

Shared process with the NobaOS shell, a plugin ABI, remote/network operation, and any cross-machine
protocol. The engine is an in-process library and a local executable — nothing more.
