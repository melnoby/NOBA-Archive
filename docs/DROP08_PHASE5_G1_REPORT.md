# DROP 08 Phase 5 — G.1 feasibility gate — RESULTS

**Linux container 2026-08-28; then executed on real Windows 11 the same day.
→ §6 is the Windows result and supersedes §5–§7 of the Linux figures. GATE 1: PASS, with one
anomaly characterised rather than averaged away.**

G.1 asked one question: *can a native host ask .NET for a context menu fast enough, and safely
enough, that **B1** (`com:SurrogateServer` + `hostfxr`) is a real option, or does **B3** (a C++
predicate over a data table) become the decision?*

The answer is **PROCEED B1**, with two revisions to §0–J and one risk that G.1 was forbidden to
test and therefore did not.

---

## 1. The ten items, in the order they were asked

### 1. Exact files changed

Four, all inside `harness/phase5-g1/`, none in `src/`, `tests/`, `scripts/` or `docs/adr/`:

| File | New? | What it is |
|---|---|---|
| `harness/phase5-g1/bridge/NOBA.Phase5.G1.Bridge.csproj` | new | managed half; references `NOBA.Archive.Engine` |
| `harness/phase5-g1/bridge/BridgeExports.cs` | new | `NobaShellQuery` / `NobaShellFree` / `NobaShellWarm` over the **real** `ShellMenuBuilder` |
| `harness/phase5-g1/host/g1host.cpp` | new | portable C++ host (g++ / MSVC), sections A, B0, B, C, C2, C3, F, F2, G |
| `harness/phase5-g1/run-g1.ps1` | new | Windows driver — builds both halves with MSVC and runs the identical measurement |

Plus this report, and a pointer to it at §22 of `DROP08_REAUDIT.md`.

The harness lives **outside `src/`** on purpose. §J of the plan proposes `NOBA.Archive.ShellBridge`;
this is not that project and must not be mistaken for it. It is expected to be deleted or rewritten
now that the question is answered.

### 2. Exact files NOT changed

Every file on the stop-condition list is byte-untouched:

`ShellMenuBuilder.cs` · `ShellInvocation.cs` (`ShellInvocations`) · `ShellLauncher` ·
`ArchiveActionRequest.cs` · `ShellSelection.cs` · `FormatCapabilityReport.cs` ·
`scripts/shell-register.ps1` · every other action/model contract · `docs/adr/ADR-0006`.

**Verified rather than asserted.** Nothing outside `harness/phase5-g1/` has a modification time later
than the harness's own first file:

```
$ find . -type f -newer harness/phase5-g1/bridge/NOBA.Phase5.G1.Bridge.csproj \
      -not -path '*/bin/*' -not -path '*/obj/*' -not -path './artifacts/*'
./harness/phase5-g1/bridge/BridgeExports.cs
./harness/phase5-g1/host/g1host
./harness/phase5-g1/host/g1host.cpp
./harness/phase5-g1/run-g1.ps1
```

No `IExplorerCommand`. No MSIX. No signing. No COM class. No registry write. Nothing registered.

### 3. Build result

```
dotnet build NOBA.Archive.sln -c Debug -p:NobaOfflineBuild=true
Build succeeded.  0 Warning(s)  0 Error(s)   (7.59s)
```

The harness bridge builds clean under the same offline flags. The C++ host builds clean under
`g++ -std=c++17 -O2 -Wall -pthread` with no warnings. **The App project is still not compilable on
Linux and was not compiled**; nothing in G.1 touches it.

### 4. Test result

**735 passed, 6 skipped, 0 failed.** Unchanged from Phase 4 — as it should be, since no product code
changed.

| Project | Result |
|---|---|
| Core | 95/95 |
| Security | 41/41 |
| Formats | 18/18 |
| Infrastructure | 19/19 |
| Application | 35/35 |
| Presentation | 240/240 |
| Cli | 33/33 |
| Integration | 161/161, 6 skipped |
| Parity | 23/23 |
| Architecture | 70/70 |

### 5. Cold-start measurements

Five separate processes, `--split`, 50 warm iterations each.

| | min | avg | max |
|---|---:|---:|---:|
| **cold total — process start → first menu** | **147.72 ms** | **153.40 ms** | **162.79 ms** |
| of which: CLR activation + assembly load (§A) | ~56 ms | ~58 ms | ~61 ms |
| of which: engine construction (`NobaShellWarm`) | 37.29 ms | 38.83 ms | 40.91 ms |
| of which: first `ShellMenuBuilder` call once warm | 54.10 ms | 61.56 ms | 71.89 ms |

