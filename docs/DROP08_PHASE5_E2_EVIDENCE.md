# DROP 08 Phase 5 — GATE 2 / E2 — FROZEN EVIDENCE

**Frozen 2026-08-29 by direction. E2 is BLOCKED BY ENVIRONMENT. It is not a pass and not a failure.**

No further E2 execution on the machine this evidence was gathered on.

---

## 1. Frozen evidence ledger

| | Item | Result | Evidence |
|---|---|---|---|
| 1 | **Gate 1** (G.1 latency/safety) | **PASS** | `DROP08_PHASE5_G1_REPORT.md` §6. Windows 11 build 26200. Harness SHA-256 matched the audited tree. |
| 2 | **E1** — classic out-of-process COM surrogate isolation | **PASS ×2** | 4/4 criteria, twice, different pids each time. `noba_e_probe.cpp` `87110E31…`, `e_client.cpp` `3D4CF063…` — both hash-verified against this tree. |
| 3 | **E2 pre-flight** | **PASS** | 30 requirements, 0 failures, attempt 4. Local control suite 32/32. |
| 4 | **E2 packaging** | **PASS** | `makeappx pack /nv /o` — "Package creation succeeded." |
| 5 | **E2 signing** | **PASS** | `signtool sign /fd SHA256` — "Successfully signed". Certificate trusted in `LocalMachine\TrustedPeople`, verified by count before deployment was attempted. |
| 6 | **E2 deployment** | **BLOCKED — `0x80073CFF`** | *"To install this application you need either a Windows developer license or a sideloading-enabled system."* Confirmed in `AppXDeploymentServer/Operational`. |
| 6b | **E2 precondition gate** | **BLOCKED BY POLICY, ×3** | `run-e2.ps1 -PreconditionsOnly`, three runs, byte-identical transcripts. The gate refused before creating a certificate, package or registration. Policy `AllowAllTrustedApps=0` **overrides** `AppModelUnlock` `1`/`1`. See §3.1. |
| 7 | **E2 Explorer activation** | **NEVER REACHED** | The package was never registered, so no menu was drawn and no surrogate was activated. |
| 8 | **Cleanup** | **PASS, zero residue** | "no residue: no package, no certificate in any store, no generated files" — verified across all three certificate stores, machine and user. |

**Transcripts, all preserved in `harness/phase5-e/results/`:**

| File | What it evidences |
|---|---|
| `e1-windows-20260828-230410.log` | E1 PASS, run 1 |
| `e1-windows-20260828-230422.log` | E1 PASS, run 2 |
| `e2-windows-20260829-190313.log` | E2 reaching deployment and being refused `0x80073CFF` |
| `e2-windows-20260829-202106.log` | precondition gate, BLOCKED BY POLICY |
| `e2-windows-20260829-202142.log` | precondition gate, BLOCKED BY POLICY |
| `e2-windows-20260829-202346.log` | precondition gate, BLOCKED BY POLICY |

The three precondition transcripts are **2 910 bytes each and identical apart from timestamps**. That
repetition is itself the evidence: the blocker is a deterministic property of the machine's
configuration, not an intermittent failure, and this development machine cannot close E2.

---

## 2. E2 status — and what it is not

# E2: OPEN — BLOCKED BY ENVIRONMENT

**Not a PASS.** None of the three acceptance criteria was observed.
**Not a FAIL.** None of them was refuted either. Deployment stopped before Explorer could activate
anything, so nothing about the adapter, the surrogate or the isolation model was demonstrated in
either direction.

**The acceptance criteria are unchanged and are not weakened:**

| | Criterion | Status |
|---|---|---|
| 1 | the stub verb appears in Explorer's **modern** menu | not observed |
| 2 | `explorer.exe` loads none of `coreclr` / `hostfxr` / the stub | not observed |
| 3 | a `dllhost.exe` does, running as the **standard user**, no UAC | not observed |

Gate 2 remains **OPEN**. E1 passing does not close it: E1 exercised the classic `HKCU` activation
path, and only E2 exercises the packaged COM catalogue that ADR-0006 depends on.

---

## 3. Precondition report — the blocker, in full

