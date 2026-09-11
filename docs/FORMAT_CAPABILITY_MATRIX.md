# NOBA Archive — Format Capability Matrix

> **Generated from the running engine**, not written by hand. Reproduce with:
> ```
> noba-ar formats --json
> ```
> Anything this table claims is a capability some provider reported at the moment it was produced.
>
> Produced from `NOBA Archive 0.1.0` on the Linux build machine, where the 7-Zip native
> library resolved to `/usr/lib/7zip/7z.so`. **On a machine without it, 7Z and RAR appear with no
> capabilities at all** rather than disappearing — see "When a backend is missing" below.

---

## 1. Formats and where they come from

| Format | Id | Primary | All extensions | Provider |
|---|---|---|---|---|
| 7Z | `7z` | `.7z` | `.7z` | `sevenzip-native` (needs 7z.dll / 7z.so) |
| GZIP | `gzip` | `.gz` | `.gz` | `managed-gzip` (BCL `System.IO.Compression`) |
| RAR | `rar` | `.rar` | `.rar` `.r00` | `sevenzip-native` (needs 7z.dll / 7z.so) |
| TAR | `tar` | `.tar` | `.tar` | `managed-tar` (BCL `System.Formats.Tar`) |
| ZIP | `zip` | `.zip` | `.zip` `.zipx` | `managed-zip` (BCL), `sevenzip-native` as fallback |

---

## 2. Capabilities

| Capability | 7Z | GZIP | RAR | TAR | ZIP |
|---|:--:|:--:|:--:|:--:|:--:|
| Open | ✅ | ✅ | ✅ | ✅ | ✅ |
| Browse (list entries) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Extract | ✅ | ✅ | ✅ | ✅ | ✅ |
| Test | ✅ | ✅ | ✅ | ✅ | ✅ |
| Create | ✅ | ✅ | — | ✅ | ✅ |
| Add entries (many per archive) | ✅ | — | — | ✅ | ✅ |
| Remove entries | — | — | — | — | — |
| Rename entries | — | — | — | — | — |
| Password / encryption | ✅ | — | — | — | — |
| Encrypt entry names | ✅ | — | — | — | — |
| Solid compression | ✅ | — | ✅ | — | — |
| Write split volumes | — | — | — | — | — |
| Read split volumes | ✅ | — | ✅ | — | — |
| Comments | — | — | — | — | — |
| Recovery record | — | — | ✅ | — | — |
| Random access read | ✅ | — | — | — | ✅ |
| Streaming read | — | ✅ | — | ✅ | ✅ |
| Preserves Unix mode | — | — | — | ✅ | — |
| Test verifies contents | ✅ | ✅ | ✅ | — | ✅ |

---

## 3. What the rows mean where it is not obvious

**Add entries** is "this archive can hold more than one file". GZIP creates but does not add: a `.gz`
holds exactly one compressed member, so asking for a second is refused with a reason rather than
silently keeping one of them.

**Test verifies contents** is the row that changes what a clean verdict *means*. ZIP, 7Z, RAR and GZIP
each store a checksum over an entry's uncompressed bytes, so a passing test detects a flipped bit. TAR
checksums its headers and nothing else — a tar whose file data was altered in place passes every check
the format defines. The UI says so on a clean TAR verdict instead of reporting "no checksum errors",
which for that format would be a sentence with no referent.

**Update** — modifying an archive in place — is false for every format, and is the conjunction of
Add and Remove rather than a flag of its own. No provider implements Remove yet.

**Write split volumes** is false everywhere. Reading them is a separate capability and 7Z and RAR have it.

---

## 4. Deliberately not supported

| Format | Status | Reason |
|---|---|---|
| **BZIP2** | **BLOCKED** | No BCL implementation. The two routes are a new third-party codec — a supply-chain decision this repository has no policy for yet — or extending the native interop to 7-Zip handler `0x02`, whose write path does not exist and whose availability would depend on which handlers the installed `7z.dll` carries. Detected, never opened. |
| **XZ** | **BLOCKED** | Identical reasoning; 7-Zip handler `0x0C`. |
| **`.tgz` / `.tbz2` / `.tbz` / `.txz`** | **DEFERRED** | These are a TAR *inside* a compressor, not the compressor itself. Opening one needs a layered session that unwraps the outer member and hands the inner stream to the TAR provider, with progress, cancellation and ResourceGuard accounting correct across both layers. Until that exists the extensions are not claimed, because claiming the extension is claiming the capability. |
| **RAR creation** | **PERMANENT** | ADR-0004. There is no complete, licensable RAR writer, and a partial one would produce archives that other tools read wrongly. |
| **Encrypted ZIP** | Not claimed | The managed provider cannot decrypt and does not report `Encrypt`. An encrypted entry is refused by name rather than producing wrong bytes. |

---

## 5. When a backend is missing

7Z and RAR need the 7-Zip native library. When it cannot be loaded, `UnavailableNativeBackend`
registers both formats with **no capabilities** rather than removing them, so that:

- nothing is offered — every capability reads false, and the UI disables the action;
- nothing crashes — the capability gate refuses cleanly;
- **the reason survives** — the error names the paths that were searched, and the UI can say
  "7Z is unavailable in this installation because the engine component it needs is missing"
  instead of silently showing a shorter list.

TAR, GZIP and ZIP are managed and have no such dependency: they are present on every machine this
engine runs on.

---

## 6. Known format limitations

| Format | Limitation |
|---|---|
| TAR | No checksum over file contents (see §3). Sequential — extracting one entry rescans, which is why batch extraction is implemented. Written as PAX, so long names and UTF-8 survive; ustar's 100-byte name limit does not apply. |
| GZIP | Holds one file. The stored name is ISO-8859-1 per RFC 1952, so a name that cannot be represented there — Arabic, Japanese — is **not** stored and the reader falls back to the archive's own name minus `.gz`. Content always round trips exactly. The stored size is modulo 2³², so it is exact only below 4 GiB. |
| ZIP | Managed provider cannot decrypt. Entry-name encoding is a guess when the archive does not declare UTF-8; the UI says so rather than presenting a guess as a fact. |
| 7Z / RAR | Require the native library. |