Activation broken down (§A, representative run):

| step | ms |
|---|---:|
| `dlopen` hostfxr | 0.09 |
| `hostfxr_initialize_for_runtime_config` | 1.55 |
| `hostfxr_get_runtime_delegate` (**starts the CLR**) | 18.10 |
| `load_assembly_and_get_function_pointer` (**resolves `NobaShellQuery`**) | 36.07 |

**The split is the useful part of this figure.** ~95 ms of the ~153 ms is activation plus engine
construction, and **all of it can happen at surrogate startup, off the critical path of any menu**.
What cannot be moved that way is the ~62 ms first `ShellMenuBuilder` call — first-call JIT of the
Core and capability paths. §9 says what to do about it.

### 6. Warm-start measurements

| | range across 5 runs |
|---|---|
| warm median | **0.02 – 0.04 ms** |
| warm p95 | **0.03 – 0.07 ms** |
| warm max | 0.20 – 0.24 ms |

**Three to four orders of magnitude inside any menu budget.** `SHELL-020` and `SHELL-021` are not
under threat in steady state; they are only in question for the first menu of a cold surrogate.

**Concurrency, which single-threaded numbers would have hidden.** A surrogate serving Explorer is not
called on one thread — `GetState` can be asked for several commands at once, and the shell re-asks on
a background thread after `E_PENDING`, which is two threads by construction.

| §C2 — 4 threads × 25 calls, warm | |
|---|---|
| wall clock for all 100 calls | 1.91 – 3.67 ms |
| concurrent median | 0.02 ms |
| concurrent p95 | 0.07 – 0.08 ms |
| failures | **0** |

| §C3 — 4 threads racing an **unbuilt** engine (own process, 3 runs) | |
|---|---|
| wall clock until all four returned | 109.50 – 137.50 ms |
| spread between the four threads | < 3 ms — they queue on the lock and release together |
| failures | **0 in 3 of 3 runs** |

The double-checked lock in `EnsureEngine` builds the engine exactly once and the losing threads block
for the full construction. That is correct, and it is also the reason the first menu must not be on
the critical path.

### 7. Failure/timeout behaviour

| | Behaviour | Verdict |
|---|---|---|
| managed exception (`fault: throw`) | `rc = -99`, no exception crosses the ABI, **next call returns `rc = 0`** | the host survives and keeps working |
| malformed JSON | `rc = -99`, refused | correct |
| empty request | `rc = -2`, refused | correct |
| slow managed call (2 s injected) | 2000.39 – 2000.73 ms, **blocks the caller for the whole duration** | see below |

**The bridge has no timeout of its own, and G.1 recommends it never gets one.** A timeout inside the
bridge would abandon a call that is still running in a shared surrogate. The caller owns the deadline,
and on the real surface the caller's mechanism is `GetState`'s `fOkToBeSlow` / `E_PENDING`. This is
the platform's answer to exactly this situation, not a workaround for ours.

`SHELL-073`'s principle carries: when the bridge cannot answer, **nothing is started**.

### 8. Is B1 still viable?

**Yes.** Nothing measured argues against it, and two things argue for it:

- warm cost is ~0.02 ms, so the steady state is free;
- the cold cost decomposes, and ~95 ms of ~153 ms is startup work that a surrogate can do before any
  user right-clicks.

**B3 remains fallback only and G.1 produced no evidence for taking it.** Per the authorisation it was
not implemented and not designed around.

**The one thing that could still overturn this is untested.** Question **E** — isolation from
`explorer.exe` — is a property of `com:SurrogateServer` registration, which G.1 was explicitly
forbidden to build. A standalone `.exe` host proves nothing about it in either direction. If the
surrogate turns out not to isolate as documented, the reason B2 was rejected applies to B1 as well,
and that is a Phase 5 finding, not a G.1 one.

### 9. Assumptions in §0–J that must be revised

Four. Two are substantive.

**R1 — §B.3, the ABI grows from two functions to three.** The plan fixed it at `NobaShellQuery` +
`NobaShellFree`. The measurement is the argument for a third:

```c
int32_t NobaShellWarm(void);   // build everything, answer nothing
```

