# Changelog

All notable changes to NOBA Archive are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versioning will follow SemVer from 1.0.

## [Unreleased]

### WinUI 3 — design foundation, shell and the browse screen — 2026-08-21

The first UI. Two projects, and the split between them is the whole design of this step.

**`NOBA.Archive.Presentation` — no XAML, no Windows, no archive logic.** View models, design tokens,
bidirectional rules and the UI's text, in a plain .NET library that builds and tests anywhere. WinUI 3
builds only on Windows, and putting the presentation logic inside it would have placed every line of it
beyond the environment where most of this solution's validation happens. **47 tests** run against it on
Linux. That the fake engine those tests use is thirty lines of dictionaries is itself the evidence that
no archive logic leaked upward.

**`NOBA.Archive.App` — XAML and wiring.** Deliberately outside `NOBA.Archive.sln`, in its own
`NOBA.Archive.App.sln`: adding a Windows App SDK reference to the engine solution would end its ability
to build and test on Linux. `scripts/app.ps1` builds and runs it, and builds the engine solution first —
a XAML error is noise if the engine is broken.

**The design system is now code, and it generates the XAML.** `docs/DESIGN_TOKENS.md` §9 asked for
exactly this and it had never been done: `Themes.generated.xaml` is produced from `DesignTokens.cs`, and
`DES-004` regenerates it and compares, so an edit to the XAML fails the build instead of silently
disagreeing with the design system. The primitives are private fields, which makes "no component names a
raw colour" a structural fact rather than a convention.

**Five colour pairs failed WCAG AA, and the palette had claimed AA since Phase 1.** The first run of the
contrast test found them:

| pair | theme | was | now |
| --- | --- | ---: | ---: |
| `border.strong` on `surface.base` | dark | 1.59:1 | **3.10:1** |
| `border.strong` on `surface.raised` | light | 2.56:1 | **3.50:1** |
| `text.secondary` on `surface.sunken` | light | 4.23:1 | **4.84:1** |
| `state.allowed` on `surface.raised` | light | 3.30:1 | **5.02:1** |
| `state.warning` on `surface.raised` | light | 3.19:1 | **5.02:1** |

The palette was corrected, not the threshold. Translucent tokens are composited onto their surface
before measuring, and the `state.*` colours are held to body contrast (4.5:1) rather than the boundary
threshold, because they are read as words — a refusal the user has to squint at is a refusal that gets
dismissed.

**Arabic is a mode, not a translation.** Flow direction, font stack, line height and row height are all
functions of one property on the shell, so there is no code path in which the UI is in Arabic and laid
out for Latin. Entry names and paths are direction-isolated by the view model before the view sees them
— `BIDI-003` is a security test wearing typographic clothes, because a traversal path that renders with
its segments reordered is a path the user cannot judge. The strings are data rather than `.resw` files
precisely so `L10N-001` can assert that both languages define every key, on a machine that cannot build
WinUI.

**The create screen's options come from the engine's capability set**, not from knowledge of the
formats: RAR never appears in the format list, 7Z disappears when the native library is missing, and an
option the backend cannot honour is disabled with a reason rather than hidden or silently dropped
(`UI-020…027`). This is the capability gate defended from above, where the failure would otherwise be an
archive that is not the one the user asked for.

**Architecture.** `Presentation` may reference `Core` and `Application` and nothing else — not
`Security`, not `Infrastructure`, not a backend — because from any of those a screen could validate a
path, write a file, or decide a policy. Enforced by the dependency test.

**Not verified, and this is the first code in this project shipped in that state.** The XAML has never
been through a compiler: there is no compiler for it on the machine it was written on. The first Windows
build will very likely produce errors, `scripts/app.ps1` writes them to a transcript, and they are the
report rather than something to work around. The screenshots must come from that machine too — the
design foundation page renders the tokens in HTML and says plainly that it is a drawing, not a capture.

**Still to build:** the create screen's XAML (its logic is done and tested), the operation centre, and
real drag-and-drop progress. Packaging and NOBA OS integration are untouched, as agreed.

### Correction — the test totals were wrong — 2026-08-21

Two numbers published in this file and on the roadmap were **arrived at by adding up suite totals by
hand, and the addition was wrong**: "388 tests" for the R18 drop and "396" for the RAR drop. The real
figures, from the same transcripts, are **366** and **375**. Nothing about pass/fail or skips changes —
every suite reported 0 failures and 0 skips, and those numbers were read, not computed — but a headline
figure nobody computed is not evidence, and this project has spent three phases insisting on exactly
that distinction.

`validate.ps1` now sums the suite totals itself and prints

    TESTS: <n> across <k> suite(s) - <f> failed, <e> errored, <s> skipped

so the figure in every future transcript comes from the machine that ran the tests. It also calls out
any skip on its own line: a skipped test proves nothing, and that belongs on the headline rather than
inside one suite's output.

### 7Z creation — 2026-08-21

The native backend writes 7Z. Same engine, same security rules, same capability gate — what changed is
that `Create` is now true for 7Z, and true because it works rather than because it is declared.

**Verified against 7-Zip itself.** Archives produced here list, test (`Everything is Ok`) and extract
under the real `7z` command line, byte for byte identical to the source tree, including an Arabic
entry name, a 200-character name, an empty file, an 8 MB incompressible file, and a 2019 timestamp.
The test suite reads every archive back through `7z.dll` — 7-Zip's own handler — because a suite that
needs `7z.exe` installed is a suite that silently skips.

