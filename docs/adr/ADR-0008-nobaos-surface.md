# ADR-0008 — Engine library + CLI as the NobaOS integration surface

**Status:** Accepted (Phase 0, 2026-08-19) · **Refined by** ADR-0009 (CLI ships in the installer, single engine)

## Context
NOBA Archive must ship standalone yet be integrable into NobaOS
(*برنامج منفصل يمكن دمجه إلى NobaOS*). Options: in-process library, CLI, local IPC service, or nothing
beyond clean layering.

## Decision
Build **two** surfaces in this milestone: the UI-free engine assemblies (`Core`, `Application`,
`Security`, `Infrastructure`, `Formats`) consumable in-process via `AddNobaArchiveEngine(...)`, and
`noba-ar.exe`, a thin CLI over the same engine with stable exit codes and `--json` output.
A named-pipe IPC host is designed for but **not built** (rule 54) — an always-on listener with no current
consumer is attack surface, not architecture.

## Consequences
- The engine must never depend on WinUI, never write to the console, and never show a dialog; host
  interaction is expressed as `IPasswordProvider`, `IConflictPrompt`, `IProgress<>`.
- `IAppDataPathProvider` becomes mandatory, which also unlocks portable mode later.
- The CLI accepts a password only on stdin or from an ACL-checked file — never as a command-line
  argument and never from an environment variable, since a same-user process can read both a process's
  command line and its environment block.
- The engine set includes `NOBA.Archive.Windows`; without its Win32 primitives the embedded engine could
  not propagate Mark of the Web or perform the post-open handle re-check.
- `Core`'s public types become a versioned compatibility contract with NobaOS.