Without it, activation, engine construction and the first menu are one undifferentiated ~153 ms and
there is nothing to do but return `E_PENDING` and hope. With it, ~95 ms moves to surrogate startup.
The plan's own §B.1 already permits the C++ layer to "cache the CLR host"; this is that permission
made callable. **It decides nothing and returns no data**, so §B.2's prohibitions are untouched.

**R2 — `NobaShellWarm` must build a menu, not just the container.** This is the revision the split
measurement forced and it was not visible before. Warming the DI container and the provider registry
costs ~39 ms; the *first `ShellMenuBuilder` call after that* still costs ~62 ms, because first-call
JIT of the Core and capability paths has not happened. So warming that stops at the container leaves
the larger half of the cost sitting in the first user-visible menu. `NobaShellWarm` should issue a
synthetic query against a throwaway selection and discard the result.

With R2, `E_PENDING` becomes a **safety net for the case where a menu is drawn before warming
finishes**, rather than the expected path for every cold surrogate. §0.2 stated it as the expected
path; that is now too pessimistic and should be restated.

**R3 — §E.2's `E_PENDING` row is worded too narrowly.** It reads "CLR not yet warm". The measurement
shows two distinct costs, and the CLR is the smaller one. It should read *"bridge not yet warm"*,
covering activation, engine construction and first-menu JIT together.

**R4 — §G.1's own description was wrong, and deliberately not followed.** It specified "a minimal C++
DLL that hosts the CLR, calls **a trivial managed function**, and logs cold and warm round-trip
times". A trivial function would have measured the CLR and nothing else — it would have reported a
comfortable number and told us nothing about the cost we actually care about, which is the engine and
the first `ShellMenuBuilder` call. The harness calls the **real** `ShellMenuBuilder` over the **real**
provider registry instead. §G.1's wording should be corrected so the next reader does not think the
cheaper measurement was the plan.

**Not revised:** the B1-over-B3 choice · §B.2's prohibitions · §C's data flow · §D's hierarchy ·
§E.1 · §E.3 · §F · §H · §I · §J. **ADR-0006 is unamended.**

### 10. Recommendation

# PROCEED B1

conditional on R1 and R2 being accepted into the plan, and on **E** being verified with the real
sparse package before the adapter is treated as done.

---

## 2. What was measured where — the part that must not be skipped

**Every number in this report was produced on Linux, in this container.** They are not Windows facts
and must not be quoted as any. Three things differ on Windows and all three affect the figures: CLR
startup, assembly loading off NTFS, and the file system cache.

`harness/phase5-g1/run-g1.ps1` exists so the identical measurement can be run where it counts. It
locates MSVC through `vswhere`, imports `vcvars64.bat`, builds both halves, runs five cold processes
plus the cold-race process, and prints the same aggregate. **It registers nothing and writes nothing
outside the harness folder.** Until its transcript exists, §5, §6 and §7 above are Linux results
standing in for Windows ones.

Run it from the repository root:

```powershell
.\harness\phase5-g1\run-g1.ps1
```

## 3. Questions A–H, and how each was actually settled

| | Question | Status |
|---|---|---|
| **A** | Can the C++ surrogate activate the managed bridge reliably? | **MEASURED (Linux).** Yes — activation in ~56 ms, clean in every run. |
| **B** | Cold-start latency? | **MEASURED (Linux).** 147.72 – 162.79 ms, decomposed in §5. |
| **C** | Warm-start latency? | **MEASURED (Linux).** median 0.02 ms, p95 ≤ 0.08 ms, including under 4-way contention. |
| **D** | Does `GetState` need `E_PENDING`? | **DERIVED, NOT OBSERVED.** There is no `IExplorerCommand` here to watch. From B and C: **yes, keep it** — but with R2 it protects only the window before warming completes, not every cold menu. |
| **E** | Does the bridge stay isolated from `explorer.exe`? | **OUT OF G.1 SCOPE — NOT TESTED.** A property of `com:SurrogateServer` registration, which G.1 is forbidden to build. Not claimed in either direction. |
| **F** | Does managed failure fail safely without crashing Explorer? | **MEASURED (Linux).** The exception is caught managed-side, `-99` crosses the ABI, the host survives and the next call succeeds. Whether *Explorer* survives is part of E. |
| **G** | Timeout/error behaviour? | **MEASURED (Linux).** The bridge blocks and owns no deadline — by design; the caller does. |
| **H** | Compatible with the existing surface-agnostic Shell contract? | **YES, by construction and by inspection.** The bridge holds no rule: JSON → `ShellSelection` → `ShellMenuBuilder.Build` → JSON. It reproduced the model's real output verbatim, including the disabled states the classic surface cannot express — for three `.txt` files it returned `{"action":"CompressTo","format":"gzip","state":"disabled","blocker":"SingleMemberFormat"}`. No format name, command construction or capability rule exists in the C++ at all; one capability predicate appears in the managed bridge — see the qualification below. |

