# DROP 05 — Format & Capability Audit

> Read from the source tree at `2026-08-26-drop-04-production-extraction`, before any DROP 05 edit.
> Every row here was checked against a file in the repository or against a command actually executed;
> where something was executed, the command is shown.

---

## 1. What actually exists

### Projects that matter to formats

| Project | Contains | Runs on Linux |
|---|---|:--:|
| `NOBA.Archive.Core` | `ArchiveFormat`, `ArchiveCapabilities`, `FormatCapabilityReport`, `IArchiveProvider`, `IArchiveSession`, `IArchiveBuilder` | yes |
| `NOBA.Archive.Formats` | `ArchiveProviderRegistry`, `MagicByteFormatDetector`, `Managed/ZipArchiveProvider` | yes |
| `NOBA.Archive.Backends.SevenZip` | `7z.dll`/`7z.so` interop, `SevenZipArchiveProvider`, `SevenZipBuilder`, `SevenZipLibraryLocator` | yes, when the native library is present |
| `NOBA.Archive.Engine` | composition root — the one place providers are registered | yes |

**There are exactly two providers.** `ZipArchiveProvider` (managed) and `SevenZipArchiveProvider`
(native). Nothing else implements `IArchiveProvider` outside the test fakes.

### Native dependency

`SevenZipLibraryLocator` looks for `7z.dll` on Windows and `7z.so` elsewhere, always by absolute path,
in descending order of trust (shipped beside the binaries → configured → an installed 7-Zip → system).
Loading by bare name is deliberately never done — THREAT_MODEL T8.

On the Linux build machine the library resolves to `/usr/lib/7zip/7z.so`, which is why 7Z and RAR work
here at all. **Nothing in the repository ships a copy**, so on any machine without one, 7Z and RAR
disappear from the capability list rather than failing later — `UnavailableNativeBackend` exists for
exactly that, and it is the correct behaviour.

### NuGet

`Directory.Packages.props` carries no compression package. Both providers use the BCL
(`System.IO.Compression`) or the native library. **No third-party archive dependency exists today.**

---

## 2. Capability matrix — from source, not from documentation

Read from `SevenZipArchiveProvider.GetCapabilities`, `ZipArchiveProvider.GetCapabilities` and
`ArchiveProviderRegistry.CapabilitiesFor` (which unions the providers that claim the format).

| | 7Z | ZIP | RAR | TAR | GZIP | BZIP2 | XZ |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| Open / ListEntries / Extract / Test | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Create | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| AddEntries | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| RemoveEntries / RenameEntries | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Encrypt / EncryptFileNames | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| SolidCompression | ✅ | ❌ | ✅ *(read)* | ❌ | ❌ | ❌ | ❌ |
| SplitVolumes *(write)* | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| ReadSplitVolumes | ✅ | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ |
| RandomAccessRead | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| StreamingRead | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Comments / RecoveryRecord | ❌ | ❌ | RecoveryRecord | ❌ | ❌ | ❌ | ❌ |

**ZIP is served by two providers.** The native backend claims it at priority 5, the managed one at
priority 10, so the managed provider wins for ZIP and the native one is the fallback. The registry
*unions* capabilities across providers, which means `CapabilitiesFor(Zip)` reports the union of both —
including `StreamingRead` from one and `RandomAccessRead` from the other. That is defensible (some
provider can do it) but it is worth stating, because a caller reading the union cannot tell which
provider will be resolved.

**Encrypted ZIP is not creatable and is not claimed.** `ZipArchiveProvider` does not report `Encrypt`,
and its session refuses to extract an encrypted entry with a named error rather than producing wrong
bytes. Nothing in the product advertises encrypted ZIP.

---

## 3. The four missing formats — verified, not assumed

The detector already recognises all four. Executed on the build machine:

```
$ noba-ar list plain.tar     → UnsupportedOperation: The TAR backend does not support: Open, ListEntries.
$ noba-ar list a.txt.gz      → UnsupportedOperation: The GZIP backend does not support: Open, ListEntries.
$ noba-ar list tree.tar.gz   → UnsupportedOperation: The GZIP backend does not support: Open, ListEntries.
$ noba-ar list a.txt.bz2     → UnsupportedOperation: The BZIP2 backend does not support: Open, ListEntries.
$ noba-ar list a.txt.xz      → UnsupportedOperation: The XZ backend does not support: Open, ListEntries.
```

So the product **detects four formats it cannot open**, and says so honestly through the capability
gate. Nothing lies; the gap is real and it is a missing provider, not a missing message.

### Options considered for each

| Format | Route | Verdict |
|---|---|---|
| **TAR** | `System.Formats.Tar` — in the shared framework since .NET 7, already on the target | **Implement.** No new dependency, streaming, cancellable, PAX handles Unicode and long names |
| **GZIP** | `System.IO.Compression.GZipStream` — BCL | **Implement**, as a *single-member* container. The header's stored file name and mtime need ~40 lines of parsing the BCL does not expose |
| **BZIP2** | No BCL support. Either a new third-party package or extending the native interop to 7-Zip handler `0x02` | **BLOCKED** — see §5 |
| **XZ** | No BCL support. Same two routes (7-Zip handler `0x0C`) | **BLOCKED** — see §5 |

### The composite problem, and a real defect it has already caused

GZIP, BZIP2 and XZ are **compressors of one stream**. TAR is an **archive container**. `.tar.gz` is a
TAR *inside* a GZIP, and it is not the same thing as either.

`ArchiveFormat` currently gets this wrong:

```csharp
public static readonly ArchiveFormat GZip  = new("gzip",  "GZIP",  ".gz",  ".gz", ".tgz");
public static readonly ArchiveFormat BZip2 = new("bzip2", "BZIP2", ".bz2", ".bz2", ".tbz2", ".tbz");
public static readonly ArchiveFormat Xz    = new("xz",    "XZ",    ".xz",  ".xz", ".txz");
```

`.tgz`, `.tbz2`, `.tbz` and `.txz` are all **tar-inside-a-compressor**. Claiming them as extensions of
the plain compressor means a `.tgz` would be opened as a single-member GZIP whose one entry is an
unnamed blob that happens to be a tar file — technically not false, but useless, and it would put
`.tgz` in the open-file filter with no way to see what is inside. **This is a real modelling defect
and it predates this drop.**

---

## 4. Hard-coded format decisions found

Searched for `ArchiveFormat.<Member>`, `ArchiveCapabilities.`, and `"zip"`/`".7z"`-shaped literals
across `src/`.

| Where | What | Verdict |
|---|---|---|
| `src/NOBA.Archive.App` (all of it) | none | ✅ clean — `ARC-055` enforces this and passes |
| `BrowseViewModel.OpenableExtensions` | derives the open filter from `AvailableFormats` × `CapabilitiesFor(Open)` | ✅ correct (fixed in DROP 04) |
| `CreateOptionsViewModel.CreatableFormats` | derives from the registry × `Create` | ✅ correct (DROP 02) |
| `SevenZipArchiveProvider.FormatIdFor` | maps `ArchiveFormat` → 7-Zip handler id | ✅ correct — this *is* the provider; it is where format knowledge belongs |
| `MagicByteFormatDetector.Signatures` | byte signatures per format | ✅ correct — same argument |
| `ArchiveFormat` static list | the format vocabulary | ✅ correct, but see the `.tgz` defect above |

**No hard-coded format capability decision exists in presentation or WinUI.** The DROP 02/04 work held.
What is missing is not de-hard-coding — it is that the UI can say *that* something is unavailable but
not *why*.

---

## 5. BLOCKED — with reasons

### BZIP2 and XZ

**Reason.** Neither has a BCL implementation, and this repository has no third-party compression
dependency. The two available routes both cost more than this drop can honestly absorb:

1. **A new package** (e.g. SharpZipLib for BZIP2, a managed XZ port). Adding a third-party codec to a
   security-sensitive path means reviewing it against THREAT_MODEL, deciding a supply-chain policy the
   repository does not yet have, and owning its decompression-bomb behaviour under ResourceGuard.
   That is a decision to take deliberately, not as a side effect of a format drop.
2. **Extending the native interop** to 7-Zip handlers `0x02` (BZip2) and `0x0C` (Xz). The reading side
   is plausible — `SevenZipArchiveProvider.FormatIdFor` is a small map. The writing side is not:
   `SevenZipBuilder` hard-codes the 7z handler and there is no per-format `IOutArchive` path. And every
   claim would be **Windows-verifiable only**, because the capability then depends on which handlers the
   installed `7z.dll` carries — a machine-dependent capability this product has no way to probe today.

**Impact.** `.bz2` and `.xz` remain detected-but-unopenable, exactly as now. No regression; no promise.

**Recommended next drop.** Decide the dependency policy first, then implement both together with the
composite model below. They are one piece of work, not two.

### `.tar.gz` / `.tgz` composite

**Reason.** A composite needs a layered session — decompress the outer member, hand the inner stream to
the TAR provider, and keep progress, cancellation and ResourceGuard accounting correct across both
layers. That is a genuine architectural addition, and inventing it while also adding two providers is
how a drop stops being reliable.

**Impact.** `.tgz` files will not open. They do not open today either.

**Recommended next drop.** Together with BZIP2/XZ, as one "compressed tar" milestone.

---

## 6. Risks

| Risk | Mitigation in this drop |
|---|---|
| TAR has **no content checksum** — only header checksums | `Test` is still offered, because the format's own definition of integrity is what "test" means everywhere else in this product. The result carries an explicit finding saying content was not verified, so nobody reads a clean TAR test as a content guarantee |
| TAR is **sequential**: extracting one entry means re-scanning | Report `StreamingRead`, never `RandomAccessRead`, and implement `SupportsBatchExtraction` so the common "extract everything" path is a single pass |
| GZIP holds **exactly one member** | The builder refuses a second entry with a named error, and the create path is tested to leave no output file behind |
| GZIP's stored size is **mod 2³²** | Report it, and note the limitation for members ≥ 4 GiB |
| Adding formats could regress the **7Z batch path** | The 7Z provider is not touched at all in this drop |
| The union in `CapabilitiesFor` could over-promise as providers multiply | Documented above; no change made, because no current pair disagrees in a way that matters |

---

## 7. Recommended implementation order

1. TAR provider (`System.Formats.Tar`) — read, list, test, extract, batch extract, create.
2. GZIP provider (`GZipStream` + a header reader) — read, list one entry, test, extract, create one.
3. Fix the `.tgz` / `.tbz2` / `.tbz` / `.txz` extension claims so a composite is not mistaken for a
   plain compressor.
4. Register both in the engine; assert they appear through the registry rather than by name.
5. A format-availability service so the UI can say *why* something is unavailable.
6. Tests: the whole matrix per format, plus the negative controls.
7. Documentation: `FORMAT_CAPABILITY_MATRIX.md` generated from the running engine, not typed by hand.
