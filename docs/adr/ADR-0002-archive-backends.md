# ADR-0002 — Hybrid archive backend

**Status:** Accepted (Phase 0, 2026-08-19)

## Context
We need ZIP, 7Z, RAR, TAR, GZIP, BZIP2, XZ with real create/extract/test performance, encryption,
solid compression, and split volumes — without implementing compression ourselves (rule 2).

## Options
1. **Fully managed** (`System.IO.Compression` + SharpCompress, MIT): simplest deployment and licensing,
   but no 7Z creation, no 7z filename encryption, weaker large-archive performance.
2. **Native `7z.dll` for everything**: fewest code paths, but a native dependency on every operation and
   unit tests that cannot run without the binary.
3. **Hybrid**: managed for ZIP and the TAR family; native `7z.dll` for 7Z creation, RAR/other extraction,
   solid mode and filename encryption.

## Decision
Option 3, with the split drawn on capability rather than on format name:

- **Managed** (`System.IO.Compression` + SharpCompress 0.50.x, MIT): ZIP read and unencrypted ZIP
  create/update; TAR, GZIP, BZIP2 create and extract; XZ and LZip read.
- **Native `7z.dll` 26.02** (LGPL-2.1+): 7Z create/extract/update, **encrypted ZIP creation**
  (no managed library writes WinZip-AES), XZ creation, RAR extraction, multi-volume 7Z, solid mode,
  7Z filename encryption, and the read-only bonus tier.

## Consequences
- The product ships a native binary; it is loaded by absolute path, hash-pinned, and user-replaceable
  (an LGPL requirement, and a defence against DLL hijacking).
- `7z.dll`'s own CVE history becomes ours to track — mitigated structurally by ADR-0005.
- Most unit tests run without the native binary because ZIP/TAR paths are managed; Formats tests that
  need it are tagged and skip with a clear message when it is absent.
- SharpCompress is pre-1.0; its version is pinned exactly and it sits behind our provider interface so a
  future swap touches one project.
