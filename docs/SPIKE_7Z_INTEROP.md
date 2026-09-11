# Spike — 7-Zip interop: can we own every destination stream?

**Date:** 2026-08-19 · **Answers:** risk **R1**, decision **ADR-0003**
**Verdict: YES — confirmed by execution on Windows against the real `7z.dll`, and on Linux against
`7z.so`. ADR-0005 is implementable.**
**Run early, at your request, instead of waiting for Phase 3.**

---

## 1. The question

ADR-0005 is the load-bearing security decision of this product: *providers decode, and only our
writer touches the filesystem.* It removes the traversal/symlink CVE class (`CVE-2025-11001`,
`CVE-2025-55188`, `CVE-2025-8088`) structurally — but **only if a native backend can be driven that
way.** If `7z.dll` insists on choosing destinations itself, the whole security argument weakens.

That was risk R1, and it was scheduled for the start of Phase 3 — after ZIP. Wrong order: if the answer
were "no", the design would change, and discovering that after Phase 2 would waste the work.

Concretely: does `IInArchive::Extract` with a custom `IArchiveExtractCallback` give **per-entry
`ISequentialOutStream` control** — for plain, solid, and encrypted archives alike?

## 2. How it was tested

`spikes/NOBA.Archive.Spike.SevenZip` — a standalone console project, deliberately outside
`NOBA.Archive.sln` because it answers a question rather than shipping.

- **Real native library:** 7-Zip's `7z.dll` on Windows and `7z.so` on Linux, loaded by absolute path
  via `NativeLibrary.Load`. **Windows is the authoritative run** — it is the target platform, and §3
  shows why running only on Linux would have left two wrong conclusions standing.
- **Hand-rolled COM-lite interop:** function-pointer vtables and `[UnmanagedCallersOnly]` thunks, not
  `[ComImport]`. Built-in COM interop is Windows-only, whereas 7-Zip's own COM-lite (`MyCom.h`) is just
  IUnknown-shaped vtables — so this approach runs on both platforms and is AOT-friendly. **This is
  exactly option B from ADR-0003.**
- **Fixtures built by the 7-Zip CLI**, so they are known-good rather than produced by the code under
  test: ASCII, Arabic (`تقرير.txt`), an emoji name, 64 KiB incompressible, 256 KiB compressible, and a
  zero-byte file — across plain ZIP, plain 7z, solid 7z (`-ms=on -mx=9`), and encrypted 7z with
  encrypted headers (`-mhe=on`).

## 3. Result

### 3.1 Windows — the authoritative run

```text
spike build  : r3 — PROPVARIANT=24, OLE BSTR on Windows, platform library discovery
library      : C:\Program Files\7-Zip\7z.dll
OLECHAR size : 2 bytes
PROPVARIANT  : 24 bytes
handlers     : 60

1. Plain ZIP  - per-entry managed streams ... PASS  5 entries, 5 GetStream calls
2. Plain 7Z   - per-entry managed streams ... PASS  5 entries, 5 GetStream calls
3. SOLID 7Z   - per-entry managed streams ... PASS  5 entries, 5 GetStream calls
4. Encrypted 7Z (incl. encrypted names)   ... PASS  5 entries, 5 GetStream calls,
                                                   password requested 1x on open / 1x on extract
5. Selective extraction of one entry      ... PASS  entry 4 of 5 solid entries, 37 bytes decoded correctly
6. Refusing an entry (null out-stream)    ... PASS  1 refused, 4 extracted, extraction continued
7. Backend writes nothing to disk         ... PASS  0 files created by the backend, in CWD or temp
8. Archive read through a managed stream  ... PASS  65,965 bytes read via managed IInStream
9. Entry-name fidelity (informational)    ... PASS  zip: exact, 7z: exact, 7z+enc: exact

SPIKE RESULT: all scenarios passed.
```

**R1 is closed on the target platform**, not by analogy from another one.

