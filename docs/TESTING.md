# NOBA Archive — Testing Strategy

> **Status:** live from Phase 1 — eight suites exist and run. **No test result in this repository may be
> reported as PASS unless the command was actually executed and its output observed** (rule 51).
> **Date:** 2026-08-19

## 1. Test pyramid

| Layer | Project | Count target | Runs where |
|---|---|---|---|
| Unit | `Core.Tests`, `Security.Tests`, `Application.Tests`, `Infrastructure.Tests`, `Formats.Tests` | fast, <5 s total | every build, CI |
| Architecture | `Architecture.Tests` | 47 tests as of Phase 1 | every build, CI |
| CLI contract | `Cli.Tests` | exit codes, JSON purity, password channel | every build, CI |
| Parity | `Parity.Tests` | `PAR-###`: both surfaces, one engine | every build, CI |
| Integration | `Formats.Tests`, `Integration.Tests` | real archives on a real filesystem | CI + local |
| Security | inside `Security.Tests` + `Integration.Tests`, tagged `Category=Security` | every `SEC-###` in the threat model | CI, gate for release |
| Performance | `Integration.Tests` tagged `Category=Perf`, BenchmarkDotNet harness | manual + nightly | Phase 8 onward |
| UI | `Ui.Tests` | happy paths + error paths | nightly + pre-release |

## 2. Fixtures — generated, not committed blobs

`NOBA.Archive.TestKit` generates fixtures deterministically from a seed, so tests are reproducible and
the repository does not carry binary archives whose provenance nobody remembers.

```csharp
var tree = FixtureTree.Create(seed: 1234)
    .WithFile("readme.txt", size: 1.KiB())
    .WithFile("مستند عربي.txt", ContentKind.Utf8Text)   // Arabic
    .WithFile("файл.bin",  ContentKind.Random)
    .WithFile("emoji 🗜️.dat", size: 3.MiB())
    .WithFile("spaces and (parens) [brackets].log")
    .WithFile("very.long.name.with.many.dots.tar.gz")
    .WithEmptyFile("zero.bin")
    .WithEmptyDirectory("empty-dir")
    .WithDeepTree(depth: 40)
    .WithDuplicateNamesDifferingByCase("Data.txt", "data.TXT")
    .WithIncompressible(size: 64.MiB())      // random bytes
    .WithHighlyCompressible(size: 256.MiB()); // zeros — bomb-adjacent, legitimate
```

Hostile fixtures are **byte-crafted**, not produced by a normal archiver, because a normal archiver will
not emit `..\` entries:

| Builder | Produces |
|---|---|
| `MaliciousZip.WithTraversalEntry("..\\..\\Windows\\System32\\evil.dll")` | classic traversal |
| `MaliciousZip.WithAbsoluteEntry("C:\\Windows\\evil.dll")` | rooted path |
| `MaliciousZip.WithAdsEntry("readme.txt:payload.ps1")` | CVE-2025-8088 class |
| `MaliciousTar.WithSymlink("link", "..\\..\\")` then `.WithFileThrough("link/evil")` | write-through-symlink |
| `MaliciousZip.WithReservedName("CON")`, `.WithTrailingDot("file.")` | Win32 name traps |
| `MaliciousZip.WithRtlOverride("cod\u202Etxt.exe")` | extension spoofing |
| `BombBuilder.Ratio(10_000)`, `.Nested(depth: 6)`, `.EntryCount(2_000_000)` | DoS class |
| `CorruptionBuilder.Truncate(pct)`, `.FlipBits(n)`, `.MismatchCentralDirectory()` | integrity class |

Large fixtures (>1 GiB) are generated sparsely at test time and marked `Category=Large` so the default
`dotnet test` run stays fast.

## 3. Test catalogue (IDs referenced by the threat model)

### Security — all are release gates

| ID | Scenario | Expected |
|---|---|---|
| SEC-001 | `..\` traversal entry | entry blocked, reported, nothing written outside destination |
| SEC-002 | absolute / UNC / device / drive-relative entry | blocked |
| SEC-003 | ADS in entry name | blocked (never written, never stripped-and-accepted) |
| SEC-004 | symlink entry escaping destination | not created; reported |
| SEC-005 | symlink then write-through | second write blocked by post-open handle re-check |
| SEC-006 | reserved device names (`CON`, `LPT1`), trailing dot/space | **never written under the requested name.** Default policy: deterministic rename with a visible report; `Skip` is a setting. Silent acceptance fails the test |
| SEC-007 | RTL-override filename | displayed de-spoofed; real extension shown |
| SEC-008 | ratio / expanded-size / entry-count bombs | user prompted with real numbers; no unbounded write |
| SEC-009 | zip quine / recursive nesting | auto-open depth limit enforced |
| SEC-010 | local vs central header mismatch | `CorruptData`, no partial file left behind |
| SEC-011 | absurd declared dictionary size | allocation refused, clear error |
| SEC-012 | destination `C:\out` vs sibling `C:\out-evil` | prefix check rejects the sibling |
| SEC-013 | pre-existing junction inside destination | write refused after handle re-check |
| SEC-014 | password passed to `ILogger` | password absent from every sink's output |
| SEC-015 | password never written to settings/temp/registry | filesystem+registry scan finds nothing |
| SEC-016 | `Password.Dispose()` | backing memory zeroed |
| SEC-017 | malformed metadata (negative/huge sizes, bad offsets) | no crash, typed error |
| SEC-018 | archive carries MOTW | every extracted file carries the same zone |
| SEC-019 | `7z.dll` planted in CWD | not loaded; absolute-path load only |
| SEC-020 | temp workspace ACL | current user only |
| SEC-021 | cancelled/crashed operation | temp swept on next start; no partial file under the final name |

### Integration

`INT-001..019` round-trips per format (create → list → extract → byte-compare → test);
`INT-020` legacy-code-page, **Arabic and emoji** ZIP names. The Windows interop spike showed the native
ZIP handler round-tripping UTF-8 names **exactly**, so this test exists for real-world legacy archives —
CP437/CP1256 names written by older tools, with no UTF-8 flag — rather than for anything the backend does
to us; the code page must still be configurable. **Entry-name fidelity is asserted separately from
decoded content**, because verifying content *by name* lets a name-decoding difference masquerade as a
decode failure (that is exactly how the spike first misreported it);
`INT-021` Unicode normalisation and case-collision;
`INT-030` Zip64 (>4 GiB member, >65,535 entries); `INT-031` 8 GiB single file, constant memory;
`INT-040` multi-volume incl. a missing volume; `INT-050` corrupt/truncated archives;
`INT-060` password-protected create+extract per format, wrong password, empty password;
`INT-070` long paths >260 chars; `INT-080` cancellation at 10%/50%/90% leaves no partial output;
`INT-090` network path (UNC) source and destination; `INT-091` removable drive removed mid-operation;
`INT-092` disk full mid-write.

### Application behaviour (`APP-###`)

