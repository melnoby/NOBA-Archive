# NOBA Archive — Product Execution Plan (Drops 01–15)

> **What this file is.** The single tracker for taking NOBA Archive from its current Feature 02 state
> to a release candidate. One row per drop, one status per row, and a rule that a status only moves
> on evidence. It is meant to be read at a glance and updated at the end of every drop.
>
> **Owner:** m.a.elnoby · **Created:** 2026-08-26 · **Baseline drop:** `2026-08-26-drop-01-product-baseline` · **Current drop:** `2026-10-18-drop-09-operations-center-concurrency`

---

## 1. How to read and update this file

### Status legend

| Symbol | Meaning |
|:--:|---|
| `TODO` | Not started. No code, no tests. |
| `WIP` | In progress. Do not treat any part of it as delivered. |
| `LINUX` | Implemented; automated suite green on the build machine. **Not** proven on Windows. |
| `WINDOWS` | Additionally proven by a real Windows run, with a transcript in `artifacts/`. |
| `DONE` | Windows-proven **and** its exit criteria are all met. |
| `BLOCKED` | Waiting on something named in the Blocker column. |
| `DEBT` | Deliberately deferred. Recorded in §6, not silently dropped. |

### The rule that makes the file worth keeping

**A status only moves when evidence exists.** `LINUX` needs a suite total in §5. `WINDOWS` needs a
named transcript file. Nothing is marked `DONE` because it looks finished, and nothing that was
executed only on Linux is ever recorded as Windows-proven. This project has already spent whole
sessions on an empty folder that was read as proof; the discipline is the point.

### End-of-drop ritual

1. Bump `BuildStamp` if implementation changed.
2. Run `bash scripts/test.sh Release` and `python3 scripts/analyzer-preflight.py`.
3. Deliver the changed files; the human runs `scripts\validate.ps1`, `scripts\create-gate.ps1`
   (when the drop touched creation) and `scripts\app.ps1`.
4. **Save the transcripts those scripts write into `artifacts/`**, then append a row to §5 with real
   numbers and the real filename. A number quoted from a chat message is not evidence.
5. Update the drop's status and its **Remaining** column here.
6. Move anything deferred into §6.
7. **Stop.** The next drop starts only when explicitly instructed.

---

## 2. Verified baseline — 2026-08-26

Everything here was measured, not assumed. Where a fact is Windows-only and unverified, it says so.

### Architecture (unchanged, and to be preserved)

```
Core ──────────── contracts, primitives, operation model        (no dependencies)
 ├── Security ─── ResourceGuard, path policy
 ├── Windows ──── Mark of the Web, handle verification
 ├── Application  handlers, OperationQueue, PauseToken, PausableStream
 ├── Infrastructure  IFileSystem, extraction writer (ADR-0005)
 ├── Formats ──── managed ZIP provider, registry, magic-byte detector
 ├── Backends.SevenZip  native 7z.dll interop
 ├── Engine ───── composition root
 ├── Presentation  view models, bidi, design tokens (builds on Linux)
 └── App ──────── WinUI 3, Windows-only, cannot compile on the build machine
```

### Formats actually registered today

Read from `noba-ar formats` on the current build — not from the 1.0 target matrix in
`FORMAT_SUPPORT.md`, which is a plan and not a claim.

| Format | Provider | Capabilities reported by the running engine |
|---|---|---|
| **7Z** | native | Open, ListEntries, Extract, Create, AddEntries, Test, Encrypt, EncryptFileNames, SolidCompression, ReadSplitVolumes, RandomAccessRead, ContentIntegrityCheck |
| **ZIP** | managed | Open, ListEntries, Extract, Create, AddEntries, Test, RandomAccessRead, StreamingRead, ContentIntegrityCheck |
| **TAR** | managed *(new in DROP 05)* | Open, ListEntries, Extract, Create, AddEntries, Test, StreamingRead, PreservesUnixMode |
| **GZIP** | managed *(new in DROP 05)* | Open, ListEntries, Extract, Create, Test, StreamingRead, ContentIntegrityCheck |
| **RAR** | native | Open, ListEntries, Extract, Test, SolidCompression, ReadSplitVolumes, RecoveryRecord, ContentIntegrityCheck *(read-only, permanently — ADR-0004)* |

Generated from `noba-ar formats --json`; the full matrix is `docs/FORMAT_CAPABILITY_MATRIX.md`.

**BZIP2 and XZ remain BLOCKED**, and `.tgz`/`.tbz2`/`.tbz`/`.txz` are DEFERRED — reasons in
`docs/DROP05_AUDIT.md` §5. TAR and GZIP were implemented on the BCL (`System.Formats.Tar`,
`System.IO.Compression`), so **no third-party dependency was added**.

### The capability contract (added in DROP 02)

`FormatCapabilityReport` (`Core/Formats`) is the one thing the product asks about a format. It is a
projection of the provider's own `ArchiveCapabilities` flags — it stores no rules of its own, so it
cannot disagree with the engine.

| Property | Derived from | Answers |
|---|---|---|
| `CanOpen` | `Open` | may this file be opened at all |
| `CanBrowse` | `ListEntries` | may its contents be listed |
| `CanTest` | `Test` | may it be integrity-tested |
| `CanExtract` | `Extract` | may it be extracted |
| `CanCreate` | `Create` | may a new one be written |
| `CanAdd` / `CanRemove` / `CanRename` | `AddEntries` / `RemoveEntries` / `RenameEntries` | may an existing one be modified |
| `CanUpdate` | `AddEntries` **and** `RemoveEntries` | may it be updated in place |
| `CanPasswordProtect` | `Encrypt` | may it be encrypted |
| `CanEncryptEntryNames` | `EncryptFileNames` | may the listing itself be hidden |
| `CanSplit` | `SplitVolumes` | may volumes be written |
| `CanCompressSolid` | `SolidCompression` | is solid compression meaningful |
| `CanConfigureCompression` | `CanCreate` | are compression options meaningful |
| `IsReadOnly` | `CanOpen && !CanCreate && !CanAdd` | is this format readable only |

**One deliberate deviation from the requested list.** `CanEncrypt` and `CanPasswordProtect` would be
the same flag in every provider we have or plan. Two properties that must always agree are two
properties that will eventually disagree, so encryption is modelled once, as `CanPasswordProtect`.

Current report, derived from the table above:

| | 7Z | ZIP | RAR |
|---|:--:|:--:|:--:|
| Open / Browse / Test / Extract | yes | yes | yes |
| Create | yes | yes | **no** |
| Add | yes | yes | no |
| Remove / Rename / Update | no | no | no |
| Password protect | yes | no | no |
| Encrypt entry names | yes | no | no |
| Solid | yes | no | yes *(read)* |
| Split volumes | no | no | no *(read only)* |
| Read-only | no | no | **yes** |

`ARC-055` forbids the WinUI project from making any of these decisions for itself: no source or
markup file in `src/NOBA.Archive.App` may name a format constant, test a capability flag, or compare
against a format name or extension. `ARC-056` is its negative control.

