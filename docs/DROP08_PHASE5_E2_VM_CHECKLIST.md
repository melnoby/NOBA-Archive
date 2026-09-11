# DROP 08 Phase 5 — E2 VM EXECUTION CHECKLIST

**The only remaining action for Gate 2. One evidence-producing run.**

Every command, path and exit code below was read out of the current tree, not recalled. The ordering
is designed so the cheapest thing that can falsify the plan runs first.

---

## 0. Standing discipline adopted 2026-08-29

| | Rule |
|---|---|
| **B1** | **PROCEED.** Not reopened unless new evidence contradicts E1's established facts. |
| **E2** | OPEN, environment-blocked. Not run again on the current machine. No change to its Developer Mode, Group Policy, AppX policy, MDM state or security configuration. |
| **E2 acceptance** | Unchanged. A deployment failure before Explorer activation is neither a pass nor an implementation failure. **Only actual Explorer activation evidence closes E2.** |
| **§L.13** | A **product decision**, not a Phase 9 deferral, and never solved technically. NOBA never modifies enterprise AppX policy. |
| **Harness economics** | *Build the smallest test that can falsify the current architectural hypothesis.* No new harness component without naming the unknown it resolves, the decision its result can change, what it replaces, and whether the fact is obtainable more cheaply. "More coverage" alone is not a reason at this phase. |
| **Engineering mode** | Inspect the tree, find the smallest unresolved risk, design one decisive test, run it, change only what the evidence justifies, document after the decision. |
| **Stop conditions** | Environment blocker → stop before building artefacts. Architectural contradiction → stop before coding around it. Ambiguous result → add the minimum diagnostic that makes the next result decisive; never interpret optimistically. |

**Nothing in this checklist adds a harness component.** It is a document. `-PreconditionsOnly`
already exists.

---

## 1. VM specification

| | Requirement | Why | Hard? |
|---|---|---|---|
| OS | **Windows 11 Pro, build ≥ 22000** | the modern context menu does not exist earlier. 26200 is what Gate 1 and E1 ran on. | **hard** |
| Domain | **not domain-joined** | a Group Policy block re-applies at the next refresh even if changed locally | **hard** |
| MDM | **not enrolled** | same reason | **hard** |
| Sideloading | **no policy override.** `…\Policies\Microsoft\Windows\Appx\AllowAllTrustedApps` must be **absent or 1** | **Developer Mode being ON is NOT sufficient.** The development machine has Developer Mode ON *and* `AppModelUnlock\AllowAllTrustedApps = 1`, and is still refused, because the policy value overrides both. Step 1 tests the real condition; this row states it so a VM is not built against the wrong criterion. | **hard** |
| Account | **local administrator available** | exactly one elevated step: trusting the DEV-ONLY certificate | **hard** |
| Session | **interactive desktop**, Explorer running | criterion 1 needs a human to right-click and look | **hard** |
| Defender | **real-time protection ON** | Gate 1's figures were taken with it off, so they are optimistic. E2 measures no latency, but a realistic machine is worth having | preferred |
| Disposable | **snapshot capability** | a DEV-ONLY certificate is trusted machine-wide for the duration of the run | preferred |
| CPU/RAM | 4 vCPU / 8 GB is ample | build + one MSIX registration | preferred |
| Network | needed only to download the toolchain | the build itself is offline — see §2 | — |

Hyper-V, VMware, VirtualBox or a cloud Windows 11 VM all qualify. Check any cloud image is **not**
policy-managed before relying on it.

---

## 2. Toolchain to install on the VM

| | Component | Needed by |
|---|---|---|
| 1 | **.NET 10 SDK** (`dotnet` on PATH) | `run-g1.ps1` builds the bridge |
| 2 | **Visual Studio Build Tools** with **Desktop development with C++** | `cl.exe`, located via `vswhere` |
| 3 | **Windows SDK** (`makeappx.exe`, `signtool.exe` under `…\Windows Kits\10\bin\10.*\x64`) | `run-e2.ps1` packs and signs |
| 4 | Windows PowerShell **5.1** | ships with Windows. **`pwsh` is not required** and is not installed by default |

**The build needs no NuGet network access.** `Directory.Build.props` swaps the
`Microsoft.Extensions.*` packages for a local shim when `NobaOfflineBuild=true`, and every harness
script sets it. The bridge has **no `PackageReference`** — project references only.

---

## 3. Transfer

Copy the repository **to the VM**. The commands below use `C:\NOBA-Archive` **on the VM**.

> Your development machine's copy stays at `C:\Users\m-m-e\Desktop\NOBA-Archive`. It is not used
> for E2, is not modified, and nothing in this checklist runs on it.

- **22 MB** excluding `bin`, `obj`, `.git`, `artifacts`.
- **Exclude `bin` and `obj`.** Stale output carrying the other machine's absolute paths is the kind
  of thing that produces a confusing first failure.
- Nothing else is needed. No committed native binary, no external payload.