**The builder inverts control, like the extractor did.** `IOutArchive::UpdateItems` takes the whole
item list at once and then pulls each entry's properties and bytes in whatever order its compressor
wants, so there is no moment during `AddAsync` at which a stream can be handed over. `IArchiveBuilder`
now takes an `EntryContentSource` — a callback that opens the entry's bytes on demand — instead of an
open `Stream`. Holding every source file open until `CompleteAsync` would exhaust handles on any real
folder; this way one file is open at a time.

**Three defects found while building it, all of them silent:**

- **`ISetProperties` indexes its value array at the platform's `PROPVARIANT` stride.** Handing it a
  managed array of the 24-byte Windows struct meant that on Linux — 16 bytes — every property after
  the first was read from the wrong offset. The compression level worked (index 0) and **every later
  option was ignored**: an archive requested non-solid came back solid, with no error anywhere. The
  same mistake as the original 24-vs-16 finding, in the direction the original reasoning did not
  cover: over-declaring is safe for one value and wrong for an array.
- **`kpidMTime` was `20`, not `12`.** An off-by-eight into 7-Zip's property enum lands on `kpidType`,
  and the result is not an error — it is archives with no timestamps at all, which every tool displays
  as blank rather than as a failure.
- **The native backend never read entry timestamps back either**, so every 7Z and RAR file extracted
  with the time it was written rather than the time it was archived. Silently, because a plausible
  timestamp looks like a real one. The managed ZIP path has carried this since Phase 2; the 7Z round
  trip is what exposed the gap.

**And one in the read path, exposed by being able to write encrypted archives.** `TestAsync` never
asked for a password. An archive whose entries are encrypted but whose header is not opens without
one, so testing it reported "unsupported method" for every entry — 7-Zip's answer when it cannot build
a decoder because no key was supplied. Extraction had always asked; testing had not. This matters
beyond the `test` verb: "compress and delete the originals" verifies the new archive before deleting
anything, so an encrypted archive would have failed verification.

**Capabilities say what is true.** 7Z gains `Create`, `AddEntries`, `Encrypt`, `EncryptFileNames`,
`SolidCompression`. It does **not** gain `SplitVolumes` (writing volumes needs
`IArchiveUpdateCallback2`, which is not implemented), `RemoveEntries` or `RenameEntries` (updating an
existing 7z is not implemented). `7ZC-011` proves an unadvertised option is refused rather than
quietly dropped, and `7ZC-012` pins the whole set. RAR still cannot be created and never will
(ADR-0004).

**CLI.** `create` takes `--solid`, `--encrypt` and `--encrypt-names`, checks them against the
registry's real capabilities before doing any work, and reads the password from stdin or an
ACL-checked file exactly as the other verbs do. The help text no longer says the only creatable
format is ZIP.

**Added — `7ZC-001…012`**: valid-7z signature and listing, full byte-for-byte round trip, integrity
test of our own output, timestamps, compression level actually reaching the backend, solid honoured in
both directions, encrypted round trip, encrypted file names, encryption refused without a password
(and leaving no archive behind), RAR creation still refused, unadvertised options refused, and the
capability set itself.

**Known limitations.** Creation only — an existing 7z cannot be added to, and nothing can be removed
or renamed. No split volumes. No progress reporting during compression: 7-Zip reads entries in its own
order and interleaves them inside a solid block, so per-entry byte progress would be a fiction, and
`AddAsync` reports "queued" rather than "written". ZIP creation is still the managed path and is
unchanged.

### Source drop stamp — 2026-08-21

`noba-ar version` now prints the source drop it was built from, and `validate.ps1` reads it out of
`BuildStamp.cs` before building so the transcript names the tree even when the build fails.

This is not bookkeeping. A stale source tree has now silently passed full validation **twice** — once a
Phase 3 run against an un-extracted Phase 2 tree, once a RAR-fix run against the tree before the fix.
Both transcripts read PASS on every phase with `Skipped: 0`, and nothing in either said which source
produced them. The second one was caught only because the suite totals were eight short. A validation
report that cannot identify its own input is not evidence, and `CLI-026` now fails the build if that
line disappears.

### Seven real RAR archives — 2026-08-21

The first RAR files NOBA Archive had ever been pointed at came from a user's disk. Every one of them
was reported as **encrypted, password required** — including the one that is not an archive at all.

**RAR did not work. At all. Through any surface.** 388 tests passed on a build that could not open a
single RAR file, and the reason they passed is worth stating plainly rather than filing under
"coverage gap":

- The handlers resolve the format from content, choose a provider with it, and then opened the archive
  with `OpenOptions` that did not carry it. The native backend fronts three of 7-Zip's handlers behind
  one entry point, so with nothing said it **defaulted to 7Z**.
- 7-Zip answers `S_FALSE` for "this is not my format". That was being read as "the header is
  encrypted" — the one other thing `S_FALSE` means.
- So a valid RAR went to the 7Z handler, was refused, was diagnosed as encrypted, and the headless
  password provider turned that into exit code 4.
- **Every existing test used 7Z, which is the format the wrong default happened to name.** The twelve
  `SZ-` tests do run through the whole engine — they were not shallow — and the defect sat underneath
  all of them. The `SZ-011` licence test names RAR in its title and asserts on a capability flag; it
  passes against a backend that cannot open a RAR.

**Fixed**

- The three handlers pass the format they resolved (`OpenOptions.ForceFormat`). The backend's default
  is gone: a caller that does not say now gets `InvalidConfiguration` at the call rather than a
  question about a password three layers away.
- `S_FALSE` is checked against the format's signature before anyone claims encryption. Encrypted
  headers do not hide the container's magic bytes in either 7Z or RAR, so the two cases are decidable.
