# Phase 2 Report — ZIP and the extraction writer

**Status:** complete, awaiting review
**Date:** 2026-08-20
**Scope delivered:** production ZIP (open, list, info, test, extract, create), the ADR-0005 extraction
writer and its new counterpart the archive publisher, and the `SEC-###` / `INT-###` / `PAR-###` suites.

---

## 1. Gate results

Every line below was produced by running the command, not by reading the code. Reproduce with
`./scripts/test.sh` (Linux, offline profile) or `.\scripts\test.ps1` (Windows).

| Gate | Result | Evidence |
| --- | --- | --- |
| BUILD | **PASS** | `dotnet build NOBA.Archive.sln -c Release` — 0 warnings, 0 errors, 18 projects |
| UNIT TESTS (Core) | **PASS** | 89/89 |
| UNIT TESTS (Security) | **PASS** | 41/41 |
| UNIT TESTS (Application) | **PASS** | 21/21 |
| UNIT TESTS (Infrastructure) | **PASS** | 19/19 |
| UNIT TESTS (Formats) | **PASS** | 18/18 |
| HOSTILE ARCHIVE + ROUND TRIP (SEC/INT) | **PASS** | 47/47 |
| ARCHITECTURE TESTS | **PASS** | 47/47 |
| CLI CONTRACT TESTS | **PASS** | 25/25 |
| PARITY (PAR) | **PASS** | 23/23 |
| **Total** | | **330 tests, 0 failures** |

**Confirmed in the shipping configuration on Windows, 2026-08-20 (R13 CLOSED).** The table above was
first produced by the offline profile on Linux; it has since been reproduced on Windows 11 build 26200
with .NET SDK 10.0.400, the real NuGet feed and real xUnit v3: 13 phases, 0 failed, 330 tests,
0 skipped. The Windows run found one real bug first — ZIP timestamps written in UTC into a field that
stores local time — which is recorded in `docs/R13_WINDOWS_VALIDATION.md` and fixed.

---

## 2. What was built

### ZIP (`NOBA.Archive.Formats.Managed.ZipArchiveProvider`)
On `System.IO.Compression`: no third-party dependency, Zip64 for free, fully streaming, `.NET 10` async
APIs throughout (`ZipArchive.CreateAsync`, `entry.OpenAsync`). Capabilities advertised are exactly what
the backend can do — `Encrypt` is deliberately **absent**, because the BCL cannot write encrypted
entries and advertising it would mean shipping an unencrypted archive to a user who asked for a
password. `ZipEncryptionProbe` reads the central directory's general-purpose bit flag so encrypted
entries are *detected* and reported even though they cannot be decrypted yet.

### The extraction writer (ADR-0005)
The one component that touches an extraction destination. Per entry: validate → refuse links → create
directories one component at a time refusing pre-existing reparse points → resolve conflicts through
the single conflict service → open a temp file in the destination directory → **re-verify the open
handle resolves inside the destination, failing closed** → stream through the bomb guard → publish
atomically → apply metadata and Mark of the Web.

### The archive publisher (new — ADR-0005 amendment)
Implementing ZIP exposed an asymmetry: ADR-0005 governed extraction only, so the provider legitimately
opened its own output file. That put `System.IO.File` back inside `NOBA.Archive.Formats`. Providers now
receive an open stream for **creation** as well as reading, and `ArchivePublisher` (Infrastructure)
stages a new archive beside its destination and publishes it on success. `NOBA.Archive.Formats` now
contains no filesystem access at all, and the architecture test proves it rather than the comment
claiming it.

### CLI (`noba-ar`)
`list`, `info`, `test`, `extract`, `create` on top of `formats` / `version` / `help`. Text on stdout,
`--json` for a single machine-readable document with human text diverted to stderr so a pipe stays
clean. Passwords are read from stdin or an ACL-checked file, never from argv or the environment. Still
a thin host: it contains no archive logic, and PAR-004/005 prove its output is the engine's, not a
re-derivation.

---

## 3. Three defects the suites found

These are the reason the phase took the shape it did. All three were in code I had already written and
believed correct.

### 3.1 The bomb guard could miss the entry entirely
`GuardedWriteStream` consults the resource guard on an amortised schedule — after each check it sets
the next checkpoint to roughly double the current size. An entry that **ends between two checkpoints is
never measured at its full size**. A 512 MB entry expanding 1029:1 (past the 1000:1 threshold) extracted
completely: every scheduled check saw a ratio still under the limit, and the run finished before the
next check came due.

Fixed by measuring explicitly once decoding completes (`VerifyFinalSize`), before anything is published.
SEC-012 and SEC-013 now fail without it.

### 3.2 The archive-level guard was configured but never called
`ResourceGuardOptions` has had `MaxTotalExpandedSize`, `MaxTotalExpansionRatio` and `MaxEntryCount`
since Phase 1, and `ResourceGuard.ObserveTotals` implements all three — but **nothing in the extraction
path ever called it**. An archive of many entries that each expand modestly, adding up to something
absurd, was invisible to the per-entry ratio check. Three configuration settings looked like
protections and were dead code.

Now wired in twice: a declared-totals pre-check before the first byte is written, and running totals
from bytes actually produced after each entry. SEC-021 covers the shape.

### 3.3 A failed create truncated the existing archive
The ZIP builder's own doc comment claimed the create handler "renames the staged file into place after a
successful completion". It did not — creation wrote straight to the target path. A create that failed
partway through an overwrite destroyed the archive it was replacing, and combined with
"compress and delete originals" that is a data-loss path, not an inconvenience. INT-015 covers it.

