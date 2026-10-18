# NOBA Archive — Architecture

> **Status:** Phase 0 proposal — awaiting review and approval.
> **Date:** 2026-08-19
> **Applies to:** NOBA Archive 1.0 — x64, ARM64-ready
> **Minimum technical compatibility:** Windows 10 version 1809 (build 17763)
> **Primary tested / recommended baseline:** Windows 10 22H2 and Windows 11 — see
> [adr/ADR-0010](adr/ADR-0010-minimum-os.md)

---

## 1. Purpose and scope

NOBA Archive is a professional Windows archive manager: create, inspect, extract, and verify archives
with the reliability and speed expectations set by 7-Zip and WinRAR.

This document defines the layering, the archive-engine abstraction, the concurrency and progress model,
and the rules that keep the codebase maintainable. It is the contract that later phases implement.
It deliberately does **not** contain UI mock code or provider implementations — those belong to Phases 1–6.

> **Note on "(rule N)" citations.** Throughout these documents, `(rule N)` refers to numbered section N
> of the *NOBA Archive Master Build Prompt* — the product brief this design answers to. They are
> traceability markers, not internal coding rules.

Two hard product constraints shape everything below:

1. **The UI must never touch a compression library.** Backends must be swappable without touching the UI.
2. **NOBA Archive ships standalone but must be embeddable into NobaOS.** The engine therefore has no UI,
   no WinUI, and no Windows App SDK dependency. See [NOBAOS_INTEGRATION.md](NOBAOS_INTEGRATION.md).

---

## 2. Technology decision (summary)

| Concern | Decision | Rationale |
|---|---|---|
| Language / runtime | C# 13+, **.NET 10 (LTS)** | .NET 8 (LTS, 36 months) and .NET 9 (STS, extended to 24 months) **both reach end of support on 2026-11-10** — under three months from now. .NET 10 is the current LTS and the only defensible target for a product starting today. |
| UI framework | **WinUI 3 / Windows App SDK 2.x** | Native Windows look, Fluent design, Mica/backdrop, per-monitor DPI, built-in UI Automation for accessibility, first-class shell integration. Microsoft re-committed to WinUI at Build 2026. |
| Pattern | **MVVM** with `CommunityToolkit.Mvvm` source generators | No hand-written `INotifyPropertyChanged` boilerplate; keeps ViewModels testable and free of XAML. |
| DI | `Microsoft.Extensions.DependencyInjection` | Single composition root in `NOBA.Archive.App`; no service locator anywhere. |
| Logging | `Microsoft.Extensions.Logging` + Serilog file sink | Structured logging with a mandatory redaction layer (§10). |
| Config | `Microsoft.Extensions.Configuration` + versioned JSON | Migration-aware; a malformed settings file must never block startup. |
| Tests | **xUnit v3** + FluentAssertions + a custom architecture-rule test | Architecture boundaries are enforced by a test, not by convention. |
| Packaging | Unpackaged EXE + **WiX MSI (WixToolset.Sdk v6/v7)**, plus a **sparse MSIX package** for the Windows 11 context menu | Traditional utility install (user-chosen directory, clean uninstall) without giving up modern `IExplorerCommand` integration. |

**Explicitly rejected:** Electron, any web/WebView UI, Flutter, MAUI, WPF, microservices, a database,
a background service, telemetry, cloud dependencies. None are needed and each costs startup time,
memory, or trust. See `adr/ADR-0001-ui-stack.md`.

---

## 3. Solution layout