- **A per-entry failure on the batch path was being swallowed.** 7-Zip reports a failed entry through
  `SetOperationResult` and still returns `S_OK` from `Extract`; the R18 path used that only to abandon
  the temp file. An archive whose every entry failed therefore extracted **nothing and reported
  success** — while the single-entry path threw for the same archive, so the behaviour depended on
  whether the selection held one file or two. This is a defect the R18 change introduced, found six
  hours later by the first real archive.
- **The archive-wide expansion-ratio guard was inert on 7Z and RAR.** `kpidPackSize` was never read,
  so every native-backend entry reported zero compressed bytes, and `ResourceGuard` skips the ratio
  check when the compressed total is zero. It failed open, on the two formats that compress hardest,
  and left no trace of doing so. Now read per entry and summed archive-wide.
- `test` on an archive this build has no codec for reports **Unsupported**, not **Corrupted**.
  "Corrupted" is a claim about the file; the file was intact and the user needs a codec, not a backup.

**Added**

- `RarFixtures` — RAR4 archives assembled byte by byte from the block layout, with the stored method so
  no codec is involved, each verified to list and extract identically under 7-Zip. There is no RAR
  writer to make them with and there never will be (ADR-0004), and names carry the RAR4 Unicode flag so
  the Arabic entry decodes the same whatever the tester's code page.
- **RAR-001…008**, all driven through the engine rather than the provider, because the wiring is where
  the defect was. Reverting each fix was checked to fail them.
- The NTFS master-file-table signature in the non-archive table. The unopenable `.rar` was raw
  file-table data left behind by disk-recovery software years ago — never an archive, in any tool.
- `shakedown.ps1` now covers 7Z and RAR as well as ZIP, pins 7-Zip's handler per extension (trying
  `-trar5` before `-trar`, as NOBA does), and takes `-Extract`: a second pass that extracts with both
  tools and compares the trees by SHA-256. Listing agreement says the headers were read alike and
  nothing about a decoder — and the summary now reports how many archives were **actually decoded**,
  because "both tools refused" counted as agreement and read as a pass.

**Windows-verified 2026-08-21, on the user's own archives.** `validate.ps1` reports the drop it built
(`2026-08-21-rar-first-contact`) and **90 integration tests, `Skipped: 0`**, 375 across all suites.
(First published as 396; see the correction at the top.)
`shakedown.ps1 -Extract` over the seven real archives:

| | |
| --- | --- |
| archives decoded and compared | **6 of 7** |
| files hashed | **29** |
| files differing from 7-Zip | **0** |
| the seventh | refused by both tools — the NTFS fragment, named as such |

That covers RAR2 (2007), RAR4 (2009–2012) and RAR5 (2022), ~105 MB, with JPEG, DWG, an Access
database and a Crystal Reports file among the contents. It is the first evidence in this project that
the RAR decode path produces correct bytes, and it comes from a comparison against 7-Zip file by file
rather than from a count.

**Still unproven after this.** R16 — legacy code-page entry names — remains at zero real-world
evidence: every one of these archives carries correctly encoded Unicode names, so the guessing path
was never entered. Encrypted RAR is also untested.

### R18 FIXED — batch extraction — 2026-08-21

Solid 7z extraction, 3200 entries, same machine and same command:

| | before | after |
| --- | ---: | ---: |
| solid extract (Linux) | 20.09 s | **0.91 s** |
| solid extract (Windows) | 13.08 s | **3.29 s** |
| scaling ×4 entries (Linux) | ×8.9 | **×2.2** |
| scaling ×4 entries (Windows) | ×7.7 | **×3.2** |
| worst ratio against 7-Zip (Windows) | ×10.7 | **×3.0** |

Solid and non-solid have converged — on Windows, 3.29 s solid against 3.39 s non-solid; on Linux solid
is now the *faster* of the two (0.91 s against 1.50 s). That convergence is the shape to look for: it
is what happens when a block decodes once instead of once per entry, and it is stronger evidence than
the absolute numbers that the quadratic term is gone rather than merely smaller.

**Windows-verified 2026-08-21.** `validate.ps1` on the fixed tree: 82 integration tests (hostile
archive + round trip) with `Skipped: 0`, and **366 tests across all suites, 0 failures, 0 skips**.
(This file first published that total as 388; see the correction at the top.)
`benchmark.ps1 -EntryCounts 800,3200` then reported *"tracks linear growth out to 3200 entries"* with
the verdict taken from the last step, which is the configuration that previously produced the false
all-clear.

**Why Windows does not reach the Linux figure.** 3.29 s is not sub-second, and the reason is visible in
the control: 7-Zip itself takes 1.297 s on Windows for the same archive against 0.08 s on Linux — the
baseline is ~16× slower, not NOBA. The number that carries meaning across platforms is the ratio, and
it is ×2.5–3.0 on both. R18 is closed on the scaling claim, not on an absolute-time claim.

**What changed.** `IArchiveSession` gained `SupportsBatchExtraction` and `ExtractManyAsync`, whose shape
mirrors 7-Zip's own extraction callback: it asks the caller for a stream per entry and is told null for
the ones it may not have. `IExtractionWriter` gained `BeginEntryAsync` returning an `IEntryWriteSession`,
because control is inverted for a native backend — 7-Zip drives its loop and asks where entry N goes, so
there is no point at which we can hand it a callback that fills a stream we opened.

**ADR-0005 is untouched, and more visibly so than before.** `ExtractManyAsync` never sees a path. The
writer still resolves every destination, still re-verifies every opened handle, and still decides
publication alone. `WriteEntryAsync` is now a thin wrapper over begin/complete, so the fast and slow
paths share one implementation of the rules rather than two that can drift.

