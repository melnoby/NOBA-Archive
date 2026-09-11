# R13 — Windows shipping-configuration validation

**Status:** **CLOSED — 2026-08-20.**

Run 2 on `noby` was clean: 13 phases, `Phases failed: 0`, 330 tests, `Skipped: 0`, 0 build errors, with
the real NuGet feed and real xUnit v3. Phase 1 and Phase 2 are validated in the shipping configuration.

R13 was open from the start of Phase 1 to the end of Phase 2 and cost one real product bug that no
amount of Linux testing could have found. The rule it earns: **a green suite on one platform is one
data point, not a result.**

---

## Run 2 — 2026-08-20T20:20:54+03:00 — CLEAN

Same environment as run 1. Every phase exit 0.

| Phase | Result |
| --- | --- |
| `dotnet --info`, nuget sources, restore | PASS |
| build `Release`, shipping deps | PASS — 66 warnings (down from 70), **0 errors** |
| Core / Security / Application / Infrastructure / Formats | PASS (89 / 41 / 21 / 19 / 18) |
| **HOSTILE ARCHIVE + ROUND TRIP** | **PASS (47/47)** |
| Architecture / CLI / Parity | PASS (47 / 25 / 23) |
| **Total** | **330 tests, 0 failed, 0 skipped** |

Both fixes are confirmed on the platform that exposed them: the ZIP timestamp conversion, and the
racy progress assertions. The four-warning drop is the three analyzer findings fixed plus one that
moved with the `Progress<T>` rewrite.

Remaining warnings are all `xUnit1051` (use `TestContext.Current.CancellationToken`). They are visible,
counted, and now safe to address as ordinary cleanup rather than under gate pressure — see §4.1.

---

## 0. Run 1 — 2026-08-20T20:02:45+03:00

**Environment:** PowerShell 5.1.26100.9168 (Desktop) · Windows NT 10.0.26200 (build 26200) · AMD64 ·
.NET SDK 10.0.400 · runtime 10.0.11 · nuget.org enabled · no `global.json`.

| Phase | Result |
| --- | --- |
| `dotnet --info`, nuget sources | PASS |
| **restore** (real feed, 18 projects) | **PASS** |
| **build** `Release`, shipping deps | **PASS** — 70 warnings, **0 errors** |
| Core / Security / Application / Infrastructure / Formats | PASS (89 / 41 / 21 / 19 / 18) |
| **HOSTILE ARCHIVE + ROUND TRIP** | **FAIL** — 46/47, one failure |
| Architecture / CLI / Parity | PASS (47 / 25 / 23) |

`Skipped: 0` in every suite: 330 tests ran, none were quietly excused.

### The failure — *Windows-specific code issue*, and the reason R13 exists

```
RoundTripTests.Modification_times_survive_the_round_trip [FAIL]
  expected ~2021-06-15T10:30:00.0000000+02:00, got 2021-06-15T08:30:00.0000000+02:00
```

Off by exactly the machine's UTC offset. A ZIP entry's timestamp is an **MS-DOS date/time: local wall
clock, no timezone**. The filesystem layer deliberately reports instants in UTC, and the ZIP provider
handed that UTC value straight to the writer — so the UTC wall clock was stored as though it were
local, and every extracted file came back wrong by the offset.

**This could not have been caught on Linux as configured.** The build container runs UTC, where
converting between an instant and a local wall clock is the identity. 330 green tests, a real bug, and
zero possibility of seeing it. That is the entire argument for R13 in one line.

It was reproduced on Linux immediately once the timezone was pinned:

```
TZ=Africa/Cairo → expected ~2021-06-15T10:30:00+02:00, got 2021-06-15T08:30:00+02:00
```

Fixed in `ZipArchiveProvider.ToDosLocalTime`, and verified across UTC, Africa/Cairo,
Australia/Adelaide (half-hour offset), America/Sao_Paulo and Asia/Kathmandu (quarter-hour offset).
`scripts/test.sh` now pins a non-UTC timezone so this class of bug cannot hide again.

### Scorecard against the predictions in §4

| # | Prediction | Outcome |
| --- | --- | --- |
| 4.1 | xUnit analyzers × warnings-as-errors would fail the build | **Correct.** 70 warnings, mostly `xUnit1051`. The mitigation held: 0 errors, all suites ran. |
| 4.2 | Mapped/SUBST drives fail handle verification | **Not exercised** — extraction was to `C:`. Still open. |
| 4.3 | `GetFinalPathNameByHandle` buffer boundary | Fixed pre-emptively; not reachable in this run. |
| 4.4 | Parallel integration tests exhaust disk | **Correct as mitigation** — serialised, no I/O failures. |
| 4.5 | SEC-011 symlink test would skip on Windows | **WRONG.** `Skipped: 0` — the test *ran and passed*. The planted-symlink defence is now genuinely verified on Windows, and that R14 gap is closed. |
| 4.6 | Unexplained intermittent integration failure | **Now diagnosed** — see below. Not the failure seen in this run. |
| 4.7 | Mark of the Web has no coverage | Unchanged. Still uncovered. |