### 3.2 Linux — the earlier run

```text
library      : /usr/lib/7zip/7z.so
OLECHAR size : 4 bytes
PROPVARIANT  : 24 bytes
handlers     : 59

1. Plain ZIP  - per-entry managed streams ... PASS  5 entries, 5 GetStream calls
2. Plain 7Z   - per-entry managed streams ... PASS  5 entries, 5 GetStream calls
3. SOLID 7Z   - per-entry managed streams ... PASS  5 entries, 5 GetStream calls
4. Encrypted 7Z (incl. encrypted names)   ... PASS  5 entries, 5 GetStream calls,
                                                   password requested 1x on open / 1x on extract
5. Selective extraction of one entry      ... PASS  entry 4 of 5 solid entries, 37 bytes decoded correctly
6. Refusing an entry (null out-stream)    ... PASS  1 refused, 4 extracted, extraction continued
7. Backend writes nothing to disk         ... PASS  0 files created by the backend, in CWD or temp
8. Archive read through a managed stream  ... PASS  65,963 bytes read via managed IInStream
9. Entry-name fidelity (informational)    ... PASS  zip: 1 mangled, 7z: exact, 7z+enc: exact

SPIKE RESULT: all scenarios passed.
```

The two runs agree on every scenario. They **disagree on scenario 9 and on the handler list**, and the
Windows answer is the one that counts — see F3 and F4.

**What each scenario establishes:**

| # | Establishes |
|---|---|
| 1–3 | `GetStream` hands back an `ISequentialOutStream` we constructed, for every entry, **including in a solid archive** where entries share one compressed stream. There is no path anywhere in the signature. |
| 4 | Encryption changes nothing about stream ownership, and the password crosses as a BSTR through `ICryptoGetTextPassword` — never a command line, an environment variable, or a temp file (THREAT_MODEL.md T4). |
| 5 | Selective extraction from a solid archive works, so "extract selected entries only" is real rather than "extract everything and discard". |
| 6 | **Returning a null stream refuses one entry and extraction continues.** This is precisely how a path-policy rejection will be expressed: the backend never learns a destination, never learns *why*, and the remaining entries still extract. |
| 7 | With the process CWD pointed at a watched directory, a full extraction created **zero** files there or in temp. The claim in ADR-0005 is now an observation. |
| 8 | Even the archive's own bytes arrive through a managed `IInStream`, so input never goes through a path either. |
| 9 | Informational — see finding F3. |

## 4. Decision

**ADR-0003 resolves to option B: our own thin interop layer, not SharpSevenZip.**

The deciding argument is no longer preference. Option A's natural usage model has the library write
files; option B was preferred *conditional on the spike proving per-entry stream control*. It proved it,
across the hardest cases (solid, encrypted headers, selective, refusal). Option B therefore keeps
ADR-0005 intact **and** avoids taking an LGPL-3.0 assembly, which in turn removes the packaging
constraint that would have blocked Native AOT for the CLI.

Cost estimate stands at roughly 2–3 weeks for a production-grade layer. The spike is ~700 lines and
already covers open, list, properties, extract, selective extract, refusal, passwords on both open and
extract, and teardown — so the estimate is now evidence-based rather than a guess.

## 5. Findings that change the plan

### F1 — Teardown order is a memory-safety requirement, not a style preference

The spike **crashed with SIGSEGV** on its first run. Cause: `using` declarations unwind in reverse order
of declaration, so the `IInStream` object was freed *before* the archive was released — and 7-Zip then
called `Release` on freed memory.

> **Rule for the production layer:** close and release the archive **first**, then release the streams.
> `IArchiveSession` must own both and dispose them in that order; it must never leave the ordering to a
> caller's `using` statements.

Encoded in the spike as `OpenedArchive`. This is exactly the kind of defect that is a rare heisenbug in
production and a five-minute fix in a spike.

### F2 — A password can be required to **open**, not just to extract