**A security control was nearly lost to a performance fix.** Rewriting the extraction loop dropped the
call site of the archive-wide bomb guard's *running* half — the one that counts bytes actually produced,
as opposed to the pre-flight check that reads the archive's own declared totals. Declared totals are
attacker-written and can only ever refuse, never reassure; the running check is what catches an archive
that understates itself. The compiler caught it only as "variable assigned but never used", which is a
thin thread for a security control to hang by. Restored, and now called from the single `Record` point
both paths share.

**Fixed while making the change.** `Publish` flushed a stream `CompleteAsync` had already disposed —
caught by 14 existing tests, not by review. The order is not a preference: on Windows a file cannot be
moved while a handle to it is open.

**Added.** `ExtractionOptions.PreferSingleEntryExtraction` forces the old path — `SZ-013` drives both
over the same archive and compares the extracted trees byte for byte, and it gives a user hitting a
backend-specific problem somewhere to go other than an older version. `SZ-014` pins that hostile entries
are still refused on the batch path, which is the failure that would matter: the backend now drives the
loop, and a broken decline would write the entry ADR-0005 exists to block.

### Phase 3 — the native backend — 2026-08-21

7Z and RAR through `7z.dll`, on the COM-lite interop the spike validated (ADR-0003 option B).
**Verified on Windows 2026-08-21** against `C:\Program Files\7-Zip\7z.dll`: **364 tests, 0 failures,
`Skipped: 0`**. That last number is the one that counts — all twelve `SZ-` tests genuinely executed
rather than skipping past a missing library, and so did the six Mark of the Web tests. The
24-byte-`PROPVARIANT` correction the spike forced is confirmed by the absence of an access violation:
a 16-byte declaration would have corrupted the stack somewhere unrelated rather than failing here.

**Added**

- `NOBA.Archive.Backends.SevenZip` — the spike's interop, productionised. Open, list, extract, test for
  7Z and RAR; encrypted 7Z including encrypted headers.
- `SevenZipLibraryLocator` — absolute-path discovery only, never the working directory (T8), with the
  search list kept so an absent library produces "looked in …" rather than a shrug.
- `SevenZipBackend` — one concrete type that is either the real backend or an explanatory stand-in.
  Absence of `7z.dll` costs 7Z and RAR and nothing else; ZIP keeps working.
- **SZ-001…012**, run against real archives embedded as base64 fixtures rather than generated at test
  time — a suite that needs a 7-Zip binary installed is a suite that silently skips.

**The measurement that matters (SZ-005).** ADR-0005 holds across the interop boundary, and now by
experiment rather than by reading a header. 7-Zip's `GetStream` callback has no path in its signature,
so the native backend cannot choose a destination; extracting a hostile archive whose every entry is
refused leaves the destination provably empty. The claim was about a C++ library nobody here compiled,
which is precisely why it needed measuring.

**Fixed — a real defect carried over from the spike.** `SetOperationResult` carries no entry index; it
reports on whatever `GetStream` was last asked about. The spike keyed results by `Results.Count`, which
agrees with the entry index only while every entry extracts in order. The moment one is skipped by
policy — which is the normal case for a hostile archive — every result lands against the wrong entry.
Now the index is remembered from `GetStream`.

**Two tests failed on this change, both correctly.** The architecture suite caught `Engine` referencing
a project its layering did not allow, and the CLI contract test caught the format set changing from
`zip` to `7z,rar,zip` — it was written to fail loudly the moment 7Z appeared. The layering now also
pins the new project to **Core only**: a backend that could reference Infrastructure could open a file,
and ADR-0005 would become a convention instead of a structural property.

**Licence, enforced rather than remembered.** `SZ-011` fails the build if RAR ever advertises `Create`,
`AddEntries`, `RemoveEntries` or `RenameEntries`. The unRAR licence forbids building a RAR-compatible
compressor from its source; a comment is not a thing a future contributor can be relied on to read.

**R18 measured — 2026-08-21.** `scripts/benchmark.ps1` added; it builds solid and non-solid archives
at increasing entry counts and times both tools. Linux, one run each, `-mx=1`:

| entries | non-solid extract | solid extract | 7-Zip solid | NOBA / 7-Zip |
| ---: | ---: | ---: | ---: | ---: |
| 200 | 0.33 s | 0.49 s | 0.09 s | 5.7× |
| 800 | 0.67 s | 2.26 s | 0.03 s | 80.9× |
| 3200 | 1.48 s | **20.09 s** | 0.08 s | **257.6×** |

**The benchmark's own defaults were wrong, and hid this.** Run on Windows at 50/200/800 entries the
same code reported *"tracks linear growth — R18 may be smaller than predicted"*. It is not a platform
difference: at matched sizes the two agree almost exactly (200→800 grew ×4.6 on Linux, ×4.4 on
Windows). The Windows run simply stopped before the quadratic term takes over, and the script then
printed a conclusion its data could not support. A benchmark whose defaults produce a false all-clear
is worse than no benchmark, so: the default now reaches 3200, the verdict is computed from the **last
step** rather than first-to-last (asymptotic behaviour lives in the tail, and averaging it against the
small-n points is exactly what hid it), and below 2000 entries it reports **INCONCLUSIVE** instead of
guessing.