**Method note.** All three were found by tests written to check a *property* ("nothing exists outside
the destination", "an existing archive survives a failed create"), not by tests that re-stated the
implementation. The bomb defect in particular was invisible to any unit test of the guard, which
answers correctly when asked; the bug was that nobody asked at the right moment.

---

## 4. Test suites added

**SEC-### (hostile archives, 21 cases).** Fixtures are built with `System.IO.Compression` and by hand,
never with NOBA Archive — code under test cannot generate a traversal fixture, because it refuses to.
Covered: relative, deep, backslash, absolute-Windows, absolute-POSIX, UNC and alternate-data-stream
traversal; disguised traversal behind a safe prefix; a symlink planted in the destination chain;
single-entry and aggregate compression bombs; declared-vs-produced size; deceptive Unicode names;
degenerate names (`.`, `..`); a genuinely ZipCrypto-encrypted entry; corrupted payload; truncated
archive; an entirely hostile archive; and hostile entries surfaced in a *listing* so a UI can grey them
out before the user presses anything.

Every case asserts two things, and the second matters more: the entry was refused **and reported**, and
nothing exists outside the destination folder afterwards.

**INT-### (round trip, 16 cases).** Byte-for-byte tree round trip; Arabic, Japanese, accented and emoji
names; empty files; a 5 MB + 7 byte file deliberately not a multiple of the copy buffer; compression
actually compressing; timestamps within DOS granularity; content-based format detection through a
misnamed `.docx`; all four conflict policies; selection; flattening; monotonic progress; cancellation
publishing nothing; verify-before-delete; failed create preserving the target; overwrite refusal.

**PAR-### (dual-surface parity, 23 cases).** The same archives through the embedding-host API and
through `noba-ar` argv-in/JSON-out, comparing refusals entry for entry, resulting file trees, listings,
and exit codes. `ParityFoundationTests` proved both surfaces resolve the same objects; these prove they
reach the same *outcome*.

---

## 5. Two corrections to the project's own tooling

**The offline harness diverged from xUnit.** `Assert.Equal` used the default equality comparer, which
compares two `byte[]` by reference — so an assertion that two files had identical content failed even
when they did. That is a shim that can report a false failure and, in the mirror case, a false pass.
Now structural for sequences, matching xUnit. Also added: `ThrowsAnyAsync`, collection `Contains` /
`DoesNotContain` / `All` / `Single`, and `Assert.Skip` with the runner reporting skips as skips —
never folded into the pass count.

**`scripts/test.sh` ran `--no-build`.** It therefore tested whatever binaries happened to be on disk. A
green architecture suite earlier in this phase was produced against assemblies that predated the code
violating it; the same stale-binary trap cost time during the 7z spike. The script now builds first and
refuses to run any suite if the build fails. `test.ps1` already did.

---

## 6. Open risks

**R13 — CLOSED 2026-08-20.** Two runs on Windows; the first found the ZIP timestamp bug, the second was
clean. `scripts/test.sh` now pins a non-UTC timezone so that class of bug cannot hide in a UTC
container again.

**R14 — CLOSED 2026-08-20.** Both enumerated gaps now have executable evidence on Windows.

- SEC-011 (planted symlink) **ran and passed** — I had predicted it would skip, and was wrong.
- Mark of the Web: `MOTW-001…006` added and **all six ran** on Windows (`Skipped: 0` on the integration
  suite, which is the line to check — a green run with `Skipped: 6` would have meant nothing). Before
  this, `ReadZoneIdentifier`/`WriteZoneIdentifier` had never executed once in all of Phase 2 despite
  330 passing tests, because the shared fixture disables propagation.

The general lesson behind R14 stands and is not closable by a test: **one platform is one data point.**
Two of the 7z spike's findings were wrong until Windows corrected them, and the ZIP timestamp bug was
mathematically invisible in a UTC container. `scripts/test.sh` now pins a non-UTC timezone; that removes
one specific blind spot, not the category.

**R16 — the legacy code-page fallback is a guess, by construction.** When an archive carries no UTF-8
flag and its bytes are not valid UTF-8, the code page is unknowable from the file. NOBA Archive guesses
the machine's ANSI code page — the same guess 7-Zip and WinRAR make — and **reports that it guessed**,
with `--name-encoding` to override. A reader whose locale differs from the archive's author will still
see the wrong script until they say otherwise. Multi-byte CJK code pages have no built-in tables and
need a host-registered `CodePagesEncodingProvider`.

**R15 — encrypted ZIP is detected, not supported.** Encrypted entries are reported and refused with a
specific error. Reading them arrives with the native backend in Phase 3.

---

## 7. Proposed Phase 3 scope

7Z via `7z.dll` on the COM-lite interop the spike validated (ADR-0003 option B), encrypted ZIP and 7Z
read/write, and the same SEC/INT/PAR suites extended over both. The extraction writer does not change:
that is the payoff for having built it first.

**Recommendation:** Phase 2 is validated where it ships, and R1 is re-proven against the real `7z.dll`
(9/9, including scenario 7: the native backend wrote **zero** files to disk, which is the ADR-0005
invariant holding across the interop boundary). Per the agreed priority order the next step is the
**internal alpha** — open/list/extract ZIP on real archives — before any
further ZIP or product work.