```text
NOBA.Archive/
├── NOBA.Archive.sln
├── Directory.Build.props            # shared: nullable, TreatWarningsAsErrors, deterministic, analyzers
├── Directory.Build.targets          # Microsoft.Extensions wiring, test-project wiring
├── Directory.Packages.props         # central package version management (exact pins)
├── .editorconfig                    # analyzer severity, enforced in build
│
├── src/
│   ├── NOBA.Archive.Core/           # net10.0  - domain model + contracts. BCL only.
│   ├── NOBA.Archive.Security/       # net10.0  - path policy, bomb guards, secret redaction
│   ├── NOBA.Archive.Application/    # net10.0  - use cases, operation queue, policies
│   ├── NOBA.Archive.Infrastructure/ # net10.0  - filesystem, temp workspace, settings, logging
│   ├── NOBA.Archive.Formats/        # net10.0  - providers + format detection
│   ├── NOBA.Archive.Windows/        # net10.0-windows - Win32 primitives (handle check, MOTW, DLL search)
│   ├── NOBA.Archive.Engine/         # net10.0  - AddNobaArchiveEngine + InstallationContract
│   ├── NOBA.Archive.Cli/            # net10.0  - noba-ar.exe: parsing, output, exit codes. Nothing else.
│   ├── NOBA.Archive.UI/             # (Phase 5) ViewModels, design system, localization
│   └── NOBA.Archive.App/            # (Phase 5) WinUI 3 host and composition root
│
├── tests/
│   ├── NOBA.Archive.TestKit/               # fakes, fixture builders, offline harness
│   ├── NOBA.Archive.Core.Tests/
│   ├── NOBA.Archive.Security.Tests/
│   ├── NOBA.Archive.Application.Tests/
│   ├── NOBA.Archive.Infrastructure.Tests/
│   ├── NOBA.Archive.Formats.Tests/
│   ├── NOBA.Archive.Architecture.Tests/    # dependency direction + member-level rules
│   ├── NOBA.Archive.Cli.Tests/             # CLI contract: exit codes, JSON purity, password channel
│   ├── NOBA.Archive.Parity.Tests/          # PAR-###: both surfaces, one engine
│   ├── NOBA.Archive.Integration.Tests/     # (Phase 2) real archives on a real filesystem
│   └── NOBA.Archive.Ui.Tests/              # (Phase 5) WinAppDriver / UIA
│
├── docs/          # this folder
├── scripts/       # build.ps1, test.ps1, test.sh
├── assets/        # (Phase 5) icons, brand, file-type glyphs
└── installer/     # (Phase 9) WiX project + sparse package manifest
```

**Why `NOBA.Archive.Engine` exists.** `Application` may not reference `Infrastructure` (§3.1), so
neither of them can be the place that wires the container. `Engine` is that place: one small facade
holding `AddNobaArchiveEngine(...)` and `InstallationContract`. It is what NobaOS references, what the
CLI references, and what the desktop app will reference in Phase 5 — which is precisely how "exactly one
archive engine" stays true rather than aspirational.

**Why most projects target `net10.0` rather than `net10.0-windows`.** Only `NOBA.Archive.Windows` needs
Win32, so only it carries the Windows TFM. Keeping the rest platform-neutral at compile time means the
engine is genuinely embeddable, the unit suites run on any CI image, and nothing below
`NOBA.Archive.Windows` can quietly take a platform dependency — the compiler enforces what §16 asks for.
The product still ships Windows-only; `App` and `UI` are Windows-targeted, and `Infrastructure` picks up
the Windows TFM in Phase 2 when the extraction writer takes its dependency on the handle re-check.

### 3.1 Dependency direction (enforced)

```text
            App (Phase 5)                     Cli
                  │                            │
                  ├──────────► UI              │
                  │                            │
                  └──────────► Engine ◄────────┘        Engine is the facade both hosts
                                 │                      use: AddNobaArchiveEngine(...)
             ┌───────────┬───────┴───────┬────────────┐
             ▼           ▼               ▼            ▼
        Application  Infrastructure   Formats     (Security)
             │           │               │            │
             │           └──► Windows    │            │
             │                  │        │            │
             └──────────────────┴────────┴──► Security
                                                 │
                                                 ▼
                                               Core
                                          (BCL only, leaf)
```

Read it as: **`Core` is the leaf every arrow eventually points at.** `App` and `Cli` are the only
projects allowed to see everything, because they are the only places that wire the container.

| Project | May reference | Must **not** reference |
|---|---|---|
| `Core` | *(BCL only)* | everything else |
| `Security` | `Core` | everything else |
| `Windows` | `Core` | Application, Infrastructure, Formats, UI, App |
| `Application` | `Core`, `Security` | **Infrastructure**, Formats, Windows, UI, App, any archive library |
| `Infrastructure` | `Core`, `Security`, `Windows` | Application, Formats, UI, App |
| `Formats` | `Core`, `Security` | Application, Infrastructure, Windows, UI, App |
| `Engine` | `Core`, `Security`, `Application`, `Infrastructure`, `Formats` | UI, App, Cli |
| `UI` | `Core`, `Application` | Infrastructure, Formats, Windows, App, **any archive library** |
| `App` | all of the above | — (composition root only) |
| `Cli` | `Core`, `Security`, `Application`, `Infrastructure`, `Formats`, `Windows`, `Engine` | UI, App |

