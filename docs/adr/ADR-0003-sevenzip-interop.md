# ADR-0003 — How to talk to 7z.dll

**Status:** **Accepted (2026-08-19)** — option **B**, decided by an executed spike, not by preference.
See [SPIKE_7Z_INTEROP.md](../SPIKE_7Z_INTEROP.md).

## Context

Two ways to drive `7z.dll` from .NET:

**A. SharpSevenZip 2.0.109 (LGPL-3.0-or-later)** — mature wrapper; callbacks, multi-volume and
self-extraction already solved. But its natural usage model has the library write files, which conflicts
with ADR-0005, and LGPL-3.0 constrains packaging (that assembly must stay separate and replaceable).

**B. Our own thin P/Invoke + COM-lite interop layer** — `IInArchive` / `IOutArchive` driven through
function-pointer vtables with our own `IArchiveExtractCallback`, so every extracted byte lands in a
`Stream` we chose. No LGPL-3.0 assembly. Roughly 2–3 weeks plus its own tests.

The original decision was "prefer B, **conditional** on the spike proving that
`IInArchive::Extract` with a custom `IArchiveExtractCallback` reliably yields per-entry
`ISequentialOutStream` control across ZIP/7Z/RAR, including solid archives".

## Decision

**Option B.** The spike was run early — before Phase 2 rather than at the start of Phase 3 — and the
condition was met across every hard case:

- per-entry managed streams for plain ZIP, plain 7z, and **solid** 7z;
- **encrypted 7z with encrypted headers**, password supplied through `ICryptoGetTextPassword`;
- selective extraction of one entry from a solid archive;
- **refusing** an entry by returning a null stream, with extraction continuing — the exact mechanism a
  path-policy rejection will use;
- **zero files created by the backend**, in the working directory or in temp, during a full extraction;
- the archive itself read through a managed `IInStream`.

## Consequences

- ADR-0005 is implementable with a native backend. The security property is real, not aspirational.
- No LGPL-3.0 assembly, so the packaging constraint disappears and Native AOT stays open for the CLI.
- The interop layer must be built with these rules from the first commit, each learned the hard way in
  the spike:
  1. **Release the archive before the streams.** The handler holds its `IInStream` while open; freeing
     the stream first makes 7-Zip call `Release` on freed memory (the spike died with SIGSEGV).
     `IArchiveSession` owns both and disposes them in that order.
  2. **`QueryInterface` may return a different object** for a different interface.
     `ICryptoGetTextPassword` has a different vtable shape from `IArchiveExtractCallback`; returning the
     same pointer corrupts memory silently.
  3. **`OpenAsync` must be able to request a password.** Encrypted headers make it required at open
     time, and the session-scoped cache is what stops the user being prompted twice per operation.
  4. **Do not hard-code UTF-16 string marshalling.** `OLECHAR` is `wchar_t`: 2 bytes on Windows, 4 on
     Linux, and the Linux slots carry widened UTF-16 code units rather than code points.
- Still to verify on Windows against `7z.dll` 26.02: string marshalling above all, plus the handler
  list. Everything else transfers, because it is the same source compiled for both platforms.
- Creation (`IOutArchive`) was not exercised; that is a separate, smaller Phase 3 spike.