Non-solid grows roughly linearly. Solid grows **×40.7 for ×16 entries**, and the exponent is still
climbing between the last two points — fixed overhead dominates at small n, the quadratic term takes
over as n grows. So the prediction was right in kind and understated in degree: 20 seconds against
7-Zip's 0.08 on a single 3200-entry archive is not a tuning problem, it is the wrong algorithm.

The fix is a batch extraction path: one `Extract` call for the whole selection, with the extraction
writer supplying a stream per entry through the callback. That maps exactly onto 7-Zip's model and
keeps ADR-0005 intact — the writer still owns every file. It is an addition to `IArchiveSession`, so
it is a deliberate change rather than a patch, and it now has a number to be judged against.

**The original prediction, kept for the record.** `ExtractToAsync` extracts one entry per `Extract` call. For a
**solid** archive that re-decodes the block each time, so a solid archive of n entries is O(n²) work.
It is correct and it passes, but it will be slow on large solid archives, and the fix — a batch
extraction path where the writer supplies streams, which maps exactly onto 7-Zip's callback model —
should follow a measurement rather than precede one.

### First real-world run — 2026-08-21

The alpha was run over 60 archives from a real Downloads folder and compared against 7-Zip.
59 agreed on entry count. The one disagreement produced two findings, one of them mine.

**Fixed — detection let a lying extension overrule the bytes.**
`3.zip` was a password-protected Office document: a Compound File, `D0CF11E0A1B11AE1`, with **zero**
`PK` signatures anywhere in it. No magic byte matched, detection fell back to the `.zip` extension, and
the ZIP backend reported *"End of Central Directory record could not be found"* — which reads as "your
archive is corrupt" when the file was never an archive. `MagicByteFormatDetector` now carries a small
table of well-known non-archives (Compound File, PDF, PE, ELF, SQLite, RTF, PNG, JPEG) that is
consulted **before** the extension, and `FormatDetection.IdentifiedAs` carries the answer through to
the message: *"This file is a Compound File …, not an archive — the extension says otherwise."*
`SEC-022` pins it; `SEC-023` pins that a genuinely truncated ZIP is still reported as damaged and does
not get swept into the same bucket.

**Fixed — the comparison harness, which produced a false positive.**
`shakedown.ps1` called `7z l` without `-tzip`, so 7-Zip silently used its Compound handler and appeared
to list 10 entries from a file it had also refused to open as a ZIP. The harness reported NOBA-FAILED
against a file NOBA had judged correctly. Now `-tzip` pins the handler and a `BOTH-REFUSED` verdict
exists, because "both tools refused it" is agreement and must not read as a defect.

**Not exercised:** of the 60 archives, 51 were ASCII and 8 declared UTF-8. **Zero** needed a guessed
entry-name encoding, so the whole INT-020 legacy code-page path — the feature this product's Arabic
users most need — still has no real-world evidence behind it. Modern tools write UTF-8 correctly; only
genuinely old archives will test it.

### ZIP production hardening — legacy names and Mark of the Web — 2026-08-20

343 tests, 0 failures on Linux (6 Windows-only tests skipped and reported as skipped).

**Added**

- **Legacy entry-name decoding (INT-020).** A ZIP name is bytes; bit 11 says "UTF-8" and without it the
  bytes are in an unrecorded code page. An archive of Arabic filenames from an older Windows machine
  carries CP1256 and previously came back as `?????.txt` — which this product would then have written
  to disk under that name. Now: believe the archive's flag, else sniff strict UTF-8 (many tools write
  UTF-8 and forget the flag), else fall back to the machine's ANSI code page **and say that it is a
  guess**. `--name-encoding <cp>` overrides, and the listing reports which encoding was used.
- `LegacyCodePages` — CP437, 850, 1250–1258 as checked-in 128-entry tables with a `SingleByteEncoding`
  over them. No new dependency: `System.Text.Encoding.CodePages` would have been one package for a few
  lookup tables, and the offline profile could not have decoded Arabic at all without it. Multi-byte
  CJK code pages are deliberately not implemented and defer to a host-registered provider.
- `EntryNameEncodingInfo` on `IArchiveSession` so a UI can show the encoding and offer to reinterpret.
- **MOTW-001…006** — the Mark of the Web suite. `ReadZoneIdentifier`/`WriteZoneIdentifier` had never
  executed once in all of Phase 2 because the shared fixture disables propagation. Covers round trip,
  propagation from a tagged archive to every extracted file, local archives not gaining a zone,
  propagation being switchable, and the deliberate asymmetry: an ADS **we** write on a validated path
  is the mechanism, an ADS an **archive entry** asks for is CVE-2025-8088 and stays refused.

**Verified on Windows 2026-08-20**, shipping configuration, `Skipped: 0` on the integration suite —
so all six MOTW tests genuinely executed rather than skipping past. **349 tests, 0 failures, 0 skips**
across nine suites. R14's two enumerated gaps (planted symlink, Mark of the Web) are now closed with
executable evidence on the target platform.

### R13 CLOSED — Windows shipping configuration validated — 2026-08-20

Run 2 on Windows 11 build 26200 / SDK 10.0.400, real NuGet feed, real xUnit v3:
**13 phases, 0 failed, 330 tests, 0 skipped, 0 build errors.**

Phase 1 and Phase 2 are validated where the product ships. R13 had been open since Phase 1 and cost
exactly one real product bug that no amount of Linux testing could have surfaced. The rule it earns is
recorded in `docs/R13_WINDOWS_VALIDATION.md`: a green suite on one platform is one data point, not a
result.

R14 partially closed: SEC-011's planted-symlink defence ran and passed on Windows. Mark of the Web
propagation remains uncovered anywhere and needs a dedicated Windows-only test.

