# DROP 08 Phase 5 — E2 SPARSE-PACKAGE AUDIT

**2026-08-29. Prevention pass, not a bug-fix pass. No Windows execution during this audit.**

Three E2 attempts failed on three separate defects of mine, plus one that left a certificate trusted
machine-wide. All four were reasoning where documentation should have been read, and each check added
afterwards covered what I had already thought about rather than what the operation required. This
audit inverts that: **every assertion is derived from an operation the harness performs**, and every
one has a negative control that has been seen to fail.

---

## A. REQUIREMENTS MATRIX

Status key: **VERIFIED** — confirmed against an authoritative source and checked in code.
**AMBIGUOUS** — documentation does not settle it; the safe option is taken and the reasoning recorded.

| # | REQUIREMENT | SOURCE | EXACT MECHANISM | CURRENT IMPL | PRE-FLIGHT | NEGATIVE CONTROL | STATUS |
|---|---|---|---|---|---|---|---|
| 1 | Package has `Identity` with Name/Publisher/Version | S1 | foundation schema | present | M03 | — | VERIFIED |
| 2 | `Publisher` equals the signing certificate subject **exactly** | S1, S5 | mismatch → `0x8007000B`, Event ID 150 | `CN=NOBA-DEV-ONLY` | M04 | `publisher-mismatch` | VERIFIED |
| 3 | `ProcessorArchitecture` neutral or absent | S1, S2 | sparse identity package carries no binaries | `neutral` | M05 | `arch-x64` | VERIFIED |
| 4 | `uap10:AllowExternalContent` = `true`, element in `Properties` | S1 | absent → `0x80073D2E` | present | M06 | `no-allow-external`, `allow-external-false` | VERIFIED |
| 5 | No `SplashScreen` | S3 | documented registration failure for this shape | absent | M07 | `splashscreen` | VERIFIED |
| 6 | `TargetDeviceFamily` Windows.Desktop, MinVersion ≥ 10.0.19041.0 | S1 | AllowExternalContent floor | `10.0.19041.0` | M08 | `min-version-low` | VERIFIED |
| 7 | `rescap:Capability runFullTrust` | S1, S2 | desktop package | present | M09-runFullTrust | `no-runfulltrust` | VERIFIED |
| 8 | `rescap:Capability unvirtualizedResources` | S1, S3 | exempts from registry/file virtualisation | present | M09-unvirtualizedResources | `no-unvirtualized` | **AMBIGUOUS** — see §G-1 |
| 9 | `Application/@Executable` set and present in the external location | S1 | package references it by name | `noba_e2_host.exe` | M10, M24 | external-location control | VERIFIED |
| 10 | `uap10:TrustLevel="mediumIL"` | S1 | Win32 compatibility | set | M11-TrustLevel | `trustlevel-wrong` | VERIFIED |
| 11 | `uap10:RuntimeBehavior="win32App"` | S1 | Win32 compatibility | set | M11-RuntimeBehavior | `runtimebehavior-wrong` | VERIFIED |
| 12 | `AppListEntry="none"` | S1 | identity package must not appear as an installed app | set | M12 | `applistentry` | VERIFIED |
| 13 | `com:Extension windows.comServer` under **Application**/Extensions | S2 | Microsoft ContextMenuSample | correct | M13 | — | VERIFIED |
| 14 | `com:SurrogateServer` — out-of-process host | S2, S6 | the element under test; an in-proc server loads into `explorer.exe` | present | M14 | `no-surrogate` | VERIFIED |
| 15 | `com:Class/@Id` = the CLSID the stub implements | S2 | COM activation | matches | M15 | `clsid-mismatch` | VERIFIED |
| 16 | `com:Class/@ThreadingModel` set | S2 | `STA` in the sample | `STA` | M16 | — | VERIFIED |
| 17 | `com:Class/@Path` present in the external location | S1 | resolved via `-ExternalLocation` | `noba_e2_stub.dll` | M24 | external-location control | VERIFIED |
| 18 | Context menu = **desktop4**`:Extension` + **desktop4**`:FileExplorerContextMenus` | S4, S2 | `FileExplorerContextMenus` shipped in desktop4 | corrected | M17, M18 | `desktop5-extension` | VERIFIED |
| 19 | **desktop5**`:ItemType` / **desktop5**`:Verb` | S7, S2 | `ItemType` added in desktop5 | correct | M19, M20 | `verb-clsid-mismatch` | VERIFIED |
| 20 | Every `Verb/@Clsid` = the stub CLSID | S2 | activation target | matches | M20 | `verb-clsid-mismatch` | VERIFIED |
| 21 | No wrong-prefix combination | S4, S7 | `makeappx` C00CE014 | none | M21 | `desktop5-extension` | VERIFIED |
| 22 | Every used prefix declared **and** in `IgnorableNamespaces` | S1, S2 | unknown-namespace handling | correct | M02a, M02b | `not-ignorable` | VERIFIED |
| 23 | No `RunAs` attribute anywhere | plan §L.4 | would break the elevation boundary | none | M22 | `runas` | VERIFIED |
| 24 | Package assets exist in the package root | S1 | `makeappx` payload | 3 logos | M23 | `missing-asset` | VERIFIED |
| 25 | `makeappx pack /nv` | S1, S3 | sparse payload fails standard validation | `/nv /o` | — | — | VERIFIED |
| 26 | `signtool sign /fd SHA256` with the dev certificate | S1 | package must be signed | by thumbprint | — | — | VERIFIED |
| 27 | Certificate in **`LocalMachine\TrustedPeople`** | S5, S8 | user store → `0x800B0109` | elevated helper | verified before deploy | — | VERIFIED |
| 28 | Trusting the certificate requires Administrator | S8 | machine store | elevated helper ×1 | exit code + count check | — | VERIFIED |
| 29 | `Add-AppxPackage -Path … -ExternalLocation …` per-user, **unelevated** | S1 | per-user registration | as written | script refuses to run elevated | — | VERIFIED |
| 30 | **Sideloading must be permitted on the machine** | S5, S10, S11 | blocked → `0x80073CFF` | **GATE** in pre-flight, both keys | **S01** | no-registry → FAIL | **CORRECTED — see §G-6** |
| 31 | Explorer must be restarted to see a new handler | practice | menu handler cache | restarted | — | — | VERIFIED |
| 32 | Surrogate runs as the invoking user | S6, plan §L.4 | no `RunAs`, no elevation moniker | none present | M22 + criterion 3 | — | VERIFIED |
| 33 | Deprovision ≠ uninstall; per-user registration is separate | S9 | applies at next sign-in | per-user only here | — | — | VERIFIED (N/A to this gate) |
| 34 | `Remove-AppxPackage` releases the external-location registration | S1 | no separate lifetime | cleanup step 2 | residue check | — | VERIFIED |
| 35 | Certificate lifecycle via `X509Store`, not `Remove-Item` | observed ×2 | provider fails "Access is denied" | module-wide | control suite §4 | unreadable-store control | VERIFIED |
| 36 | An unreadable store is UNKNOWN, never CLEAN | this project's rule | absence of evidence | `-1` sentinel | control suite §4 | unreadable-store control | VERIFIED |
| 37 | The harness's own scripts must parse under **Windows PowerShell 5.1** | observed on the VM 2026-08-29 | 5.1 decodes a BOM-less file as the ANSI codepage; `0x91`–`0x94` are PowerShell quote characters there | all 5 harness scripts pure ASCII | H01 (run-e2), section 0 (control suite) | rebuilt `Show-DeploymentLog` hazard, no BOM | VERIFIED |
| 38 | The 5.1 parse check must be **pinned to a codepage**, not inherited from the host | the defect itself | PowerShell 7 decodes BOM-less as UTF-8, so a host-inherited check cannot see it | `ConvertFrom-E2ScriptBytes` + `Parser::ParseInput`, cp1252/1251/932 | H02 | even-arrow file: parses cleanly, byte rule still rejects it | VERIFIED |
| 39 | The encoding check must run **before** the pre-flight module is imported | the defect itself | a mis-decoded module throws at `Import-Module` and can never explain why | bootstrap byte guard in `test-e2-preflight.ps1` | bootstrap, exit 1 | `E2Preflight.psm1` itself failed the CP1252 parse with 5 errors | VERIFIED |

