# ADR-0005 — Providers decode; only our writer touches the filesystem

**Status:** Accepted (Phase 0, 2026-08-19) — **the most important decision in this design**

## Context
The 2025 archiver CVE wave was not about broken decompression. It was about backends deciding where
bytes land: `CVE-2025-11001`/`-11002` and `CVE-2025-55188` (7-Zip symlink traversal / arbitrary file
write, actively exploited) and `CVE-2025-8088` (WinRAR NTFS alternate-data-stream path traversal,
exploited as a zero-day). Every archive backend we could adopt, including `7z.dll`, has shipped a bug of
this class.

## Decision
`IArchiveSession.ExtractToAsync(entry, Stream destination, …)` is the only extraction primitive.
Providers never call `File.Create`, never create directories, never create links, never apply attributes.
A single `ExtractionWriter` performs, for every entry:

1. `IPathPolicy.Validate(entry.RawPath, destinationRoot)` → `SafeLocalPath` or a `PathRejection`.
2. Component-by-component directory creation, refusing pre-existing reparse points.
3. Open the file handle with reparse-point-aware flags, then **re-verify with
   `GetFinalPathNameByHandle`** that the opened handle is still inside the destination.
4. Stream the decoded bytes, enforcing bomb guards as they flow.
5. Write to a temp name in the destination directory, then publish:
   `File.Move(temp, final, overwrite: false)` for a new file, `File.Move(…, overwrite: true)` or
   `File.Replace(temp, final, backup)` under an overwrite policy. (`File.Replace` alone is wrong —
   it throws when the destination does not exist, which is the normal case.)
6. Apply timestamps/attributes and propagate Mark of the Web.

**Where it lives:** `ExtractionWriter` is implemented in `NOBA.Archive.Infrastructure` — the only
project permitted to touch `System.IO` and to reference `NOBA.Archive.Windows` for the Win32 primitives
(`GetFinalPathNameByHandle`, `Zone.Identifier`). `NOBA.Archive.Application` drives it through
`IExtractionWriter`, declared in `Core`, and never sees a file handle. This is what keeps the security
property and the dependency rules in ARCHITECTURE.md §3.1 consistent with one another.

## Amendment (Phase 2, 2026-08-20) — the rule runs in both directions

Implementing ZIP made the asymmetry obvious: the original decision governed extraction only, so the
first ZIP provider legitimately opened its own output file, created directories, and renamed the result
into place. That put `System.IO.File` back inside `NOBA.Archive.Formats` and quietly reintroduced the
shape this ADR exists to remove.

The rule is therefore symmetric:

- `IArchiveProvider.OpenAsync` reads from a stream the caller opened (`ArchiveSource.FromStream`).
- `IArchiveProvider.CreateAsync(Stream output, …)` writes into a stream the caller opened.
- `IArchivePublisher` / `IArchivePublication` (Infrastructure) stage a new archive beside its
  destination and publish it atomically on success. An interrupted create leaves the staging file, so
  "compress and replace" can never truncate the archive it was updating — which matters most when
  `DeleteSourcesAfterVerifiedSuccess` is on.

`NOBA.Archive.Formats` now contains no filesystem access at all, in either direction, and the
architecture test that bans `System.IO.File`/`Directory` from it passes on the shipping code rather
than by convention.

## Consequences
- A path-handling defect in `7z.dll` or SharpCompress cannot write outside the destination through NOBA.
- One code path to review, fuzz, and fence with `SEC-###` tests instead of one per backend.
- Some backend conveniences (built-in extract-to-folder, built-in link handling) are unused; that is the
  point. Interop must expose per-entry stream control — see ADR-0003.
- Slight throughput cost from our own buffering, judged negligible against a whole vulnerability class.