A 7z written with `-mhe=on` (encrypted headers) returns `S_FALSE` from `Open` when no
`IArchiveOpenCallback` can supply a password. Consequences for the Phase 1 contracts:

- `IArchiveProvider.OpenAsync` must be able to request a password, and `PasswordRequired` must be a
  legal outcome of **opening**, not only of extracting.
- The CLI must be able to accept `--password-stdin` for `list` and `info`, not just `extract`.
- The password may be requested **twice** (once on open, once on extract), so the session-scoped
  password cache in THREAT_MODEL.md T4 is not an optimisation — without it the user gets prompted twice
  for one operation.

`IPasswordProvider` already takes a `PasswordRequest`, so no contract has to change shape — but the
open path must actually use it, and the `PasswordRequired` error code must be reachable from `OpenAsync`.

### F3 — ZIP entry-name decoding: **corrected by the Windows run**

The Linux run mangled an emoji ZIP entry name (`data 🗜️.bin` → `data ð…ï¸.bin`), and I wrote that up as
"Phase 2 must set the ZIP handler's code page". **The Windows run round-tripped it exactly**
(`zip: exact, 7z: exact, 7z+enc: exact`), so that conclusion was wrong as stated: it described a
Linux-build artifact, not a defect on the platform we ship.

What actually stands:

- **No code-page workaround is needed for archives NOBA itself creates on Windows.** The 7-Zip Windows
  handler decodes UTF-8 ZIP names correctly.
- **`INT-020` stays, for the real reason.** Legacy ZIPs produced by other tools genuinely do carry names
  in CP437, CP1256 and friends without the UTF-8 flag, and Arabic filenames from older Windows tooling
  are exactly that case. The test must cover *those*, and the code page must be configurable — but the
  motivation is real-world archives, not something the backend does to us.
- **The methodology point survives intact**, and it is the more valuable half: the spike originally
  verified decoded content *by entry name*, so a name-decoding difference masqueraded as a decode
  failure. Verification now matches **content**, with name fidelity measured separately. Phase 2's tests
  must keep the two apart.

**The honest lesson: one platform is one data point.** I generalised a Linux observation into a Phase 2
requirement, and the target platform disagreed. Findings from a non-target platform are hypotheses until
the target confirms them.

### F4 — The handler list, read from the binary (Windows figures are the ones that count)

**60 handlers on Windows** (59 on Linux). Only **seven** support update, identically on both:

```text
7z   bzip2   gzip   tar   wim   xz   zip
```

Everything else — including **Rar and Rar5** — is read-only *in the binary itself*, on both platforms.
That is an independent confirmation of ADR-0004: RAR creation is not merely something we decline to
build, it is not present to build with.

**Correction to the Phase 0 review: `zstd` IS present on Windows**, read-only, extensions `zst tzst`.
The Linux build does not have it, and I wrote "ACE, zstd, lz4 and lzip are absent" on that basis. Only
part of that holds:

| Format | Windows `7z.dll` | Linux `7z.so` |
|---|---|---|
| **zstd** | **present, read-only** | absent |
| ACE | absent | absent |
| lz4 | absent | absent |
| lzip | absent | absent |

So `.zst` and `.tzst` can join the read-only bonus tier for free — after an integration test, per rule 49.
Dropping ACE, lz4 and lzip during the Phase 0 review remains correct.

Full read-only tier on Windows: APFS, APM, Ar, Arj, Base64, Cab, Chm, COFF, Compound, Cpio, CramFS, Dmg,
ELF, Ext, FAT, FLV, GPT, HFS, Hxs, IHex, Iso, LP, Lzh, lzma, lzma86, MachO, MBR, MsLZ, Mub, Nsis, NTFS,
PE, Ppmd, QCOW, Rar, Rar5, Rpm, Sparse, Split, SquashFS, SWF, SWFc, TE, Udf, UEFIc, UEFIf, VDI, VHD,
VHDX, VMDK, Xar, Z, **zstd**.