Being wrong about 4.5 is worth recording: I predicted a skip and got a pass, which means a control I
had written off as unverified is in fact verified. The same lesson as the 7z spike — a prediction about
a platform is not evidence from it.

### The intermittent failure (§4.6) is resolved — and it was my test, not the product

Caught on the 13th consecutive run with output this time:

```
RoundTripTests.Progress_is_monotonic_and_ends_at_the_real_total [FAIL]
  Expected <Completed> but found <Running>
```

`Progress<T>` posts every report to the thread pool when there is no synchronization context. So the
callbacks ran **concurrently and out of order**: `List.Add` from several threads was a data race, and
"the last snapshot" was whichever callback happened to finish last. The 100 ms sleep papered over it
most of the time.

The production code reports its states in the right order; only the delivery was unordered. Fixed by
reporting inline through `SynchronousProgress<T>`, which observes the sequence the handler actually
produced. INT-013 had the same flaw with a worse failure mode: a cancellation request that might not
run until after the operation had already finished. **0 failures in 25 consecutive runs** since, against
1-in-13 before.

---

## Original brief (written before the run)

---

## 1. Why this cannot be done from the agent side

Two hard blocks, both verified rather than assumed:

- **No shell on the Windows machine.** The desktop bridge exposes file read/write only; there is no
  `device_bash`, and no PowerShell execution of any kind. `get_device_info` reports `platform: win32`
  with one connected folder and nothing executable.
- **No package feed from the build sandbox.** `https://api.nuget.org/v3/index.json` returns HTTP 000
  through the sandbox proxy (`NU1301`, proxy tunnel 403). The shipping configuration cannot be
  restored here at all, so it cannot be exercised here even on Linux.

Consequently R13 can only be closed by running the commands below on `noby` and returning the
transcript. Everything the agent could do to make that run succeed and be diagnosable has been done and
is listed in §4.

---

## 2. The run

```powershell
cd C:\Users\m-m-e\Desktop\NOBA-Archive
.\scripts\validate.ps1
```

`validate.ps1` exists for this purpose. It uses the shipping dependency set and has **no `-Offline`
switch**. It runs each phase separately — `dotnet --info`, NuGet sources, restore, build, then all nine
suites — records each exit code independently, continues past failures, and writes everything to
`artifacts\r13-windows-validation-<timestamp>.txt`.

Send that file back unedited. Do not re-run to chase green.

`build.ps1` and `test.ps1` still work if you prefer them, but they stop at the first failing phase and
do not fingerprint the environment, so the transcript is less useful.

---

## 3. Failure classification

Every failure in the transcript gets exactly one of these five labels. The point of separating them is
that they have different owners and different urgency.

| Class | What it means | Who fixes it |
| --- | --- | --- |
| **Environment / toolchain** | SDK version, PowerShell, execution policy, missing workload, analyzer severity, path length limits | Machine or build config; no product bug |
| **Dependency / restore** | `NU****` — feed unreachable, version does not exist, central pinning conflict, lock mismatch | `Directory.Packages.props` |
| **Windows-specific code** | Compiles and passes on Linux, misbehaves on Windows: Win32 interop, path semantics, ADS, casing, drive mapping | Product bug — highest value finding |
| **Test / runtime** | The suites run but a test fails on its merits, or the offline harness and xUnit disagree | Product bug or a test that was wrong |
| **Packaging / native dependency** | `7z.dll` discovery, DLL search hardening, MSI/WiX, file associations | Phase 3+ scope |

---

## 4. Predicted failures, pre-classified

These are stated **before** the run, on purpose: a prediction that survives contact with the machine is
evidence, and one that does not is a correction. Everything here was found by reading code that has
never executed.

### 4.1 xUnit v3 analyzers × `TreatWarningsAsErrors` — *environment/toolchain* — **addressed**
`xunit.v3` ships Roslyn analyzers whose diagnostics default to warnings. The solution sets
`TreatWarningsAsErrors=true`. `xUnit1051` alone ("use `TestContext.Current.CancellationToken`") fires on
every call in a test that accepts a `CancellationToken` — dozens of sites — so the build would have
failed before a single test ran, for reasons unrelated to correctness.

`Directory.Build.targets` now sets `TreatWarningsAsErrors=false` **for test projects in the shipping
configuration only**. Product code is unchanged. The warnings are not suppressed: they appear in the
transcript and should be triaged once the real list is known instead of guessed at.