### Operations proven to exist

| Capability | State | Proven by |
|---|---|---|
| Open / browse archive | Working | Windows runtime, screenshots |
| Test archive | Working, **and now an operation** | Windows runtime for the verdict; ATF-020…022 for the queue integration added in DROP 01 |
| Extract all → chosen folder | Working | Windows runtime — 3,800 files / 950 MB / 0:47 |
| Progress reporting | Working | Windows runtime |
| Pause / Resume | Working | Windows runtime — paused at 18% / 172 MB / 689 entries |
| Cancel → state `Cancelled` | Working | Windows runtime |
| **Cancel → disk writes stop** | **Unverified on Windows** | Automated only (OPX-006, OPX-016) |
| Clear finished | Automated only | OPX-011 |
| Destination reporting | Implemented this drop | OPX-015; not yet seen on Windows |
| Arabic / RTL, Latin digits | Working | Windows runtime, both languages |
| ResourceGuard refusal | Working | 748:1 fixture refused before a byte was written |
| Batch + fallback extraction | Working | OPX-005/006/013/016, with negative controls |
| **Create archive → queue** | Implemented this drop | CRT-001…013, UI-040…054; **not yet seen on Windows** |
| **Create → pause inside one large file** | Implemented this drop | `PausableStream` read gating; DEBT-02 still applies |
| **Queue admission order** | Fixed in DROP 02 | OPQ-021, with a negative control |
| **Extraction conflict policy** | Implemented in DROP 04 | EXT-005…010 against a real disk; UI-060 for the seam |
| **Cancelling state** | Implemented in DROP 04 | OPQ-022/023 with a negative control; UI-066/067 |
| **Mark of the Web from the window** | **Repaired in DROP 04** | ARC-057 (source rule — see §2 note) |

### Test totals — build machine, this drop

| Suite | Passed | Skipped |
|---|--:|--:|
| Core | 95 | 0 |
| Security | 41 | 0 |
| Application | 35 | 0 |
| Infrastructure | 19 | 0 |
| Formats | 18 | 0 |
| Integration | 161 | 6 *(Mark-of-the-Web needs NTFS)* |
| Architecture | 57 | 0 |
| Cli | 26 | 0 |
| Parity | 23 | 0 |
| Presentation | 167 | 0 |
| **Total** | **642** | **6** |

Build: 0 warnings, 0 errors. `analyzer-preflight`: PASS (CS0103 / CS0104 / **CS1061** / CA1822 /
CA1859 / shadowing).

**How the runner counts.** Each suite prints `passed/ran`, where `ran = discovered − skipped`; a
skipped test is counted as neither a pass nor a failure. So for this run:

| | |
|---|--:|
| Discovered | **648** |
| Ran | **642** |
| Passed | **642** |
| Failed | **0** |
| Skipped | **6** |

The ten rows above sum to 642 exactly. Log: `artifacts/test-20260826-205122/`.

**Correction to the DROP 01 row of this table (was wrong).** Its per-suite numbers were right and its
`Total` cell said **536**, which is not what those rows add up to — they add up to **543**. The
accompanying sentence then attributed 543 to *Windows*, which compounded it. The logs settle both:

| Run | Linux passed | Skipped | Log |
|---|--:|--:|---|
| Feature 02 — destination observability | 537 | 6 | `artifacts/test-20260826-152913/` |
| Feature 02 — FlowDirection ambiguity | 539 | 6 | `artifacts/test-20260826-163307/` |
| DROP 01 close | **543** | 6 | `artifacts/test-20260826-163614/` |
| DROP 02 close | **574** | 6 | `artifacts/test-20260826-175634/` |

The Windows figure of 543 came from a run made at 15:15, when Linux stood at 537 — and 537 + 6 is
543, because the six Mark-of-the-Web tests skip on Linux and run on NTFS. That is the most likely
reading, not a proven one: **the transcript it cites, `r13-windows-validation-20260826-151539.txt`,
is not in `artifacts/`.** It was pasted into a conversation and never saved, so by this file's own
evidence rule that Windows row is unbacked and is marked so in §5.

Two counting defects were behind the confusion above, and both are fixed rather than explained:
`scripts/test.sh` printed nine summary lines while running ten suites — the Presentation row was
missing from the label array, so a Presentation failure set the exit code but never appeared in the
summary. And `validate.ps1` already sums the Windows totals itself, so the Windows number has not
been added up by hand since it was written. The Linux total is now taken from the per-suite logs
rather than transcribed.

### Known limitations at baseline

