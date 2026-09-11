# Operations fixtures

## `noba-ops.7z`

The archive the Operations gate runs against.

| | |
|---|---|
| Stored | 17.0 MB |
| Extracted | 950 MB |
| Entries | 3,800 files in 12 folders |
| Total expansion ratio | **55.8 : 1** |
| Policy refuses above | **500 : 1** total, **1000 : 1** per entry |
| Top-level names | `تقارير الأداء`, `سجلات النظام`, `أرشيف العملاء`, `logs`, `data`, `exports` |

### Why this replaced the previous fixture

The previous large fixture expanded **748 : 1**. NOBA refused it, and that refusal was correct:
`ResourceGuard.ObserveTotals` is called in `ExtractArchiveHandler` **before a single byte is
written**, precisely so an archive that declares an absurd expansion never gets to write anything.
Nothing about that policy has been changed, raised, disabled or bypassed to make this gate pass.
The fixture was rebuilt instead.

The ratio is now a design input. Every 4 KiB block of every file carries 64 bytes of
cryptographic randomness, which no compressor can shrink, so the archive has a hard ceiling near
64 : 1 by construction — measured at 55.8 : 1. It is large because it holds a lot of real bytes,
not because it expands.

### Why it is shaped this way

Extraction time is dominated by per-file cost on NTFS — create, write, Mark-of-the-Web stream,
rename — not by raw throughput. 3,800 files of 256 KiB therefore run far longer than a handful of
large files of the same total size, which is what gives a person time to watch progress advance and
to press Pause, Resume and Cancel.

Arabic folder and file names are in the archive on purpose: the current-entry line and the file
table both have to survive a right-to-left path.

Reference timing on the Linux build machine: **12.6 s** for the full extraction. Windows will
differ — Defender's real-time scanning usually makes it slower.

## `scripts\make-ops-fixture.ps1`

Rebuilds the fixture, at any size, using NOBA's own `create` path.

```powershell
.\scripts\make-ops-fixture.ps1                 # the defaults above
.\scripts\make-ops-fixture.ps1 -Files 12000    # ~3 GB extracted
```

Use the second form if the default extraction finishes too quickly to click Pause on your machine.

The script measures the achieved ratio and **fails** if it ever climbs past half the policy, so a
future edit cannot quietly produce a fixture that only works because the guard was looking the
other way.
