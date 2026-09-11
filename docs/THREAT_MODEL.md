# NOBA Archive — Security Threat Model

> **Status:** Phase 0. Every mitigation listed here has a corresponding test ID in
> [TESTING.md](TESTING.md); a mitigation without a passing test is not a mitigation.
> **Date:** 2026-08-19 · **Method:** asset/trust-boundary analysis with STRIDE per boundary

---

## 1. Why an archiver is a high-value target

An archive manager routinely opens files that arrived by email, chat, or download from a stranger, and
then writes their contents to the user's disk. It is a file-parsing engine with write privileges — the
same shape as the bugs that have been exploited in the wild against this product category:

| CVE | Product | Class | Lesson taken |
|---|---|---|---|
| `CVE-2025-8088` | WinRAR | Path traversal via NTFS **alternate data stream** entry names; exploited as a zero-day by multiple actors | The entry name is attacker data in *every* Windows-specific dimension, not just `..` |
| `CVE-2025-6218` | WinRAR | Directory traversal on extraction | Traversal defence must be canonical-path based, not string-based |
| `CVE-2025-11001` / `-11002` | 7-Zip | **Symlink** handling → directory traversal → RCE; actively exploited | A trusted backend is still a source of unsafe writes |
| `CVE-2025-55188` | 7-Zip | Symlink handling → arbitrary file write | Same |
| `CVE-2023-38831` | WinRAR | Filename/extension spoofing leading the user to execute the wrong file | Display safety is a security control, not cosmetics |

**Architectural consequence:** the backend never decides where a byte lands (ARCHITECTURE.md §5.3).
NOBA Archive's extraction writer is the only component that touches the destination filesystem, so this
whole class is defended once, in code we control and test — including against defects in `7z.dll` itself.

---

## 2. Assets

| # | Asset | Why it matters |
|---|---|---|
| A1 | The user's filesystem outside the chosen destination | Arbitrary write ⇒ persistence ⇒ code execution |
| A2 | Archive passwords and derived keys | Compromise unlocks the user's protected data |
| A3 | Archive contents (confidentiality) | Must not leak to temp files, logs, or other users |
| A4 | Archive contents (integrity) | Silent corruption is worse than a loud failure |
| A5 | Process integrity of NOBA Archive | Memory-safety / interop abuse ⇒ code execution |
| A6 | Host availability (CPU, RAM, disk) | Bombs cause DoS and can fill the system drive |
| A7 | User's trust decisions | Spoofed names cause the user to run the wrong thing |
| A8 | The install directory and shell registration | Tampering ⇒ persistent code execution as the user |

## 3. Trust boundaries

```text
   ┌──────────────── UNTRUSTED ────────────────┐
   │ archive bytes · entry names · link targets │
   │ declared sizes · CRCs · attributes         │
   │ comments · timestamps · volume references  │
   └──────────────────┬────────────────────────┘
                      │  TB1: parse
        ┌─────────────▼─────────────┐
        │ Provider (managed / 7z.dll)│   decode only, no filesystem access
        └─────────────┬─────────────┘
                      │  TB2: validate  ◄── NOBA.Archive.Security
        ┌─────────────▼─────────────┐
        │ Extraction writer          │   sole owner of destination writes
        └─────────────┬─────────────┘
                      │  TB3: write
        ┌─────────────▼─────────────┐
        │ User filesystem            │
        └────────────────────────────┘

   TB4: UI ↔ user (password entry, conflict prompts, name display)
   TB5: process ↔ native 7z.dll (interop / DLL loading)
   TB6: installer ↔ OS (shell registration, install directory ACLs)
```

**TB2 is the security core of the product.** Everything crossing it is hostile until proven otherwise.

---

## 4. Threats and mitigations

### T1 — Path traversal (A1) · *Elevation / Tampering*