| ID | Scenario | Expected |
|---|---|---|
| APP-030 | "Delete source after archiving" with a successful archive | delete happens only after re-open + list + integrity test succeed; files go to the Recycle Bin |
| APP-031 | "Delete source after archiving" where the integrity test fails | **nothing is deleted**; the failure is reported with the operation ID |
| APP-032 | Overwrite policy not chosen by the user | no existing file is ever replaced |
| APP-033 | Conflict "…All" latching | one decision applies to the remainder and is shown in the operation summary |
| APP-034 | Disk-space pre-flight underestimates and the disk fills mid-write | typed `DiskFull` error, temp file removed, no partial file under the final name |

### Packaging (`PKG-###`, Phase 9)

| ID | Scenario | Expected |
|---|---|---|
| PKG-001 | Clean install on a fresh Windows image | app launches; no associations claimed unless the user ticked them |
| PKG-002 | Upgrade over a previous version | settings preserved and migrated; no duplicate shell entries |
| PKG-003 | Uninstall | binaries, sparse package registration, associations, classic handlers and registry keys all removed; `%LOCALAPPDATA%` data left or removed per the user's choice |
| PKG-004 | `7z.dll` version + SHA-256 manifest check | shipped binary matches the manifest; a mismatch surfaces the documented warning |
| PKG-005 | Sparse package signature | package is signed and installs without Developer Mode |
| PKG-006 | Install + smoke + `SEC-###` suite + uninstall on a **Windows 10 1809-level image** (LTSC 2019) | passes, or the release notes state explicitly that the minimum-build image was not validated |
| PKG-007 | `noba-ar.exe` present after install; `HKLM\SOFTWARE\NOBA\Archive` written with `InstallPath`, `CliPath`, `Version`; the **optional** `PATH` entry added only if the user opted in | discovery works with `PATH` untouched; everything added is removed on uninstall |

### Dual-surface parity (`PAR-###`)

Every scenario below runs twice — once through the `Application` handlers directly, once by invoking
`noba-ar.exe` — and asserts identical outcomes. This is how ADR-0009's "exactly one engine" rule is
verified rather than trusted.

| ID | Scenario | Expected |
|---|---|---|
| PAR-001 | Extract each `SEC-001..013` hostile archive through both surfaces | identical blocked/allowed decisions and identical blocked-entry reports |
| PAR-002 | Create → test → extract round-trip per format through both surfaces | byte-identical outputs |
| PAR-003 | Wrong password, corrupt archive, insufficient disk space | same `ArchiveErrorCode`; CLI maps it to the documented exit code |
| PAR-004 | Bomb guards tripped | same thresholds and same refusal; CLI is non-interactive and exits `6` |
| PAR-005 | MOTW propagation | extracted files carry the same zone from either surface |