**Not applicable, with reasoning:** the `<msix>` element in an application manifest (S1 step 3) gives
an *unpackaged EXE* identity when launched directly. Nothing here launches `noba_e2_host.exe`; the
stub runs inside the packaged surrogate and takes identity from the package. Recorded so its absence
is a decision, not an oversight.

---

## B. AUTHORITATIVE SOURCES

| | Source |
|---|---|
| **S1** | [Grant package identity by packaging with external location — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps) — the canonical sparse manifest, makeappx/signtool/Add-AppxPackage commands, per-user vs per-machine |
| **S2** | [Microsoft ContextMenuSample `Package.appxmanifest`](https://github.com/microsoft/Windows-AppConsult-Samples-DesktopBridge/blob/main/Docs-ContextMenuSample/ContextMenuSample.Package/Package.appxmanifest) — a shipping manifest with com:SurrogateServer + desktop4/desktop5 in Application/Extensions |
| **S3** | [Unpackaged Windows Apps with Identity using a Sparse Package — Nick's .NET Travels](https://nicksnettravels.builttoroam.com/sparse-package/) — `/nv`, no SplashScreen, `unvirtualizedResources` |
| **S4** | [desktop4:FileExplorerContextMenus — Microsoft Learn](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop4-fileexplorercontextmenus) — element hierarchy and child ItemType namespaces |
| **S5** | [MSIX troubleshooting guide — Microsoft Learn](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide) — `0x800B0109`, `0x8007000B`, sideloading policy, deployment event log |
| **S6** | [Packaged DLL for Windows 11 Explorer context menu — xplorer² blog](https://www.zabkat.com/blog/win11-explorer-menu-package.htm) — signing required, IExplorerCommand required |
| **S7** | [desktop5:ItemType — Microsoft Learn](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop5-itemtype) — Verb child, namespace URI |
| **S8** | [Create a certificate for package signing — Microsoft Learn](https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing) — LocalMachine TrustedPeople, admin session |
| **S10** | [Settings for developers — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/get-started/developer-mode-features-and-debugging) — `AppModelUnlock`, `AllowAllTrustedApps`, `AllowDevelopmentWithoutDevLicense`, the App Package Deployment Group Policy path |
| **S11** | [MSIX error 0x80073CFF — AppDeployNews](https://www.appdeploynews.com/packaging-types/msix-errors/msix-error-code-0x80073cff/) — a trusted-signature package still requires a developer license or `AllowAllTrustedApps` |
| **S9** | [Add-AppxProvisionedPackage — Microsoft Learn](https://learn.microsoft.com/en-us/powershell/module/dism/add-appxprovisionedpackage) — provisioning applies at next sign-in |

---

## C. MANIFEST VALIDATION MATRIX

29 checks, all in `E2Preflight.psm1`, all executed against the real manifest and against a
deliberately broken copy.

| ID | Check | Control | Caught as |
|---|---|---|---|
| M01 | root is foundation `Package` | — | — |
| M02a | every used prefix declared | — | — |
| M02b | every used prefix in `IgnorableNamespaces` | `not-ignorable` | M02b |
| M03 | `Identity` present | — | — |
| M04 | Publisher = certificate subject | `publisher-mismatch` | M04 |
| M05 | architecture neutral/absent | `arch-x64` | M05 |
| M06 | `AllowExternalContent` = true | `no-allow-external`, `allow-external-false` | M06 |
| M07 | no `SplashScreen` | `splashscreen` | M07 |
| M08 | TargetDeviceFamily / MinVersion | `min-version-low` | M08 |
| M09 | `runFullTrust`, `unvirtualizedResources` | `no-runfulltrust`, `no-unvirtualized` | M09-* |
| M10 | `Application/@Executable` | — | — |
| M11 | `TrustLevel`, `RuntimeBehavior` | `trustlevel-wrong`, `runtimebehavior-wrong` | M11-* |
| M12 | `AppListEntry="none"` | `applistentry` | M12 |
| M13 | `com:Extension` under Application | — | — |
| M14 | `com:SurrogateServer` present | `no-surrogate` | M14 |
| M15 | `com:Class/@Id` = stub CLSID | `clsid-mismatch` | M15 |
| M16 | `ThreadingModel` set | — | — |
| M17 | `desktop4:Extension` | `desktop5-extension` | M17 |
| M18 | `desktop4:FileExplorerContextMenus` | `desktop5-extension` | M17/M18 |
| M19 | `desktop5:ItemType` | — | — |
| M20 | every `Verb/@Clsid` matches | `verb-clsid-mismatch` | M20 |
| M21 | no wrong-prefix combinations (4 variants) | `desktop5-extension` | M21 |
| M22 | no `RunAs` attribute | `runas` | M22 |
| M23 | package assets exist | `missing-asset` | M23 |
| M24 | external-location payload exists | empty-external-location | M24 |

**Result: 29 checks pass on the real manifest; 18 negative controls each caught by name; 0 misses.**

---

## D. CERTIFICATE / TRUST MATRIX

| Stage | Mechanism | Elevated | Verified how |
|---|---|---|---|
| CREATE | `New-SelfSignedCertificate` → `CurrentUser\My`, code-signing EKU | no | thumbprint reported |
| TRUST | `Import-Certificate` → `LocalMachine\TrustedPeople`, elevated helper | **yes** | helper exit code |
| VERIFY | `Get-E2CertCount` before deploying | no | count > 0, or the run stops |
| SIGN | `signtool /fd SHA256 /sha1 <thumbprint>` | no | exit code |
| DEPLOY | `Add-AppxPackage -ExternalLocation` | no | exit + event log on failure |
| ACTIVATE | Explorer → surrogate | no | criteria 2 and 3 |
| REMOVE PACKAGE | `Remove-AppxPackage` | no | re-query |
| REMOVE TRUST | `X509Store.Remove`, elevated helper, **first** among cert steps | **yes** | re-count |
| VERIFY ZERO RESIDUE | all three stores independently | no | UNKNOWN ≠ CLEAN |

**Rules now enforced in code:** `X509Store` everywhere, never `Cert:` `Remove-Item`; an unreadable
store yields `-1` and is reported as residue; the machine store is cleared before lower-risk stores;
trust is verified *before* deployment so a declined UAC prompt fails by name, not as `0x800B0109`.

---

## E. DEPLOYMENT MATRIX

| Prerequisite | Proven before deploying |
|---|---|
| Windows 11 build ≥ 22000 | precondition |
| process is **not** elevated | precondition, refuses otherwise |
| bridge built | stage step |
| stub + host present in the external location | M24 |
| manifest structurally valid | M01–M23 |
| `AllowExternalContent` = true | M06 |
| Publisher = certificate subject | M04 |
| package packed | step 5 exit code |
| certificate trusted **in the machine store** | count check, run stops otherwise |
| package signed | step 7 exit code |
| SDK tools located | pre-flight |

On failure the script prints the last `AppXDeploymentServer/Operational` errors, so the cause is
named rather than left as an HRESULT.

---

## F. CLEANUP MATRIX

| # | Step | Guarded | Runs even if |
|---|---|---|---|
| 1 | stop E2-owned surrogates (identified by our stub, never by name) | yes | anything |
| 2 | `Remove-AppxPackage` — releases the external-location registration with it | yes | build/pack/sign/deploy failed |
| 3 | **machine** certificate, elevated helper | yes | steps 1–2 failed |
| 4 | user certificate stores, each independent | yes | step 3 failed |
| 5 | verify all three stores | yes | anything |
| 6 | generated files and staged payload | yes | anything |
| 7 | report residue, naming machine-wide trust explicitly | yes | anything |

Reachable from `finally`, from `-CleanOnly`, and from aborted-run recovery at startup.

---

## G. CURRENT GAPS

**G-6 — REQUIREMENT 30 WAS WRONG, AND THE AUDIT'S OWN METHOD CAUGHT NOTHING.**

The first version of this matrix recorded requirement 30 as VERIFIED on the strength of one sentence
in S5 — *"no separate sideloading policy toggle for trusted app package installs"* — and made the
check **informational**. Attempt 4 then printed, in its own section 0:

```
appx policy: AllowAllTrustedApps=0
```

and deployed anyway. It failed with `0x80073CFF`: *"you need either a Windows developer license or a
sideloading-enabled system"*.

**The fact was gathered, printed, and never wired to a decision.** That is the identical defect to a
manifest comment describing an element that was never written — which this audit was created to
prevent, and which it reproduced in its own requirements matrix. The rule "derive assertions from the
operation" was applied to the manifest and not to the machine.

**Two keys, and only one of them is documented:**

| Key | Role | Documented? |
|---|---|---|
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock` | the setting the Settings UI writes: `AllowAllTrustedApps=1` (sideloading), `AllowDevelopmentWithoutDevLicense=1` (Developer Mode) | **yes** — S10 |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx` | the Group Policy override the machine has set to 0 | **not documented** — S10 does not describe it. But **OBSERVED AND REPRODUCIBLE** as of 2026-08-29: the machine has policy `0` while `AppModelUnlock` is `1`/`1` with Developer Mode ON, and deployment is refused. Three identical runs. Precedence measured, not assumed. See `DROP08_PHASE5_E2_EVIDENCE.md` §3.1 |

**Now a gate.** `Test-E2Sideloading` reads both keys and returns FAIL before anything is created, with
the observed values and the remedy. Unset reads as FAIL, never PASS: absence of a permission is not
permission. **The script will not change machine policy** — that is a security decision, and a
Group Policy value may belong to an organisation rather than to the developer.

**G-7 — a product risk this failure exposed, recorded in plan §L.13.** If an enterprise disables
sideloading by policy, **NOBA's sparse package will not register either**, CA-signed or not. The
installer must detect that and say so, rather than failing with an HRESULT. That is a real deployment
constraint on ADR-0006's packaging decision, found by a harness failure rather than by review.

**G-1 — `unvirtualizedResources` necessity is AMBIGUOUS.** The canonical Microsoft example (S1) and
S3 both include it; neither states whether it is required for a package whose only extension is a COM
server. **Decision: include it.** Omitting it is the untested option, and no source recommends
omission. Flagged rather than settled silently.

**G-2 — criterion 1 is human observation.** Whether the entry lands in the modern menu cannot be read
from the process table. Asked as a question with a third answer — *"only under Show more options"* —
so the classic surface cannot be mistaken for the modern one.

**G-3 — the elevated helpers are separate processes.** Their exit code is checked and the effect is
verified afterwards by counting the store, but their output is not captured in the transcript.
Acceptable: the effect is what matters and the effect is measured.

**G-4 — E2 does not test the per-machine install flow.** Stage + provision is Phase 9, out of scope
for this gate, and stated in the script's own output so the result is not over-read.

**G-5 — no negative control for M13/M16/M19.** Structural presence checks whose failure mode is
covered by neighbouring controls. Recorded rather than claimed as complete.

---

## H. CHANGES REQUIRED, AND MADE

| File | Change |
|---|---|
| `harness/phase5-e/E2Preflight.psm1` | **new** — 29 manifest checks plus the `X509Store` certificate helpers |
| `harness/phase5-e/test-e2-preflight.ps1` | **new** — local control suite, 18 negative controls, runs on any platform |
| `harness/phase5-e/package/AppxManifest.xml` | `ProcessorArchitecture` → `neutral`; `unvirtualizedResources` added |
| `harness/phase5-e/run-e2.ps1` | rewritten: pre-flight before creation, aborted-run recovery, `-CleanOnly`, deployment-log diagnostics, guarded cleanup ordered by consequence |

**Also fixed, found by running the new module:** `Sel()` returned an `XmlNodeList` from a PowerShell
function, which enumerates it — an empty result became `$null` and `.Count` threw under `StrictMode`.
The comma operator keeps every result an array. That defect existed for minutes because the control
suite ran immediately; the same class of defect survived three Windows runs when it did not.

---

## I. PRE-RUN CHECKLIST

1. `.\harness\phase5-e\test-e2-preflight.ps1` → must report **PASS**. (Windows PowerShell 5.1;
   `pwsh` is not installed on the gate machine.)
1b. **Sideloading must be permitted** — `S01` in pre-flight. See §G-6.
2. `.\harness\phase5-g1\run-g1.ps1` has been run at least once, so the bridge exists.
3. PowerShell is **not** elevated.
4. Windows 11 build ≥ 22000.
5. VS Build Tools with the C++ workload, and the Windows SDK (`makeappx`, `signtool`).
6. No leftover `CN=NOBA-DEV-ONLY` certificate — the script detects and clears it, and `-CleanOnly`
   does it on its own.
7. Two UAC prompts are expected: trust, then removal.

---

## J. STATUS

| | |
|---|---|
| Gate 1 | PASS |
| Gate 2 / E1 | PASS ×2, hash-verified |
| Gate 2 / E2 | **OPEN — BLOCKED BY ENVIRONMENT.** `0x80073CFF`, explicit policy block. Not a pass, not a fail: Explorer activation was never reached. Frozen in `DROP08_PHASE5_E2_EVIDENCE.md`. |
| Harness 5.1 parseability | PASS — 5 scripts, 21 checks, 3 codepages. Defect and guard in `DROP08_PHASE5_DELTA.md` §13. |
| `scripts/` | surveyed read-only: `dragdrop-gate.ps1`, `format-gate.ps1`, `shell-gate.ps1` do **not** parse under 5.1 on a non-UTF-8 ANSI codepage (deliberate Arabic/Japanese fixtures — a BOM is the only correct fix). **Not modified.** |
| Product code | untouched — `src/`, `tests/`, `scripts/`, ADR-0006, `BridgeExports.cs`, `g1host.cpp` |
| Recommendation | PROCEED B1, conditional on E2 |