**Vectors:** `..\`, `../`, mixed separators, `....\\`, over-long paths that truncate, `%2e%2e`,
Unicode look-alike separators, absolute paths (`C:\Windows\...`), drive-relative (`C:file`),
UNC (`\\server\share`), device paths (`\\?\`, `\\.\`), and NTFS **ADS** suffixes (`readme.txt:run.ps1`).

**Mitigation (`IPathPolicy`, `NOBA.Archive.Security`):**

1. Reject any entry path containing `\0`, a control character, or `:` other than a legal drive colon
   that we already reject anyway. **ADS in an entry name is always rejected** — no exceptions.
2. Normalise separators, reject rooted paths, reject any `..` segment (do not "resolve" it — reject it).
3. Reserved device names (`CON PRN AUX NUL COM1..9 LPT1..9`, including with extensions) and names with
   trailing dots or spaces are **not an escape vector — they are Win32 naming impossibilities.** They are
   therefore *deterministically renamed and reported* by default (Skip is a setting), because a Linux
   source tarball containing `aux.h` is legitimate and blocking it would be a usability defect masquerading
   as security. What is never allowed is writing under the requested name.
4. Combine with the destination root, then `Path.GetFullPath`, then verify with
   `destinationRoot` prefix comparison **on the canonicalised, `\\?\`-prefixed form**, with
   `OrdinalIgnoreCase` and a required directory separator at the boundary
   (so `C:\out-evil` is not accepted as inside `C:\out`).
5. Re-verify **after** the file handle is opened, using `GetFinalPathNameByHandle`, to defeat TOCTOU and
   junction swaps that occurred between validation and write.

**Fail mode:** the entry is skipped, the operation continues, and the entry is reported in a "Blocked
entries" summary with the reason. The user is never silently protected — they are told.
**Tests:** `SEC-001`, `SEC-002`, `SEC-003`, `SEC-012`.

### T2 — Symlink / junction / hardlink abuse (A1) · *Elevation*

**Vectors:** (a) the archive contains a symlink entry whose target escapes the destination;
(b) the archive contains a symlink *and then* a file that writes through it; (c) a pre-existing
junction in the destination redirects our writes; (d) hardlink entries targeting sensitive files.

**Mitigation:**

1. Link entries are **not created by default.** Setting: *Off (default) / Create only if the target
   resolves inside the destination / Always ask*. "Always create" is not an option.
2. Every write opens with `FILE_FLAG_OPEN_REPARSE_POINT` semantics — we never follow a reparse point we
   did not create — and every intermediate directory is created by us, not assumed.
3. After opening the handle, `GetFinalPathNameByHandle` must still resolve inside the destination
   (this is what defeats vector (b) and (c)).
4. Directory creation is performed one component at a time, rejecting any component that already exists
   as a reparse point unless the user explicitly extracted into that path.
5. Hardlink entries are converted to regular file copies or skipped; never created against an
   out-of-destination target.

**Tests:** `SEC-004`, `SEC-005`, `SEC-013`.

### T3 — Decompression bombs and resource exhaustion (A6) · *DoS*

**Vectors:** 42.zip-style nesting, single-entry ratio bombs, millions of tiny entries, absurd declared
dictionary sizes, quines, deeply recursive archives.

**Mitigation — heuristics with user override, never a hard wall:**

| Guard | Default | Behaviour on trip |
|---|---|---|
| Expansion ratio per entry | > 1000:1 | pause and ask, showing the observed ratio |
| Total expanded size vs archive size | > 500:1 | pause and ask |
| Total expanded size vs free disk | exceeds free space | block with the disk-space dialog |
| Entry count | > 1,000,000 | warn, continue on confirmation |
| Path depth | > 64 | reject entry |
| Declared dictionary / memory need | > 25% of physical RAM | ask before allocating |
| Nested-archive auto-open depth | 2 | refuse deeper auto-open |

All limits are settings. A legitimate 200 GB VM image inside a 7z must not be blocked — the user is asked
once, informed of the numbers, and can proceed. Counters are enforced **during** streaming, not from
declared metadata, because declared sizes are attacker-controlled.
**Tests:** `SEC-008`, `SEC-009`, `SEC-011`.

### T4 — Password and key exposure (A2) · *Information disclosure*

**Mitigations:**

- `Password` is a dedicated type over pooled, pinned memory, zeroed on `Dispose`, with a sealed
  `ToString()` ⇒ `***` and `[JsonIgnore]`/non-serialisable by construction. `SecureString` is not used —
  it is no longer recommended for new development and offers no real protection on modern .NET.
- Never persisted. An in-memory, per-session cache keyed by archive path with a short idle timeout
  prevents re-prompting; it is cleared on window close, session end, and lock/sleep.
- The redacting logger provider (ARCHITECTURE.md §10) is in front of every sink, and a test asserts
  a password passed to `ILogger` does not appear in the output.
- Passwords never reach the command line of a child process (a further reason to prefer in-process
  `7z.dll` over shelling out to `7z.exe`) and never land in temp files.
- Crash dumps: `Environment.FailFast` paths scrub the cache first; minidump collection is off by default.
- Password strength is shown at entry, with a clear statement that ZIP AES-256 and 7z AES-256 key
  strength is bounded by the password.

**Tests:** `SEC-014` (redaction), `SEC-015` (no persistence), `SEC-016` (zeroing).

### T5 — Malicious / malformed metadata (A4, A5) · *Tampering, DoS*

Declared sizes, CRCs, entry counts, and offsets are never trusted for allocation or for loop bounds.
Sizes are used only as *hints* for progress; buffers are pooled and fixed. Every parse path has a
fuzz-tested entry point (Phase 7). CRC/hash verification failures produce `CorruptData`, and a partially
extracted file is deleted rather than left in place.

**Tests:** `SEC-010`, `SEC-017`, fuzz corpus in Phase 7.

### T6 — Filename spoofing at the point of user trust (A7) · *Spoofing*

Right-to-left override (U+202E) and other bidi controls, homoglyphs, double extensions
(`invoice.pdf.exe`), and very long names are rendered so the user sees reality:

- Bidi control characters are stripped from display and replaced with a visible marker; the raw name is
  shown in the details pane.
- The **real** extension and file type are shown in their own column, derived from the actual name tail.
- Executable content types get a distinct icon treatment and a confirmation before *Open from archive*.
- Homoglyph/mixed-script names in the same directory are flagged.

Note that Arabic UI is RTL by design; the mitigation targets *embedded bidi control characters inside a
filename*, not Arabic text, which must render correctly and normally. **Tests:** `SEC-007`, `INT-021`.

### T7 — Mark of the Web loss (A7) · *Spoofing / bypass*

If the archive carries a `Zone.Identifier` ADS (downloaded from the internet), every extracted file
receives the same zone. This is what makes SmartScreen and Office Protected View work downstream.
Losing MOTW on extraction is treated as a **security defect**, not a feature gap. **Test:** `SEC-018`.

### T8 — Native interop abuse and DLL hijacking (A5, A8) · *Elevation*

- `7z.dll` is loaded by **absolute path** from the install directory with
  `LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR`; the current directory is never in
  the search path, and `SetDefaultDllDirectories` is called at startup.
- The shipped DLL's SHA-256 is pinned in the installer manifest and checked at first load; a mismatch
  produces a clear warning (the user may legitimately have replaced it, per LGPL — so it warns, it does
  not hard-fail, and it records which build is in use in the operation log).
- All interop is `SafeHandle`-based; no raw `IntPtr` ownership, no `unsafe` outside the interop project.
- Install directory is `%ProgramFiles%` with default ACLs (non-admin users cannot write to it).
- The app requests `asInvoker` — never `requireAdministrator`, never auto-elevation.

**Tests:** `SEC-019`, packaging test `PKG-004`.

### T9 — Temp-file leakage (A3) · *Information disclosure*

Per-operation directory under `%LOCALAPPDATA%`, ACL'd to the current user, never in `%TEMP%` shared
space, deterministic cleanup, startup sweeper for orphans, and — most importantly — **preview and
"open from archive" do not extract more than the single requested entry**. Encrypted archives never leave
decrypted material in temp after the viewer closes. **Tests:** `SEC-020`, `SEC-021`.

### T10 — Destructive-operation mistakes (A4) · *Tampering by accident*

"Delete source files after archiving" runs **only after** the created archive has been re-opened, listed,
and integrity-tested; a failure anywhere aborts the delete. Overwrite never happens under any policy the
user did not choose. Files are sent to the Recycle Bin where possible, not hard-deleted.
**Tests:** `APP-030`, `APP-031`.

### T11 — Supply chain (A5, A8) · *Tampering*

Central package management with pinned versions and a committed lock file, NuGet package signature
verification enabled, dependency review before any addition (DEPENDENCIES.md), `dotnet list package
--vulnerable` in CI, SBOM generated at release, and code-signing readiness for the app, the installer,
and the shell extension. No dependency is added without a licence and maintenance check.

### T12 — Update channel (deferred, A5)

Auto-update is **disabled in 1.0**. The `IUpdateService` seam exists with no URL. When enabled, the
design requires: HTTPS-only, signature verification of the payload before execution, no auto-install
without consent, and rollback on failure. Shipping an unverified updater is a worse vulnerability than
having no updater.

---

## 5. Explicitly out of scope

- Defending against an attacker who already has code execution as the user.
- Defending against a malicious local administrator or a compromised OS.
- Anti-malware scanning of archive contents (we set MOTW and stay out of AV's way).
- Side-channel resistance of the underlying crypto implementations in `7z.dll`.
- Protecting archive contents from the user who owns them.

## 6. Security acceptance gate

NOBA Archive does not ship 1.0 unless:

- [ ] Every `SEC-###` test in [TESTING.md](TESTING.md) exists and passes.
- [ ] A fuzz run over ZIP/7Z/TAR/RAR parsing completes with no crash and no unhandled exception.
- [ ] No path in the codebase writes to the destination outside the extraction writer (architecture test).
- [ ] The redaction test passes for every logging sink.
- [ ] Extraction is verified to preserve MOTW.
- [ ] `7z.dll` is at the latest patched upstream version, with its CVE history reviewed at release time.
- [ ] A written security review of the extraction writer has been completed and signed off (Phase 7).
