# ADR-0006 — Unpackaged EXE + MSI, with a sparse package for the shell menu

**Status:** Accepted (Phase 0, 2026-08-19)

## Context
Windows 11's modern context menu requires an `IExplorerCommand` COM server registered through an MSIX
package. A file utility, however, is expected to install like 7-Zip or WinRAR: user-chosen directory,
per-machine install, plain uninstall.

## Options
Full MSIX (clean lifecycle, Store-ready, but no install-directory choice and an awkward story for a
system utility) · unpackaged only (traditional, but legacy-only context menu on Windows 11) ·
**unpackaged + sparse package** (traditional install, modern menu).

## Decision
Ship an unpackaged EXE installed by a **WiX MSI** (WixToolset.Sdk v6/v7 — pin the exact version at
Phase 9), plus a **sparse MSIX package** with
`<uap10:AllowExternalContent>` that registers the `IExplorerCommand` handler. Classic
`ContextMenuHandlers` registry entries are offered as an opt-in fallback for Windows 10.

## Consequences
- The sparse package must be signed with a certificate the machine trusts. Signing is *needed* by
  Phase 9, so certificate **procurement starts at Phase 6** — lead times are the risk, not the signing.
- Uninstall must remove the sparse package registration, file associations, and any classic handlers.
  A packaging test verifies a clean machine state after uninstall.
- Per-machine install directory means per-user settings live in `%LOCALAPPDATA%`, which is also what
  makes portable mode straightforward later.
- The installer also lays down **`noba-ar.exe`** and `native\x64\7z.dll`, records the install directory
  in `HKLM\SOFTWARE\NOBA\Archive\InstallPath`, and adds the directory to `PATH` (opt-out at install
  time) so NobaOS can invoke the CLI without a hard-coded path (ADR-0009). Uninstall removes all of it.
- The MSI targets **Windows 10 1809 (17763) and later**, with the sparse package registered only where
  the OS supports the modern context menu; on older builds the classic opt-in handler is offered instead
  (ADR-0010).
- MSIX-only distribution stays possible later; nothing in the app assumes an unpackaged identity beyond
  the `IAppDataPathProvider` seam.