| # | Limitation | Feeds |
|--:|---|---|
| ~~L1~~ | ~~No Create screen and no Settings screen.~~ **Create closed in DROP 02**: `CreatePage` is a real screen and `NavCreate` navigates to it. Settings remains absent and its nav item stays disabled with the "Not built yet" tooltip. | DROP 10, 14 |
| ~~L2~~ | ~~No TAR / GZIP / BZIP2 / XZ provider.~~ **TAR and GZIP implemented in DROP 05** on the BCL, no new dependency. BZIP2 and XZ remain **BLOCKED** (`docs/DROP05_AUDIT.md` §5) | a later drop |
| L3 | No archive modification: add, remove, rename, update are absent from the engine handlers. The capability report already says so — `CanAdd` is true for 7Z/ZIP at the *provider* level, but no handler exists, so `CanUpdate` is false everywhere | DROP 03 |
| L4 | No search or filter inside an open archive | DROP 07 |
| L5 | No Explorer / shell integration; no installer beyond the alpha publish | DROP 08 |
| ~~L6~~ | ~~Extraction conflict policy exists in the engine but the UI never offers it.~~ **Closed in DROP 04**: chosen in the extract dialog, carried through the seam, one list for every surface. | — |
| L7 | Split archives are read-only; no writing of volumes | DROP 05 |
| L8 | `artifacts\alpha\noba-ar.exe` is refreshed only by `alpha.ps1`; `validate.ps1` warns when stale | operational |
| L9 | 173 `xUnit1051` warnings in Feature 00/01 test projects; visible, counted, not errors | DEBT-01 |
| L10 | Creation offers no password field. The engine encrypts (7ZC-007/008) and `CreateOptionsViewModel` gates the option correctly, but the Create screen has no way to type a password, so encryption is unreachable from the UI | DROP 06 |
| L11 | Creation offers no split-volume control. No provider advertises `SplitVolumes`, so there is nothing honest to offer yet | DROP 05 |
| L12 | The Create screen has no drag-and-drop and no "compress here" entry point; inputs are added through pickers only | DROP 08, 10 |
| L13 | Disk-full, permission-denied and locked-source behaviour during creation is **not covered by an automated test**. All three need a real filesystem state the build machine cannot produce, and a fake `IFileSystem` would prove only that the fake throws | DROP 12 |
| L14 | Creation **creates the destination folder** if it does not exist. Deliberate — a typed folder name is an instruction — but it means "invalid destination" only covers paths that cannot exist at all (CRT-010) | — |
| L15 | **Cancellation stopping physical disk writes is still unproven on Windows.** OPX-006/016 watch the destination on Linux and OPQ-022/023 prove the state machine, but nobody has watched an NTFS folder stop changing after Cancel. `scripts/watch-destination.ps1` exists for exactly this and has not been run against an extraction | manual gate G/H |
| L16 | The extract dialog offers no "Extract here" shortcut and no remembered destination; every extraction names a folder | DROP 08, 10 |
| L17 | Disk-full and permission-denied **during extraction** have no automated test, for the same reason creation's do not: the build machine cannot produce the filesystem state, and a fake `IFileSystem` would prove only that the fake throws | DROP 12 |
| L19 | **BZIP2 and XZ are detected and cannot be opened.** No BCL codec exists and no third-party dependency has been approved; extending the native interop would make the capability depend on which handlers the installed `7z.dll` carries | next format drop |
| L20 | **`.tgz`, `.tbz2`, `.tbz`, `.txz` do not open.** They are a TAR inside a compressor and need a layered session; the extensions are deliberately no longer claimed by the plain compressors, which is a behaviour change from before DROP 05 | next format drop |
| L21 | **GZIP cannot store a non-Latin-1 file name.** RFC 1952 §2.3.1 defines the field as ISO-8859-1, so an Arabic or Japanese name is not written and the reader falls back to the archive's own name minus `.gz`. Content always round trips exactly; only the name is lost, and only for names the format cannot represent | — |
| L22 | **A TAR test cannot verify contents.** The format has no checksum over file data. Modelled as `ArchiveCapabilities.ContentIntegrityCheck` and said on screen, rather than left for the reader to assume | — |
| L23 | The registry **unions** capabilities across providers claiming the same format, so `CapabilitiesFor(Zip)` reports what *some* provider can do rather than what the resolved one will. Defensible and currently harmless — no pair disagrees in a way that matters — but it would mislead once two providers of one format differ | a later drop |
| L18 | Two archive entries that map to the same destination path are handled by the conflict policy — the second is a collision with the first. Correct, and covered by EXT-006…008, but it means "keep both" produces a renamed second copy rather than reporting the archive as malformed | — |
| L24 | `PathKey` compares path text, not identity. Two paths reaching one file through a junction, `subst` drive or UNC alias are listed twice, and the archive holds the contents twice | DROP 06 |
| L25 | Drop is Create-only. Dropping an archive onto Browse to open it is not implemented | DROP 06 |
| L26 | No drag *out*: entries cannot be dragged from NOBA Archive to Explorer | DROP 06 |
| L27 | The drag cursor shows "copy" over items that will then be refused by name. Deciding earlier would mean touching the disk on every pointer move | DROP 06 |
| L28 | **ADR-0006's shell registration mechanism is not built.** No WiX MSI, no sparse MSIX package, no `IExplorerCommand` server, no signing certificate. The interim classic handler lands under "Show more options" on Windows 11 | a packaging drop |
| L29 | Registry menu labels are English only. A `MUIVerb` holds one string; per-language labels need the runtime handler | with L28 |
| L30 | No single instance. Each shell action starts a new process; `ShellCommandLine.Parse` is the seam a future activation redirection would feed | a later drop |
| L31 | Batch archive actions are absent. Open, Test and both Extracts require exactly one archive | a later drop |
| L32 | `Compress to` does not avoid a name collision. The engine refuses an occupied target through the existing overwrite contract rather than the menu inventing "name (2).7z" | by design |
| L33 | **Concurrent operations against the native 7Z/RAR backend are unverified.** DROP 09 proved `OperationQueue` and the real `CreateArchiveHandler`/`ExtractArchiveHandler` safe under genuine concurrency for the managed ZIP provider (OPQ-025…027, OPX-017…019) and the `EngineWorkspace` outcome-parking pattern in isolation — but `7z.dll` is not present on the Linux build machine, so whether two simultaneous `IInArchive`/`IOutArchive` handler objects from `SevenZipLibrary.CreateObject` are safe has no automated coverage anywhere, and `EngineWorkspace` itself (`NOBA.Archive.App`, Windows-only) has never been exercised under concurrency either. The shipped default stays `MaxConcurrentOperations = 1` partly for this reason | a Windows concurrency gate |

---

### The DROP 02 Windows gate — what was executed

Run on 2026-08-26 at 19:26. The engine half is closed; the screen half is not, because `app.ps1` was
not run. `validate.ps1`: 580 discovered / 574 ran / **0 failed** / 6 skipped → on NTFS the six
Mark-of-the-Web tests run, giving 580 passed. `create-gate.ps1`: **19 passed, 0 failed, 0 skipped**.

| # | Check | Proven by | Status |
|--:|---|---|:--:|
| 1 | Solution compiles on Windows (**the WinUI app is not in this solution**) | `validate.ps1` | **PASS** |
| 1b | WinUI app project compiles | `app.ps1` | OPEN |
| 2 | Application starts | `app.ps1` | OPEN |
| 3 | Browse an existing archive | eyes | OPEN |
| 4 | Test an archive | eyes | OPEN |
| 5 | Create nav item is enabled and opens the screen | eyes | OPEN |
| 6 | Create 7Z/ZIP from file / files / folder / Arabic / Unicode | `create-gate.ps1` (engine) **+** eyes (screen) | **engine PASS**, screen OPEN |
| 7 | Destination picker returns a folder | eyes | OPEN |
| 8 | Format list shows only creatable formats | `create-gate.ps1` reported 7z, zip creatable; screen OPEN | partial |
| 9 | Compression profile selection | eyes | OPEN |
| 10 | Progress appears in Operations | eyes | OPEN |
| 11–13 | Pause / Resume / Cancel | eyes | OPEN |
| 14–15 | Completed and Failed states are correct | eyes | OPEN |
| 16 | Cancellation is never reported as success | eyes | OPEN |
| 17 | Arabic / RTL layout | eyes, both languages | OPEN |
| 18 | The row says "Creating", not "Extracting" | eyes | OPEN |
| 19 | Operation destination is visible | eyes | OPEN |
| 20 | Existing-archive collision behaves as contracted | `create-gate.ps1` **+** eyes | **engine PASS**, screen OPEN |
| B | Archive exists, non-zero, right extension, re-opens, tests Valid, names survive | `create-gate.ps1` | **PASS** |

Transcripts: `artifacts/r13-windows-validation-20260826-192638.txt`,
`artifacts/create-gate-20260826-192833.txt` — **both must be committed to `artifacts/`**, or by this
file's own rule the rows above are unbacked.

`create-gate.ps1` covers the engine half of 6, 20 and all of B by building real archives and reading
them back. It cannot cover the screen: the pickers, the progress strip, the state badge and RTL need
the running app. A SKIP it prints is not a pass.

---

## 3. Drop order, dependencies and gates

