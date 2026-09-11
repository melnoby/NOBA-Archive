# DROP 08 Phase 5 — PLAN DELTA, after the conditional PROCEED B1

**Gate 1 PASS. Gate 2 OPEN — E1 PASS ×2, hash-verified; E2 BLOCKED BY ENVIRONMENT.

**§12 is the current status**, and the frozen evidence is `DROP08_PHASE5_E2_EVIDENCE.md`. §9–§11 are
the three earlier attempts, each a defect of mine, each now checked by the script rather than by my
memory. §2 records how the Question E contradiction was decomposed; plan §L.6 carries a correction
that attempt 2 forced, and §L.13 the product requirement attempt 4 exposed.**

The review asked for a delta showing six things before any substantial product implementation:

| | Section | Status |
|---|---|---|
| 1 | Windows G.1 evidence | **CLOSED — GATE 1 PASS.** See `DROP08_PHASE5_G1_REPORT.md` §6. |
| 2 | Surrogate isolation evidence (Question E) | **OPEN — E1 PASS ×2; E2 BLOCKED BY ENVIRONMENT. Evidence frozen in `DROP08_PHASE5_E2_EVIDENCE.md`.** |
| 3 | Warm-up placement | **DECIDED** |
| 4 | `E_PENDING` decision | **DECIDED** |
| 5 | Capability single-source boundary | **DECIDED — recorded as a requirement, no code written** |
| 6 | Exact files to be touched | **LISTED** |
| 7 | *(added 2026-08-28)* Installation / elevation boundary | **DECIDED — `DROP08_PHASE5_PLAN.md` §L** |

Nothing in §3–§6 has been implemented. This is a plan delta, not a drop.

---

## 1. Windows G.1 evidence — BLOCKED, prepared

**I cannot execute anything on Windows.** Every Windows result in this project has come from a
transcript you produced, and this one is no different. What I can do is make the run produce the six
things you asked for, in one pass, without needing a second attempt.

### What is ready

`harness/phase5-g1/run-g1.ps1` now reports, explicitly labelled with your numbering:

| | | Where it comes from |
|---|---|---|
| **(6)** | exact build/runtime environment | §1 — captured **before** anything is built |
| **(1)** | cold-start measurements | §4, aggregated over 5 separate processes |
| **(2)** | warm-start measurements | §4, median and p95 |
| **(3)** | menu construction cost | §4, isolated into three figures — engine construction, first menu, steady-state menu |
| **(4)** | failure behaviour | §4 — fault return codes, whether the host recovered, the slow-call duration, read out of each run rather than assumed |
| **(5)** | concurrency behaviour | §4 warm contention, **§5 cold race** — four threads against an unbuilt engine |

The environment fingerprint captures OS caption and build number, CPU model and core/thread count,
RAM, **Windows Defender real-time protection state**, active power plan, the .NET SDK and every
installed runtime, the hostfxr path and file version, `BuildStamp`, the MSVC toolset and Windows SDK
versions, and a **SHA-256 of each harness source file**.

Defender is in that list on purpose. Its on-access scanning runs on every assembly load, which is
precisely the cost this harness measures; a machine with it off produces a materially better cold
start than the machine a user actually has. The hashes are there because a transcript that does not
identify the tree that produced it is not evidence — a lesson this project paid for three times
during Phase 4.

The script writes a transcript to `harness/phase5-g1/results/g1-windows-<timestamp>.log`, so the
evidence can be committed rather than copy-pasted.

### One change to the harness, and why it is still "identical"

You asked for the identical harness. Section 6 (`--warm-full`) is **new**, and I want to be exact
about what that means:

- The default invocation — `--split`, which is what §3 of the driver runs — executes sections A, B0,
  B, C, C2, C3, F, F2 and G **unchanged**. Re-run on Linux after the addition, it reproduced the
  baseline: cold 150.45 / 152.30 / 158.64 ms against the original 147.72 – 162.79 ms range.
- `--warm-full` is a **separate mode in a separate process** that does not run when it is not asked
  for. It exists because you accepted R2, and a requirement with no measurement behind it is a
  preference.
- **`BridgeExports.cs` was not touched.** The R2 warm-up is a synthetic query issued *by the host* —
  which is exactly where R2 puts it. No managed change was needed and none was made.

### To run it

```powershell
cd C:\Users\m-m-e\Desktop\NOBA-Archive
.\harness\phase5-g1\run-g1.ps1
```

Requires Visual Studio Build Tools with the **Desktop development with C++** workload; the script
locates MSVC through `vswhere` and stops with an install instruction if it is absent, rather than
falling back to another toolchain and reporting the result as though it were the same measurement.

**It registers nothing** — no COM class, no CLSID, no package, no certificate, no registry write.

### Until that transcript exists

Every figure in `DROP08_PHASE5_G1_REPORT.md` is a Linux container result. None of them is Windows
evidence and none is quoted as such.

---

## 2. Surrogate isolation, Question E — BLOCKED, and not closable as written

**This is the contradiction, and I am reporting it rather than resolving it.**

Gate 2 says:

> Prove with the actual intended `com:SurrogateServer`/package configuration that the managed bridge
> runs outside `explorer.exe`. This must be measured/verified with the real package mechanism. A
> standalone executable is NOT sufficient evidence.

The accepted constraints in the same message say:

> No MSIX/signing/Phase 6/DROP 09. … Do NOT start the full `IExplorerCommand` implementation yet.

**The real package mechanism cannot be exercised without both of the forbidden things.** Two
documented facts, not inferences:

- A sparse package's context-menu extension **will not appear at all unless the package is signed**.
  The xplorer² write-up of exactly this configuration states it plainly: *"The package won't show in
  the context menu without a digital signature."*
- The packaged path activates the surrogate **because Explorer invokes a registered
  `IExplorerCommand` handler**. The same source lists the requirement as *"a shell extension
  implementing `IExplorerCommand`"*. `com:SurrogateServer` is how that COM class is hosted; it is not
  independently reachable.

So: proving E through the packaged mechanism requires a signed package containing an
`IExplorerCommand` server — which is the implementation gate 2 is a precondition for. **E as written
cannot be closed before the thing it gates.**

### The decomposition

E is not one question. It is three, and they have different costs.

| | Question | Cost to answer | Answers what |
|---|---|---|---|
| **E0** | Is `com:SurrogateServer` documented and shipped as loading into `dllhost.exe`? | none | precedent — **not measurement**, and you asked for measurement |
| **E1** | Does hosting `hostfxr`/.NET inside an **out-of-process COM surrogate** actually put the CLR in the surrogate and not in the caller, and does a fault there leave the caller alive? | a minimal COM DLL + `HKCU` registration. **No package. No signing. Fully reversible.** | the *hosting model* — the thing B1 is actually betting on |
| **E2** | Does **MSIX `com:SurrogateServer`**, activated by **Explorer**, behave the same way? | a dev-signed sparse package **and** a stub `IExplorerCommand` | the *production activation path* — what ADR-0006 depends on |

**E1 is measurable now and cheaply.** Microsoft documents the classic route precisely: set
`AppID` under the CLSID, an `InprocServer32` pointing at the DLL, and a `DllSurrogate` value under
the `AppID` key that is **an empty string, which selects the system-supplied `dllhost.exe`**; the
client then activates with `CLSCTX_LOCAL_SERVER`. All of it lives under `HKCU\Software\Classes`, the
same hive `shell-register.ps1` already writes to, and it removes cleanly.

The evidence E1 would produce is direct and checkable:

1. the client process's module list contains **no** `coreclr.dll` / `hostfxr.dll`;
2. a `dllhost.exe` exists whose module list contains both, and the loaded `NOBA` bridge assembly;
3. faulting the managed bridge kills the surrogate and **the client keeps running**;
4. `NobaShellQuery` still round-trips across the process boundary and returns the real menu.

**E1 passing does not imply E2 passes**, and I will not let it be read that way. They are different
activation paths — HKCR versus the packaged COM catalog — and E2 is the one production uses.

### DECISION, 2026-08-28: **OPTION 2 SELECTED**

E1 **and** a scoped E2 probe are authorised. Everything below in this subsection is the binding scope,
**written before any of it exists** so that the criteria cannot be adjusted afterwards to fit whatever
the probe happens to produce. That ordering is the point; this project has already been bitten once by
a green result that came from a stale tree, and once by a "verification" that was only a syntax check.

**Sequencing, per the authorisation:** Gate 1 is evaluated **first**, on its own Windows transcript.
E1/E2 proceed only if Gate 1 leaves B1 viable. A green Linux harness is not a reason to assume Gate 1
passed.

#### The E2 stub contract — seven constraints, all binding

The E2 `IExplorerCommand` is a **feasibility harness only**. It must:

| | Constraint |
|---|---|
| 1 | expose a **constant, harmless title** |
| 2 | return a **deterministic, harmless state** |
| 3 | do **nothing meaningful** on `Invoke` |
| 4 | contain **no archive business logic** |
| 5 | contain **no capability logic** |
| 6 | contain **no format names** |
| 7 | contain **no production NOBA action implementation** |

Constraints 4–6 are the same rule `SHELL-063` enforces, applied before the file exists rather than
after. The stub is not connected to `ShellMenuBuilder`, and connecting it is explicitly out of scope.

#### Pass criteria, fixed in advance

**E1 — classic `AppID`/`DllSurrogate`, `HKCU`, no package, no signing.** PASS requires all four:

1. the client process's module list contains **no** `coreclr.dll` and **no** `hostfxr.dll`;
2. a `dllhost.exe` exists whose module list contains **both**, plus the NOBA bridge assembly;
3. faulting the managed side kills the surrogate and **the client survives**;
4. `NobaShellQuery` round-trips across the process boundary and returns the real menu.

Anything less is FAIL, not "partial".

**E2 — sparse package, `com:SurrogateServer`, activated by Explorer.** PASS requires all three:

1. the stub verb appears in Explorer's **modern** menu;
2. **`explorer.exe` does not load** `coreclr.dll` / `hostfxr.dll` / the NOBA adapter DLL;
3. a **`dllhost.exe`** does, and it runs as the **standard user** — no elevation, no UAC prompt
   (which is also the first runtime evidence for §L.4 and §L.7).

**E1 PASS does not imply E2 PASS, and E1 will not be reported as if it did.** They are different
activation paths — `HKCR` versus the packaged COM catalog — and only E2 exercises the one production
uses. If E1 passes and E2 fails, the gate result is **FAIL**, not "mostly passed".

#### What remains forbidden while this gate runs

No real NOBA `IExplorerCommand` · no connection between the adapter and `ShellMenuBuilder` · no
production localisation · no real `GetState` mapping · no real `Invoke` · no production MSIX · no
production signing procurement · no Phase 6 · no DROP 09 · no amendment to ADR-0006.

**Capability boundary, restated because C++ is about to exist:** one authoritative managed capability
derivation. **No hand-written capability predicate in C++.** The C++/COM layer consumes `entryId`,
`title` and `state` as opaque values and recreates no format or capability rule. `SHELL-063` stays
widened per §5 of this document.

### The three options as they were presented

Kept for the record. **Option 2 was chosen.**

**Option 1 — authorise E1 only.** A minimal COM DLL (`DllGetClassObject` + `IClassFactory` + one
trivial interface) plus an `HKCU` surrogate registration and a small client. **No `IExplorerCommand`,
no `IShellItemArray`, no menu, no package, no certificate.** Gets measured isolation evidence for the
hosting model this week. Leaves E2 open, to be closed as the first step of Phase 5 implementation.

**Option 2 — authorise E1 *and* a scoped E2 probe.** E1 as above, plus a dev-signed sparse package
whose `IExplorerCommand` is a **stub**: `GetTitle` returns a constant, `GetState` returns
`ECS_ENABLED` unconditionally, `Invoke` does nothing. No `ShellMenuBuilder`, no capability model, no
launching. Its only purpose is to make Explorer activate the surrogate so the process topology can be
observed. **This needs an explicit carve-out from "No MSIX/signing"** — note that your earlier Phase 5
authorisation did already approve *"a DEV-ONLY self-signed certificate"*, so this may be narrower than
the current constraint list reads, but I am not going to assume that on your behalf.

**Option 3 — defer E.** Close gate 1 only, and treat E as the first task inside Phase 5
implementation rather than a precondition for it. Honest, and consistent with the fact that E cannot
be closed before the thing it gates — but it means `IExplorerCommand` work starts with B1's central
risk still open, which is what gate 2 was written to prevent.

**E is UNTESTED until the criteria above are met on Windows.** No part of it is claimed in either
direction, and Option 2 being authorised is not itself evidence of anything.

---

## 3. Warm-up placement — DECIDED

R1 and R2 are accepted. The measurement that forced R2 is now in the harness, and it is emphatic.

### Where the warm-up goes

**At surrogate startup, on a background thread, before any menu is requested** — not lazily on the
first `GetState`.

### What it must do

`NobaShellWarm` must drive the **real menu path** end to end: build the DI container **and the
provider registry**, then issue a synthetic `ShellMenuBuilder` query and discard the result. Stopping
at the container is the mistake R2 names.

### The evidence (Linux — Windows figures pending §1)

| Warm-up strategy | First user-visible menu |
|---|---:|
| DI container only (`NobaShellWarm` as originally written) | **54.10 – 71.89 ms** |
| DI container **plus a synthetic menu** (R2) | **0.96 – 1.09 ms** |

Roughly a **sixty-fold** reduction in the only latency a user can perceive, bought with ~95–115 ms of
startup work that happens where nobody is waiting. Bridge READY at 145–206 ms from process start.

### One finding R2 did not anticipate

The R2 figure is **~1.0 ms, not the ~0.02 ms steady state.** That gap is real and it is informative.

The synthetic warm-up issues a **single-file** selection; the measured call is a **three-file
multi-selection**. The residual ~1 ms is the multi-select branches that the warm-up never reached —
first-call JIT of exactly the code path Phase 5 exists to serve. The measurement was built that way
deliberately, so the number would be the pessimistic one rather than a replay of an identical call.

**Requirement added:** the production warm-up must issue **at least two** synthetic queries — one
single-item and one multi-item — so both branches are warm. On this evidence that should bring the
first user-visible menu to the steady-state figure. Cheap, and it removes the last measurable cliff.

---

## 4. `E_PENDING` decision — DECIDED

**Keep `E_PENDING`. Treat "bridge not yet warm" as the only condition that produces it. Invent no
other semantics.**

| `GetState` condition | Returns |
|---|---|
| entry present and enabled | `ECS_ENABLED` |
| entry present, disabled with a `ShellBlocker` | `ECS_DISABLED` |
| entry absent from the menu | `ECS_HIDDEN` |
| **bridge not yet warm** and `fOkToBeSlow == FALSE` | **`E_PENDING`** |

Three consequences of the measurement, stated so the next reader does not re-litigate them:

1. **`E_PENDING` is a safety net, not the normal path.** With §3's warm-up placement the bridge is
   ready in ~150–200 ms of surrogate startup, and the window in which a menu can arrive before
   warming completes is small. §0.2 of the plan described `E_PENDING` as the expected path for every
   cold surrogate; on the measurement that is too pessimistic, and R3 corrects the wording.
2. **The condition is "bridge not yet warm", not "CLR not yet warm".** The CLR is the *smaller* half:
   activation ~56 ms against engine construction ~39 ms plus first-menu JIT ~62 ms. Wording that
   names only the CLR would leave the larger cost unaccounted for.
3. **The bridge still gets no timeout of its own.** A timeout inside the bridge would abandon a call
   still running in a shared surrogate. The caller owns the deadline, and `fOkToBeSlow` / `E_PENDING`
   *is* that mechanism. The 2-second injected call blocks for its full duration by design.

**Not decided here:** whether a disabled entry can explain itself. §E.2 of the plan is DEFERRED and
no explanatory text has been invented for disabled items.

---

## 5. Capability single-source boundary — DECIDED, recorded, not implemented

**Per your instruction, no helper was added and `BridgeExports.cs` was not touched.** What follows is
a Phase 5 architecture requirement, written down so it is closed by design rather than by cleanup.

### The observation that prompted it

Applying §B.2's own rule to the harness turned up exactly one hit, in the managed half:

```
harness/phase5-g1/bridge/BridgeExports.cs:213
    .Where(f => new FormatCapabilityReport(f, registry.CapabilitiesFor(f)).CanCreate)
```

`g1host.cpp` returns **zero** — which is the file `SHELL-063` is written about. The managed line is
not a new rule; it is the same expression `CliApplication.cs:168` and `:309` already use, because
`ShellMenuBuilder.Build` requires its caller to supply the creatable-format list. But it is now the
**third** hand-written copy of that derivation, and hand-copied derivations are precisely how
`shell-register.ps1` drifted from `ShellLauncher` and produced D2 and D3.

### The requirement

> **There must be ONE authoritative derivation of "which formats can be created". The bridge consumes
> it. It does not reproduce it.**

Consequences for Phase 5:

- **`SHELL-084`** *(new id, after `SHELL-083`)* — a source rule: the creatable-format derivation
  appears in exactly one place; `NOBA.Archive.ShellBridge` obtains it and does not recompute it.
  Failing this rule fails the build, in the same way `SHELL-063` does for the C++ adapter.
- **`SHELL-063` widens.** It currently covers `src/NOBA.Archive.ShellAdapter/` (the C++). It should
  cover the managed bridge as well — the bridge is a marshaller, and a capability predicate in a
  marshaller is a rule in the wrong layer regardless of which language it is written in.
- The single source belongs in **Core**, beside `FormatCapabilityReport`, not in the bridge and not
  in the CLI. `CliApplication` should end up consuming it too, which retires two of the three copies.

**No code has been written for any of this.**

---

## 6. Exact files to be touched

Split by gate, so nothing is opened before its gate is closed.

### 6a. Already changed, this delta (harness and docs only)

| File | Change |
|---|---|
| `harness/phase5-g1/host/g1host.cpp` | **additive** — new `--warm-full` mode (R2 evidence). Sections A, B0, B, C, C2, C3, F, F2, G unchanged; baseline re-verified on Linux |
| `harness/phase5-g1/run-g1.ps1` | rewritten to report your six items, capture the environment fingerprint, hash the harness sources, write a transcript, and run the R2 mode |
| `docs/DROP08_PHASE5_DELTA.md` | this document |
| `docs/DROP08_PHASE5_PLAN.md` | **new §L** — installation, elevation and the security boundary. Inserted before §K; §0–J unchanged apart from §F.2, which §L.6 refines rather than contradicts |

**Not touched:** `BridgeExports.cs` · `NOBA.Phase5.G1.Bridge.csproj` · anything under `src/` ·
anything under `tests/` · anything under `scripts/` · `docs/adr/ADR-0006`.

### 6b. Gate 1 (Windows G.1) — no source files

Running it produces `harness/phase5-g1/results/g1-windows-<timestamp>.log` and nothing else. Its
result is then written into `DROP08_PHASE5_G1_REPORT.md` §5–§7, replacing the Linux figures.

### 6c. Gate 2 (isolation) — **BUILT 2026-08-28 under the Option 2 authorisation**

All of it outside `src/`, for the same reason the G.1 harness is.