**Next:** R1 — re-prove the 7z interop and the ADR-0005 invariant against the real `7z.dll`.

### R13 run 1 — Windows shipping configuration — 2026-08-20

First build and test run with the real dependency set, on Windows 11 build 26200 / SDK 10.0.400.
Restore PASS, build PASS (70 warnings, 0 errors), 8 of 9 suites PASS, 330 tests run, 0 skipped.
See `docs/R13_WINDOWS_VALIDATION.md` §0.

**Fixed**

- **ZIP timestamps were written in UTC into a field that stores local time.** A ZIP entry's timestamp
  is an MS-DOS date/time — local wall clock, no timezone — and the provider passed it the UTC instant
  reported by the filesystem layer, so every extracted file came back wrong by the machine's UTC
  offset. Invisible to the whole Phase 2 suite because the build container runs UTC, where the
  conversion is the identity. Found by the first Windows run.
- **INT-012/INT-013 were racy tests.** `Progress<T>` posts reports to the thread pool, so callbacks ran
  concurrently and out of order: `List.Add` was a data race and "the last snapshot" was arbitrary. This
  was the unexplained 1-in-13 failure recorded in the previous entry. Now reported inline via
  `SynchronousProgress<T>`; 0 failures in 25 consecutive runs.
- Three xUnit analyzer findings surfaced by the Windows build (`xUnit2009` ×2, `xUnit2031`), all
  genuine improvements to failure messages.

**Changed**

- `scripts/test.sh` pins a non-UTC timezone (`Africa/Cairo`, overridable via `NOBA_TZ`) so timezone
  bugs cannot hide in a UTC container again. The fix is verified across UTC, whole-hour, half-hour and
  quarter-hour offsets.

**Correction**

- The prediction that SEC-011's planted-symlink test would skip on Windows was **wrong**: it ran and
  passed. That security control is verified on Windows, and the corresponding R14 gap is closed.

### R13 pre-flight and design foundation — 2026-08-20

**Added**

- `scripts/validate.ps1` — the Windows shipping-configuration run that closes R13. No `-Offline`
  switch, runs every phase past failures, writes one classified transcript to `artifacts/`.
- `docs/R13_WINDOWS_VALIDATION.md` — the runbook, the five-way failure taxonomy, and seven failures
  predicted in advance from reading code that has never executed.
- `ADR-0011`, `docs/DESIGN_TOKENS.md`, `docs/UI_DESIGN_NOTES.md` — design foundation only: tokens,
  Arabic/RTL and LTR rules, typography, semantic colours, spacing/radius/elevation, accessibility. No
  UI is built by this.

**Fixed**

- `WindowsPathVerifier.GetFinalPath` treated a return value equal to the buffer length as success. That
  is the buffer-too-small signal, and the buffer is unwritten in that case, so it would have built a
  string from uninitialised memory at ~1023-character paths.
- The handle-verification refusal now names both the resolved path and the expected root, so a
  namespace mismatch (mapped or SUBST drive) is distinguishable from an attack in one line of a log.

**Changed**

- Test projects in the shipping configuration no longer treat warnings as errors. xUnit v3's analyzers
  default to warnings and `xUnit1051` alone would fail the build before any test ran. Product code is
  unchanged and the warnings still appear in the transcript.
- `NOBA.Archive.Integration.Tests` runs without parallelisation: its fixtures expand to hundreds of
  megabytes and concurrent preallocation reads as a code defect on a machine short of disk.
- `scripts/test.sh` writes every suite's output to `artifacts/test-<timestamp>/`.

**Known issue**

- `NOBA.Archive.Integration.Tests` failed once on Linux and has passed seven runs since. The output was
  not captured; no diagnosis. Recorded in the R13 runbook §4.6 rather than dismissed.

### Phase 2 — ZIP and the extraction writer — 2026-08-20

330 tests, 0 failures, in the offline profile on Linux. See `docs/PHASE_2_REPORT.md`.

**Added**

- ZIP: open, list, info, test, extract, create — on `System.IO.Compression`, streaming, Zip64. Encrypted
  entries are detected and reported; `Encrypt` is deliberately not advertised, because the managed
  backend cannot do it.
- `ExtractionWriter` — the only component that writes to an extraction destination (ADR-0005), with
  post-open handle re-verification that fails closed.
- `ArchivePublisher` — the creation-side counterpart: a new archive is staged beside its destination and
  published atomically, so an interrupted create cannot truncate the archive it was replacing.
- `noba-ar list | info | test | extract | create`, text and `--json`.
- `SEC-###` hostile-archive suite (21), `INT-###` round-trip suite (16), `PAR-###` dual-surface parity
  suite (23).

**Fixed**

- The archive-bomb guard could miss an entry entirely: its amortised check schedule set the next
  checkpoint past the entry's final size, so an entry ending between checkpoints was never measured. A
  512 MB entry expanding 1029:1 extracted in full. The final size is now measured explicitly before
  anything is published.
- `ResourceGuard.ObserveTotals` was implemented and configured but never called during extraction, so
  `MaxTotalExpandedSize`, `MaxTotalExpansionRatio` and `MaxEntryCount` were dead settings. Now checked
  both up front against declared totals and continuously against produced bytes.
- A failed create truncated the existing archive at the target path.
- The offline test harness's `Assert.Equal` compared arrays by reference rather than structurally,
  unlike xUnit — capable of reporting both false failures and false passes.
- `scripts/test.sh` ran `--no-build` and so could assert against stale binaries; it now builds first and
  refuses to run any suite if the build fails.

**Changed**