**Before transferring, run `test-e2-preflight.ps1` on the authoring machine and check section 0.**
The 5.1 parseability gate is a property of the files, so it is cheapest to check where they are
edited — and a script that will not parse fails at step 1, before the suite that would have
explained it ever runs. SHA-256 verification proves a faithful copy; it does not prove the file
parses on the target, and on 2026-08-29 a byte-identical copy did not.

---

## 4. Run order

Each step is a gate. **Stop at the first one that fails** — that is stop-condition discipline, not
caution.

### Step 1 — falsify the blocker first, before installing anything heavy

```powershell
cd C:\NOBA-Archive          # on the VM
.\harness\phase5-e\run-e2.ps1 -PreconditionsOnly
```

Verified against the script: this exits **before** any `dotnet`, `vswhere` or SDK lookup. It needs
only Windows 11, PowerShell 5.1 and the harness folder. It reads two registry values, creates
nothing, changes nothing.

| Exit | Meaning |
|---|---|
| **0** | sideloading permitted — E2 can run here. Continue. |
| **2** | **BLOCKED BY ENVIRONMENT.** Stop. The VM is unsuitable; nothing else is worth installing. |
| 1 | not Windows 11 ≥ 22000, or the shell is elevated |

Run this **before** the ~10 GB toolchain install. It is the cheapest thing that can invalidate the
whole plan — and on the development machine it has now returned **2** three times, byte-identical,
which is what makes a separate VM necessary rather than optional.

### Step 2 — snapshot **S1**, clean baseline

After the toolchain is installed and step 1 returns 0. Reverting here skips a reinstall.

### Step 3 — local control suite (no Windows-specific state)

```powershell
.\harness\phase5-e\test-e2-preflight.ps1
```

Must print **`LOCAL CONTROL SUITE: PASS — 70 checks, 0 failures (windows)`**. The count is platform-dependent by one — section 4's *"no registry → FAIL, not PASS"* control can only run where there is no registry — so a non-Windows machine prints **71**. The suite prints which platform it counted for, so a correct Windows run is not mistaken for an incomplete transfer. Exit 0. Creates nothing outside
`%TEMP%`.

**Section 0 is the Windows PowerShell 5.1 parseability gate**, and it runs before everything else,
including before the pre-flight module is imported. On 2026-08-29 `run-e2.ps1` arrived on the VM
byte-identical to the source tree (SHA-256 verified) and 5.1 still refused to parse it: the file was
UTF-8 **without a BOM**, 5.1 decodes such a file as the machine's ANSI codepage, and in CP1252 the
third byte of `→` is `0x92` — U+2019, which PowerShell accepts as a string delimiter. See
`DROP08_PHASE5_DELTA.md` §13.