| | |
|---|---|
| **Effective state** | **BLOCKED BY POLICY** |
| **Policy source** | Group Policy — `HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx\AllowAllTrustedApps = 0` |
| **Effective value** | `0` — an explicit block. It overrides the `AppModelUnlock` setting. |
| **User-configurable?** | **No, not by the developer alone.** A value under `…\Policies\…` is written by Group Policy, MDM, or an OEM/organisation image. The Settings toggle is typically greyed out while it is present. |
| **Harness action** | **REFUSE.** The gate stops before creating a certificate, a package or a registration. Exit code **2** (BLOCKED), never 1 (FAIL). |
| **Failure code if it ran anyway** | `0x80073CFF` |
| **Does the harness change it?** | **Never.** Not the policy key, not `AppModelUnlock`, not Developer Mode. |

The second key, for completeness — the documented *setting*, which the policy overrides:

```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock
    AllowAllTrustedApps                 sideloading
    AllowDevelopmentWithoutDevLicense   Developer Mode
```

### 3.1 The precedence is now OBSERVED, not assumed — confirmed three times

The machine turned out to be a natural experiment, and it settles by measurement what Microsoft does
not document. Its two keys **disagree**:

```
HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx
    AllowAllTrustedApps                = 0        <- deny
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock
    AllowAllTrustedApps                = 1        <- allow
    AllowDevelopmentWithoutDevLicense  = 1        <- Developer Mode ON
```

**Sideloading is switched on at the setting level, Developer Mode is on, and deployment is still
refused.** The Group Policy value wins. Observed three times — `e2-windows-20260829-202106.log`,
`-202142.log`, `-202346.log`, each 2 910 bytes, byte-identical apart from timestamps.

**This is measured, not documented.** Microsoft's Developer Mode page (S10) describes
`AppModelUnlock` and does not describe the Policies override's precedence. The status therefore moves
from *AMBIGUOUS* to **OBSERVED AND REPRODUCIBLE** — a stronger evidence class than an assumption, and
a weaker one than a documented guarantee. It is recorded as such rather than promoted to a rule.

**The harness predicted this exact configuration before the machine reported it.** One of the seven
synthetic branch controls in `test-e2-preflight.ps1` is literally
`policy 0 blocks, even with Developer Mode on; P=0, U=1, D=1` — written to test a branch that was
unreachable on any machine to hand. The machine then produced `P=0, U=1, D=1`, and the decision
function returned `BLOCKED BY POLICY`, refusing before creating a certificate, a package or a
registration. Synthetic control and real machine agree.

**Consequence, and it is not only about this machine:** *"Developer Mode is on"* is **not** a
sufficient test for whether a package can be registered. Anything that checks only `AppModelUnlock`
— including a future NOBA installer — will conclude the wrong thing on exactly this class of machine.
Carried into plan §L.13.

Reproduce the report at any time, on any machine, without side effects:

```powershell
.\harness\phase5-e\run-e2.ps1 -PreconditionsOnly
```

Reads two registry values and exits. Creates nothing. Exit **0** = E2 can run here; **2** = blocked.

---

## 4. Permanent requirement — Phase 5 and Phase 9

Recorded in `DROP08_PHASE5_PLAN.md` **§L.13**, and repeated here because it outlives this harness.

> **NOBA must detect an enterprise AppX/sideloading block, explain it clearly, continue the rest of
> the installation where possible, and NEVER modify the policy.**

| | Requirement |
|---|---|
| 1 | The installer **detects** the sideloading policy before attempting to register the sparse package |
| 2 | When blocked, it says so **in those words** — not an HRESULT — and names both keys and their values |
| 3 | The rest of the install **still succeeds**: the app, the CLI, `PATH`, file associations. Only the modern context menu is unavailable |
| 4 | On such a machine the **Windows 10 classic fallback** (surface B, ADR-0006) becomes the only context-menu surface, and `SHELL-L8`'s multi-selection limit applies there and must be **stated, not hidden** |
| 5 | The installer **never** changes the policy, `AppModelUnlock`, or Developer Mode |

**ADR-0006 is not amended.** This is a precondition the installer must handle, not a different
packaging decision.

---

## 5. Resuming E2 on a permissive machine

The harness is prepared to resume **without repeating any of the four earlier defects**. Nothing about
the E2 procedure needs to be rediscovered.