`Application` never references `Infrastructure`: it declares interfaces (`IFileSystem`,
`IExtractionWriter`, `ITempWorkspace`, `IArchiveProviderRegistry`) in `Core` and the composition root
supplies the implementations. That is what keeps the use cases testable and what lets NobaOS substitute
its own filesystem and paths.

#### Enforcement — two mechanisms, both real tests

| Rule class | Mechanism |
|---|---|
| Assembly-direction rules (the table above) | `Assembly.GetReferencedAssemblies()` over every product assembly. Note the limitation: the compiler prunes unused references, so this catches real violations but cannot prove absence of intent. |
| Member-level rules (no `System.IO.File`/`Directory` outside `Infrastructure`; no `Console.` in the engine; no `.Result`/`.Wait()`; no bare `catch (Exception)`; no static mutable state; no archive library type outside `Formats`) | **IL inspection with Mono.Cecil** (or a Roslyn analyzer where a compile-time error is better than a test failure). `GetReferencedAssemblies()` cannot see method bodies — these rules live in IL. |

Both run as xUnit tests in `NOBA.Archive.Architecture.Tests` and fail the CI build. The architecture is
machine-checked, not merely described (rule 45).

**Corollary:** `SharpCompress`, `SharpSevenZip`, and `7z.dll` appear in exactly one project (`Formats`).
Grep for them anywhere else is a build failure.

---

## 4. Core domain model

`NOBA.Archive.Core` contains records, value objects, and interfaces only — no I/O, no statics, no logging.

### 4.1 Value objects (primitive-obsession guards)

| Type | Wraps | Invariant enforced at construction |
|---|---|---|
| `ArchivePath` | entry path inside an archive | forward-slash normalised, no `..` segment, no rooted path, no drive letter, no ADS `:` marker, no NUL |
| `SafeLocalPath` | a validated absolute Windows path | absolute, canonicalised, provably inside a declared destination root |
| `ByteSize` | `long` | non-negative; formatting (`1.4 GB`) lives here, not in the UI |
| `CompressionLevel` | `int 0..9` | clamped and mapped per format |
| `DictionarySize` | `long` | power-of-two, format-legal range, memory-budget aware |
| `OperationId` | `string` | `EX-20260819-000142` shape (§9) |
| `Password` | pooled, pinned `byte[]`/`char[]` behind a `SafeHandle`-style wrapper | `ToString()` is `sealed override` returning `"***"`, `[JsonIgnore]`, non-`Serializable`, zeroed on `Dispose`. (`SecureString` is deliberately **not** used — Microsoft no longer recommends it for new development and it provides no real protection on modern .NET.) |

### 4.2 Entities and records

```csharp
public sealed record ArchiveEntry(
    ArchivePath? Path,          // null ⇔ the archive's path was rejected; see Rejection
    PathRejection? Rejection,   // why it was rejected, for the "Blocked entries" report
    EntryKind   Kind,           // File | Directory | Symlink | HardLink | Special
    ByteSize    UncompressedSize,
    ByteSize    CompressedSize,
    DateTimeOffset? Modified,
    DateTimeOffset? Created,
    DateTimeOffset? Accessed,
    FileAttributes? Attributes,
    uint?       Crc32,
    bool        IsEncrypted,
    string?     Comment,
    string?     LinkTarget,     // raw, untrusted — validated before any use
    string?     RawPath);       // exactly as stored; kept for diagnostics only
```

Other core types: `Archive`, `ArchiveFormat`, `ArchiveCapabilities`, `CompressionMethod`,
`CompressionProfile`, `CompressionOptions`, `ExtractionOptions`, `ArchiveOperation`, `OperationType`,
`OperationState`, `OperationProgress`, `ArchiveTestResult`, `ArchiveError`, `ArchiveErrorCode`,
`PasswordRequest`, `ArchiveStatistics`, `ConflictPolicy`, `ConflictDecision`.

