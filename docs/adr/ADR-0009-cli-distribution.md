# ADR-0009 — `noba-ar.exe` ships in the standard installer, over the same engine

**Status:** Accepted (Phase 0, 2026-08-19) — resolves open decision D6

## Context

NOBA Archive must be drivable by NobaOS. Two questions were open: whether the CLI ships with the
product or is a separate NobaOS-side artifact, and how it relates to the desktop application's
archive logic.

The failure mode to avoid is well known in this product category: a "quick" CLI that grows its own
extraction path, its own conflict handling, and its own idea of what a safe path is — and then drifts
from the UI, so a fix in one does not fix the other, and the security guarantees of ADR-0005 apply to
only one of the two.

## Decision

1. **`noba-ar.exe` is part of the standard NOBA Archive installer**, installed alongside the desktop
   application and the native archive dependencies (`native\x64\7z.dll`). It is not an optional
   component, not a separate download, and not a NobaOS-side build.
2. **It is a thin CLI host over the exact same engine.** `NOBA.Archive.Cli` contains argument parsing,
   output shaping (`--json` / human text), exit-code mapping, and non-interactive implementations of
   `IPasswordProvider` / `IConflictPrompt`. **Nothing else.**
3. **No second archive engine.** No compression, extraction, path validation, conflict resolution,
   progress, queueing, or bomb-guard logic may be implemented in `NOBA.Archive.Cli`. It resolves the
   same `NOBA.Archive.Application` handlers, the same `NOBA.Archive.Formats` providers, the same
   `NOBA.Archive.Security` path policy, and the same `IExtractionWriter` as the desktop UI, through the
   same `AddNobaArchiveEngine(...)` registration.
4. **NobaOS invokes the installed executable through an explicit discovery contract.** The installer
   writes `HKLM\SOFTWARE\NOBA\Archive` with `InstallPath`, `CliPath`, and `Version`; a host reads those
   values. Resolution order is: host-supplied path → `CliPath` → `InstallPath` + `noba-ar.exe` → fail with
   "NOBA Archive is not installed".
5. **`PATH` is an optional installer feature, not a discovery mechanism.** Adding the install directory
   to the system `PATH` is a convenience for people who want `noba-ar` from any shell. It is **opt-in and
   off by default**, and it is never a runtime dependency of the engine, the CLI, or NobaOS integration:
   a machine with the feature disabled is fully supported and integration must keep working there.
   Nothing in the engine or the CLI reads `PATH`. The contract lives in
   `NOBA.Archive.Engine.InstallationContract`, so the installer and any host share one definition.

## Enforcement

This is not a guideline; it is checked:

- `NOBA.Archive.Cli` is bound by the same dependency table as every other project
  (ARCHITECTURE.md §3.1) and the architecture tests apply to it unchanged.
- An additional architecture rule: **`NOBA.Archive.Cli` may not contain any type implementing
  `IArchiveProvider`, `IExtractionWriter`, `IPathPolicy`, or `IConflictResolutionService`.** IL
  inspection fails the build if one appears.
- A size/shape guard in review: if a change to the CLI is not argument parsing, output formatting, or
  exit-code mapping, it belongs in `Application` or below and must be usable by the UI too.
- Integration tests run the same scenario twice — once through the Application handlers directly and
  once through the CLI — and assert identical outcomes, including on the `SEC-###` hostile archives.
  A security guarantee that holds in the UI but not in the CLI is a bug, and this is how it gets caught.

## Consequences

- The installer gains one executable and the `HKLM\SOFTWARE\NOBA\Archive` key; uninstall removes both,
  plus the optional `PATH` entry if the user enabled it (`PKG-003`, `PKG-007`).
- A host that "works on my machine" only because the directory happens to be on `PATH` is a broken host.
  An architecture test asserts the engine never reads an environment variable, so the dependency cannot
  creep in unnoticed.
- Every security control — path policy, symlink handling, bomb guards, MOTW propagation, password
  hygiene — applies to NobaOS-driven operations automatically, because there is only one implementation.
- A bug fix or a hardening change lands once and benefits both surfaces.
- The CLI's exit codes and `--json` schema become a compatibility contract with NobaOS; changing them
  is a major-version event, exactly like `Core`'s public types (ADR-0008).
- Native AOT for the CLI stays subject to the LGPL constraint in DEPENDENCIES.md §5 — the CLI shares the
  engine, so it shares the packaging rules.
