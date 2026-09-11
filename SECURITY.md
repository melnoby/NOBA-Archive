# Security Policy — NOBA Archive

> **Status:** Phase 0. The controls below are commitments the implementation must meet, each mapped to
> a test in [docs/TESTING.md](docs/TESTING.md). None has been implemented or verified yet.

## Why this matters for an archiver

An archive manager parses files from untrusted sources and writes their contents to disk. This product
category has a live exploitation history — WinRAR `CVE-2025-8088` (alternate-data-stream path traversal,
exploited as a zero-day), WinRAR `CVE-2025-6218`, and 7-Zip `CVE-2025-11001`/`-11002` and `CVE-2025-55188`
(symlink handling → arbitrary file write / RCE, actively exploited). NOBA Archive is designed around that
reality rather than in spite of it.

## Design commitments

| # | Commitment |
|---|---|
| 1 | **The compression backend never decides where a byte lands.** Providers decode into streams we own; a single hardened writer performs every destination write. This removes the traversal/symlink class even if the backend itself is vulnerable. ([ADR-0005](docs/adr/ADR-0005-extraction-writer.md)) |
| 2 | Two classes of bad entry path, handled differently and never silently: **escape attempts** (`..`, rooted, UNC, device paths, NTFS alternate-data-stream names, control characters) are **rejected outright and reported** — never "repaired". **Win32-illegal but harmless names** (reserved device names like `aux.h` from a Linux source tree, trailing dots or spaces) are **deterministically renamed and reported**, because rejecting them would break legitimate archives; the user can switch this to Skip. Nothing is ever written under a name the archive asked for and Windows cannot represent. |
| 3 | Every write is re-verified **after** the handle is opened (`GetFinalPathNameByHandle`) to defeat TOCTOU and junction redirection. |
| 4 | Symlink and junction entries are not created by default; hardlinks are never created against out-of-destination targets. |
| 5 | Archive-bomb guards (ratio, expanded size, entry count, depth, memory) enforced from observed bytes, not declared metadata — configurable, with the user informed rather than silently blocked. |
| 6 | Passwords are never logged, never persisted, never passed on a command line, never written to temp; memory is zeroed after use. A test asserts redaction on every logging sink. |
| 7 | Extracted files inherit the archive's Mark of the Web so SmartScreen and Protected View keep working. |
| 8 | Filename spoofing (bidi overrides, homoglyphs, double extensions) is neutralised at display time; the real extension is always shown. |
| 9 | `7z.dll` is loaded by **absolute path** with `SetDefaultDllDirectories` + `LOAD_LIBRARY_SEARCH_SYSTEM32 \| LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR` — the current directory is never searched, which is the actual hijacking defence. Its SHA-256 is recorded and surfaced; a mismatch **warns rather than blocks**, because replacing the DLL is a legitimate LGPL right, and the build in use is recorded in the operation log. |
| 10 | The app runs `asInvoker`. It never requests administrator rights and never auto-elevates. |
| 11 | Temp workspaces are per-operation, ACL'd to the current user, swept on startup, and never hold decrypted content longer than needed. |
| 12 | No telemetry, no network access, and no auto-update in 1.0. `IUpdateService` exists as a seam with no URL. |

## Supported versions

No release exists yet. Once 1.0 ships, the latest release is supported; users are expected to update
`7z.dll` alongside the app when upstream patches a vulnerability.

## Reporting a vulnerability

Until a public channel is published, report privately to the project owner. Please include the archive
sample (or a generator for it), the observed behaviour, the expected behaviour, and the operation ID
from the error dialog if one was shown. Please do not open a public issue for an exploitable defect.

Expect an acknowledgement within a few days and a fix or mitigation plan before public disclosure.

## Out of scope

Attacks requiring pre-existing code execution as the user, malicious local administrators, malware
detection inside archives, and cryptographic side channels in third-party implementations.