**`RawPath` vs `Path` is a security boundary.** `RawPath` is what the archive claimed. `Path` is what we
are willing to act on. Nothing in Application or Formats may write to disk using `RawPath`.

A hostile entry must remain **listable and reportable** — the user has to be able to see that an archive
tried something, and why it was blocked. That is why `Path` is nullable and paired with `Rejection`
rather than throwing inside the `ArchivePath` constructor: `ListEntriesAsync` never fails because an
archive is malicious, it *shows* the malice. Entries with `Path == null` can never be extracted.

### 4.3 Errors

No exception from a compression library ever escapes `Formats`. Providers translate to
`ArchiveError(ArchiveErrorCode Code, string TechnicalDetail, Exception? Inner)` where `ArchiveErrorCode`
is a closed enum (`PasswordRequired`, `WrongPassword`, `CorruptData`, `UnsupportedMethod`,
`UnsupportedOperation`, `AccessDenied`, `DiskFull`, `PathTooLong`, `PathRejected`, `BombSuspected`,
`VolumeMissing`, `Cancelled`, `BackendFailure`, `IoFailure`). The UI maps codes → localised messages;
`TechnicalDetail` only ever appears behind a **Details** expander (rule 24).

---

## 5. Archive engine abstraction

### 5.1 Capabilities are explicit and per-format

```csharp
[Flags]
public enum ArchiveCapabilities
{
    None             = 0,
    Open             = 1 << 0,
    ListEntries      = 1 << 1,
    Extract          = 1 << 2,
    Create           = 1 << 3,
    AddEntries       = 1 << 4,
    RemoveEntries    = 1 << 5,
    RenameEntries    = 1 << 6,
    Test             = 1 << 7,
    Encrypt          = 1 << 8,
    EncryptFileNames = 1 << 9,
    SolidCompression = 1 << 10,
    SplitVolumes     = 1 << 11,
    ReadSplitVolumes = 1 << 12,
    Comments         = 1 << 13,
    RecoveryRecord   = 1 << 14,
    RandomAccessRead = 1 << 15,
    StreamingRead    = 1 << 16,
    PreservesUnixMode= 1 << 17,
}
```

### 5.2 Provider contract

```csharp
public interface IArchiveProvider
{
    string ProviderId { get; }                              // "managed-zip", "sevenzip-native"
    IReadOnlyList<ArchiveFormat> SupportedFormats { get; }
    ArchiveCapabilities GetCapabilities(ArchiveFormat format);
    int PriorityFor(ArchiveFormat format);                  // registry tie-breaker

    ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken ct);

    ValueTask<IArchiveSession> OpenAsync(ArchiveSource source, OpenOptions options,
                                         IPasswordProvider passwords, CancellationToken ct);

    ValueTask<IArchiveBuilder> CreateAsync(ArchiveTarget target, CompressionOptions options,
                                           IPasswordProvider passwords, CancellationToken ct);
}

public interface IArchiveSession : IAsyncDisposable
{
    ArchiveFormat Format { get; }
    ArchiveCapabilities Capabilities { get; }
    ArchiveStatistics Statistics { get; }
    bool SupportsPause { get; }        // honest capability — see §7.3 and ADR-0007

    IAsyncEnumerable<ArchiveEntry> ListEntriesAsync(CancellationToken ct);

    /// Decodes one entry. The provider NEVER opens, creates, or writes a file on disk.
    ValueTask ExtractToAsync(ArchiveEntry entry, Stream destination,
                             IProgress<long>? bytes, CancellationToken ct);

    ValueTask<ArchiveTestResult> TestAsync(IProgress<OperationProgress>? p, CancellationToken ct);
}
```

### 5.3 The single most important rule in this design

> **Providers decode. They never write to the filesystem.**

`ExtractToAsync` hands bytes to a `Stream` that `Application` supplied, after `Security` validated the
destination. Path resolution, conflict handling, directory creation, symlink decisions, and attribute
application are performed once, in our own hardened extraction writer.