| # | Drop | Depends on | Primary risk | Status | Remaining |
|--:|---|---|---|:--:|---|
| 01 | Product Baseline | — | none; audit only | `LINUX` | WinUI build + Windows cancel-on-disk + Clear finished still manual |
| 02 | Creation Engine | 01 | L1 — there is no Create screen to build on | `WINDOWS` (engine) / `LINUX` (screen) | WinUI build + the screen half of the gate; password field (L10) deferred to 06 |
| 03 | Archive Management | 02, 05¹ | honest capability reporting | `TODO` | — |
| 04 | Production Extraction | 01 | conflict policy UX | `LINUX` | WinUI build + the extraction gate below |
| 05 | Format & Capability Expansion | 01 | four providers do not exist yet | `LINUX` | WinUI build + the Windows format gate; BZIP2/XZ and composite tar BLOCKED by design |
| 06 | Security | 04, 05 | adversarial corpus needs building | `TODO` | — |
| 07 | Archive Intelligence (no AI) | 05 | large-archive listing performance | `TODO` | — |
| 08 | Shell Integration | 02, 04, packaging | needs a real installer; Windows-only verification | `TODO` | — |
| 09 | Operations Center | 02, 03, 04 | concurrency; queue is currently `MaxConcurrentOperations = 1` | `LINUX` | WinUI build + a Windows run; native-backend concurrency (L33) remains unverified by design |
| 10 | Windows UX | 02, 04, 07, 09 | scope creep | `TODO` | — |
| 11 | Performance and Scale | 05, 07 | must measure, not guess | `TODO` | — |
| 12 | Recovery and Failure Safety | 03, 04, 06 | fault injection needs a seam | `TODO` | — |
| 13 | Release Security Audit | 06, 12 | hostile review of everything | `TODO` | — |
| 14 | Product Polish | 10 | must not redesign | `TODO` | — |
| 15 | Release Candidate | all | evidence, not optimism | `TODO` | — |

¹ **Recommended adjustment, not a change to your order.** DROP 03 (management) needs the provider
capability model that DROP 05 defines, otherwise the UI has to hardcode "7z can add, RAR cannot" —
exactly the scattered format checks DROP 05 forbids. Two options, your call at the time:
either pull the *capability contract* (not the new providers) forward into DROP 02, or run 05 before
03. Everything else follows your order as written.
**Resolved:** the capability contract was pulled forward into DROP 02 as `FormatCapabilityReport`,
so DROP 03 no longer depends on DROP 05 for it. The dependency column above is left as written
because DROP 03 still benefits from more than three formats to be honest about.

**A second dependency worth naming now:** DROP 08 needs packaging that does not exist yet
(ADR-0006 specifies a sparse package for the shell menu). Either DROP 08 absorbs the installer work
or it is lifted into its own drop when we reach it.

---

## 4. Per-drop contracts

Each drop is finished when its **exit criteria** are met — not when the code compiles.

### DROP 01 — Product Baseline
- **Scope:** audit only. WinUI build, preflight, open/browse, test, extraction, progress, pause,
  resume, cancel, destination, history, clear finished, Arabic/English, ResourceGuard, both
  extraction paths, existing tests. Fix real defects found; record optional gaps.
- **Exit:** this document's §2 verified and signed off; WinUI build PASS on Windows; no known
  unfixed defect in the audited list.
- **Evidence:** `validate.ps1` transcript + `app.ps1` transcript + this file.

### DROP 02 — Creation Engine
- **Scope:** Create screen (closes L1), files + folders, multi-selection, destination and name,
  collision policy, compression level/method/store, recursion, empty directories where the format
  keeps them, Unicode and Arabic, long paths, large input sets, progress, pause, cancel, partial
  output cleanup, disk-full, permission and lock failures.
- **Must not:** create a second engine; `CreateArchiveHandler` and the existing provider builder are
  the only creation path.
- **Exit:** creation runs through the Operation Queue reporting source, destination, state, progress,
  bytes, files, errors, result; tests for normal, empty directory, Arabic, large input, cancellation,
  failure, collision, invalid destination; no timing-based assertions.

#### The Create workflow as built

1. **Choose inputs.** Add files (multi-select) or a folder; the same path twice is added once, case-
   insensitively. Remove one, or clear all.
2. **Choose a destination folder** and **type a name.** The extension is *not* what was typed — it is
   the chosen format's, so naming an archive `backup.zip` and picking 7Z produces `backup.7z`.
3. **Choose a format.** The list is the formats this build can genuinely write, asked of the provider
   registry: no native `7z.dll`, no 7Z in the list. RAR is never in it (ADR-0004).
4. **Choose a compression profile** — Fast, Balanced, Maximum compression, Smallest size. `Custom` is
   deliberately absent: it is what the model *reports* after a manual level, not something to pick.
5. **Solid** is offered only when the format supports it, disabled with a reason when it does not, and
   cannot be switched on regardless — a ticked box the backend ignores is worse than no box.
6. **Start.** The button is enabled only when nothing blocks it, and the line beneath it names the
   blocker: no inputs, no destination, no name, or nothing this build can create.
7. **Watch.** The creation is enqueued on the same Operation Queue as extraction and testing, and
   appears in the same history, typed `Create`, carrying its destination. Pause is offered because the
   create handler genuinely honours it — between entries *and* inside a single large file, through the
   `PausableStream` wrapping each source.
8. **Result.** Completed, Cancelled or Failed, read back from the queue rather than inferred. A null
   outcome means "did not complete", which is two different things to a user; reporting a cancellation
   as a failure sends somebody looking for a fault that was their own decision.

**Collision.** An existing archive is refused before a byte is compressed, reported as a collision
rather than a mysterious failure, and replaced only after the user ticks Overwrite. That consent is
spent on success and reset whenever the target changes, so agreeing to replace one file never carries
silently to the next.

**Formats this build can create:** 7Z (when `7z.dll` is present) and ZIP. Nothing else — and the list
is derived, not written down, so it shrinks honestly on a machine missing the native library.

### DROP 03 — Archive Management
- **Scope:** add files/folders, update, replace, remove, rename, move where supported; integrity
  preserved; test-after-modify where appropriate; UX distinguishes in-progress / success / cancelled
  / failed / partial.
- **Must not:** fake a capability a provider does not have.
- **Exit:** every operation gated on real provider capability; integration and negative tests;
  a partial failure is reported as partial, never as success.

### DROP 04 — Production Extraction
- **Scope:** extract all / selected / here / to…; folder picker; destination display; overwrite,
  skip, rename conflict policy in the UI; nested directories, Unicode, Arabic, long paths, large
  files and archives; progress, pause, resume, cancel.
- **Exit:** cancellation is real — state `Cancelled`, no false `Completed`, **no continued disk
  writes**, deterministic partial-output behaviour; pause stops output, not just the badge; both
  extraction paths tested; the Windows cancel-on-disk gate from DROP 01 closed here if still open.

#### The extraction workflow as built

