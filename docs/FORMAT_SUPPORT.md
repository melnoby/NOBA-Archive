# NOBA Archive — Format Support & Backend Evaluation

> **Status:** Phase 0 proposal. Nothing in the "Planned" columns is a claim that the feature works —
> it is a claim about what we intend to build and verify. Columns flip to "Supported" only after the
> corresponding integration test passes (rule 49).
> **Date:** 2026-08-19

---

## 1. Backend candidates evaluated

| Candidate | Type | Licence | Maintenance | Verdict |
|---|---|---|---|---|
| `System.IO.Compression` (BCL) | managed | MIT (.NET) | Microsoft, LTS | **Selected** — ZIP baseline: Deflate, Zip64, streaming, zero deps |
| **SharpCompress** 0.50.x | managed | **MIT** | Active; targets net10.0 among others | **Selected** — TAR/GZIP/BZIP2/XZ/LZip/Zstd, ZIP **read** of AES+ZipCrypto (it does not *write* encrypted ZIP), RAR read fallback |
| **7-Zip `7z.dll`** 26.02 (2026-06-25) | native | **LGPL 2.1+**, with BSD-2/BSD-3 parts and the unRAR restriction on the RAR handler | Active, upstream `ip7z/7zip` | **Selected** — 7Z create/extract, RAR/other extraction, solid, filename encryption |
| **SharpSevenZip** 2.0.109 (2026-07-10) | managed wrapper over `7z.dll` | **LGPL-3.0-or-later** | Active (JeremyAnsel fork of SevenZipSharp) | **Conditionally selected** — see §5 |
| `squid-box/SevenZipSharp` | managed wrapper | LGPL | Less active than the fork above | Rejected — superseded |
| RARLAB `UnRAR.dll` | native | RARLAB unRAR licence | Vendor-maintained | **Deferred** — 7z.dll already covers RAR read; a second native dep is not justified for 1.0 |
| Managed 7z writer (e.g. hand-rolled LZMA SDK) | managed | LZMA SDK public domain | — | Rejected for 1.0 — reimplementing 7z container writing is exactly the "write your own algorithm" trap rule 2 forbids |
| Custom compression implementations | — | — | — | **Rejected** (rule 2) |

**Selected engine strategy: hybrid.** Managed providers own ZIP and the TAR family. The native
7z provider owns 7Z creation and everything the managed side cannot do safely or quickly.
Rationale in `adr/ADR-0002-archive-backends.md`.

---

## 2. Capability matrix — target for 1.0

Legend: ● planned & will be verified · ○ deliberately not supported · — not applicable to the format

| Format | Open | List | Extract | Test | Create | Add | Delete | Encrypt | Encrypt names | Solid | Split | Backend |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|---|
| **ZIP** | ● | ● | ● | ● | ● | ● | ● | ● AES-256 **(native)** | ○ (see note) | — | ○ (1.0) | managed, **+ native for encrypted create** |
| **7Z** | ● | ● | ● | ● | ● | ● | ● | ● AES-256 | ● | ● | ● read/write | native 7z.dll |
| **RAR / RAR5** | ● | ● | ● | ● | ○ **legally excluded** | ○ | ○ | ● decrypt only | ● decrypt only | ● read | ● read | native 7z.dll |
| **TAR** | ● | ● | ● | ● | ● | ● | ○ | — | — | — | — | managed |
| **GZIP** (.gz, .tar.gz) | ● | ● | ● | ● | ● | — | — | — | — | — | — | managed |
| **BZIP2** (.bz2, .tar.bz2) | ● | ● | ● | ● | ● | — | — | — | — | — | — | managed |
| **XZ** (.xz, .tar.xz) | ● | ● | ● | ● | ● | — | — | — | — | — | — | **native (create)** / managed (read) |
| CAB, ISO, WIM, MSI, ARJ, LZH, VHD/VHDX, DMG, RPM, **ZSTD**… | ● | ● | ● | ● | ○ | ○ | ○ | — | — | — | ● read | native 7z.dll — **read-only, "bonus" tier** (measured from the binary) |

Notes — these corrections matter, because getting them wrong would ship a lie in the UI:

- **ZIP encryption is a split responsibility.** `System.IO.Compression` has no encryption at all, and
  SharpCompress reads WinZip-AES/ZipCrypto but does **not write** encrypted entries. Creating an
  encrypted ZIP therefore routes to `7z.dll`, which writes WinZip-AES. The managed path handles
  unencrypted ZIP creation. Phase 2 must verify this in an integration test before the UI offers it.