- ADR-0005 amended: providers receive caller-owned streams for creation as well as reading.
  `NOBA.Archive.Formats` now contains no filesystem access in either direction, enforced by the
  architecture tests.
- The engine, CLI and their test suites target the platform-neutral `net10.0` again. Windows types stay
  behind `[SupportedOSPlatform]` and an `OperatingSystem.IsWindows()` guard, so the analyzer proves the
  guard and the suites remain runnable off Windows.

### Spike — 7-Zip interop — 2026-08-19

Run early, ahead of Phase 3, because a "no" would have changed the architecture. **Answer: yes.**
All 9 scenarios passed against a real `7z.so`. See `docs/SPIKE_7Z_INTEROP.md`.

**Added**

- `spikes/NOBA.Archive.Spike.SevenZip` — COM-lite interop over function-pointer vtables (no
  `[ComImport]`), exercising open, list, properties, extract, selective extract, entry refusal,
  passwords on open and extract, and teardown, against plain ZIP, plain 7z, solid 7z, and encrypted 7z
  with encrypted headers.
- `docs/SPIKE_7Z_INTEROP.md` — method, results, findings, and limits.

**Decided**

- **ADR-0003 accepted: option B**, our own interop layer. `IArchiveExtractCallback.GetStream` gives
  per-entry `ISequentialOutStream` control in every case tested, so ADR-0005 is implementable with a
  native backend and no LGPL-3.0 assembly is needed. **Risk R1 closed; decision D1 resolved.**

**Verified on Windows (2026-08-19)**

- The spike was re-run on the target platform against the real `C:\Program Files\7-Zip\7z.dll`:
  **all 9 scenarios pass**. R1 is closed on Windows, not by analogy from Linux.
- Two Windows-only defects were found and fixed on the way there:
  **`PROPVARIANT` is 24 bytes on Windows x64 and 16 in 7-Zip's Linux shim** (declaring 16 made the
  handler write past a stack slot — an access violation in an innocent function), and the BSTR
  **allocator must be OLE's `SysAllocString` on Windows**, not `malloc`.
- Two earlier findings were **corrected** by the Windows evidence: ZIP entry names round-trip
  **exactly** on Windows (the mangling was a Linux-build artifact, so no code-page workaround is needed
  for archives we create — `INT-020` stays for real-world legacy archives), and **`zstd` IS present**
  as a read-only handler on Windows (`.zst`, `.tzst`), contrary to the note written from the Linux run.
  ACE, lz4 and lzip are genuinely absent on both.