1. **Extract all** or **Extract selected** opens one dialog rather than a bare folder picker. It shows
   the archive, what is being extracted, the destination once chosen, and what should happen to a file
   already there. Nothing is written until Extract is pressed, and the button stays disabled until a
   folder has actually been named.
2. **The conflict policy is one product decision, listed once** in `ExtractPlanner.Policies` and passed
   through the workspace seam as a `ConflictPolicy` — the same value `noba-ar --conflict` takes. A
   future "NOBA → Extract to…" verb calls the same method with a policy of its own; there is no second
   place to add one.

   | Policy | What happens | Existing file |
   |---|---|---|
   | **Ask** *(default)* | one dialog per collision, with "apply to all" | untouched unless the person says otherwise |
   | Keep the existing file | entry skipped, counted in `EntriesSkipped` | untouched |
   | Keep both | the incoming entry gets a new name | untouched |
   | Replace | the entry is overwritten | **replaced** |
   | Stop | the operation fails at the first collision | untouched; what was written before it stays |

   A destination **directory** that already exists is never a conflict — that is the normal case. Only
   a colliding **file** is a decision.
3. **Cancelling** is now a state of its own. Pressing Cancel moves the badge immediately, the writer
   unwinds, and the badge becomes Cancelled when it has. Work that runs to the end after Cancel was
   pressed is still reported Cancelled — never Completed.
4. **Partial extraction is a fact, not a failure to hide.** Files written before a cancellation or a
   Stop remain; no temporary file survives under a name that looks like content.

### DROP 05 — Multi-Format Platform
- **Scope:** audit and complete provider support; build the provider capability model; TAR / GZIP and
  the other formats the architecture already intends.
- **Must not:** scatter format checks through WinUI; claim unsupported functionality.
- **Exit:** a capability model the UI consumes to enable/disable operations; architecture test
  forbidding format checks in the App project; per-format integration tests; `FORMAT_SUPPORT.md`
  updated so "Supported" means "a test proves it".

### DROP 06 — Drag & Drop Experience

> **Numbering note.** This plan already had a "DROP 06 — Security" below, written before the drag and
> drop work was scheduled. The delivered DROP 06 is this one; the security drop keeps its content and
> is renumbered when it is scheduled. Two sections briefly share a number rather than a plan quietly
> disagreeing with the build stamps.

- **Objective:** drag files and folders from Windows Explorer into the Create screen, as a native
  desktop gesture rather than a second way to run the picker.
- **Architecture:** `CreatePage` resolves `DataPackageView` → `StorageItems` → `IStorageItem.Path`
  and hands strings to `CreateViewModel.AcceptDrop`. Every rule lives in the view model, which has
  no WinUI dependency and is tested where WinUI cannot be built. No second creation pipeline: a
  dropped path and a picked path are indistinguishable by the time the engine sees them.
- **Accepted inputs:** one file, many files, one folder, many folders, and any mixture. A folder
  becomes exactly one input — `ARC-058` fails the build if a WinUI source enumerates a directory, so
  a folder of thirty thousand files costs what a folder of three costs. Nothing is read or copied;
  the drop transfers path references only.