**Note on H.** That the harness produced `SingleMemberFormat` without containing the word "gzip"
anywhere is the strongest single piece of evidence in this report: the rule was reached, not
reimplemented. It is what B3 could not have done without a second copy of the rule.

**One qualification, found by applying §B.2's own rule to the harness early.** Scanning both halves
for format names and capability predicates returns exactly one hit, and it is in the managed bridge,
not the C++:

```
bridge/BridgeExports.cs:213:  .Where(f => new FormatCapabilityReport(f, registry.CapabilitiesFor(f)).CanCreate)
```

`g1host.cpp` returns **zero** hits, which is the file `SHELL-063` is actually written about. The
managed line is not a new rule — `ShellMenuBuilder.Build` requires its caller to pass the creatable
list, and this is the same expression `CliApplication.cs:168` and `:309` already use. But it is now
the **third** place that derivation is written out by hand, and hand-copied derivations are precisely
how `shell-register.ps1` drifted from `ShellLauncher` and produced D2 and D3. **Recommendation for
§J:** the real `NOBA.Archive.ShellBridge` should obtain the creatable list from one shared helper
rather than re-deriving it, and `SHELL-063`'s source rule should cover the managed bridge as well as
the C++ adapter. This is an observation, not a G.1 blocker.

## 4. Defects found in the harness itself, by running it

Recorded because the standing rule is that parsing clean is not running clean, and each of these was
found by execution rather than by reading.