| Earlier defect | Now prevented by |
|---|---|
| `desktop5` prefixes where the schema needs `desktop4` | pre-flight M17/M18/M21 |
| certificate in the user store (`0x800B0109`) | `LocalMachine\TrustedPeople` + elevated helper + **trust verified before deploying** |
| `AllowExternalContent` element missing (`0x80073D2E`) | pre-flight M06 |
| cleanup aborting and leaving machine-wide trust | guarded sequence ordered by consequence, `X509Store` throughout, UNKNOWN ≠ CLEAN |
| sideloading blocked (`0x80073CFF`) | pre-flight **S01**, and `-PreconditionsOnly` |

**Order on a new machine:**

```powershell
.\harness\phase5-e\run-e2.ps1 -PreconditionsOnly     # exit 0 = can run, 2 = blocked
.\harness\phase5-e\test-e2-preflight.ps1             # must report PASS (32 local checks)
.\harness\phase5-g1\run-g1.ps1                       # builds the bridge E2 needs
.\harness\phase5-e\run-e1.ps1                        # re-establish E1 on that machine
.\harness\phase5-e\run-e2.ps1                        # the gate
```

`-CleanOnly` removes all residue at any point. The harness detects and clears residue from an aborted
run at startup, so stale state cannot make a later run look successful.

---

## 6. What a clean Windows test environment needs

E2 cannot be closed on a machine whose organisation forbids sideloading, and that policy should not be
worked around. A separate environment is required. **Not asking for any change to the machine this
evidence came from.**

### Mandatory

| | Requirement | Why |
|---|---|---|
| 1 | **Windows 11, build ≥ 22000** | the modern context menu does not exist before it. Build 26200 is what Gate 1 and E1 ran on. |
| 2 | **Sideloading permitted** — no `…\Policies\Microsoft\Windows\Appx\AllowAllTrustedApps = 0`, and Developer Mode or `AllowAllTrustedApps = 1` available | the whole blocker |
| 3 | **Not domain-joined / not MDM-managed**, or managed by someone who can grant the above | a policy re-applies itself at the next refresh even if changed locally |
| 4 | **Local administrator available** | one elevated step, to trust the DEV-ONLY certificate |
| 5 | **An interactive desktop session** | criterion 1 needs a human to right-click and look at the menu |
| 6 | .NET 10 SDK · VS Build Tools with the **Desktop development with C++** workload · Windows SDK (`makeappx`, `signtool`) | build and package |
| 7 | Windows PowerShell 5.1 | what the scripts target; `pwsh` is not required |

### Strongly preferred

| | Requirement | Why |
|---|---|---|
| 8 | **Disposable** — a VM with a snapshot, or a scratch machine | a DEV-ONLY certificate is trusted machine-wide for the duration, and a snapshot makes residue moot |
| 9 | **Defender real-time protection ON** | Gate 1's figures were taken with it **off**, so every cold number there is optimistic. E2 does not measure latency, but a realistic machine is worth having |
| 10 | No other NOBA package or `CN=NOBA-DEV-ONLY` certificate present | the harness detects and clears residue, but a clean start needs no interpretation |

### Options that fit

- **A local Hyper-V / VMware VM** running Windows 11 Pro, not joined to the domain, with a
  pre-E2 snapshot. Cheapest and fully disposable.
- **A Windows 11 Dev Box / cloud VM.** Same properties; check the image is not policy-managed.
- **A second physical machine** outside the managed estate.

### What will *not* work

- This machine, unless the organisation changes the policy — which is their decision, not a
  workaround.
- Any machine where the policy is re-applied by Group Policy refresh or MDM.
- A CA-issued certificate instead of the DEV-ONLY one. **It does not help.** `0x80073CFF` is about
  the sideloading policy, not about who signed the package. A trusted signature still needs a
  developer license or `AllowAllTrustedApps`.
- Running the harness elevated. It refuses, because registering as administrator registers for the
  administrator and makes criterion 3 meaningless.

---

## 7. Status

| | |
|---|---|
| Gate 1 | **PASS** |
| Gate 2 / E1 | **PASS ×2**, hash-verified |
| Gate 2 / E2 | **OPEN — BLOCKED BY ENVIRONMENT** (`0x80073CFF`, explicit policy block; precondition gate confirmed ×3) |
| Gate 2 overall | **OPEN** |
| Acceptance criteria | **unchanged, not weakened** |
| Recommendation | **PROCEED B1, still conditional on E2** |
| Product code | untouched — `src/`, `tests/`, `scripts/`, ADR-0006, `ShellMenuBuilder`, `ShellInvocations`, `ShellLauncher`, `BridgeExports.cs`, `g1host.cpp` |