| File | Gate | What it is |
|---|---|---|
| `harness/phase5-e/surrogate/noba_e_probe.cpp` | E1 | COM DLL — `DllGetClassObject`, `IClassFactory`, an `IDispatch` probe, hosts the bridge |
| `harness/phase5-e/surrogate/noba_e_probe.def` | E1 | exports |
| `harness/phase5-e/client/e_client.cpp` | E1 | `CoCreateInstance(CLSCTX_LOCAL_SERVER)`, enumerates modules in **both** processes, faults the surrogate |
| `harness/phase5-e/run-e1.ps1` | E1 | registers under `HKCU`, runs, **unregisters in a `finally`**, checks for residue |
| `harness/phase5-e/package/StubCommand.cpp` + `.def` | E2 | stub `IExplorerCommand` — constant title, unconditional `ECS_ENABLED`, no-op `Invoke` |
| `harness/phase5-e/package/noba_e2_host.cpp` | E2 | `int main() { return 0; }` — the manifest requires an `Executable` that exists; nothing launches it |
| `harness/phase5-e/package/AppxManifest.xml` | E2 | sparse package, `com:SurrogateServer`, `desktop5:FileExplorerContextMenus`, **no `RunAs`** |
| `harness/phase5-e/package/Assets/*.png` | E2 | the three logos `makeappx` requires |
| `harness/phase5-e/run-e2.ps1` | E2 | dev cert → pack → sign → register → observe → **remove package and cert in a `finally`** |

**`IDispatch`, not a custom interface.** A custom `IUnknown`-derived interface cannot cross a process
boundary without a proxy/stub or a registered type library. `IID_IDispatch` has a system-registered
proxy, so an `IDispatch` probe marshals with no extra machinery — three fixed DISPIDs, no type
information, nothing to drift.

**Why `noba_e_probe.cpp` duplicates the `hostfxr` code from `g1host.cpp`.** `g1host.cpp` is
hash-locked to the Gate 1 transcript — its SHA-256 is printed there as proof of provenance, and
refactoring it into a shared header would invalidate that proof. Duplication in a throwaway harness
is the cheaper of the two costs.

No file under `src/`, `tests/` or `scripts/` is touched.

### 6d. Phase 5 implementation — **not authorised, listed for completeness**

Unchanged from §J of the plan, plus what this delta adds: `NobaShellWarm` in the ABI (R1), warming
through **two** synthetic menus (§3), and the single-source capability derivation with `SHELL-084`
and the widened `SHELL-063` (§5).

---

## 7. Status

| | |
|---|---|
| B1 | **recommended, conditional** — unchanged |
| B2 | rejected — unchanged |
| B3 | fallback only; no evidence produced for it, none sought |
| ADR-0006 | **unamended** |
| classic registry workaround | not attempted |
| `%*` → `%1` | not changed |
| collector/debounce | not implemented |
| N-process → N-archive | not accepted |
| MSIX / signing / Phase 6 / DROP 09 | not started |
| `IExplorerCommand` | **not started** |
| installer | **not started** — §L is architecture only |
| elevation as a `SHELL-L8` workaround | explicitly ruled out, §L.8 |

**Blocked on you for two decisions:** run gate 1 on Windows, and choose an option in §2 for gate 2.

## 8. Installation / elevation boundary — added 2026-08-28

Recorded in full as **`DROP08_PHASE5_PLAN.md` §L**, answering the seven required items. The headline:

**`INSTALL / UNINSTALL = Administrator. NORMAL RUNTIME = Standard User.`**

**ADR-0006 is not amended, and no contradiction was found.** It already chose a per-machine WiX MSI
with an `HKLM` install record and a `PATH` entry — all elevated operations. The requirement names a
boundary that decision already implied.

Three things §L says that are not obvious, and are the reason it is worth reading rather than
skimming:

1. **MSIX does not inherently require elevation.** Per-user registration
   (`Add-AppxPackage -ExternalLocation`) runs with no UAC prompt at all. Elevation here follows from
   choosing a *per-machine* install, which ADR-0006 did for reasons unrelated to the shell. The
   requirement is satisfied — but by a choice, not by a platform constraint.
2. **An elevated installer must never call plain `Add-AppxPackage`.** That registers the package for
   the *installing* principal — the administrator or `SYSTEM` — and the real user's Explorer then
   shows nothing, with no error to explain it. The documented per-machine pair is *stage then
   provision* precisely because one step cannot cross that boundary. The same trap applies to the
   Windows 10 classic fallback: `shell-register.ps1` writes `HKCU` **by design**, so running it from
   an elevated installer would land the keys in the administrator's hive. Recorded against the
   installer; **the script is not modified.**
3. **Deprovisioning is not uninstalling.** A user who has already signed in once keeps a working
   registration after the provisioned package is removed — so the menu survives an "uninstall" that
   only deprovisions. `SHELL-086` is the new id for uninstall completeness across both surfaces.

Two new ids proposed, after `SHELL-084`: **`SHELL-085`** (no `RunAs`, no elevation moniker, no
`CoCreateInstanceAsAdmin`, no `runas` verb, no `requestedExecutionLevel` other than `asInvoker`,
enforced as a source rule and against the generated manifest) and **`SHELL-086`** (uninstall
completeness). Neither is implemented.

**One finding from the current tree, recorded not fixed:** `src/NOBA.Archive.App/app.manifest`
declares `dpiAwareness`, `longPathAware` and `supportedOS` but **no `requestedExecutionLevel`**, and
`NOBA.Archive.Cli` has no manifest at all. Both should state `asInvoker` explicitly under this
requirement. That is implementation, and this turn was a plan update.

---