- A `spike build : rN` marker was added after a re-run silently executed a stale binary: a zip preserves
  its creation timestamps, and when those are older than existing `obj\` output MSBuild skips
  compilation. A verification run must be able to prove which build produced it.

**Findings that changed the plan**

- Teardown order is a memory-safety requirement: release the archive **before** the streams, or 7-Zip
  calls `Release` on freed memory (the spike died with SIGSEGV until this was fixed).
- A 7z with encrypted headers needs a password to **open**, not just to extract — so `OpenAsync` must be
  able to request one, and the session password cache prevents a double prompt per operation.
- ZIP entry-name decoding is lossy under the handler's default code page (an emoji name came back
  mangled; 7z round-tripped it exactly). Phase 2 must set the code page explicitly; `INT-020` extended.
- `QueryInterface` must be able to return a *different* object for a different interface.
- Handler list read from the binary: 59 handlers, only 7 support update; **Rar and Rar5 are read-only in
  the binary itself**, and ACE/zstd/lz4/lzip are absent — confirming ADR-0004 and the Phase 0 correction.
- `OLECHAR` is 2 bytes on Windows and 4 on Linux, and the Linux slots carry widened UTF-16 code units.
  String marshalling is the one part of the spike that must be re-verified on Windows.

### Phase 1 — Foundation — 2026-08-19

Solution, contracts, and infrastructure. **No archive format support yet** — that is Phase 2.
Build: PASS (0 warnings). 272 tests across 8 suites: PASS. See `docs/PHASE_1_REPORT.md`.

**Added**

- `NOBA.Archive.sln` with 17 projects, `Directory.Build.props` / `.targets`, `Directory.Packages.props`,
  `.editorconfig`, and `scripts/build.ps1` / `test.ps1` / `test.sh`.
- `NOBA.Archive.Core` — value objects (`ArchivePath`, `SafeLocalPath`, `ByteSize`, `OperationId`,
  `Password`, `CompressionLevel`, `DictionarySize`), the format model, the operation state machine,
  the closed `ArchiveErrorCode` set, and every engine contract.
- `NOBA.Archive.Security` — `PathPolicy`, `ResourceGuard`, `SecretRedactor`.
- `NOBA.Archive.Application` — `OperationQueue`, `ConflictResolutionService`, `CapabilityGate`,
  `ProgressAggregator`, `DiskSpaceEstimator`, `PauseToken`, `OperationIdFactory`.
- `NOBA.Archive.Infrastructure` — `PhysicalFileSystem`, `TempWorkspaceFactory`, `SettingsStore` with
  versioned migrations, `RedactingLoggerProvider`, `SystemClock`, `AppDataPathProvider`.
- `NOBA.Archive.Formats` — `ArchiveProviderRegistry`, `MagicByteFormatDetector`. No format code.
- `NOBA.Archive.Windows` — `GetFinalPathNameByHandle` and DLL-search hardening via `[LibraryImport]`.
- `NOBA.Archive.Engine` — `AddNobaArchiveEngine(...)`, `InstallationContract`, headless host services.
- `NOBA.Archive.Cli` — `noba-ar` with `formats`, `version`, `help`, `--json`, and the documented exit codes.
- Eight test suites plus `NOBA.Archive.TestKit`, including the dual-surface parity foundation.

**Changed**

- `PATH` demoted from discovery mechanism to **optional installer feature, opt-in and off by default**.
  Discovery goes through `InstallationContract` (`HKLM\SOFTWARE\NOBA\Archive`), and an architecture
  test asserts the engine reads no environment variable. Updated ADR-0009, NOBAOS_INTEGRATION.md,
  ARCHITECTURE.md §17.
- ARCHITECTURE.md §3 now describes the solution as built, including `NOBA.Archive.Engine` and the
  per-project target frameworks.
- `FluentAssertions` removed from the dependency plan; plain xUnit assertions are used instead
  (risk R7 closed).

**Known gap**

- The build and test runs were executed on Linux with `NobaOfflineBuild=true`, because this environment
  cannot reach nuget.org. They must be repeated on the Windows build machine with the shipping
  dependency set before Phase 2 is approved (risk R13).

### Phase 0 — Architecture & Feasibility — 2026-08-19

Documentation only. No application code, no solution, no build.

**Added**

- `README.md` — product overview, roadmap, third-party notices summary, privacy stance.
- `SECURITY.md` — security posture, twelve design commitments, reporting process.
- `docs/ARCHITECTURE.md` — layering, enforced dependency direction, domain model, `IArchiveProvider`
  abstraction, operation queue, progress model, filesystem/temp design, observability, presentation and
  Windows-integration design.
- `docs/FORMAT_SUPPORT.md` — backend evaluation, target capability matrix, RAR licensing assessment,
  7-Zip LGPL compliance plan, format hazard list.
- `docs/THREAT_MODEL.md` — assets, trust boundaries, twelve threat classes with mitigations and test IDs,
  security acceptance gate.
- `docs/DEPENDENCIES.md` — dependency matrix with licences, versions, risks, and packaging consequences.
- `docs/TESTING.md` — test pyramid, deterministic fixture generation, hostile-archive builders, full test
  catalogue, validation commands, CI plan.
- `docs/DEVELOPMENT.md` — prerequisites, repository conventions, enforced coding rules, phase workflow
  and report template.
- `docs/NOBAOS_INTEGRATION.md` — embeddable engine and `noba-ar.exe` CLI contract for NobaOS.
- `docs/PHASE_0_REPORT.md` — deliverables, acceptance criteria, risks, unresolved decisions, honest
  validation status.
- `docs/adr/ADR-0001` … `ADR-0008` — decision records.

**Decided**

- .NET 10 LTS + WinUI 3 / Windows App SDK 2.x, MVVM, DI.
- Hybrid archive backend: managed ZIP/TAR family, native `7z.dll` 26.02 for 7Z/RAR/bonus formats.
- RAR support is read-only, permanently, for licensing reasons.
- Providers decode only; a single hardened writer owns every filesystem write.
- Unpackaged EXE + WiX MSI, with a sparse MSIX package for the Windows 11 context menu.
- NobaOS integration via engine assemblies plus a CLI; IPC designed but not built.

**Reviewed**

- All Phase 0 documents went through an adversarial review pass. Corrections applied: layering diagram
  reconciled with the dependency table; architecture-rule enforcement split into assembly-reference and
  IL-inspection mechanisms; the extraction writer relocated to `Infrastructure` so ADR-0005 and the
  dependency rules agree; `File.Replace` semantics corrected; encrypted-ZIP creation reassigned to
  `7z.dll` (no managed library writes WinZip-AES); ZIP split volumes and the ACE/LZ4/ZSTD/LZip bonus
  entries removed as unsupportable; `ArchiveEntry.Path` made nullable so hostile entries stay listable
  and reportable; reserved-device-name handling separated from escape-attempt handling; the CLI password
  environment variable removed; Windows App SDK licence corrected; file-association mechanism corrected;
  `APP-###` and `PKG-###` test series added for previously dangling references.

**Decisions locked after review (2026-08-19)**

- **D6 resolved — `noba-ar.exe` ships in the standard installer** (`docs/adr/ADR-0009-cli-distribution.md`),
  installed alongside the desktop app and the native archive dependencies, with its directory recorded in
  `HKLM\SOFTWARE\NOBA\Archive\InstallPath` and added to `PATH` so NobaOS can invoke it directly.
  It is a **thin CLI host over the same engine** — no second archive engine, no duplicated
  compression/extraction logic. Enforced by an architecture rule (`Cli` may not implement
  `IArchiveProvider`, `IExtractionWriter`, `IPathPolicy`, or `IConflictResolutionService`) and by a new
  `PAR-###` dual-surface parity suite that runs the hostile `SEC-###` archives through both surfaces.
- **D7 resolved — minimum Windows 10 1809 (build 17763)**
  (`docs/adr/ADR-0010-minimum-os.md`), with Windows 10 22H2 and Windows 11 as the tested and recommended
  baseline. The three claims are kept separate everywhere, the docs state plainly that Windows 10 1809
  Home/Pro receives no Microsoft security updates, no code below `NOBA.Archive.Windows` may branch on OS
  version, Windows 11-only features degrade rather than gate, every future dependency is checked against
  1809, and release validation includes a 1809-level image (`PKG-006`) where practical.
- Added `ADR-0009`, `ADR-0010`, test IDs `PKG-006`, `PKG-007`, `PAR-001..005`, and risks `R11`, `R12`.

**Not done (by design)**

No solution, projects, or code. Phase 1 owns the foundation. No build or test was run, and no result
is claimed.