(Measured against the 7-Zip build installed on the development machine; re-confirm against the exact
`7z.dll` 26.02 that ships with the product, since the handler set does change between versions — which
is the whole point of reading it from the binary instead of from memory.)

### F6b — `PROPVARIANT` is a different size on Windows, and the mismatch corrupts the stack

Found on the **first Windows run**, which is the whole reason for doing one.

```text
library      : C:\Program Files\7-Zip\7z.dll
OLECHAR size : 2 bytes
handlers     : 60
Fatal error.
System.AccessViolationException: Attempted to read or write protected memory.
   at NOBA.Archive.Spike.SevenZip.SevenZipLibrary.EnumerateHandlers()
```

The library loaded, exports resolved, `GetNumberOfFormats` returned 60 — and then reading the first
handler property blew up.

Cause: **`PROPVARIANT` is 24 bytes on Windows x64 and 16 bytes in 7-Zip's Linux shim.** On Windows it is
the real OLE structure — 8 bytes of header (`VARTYPE` + three reserved `WORD`s) followed by a union
whose largest members are counted-array structs like `CAUB` (`{ ULONG cElems; UCHAR* pElems; }` = 4 + 4
padding + 8 = 16). 7-Zip's `MyWindows.h` shim on Linux has a scalar-only union, so 16 bytes is right
there.

The struct was declared as 16 bytes, so on Windows `GetHandlerProperty2` wrote 24 bytes into a 16-byte
stack slot. No error at the call — just corrupted neighbouring stack and an access violation afterwards,
in a function that had done nothing wrong.

**Fix:** declare it as 24 bytes. That is correct on both platforms; Linux simply leaves the last 8 bytes
untouched. The spike now also prints `PROPVARIANT : N bytes` in its header so a future mismatch is
visible rather than mysterious.

**A second-order lesson from verifying that fix.** The first re-run on Windows reproduced the crash
*identically* — and the giveaway was that the new `PROPVARIANT` header line was absent, proving the old
binary had run. Expanding the zip had refreshed the sources with timestamps older than the existing
`obj\` output, so MSBuild skipped compilation. Hence the `spike build : rN` marker and the explicit
clean step in §8: **a verification run has to be able to prove which build produced it**, or a stale
binary will happily "confirm" a bug that was already fixed.

**The broader lesson, and it widens F6:** "the ABI is the same on both platforms" is true of the
*interfaces* and false of nearly everything around them — allocators differ, character widths differ,
and **struct sizes differ**. The production interop layer needs a startup assertion on
`sizeof(PROPVARIANT)` and `sizeof(OLECHAR)` rather than a comment saying they should match.

### F6 — String marshalling is the one part that does **not** transfer to Windows

7-Zip's `OLECHAR` is `wchar_t`: **2 bytes on Windows, 4 on Linux**. Worse, the 4-byte slots on Linux
carry *UTF-16 code units widened*, not true UTF-32 — so a non-BMP character arrives as two slots each
holding one surrogate half, and decoding the buffer as UTF-32 mangles exactly those characters.

Everything else in this spike transfers directly. **String handling must be re-verified against
`7z.dll` on Windows**, and that is the first task when the layer is built for real.

## 6. Honest limits of this spike

| Limit | Why it does not change the verdict | What still must be done |
|---|---|---|
| ~~Only run on Linux~~ | **Resolved.** Now run on Windows against the real `7z.dll`; all 9 scenarios pass | — |
| ~~String marshalling unverified on Windows~~ | **Resolved.** `OLECHAR = 2` handled, OLE BSTR allocator used, names round-trip exactly | — |
| The Windows 7-Zip build was whatever is installed on the dev machine, not pinned 26.02 | The interfaces have been stable for years; the handler set has not (F4) | Re-confirm the handler list against the exact shipped `7z.dll` |
| No `IOutArchive` (creation) exercised | The spike's question was about extraction, where the security property lives | Phase 3 spike for creation callbacks |
| No cancellation, no progress throttling, no multi-volume | Out of scope for the question asked | Phase 3 |
| Refcounting is simplified — managed side owns lifetime | Sufficient and correct for a spike; 7-Zip only needs coherent counts | Production layer needs full `SafeHandle`-based lifetime |

## 7. What changes as a result

| Document | Change |
|---|---|
| `adr/ADR-0003-sevenzip-interop.md` | Status **Proposed → Accepted**, option **B** |
| `PHASE_0_REPORT.md` | Risk **R1 closed**; decision **D1 resolved** |
| `ARCHITECTURE.md` §5 | `IArchiveProvider.OpenAsync` may raise `PasswordRequired` (F2) |
| `FORMAT_SUPPORT.md` §2 | Bonus tier replaced with the handler list read from the binary (F4) |
| `TESTING.md` | `INT-020` extended to emoji + Arabic in ZIP; name fidelity tested separately from content (F3) |
| Phase 3 plan | Interop spike is **done**; Phase 3 starts from working code rather than an open question |

## 8. Reproducing

### Windows

Requires 7-Zip installed (the spike looks in `C:\Program Files\7-Zip\`) and the .NET 10 SDK.

```powershell
# If you only have the delivered zip, expand it into the repository folder first:
Expand-Archive -Path .\noba-archive-phase1-source.zip -DestinationPath . -Force