**Sources for the two documented claims in §2:**
[Registering the DLL Server for Surrogate Activation — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/com/registering-the-dll-server-for-surrogate-activation) ·
[Packaged DLL for Windows 11 Explorer context menu — xplorer² blog](https://www.zabkat.com/blog/win11-explorer-menu-package.htm) ·
[Support legacy context menus — MSIX, Microsoft Learn](https://learn.microsoft.com/en-us/windows/msix/packaging-tool/support-legacy-context-menus)

---

## 9. GATE 2 — E1 PASS, E2 BLOCKED BY A DEFECT IN MY MANIFEST

**Run by the user on Windows 11 Pro build 26200, 2026-08-28 22:24.**

# GATE 2: OPEN — E1 PASS, E2 NOT YET RUN

Gate 2 is FAIL unless **both** halves pass, and E2 never reached the point of being tested. **The
blocker was my own manifest, not a Windows finding.** Nothing was learned about isolation that E1 did
not already show, and nothing about the packaged path either way.

### 9.1 E1 — PASS, 4 of 4

| | Criterion | Evidence |
|---|---|---|
| **4** | the bridge round-trips across the process boundary | the real menu came back from the surrogate, including `{"format":"gzip","state":"disabled","blocker":"SingleMemberFormat"}` — from a probe containing no format name |
| **1** | the client did not load the runtime | client pid 11224, **22 modules, coreclr/hostfxr absent** |
| **2** | the surrogate did | host pid 40500, **61 modules, coreclr=yes hostfxr=yes probe=yes** |
| **3** | faulting it did not take the client down | `Invoke` returned **0x80010105** (`RPC_E_SERVERFAULT`), and the client kept executing to print its own verdict |

The probe's log, written from inside `dllhost.exe`, reads as one continuous story in a single process
that is not the client:

```
[pid 40500] DllGetClassObject — loaded into this process
[pid 40500] DISPID 1 -> pid reported
[pid 40500] CLR started and bridge resolved INSIDE this process
[pid 40500] DISPID 2 -> menu returned (400 bytes)
[pid 40500] DISPID 3 -> faulting this process deliberately
```

Unregistration was clean — nothing NOBA-owned survived under `HKCU`.

**What E1 establishes:** the *hosting model* B1 is betting on works. A DLL that starts the CLR,
activated out-of-process, keeps the runtime entirely out of the caller, and its death is survivable.

**What E1 does not establish, and is not allowed to stand in for:** the packaged activation path.
`HKCU` + `DllSurrogate` is not the packaged COM catalogue Explorer uses. **That is E2, and E2 has not
run.**

### 9.2 E2 — blocked, and the blocker was mine

```
MakeAppx : error: error C00CE014: App manifest validation error:
  Element '{...appx/manifest/desktop/windows10/5}Extension' is unexpected according to content
  model of parent element '{...appx/manifest/foundation/windows10}Extensions'.
```

**Root cause.** I wrote the whole context-menu extension in the `desktop5` namespace. The schema
requires **mixed** prefixes: `Extension` and `FileExplorerContextMenus` are **desktop4**;
`ItemType` and `Verb` are **desktop5**. `FileExplorerContextMenus` shipped in desktop4 and `ItemType`
was added in desktop5, so the pairing is historical, not arbitrary — and the Microsoft context-menu
sample uses exactly that mix. I did not check it before shipping the file, and it cost a Windows run.

**Fixed**, with the rejection quoted in the manifest itself so the next reader meets the reason where
the mistake would be made.

### 9.3 Four defects fixed, all mine, none a Windows finding

| | Defect | Fix |
|---|---|---|
| 1 | manifest used `desktop5` throughout; the schema needs `desktop4` for `Extension`/`FileExplorerContextMenus` | corrected, and **`run-e2.ps1` now validates the structure before packing** — right prefixes present, wrong prefixes absent |
| 2 | `run-e2.ps1` created and trusted the certificate **before** packing, so a manifest error meant a certificate store was written and rolled back for a run that could never succeed | reordered: publisher check → structure check → `RunAs` check → **pack** → only then the certificate. Everything that can fail for free fails first. |
| 3 | the script `throw`ing out of the `try` meant cleanup ran but **the verdict never printed** — the run ended on a red stack trace instead of a result | `catch` added; a failure now records itself and section 8 still prints **E2 FAIL** |
| 4 | MSVC C4996: `_wfopen` is deprecated, twice in each C++ file | routed through `_wfopen_s` where it exists (`OpenFileW`). **Not** `_CRT_SECURE_NO_WARNINGS` — that is suppression, and this project does not suppress warnings |

**A fifth, caught here rather than on Windows.** The first version of the new `RunAs` check used
`Select-String -Pattern 'RunAs'` on the file text — and flagged the *clean* manifest, because the
manifest's own comment says "there is no RunAs anywhere in this file". A check that matches its own
documentation is worse than no check: it fails on correct input and trains you to ignore it. It now
inspects the parsed XML for a `RunAs` **attribute**. Both controls executed: clean manifest → 0
attributes; a manifest with `RunAs="Interactive User"` injected → 1.

Every structural check was run against the real manifest with three negative controls — all-`desktop5`
(caught, 4 findings), `SurrogateServer` removed (caught, 2 findings), `RunAs` injected (caught).

### 9.4 Provenance for the next run

| Source | At the E1 run | Now | |
|---|---|---|---|
| `e_client.cpp` | `3D4CF063E45E7FBB` | `3D4CF063E45E7FBB` | **unchanged** — the client that produced the E1 verdict is byte-identical |
| `noba_e_probe.cpp` | `806E34EA297B1625` | `87110E312199CC92` | changed: the `_wfopen` fix, in the logging helper only |
| `StubCommand.cpp` | — | `A9812CD19DFF47AE` | |
| `AppxManifest.xml` | — | `35D272CB9865CB76` | |

**E1's PASS stands on its transcript.** The only change to the probe since is how it opens its log
file; none of the four criteria touch that. But the binary that passed is not byte-identical to the
one now on disk, so **please re-run E1 alongside E2** — it takes seconds and restores hash provenance
for both halves of the gate.

```powershell
.\harness\phase5-e\run-e1.ps1
.\harness\phase5-e\run-e2.ps1
```

### 9.5 Status

| | |
|---|---|
| Gate 1 | **PASS** (§6 of `DROP08_PHASE5_G1_REPORT.md`) |
| Gate 2 / E1 | **PASS** — 4/4, transcript above |
| Gate 2 / E2 | **OPEN — BLOCKED BY ENVIRONMENT.** See `DROP08_PHASE5_E2_EVIDENCE.md`. |
| **Gate 2 overall** | **OPEN** |
| Recommendation | **PROCEED B1, still conditional on E2** |

All four C++ artefacts cross-compile clean under MinGW-w64 with `-Wall -Wextra` and now build clean
under MSVC `/W3` without suppression. Nothing under `src/`, `tests/` or `scripts/` was touched.
ADR-0006 unamended. No real `IExplorerCommand`, no production MSIX, no Phase 6, no DROP 09.

---

## 10. GATE 2 — E1 RE-CONFIRMED, E2 ATTEMPT 2

**Run by the user on Windows 11 Pro build 26200, 2026-08-28 23:04–23:05.**

# GATE 2: STILL OPEN — E1 PASS (×2, hash-verified), E2 FAIL at deployment

### 10.1 E1 — PASS twice, and now provenance-clean

Run twice back to back. Identical results, different pids each time (client 72540 → host 45452;
client 39952 → host 51376), which is what an isolated surrogate should look like.

| Source | Transcript | This tree | |
|---|---|---|---|
| `noba_e_probe.cpp` | `87110E312199CC92` | `87110E312199CC92` | **match** |
| `e_client.cpp` | `3D4CF063E45E7FBB` | `3D4CF063E45E7FBB` | **match** |

**The `_wfopen` fix also cleared the two MSVC C4996 warnings** — the build output is now silent where
it previously carried them, with no suppression anywhere.

E1 is settled: 4/4, twice, from a tree whose hashes match. **It still says nothing about E2.**

### 10.2 E2 — packed and signed, refused at deployment

Everything up to registration worked, including all three of the new pre-flight checks:

```
ok    publisher matches the certificate subject: CN=NOBA-DEV-ONLY
ok    manifest structure checked: desktop4 Extension/FileExplorerContextMenus, desktop5 ItemType/Verb
ok    no RunAs attribute in the manifest (§L.4)
      Package creation succeeded.
ok    signed
```

**The manifest fix worked.** `makeappx` accepted the package this time, and the structure check that
would have caught the old defect ran green against the corrected file.

Then:

```
FAIL  E2 could not complete: Deployment failed with HRESULT: 0x800B0109,
      A certificate chain processed, but terminated in a root certificate which is not trusted
      by the trust provider.
```

The `catch` added after the last run did its job: cleanup ran, the certificate was removed, and
**section 8 printed `E2 FAIL - 0 passed, 1 failed`** instead of ending on a stack trace.

### 10.3 Root cause — a wrong recommendation of mine in plan §L.6

I put the certificate in `Cert:\CurrentUser\TrustedPeople`, and §L.6 recommended exactly that, on the
reasoning that a per-user install should need no elevation.

**That reasoning was wrong, and it was reasoning rather than evidence.** MSIX deployment consults the
**machine** TrustedPeople store. Microsoft's signing guidance says it directly — import the
certificate *"into the Local Machine Trusted People store using `Import-PfxCertificate` from an
**admin PowerShell session**"* — and there is a Microsoft community thread whose title is the symptom
verbatim: *"Self-Signed Certificate in User Store Causes MSIX Installation Error"*.

**§L.6 is corrected in the plan**, marked as corrected by measurement, with the withdrawn
recommendation left visible rather than quietly edited away.

### 10.4 The correction strengthens §L rather than undermining it

This is the interesting part, and it is worth not glossing.

| Step | Who | Elevated? |
|---|---|---|
| trust the **DEV-ONLY** certificate | Administrator | **yes — once, then removed** |
| `makeappx` / `signtool` | you | no |
| `Add-AppxPackage -ExternalLocation` | you | **no** |
| Explorer draws the menu | you | **no** |
| the surrogate that activates | you | **no** |

Only *install-time trust* needs Administrator. Registration, Explorer and the surrogate all stay
standard-user — which is precisely the boundary §L describes, now demonstrated rather than asserted.
**Criterion 3 still means what it says**, because the process that activates the handler was never
elevated.

**And it is a development cost, not a product one.** A CA-issued certificate is already chained to a
trusted root, so Phase 9 has no trust step at all. §F.2 named `LocalMachine\TrustedPeople` from the
start; L.6's first version "refined" it to `CurrentUser` on reasoning, and the refinement is
withdrawn.

### 10.5 What changed in `run-e2.ps1`

- **The certificate now goes to `Cert:\LocalMachine\TrustedPeople`**, imported by an **elevated helper
  process**. The script itself still refuses to run elevated, so registration and activation stay
  standard-user and criterion 3 stays meaningful.
- **The trust is verified before deployment is attempted** — the thumbprint is looked up in the
  machine store, so a declined UAC prompt fails with *"the certificate is not in
  LocalMachine\TrustedPeople"* rather than resurfacing later as `0x800B0109`, which names the symptom
  and not the cause.
- **Cleanup elevates a second time** to remove it, then **verifies removal across all three store
  locations** and prints the manual command if anything survives. A DEV-ONLY certificate left trusted
  machine-wide is exactly the residue `SHELL-W9` exists to catch.
- The script warns before each UAC prompt and says why, so neither is a surprise.

Both elevated command strings were parsed and control-tested here before shipping — including the
`$_` escaping trap, where a mis-quoted removal command would have expanded to an empty subject,
matched nothing, and silently left the certificate trusted.

### 10.6 Two UAC prompts to expect

```powershell
.\harness\phase5-e\run-e2.ps1
```

One at the start (trust the DEV-ONLY certificate), one at cleanup (remove it). Between them,
everything runs as you. If you decline either, the script says so plainly rather than failing later
with a chain error.

### 10.7 Status

| | |
|---|---|
| Gate 1 | **PASS** |
| Gate 2 / E1 | **PASS** — 4/4, run twice, hashes verified |
| Gate 2 / E2 | **FAIL** at deployment; root cause found, fixed, and the plan corrected |
| **Gate 2 overall** | **OPEN** |
| Recommendation | **PROCEED B1, still conditional on E2** |

Two Windows runs have now been spent on defects of mine — a manifest namespace and a certificate
store. Both were reasoning where I should have checked documentation first, and both are now checked
by the script rather than by me remembering. Nothing under `src/`, `tests/` or `scripts/` has been
touched. ADR-0006 unamended.

---

## 11. GATE 2 / E2 — ATTEMPT 3: certificate fix worked, a missing manifest element did not

**Run 2026-08-29 07:02. Gate 2 still OPEN.**

### 11.1 What the fix achieved

The `LocalMachine\TrustedPeople` correction was right. The certificate was trusted through the
elevated helper, signing succeeded, and deployment got past `0x800B0109` entirely. All four
pre-flight checks passed. Then:

```
0x80073D2E — The package deployment failed because it must allow external content to be deployed
             with an external location.
```

### 11.2 Root cause — I wrote the comment and forgot the element

The manifest carried this, from the first version:

```xml
<!-- uap10:AllowExternalContent is what makes this a SPARSE package ... -->
```

**The element it describes was never in the file.** I declared the `uap10` namespace, wrote a comment
explaining the feature, and never added `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>`
to `<Properties>`. `Add-AppxPackage -ExternalLocation` requires it.

**And my structure validator did not catch it**, because I wrote that validator from memory of what I
had thought about — the namespace prefixes, the COM server, `RunAs` — and a checklist written from
memory checks memory, not the requirement. The script is the thing that passes `-ExternalLocation`,
so it is the thing that should have known the manifest must permit it.

Fixed both: the element is present, and the validator now asserts `AllowExternalContent` exists **and
is `true`**. Controls executed: present-and-true → OK; element removed → `MISSING`; set to `false` →
`WRONG VALUE 'false'`.

### 11.3 The worse defect — cleanup left a certificate trusted machine-wide

```
Remove-Item : Access is denied
At run-e2.ps1:453 char:34
```

The cleanup loop removed from the **user** stores first, in a bare loop with no `try`/`catch`. One
removal threw, the exception escaped the `finally` block, and **the run ended before the machine-store
certificate was removed.**

**The code written to prevent residue left residue** — and the machine-wide kind, which is exactly
what `SHELL-W9` exists to catch. I have asked you separately to check for and remove it.

Rewritten so this cannot recur:

- **Ordered by consequence.** The machine store is cleaned **first**, because it is the one with
  security consequences. The user copies, which carry no machine-wide trust, come after.
- **Every step is independent.** Package removal, machine certificate, each user store, the
  verification pass and the file cleanup are each wrapped. No step can prevent a later one running.
- **An unreadable store is reported, never treated as empty.** Verified by execution: the helper was
  run against a `Cert:` drive that does not exist and returned cleanly instead of throwing.
- **Verification names what survives**, and says explicitly when the survivor is machine-wide trust,
  with the elevated command to remove it.
- **A new startup check** reports leftover `CN=NOBA-DEV-ONLY` certificates from an earlier aborted
  run — because a run that silently inherits stale trust could otherwise "succeed" for the wrong
  reason.

### 11.4 Three attempts, three defects, all mine

| Attempt | Failed at | Cause | Now prevented by |
|---|---|---|---|
| 1 | `makeappx` | `desktop5` prefixes where the schema needs `desktop4` | structure check before packing |
| 2 | deployment `0x800B0109` | certificate in the user store; **plan §L.6 recommended it** | `LocalMachine` + elevated helper + trust verified before deploying |
| 3 | deployment `0x80073D2E` | `AllowExternalContent` element missing | validator asserts it exists and is `true` |
| — | cleanup | `Remove-Item` on the `Cert:` provider fails for `LocalMachine`, and the loop was unguarded | `X509Store` API throughout; machine store cleaned first; every step independent |

**The pattern is consistent and worth naming: each was reasoning where I should have read the
documentation first, and each check I added afterwards covered what I had already thought about
rather than what the run actually required.** The validator now derives its checks from what the
script does — it passes `-ExternalLocation`, therefore the manifest must allow external content —
rather than from my recollection of the schema.

Attempt 2 is the one that also cost a plan correction; attempt 3 cost a machine-wide certificate left
trusted, which is worse than a failed run.

### 11.6 A fourth defect, found while cleaning up the third

The manual removal I sent also failed:

```
Get-ChildItem Cert:\LocalMachine\TrustedPeople | ? Subject -eq 'CN=NOBA-DEV-ONLY' | Remove-Item -Force
Remove-Item : Access is denied
```

Two causes, and I had both wrong.

**First, that prompt was not elevated** — `CurrentUser\My` succeeded in the same paste while
`LocalMachine` did not, which is exactly what an unelevated prompt looks like. I wrote "in an elevated
PowerShell" above a code block and assumed it would be read as a precondition rather than a caption.

**Second, and more useful: `Remove-Item` on the `Cert:` provider is not reliable for
`LocalMachine\TrustedPeople` even when elevated.** It has now failed twice in this gate — once inside
the script's own cleanup, once by hand. The `X509Store` API opens the store `ReadWrite` explicitly and
does not depend on the provider's behaviour:

```powershell
$s = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
$s.Open('ReadWrite')
$s.Certificates | Where-Object { $_.Subject -eq 'CN=NOBA-DEV-ONLY' } | ForEach-Object { $s.Remove($_) }
$s.Close()
```

**Every certificate operation in `run-e2.ps1` now uses that API** — the elevated machine removal, both
user-store removals, the verification pass, and every printed manual command. Verified by execution:
against a store that cannot be opened, the helper returns cleanly rather than throwing, and the
counter returns **-1 for "could not look"** so that an unreadable store is reported as unknown and
**never silently counted as clean**. That distinction is the whole point — the previous cleanup's real
failure was reporting nothing while leaving a machine-wide certificate behind.

### 11.5 Status

| | |
|---|---|
| Gate 1 | **PASS** |
| Gate 2 / E1 | **PASS** — 4/4, twice, hash-verified |
| Gate 2 / E2 | **FAIL ×3**, attempt 4 ready |
| **Gate 2 overall** | **OPEN** |
| Recommendation | **PROCEED B1, still conditional on E2** |

Nothing under `src/`, `tests/` or `scripts/` touched. ADR-0006 unamended. No real `IExplorerCommand`,
no production MSIX, no Phase 6, no DROP 09.

---

## 12. E2 — FROZEN, BLOCKED BY ENVIRONMENT (2026-08-29)

**Full evidence: [`DROP08_PHASE5_E2_EVIDENCE.md`](DROP08_PHASE5_E2_EVIDENCE.md).**

Attempt 4 reached deployment with every pre-flight requirement green — 30 requirements, package
packed, package signed, certificate trusted and verified — and was refused by the machine:

```
0x80073CFF — To install this application you need either a Windows developer license
             or a sideloading-enabled system.
```

The machine carries an explicit block: `HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx`
`AllowAllTrustedApps = 0`. **Changing it is not authorised and is not being asked for.**

**E2 is neither a pass nor a fail.** Explorer activation was never reached, so none of the three
acceptance criteria was observed or refuted. **The criteria are unchanged and are not weakened.**
Gate 2 stays **OPEN**.

Cleanup was clean: no package, no certificate in any store, no generated files.

**The harness now treats this as a distinct outcome.** `S01` fails pre-flight before anything is
created, and the exit code is **2 (BLOCKED)**, never 1 (FAIL). `run-e2.ps1 -PreconditionsOnly` prints
the precondition report — policy source, effective value, whether it is user-configurable, and what
the harness will do — reading two registry values and creating nothing.

**The decision was split from the registry read** so the BLOCKED branch could be tested: it is the
branch that matters and it is unreachable on a machine without the policy. Seven branch controls now
execute, including both blocked cases. Local suite: **32 checks, 0 failures.**

The permanent product requirement is `DROP08_PHASE5_PLAN.md` §L.13 — detect, explain, continue where
possible, never modify. §6 of the evidence document states what a clean Windows environment needs to
close E2 legitimately.

---

## 13. The harness never reached E2 — a PowerShell 5.1 encoding defect (2026-08-29)

```
# GATE 2: STILL OPEN. E2 NOT RUN. This is a harness provenance/compatibility defect,
#                     not an E2 result. The frozen evidence in §12 is unchanged.
```

### 13.1 What was reported

`run-e2.ps1` reached the VM and Windows PowerShell 5.1 refused to parse it:

```
At line:130 char:1  Missing closing '}' in statement block or type definition.
At line:122 char:29 function Show-DeploymentLog {
```

The VM copy also displayed mojibake — `â€"` and `â†'`. `Show-DeploymentLog`'s `try`/`catch` is
syntactically closed, so the reported brace was not the defect.

### 13.2 Provenance — checked first, and clean

| | |
|---|---|
| VM SHA-256 | `01CAE4B48A5A6158B70876CFAB76ADD557645379A68A9B9533AC2D86904C3683` |
| source tree SHA-256 | `01cae4b48a5a6158b70876cfab76add557645379a68a9b9533ac2d86904c3683` |
| verdict | **identical.** Not a transfer defect. No byte-preserving re-transfer was needed. |

### 13.3 Root cause — established by reproduction, not by reading

Every harness script was **UTF-8 without a BOM**. `run-e2.ps1` held 77 non-ASCII bytes:
`→` ×17, `—` ×6, `§` ×4.

**Windows PowerShell 5.1 decodes a BOM-less file as the machine's ANSI codepage, not as UTF-8.**
PowerShell 7 defaults to UTF-8, which is why every parse check run before this passed.

`→` is U+2192, stored as `E2 86 92`. In CP1252 the byte `0x92` is **U+2019 RIGHT SINGLE QUOTATION
MARK**, and **PowerShell accepts U+2019 as a string delimiter**. Each mis-decoded arrow therefore
opened or closed a string; the closing braces between them ended up inside string literals, and the
parser reported the imbalance at the nearest enclosing function — `Show-DeploymentLog`, which holds
two arrow lines.

The experiment: decode the file's bytes as CP1252, hand the text to
`[System.Management.Automation.Language.Parser]`. Result — **the same two errors, at line 130 and
line 122.** The four dangerous bytes are `0x91`–`0x94`.

### 13.4 The parse alone would not have caught it

`run-e1.ps1`, `run-g1.ps1` and `test-e2-preflight.ps1` **parsed cleanly** under the same CP1252
decode while carrying the same hazard: their quote bytes happened to be even in number, or sat
inside a `<# #>` block comment. `E2Preflight.psm1` did **not** — it failed with 5 errors, which
means the pre-flight module itself would not have imported on the VM.

An even number of mis-decoded quotes pairs up. That is luck, not correctness, and it is why the
rule below is a **byte rule**, with the parse kept as its falsification rather than as the guarantee.

### 13.5 The rule

> **A harness script is pure ASCII, or it carries a UTF-8 BOM.**

Pure ASCII decodes to the same characters under UTF-8 and under every ANSI codepage — no ANSI
codepage reassigns `0x00`–`0x7F`, and no DBCS lead byte is below `0x81`. A BOM makes 5.1 decode as
UTF-8 explicitly. Either removes the failure class; neither is a matter of taste.

### 13.6 What changed

| File | Change |
|---|---|
| `harness/phase5-e/run-e2.ps1` | ASCII-only: `→`→`->`, `—`→`-`, `§`→`section `. Pre-flight gains requirement **H01**. |
| `harness/phase5-e/run-e1.ps1` | ASCII-only |
| `harness/phase5-e/E2Preflight.psm1` | ASCII-only. Adds `Test-E2ScriptEncoding`, `Get-E2ScriptEncodingFacts`, `ConvertFrom-E2ScriptBytes`. |
| `harness/phase5-e/test-e2-preflight.ps1` | ASCII-only. Adds a bootstrap byte guard **before** `Import-Module`, and section 0. |
| `harness/phase5-g1/run-g1.ps1` | ASCII-only |

No product code, no `scripts/`, no ADR-0006, no `ShellMenuBuilder`, `ShellInvocations`,
`ShellLauncher`. No `IExplorerCommand` work. E2 was not run.

### 13.7 The permanent guard

`Test-E2ScriptEncoding` produces two findings per file per codepage:

| | Requirement |
|---|---|
| `H01-<file>` | pure ASCII, or a UTF-8 BOM — the guarantee |
| `H02-<file>-<cp>` | the raw bytes, decoded as 5.1 would decode them, parse — the falsification |

It runs in two places:

1. **`test-e2-preflight.ps1` section 0**, over the whole `harness/` tree, at codepages 1252, 1251
   and 932. A **bootstrap byte check runs before `Import-Module`**, because a mis-decoded module
   throws a parse error at import and can never explain itself — which is precisely what the VM saw.
2. **`run-e2.ps1` pre-flight, requirement `H01`**, folded to one line so the transcript stays
   readable; every failure is named in the detail.

Five controls, each of which has been seen to fail:

| Control | Asserts |
|---|---|
| the rebuilt `Show-DeploymentLog` hazard, no BOM | rejected by the byte rule **and** fails the 5.1 parse — 3 errors, first at line 4 |
| the same content **with** a BOM | passes both |
| the same content written `->` | passes both |
| an **even** number of arrows, no BOM | **parses cleanly** — a parse-only guard would have passed it |
| the same file | rejected by the byte rule anyway |

The hazard characters are built from their code points and never typed, so the control suite stays
pure ASCII itself.

Local suite: **40 checks, 0 failures.** Pre-flight: **32 requirements** (was 31), `H01` first.

### 13.8 `scripts/` — reported, not modified

`scripts/` is protected, so it was surveyed read-only with the same tool. Three files declare
`#Requires -Version 5.1` and **cannot parse under it** on a machine whose ANSI codepage is not UTF-8:

| File | 5.1 parse (cp1252) | Content |
|---|---|---|
| `scripts/dragdrop-gate.ps1` | **FAIL**, 4 errors, first line 93 | Arabic and Japanese path fixtures |
| `scripts/format-gate.ps1` | **FAIL**, 5 errors, first line 195 | same fixtures |
| `scripts/shell-gate.ps1` | **FAIL**, 13 errors, first line 431 | same fixtures |

These are **deliberate Unicode test data** — `مستندات\تقرير سنوي.txt`, `日本語のファイル.txt` — and
must stay Unicode. **ASCII is the wrong fix for them; a UTF-8 BOM is the only one.**

Six more are BOM-less with quote-capable bytes and parse today only by luck:
`alpha.ps1`, `shakedown.ps1`, `shell-argv-probe.ps1`, `shell-register-trace.ps1`,
`shell-register.ps1`, `validate.ps1`.

Clean already: `app.ps1`, `benchmark.ps1`, `build.ps1`, `test.ps1` (pure ASCII);
`create-gate.ps1`, `make-ops-fixture.ps1`, `watch-destination.ps1` (UTF-8 with BOM).

**Nothing under `scripts/` was touched.** If these ever ran under 5.1 on the development machine,
the explanation would be that machine's ANSI codepage being set to UTF-8 (65001) — that is a
hypothesis, not a finding, and it is not assumed here.

### 13.9 The pattern, again

Every parse check run before this one passed, because they all ran under PowerShell 7 — the platform
that does not have the defect. The check covered what I had already thought about instead of what the
target platform actually does. The guard added here is deliberately built the other way round: the
byte rule is derived from how 5.1 decodes files, and the parse is pinned to a codepage rather than
inherited from whatever host happens to run it, so it reproduces the VM's failure on Linux.

### 13.10 Status

| | |
|---|---|
| E2 | **OPEN.** Not run. §12's environment block stands. |
| E2 acceptance criteria | **unchanged** |
| Gate 1 | PASS |
| E1 | PASS ×2, hash-verified |
| Harness 5.1 parseability | **PASS** — 5 files, 21 checks, 3 codepages |
| `scripts/` | 3 files unparseable under 5.1, **reported, not modified** |
| `IExplorerCommand` | NOT STARTED |

---

## 14. A permissive VM exists — the environment block is falsified, off the development machine (2026-08-30)

```
# GATE 2: OPEN and now RUNNABLE. E2 still NOT RUN - the VM has no toolchain yet.
#         Nothing here is an E2 result.
```

### 14.1 Checklist step 1, on `NOBA-Test`

`C:\Users\NOBA-Test\Desktop\NOBA-Archive`, Windows 11 Pro build **26200**, unelevated:

```
effective state    : PERMITTED
policy source      : AppModelUnlock\AllowDevelopmentWithoutDevLicense = 1 (Developer Mode)
harness action     : PROCEED.
HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx
    AllowAllTrustedApps                = (unset)
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock
    AllowAllTrustedApps                = (unset)
    AllowDevelopmentWithoutDevLicense  = 1
ok    E2 can run on this machine.
```

**The policy key is unset here.** On the development machine it is `0`, which overrides everything —
three byte-identical transcripts, §12. This is the first machine on which E2 can be attempted
legitimately, and **no policy was changed on either machine to get here.**

Scope, stated before the run rather than after it: E2 passing on a sideloading-enabled machine
closes the packaged `com:SurrogateServer` activation path. It says nothing about machines carrying
the policy block. **§L.13 is unaffected and is still a product decision.**

### 14.2 Section 13's fix, confirmed on a real Windows PowerShell 5.1 host

The encoding defect was diagnosed by emulating 5.1's decode. This is the first time the corrected
files have run under the real thing — `powershell.exe`, 5.1.26100.9168:

| | |
|---|---|
| `run-e2.ps1` | **parsed and executed.** This is the file that failed at lines 130 and 122. |
| `E2Preflight.psm1` | **imported.** The precondition report is its output. It had 5 errors under the CP1252 decode. |
| `docs/vm/verify-toolchain.ps1` | parsed and executed |
| not yet exercised under 5.1 | `test-e2-preflight.ps1` (section 0), `run-e1.ps1`, `run-g1.ps1` — checklist steps 3, 4, 5 |

One variable is not yet recorded: **this VM's ANSI codepage.** If it happened to be 65001, a BOM-less
UTF-8 file would have decoded correctly regardless. That does not weaken the diagnosis — the
mechanism was established by reproduction, and the original failure on a 5.1 host is on record — but
it is the difference between "the fix works" and "the fix was needed here". `verify-toolchain.ps1`
now reports the codepage so the question stops being open.

### 14.3 Toolchain: absent, as expected on a fresh VM

`verify-toolchain.ps1`: **3 ok, 4 failures.** No `dotnet`, no `vswhere`, no `Windows Kits\10\bin`,
no `cl.exe`. Host checks all passed: build 26200, PowerShell 5.1 Desktop, shell not elevated.

Install list and exact component IDs: `DROP08_PHASE5_VM_TOOLCHAIN.md`.

**A defect in the verification script itself, found by running it:** the missing-`LIB` check was
counted as a *difference* when MSVC was absent, reporting one failure twice and making
`4 failure(s), 1 difference(s)` mean less than it said. It is now skipped when MSVC is absent, and
reported as a **failure** when `vcvars64.bat` imports and `LIB` is still empty — which is a real and
distinct defect.

### 14.4 Status

| | |
|---|---|
| E2 | **OPEN. Runnable on `NOBA-Test`, not run.** Blocked only by the missing toolchain. |
| E2 acceptance criteria | **unchanged** |
| §L.13 | unaffected |
| Development machine | untouched, still policy-blocked, still not used for E2 |
| Harness | unchanged — no product code, no `scripts/`, no ADR-0006 |
| `IExplorerCommand` | NOT STARTED |

---

## 15. E2 RUN 1 — 2 PASS, 1 UNMEASURED. Not a refutation, and not closed (2026-08-31)

```
# GATE 2: OPEN. E2 ran end to end on NOBA-Test and returned 2/3.
#         Criterion 3 was NOT MEASURED. That is a harness defect, not a Windows finding.
```

### 15.1 What the run established

Everything up to the criteria worked, unelevated, on a permissive machine: stub built (MSVC
14.44.35207), pre-flight **32/32 including H01**, packed, DEV-ONLY certificate created and trusted
(UAC #1), signed, **registered for the user with `-ExternalLocation`**, Explorer restarted, cleanup
clean (UAC #2) — `no residue: no package, no certificate in any store, no generated files`.

| Criterion | Result |
|---|---|
| 1. verb in the modern menu | **PASS** — observed above *Show more options* |
| 2. `explorer.exe` loaded none of coreclr/hostfxr/stub | **PASS** — 1 process checked, none present |
| 3. a `dllhost.exe` hosts the stub, as the standard user | **FAIL** — *"no dllhost.exe has the stub loaded - either never activated, or it has already exited"* |

### 15.2 What section 11 proved, and what it did not

The stub's own log, written from inside the hosting process:

```
[pid 11912] DllGetClassObject - loaded into this process
[pid 11912] GetTitle - Explorer activated this handler here
[pid 11912] NobaShellWarm returned 0 - the CLR is running IN THIS PROCESS
[pid  8388] DllGetClassObject - loaded into this process
[pid  8388] NobaShellWarm returned 0 - the CLR is running IN THIS PROCESS
[pid  8388] Invoke - deliberately does nothing
```

**Established:** the stub loaded into two processes that are not `explorer.exe` (criterion 2 measured
explorer independently), and the CLR ran in both. Two distinct PIDs is itself consistent with a
surrogate that starts and exits quickly.

**Not established:** that the host was `dllhost.exe`, and that it ran as the standard user. Those are
precisely the two facts criterion 3 asserts, and the log records only a PID.

So criterion 3 is **corroborated but unmeasured**. It is not a refutation, and it is not a pass.

### 15.3 Root cause — two defects, both mine

**(a) The measurement was a race the harness could not win.** Criterion 3 sampled live processes
**once**, after a human pressed ENTER. A COM surrogate exits when idle. The lifetime of the thing
being measured was not under the harness's control, and the sample was taken after it ended.

**(b) "Could not look" was reported as "nothing was there."**

```powershell
function Get-ProcModules([string]$Name) {
    ...
    if ($rows.Count -eq 0) { return $null }   # $null means "could not look"
}
function Get-E2Surrogates {
    $rows = Get-ProcModules 'dllhost'
    if ($null -eq $rows) { return @() }        # ... and here it silently became "none"
}
```

Criterion 2, three lines away, honours the distinction — *"could not read explorer module lists —
NOT evidence of absence."* Criterion 3 collapsed it and then printed a conclusion the harness could
not support. **This is the certificate rule** — *if an operation cannot inspect, the result is
UNKNOWN, never CLEAN* — applied to one code path and not to the one beside it.

Ruled out before writing this, rather than assumed: `com:SurrogateServer` in the manifest declares
neither `CustomSurrogateExecutable` nor `SystemSurrogate`, which is the empty-`DllSurrogate` shape,
so the system surrogate `dllhost.exe` is the correct process to look for. Looking for `dllhost` was
not itself the error.

### 15.4 The fix — PowerShell only, no C++, no product code

| | Change |
|---|---|
| **A** | `Get-ProcModules` returns a record — `Found` / `Rows` / `Unreadable` / `Error` — so *no such process*, *present but unreadable*, and *present and read* stay three different answers. |
| **B** | Section 9 now **watches** for up to `-ObserveSeconds` (default 180), polling every 250 ms, and captures the surrogate's PID, process name, owner, and module list **at the moment of detection**. The operator can stop early with any key. Criterion 3 reads the capture instead of re-sampling. |
| **C** | The explorer PID and start time are pinned **before** the observation window. A clean but *different* explorer at criterion-2 time is now **UNKNOWN**, not PASS — `coreclr` never unloads, so a clean explorer only means something if it is the one that drew the menu. |
| **D** | `Resolve-E2SurrogateVerdict` is split out of the measurement, the same way `Resolve-E2SideloadingState` was, because the branch that matters is unreachable on a healthy machine. |
| **E** | Three-state verdicts. `UNKNOWN` does not count towards 3/3, and section 13 now prints **`E2 NOT CLOSED — n UNMEASURED`** rather than a bare FAIL, so no reader records a finding that was never made. |

The stub was deliberately **not** changed. `StubCommand.cpp` stays inside its seven constraints.

### 15.5 Controls

Eight branch controls plus two regression controls, all runnable on any platform:

| Control | Expect |
|---|---|
| captured, CLR + hostfxr, right owner | PASS |
| captured, wrong owner | FAIL |
| captured, no CLR | FAIL |
| captured, no hostfxr | FAIL |
| not captured, module lists unreadable | **UNKNOWN** |
| not captured, hosts present and readable | FAIL |
| not captured, no host process ever existed | FAIL |
| unreadable outranks "hosts were present" | **UNKNOWN** |
| UNKNOWN never says *"never activated"* or *"already exited"* | asserted on the text |
| UNKNOWN says plainly that absence was not established | asserted on the text |

Local suite: **50 checks, 0 failures** (was 40). All five harness scripts still pure ASCII and still
parse under the 5.1-equivalent decode.

### 15.6 The pattern

Attempt 4 printed the cause of a failure and deployed anyway. This run printed a conclusion its own
measurement could not support. Both are the same defect: **the harness stated more than it had
observed.** The correction is structural rather than a patch — a criterion that cannot be measured
now says so, in its own state, and the transcript refuses to call it a finding.

### 15.7 Status

| | |
|---|---|
| E2 | **OPEN. NOT CLOSED.** 2 measured PASS, 1 unmeasured. Re-run required. |
| E2 acceptance criteria | **unchanged** — the criteria were not weakened, the measurement was fixed |
| Isolation hypothesis | **not refuted**; corroborated by the stub log, not yet measured |
| Machine | `NOBA-Test`, permissive, clean after the run |
| Product code | untouched — `src/`, `tests/`, `scripts/`, ADR-0006, `StubCommand.cpp` |
| `IExplorerCommand` | NOT STARTED |

---

## 16. §15's fix broke the residue scan. Two stale call sites, and a suite that could not see them (2026-08-31)

```
# GATE 2: OPEN. E2 did not reach BUILD. Nothing was created, nothing to clean.
#         A harness defect, again mine, and this one the control suite was structurally blind to.
```

### 16.1 What happened

```
1. RESIDUE FROM PREVIOUS RUNS
The term 'Get-E2Surrogates' is not recognized
```

§15 renamed `Get-E2Surrogates` to `Find-E2Surrogate` and changed its return shape. **Two call sites
were left behind**: the residue scan at section 1, and step 1 of `Invoke-E2Cleanup`. The run died
before BUILD, which is the cheap place for it to die — but only by luck. `Invoke-E2Cleanup` runs in
a `finally`, so the same missing function would have thrown **during cleanup**, on a run that had
already trusted a certificate machine-wide and registered a package. That is attempt 3's residue
failure, rebuilt.

### 16.2 Why the suite passed 50/50 with the harness broken

**The control suite never loads `run-e2.ps1`.** It imports `E2Preflight.psm1`, exercises the module,
and validates the manifest. `run-e2.ps1` is the file that carries the sequence, the state and the
call sites, and until now the suite's only claim about it was that it *parses*.

Parsing does not resolve command names. PowerShell binds commands at call time, so a call to a
function that no longer exists is a runtime error on the line that runs it — which is why it
surfaced at section 1 of a live run instead of in a suite that reports 50 checks.

So the fix in §15 was correct and the way it was verified was not: 50/50 measured the part I had
changed and not the part that had to keep working. **Same shape as §13 and §15 — the check covered
what I had already thought about.**

### 16.3 The fix

| | Change |
|---|---|
| `Get-E2Surrogates` | restored, and now returns a **record** — `Rows` / `Found` / `Unreadable` — instead of a bare list. `Find-E2Surrogate` is built on it. |
| residue scan | consumes `.Rows`; an unreadable module list is now reported as `UNREADABLE - surrogate state unknown` and counts as residue, not as clean |
| `Invoke-E2Cleanup` | consumes `.Rows`; unreadable lists and a failed scan both add residue lines instead of passing silently |

The three-state rule now holds on all three paths that ask about surrogates — criterion 3, the
residue scan, and cleanup — rather than only on the one §15 rewrote.

### 16.4 The control that closes the class

Section 7 of the suite parses every harness script and resolves **every command it calls** against
the functions the file defines, the functions `E2Preflight` exports, and what is installed. Anything
left over is a call to something that does not exist.

Verified by falsification, not by assertion: the pre-fix `run-e2.ps1` was reconstructed and scanned.

```
SCAN OF THE PRE-FIX FILE -> unresolved: Get-E2Surrogates
```

Two controls: a synthetic script with a call site left behind by a rename must be caught **by name**,
and its corrected version must pass.

The Windows-only allowlist is explicit — `Add-AppxPackage`, `Export-Certificate`, `Get-AppxPackage`,
`Get-CimInstance`, `Get-MpComputerStatus`, `Get-WinEvent`, `Invoke-CimMethod`,
`New-SelfSignedCertificate`, `Remove-AppxPackage`, `cl.exe`, `cmd.exe`, `powercfg`. It doubles as the
record of what the harness needs Windows for, and a new entry has to be added by hand, which is a
review point rather than a silent pass.

### 16.5 The count was platform-dependent, and the checklist said one number

The VM reported `49 checks` where this machine reported `50`. Both were correct: section 4's
*"no registry -> FAIL, not PASS"* control can only run where there is no registry to read, so
Windows runs one fewer. The checklist stated a single number, which made a correct Windows run look
like an incomplete transfer.

The suite now prints the platform it counted for:

```
LOCAL CONTROL SUITE: PASS - 57 checks, 0 failures (windows)
LOCAL CONTROL SUITE: PASS - 58 checks, 0 failures (non-windows)
```

### 16.6 Status

| | |
|---|---|
| E2 | **OPEN. Not run since §15.** The §15 run's 2 PASS / 1 UNMEASURED still stands as the last result. |
| E2 acceptance criteria | **unchanged** |
| Local suite | **58 non-Windows / 57 Windows**, 0 failures |
| Product code | untouched — `src/`, `tests/`, `scripts/`, ADR-0006, `StubCommand.cpp`, the manifest |
| `IExplorerCommand` | NOT STARTED |

---

## 17. The 05:00 run diagnosed: a mistimed window, not a refutation (2026-08-31)

```
# GATE 2: OPEN. The 05:00 transcript is preserved as the official FAIL evidence and is NOT restated.
#         Criterion 3 stays FAIL there. The measurement is fixed for the NEXT run only.
```

### 17.1 The line that settles it

```
12. CLEANUP
      stopped surrogate pid 13232
```

`Invoke-E2Cleanup` calls **`Get-E2Surrogates`** — the same function the watcher reaches through
`Find-E2Surrogate`, the same `dllhost` name filter, the same `-like '*noba_e2_stub*'` substring.
It found pid 13232 holding the stub, **after** the window had closed and the criteria had been
printed.

So the watcher's matcher works on this exact process. Whatever went wrong, it was not the matcher.

### 17.2 The five questions, answered from the transcript

| | Question | Answer |
|---|---|---|
| 1 | Was pid 13232 alive before the polling sample? | **No evidence that it was alive during the window.** ~720 samples over 180 s, `unreadable: 0` throughout — every `dllhost` was readable and none held the stub. It was alive at cleanup. |
| 2 | Did `Get-ProcModules` inspect the same PID while alive? | It inspected every readable `dllhost` 720 times and matched none; it matched this PID at cleanup. So the inspection path works — the PID was not there to be inspected. |
| 3 | Loaded before the first enumeration and unloaded before the next? | **Refuted.** Nothing unloaded: cleanup found the stub *still loaded* in 13232 long afterwards. |
| 4 | Does the watcher only do an exact module-name match, missing a short-lived activation? | **Refuted twice.** The match is a substring (`*noba_e2_stub*`), and that same match succeeded on this same process at cleanup. The lifetime was not short. |
| 5 | Does §11 write the PID before or after the module-load event, and can it be correlated without touching the stub? | §11's first line is written **from inside `DllGetClassObject`**, so after the DLL is loaded. And it needs no ETW and no stub change to correlate: **cleanup already correlated it.** `Get-ProcModules` also records `StartTime`, which settles "alive during the window?" for free — it was captured and then discarded. |

**Conclusion: the activation happened outside the observation window.** Not a lifetime problem, not
a matcher problem, not an enumeration problem.

### 17.3 The defect

A fixed 180-second timer measures the **operator**, not the architecture. When it expired the
harness printed:

```
[FAIL] candidate host processes were present and readable throughout the window,
       and none ever had the stub loaded
```

That sentence is true and the conclusion it invites is not earned. *"Nobody activated it while the
window was open"* and *"activation does not land in a surrogate"* produce the identical empty
sample, and **only the operator can tell them apart.** The harness never asked, and reported the
refutation.

This is §15's defect one level up. §15 fixed *"the harness could not look"* being read as *"nothing
was there."* This is *"nobody looked yet"* being read as the same thing.

### 17.4 The fix — measurement only, PowerShell only

| | Change |
|---|---|
| **A** | The window is **driven by the operator, not by a clock**. It ends when the surrogate is captured, or when the operator presses a key to say the right-click has happened. The clock is a backstop. |
| **B** | A keypress does **not** end sampling — it starts a `-GraceSeconds` burst (default 20), because activation and process start are not instantaneous. |
| **C** | If the window expires with no keypress, the harness **asks** whether the right-click happened inside it, and samples once more if the answer is yes. |
| **D** | `Resolve-E2SurrogateVerdict` takes `-ActivationConfirmed`. **Unconfirmed and empty is `UNKNOWN`**, never FAIL. Confirmed and empty is still a real FAIL — the criterion did not become unfailable. |
| **E** | Criterion 1's `y` is itself proof a right-click just happened, so an uncaptured run samples again immediately after that answer. |
| **F** | The window's start and end times are printed, and a captured surrogate's `StartTime` with them. |
| **G** | **Cleanup's late detection is promoted to evidence.** It now prints the surrogate's `StartTime` and whether it started inside the window, with the note that a surrogate visible at cleanup but not during the window means the window was mistimed — the single line that would have diagnosed this run on sight. |

Criterion 3 is unchanged in substance: still a live `dllhost.exe`, still `coreclr` **and** `hostfxr`,
still the standard user. Nothing was relaxed. No product code, no manifest, no `StubCommand.cpp`.

### 17.5 Controls

Sixteen now, including the regression asserted on the text itself:

| Control | Expect |
|---|---|
| **THE 05:00 RUN: window expired unconfirmed** | **UNKNOWN** |
| unconfirmed outranks "no host process ever existed" | UNKNOWN |
| it does not say *"none ever had the stub loaded"* | asserted on the string |
| **criterion 3 can still FAIL once activation is confirmed** | **FAIL** |
| a capture stands even if nobody confirmed | PASS |

Local suite: **64 non-Windows / 63 Windows**, 0 failures.

### 17.6 If polling ever proves fundamentally too short

It is not — 13232 outlived the window and the criteria section. But if a future run shows a
genuinely sub-poll lifetime, the deterministic option is a **harness-driven activation** of the same
CLSID (`CoCreateInstance`, `CLSCTX_LOCAL_SERVER`) with the package registered, measured with no human
timing at all.

**That would be criterion 3b — supporting evidence, never a replacement.** Criterion 3 is about
*Explorer's* activation landing in a surrogate as the standard user; a harness-initiated activation
proves the packaged `com:SurrogateServer` shape works, not that Explorer uses it. It also needs
verifying that a packaged COM class is activatable by CLSID from an unpackaged process, which is a
claim, not a fact, until tested. **Not implemented, not proposed for this run.**

### 17.7 Status

| | |
|---|---|
| E2 | **OPEN.** The 05:00 result stands as recorded: 2 PASS, 1 FAIL. **Not reinterpreted, not converted.** |
| Criterion 3 | unchanged in substance; the measurement that feeds it is fixed |
| Isolation hypothesis | corroborated by §11 and by cleanup's late detection; **still not measured** |
| Product code | untouched — `src/`, `tests/`, `scripts/`, ADR-0006, `StubCommand.cpp`, the manifest |
| `IExplorerCommand` | NOT STARTED |

---

## 18. E2 PASS — 3/3, measured (2026-08-31 10:40)

```
# GATE 2: CLOSED. E1 PASS x2 + E2 PASS 3/3. B1 is no longer conditional.
```

### 18.1 The result

| Criterion | Result | Evidence |
|---|---|---|
| 1. verb in the modern menu | **PASS** | observed above *Show more options* |
| 2. `explorer.exe` loaded none of coreclr/hostfxr/stub | **PASS** | pid **10424**, from the pre-window baseline and still alive at measurement — a clean but *different* explorer would have been UNKNOWN |
| 3. a `dllhost.exe` hosts the stub, as the standard user | **PASS** | **captured live**: pid **10460**, `dllhost`, `coreclr=True hostfxr=True stub=True`, owner `nob11\NOBA-Test` |

**Two independent measurements agree on the PID.** The stub logged `[pid 10460]` from *inside* the
hosting process; the harness measured pid 10460 from outside as a `dllhost.exe` with both runtimes
and the correct owner. Nothing in the harness activates the CLSID, so the activation came from
Explorer drawing the menu.

Both halves of the isolation claim were measured on the same run: the CLR **in** the surrogate and
**not** in the explorer that drew the menu. Cleanup was clean — no package, no certificate in any
store, no generated files.

### 18.2 The PASS survived by one line, and that is a defect

The window ran **10:41:00 -> 10:41:20** and captured nothing. Pid 10460 started **10:41:52** — 32
seconds after it closed. Only the post-criterion-1 resample caught it.

Without that line the run would have printed **FAIL**: confirmed, hosts readable, nothing captured.
A false refutation — worse than the 05:00 UNKNOWN, because it would have looked like a finding.

**Root cause: the keypress REPLACED the deadline.** `$graceUntil = now + GraceSeconds` and the loop
broke there, so confirming immediately shrank the window from 180 s to 20 s. **Eagerness shortened
the measurement.** The operator confirmed at 10:41:00, ~52 s before the activation they were
confirming — self-reported timing cannot be trusted, and the design made that fatal instead of
harmless.

Fixed: grace may only **extend** the window, never end it early. The loop now runs to
`max(deadline, keypress + GraceSeconds)`. With this fix the window would have run to 10:44:00 and
captured 10460 at 10:41:52, in the window, with no resample needed.

### 18.3 The transcript contradicts itself, and that had to be fixed too

Section 12 of a **passing** run printed:

```
LATE DETECTION: surrogate pid 10460 ... started inside the observation window: NO
Criterion 3 stays UNMEASURED, not refuted.
```

Criterion 3 had just captured pid 10460 and passed on it. The note asserted a conclusion it never
checked — **inside the document that is the evidence.** Same defect as every other one in this gate:
text stating more than was measured.

Fixed the same way as the others: `Resolve-E2LateDetectionNote` is a decision with inputs, and every
branch has a control.

| Cleanup finds | Verdict |
|---|---|
| the process criterion 3 captured | **CONFIRMS** — corroborates the PASS |
| a different surrogate | **OTHER** — both held the stub |
| nothing captured, started outside the window | **MISTIMED** — UNMEASURED, not refuted |
| nothing captured, **started inside** the window | **MISSED** — *a harness defect*, not mistiming |
| start time unreadable | **UNKNOWN** |

The MISSED branch matters: a process that started inside the window and was not seen is the polling
failing, and must not be excused as bad timing.

### 18.4 Annotation on the official transcript

`results\e2-windows-20260831-104029.log` is the E2 PASS evidence and is **not** regenerated. One
line in its section 12 is wrong — *"Criterion 3 stays UNMEASURED, not refuted"* — for the reason in
§18.3. **The verdict is unaffected**: criterion 3's PASS came from a live capture whose PID the stub
log independently confirms. A capture that happened is a capture that happened; that a different
code path could have missed it does not unmake it.

No re-run is required to close E2. A confirming run under the fixed harness would be optional, and
would cost another machine-wide certificate trust cycle.

### 18.5 Controls

Seven new. The two that name this run's defects:

| Control | Expect |
|---|---|
| a confirming late detection never says UNMEASURED | asserted on the string |
| a process that started inside the window is a **harness defect**, not mistiming | asserted on the string |

Local suite: **71 non-Windows / 70 Windows**, 0 failures.

### 18.6 Status

| | |
|---|---|
| Gate 1 | **PASS** |
| E1 | **PASS x2**, hash-verified |
| **E2** | **PASS — 3/3, measured** |
| **Gate 2** | **CLOSED** |
| **B1** | **PROCEED, no longer conditional** |
| Proven | the packaged `com:SurrogateServer` activation path: Explorer draws the verb, the CLR and the bridge land in `dllhost.exe`, `explorer.exe` stays clean, the surrogate runs as the standard user, registration and activation unelevated |
| Not proven | the per-machine install flow (stage + provision), plan section L — Phase 9 |
| section L.13 | unaffected; still a product decision |
| Product code | untouched — `src/`, `tests/`, `scripts/`, ADR-0006, `StubCommand.cpp`, the manifest |
| `IExplorerCommand` | **NOT STARTED** — awaiting your go-ahead |