This is not theoretical purity. The 2025 archiver CVE wave — `CVE-2025-11001`/`CVE-2025-11002`
(7-Zip symlink directory traversal, actively exploited), `CVE-2025-55188` (7-Zip symlink arbitrary file
write), `CVE-2025-8088` (WinRAR NTFS alternate-data-stream path traversal, exploited as a zero-day) —
were all *backends deciding where bytes land*. Taking that decision away from the backend removes the
entire vulnerability class from NOBA Archive, including from `7z.dll` itself. See
[THREAT_MODEL.md](THREAT_MODEL.md) §4 and `adr/ADR-0005-extraction-writer.md`.

### 5.4 Registry and format detection

`IArchiveProviderRegistry` resolves `(format, requested capability)` → best provider. Detection order:

1. **Content sniffing** — magic bytes from the first 512 bytes and, for ZIP, the end-of-central-directory
   trailer. Authoritative.
2. **Extension** — a hint only, and only when sniffing is inconclusive.
3. **User override** — "Open as…".

A file named `.zip` that sniffs as 7z opens as 7z, and the UI says so. Never trust the extension.

### 5.5 UI reflects capabilities dynamically

Every command in the UI binds to `Capabilities.Has(...)`. Opening a RAR archive disables *Add*, *Delete*,
*Rename*, and *Create-like* commands with a tooltip explaining why, rather than failing after the click.
There is no code path that offers an operation the selected backend cannot perform (rule 6).

---

## 6. Application layer

`NOBA.Archive.Application` owns *what happens*, never *how bytes are compressed*.

| Component | Responsibility |
|---|---|
| `ExtractArchiveHandler` | plans entries, applies security policy, resolves conflicts, drives the extraction writer |
| `CreateArchiveHandler` | enumerates sources, maps profile → provider options, drives the builder |
| `AddToArchiveHandler` / `RemoveFromArchiveHandler` | capability-gated update flows |
| `TestArchiveHandler` | integrity verification and result classification |
| `SearchArchiveHandler` | metadata-only search over the entry index (never extracts to match a name) — name/path, extension, kind, size and modified-date filters, ordinal throughout, with a sort; streams unsorted, buffers only the matched subset when sorted (DROP 07) |
| `OperationQueue` | scheduling, concurrency limits, pause/resume/cancel, lifecycle events |
| `ConflictResolutionService` | the single implementation of Overwrite/Skip/Rename/Ask + "…All" |
| `DiskSpaceEstimator` | pre-flight space check with an explicit "estimate" contract |
| `ExtractionPlanner` | expands selection → concrete work list, computes totals where determinable |

Handlers depend on `IArchiveProviderRegistry`, `IFileSystem`, `IExtractionWriter`, `ITempWorkspace`,
`IPathPolicy`, `IPasswordProvider`, `IConflictPrompt` — all interfaces declared in `Core`, all
substitutable in tests.

**Where the extraction writer lives.** `ExtractArchiveHandler` (Application) decides *what* to extract
and *under which policy*; the concrete `ExtractionWriter` that opens handles, re-verifies them with
`GetFinalPathNameByHandle`, and propagates Mark of the Web lives in `NOBA.Archive.Infrastructure`,
which is the only project allowed to touch `System.IO` and to reference `NOBA.Archive.Windows` for the
Win32 primitives. Application drives it through `IExtractionWriter` and never sees a file handle.
This keeps ADR-0005's security property and the dependency rules in §3.1 consistent with each other.

---

## 7. Operation queue, concurrency, progress

### 7.1 States

```text
Queued ──► Running ──► Completed
   │          │  ▲          
   │          ▼  │          
   │       Paused┘          
   │          │             
   └──────────┴──► Cancelled
              └──► Failed
```

Transitions are a pure function `(state, trigger) → state` in `Core`, unit-tested exhaustively.
Illegal transitions throw in Debug and are logged + ignored in Release. State lives in the queue,
never in a ViewModel.

### 7.2 Concurrency

- Default **1** concurrent CPU-bound operation (compression saturates all cores by itself);
  user-configurable 1–4. Listing/testing may overlap with an extraction.
- A per-archive-file lock prevents two operations mutating the same archive.
- `CpuUsagePreference` (Low/Normal/High) maps to worker-thread count and `PriorityClass`.
- Cancellation is a single `CancellationToken` threaded from the UI command all the way into the
  provider's inner read loop. Every loop that copies bytes checks it.

### 7.3 Pause semantics — honest by construction