The gate asserts, for every `*.ps1` and `*.psm1` under `harness\`:

| | |
|---|---|
| `H01` | pure ASCII, or a UTF-8 BOM — decodes identically under every codepage |
| `H02` | the raw bytes, decoded as 5.1 decodes them (cp1252, cp1251, cp932), parse |

If section 0 fails, **stop and fix the files**. Nothing downstream is interpretable, and the failure
will otherwise resurface at step 7 as a brace error pointing at the wrong line.

### Step 4 — build the bridge, and re-establish Gate 1 on this machine

```powershell
.\harness\phase5-g1\run-g1.ps1
```

Produces `harness\phase5-g1\bridge\bin\Debug\net10.0\` — **E1 and E2 both require it**. Also
re-measures Gate 1 on the VM; with Defender **on**, expect the cold figures to be worse than the
26200 run, which is the point. Registers nothing.

### Step 5 — re-establish E1 on this machine

```powershell
.\harness\phase5-e\run-e1.ps1
```

Must print **`E1 PASS - all four criteria met`** and **`unregistered - nothing NOBA-owned survives
under HKCU`**. `HKCU` only, no elevation, no package.

If E1 fails here but passed on the other machine, **stop**: something about the VM differs and E2's
result would not be interpretable.

### Step 6 — snapshot **S2**, immediately before E2

The revert point for the single evidence run.

### Step 7 — E2. The evidence run.

```powershell
.\harness\phase5-e\run-e2.ps1
```

**Unelevated.** The script refuses otherwise — registering as administrator registers for the
administrator and makes criterion 3 meaningless.

What happens, in order: residue scan → build stub → stage → **pre-flight (32 requirements, `H01`
first, then `S01`)** → pack → certificate → **UAC #1, trust** → verify trust → sign → deploy →
Explorer restart → **you right-click a file** → three criteria → stub log → **UAC #2, cleanup**.

**Section 9's window is driven by you, not by a clock.** Right-click a file, then **press any key in
the console** — the harness keeps sampling for a further 20 s, because activation is not instant. If
the window expires without a keypress it asks whether you right-clicked while it was open, and an
unconfirmed empty window is reported **UNMEASURED, not failed** (`DROP08_PHASE5_DELTA.md` §17).

**Section 9 watches for the surrogate instead of sampling once.** It polls for up to
`-ObserveSeconds` (default 180) and captures the surrogate the moment it appears — a COM surrogate
exits when idle, and the first run lost that race (`DROP08_PHASE5_DELTA.md` §15). Right-click as many
times as you like during the window; press any key to stop early.

| Prompt | Answer |
|---|---|
| `Press ENTER once you have right-clicked a file` | right-click any file in Explorer first. **You do not need to click the menu item** — drawing the menu is enough. |
| `Did the entry appear in the MODERN menu…? [y/n/s]` | `y` = yes, in the short menu above *Show more options*. `s` = it appeared **only** under *Show more options* — that is the classic surface, not the modern one, and is **not** a pass. `n` = not seen. |

Look for: **`NOBA Phase 5 isolation probe (harness)`**

| Exit | Meaning |
|---|---|
| **0** | **E2 PASS** — 3/3 criteria. Gate 2 closes. |
| **1** | E2 FAIL — a criterion was refuted. Real evidence; capture it. |
| **2** | BLOCKED BY ENVIRONMENT — should not happen after step 1 returned 0. |

A criterion the harness could not measure now prints **`[UNKNOWN]`**, and section 13 prints
**`E2 NOT CLOSED — n UNMEASURED`**. UNKNOWN is not a pass and is **not a refutation**: it means the
harness could not look. Do not record it as evidence either way — fix the measurement and run again.

### Step 8 — capture, then discard

Collect **`harness\phase5-e\results\`** and **`harness\phase5-g1\results\`** — every run writes a
timestamped transcript. Send those; they are the evidence.

Then revert to **S1** or discard the VM. The script's own cleanup should already report
`no residue: no package, no certificate in any store, no generated files`; the snapshot makes it
moot.

---

## 5. Abort conditions

| If | Then |
|---|---|
| step 1 exits **2** | the VM is unsuitable. Do not install the toolchain. Do not change any policy. |
| step 3 is not 70/70 on Windows (71 elsewhere) | the tree was transferred incompletely. Re-copy; do not proceed. |
| step 3 section 0 fails, or any script prints a `Missing closing '}'` / `string is missing the terminator` error that the file plainly does not have | an encoding defect, not a syntax defect. The file is UTF-8 without a BOM and 5.1 is decoding it as the ANSI codepage. Do **not** hand-patch it on the VM; fix it at the source and re-transfer. `DROP08_PHASE5_DELTA.md` §13. |
| step 4 or 5 fails | the VM differs from the machine E1 passed on. Stop — E2 would not be interpretable. |
| pre-flight in step 7 fails | **nothing was created**, so nothing needs cleaning. Send the pre-flight output; the failing requirement is named. |
| deployment fails | the script prints the `AppXDeploymentServer/Operational` errors. Send them; the HRESULT alone is not the diagnosis. |
| cleanup reports residue | it names the store and prints the elevated `X509Store` removal. Reverting to S2 also clears it. |
| anything is ambiguous | **do not interpret it optimistically.** Send the transcript; the next step is a diagnostic, not a guess. |

---

## 6. Not to be done

- **No policy change** on any machine — Developer Mode, Group Policy, `AppModelUnlock`, MDM.
- **No elevated run** of `run-e2.ps1`.
- **No second E2 attempt on the same VM state** without reverting to S2 — the residue scan handles
  it, but a clean state needs no interpretation.
- **No `IExplorerCommand` work** until E2 passes.
- **No product code changes.** `src/`, `tests/`, `scripts/`, ADR-0006, `ShellMenuBuilder`,
  `ShellInvocations`, `ShellLauncher`, `BridgeExports.cs`, `g1host.cpp` stay untouched.
- **A CA-issued certificate does not substitute for a permissive VM.** `0x80073CFF` is about the
  sideloading policy, not about who signed the package.

---

## 7. What closing E2 gives, and what it does not

**Closes:** the packaged `com:SurrogateServer` activation path — Explorer draws the verb, the CLR and
the bridge land in `dllhost.exe`, `explorer.exe` stays clean, the surrogate runs as the standard
user. Gate 2 closes, and B1 stops being conditional.

**Does not close:** the per-machine install flow — `Add-AppxPackage -Stage` + `Add-AppxProvisionedPackage`,
elevated (plan §L.1, §L.11). That is Phase 9 and a separate question.

**Also unaffected:** the §L.13 product decision. It stands whatever E2 returns, because it is about
machines where the modern surface cannot be registered at all.

---

## 8. State at freeze

| | |
|---|---|
| Gate 1 | **PASS** |
| E1 | **PASS ×2**, hash-verified |
| **E2** | **PASS — 3/3, measured 2026-08-31 10:40 on `NOBA-Test`** (DELTA §18) |
| **Gate 2** | **CLOSED** |
| Harness 5.1 parseability | **PASS** — 5 scripts, 21 checks, 3 codepages. `scripts/` reported, not modified (DELTA §13.8) |
| E2 pre-flight | **PASS** |
| E2 (superseded) | was OPEN / ENVIRONMENT BLOCKED on the development machine; closed on a permissive VM |
| B1 | **PROCEED — no longer conditional** |
| `IExplorerCommand` | **NOT STARTED** |
| Phase 9 | **NOT STARTED** |
| DROP 09 | **NOT STARTED** |