- **Legacy ZipCrypto** is *readable* for compatibility but never offered when creating — it is
  cryptographically broken, and offering it would be a security defect.
- **ZIP filename encryption is not offered.** PKWARE's APPNOTE does define Central Directory Encryption,
  but no interoperable implementation (7-Zip, WinZip, Windows Explorer) reads it, so an archive using it
  would be unopenable in practice. The Create dialog disables the checkbox with an explanatory tooltip
  rather than hiding it silently. Use 7Z when filename encryption is actually needed.
- **ZIP split volumes are out of scope for 1.0.** Neither managed library handles spanned ZIP (`.z01`).
  7z.dll's "split to volumes" produces raw `.001` chunks, which is a different thing and confuses users.
  7Z multi-volume is supported; RAR multi-volume is supported on read.
- **Deflate64:** read-only (decode only).
- The **bonus tier is measured from the shipped binary, on Windows.** The spike enumerated the handlers
  via `GetNumberOfFormats` / `GetHandlerProperty2`: **60 handlers, of which exactly seven support
  update** — `7z`, `bzip2`, `gzip`, `tar`, `wim`, `xz`, `zip`. Everything else, **including `Rar` and
  `Rar5`, is read-only in the binary itself** — an independent confirmation of ADR-0004.
- **`zstd` IS present** on the Windows build, read-only (`.zst`, `.tzst`), and can join the read-only
  tier for free once it has an integration test. **ACE, lz4 and lzip are genuinely absent**, so removing
  those during the Phase 0 review was correct. (An earlier note here said zstd was absent too; that came
  from the Linux build, which does not have it. The Windows figures are the ones that count.)
- The read-only tier on Windows is: APFS, APM, Ar, Arj, Base64, Cab, Chm, COFF, Compound, Cpio, CramFS,
  Dmg, ELF, Ext, FAT, FLV, GPT, HFS, Hxs, IHex, Iso, LP, Lzh, lzma, lzma86, MachO, MBR, MsLZ, Mub, Nsis,
  NTFS, PE, Ppmd, QCOW, Rar, Rar5, Rpm, Sparse, Split, SquashFS, SWF, SWFc, TE, Udf, UEFIc, UEFIf, VDI,
  VHD, VHDX, VMDK, Xar, Z, **zstd**. Re-confirm against the exact `7z.dll` 26.02 that ships, because the
  handler set does change between versions — which is why it is read from the binary rather than from
  memory. A format still appears in the UI only after a passing integration test (rule 49).
  See [SPIKE_7Z_INTEROP.md](SPIKE_7Z_INTEROP.md).

---

## 3. RAR — licensing assessment

### 3.1 The controlling text

The unRAR licence, reproduced identically in 7-Zip's own `license.txt` and in RARLAB's distribution:

> "The UnRAR sources may be used in any software to handle RAR archives without limitations free of
> charge, but **cannot be used to re-create the RAR compression algorithm, which is proprietary.**
> Distribution of modified UnRAR sources in separate form or as a part of other software is permitted,
> provided that it is clearly stated in the documentation and source comments that the code may not be
> used to develop a RAR (WinRAR) compatible archiver."

### 3.2 What this means for NOBA Archive

| Capability | Legal status | Decision |
|---|---|---|
| RAR **extraction** | Explicitly permitted | **Ship it** |
| RAR **listing / inspection** | Explicitly permitted | **Ship it** |
| RAR **integrity test** | Extraction-equivalent; permitted | **Ship it** |
| RAR **decryption** with a user-supplied password | Part of extraction; permitted | **Ship it** |
| RAR **creation** | Requires the proprietary compression algorithm | **Excluded. Permanently.** |
| RAR **update / add / delete entries** | Requires re-compression | **Excluded** |
| RAR **recovery-record generation** | Creation-side | **Excluded** |

### 3.3 Obligations we take on

1. `NOTICE.txt` ships with the product and the installer, containing the unRAR licence text verbatim and
   the statement that this code may not be used to develop a RAR-compatible archiver.
2. The About dialog links to the third-party notices.
3. `NOBA.Archive.Formats.SevenZipNative` carries a source-header comment restating the restriction.
4. No NOBA source file contains RAR compression logic, and no roadmap item proposes adding it.

### 3.4 How the product communicates it

RAR archives open with *Extract*, *Test*, *Search*, and *Preview* enabled; *Add*, *Delete*, *Rename* and
"Convert to RAR" are absent or disabled with the tooltip:

> **RAR archives cannot be created.** The RAR compression format is proprietary and its licence does not
> permit third-party applications to create RAR archives. You can open, extract, and test RAR archives.

The format picker in the Create dialog does not list RAR at all. We do not pretend (rule 8).

---

## 4. 7-Zip / `7z.dll` licence obligations

`7z.dll` is **LGPL 2.1-or-later**, with BSD-2/BSD-3 clauses covering the LZFSE, ZSTD and XXH64 portions,
plus the unRAR restriction on the RAR handler.

Compliance plan — all of these are conditions of the design, not afterthoughts:

1. **Dynamic linking only.** `7z.dll` is loaded at runtime from the install directory. It is never
   statically linked, never ILMerged, never embedded, and never trimmed into the app.
2. **User replaceability.** The DLL sits in `<InstallDir>\native\x64\7z.dll` as a plain, replaceable file,
   and its path is overridable in Advanced settings. This satisfies the LGPL's relinking requirement.
3. **Source availability.** The exact upstream source archive for the shipped `7z.dll` build is retained
   and offered; the About dialog links to it, and `installer/` records the version and hash.
4. **Notices.** LGPL 2.1 text, BSD notices, and the unRAR restriction all ship in `NOTICE.txt`.
5. **No modification** of 7-Zip sources in 1.0. If that ever changes, modified sources must be published.

## 5. SharpSevenZip (LGPL-3.0) — conditional selection

`SharpSevenZip` is a mature managed wrapper over `7z.dll`, actively maintained (2.0.109, 2026-07-10).
It is **LGPL-3.0-or-later**, which for a closed-source consumer means: reference it as a separate
assembly, do not merge it, and let the user replace it.

Phase 3 therefore starts with a **spike, decided on evidence, not preference**:

| Option | Pros | Cons |
|---|---|---|
| **A — use SharpSevenZip** | Saves weeks of COM interop; callbacks, streams, multi-volume already solved | LGPL-3.0 constrains packaging (no single-file/ILMerge for that assembly, no full trimming), and its API leans toward "backend writes files", which conflicts with §5.3 of ARCHITECTURE.md |
| **B — own thin P/Invoke layer** | Full control of the `ISequentialOutStream` callbacks, so extraction targets *our* streams and the backend never touches disk; no LGPL-3.0 assembly; smaller surface | ~2–3 weeks of interop work and its own test burden |

**Provisional recommendation: B**, precisely because option A's stream-ownership model fights the single
most valuable security property of this architecture (§5.3). The spike must prove that
`IInArchive::Extract` with a custom `IArchiveExtractCallback` gives us per-entry `Stream` control —
if it does, B is chosen and recorded; if it does not, A is used with a wrapper that still routes writes
through our writer. Decision recorded in `adr/ADR-0003-sevenzip-interop.md` at the end of the spike.

---

## 6. Known format hazards to test (feeds the security suite)

| Hazard | Formats | Test |
|---|---|---|
| Path traversal via `..\` | all | `SEC-001` |
| Absolute / drive-rooted / UNC entry paths | ZIP, TAR, 7Z | `SEC-002` |
| NTFS alternate data stream in the entry name (`file.txt:evil.ps1`) | RAR, ZIP | `SEC-003` (mirrors CVE-2025-8088) |
| Symlink / junction entry pointing outside the destination | 7Z, TAR, RAR | `SEC-004` (mirrors CVE-2025-11001/55188) |
| Symlink created early, then written *through* by a later entry | 7Z, TAR | `SEC-005` |
| Reserved device names (`CON`, `LPT1`, trailing dot/space) | all | `SEC-006` |
| RTL-override characters spoofing the extension | all | `SEC-007` (mirrors CVE-2023-38831 class) |
| Decompression bomb (ratio / expanded size / entry count) | ZIP, 7Z, GZIP | `SEC-008` |
| Zip quine / recursive archive | ZIP | `SEC-009` |
| Mismatched local vs central directory headers | ZIP | `SEC-010` |
| Huge declared dictionary → memory exhaustion | 7Z, XZ | `SEC-011` |
| Non-UTF-8 legacy encodings, Arabic filenames | ZIP | `INT-020` |
| `>4 GiB` member, `>65535` entries (Zip64 triggers) | ZIP | `INT-030` |
| Multi-volume with a missing part | 7Z, RAR | `INT-040` |
| Truncated / bit-flipped archive | all | `INT-050` |