| | Defect | Fix |
|---|---|---|
| 1 | Managed exceptions returned `rc=-99` with no detail, making the first failure undiagnosable | exception text returned through the response buffer, with a comment that the **real bridge must not copy this** |
| 2 | `Headless = true` without `DefaultConflictPolicy` | the engine refused at construction — "a headless host cannot use ConflictPolicy.Ask: there is nobody to ask". The engine caught the harness, which is the guard working. |
| 3 | `--split` had to be exactly `argv[5]`; misplaced, it was silently ignored and produced an unsplit run that looked like a split one | scanned across `argv` |
| 4 | `atoi("--split")` → 0 warm iterations → `samples.front()` read off an empty vector | numeric-prefix check and a floor of 1 |
| 5 | Windows path widening used `std::wstring(s.begin(), s.end())`, which is correct only for ASCII — a path under `C:\Users\<non-ASCII>\` would have become mojibake and hostfxr would have reported a missing file that exists | `MultiByteToWideChar` |
| 6 | `run-g1.ps1` — `Write-Host "…" + ('-' * 40)` printed a literal `+` | parenthesised |
| 7 | `run-g1.ps1` parsed the label `first NobaShellQuery`, but the line reads `first NobaShellQuery (engine + first menu)` — the value came back null, and the driver reported the run as good while the aggregate quietly said "(no samples)" | full label, and **all five values now required**; a missing one fails the run by name |

Defect 7 was verified with a negative control: the drifted label was fed back in deliberately and the
validator reported `missing.Count=1 -> first menu`, then the correct label reported `missing.Count=0`.
Defect 3 was verified the same way, by passing `--split` in the wrong position and confirming the
split section still ran.

## 5. What G.1 explicitly did not do

No `IExplorerCommand`. No `IEnumExplorerCommand`. No `IShellItemArray` conversion. No MSIX, sparse or
otherwise. No signing, development or production. No COM registration. No CLSID. No registry write.
No change to any file named in the stop conditions. No amendment to ADR-0006. No Phase 6. No DROP 09.

**No Windows execution.** Until `run-g1.ps1` produces a transcript, this report describes a Linux
container.

---

# 6. GATE 1 — WINDOWS RESULT

**Executed by the user on Windows 11 Pro build 26200, 2026-08-28 20:49:50 +03:00.
Transcript: `harness/phase5-g1/results/g1-windows-20260828-204950.log`.**

## 6.0 Verdict

# GATE 1: PASS

Every question G.1 was set is answered on Windows evidence, and every figure that matters is **better
than the Linux baseline**. One measurement is a 30× outlier; §6.3 explains what it is, why it is not a
failure, and what it changes.

**Judged without pre-fixed numeric criteria.** Gate 2's pass criteria were pinned in advance; Gate 1's
were not — its questions were qualitative ("can it activate", "what is the latency"). I am noting that
asymmetry rather than pretending the bar was set beforehand.

## 6.1 The transcript is from this tree — verified, not assumed

The environment fingerprint exists because three Phase 4 runs were wasted on a source tree that had
never been extracted. This is the first Windows transcript in the project that proves its own
provenance:

| | Transcript | This tree | |
|---|---|---|---|
| `BridgeExports.cs` | `3143FFCEBBB31D3B` | `3143FFCEBBB31D3B` | **match** |
| `NOBA.Phase5.G1.Bridge.csproj` | `8599F64FF28C8880` | `8599F64FF28C8880` | **match** |
| `g1host.cpp` | `C2BCB7693BB6A9C2` | `C2BCB7693BB6A9C2` | **match** |
| `BuildStamp` | `2026-08-27-drop-08-phase-3-directory-identity` | same | **match** |

## 6.2 Environment

| | |
|---|---|
| OS | Windows 11 Pro, build **26200**, x64 |
| CPU | 11th Gen Intel Core i7-11850H @ 2.50 GHz — 8 cores / 16 threads |
| RAM | 63.2 GB |
| **Defender real-time protection** | **False — OFF** |
| Power plan | Balanced |
| .NET SDK / runtime | 10.0.400 / **Microsoft.NETCore.App 10.0.11** |
| hostfxr | `10.0.11`, file version `10,0,1126,37416` |
| Toolset | MSVC **14.44.35207**, Windows SDK 10.0.26100.0, VS 2022 Build Tools |
| PowerShell | Desktop **5.1**.26100.9168 |

**Defender being off is not a neutral detail and I flagged it before the run for this reason.** Its
on-access scanning runs on every assembly load — precisely the cost measured here. **Every cold figure
below is therefore optimistic relative to a default Windows install**, where real-time protection is
on. The warm figures are unaffected. This does not change the verdict; it changes how much headroom
the cold figures should be assumed to have, which is *less* than they appear.

## 6.3 Cold start — and the one anomaly

Run 1 is a 30× outlier. Reporting the mean would describe neither mode, so here is the split:

| | run 1 | runs 2–5 median | ratio |
|---|---:|---:|---:|
| **cold total** (process → first menu) | **3213.17 ms** | **103.36 ms** | **31.1×** |
| of which: `load assembly + resolve` | 379.48 ms | 30.13 ms | 12.6× |
| of which: engine construction (`NobaShellWarm`) | **2788.59 ms** | 28.21 ms | **98.9×** |
| of which: **first menu once warm** | **25.47 ms** | 27.18 ms | **0.9×** |

**The last row is the diagnosis.** Run 1's *first menu* was not slow — it was the fastest of all five.
Only the two phases that touch the file system blew up. Everything compute-bound behaved normally.

**What it is:** run 1 was the first process to execute after `dotnet build` had just written nine
assemblies. It paid first-touch I/O for the whole dependency graph — including the 7-Zip backend —
with a cold page cache. Runs 2–5 reused the mapped images and cost ~28 ms. Defender was off, so this
is not scanning.

**Why this is not a harness artefact, and matters more than it looks.** After an MSIX install, the
first surrogate activation is *also* the first touch of freshly-written files. Run 1 may be the most
production-representative cold sample in the set, not the least. It should be treated as the real
first-activation cost, not discarded.

**What it changes — a correction to `DROP08_PHASE5_DELTA.md` §4.** That section said the bridge is
ready in ~150–200 ms and "the window in which a menu can arrive before warming completes is small".
On Windows the window is **~100 ms in the steady case but up to ~3.2 s on a genuinely cold first
activation**. That is seconds, not milliseconds.

**This strengthens the `E_PENDING` decision rather than weakening it.** `E_PENDING` is not
decoration for a 100 ms window; it is what stands between the user and a multi-second stall the first
time they right-click after installing. §4's decision is unchanged; §4's *characterisation* of the
window was wrong and is corrected here.

## 6.4 Warm start, menu construction, concurrency

| | Windows | Linux baseline | |
|---|---|---|---|
| warm median | **0.01 ms** | 0.02 ms | better |
| warm p95 | **0.02 ms** | 0.03–0.07 ms | better |
| first menu, engine warm | **25.47 – 27.86 ms** | 54.10 – 71.89 ms | **~2.4× better, and far tighter** |
| concurrent p95 (4 × 25, warm) | **0.04 – 0.06 ms** | 0.07 – 0.08 ms | better |
| concurrent failures | **0** | 0 | — |
| cold race (4 threads, unbuilt engine) | **55.13 ms wall, 0 failures**, all four within 0.15 ms | 109–137 ms, 0 failures | better |

Windows is not merely adequate here; on every steady-state measure it beats the container the design
was prototyped in. The spread on "first menu" is 2.4 ms across five processes — tighter than Linux's
18 ms.

## 6.5 R2 — warming the real menu path, on Windows

| | Windows | Linux |
|---|---|---|
| bridge READY (startup, off the menu path) | **99.22 – 109.57 ms** | 145 – 206 ms |
| **first user-visible menu after full warm-up** | **0.61 – 0.88 ms** | 0.96 – 1.09 ms |
| warm-up cost paid at startup | 55.17 ms (30.72 DI + 24.46 synthetic menu) | ~95–115 ms |

**R2 is confirmed on Windows and the effect is larger than on Linux:** 25.47–27.86 ms without it,
**0.61–0.88 ms with it** — a ~35× reduction in the only latency a user can perceive, bought with
~55 ms of startup work where nobody is waiting.

The residual ~0.7 ms (against a 0.01 ms steady state) is still the multi-select branches the
single-item synthetic warm-up never reached. **The two-query warm-up requirement recorded in delta §3
stands and is now evidenced twice.**

## 6.6 Failure and timeout behaviour

Identical on Windows to Linux, across all five runs:

| | Result |
|---|---|
| managed exception | `rc = -99` every run; no exception crossed the ABI |
| host after the fault | `rc = 0` — **survived and served the next call, 5 of 5** |
| malformed JSON | `rc = -99`, refused |
| empty request | `rc = -2`, refused |
| slow call (2 s injected) | 2000.96 – 2013.93 ms, blocks the caller |

The bridge owns no deadline, by design. The caller does.

## 6.7 A defect in my own aggregation, found by running on Windows

The driver printed:

```
cold total (process -> first menu)   min   101.27  avg   725.54  max  3213.17 ms
```

**`avg 725.54` describes no run that happened.** The distribution is bimodal — one run at 3213 ms and
four at ~103 ms — and the mean sits in the empty space between the two modes. Had the outlier been
milder it would have been silently smeared into an average that looked merely "a bit slow" instead of
prompting the diagnosis in §6.3.

**Fix required before the next timing gate:** report **median** alongside min/max, and report the
first run of a freshly-built binary separately from the rest, because it is a different population.
Recorded here; the driver is not edited in this turn, since editing it would invalidate the
`g1host.cpp` / driver provenance of the transcript above.

## 6.8 Questions A–H after Windows

| | Question | Status after Gate 1 |
|---|---|---|
| **A** | activation reliable? | **PASS — measured on Windows.** 5/5 plus race plus R2 runs, no failure. ~45 ms. |
| **B** | cold-start latency? | **MEASURED.** 101–107 ms steady; **3213 ms** on first activation after a fresh write. |
| **C** | warm latency? | **MEASURED.** median 0.01 ms, p95 0.02 ms, including under contention. |
| **D** | does `GetState` need `E_PENDING`? | **DERIVED — yes, and more firmly than before.** §6.3. |
| **E** | isolated from `explorer.exe`? | **STILL NOT TESTED.** Gate 2. A standalone `.exe` is not evidence in either direction. |
| **F** | managed failure safe? | **PASS — measured.** 5/5. |
| **G** | timeout behaviour? | **MEASURED.** Caller owns the deadline. |
| **H** | compatible with the surface-agnostic contract? | **PASS.** The Windows run returned the same real model output, including `{"format":"gzip","state":"disabled","blocker":"SingleMemberFormat"}` for three `.txt` files — from a bridge containing no format name. |

## 6.9 Does B1 survive Gate 1?

**Yes.** Nothing measured argues for B3, and B3 would pay the same first-touch I/O for its own DLL
while also duplicating the rule. **PROCEED B1 stands**, still conditional on **E**.
