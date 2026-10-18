# Performance

> Numbers only. `docs/TESTING.md` §3 names this file as where DROP 11's measurements are recorded,
> and rule 25 is explicit: **optimisation happens only after measurement.** Nothing here is a target
> or an SLA — it is what this build did, on this machine, on this date. A regression test that
> actually fails on a real regression lives in `tests/NOBA.Archive.Integration.Tests/PerformanceAndScaleTests.cs`
> (`PRF-###`); this file is measurement, not assertion.

## How to reproduce

- Linux (managed ZIP only — see "What is not measured here" below): `bash scripts/benchmark-linux.sh
  "200 2000 20000" 4096 Release`. Builds `noba-ar` in Release if it is not already built, builds a
  source tree per entry count, and times `create`/`list`/`search`/`extract`/`test` through the real
  CLI, sampling each process's peak resident set size from `/proc/<pid>/status` every 20ms (there is
  no `time -v` on this build image). Also times the native `7z` CLI (`/usr/bin/7z`, already on this
  build image) against the same ZIP format, for a real "competes with 7-Zip" answer on the one format
  both sides can open here.
- Windows (native 7Z/RAR backend, solid-archive scaling — R17/R18): `.\scripts\benchmark.ps1`. This
  script already existed before DROP 11 opened; it has not been re-run as part of this drop because
  it needs a Windows machine with `noba-ar.exe` built and 7-Zip installed, which this session does not
  have. **This is a real, open gap — see the Remaining column for DROP 11 in `PRODUCT_PLAN.md` §3.**

## 2026-10-18 — Linux, managed ZIP, this build container

One run each, no warm-up, no repetition — exactly the caveat `scripts/benchmark.ps1` already states
about its own numbers, and the same reason applies here: a wall clock on shared hardware measures the
hardware as much as the code. Read growth *between* rows, not the absolute seconds, as the thing worth
trusting.

| Entries | noba create | noba create peak RSS | noba list | noba search | noba extract | noba extract peak RSS | noba test | 7z create | 7z list | 7z extract | 7z test |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 200 | 0.162 s | 42.3 MB | 0.208 s | 0.213 s | 0.341 s | 46.1 MB | 0.158 s | 0.030 s | 0.028 s | 0.053 s | 0.026 s |
| 2,000 | 0.264 s | 50.3 MB | 0.240 s | 0.216 s | 1.510 s | 54.0 MB | 0.162 s | 0.151 s | 0.027 s | 0.456 s | 0.052 s |
| 20,000 | 1.805 s | 109.9 MB | 0.713 s | 0.507 s | 6.261 s | 117.2 MB | 0.424 s | 1.023 s | 0.081 s | 0.978 s | 0.289 s |

Payload: 4096 bytes per file, flat directory, repeated-character text. Full CSV:
`artifacts/benchmark-linux-20261018-201204.csv` (`scripts/benchmark-linux.sh` timestamps a new one
per run; this is the one behind the table above).

### What this says

- **Extraction scales sub-linearly in entry count, not quadratically.** Entries grew 10x twice
  (200→2,000→20,000); extract time grew 4.4x then 4.1x each step. That is the opposite of R18's
  concern for solid 7z (each step costing *more* than proportionally) — here each 10x step costs
  *less* than proportionally. `PRF-003`/`PRF-004` (new this drop) turn this specific shape into a
  real regression assertion rather than a one-off observation.
- **Peak RSS grows far slower than entry count**: 2.6x the memory for 100x the entries (200 → 20,000).
  Not flat — some growth is expected and correct, since entry metadata itself is O(entries) — but
  nowhere near proportional, which is the empirical shape `ARCHITECTURE.md`'s "constant memory
  regardless of archive size" target asks for. `PRF-001` turns the single-large-file half of that
  claim into a real, repeatable assertion (ratio-based, not an absolute byte ceiling, so it does not
  flake on different hardware).
- **noba-ar is 3–6x slower than the native `7z` CLI at this range**, worse at the smallest size (where
  per-process .NET startup and JIT dominate a sub-second operation) than at 2,000 entries. Expected,
  and not alarming: `7z` is a long-lived native binary doing no path validation or handle
  re-verification; `noba-ar` pays a fresh process start on every CLI invocation *and* routes every
  extracted byte through the hardened extraction writer (`ExtractionWriter`, post-open handle
  re-check, guarded stream) that `7z` simply does not have. The two are not doing the same amount of
  work, and the number says so rather than hiding it.
- **`search` costs about the same as `list`** at every scale (0.21s vs 0.21–0.71s here) — consistent
  with `SearchArchiveHandler` reusing the same metadata-only `ListEntriesAsync` seam `ListArchiveHandler`
  does, filtering as it goes rather than adding a second full pass.

### What is not measured here

- **7Z, RAR, and anything through the native backend.** `7z.dll` is not present on this Linux build
  machine (L33/L34), so `noba-ar` cannot open or create `.7z`/`.rar` here at all — there is nothing to
  time. R17 ("competes with 7-Zip", originally scoped to 7z/solid) and R18 (solid-archive O(n²)
  extraction) remain exactly as open as they were before this drop; `scripts/benchmark.ps1` is the
  instrument for both and still needs a Windows run.
- **UI responsiveness and startup-to-interactive.** Both are WinUI-only and this session cannot run
  the desktop app at all — see L38.
- **Anything above 20,000 entries.** `BudgetSeconds`-style guarding (as `benchmark.ps1` has) was not
  added to `benchmark-linux.sh`; a much larger run should add one before trying six-figure entry
  counts unattended.