`IPauseToken` is awaited inside copy loops. For the managed providers, pause is real and immediate.
If a backend cannot be paused mid-item, `IArchiveSession` reports `SupportsPause = false` and the UI
shows *Pause unavailable for this format* instead of a button that lies (rule 12).

### 7.4 Progress

```csharp
public sealed record OperationProgress(
    OperationId Id, OperationType Type, OperationState State,
    string? CurrentItem,
    long ItemsDone, long? ItemsTotal,
    long BytesDone, long? BytesTotal,
    double? Percent,                 // null ⇒ indeterminate. Never faked.
    double InstantBytesPerSecond, double AverageBytesPerSecond,
    TimeSpan Elapsed, TimeSpan? Eta,
    double? CompressionRatio);
```

`ItemsTotal`/`BytesTotal`/`Percent` are **nullable on purpose**. Streaming formats and solid archives
cannot always know the total; the UI then shows an indeterminate ring. No fake progress bars (rule 26).

Progress is produced on the worker thread into a bounded `Channel` with drop-oldest semantics and
coalesced to **10 updates/second** on the UI thread. A million-entry extraction must not generate a
million dispatcher callbacks.

---

## 8. Filesystem, temp files, large files

### 8.1 `IFileSystem`

All of `File.*`/`Directory.*` is banned outside `NOBA.Archive.Infrastructure` (architecture test enforces
this by scanning for the forbidden types). The abstraction gives us testability, uniform error
translation, cancellation, progress, and one place to apply path security.

### 8.2 Long paths and Unicode