- **Duplicate policy:** never listed twice, in one drop or against the existing list. `PathKey` folds
  case and separators and drops one trailing separator, so `C:\Docs\File.txt`, `c:\docs\file.txt` and
  `C:\Docs\File.txt\` are one path. It deliberately does not resolve `..`, junctions or `subst` —
  that needs the disk, and the comparison must answer identically on the test machine (see L24).
- **Invalid item policy:** each item is judged alone and the good ones are kept. Refusals are
  `NotAFilesystemItem`, `Unreachable`, `AlreadyListed`, `SelfTarget`; the most serious reaches the
  screen as one localized sentence, never an HRESULT. A plain duplicate is silent when the drop added
  something else.
- **Self-target:** the archive being written is refused as its own input, by exact path only.
- **GZIP behaviour:** more than one input with a single-member format blocks Create with
  `CreateBlocker.TooManyInputsForFormat`. The condition is the absence of
  `ArchiveCapabilities.AddEntries`, not the format's name — so it is not a drag-and-drop rule, it
  applies to picker-added files identically, and it lifts the moment a multi-member format is chosen.
- **RTL:** the zone mirrors with the shell; Arabic text is product text, not a translation layer.
  Paths stay LTR and isolated. A dropped path is stored byte for byte — not reversed, reshaped or
  normalized, or the file will not be found.
- **What a drop never changes:** destination, name, format, profile, solid, encryption, overwrite.
  Only the input collection moves, and creation still happens only when Create archive is pressed.
- **Test coverage:** `DragDropTests` DD-001…DD-023 (Presentation, no timing-based tests, filesystem
  injected); `ARC-058`/`ARC-059` guard the no-walk rule and prove it fires.
- **Windows verification status:** **NOT VERIFIED.** The engine suite is green on Linux; the WinUI
  shell has not been compiled and the gesture has not been performed. `scripts/dragdrop-gate.ps1`
  builds both, lays out awkward material, and walks the 20 manual steps.
- **Known limitations:** L24 (text-only path comparison), L25 (Create-only target), L26 (no drag
  out), L27 (the cursor cannot promise acceptance before the drop).

### DROP 08 — Windows Shell Integration

- **Objective:** right-click a file or folder in Explorer and reach NOBA Archive's real actions.
- **Architecture:** `ShellSelection` → `ShellMenuBuilder` (capability report in, menu out) →
  `ArchiveActionRequest` → `ShellLauncher` (executable + `string[]`) → `noba-ar.exe` or
  `NOBA.Archive.App.exe` → `ShellCommandLine.Parse` → `ShellRequestSeed` → the existing
  `IArchiveWorkspace`/`OperationQueue`. One engine. `ARC-060` fails the build if anything under
  `Core/Actions` names a handler, provider, queue or ResourceGuard.
- **Actions:** Open, Test, Extract here, Extract to…, Compress to *FORMAT* (one line per creatable
  format), Create archive…
- **Capability rules:** every line derives from `FormatCapabilityReport`. RAR shows Open/Test/Extract
  and can never show Compress to RAR; BZIP2 and XZ appear nowhere; GZIP is disabled with a reason on
  a multi-member selection. The single-member rule is `PermitsMemberCount`, with exactly one
  implementation shared by the menu, the Create screen and the CLI — `SHELL-025` asserts they agree.
  **The parameter is members, not clicks (D7):** `ShellSelection.MemberCount` reads
  `ShellItem.IsDirectory`, which Explorer supplies, so a folder counts as `Many` without anything
  touching the disk. The complete 13-row selection matrix is measured in `docs/DROP08_REAUDIT.md`
  §14.2. `PermitsInputCount` survives as a thin adapter over the same method and still carries the
  clicks assumption; `CreateViewModel.Blocker` is its last caller and the debt is recorded in §14.3.
- **One action contract:** `ShellInvocations.For(action)` is the only definition of what an action
  runs. `ShellLauncher.PlanFor` fills its slots, `noba-ar shell-manifest` publishes them as tokens,
  and `shell-register.ps1` translates tokens to registry substitutions — none of them composes a
  command. `SHELL-054` compares manifest to model per entry; `SHELL-054b` and `SHELL-072` hold the
  script to translation only.
- **The resolver verb:** two argument shapes — a containing folder and a suggested archive name — are
  computed from the selection and cannot be expressed as a registry substitution. Those entries
  register as `noba-ar shell <entry-id> <selection>`, which rebuilds the selection, re-asks
  `ShellMenuBuilder` and dispatches through the same `ShellLauncher` path in-process. `SHELL-066`…
  `SHELL-073` are its inventory.
- **Registration scope:** each entry carries a `ShellScope` (`Archives` / `Files` / `Folders`) decided
  in Core from the capability model; the installer spells it as `SystemFileAssociations\<ext>`, `*`
  and `Directory`. Archive verbs are no longer registered on every file (D5), and *Compress to GZIP*
  is not registered on folders. `MultiSelectModel` comes from the command shape: `Player` with `%*`
  for whole-selection entries, `Single` for the rest (D4).
- **Create archive… seeds** `CreateViewModel.AddInputs`, the same method the picker and the DROP 06
  drop zone use, so duplicate folding comes free (`SHELL-028`).
- **Security:** there is no command string, so there is nothing to escape — `ShellLaunchPlan` is an
  executable plus an argument list, and `SHELL-024` asserts `cmd.exe` and `powershell` never appear.
- **Performance:** menu construction touches nothing. `ARC-061` forbids any filesystem call under
  `Core/Actions`; `SHELL-021` counts probe calls to prove it.
- **Installation:** `noba-ar shell-manifest` generates `scripts/shell-verbs.json` from the one
  manifest in Core; `scripts/shell-register.ps1` reads it in both directions. `SHELL-023` asserts
  uninstall removes exactly what install creates, deepest first.
- **PARTIAL — ADR-0006's mechanism is not built.** No WiX project, no `AppxManifest`, no
  shell-extension project, and no signing certificate; a sparse package will not register unsigned,
  and a COM handler cannot be compiled on the build machine. `shell-register.ps1` writes the classic
  `ContextMenuHandlers` entries — which ADR-0006 already names as the Windows 10 fallback — so **on
  Windows 11 the entry lands under "Show more options"**. ADR-0006 is unchanged and still the plan of
  record.
- **Registration defect, found and fixed on Windows:** the provider-based script appeared to hang.
  Traced: `Set-ItemProperty` on a path containing the literal `*` class key resolves the wildcard
  against all 801 subkeys of `HKCU\Software\Classes`, and the cost compounds per segment — 35 ms
  without `*`, 24,693 ms at depth 3, 840,771 ms at depth 5, no return at depth 6. `New-Item` was not
  the cause (75 ms). Fixed by using `Microsoft.Win32.Registry` throughout, with read-back
  verification and stray-entry detection. Guarded by `SHELL-032`…`SHELL-037`.
- **SHIPPING TARGET, NOT YET MET.** DROP 08 ships when NOBA Archive appears in the **Windows 11
  modern context menu, above "Show more options", as the "NOBA Archive" submenu** (OPEN-6). The
  classic `ContextMenuHandlers` registration built in Phase 2 lands **under** "Show more options" and
  is compatibility/fallback only. Closing this is SHELL-063/064/065 and SHELL-W1/W2 on the packaging
  track — Phase 5, not started.
- **Windows verification status:** **PARTIAL.** `validate.ps1` PASS (724 tests, 0 failed, 6 skipped)
  and both solutions build; the WinUI shell builds and the registration hang is diagnosed and
  repaired. The 34-step Explorer gate has **not** completed, and `%*` / `MultiSelectModel` have
  **never been observed on Windows** (SHELL-L8) — that is the first thing the gate must measure.
  `scripts/shell-gate.ps1` builds both solutions, generates the manifest, registers, and walks the 34
  manual steps including unregistration.
- **Test inventory:** SHELL-001…037 (model, script shape), SHELL-054…054c, 055…059b, 062 (Phase 1/2
  contracts), **SHELL-066…073** (the resolver verb, the script's translation, and malformed-selection
  safety). SHELL-060/061 are Phase 3; SHELL-063/064/065 are Phase 5.
- **Phase 3 result: PASS.** SHELL-060/061/074/075/076/077/078 all green. SHELL-075 was red on
  discovery — a folder named `Project.Alpha` proposed `Project.zip`, colliding with `Project.Beta` —
  and was closed by carrying `ShellItem` on `ArchiveActionRequest.Inputs` so the directory flag
  Explorer supplies is read rather than guessed from the input count. Same correction as D7, one layer
  down; no filesystem access added. `docs/DROP08_REAUDIT.md` §16.
- **Four separate gates, not one:** MODEL ✅ · REGISTRATION ⚠️ · WINDOWS CLASSIC RUNTIME ⚠️
  **executed 2026-08-28, INCOMPLETE** · WINDOWS 11 MODERN MENU ❌ not implemented.
- **The classic surface cannot compress a multi-selection, and that is now measured rather than
  suspected.** 15/15 automated gate checks pass on Windows 11, including SHELL-075. Manual M7 and M8
  fail because `%*` delivers nothing — and the obvious fix, `"%1"` with `MultiSelectModel = Player`,
  was measured to launch **one process per file**, which would make three archives instead of one
  (D4/SHELL-057). `SelectedPaths` therefore has no correct spelling on surface B.
  **`IExplorerCommand` (ADR-0006) is required, not preferred** — it receives an `IShellItemArray`.
  Four options with costs in `docs/DROP08_REAUDIT.md` §20; the decision is not made and no product
  code has been changed.
- **Phase 4 gate:** `scripts/shell-gate.ps1`, rewritten around SHELL-W3…W10 — **15 automated checks**
  that read the registry and run the command it actually contains, plus **9 manual steps** for what
  only eyes can settle. The old 34-question version was never completed once; most of what it asked a
  person to eyeball was a registry read. Guarded by SHELL-079/080/081. `docs/DROP08_REAUDIT.md` §17.
- **PHASE 5 DECIDED 2026-08-28:** it starts from ADR-0006 and the **modern Windows 11 surface that
  receives `IShellItemArray`** — not from patching the classic registry. Option A (restricting the
  classic surface) was considered and **not taken**; surface B stays as ADR-0006 always described it,
  the Windows 10 fallback, with `SHELL-L8` documented rather than disguised. The mechanism closes
  SHELL-L2, L8 and L9 structurally, and supplies `IsDirectory` from the shell. Blocker: a sparse MSIX
  will not register unsigned — a dev certificate unblocks gating, Phase 6 owns procurement. Shape and
  open questions: `docs/DROP08_REAUDIT.md` §21. **Not started.**
- **Known limitations:** SHELL-L1…L10 in `docs/SHELL_INTEGRATION.md`.

### DROP 06 — Security
- **Scope:** traversal, destination escape, absolute paths, `../`, Windows reserved names, invalid
  filenames, symlink/reparse, bombs, expansion, resource and disk exhaustion, oversized entries,
  malformed and corrupt archives, encrypted archives, password handling, temporary files, partial
  output cleanup.
- **Exit:** adversarial corpus in the repository; ResourceGuard strengthened, never weakened; no
  password in any log, description, history entry or diagnostic; **NOBA fails safely** is
  demonstrated, not asserted.

### DROP 07 — Archive Intelligence Without AI
- **Scope:** search by filename and path, extension/type/size/date filters, sorting, multi-selection,
  entry and archive metadata, ratios, CRC, counts, properties; large-archive browsing without
  loading everything into the UI; correct Arabic and Unicode matching.
- **Exit:** deterministic and fast search; application-level contracts a future Intelligence layer
  can extend without touching the archive core. **No AI in this drop.**

### DROP 08 — Shell Integration
- **Scope:** one `NOBA` entry with a concise, context-aware submenu; correct executable path and
  arguments; multi-selection; quoted, Unicode, Arabic and long paths; Explorer refresh; startup;
  errors; uninstall and cleanup.
- **Must not:** duplicate archive logic in shell code — it calls the existing contracts.
- **Exit:** verified on real Windows. Cannot be validated on the build machine at all.

### DROP 09 — Operations Center
- **Scope:** type, archive, source, destination, state, progress, files, bytes, speed, remaining,
  start, duration, error, result; deterministic states; pause/resume/cancel/clear; concurrency if the
  architecture allows; failure isolation; shutdown behaviour; history.
- **Exit:** the engine owns state and the UI never becomes the source of truth; stress tests for
  multiple operations.

#### What this drop found, rather than built

Most of the field list in Scope was already true when this drop opened: `OperationSnapshot` /
`OperationProgress` already carry type, description (the archive), destination, state, progress,
items/bytes done and total, instant and average speed, ETA, start/finish timestamps and the derived
duration, and error — none of it invented for this drop, all of it read from `IOperationQueue.cs` and
`OperationProgress.cs` as they already stood. `OperationsViewModel` (Presentation) already tracks more
than one operation independently and correctly — `OPS-023` proved that before this drop touched
anything. "History" is the queue's own entry dictionary, already kept until `RemoveFinished` is called.
So the open question this drop actually had to answer was the one named in this table's own **Primary
risk** column: concurrency, and specifically whether `OperationQueue` — built around a `SemaphoreSlim`
sized to `MaxConcurrentOperations` since DROP 01 — can truly be given more than one slot, or whether
`MaxConcurrentOperations = 1` is load-bearing and not just the shipped default.

**Answer, with evidence.** The scheduler itself tolerates any slot count: `OPQ-025` (Application.Tests)
proves three fake operations genuinely overlap — not merely "never exceed the cap" (`Honours_the_
concurrency_limit` already covered that) but actually *reach* three concurrent, by making every one of
them wait for the other two to arrive before any may proceed, so a queue that quietly serialised them
would hang the test rather than pass it by accident. `OPQ-026` proves one operation failing while two
siblings are genuinely mid-flight does not touch them — each ends in exactly the state its own work
earned. `OPQ-027` proves `DisposeAsync` cancels both a running operation and one that never reached the
slot gate, and returns rather than hanging. These three joined `OPQ-019…024` in
`OperationQueueTests.cs`; Linux run: 38/38.

That is the scheduler against fake delegates. Whether the *real* handlers — resolved once as singletons
by `AddNobaArchiveEngine`, `CreateArchiveHandler`, `ExtractArchiveHandler`, and the `IArchiveProviderRegistry`
/ `IFormatDetector` underneath them — tolerate two callers at the same moment had never been asked.
`OPX-017…019` (`OperationLifecycleTests.cs`, Integration.Tests) ask it: two real creations, and two real
extractions, each pair enqueued on the real `IOperationQueue` with `MaxConcurrentOperations = 2` and
released from the same rendezvous gate so neither can start before the other has arrived, each produce
correct, non-cross-contaminated output; and a real extraction failing on a missing archive, released
from the same gate as a real sibling extraction that is genuinely running at that moment, leaves the
sibling to finish correctly. All three are against the managed ZIP provider, for the reason
`CreationMatrixTests`/`ExtractionMatrixTests` already give: it needs no native library, so these run
unconditionally. Linux run (whole Integration suite): 183/183, 6 skipped.

**What is still not proven, and is not claimed to be.** `EngineWorkspace` (`NOBA.Archive.App`) is the one
place that actually parks concurrent outcomes by operation id (DEBT-06's `_outcomes` / `_testResults` /
`_created` / `_managed`), and it cannot be compiled or referenced from a test on this build machine —
the same WinUI restriction DEBT-10 already names. `OPX-017…019` prove the identical pattern — a
`ConcurrentDictionary<string, TOutcome>` written from concurrently running queue-driven work and read
once by the caller that awaited it — is safe; they do not and cannot exercise `EngineWorkspace` itself.
And the native backend's own thread safety under two simultaneous `IInArchive`/`IOutArchive` handler
objects (`SevenZipLibrary.CreateObject`, `src/NOBA.Archive.Backends.SevenZip`) has no automated coverage
anywhere in this repository and cannot get any from Linux, where `7z.dll` does not exist to load. Both
are recorded as **L33** rather than assumed either way.

**Decision: the shipped default stays `MaxConcurrentOperations = 1`.** Nothing above argues for raising
it — the comment that has stood on `OperationQueueOptions` since DROP 01 (compression already saturates
every core) is unaffected by any of this, and the native-backend half of L33 is a real reason not to
turn concurrency on by default before a Windows run exists to check it. What changed is that "if the
architecture allows" now has an answer for the half of the architecture Linux can test: it does, with
evidence, for the managed providers; it is unverified, honestly, for the native one.

### DROP 10 — Windows UX
- **Scope:** drag files/folders/archives in, contextual actions, keyboard shortcuts, copy/paste,
  confirmations, dialogs, progress, notifications, empty states, errors, responsiveness; Settings
  screen (closes the rest of L1).
- **Must not:** put business logic in code-behind.
- **Exit:** English and Arabic, LTR and RTL, technical paths LTR, consistent digits, keyboard
  accessible, NOBA visual identity intact.

### DROP 11 — Performance and Scale
- **Scope:** thousands to tens of thousands of entries, large archives and files, many small files,
  deep nesting, Arabic and mixed Unicode, slow and fast disks, limited memory. Measure startup,
  open, list, search, extract, create, update, memory, UI responsiveness.
- **Exit:** decisions backed by measurements; regression tests on the boundaries that matter;
  correctness, security, cancellation, pause and architecture unchanged.

### DROP 12 — Recovery and Failure Safety
- **Scope:** close during operation, cancellation, provider failure, corrupt archive, bad password,
  access denied, disk full, destination or source disappears, locked source, invalid path,
  interrupted extraction and creation, partial output.
- **Exit:** deterministic temporary-file behaviour; **never reports success for an operation that did
  not complete**; never silently discards a failure; user-readable messages with developer detail
  retained; recovery tests.

### DROP 13 — Release Security Audit
- **Scope:** hostile review of the whole product against the DROP 06 surface plus queue, cancellation,
  pause, shell integration, races and boundary violations.
- **Exit:** every real defect fixed; every unverified assumption documented as unverified. No
  manufactured results.

### DROP 14 — Product Polish
- **Scope:** every screen and state — spacing, typography, icons, hierarchy, buttons, keyboard, dark
  mode, both languages, path rendering, progress, operation states.
- **Must not:** redesign. Fix what makes the product feel unfinished.

### DROP 15 — Release Candidate
- **Scope:** no new features. Full release audit across archive, operations, security, Windows,
  reliability, quality.
- **Exit:** a checklist where every item is `PASS` / `FAIL` / `NOT VERIFIED` / `NOT SUPPORTED`, with
  evidence for every PASS; capabilities, formats, test totals, build status, security status, Windows
  validation status, limitations, debt, and a release recommendation.

---

## 5. Drop log

**Every Windows number in this table needs a file in `artifacts/`.** A transcript pasted into a
conversation is not evidence this repository holds; `validate.ps1` and `create-gate.ps1` both write
one, and the row stays marked unbacked until that file is committed.

Append one row per drop. Windows totals come from `validate.ps1`; leave the cell empty until it has run.

| Date | Drop | BuildStamp | Linux passed / skipped | Windows total | Preflight | WinUI build | Transcript |
|---|---|---|--:|--:|:--:|:--:|---|
| 2026-08-26 | Feature 02 — batch control repair | `…-batch-control-repair` | 530 / 6 | — | PASS | — | — |
| 2026-08-26 | Feature 02 — destination observability | `…-destination-observability` | 537 / 6 | 543 *(unbacked — see §2)* | PASS | FAIL (CS0104) | **transcript not saved** |
| 2026-08-26 | Feature 02 — FlowDirection ambiguity | `…-flowdirection-ambiguity` | 539 / 6 | — | PASS | — | — |
| 2026-08-26 | DROP 01 — Product Baseline | `…-drop-01-product-baseline` | 543 / 6 | — | PASS | — | `artifacts/test-20260826-163614/` |
| 2026-08-26 | DROP 02 — Creation + Capability Contract | `…-drop-02-create-capabilities` | 574 / 6 | **580 / 0** | PASS | not run | `r13-windows-validation-20260826-192638.txt` + `create-gate-20260826-192833.txt` |
| 2026-08-26 | DROP 04 — Production Extraction | `…-drop-04-production-extraction` | 614 / 6 | reported by owner | PASS | reported PASS | transcript not committed |
| 2026-08-26 | DROP 05 — Format & Capability Expansion | `…-drop-05-format-capability-expansion` | 642 / 6 | — | PASS | — | `artifacts/test-20260826-205122/` |
| 2026-08-27 | DROP 06 — Drag & Drop Experience | `…-drop-06-drag-drop` | 665 / 6 | — | PASS | not run | `artifacts/test-20260827-064539/` |
| 2026-08-27 | DROP 08 — Windows Shell Integration | `…-drop-08-windows-shell-integration` | 700 / 6 | — | PASS | not run | `artifacts/test-20260827-111109/` |
| 2026-10-18 | DROP 09 — Operations Center (concurrency) | `…-drop-09-operations-center-concurrency` | 875 / 6 | — | PASS | not run | `artifacts/test-20261018-202535/` |

---

## 6. Technical debt register

Recorded rather than fixed, because none of these affects product reliability today. Each needs a
decision, not silence.

| ID | Item | Why deferred | Costs us |
|---|---|---|---|
| DEBT-01 | 173 `xUnit1051` warnings in Feature 00/01 test suites | The xUnit analyzers do not run under the offline build, so ~173 edits would be unverifiable from the build machine. `TreatWarningsAsErrors` is off for test projects only, deliberately and documented. | A real warning can hide in the noise |
| DEBT-02 | `PausableStream`'s per-write gate is not isolated by a test | Three observables were tried; none can separate it from the per-entry gate — the abandoned temp file is deleted before assertion, and byte progress is only reported on entry completion | The per-write guarantee rests on the argument, not a test |
| DEBT-03 | The alpha CLI is refreshed only by `alpha.ps1` | Rebuilding it in `validate.ps1` would slow every run | A stale binary can be mistaken for the current drop; `validate.ps1` now warns |
| DEBT-04 | `OperationSnapshot.LastProgress` can move progress backwards if a stale snapshot arrives after a newer report | Not observed in practice | A brief visual stutter |
| DEBT-06 | `EngineWorkspace._outcomes` / `_testResults` are drained only by the awaiting caller | Every current caller awaits, so nothing accumulates | A future caller that starts an operation without awaiting would leak an entry — DROP 09 concurrency must revisit it |
| DEBT-05 | `FORMAT_SUPPORT.md` still describes the 1.0 target matrix as if current | Rewriting it belongs with DROP 05 | The document overstates what ships |
| DEBT-07 | `CreatePage` rebuilds its whole input list on every notification | The list is what a person selected by hand; a thousand-item selection is not a real case yet | A very large manual selection would redraw more than it needs to |
| DEBT-08 | The Create screen offers profiles (Fast / Balanced / Maximum / Smallest), not the numeric level | `CompressionLevel` reaches the engine and is tested (7ZC-005); only the expert control is missing | An expert cannot ask for level 3 from the UI |
| DEBT-10 | `ARC-057` is a source rule, not a behavioural one: it asserts that `EngineWorkspace` *writes* `SourceArchivePath`, not that a zone is propagated | `NOBA.Archive.App` cannot be compiled or referenced from any test on the build machine | A future refactor could satisfy the text and break the behaviour; the Windows gate is the only real check |
| DEBT-09 | `ARC-055` treats any `"zip"`-shaped string literal in the App project as a format decision | A literal used for something innocent would be a false positive | A future unrelated literal needs a rename or a rule refinement — not a suppression |

---

## 7. Standing rules for every drop

These are not restated per drop. They apply always.

- No `#pragma`, no `NoWarn`, no suppression, no catch-all `try/catch`, no timeouts that hide a defect.
- Never weaken `ResourceGuard` to make anything pass.
- Never bypass the Operation Queue.
- No business logic in WinUI code-behind.
- No sync-over-async where the architecture forbids it — an architecture test enforces this.
- No fake tests, no timing-based tests that pass by accident, no test that silently skips the
  behaviour it names.
- Every drop that changes implementation bumps `BuildStamp`.
- Colour alone never carries meaning: icon **and** text **and** state.
- Latin digits for technical values in both languages.
- **Windows runtime results come only from a Windows run.** Nothing executed on the build machine is
  ever reported as Windows-proven.