### 4.2 Handle verification vs mapped and SUBST drives — *Windows-specific code* — **NOT fixed, deliberately**
This is the one to watch. `WindowsExtractionPlatform` re-verifies every extracted file by asking
`GetFinalPathNameByHandle` where the open handle really points, then checks that answer is inside the
destination root. With `VOLUME_NAME_DOS`, a file on a **mapped network drive** resolves to
`\\server\share\...`, and a **SUBST** drive resolves to its underlying real path — neither of which
matches a destination the user expressed as `Z:\Extracted`. The comparison is
`OrdinalIgnoreCase`, so casing is safe, but the *namespace* differs.

If that is what happens, **every extraction to a mapped or substituted drive fails closed** with a
refusal. Failing closed is the correct direction for a security check to fail, but refusing all
legitimate work is still a defect.

It is left unfixed on purpose. The principled fix is to canonicalise the destination root through the
same API — open a handle to the destination directory, resolve it, and compare canonical to canonical —
and shipping that blind into the very run meant to validate this code would defeat the exercise. What
*was* done is make the failure legible: the error now names both the resolved path and the expected
root, so one line of the transcript settles it.

**To test it deliberately:** extract to a mapped drive or run `subst X: C:\some\folder` and extract to
`X:\out`. If you only ever extract to `C:\`, this will not appear and stays an open risk.

### 4.3 `GetFinalPathNameByHandle` buffer boundary — *Windows-specific code* — **fixed**
The retry guard read `if (length > buffer.Length)`. On success the return value excludes the null
terminator; a value *equal* to the buffer length means the call failed for want of room and left the
buffer unwritten, so that case would have built a string from uninitialised memory. Now `>=`. Only
reachable at ~1023-character paths, but it was wrong.

### 4.4 Disk pressure from parallel integration tests — *environment/toolchain* — **addressed**
xUnit v3 parallelises test classes; the offline harness is sequential. SEC-012 expands 512 MB, SEC-021
another 320 MB, and the writer preallocates by declared size, so several hundred-megabyte allocations
could be live at once. On a machine with modest free space that fails as an I/O error and reads like a
code defect. `NOBA.Archive.Integration.Tests` now disables parallelisation for that assembly only; the
tests were already isolated by per-test temp directories, so this costs wall-clock and nothing else.

### 4.5 SEC-011 symlink test will likely skip — *expected, not a failure*
Creating a symbolic link on Windows needs Developer Mode or elevation. The test calls `Assert.Skip` and
the runner reports skips as skips, never folded into the pass count. **A skip here is an untested
security control, not a passing one** — it belongs in R14 until Developer Mode is on for one run.

### 4.6 One unexplained integration-suite failure — *unresolved, watch for it*
During this phase's work `NOBA.Archive.Integration.Tests` failed **once**, on Linux, in the offline
profile. It has passed on the seven runs since — three full `test.sh` runs and four direct runs — and
the failing output was not captured, so there is no diagnosis, only a hypothesis: the multi-hundred-
megabyte bomb fixtures colliding with the sandbox's disk allowance immediately after a build.

It is recorded rather than dismissed. A suite that fails once in seven runs and takes its output with it
is worse than one that fails every time, because it can only be argued about. `scripts/test.sh` now
writes every suite's output to `artifacts/test-<timestamp>/`, so the next occurrence is diagnosable.

If the Windows run shows an integration failure that does not reproduce, this is the first thing to
compare it against — and a flaky *security* suite would need resolving before Phase 2 could be approved,
whatever the pass count says.

### 4.7 Mark of the Web has no coverage anywhere — *test/runtime gap*
The integration fixture sets `PropagateMarkOfTheWeb = false`, so `ReadZoneIdentifier` and
`WriteZoneIdentifier` have never executed. They cannot be exercised off Windows at all. This is a gap
in the suite, not something this run will reveal — worth a dedicated test after R13 closes.

---

## 5. What "closed" means

**Run 1 did not meet this bar** — the integration suite exited 1. The bug it found is fixed and the
flake is fixed, so run 2 is expected to be clean, but "expected" is not "observed". Re-run
`.\scripts\validate.ps1` and send the transcript.

R13 closes only when the transcript shows:

- `restore` exit 0 against the real feed,
- `build` exit 0 in `Release` with the shipping dependency set,
- all nine suites exit 0,
- and every skip is enumerated and accepted rather than counted as a pass.

Anything short of that leaves R13 open and Phase 2 unapproved. If the run fails, the failures are the
deliverable — classify them per §3, and do not work around any of them to obtain a green result.

**On success, the next step is not Phase 3 in general.** It is R1: re-proving the 7z interop and the
ADR-0005 invariant against the real `7z.dll`, before any further ZIP or product work.