### Unit

State-machine transitions (exhaustive over `state × trigger`); `ArchivePath` normalisation (property-based,
with a generator of hostile strings); conflict-resolution matrix including "…All" latching;
compression-profile → provider-option mapping per format; settings migration `v1→v2→v3` including from a
corrupt file; `ByteSize`/speed/ETA formatting in en-US and ar-EG; capability gating (a command bound to a
missing capability is disabled).

### Architecture (`Architecture.Tests`)

Reference-direction rules from ARCHITECTURE.md §3.1; `SharpCompress`/`SharpSevenZip`/`7z` referenced only
by `Formats`; no `System.IO.File`/`Directory` outside `Infrastructure`; no `Console.` in product code;
no user-facing string literal outside resources; no `catch (Exception)` without a typed rethrow or a
logged, handled outcome; no `.Result`/`.Wait()`/`GetAwaiter().GetResult()` in product code; no `static`
mutable state.

### UI (`Ui.Tests`)

Open archive → list renders; extract with a conflict → dialog appears and each button behaves; create
archive with password; cancel mid-operation → UI returns to a clean state; error dialog shows a friendly
message with technical detail only behind **Details**; password prompt does not echo or leak into
automation properties; full keyboard-only traversal of the main window; high-contrast rendering;
`ar-EG` RTL layout snapshot.

### Performance (Phase 8)

Startup to interactive; open a 100k-entry archive; open a 1M-entry archive; extraction throughput
(ZIP-Deflate, 7z-LZMA2) vs a baseline; compression throughput per profile; peak working set during an
8 GiB operation (target: bounded, not proportional to archive size); UI frame time during an operation.
Numbers are recorded in `PERFORMANCE.md`; optimisation happens only after measurement (rule 25).

## 4. Regression policy

Every production bug fixed gets a test named `REG-<issue-id>` **written before the fix**, committed in the
same change, and never deleted. A fix without a regression test is not merged (rule 38).

## 5. Validation commands

```powershell
.\scripts\build.ps1                 # restore + build, Release, warnings as errors
.\scripts\test.ps1                  # every suite that exists, with the phase-gate summary

dotnet restore
dotnet build NOBA.Archive.sln -c Release
dotnet test  NOBA.Archive.sln -c Release
dotnet test  NOBA.Archive.sln -c Release --collect:"XPlat Code Coverage"
dotnet list package --vulnerable --include-transitive

# From Phase 2 onward
dotnet test -c Release --filter "Category=Security"
dotnet test -c Release --filter "Category=Large"
# From Phase 5 / Phase 9
dotnet test -c Release --filter "Category=UI"
.\scripts\verify-native.ps1        # 7z.dll version + hash + load-path check
.\scripts\package.ps1              # MSI + sparse package
```

Add `-p:NobaOfflineBuild=true` (and use `scripts/test.sh`) only where nuget.org is unreachable; see
DEPENDENCIES.md §6. A shipping-configuration PASS may not be claimed from an offline run.

Each phase report must state, per suite: **PASS / FAIL / NOT RUN**, with the reason for anything not run.

## 6. CI

| When | Image | Suites |
|---|---|---|
| Every push / PR | `windows-latest` (Windows 11 — the development baseline) | restore → build `-warnaserror` → unit + architecture → security → integration → coverage → vulnerable-package scan |
| Nightly | `windows-latest` + a **Windows 10 22H2** image | above, plus Large, Perf, UI, and the `PAR-###` parity suite |
| Release candidate | above, plus a **Windows 10 1809-level (LTSC 2019)** image | full suite plus `PKG-001..007`: install, upgrade, uninstall, native-manifest check, CLI presence |

Validating the minimum supported build is a release gate wherever the image is available. If it could
not be run for a given release, the release notes say so — implied coverage is worse than no coverage
(rule 49).

## Mark of the Web (`MOTW-###`) — Windows only

| ID | What it pins |
| --- | --- |
| `MOTW-001` | `Zone.Identifier` round-trips, and writing it does not disturb the file |
| `MOTW-002` | A file with no zone reports null rather than throwing |
| `MOTW-003` | A tagged archive's zone reaches **every** extracted file |
| `MOTW-004` | A local archive does not gain a zone it never had |
| `MOTW-005` | Propagation can be switched off |
| `MOTW-006` | An entry requesting an alternate data stream is still refused, and the real file keeps the *archive's* zone rather than the one the archive tried to plant |

These skip off Windows and are reported as skipped, never folded into the pass count. A skip here means
an untested security control, not a working one.