- `longPathAware` in the app manifest **and** `\\?\`-prefixed paths in the interop layer.
- Every path is treated as UTF-16 throughout; Arabic, CJK, emoji and combining sequences are first-class.
- ZIP entries without the UTF-8 flag are decoded with a configurable legacy code page (default: OEM),
  with an override in the UI, because that is where real-world Arabic ZIPs break.
- Filenames are compared with `StringComparison.OrdinalIgnoreCase` for conflict detection but stored
  verbatim; Unicode normalisation differences (NFC/NFD) are detected and reported, never silently merged.

### 8.3 Streaming

Nothing is ever fully buffered. Rules:

- All transfer buffers come from `ArrayPool<byte>` (default rent size 1 MiB, tuned in Phase 8).
- **No un-pooled `byte[]` at or above the large-object-heap threshold of 85,000 bytes** — a per-item
  allocation that size belongs in the pool, not on the LOH.
- `FileOptions.SequentialScan | FileOptions.Asynchronous`; `SetLength` pre-allocation when the output
  size is known and trustworthy (never from unvalidated metadata).
- No `Stream.CopyTo`/`CopyToAsync` without an explicit pooled buffer.

Target: **constant memory regardless of archive size.** Multi-GB members and >1M entries are design
points, not stretch goals.

### 8.4 Temp workspace

`ITempWorkspace` allocates `%LOCALAPPDATA%\NOBA Archive\tmp\<OperationId>\` with an ACL limited to the
current user, tracks it in a manifest, and deletes it deterministically. On startup, a sweeper removes
directories whose owning process no longer exists. Extraction writes to a temp file **in the destination
directory** and publishes it on success, so a cancelled or crashed extraction never leaves a half-written
file under the real name. Publishing is:

- **new file** → `File.Move(temp, final, overwrite: false)` (atomic on the same volume);
- **overwrite policy** → `File.Replace(temp, final, backup)` when a backup is wanted, otherwise
  `File.Move(temp, final, overwrite: true)`.

`File.Replace` is *not* the general case: it throws when the destination does not exist, which is
precisely the normal extraction path.

---

## 9. Observability

Every operation gets `OperationId` = `{TYPE}-{yyyyMMdd}-{seq:D6}` (`EX-20260819-000142`). It is attached
to every log scope, surfaced in the Operation Center, and printed in error dialogs so a user can quote it.

```text
Operation: EX-20260819-000142
Type:      Extraction
Archive:   backup.7z
Provider:  sevenzip-native (7z.dll 26.02)
Status:    Failed
Reason:    Insufficient disk space (required 18.4 GB, available 7.2 GB)
```

---

## 10. Logging and redaction

Structured logging, rolling files in `%LOCALAPPDATA%\NOBA Archive\logs\`, default level `Information`,
capped total size, opt-in `Debug`/`Trace` for support scenarios.

A `RedactingLoggerProvider` sits in front of every sink. `Password` and key material are non-loggable
types whose `ToString()` returns `***`; the redactor additionally strips any structured property named
`password`, `pwd`, `key`, `secret`, or `passphrase`. **A unit test asserts that a log call carrying a
password produces output not containing the password.** The "never log passwords" rule gets a test,
not a promise.

Logs never contain file *contents*. Full paths are logged at `Debug` and above only.

---

## 11. Presentation layer

- `NOBA.Archive.UI` holds ViewModels, navigation/dialog services, converters, the design system
  (`Tokens.xaml`, `Controls.xaml`, light/dark/high-contrast dictionaries), and `.resw` resources.
  It references `Application` and `Core` — never a compression library, never `Formats`.
- `NOBA.Archive.App` is the WinUI 3 host: `App.xaml`, windows, views, and the DI composition root.
- **Zero business logic in code-behind.** Code-behind may only wire XAML-only concerns (focus, visual
  states, drag-drop plumbing that requires the view).
- A virtualised, flattened entry index backs the file list so a 1M-entry archive scrolls smoothly;
  the tree and the list share one model and never re-read the archive to redraw.
- Localisation: `.resw` per language (en-US, ar-EG), `FlowDirection` bound to the resolved language,
  mirrored icons where directional. No user-facing string literal exists outside a resource file —
  an analyzer rule flags them.
- Theming: Light / Dark / System via a token layer. Individual controls are never styled ad hoc (rule 29).
- Accessibility: `AutomationProperties` on every interactive element, logical tab order, visible focus
  visuals, full keyboard operability, high-contrast validated. Checked by the UI test suite.

---

## 12. Windows integration

| Feature | Mechanism |
|---|---|
| Windows 11 context menu | `IExplorerCommand` COM server registered by a **sparse MSIX package** alongside the unpackaged app |
| Windows 10 fallback | Classic `ContextMenuHandlers` registry entries, installed only if the user opts in |
| File associations | ProgIDs under `HKCU\Software\Classes`, capabilities declared under `RegisteredApplications`. Since Windows 8 an app **cannot** make itself the default handler programmatically — we register capabilities and open `IApplicationAssociationRegistrationUI` / the Settings page so the *user* chooses |
| Drag & drop | `IDropTarget` / WinUI drag events with an explicit intent rule (§13) |
| Open With | `SupportedTypes` + `ApplicationCompany`/`ApplicationName` |
| Mark of the Web | Extracted files inherit the archive's `Zone.Identifier` ADS |

Shell integration is off by default for anything beyond a single top-level cascading entry (rule 18) and
is fully removed by the uninstaller, verified by a packaging test.

## 13. Drag & drop intent

| Drop target | Current state | Action |
|---|---|---|
| Main window, no archive open | — | Open if a single archive; otherwise start *Create archive* pre-filled |
| Main window, archive open, provider has `AddEntries` | — | Prompt: *Add to `<archive>`* / *Open* / *Create new archive* |
| Main window, archive open, provider lacks `AddEntries` | e.g. RAR | Offer *Open* or *Create new archive* only — never a failing "add" |
| Entry list → Explorer | — | Extract selection to the drop location via the normal hardened writer |

The intent is always shown before a destructive or long-running action begins. Nothing is inferred silently.

---

## 14. Settings

Versioned JSON at `%LOCALAPPDATA%\NOBA Archive\settings.json`, `{"schemaVersion": n, ...}`, with an
ordered migration chain `n → n+1`. Load failures fall back to defaults, move the bad file aside as
`settings.corrupt-<timestamp>.json`, and surface a non-blocking notification. **Startup never fails
because of settings** (rule 22). Writes go to a temp file in the same directory and are published with
`File.Move(temp, settings, overwrite: true)`.

Categories: General, Appearance, Archive Defaults, Extraction, Compression, Security, Performance,
Explorer Integration, Notifications, Advanced.

---

## 15. Extensibility (designed, not built)

Extension points that cost nothing today and enable the roadmap: `IArchiveProvider` (new formats),
`IUpdateService` (no URL hard-coded anywhere; disabled in 1.0), `IStorageProvider` (future cloud
sources), the CLI surface, and portable-mode via an `IAppDataPathProvider`. No plugin loader, no
manifest format, and no host process is built in this milestone — designing the seam is enough (rule 54).

---

## 16. Supported Windows versions

| Statement | Value |
|---|---|
| Minimum technical compatibility | **Windows 10 version 1809 (build 17763), x64** |
| Primary tested / recommended baseline | **Windows 10 22H2 and Windows 11** |
| Build / development machine | Windows 11 with the current Windows App SDK toolchain |

These are three different claims and the documentation, installer, and About dialog keep them separate.
The floor exists because the Windows App SDK genuinely supports 1809 and because Windows 10 Enterprise
LTSC 2019 (build 17763) remains in extended support until 2029-01-10 — managed and appliance fleets are
a real population. It is **not** an implication that Windows 10 1809 Home/Pro still receives security
updates from Microsoft: it does not, and neither does consumer Windows 10 22H2 outside ESU.

Rules that follow from this:

- The minimum rises only when a concrete required dependency or feature makes 1809 technically
  impossible — never for convenience, and the reason is recorded in ADR-0010.
- **No code below `NOBA.Archive.Windows` may branch on OS version.** Where a Win32 primitive differs,
  `Windows` feature-detects the API (`GetProcAddress`), never `Environment.OSVersion`.
- Windows 11-only capabilities degrade rather than gate: the modern `IExplorerCommand` context menu
  falls back to a classic opt-in shell handler, and Mica falls back through the theme token layer.
  The archive engine — every format, every security control, the CLI — behaves identically on both.
- Every dependency added later is checked against 1809, not just against current Windows.
- Release validation includes a Windows 10 1809-level image where practical (`PKG-006`); if it could not
  be run for a release, the release notes say so rather than implying coverage.

Details and the servicing-status table: [adr/ADR-0010](adr/ADR-0010-minimum-os.md).

## 17. The CLI is a host, not a second product

`noba-ar.exe` ships **in the standard installer**, next to the desktop application and
`native\x64\7z.dll`. A host locates it through an explicit contract — `HKLM\SOFTWARE\NOBA\Archive`
with `InstallPath`, `CliPath`, and `Version` — defined once in
`NOBA.Archive.Engine.InstallationContract`.

**`PATH` is an optional installer feature, not a discovery mechanism.** It is opt-in, off by default,
and never a runtime dependency of the engine, the CLI, or NobaOS integration; a machine with it disabled
is fully supported. Nothing below the host reads an environment variable, and an architecture test keeps
it that way.

`NOBA.Archive.Cli` contains **only**: argument parsing, output shaping (`--json` / human text), exit-code
mapping, and non-interactive `IPasswordProvider` / `IConflictPrompt` implementations.

> **There is exactly one archive engine.** No compression, extraction, path validation, conflict
> resolution, progress, queueing, or bomb-guard logic may exist in `NOBA.Archive.Cli`. It resolves the
> same `Application` handlers, `Formats` providers, `Security` path policy and `IExtractionWriter` as the
> desktop UI, through the same `AddNobaArchiveEngine(...)` registration.

Enforced by an architecture rule (`Cli` may not contain a type implementing `IArchiveProvider`,
`IExtractionWriter`, `IPathPolicy`, or `IConflictResolutionService`) and by integration tests that run
each scenario — including the hostile `SEC-###` archives — through both surfaces and assert identical
outcomes. See [adr/ADR-0009](adr/ADR-0009-cli-distribution.md).

## 18. Non-goals for 1.0

Plugin system, cloud storage, archive conversion UI, duplicate detection, benchmark mode, backup
profiles, telemetry of any kind, auto-update delivery, ARM64 shipping binaries (the code stays
architecture-clean; only x64 is validated and shipped in 1.0).

---

## 19. Cross-references

- Format and capability matrix → [FORMAT_SUPPORT.md](FORMAT_SUPPORT.md)
- Dependencies and licences → [DEPENDENCIES.md](DEPENDENCIES.md)
- Threat model → [THREAT_MODEL.md](THREAT_MODEL.md)
- Test strategy → [TESTING.md](TESTING.md)
- NobaOS embedding → [NOBAOS_INTEGRATION.md](NOBAOS_INTEGRATION.md)
- Decision records → [adr/](adr/)