# Then clear stale build output. This is not optional after re-expanding a zip - see the warning below.
Remove-Item -Recurse -Force .\spikes\NOBA.Archive.Spike.SevenZip\bin, `
                            .\spikes\NOBA.Archive.Spike.SevenZip\obj -ErrorAction SilentlyContinue

dotnet run --project spikes\NOBA.Archive.Spike.SevenZip
# optional: point at a specific library
dotnet run --project spikes\NOBA.Archive.Spike.SevenZip -- "C:\Program Files\7-Zip\7z.dll"
```

> **Warning — a zip drop can silently run the previous binary.** A zip preserves the timestamps the
> files had when it was created. If those are *older* than build output already sitting in `obj\` and
> `bin\` — which happens whenever the machine that produced the zip is behind the machine expanding it,
> a three-hour timezone gap being quite enough — MSBuild considers everything up to date and skips
> compilation. The run then reproduces the old behaviour exactly, and looks for all the world like a fix
> that did not work.
>
> Two defences, both now in place: **delete `bin` and `obj` after expanding** (or use
> `dotnet build --no-incremental`), and check the `spike build : rN …` line the spike prints in its
> header. If that line is missing or shows an older revision, the binary is stale and the output means
> nothing.

### Linux

```bash
sudo apt-get install -y 7zip                      # provides /usr/lib/7zip/7z.so
dotnet run --project spikes/NOBA.Archive.Spike.SevenZip
```

Exit code 0 means every scenario passed. The library is located by checking known install directories,
never by consulting `PATH` — the same discipline ADR-0009 imposes on the product.

### One Windows-specific correction, made before the first Windows run

The BSTR **allocator must match the deallocator, and it differs by platform**:

- **Windows:** 7-Zip uses the real OLE `SysAllocStringLen`/`SysFreeString`, so a password BSTR has to
  come from the OLE allocator (`Marshal.StringToBSTR`). Handing it a `malloc` block would corrupt the
  heap the moment 7-Zip freed it — a crash somewhere else entirely, minutes later.
- **Linux:** 7-Zip supplies its own implementation (`MyWindows.cpp`): a 4-byte length prefix before the
  characters, `malloc`/`free`. That layout has to be reproduced by hand.

This is finding F6 in concrete form, and it is now handled in `BStr.Allocate`. It is also a reminder
that "the ABI is the same on both platforms" is true of the *interfaces* and not of the *allocators*.
