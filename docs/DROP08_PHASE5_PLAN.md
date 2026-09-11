# DROP 08 Phase 5 — Modern context menu (`IExplorerCommand`) — CONTRACT AND PLAN

**Status: PLAN ONLY. No product code written. Nothing verified on Windows.**

Authorised 2026-08-28. ADR-0006 unamended. DROP 09 not started.

---

## 0. The one problem this plan has to solve first

Every other section depends on it, so it goes before the structure rather than inside it.

The authorisation says two things that pull against each other:

> Use a **native C++** COM shell extension for the Windows Shell boundary.
> The C++ adapter must **NOT** duplicate `ShellMenuBuilder` rules, the capability model, or command
> construction.

`ShellMenuBuilder` and `FormatCapabilityReport` are .NET, in `NOBA.Archive.Core`. So the C++ layer
cannot *decide* anything — it must **ask**. How it asks is the single largest decision in Phase 5,
and it is the thing I would most like reviewed before any code exists.

### 0.1 Three ways to bridge, and what each costs

| | Shape | Where .NET runs | Verdict |
|---|---|---|---|
| **B1** | C++ DLL registered via `com:SurrogateServer`; the DLL hosts .NET through `nethost`/`hostfxr` and calls the real `ShellMenuBuilder` | **`dllhost.exe`**, a surrogate process — *not* Explorer | **Recommended** |
| **B2** | C++ DLL registered in-proc; hosts CoreCLR inside `explorer.exe` | Explorer's own process | **Rejected** |
| **B3** | C++ DLL evaluates a per-entry availability predicate published as data in `shell-verbs.json` | nowhere — C++ decides from a table | **Fallback only** |

**B2 is rejected** because loading the .NET runtime into `explorer.exe` puts our failure modes into
the user's desktop shell. A shell extension that crashes takes Explorer with it, and one that leaks
leaks in the process the user cannot restart casually.

**B3 is a second implementation of the rule wearing a data costume.** The predicate would live in
C++ and could drift from `ShellMenuBuilder` exactly the way `shell-register.ps1` drifted from
`ShellLauncher` and produced D2 and D3. `SHELL-064` would catch drift *if* the C++ evaluator were
reachable from a test, which is itself work. It is the fallback, taken only on measurement, never on
preference.

**B1 is recommended**, and it has a shipping precedent: `com:SurrogateServer` is how PowerToys
registers its Explorer extensions, and it means the DLL is loaded into `dllhost.exe` rather than into
Explorer. That gives us process isolation *and* the real `ShellMenuBuilder`, with no duplicated rule
anywhere.

### 0.2 The cost of B1, stated before it is discovered

The .NET runtime has to start in the surrogate the first time a menu is drawn. That is a cold start
inside a held right-click — the exact budget `SHELL-020`, `SHELL-021` and `ARC-061` exist to
protect.

**`IExplorerCommand::GetState` is designed for this.** It receives `fOkToBeSlow`; when that is
`FALSE` and the answer needs work, the documented return is `E_PENDING`, and the shell re-asks on a
background thread. So the contract is:

- runtime cold, `fOkToBeSlow == FALSE` → return `E_PENDING`, start warming
- runtime warm, or `fOkToBeSlow == TRUE` → real answer from `ShellMenuBuilder`

That is the mechanism the platform provides for precisely our situation, and using it is not a
workaround.

**It is still a risk, and it is measured before the adapter is built** — §G.1 is a latency spike, and
it comes first. If a warm round trip cannot be done inside the menu budget, B3 becomes the decision
and this plan is revised rather than quietly bent.

---

## A. Component and file structure

```
src/
  NOBA.Archive.Core/                    (unchanged — the authority)
    Actions/ShellMenuBuilder.cs
    Actions/ShellInvocations.cs
    Actions/ShellRegistration.cs
    Formats/FormatCapabilityReport.cs

  NOBA.Archive.ShellBridge/             ★ NEW — .NET, net10.0
    ShellBridgeExports.cs               [UnmanagedCallersOnly] C ABI, JSON in / JSON out
    ShellMenuQuery.cs                   selection JSON  → ShellSelection
    ShellMenuResult.cs                  menu + request  → JSON
    NOBA.Archive.ShellBridge.csproj

  NOBA.Archive.ShellAdapter/            ★ NEW — native C++ DLL
    NOBA.Archive.ShellAdapter.vcxproj
    dllmain.cpp                         DllGetClassObject / DllCanUnloadNow
    ClassFactory.cpp/.h
    NobaExplorerCommand.cpp/.h          IExplorerCommand — the submenu root
    NobaSubCommand.cpp/.h               IExplorerCommand — one per entry
    NobaSubCommandEnum.cpp/.h           IEnumExplorerCommand
    ClrHost.cpp/.h                      nethost/hostfxr, function-pointer cache
    Selection.cpp/.h                    IShellItemArray → selection JSON
    Guids.h                             CLSID, generated from ShellRegistration

packaging/
  sparse/AppxManifest.xml               ★ NEW
  sparse/Assets/                        ★ NEW — required package logos

scripts/
  dev-certificate.ps1                   ★ NEW — DEV-ONLY self-signed cert
  modern-menu-register.ps1              ★ NEW — pack, sign, Add-AppxPackage
  modern-menu-gate.ps1                  ★ NEW — surface A gate, SHELL-W1/W2 + W3…W10

tests/
  NOBA.Archive.Presentation.Tests/
    ShellModernAdapterTests.cs          ★ NEW — SHELL-063, 064, 065
```

**Two new projects, not one.** `ShellBridge` is the thin managed edge that speaks a C ABI;
`ShellAdapter` is the C++ COM object. Splitting them keeps the C++ free of any menu vocabulary and
keeps the managed side free of COM.

---

## B. The boundary, exactly

```
Explorer  ──COM──▶  NobaExplorerCommand (C++, in dllhost.exe)
                        │  IShellItemArray → { path, isDirectory }[]  as UTF-8 JSON
                        ▼
                    ShellBridgeExports (managed, same process)
                        │
                        ▼
                    ShellSelection ──▶ ShellMenuBuilder ──▶ ShellMenuEntry[]
                                                    └────▶ ArchiveActionRequest
                                                                  │
                                                                  ▼
                                                          ShellLauncher.PlanFor
                                                                  │
                        ┌─────────────────────────────────────────┘
                        ▼
                    C++ starts the plan's executable with the plan's argument array
                        │
                        ▼
                    noba-ar.exe / NOBA.Archive.App.exe → existing engine
```

### B.1 What the C++ layer may do

Convert `IShellItemArray` to paths and folder flags · marshal JSON both ways · implement the COM
interfaces · start a process from an executable name and an argument array · cache the CLR host.

### B.2 What the C++ layer may never do

Decide whether an action is available · know a format name, extension or capability · build a
command line · compose an archive path · read or write an archive · touch the registry for its own
purposes · localise a string.

**Enforced, not merely stated.** `SHELL-063` is a source rule over `src/NOBA.Archive.ShellAdapter/`:
no occurrence of `7z`, `zip`, `rar`, `gzip`, `tar`, `Compress`, `Extract`, `CanCreate`, `.7z`, or any
`ArchiveAction` name outside a comment. The adapter's vocabulary is `entryId`, `title`, `state` — ids
and opaque strings. If a format name appears in the C++, the build fails.

### B.3 The ABI

One function, so the surface cannot grow by accident:

```c
int32_t NobaShellQuery(
    const char* requestJson,   // UTF-8
    char**      responseJson,  // UTF-8, allocated managed-side
    int32_t*    responseLength);

void NobaShellFree(char* responseJson);
```

Two request kinds — `menu` and `invoke` — discriminated inside the JSON. JSON rather than a struct
layout because the boundary crosses a language and a marshaller, and a silently mismatched struct is
the kind of defect that shows up as a wrong menu six months later.

---

## C. Data flow

### C.1 Single selection — right-click `bundle.7z`

1. Explorer calls `GetState(psiItemArray, fOkToBeSlow)`.
2. C++ enumerates one `IShellItem`; `SFGAO_FOLDER` → `isDirectory: false`; `SIGDN_FILESYSPATH` → path.
3. → `{"kind":"menu","items":[{"path":"C:\\D\\bundle.7z","isDirectory":false}]}`
4. Managed: `ShellSelection.From(...)` → `ShellMenuBuilder.Build(selection, reportFor, creatable)`.
5. → `{"entries":[{"id":"noba.open","state":"enabled"},{"id":"noba.test","state":"enabled"}, …]}`
6. C++ maps `state` to `ECS_ENABLED` / `ECS_DISABLED` / `ECS_HIDDEN`. **No interpretation.**

### C.2 Multi-selection — three files, `Compress to ZIP` — the whole point of Phase 5

1. `Invoke(psiItemArray, pbc)` receives **one** array of **three** items.
2. C++ enumerates all three, preserving Explorer's order.
3. → `{"kind":"invoke","entryId":"noba.compress.zip","items":[…3 items…]}`
4. Managed: one `ShellSelection` of three → one `ShellMenuEntry` → **one** `ArchiveActionRequest`
   carrying all three → `ShellLauncher.PlanFor` → one `ShellLaunchPlan`.
5. → `{"executable":"noba-ar.exe","arguments":["create","C:\\D\\D.zip","C:\\D\\a.txt","C:\\D\\b.txt","C:\\D\\c.txt","--format","zip"]}`
6. C++ starts it — **one process, argument array, no command string, no `cmd.exe`.**

**One invocation, one request, one archive.** D4 and `SHELL-057` are satisfied by the mechanism
rather than worked around, which is the whole reason for choosing it.

### C.3 What the resolver verb becomes

The resolver (`noba-ar shell <entry-id> …`) exists because the classic registry cannot compute a
containing folder or a suggested name. **The modern adapter has no such limit** — it calls
`ShellLauncher.PlanFor` directly and gets a fully-filled plan.

So on surface A there is no resolver hop. `ShellInvocations.IsDirectlyRegistrable` remains a fact
about the *classic* surface only, and `SHELL-066`…`SHELL-073` keep testing the resolver because
surface B still uses it. **Nothing is deleted.**

---

## D. Menu hierarchy

```
[right-click]
  NOBA Archive                    ← root, ECF_HASSUBCOMMANDS, above "Show more options"
    ├── Open with NOBA Archive
    ├── Test archive
    ├── Extract here
    ├── Extract to…
    ├── Compress to 7Z
    ├── Compress to ZIP
    ├── Compress to TAR
    ├── Compress to GZIP          ← ECS_DISABLED where the model says so
    └── Create archive…
```

- The **root** returns `ECF_HASSUBCOMMANDS` from `GetFlags` and its children from
  `EnumSubCommands` (`IEnumExplorerCommand`).
- **One sub-command object per registered entry**, constructed from
  `ShellRegistration.Entries(...)` — the same list the classic manifest publishes. `SHELL-065`.
- Root visibility: `ECS_HIDDEN` when **every** child would be hidden, so an empty submenu never
  appears. That decision comes from the managed side with the rest.

OPEN-6 is satisfied structurally: a package-registered `IExplorerCommand` *is* the modern menu.
Placement above "Show more options" is a property of the mechanism, not something we position.

---

## E. `GetTitle` / `GetState` / `Invoke`

### E.1 `GetTitle(psiItemArray, ppszName)`

Called at **runtime**, per draw. The managed side returns the label from
`LocalizedStrings.For(language)[verb.TextKey]`, with `CompressTo` already expanded per format.

**This retires the interim label path.** `ShellVerb.InterimLabel` and
`ShellRegistration.InterimTextFor` exist only because a registry `MUIVerb` holds one string
(`SHELL-L2`). They are **not** used by the adapter. They stay in the code while surface B does, with
`SHELL-031` still guarding them against drift, and die with it.

Language: the process UI language, resolved managed-side by the same rule the application uses. Not
re-derived in C++.

### E.2 `GetState(psiItemArray, fOkToBeSlow, pCmdState)`

| Model says | Returns |
|---|---|
| entry present and enabled | `ECS_ENABLED` |
| entry present, disabled with a `ShellBlocker` | **`ECS_DISABLED`** |
| entry absent from the menu | `ECS_HIDDEN` |
| CLR not yet warm and `fOkToBeSlow == FALSE` | `E_PENDING` |

**`ECS_DISABLED` closes `SHELL-L9`.** The classic surface has no third state, so
*Compress to GZIP* on a folder had to be omitted entirely. Here it can be shown and greyed, which is
what `ShellMenuBuilder` has been producing all along —
`ShellMenuEntry.Disabled(…, ShellBlocker.SingleMemberFormat)` finally reaches a surface that can
express it.

Per the authorisation: **where the model says disabled, show disabled** — do not hide a capability
the modern surface can represent.

*Open question for review:* a greyed entry cannot explain itself in this API — there is no tooltip on
a disabled command. `GetToolTip` may not be consulted when disabled. **Unverified**; §G.5 measures
it. If it is not, we choose between a silent grey and hiding, and I will not decide that here.

### E.3 `Invoke(psiItemArray, pbc)`

Managed side: selection → `ShellMenuBuilder.Build` → matching entry → `RequestFor` →
`ShellLauncher.PlanFor` → plan. C++ starts it with `CreateProcessW` using the argument array.

**Re-asked, not trusted.** `Invoke` re-evaluates availability rather than assuming `GetState` still
holds — the selection can change between draw and click. A disabled or absent entry returns
`E_ACCESSDENIED` and does nothing.

**Fail-safe:** any bridge failure — CLR unavailable, malformed JSON, null plan — returns a failure
`HRESULT` and starts **nothing**. `SHELL-073`'s principle carries over: when the selection cannot be
reconstructed, write nothing.

---

## F. MSIX and the development certificate

### F.1 Sparse package

Per ADR-0006: unpackaged EXE + **sparse** package with `<uap10:AllowExternalContent>`.

```xml
<Identity Name="NOBA.Archive" Publisher="CN=NOBA-DEV-ONLY" Version="0.5.0.0" />

<com:Extension Category="windows.comServer">
  <com:ComServer>
    <com:SurrogateServer DisplayName="NOBA Archive Shell">
      <com:Class Id="{CLSID}" Path="NOBA.Archive.ShellAdapter.dll" ThreadingModel="STA"/>
    </com:SurrogateServer>
  </com:ComServer>
</com:Extension>

<desktop5:Extension Category="windows.fileExplorerContextMenus">
  <desktop5:FileExplorerContextMenus>
    <desktop5:ItemType Type="*">      <desktop5:Verb Id="NobaArchive" Clsid="{CLSID}"/></desktop5:ItemType>
    <desktop5:ItemType Type="Directory"><desktop5:Verb Id="NobaArchive" Clsid="{CLSID}"/></desktop5:ItemType>
  </desktop5:FileExplorerContextMenus>
</desktop5:Extension>
```

`SurrogateServer` is what keeps the DLL out of `explorer.exe` (§0.1).

**`SHELL-065`:** `Identity/Name`, the CLSID and the verb id are generated from `ShellRegistration`
constants, not typed into the XML. The manifest is produced the way `shell-verbs.json` is — by
`noba-ar`, from Core — and a test compares the generated file to the constants.

### F.2 Development certificate — **DEV-ONLY**

```powershell
.\scripts\dev-certificate.ps1        # New-SelfSignedCertificate, CN=NOBA-DEV-ONLY
                                     # → Cert:\LocalMachine\TrustedPeople
.\scripts\modern-menu-register.ps1   # makeappx pack → signtool sign → Add-AppxPackage
```

The script will print `DEV-ONLY — NOT A SHIPPING CERTIFICATE` on every run, the subject is literally
`CN=NOBA-DEV-ONLY` so it cannot be mistaken in a certificate store, and `-Remove` untrusts it.

**Known trap, called out now:** `Publisher` in the manifest must match the certificate subject
*exactly* or `Add-AppxPackage` fails with a signature error that names neither. The register script
reads the subject from the cert and compares before packing.

**Phase 6 owns procurement.** Nothing in Phase 5 may claim production signing readiness, and the
gate script will say so in its own output.

---

## G. Windows verification plan

**I cannot compile or run any of this.** Every item is Windows-only and none may be reported as
passing until a transcript exists.

| | Step | Settles |
|---|---|---|
| **G.1** | **Latency spike, before the adapter is built.** A minimal C++ DLL that hosts the CLR, calls a trivial managed function, and logs cold and warm round-trip times | Whether **B1** is viable at all, or **B3** becomes the decision |
| G.2 | Package registers; the NOBA entry appears in the modern menu | `SHELL-W1` |
| G.3 | Submenu contents per selection class, all 13 rows of the §14.2 matrix | `SHELL-W2` |
| G.4 | **Three files → Compress to ZIP → exactly one archive containing three** | **The reason Phase 5 exists** |
| G.5 | *Compress to GZIP* on a folder is **shown and greyed**, not absent | `SHELL-L9`, and whether a disabled entry can explain itself |
| G.6 | Labels in Arabic with the UI in Arabic | `SHELL-L2` |
| G.7 | Arabic and Japanese paths survive `IShellItemArray` → archive | `SHELL-W8` |
| G.8 | Both surfaces registered → NOBA appears **once**, not twice | OPEN-5 |
| G.9 | Uninstall: package removed, classic keys removed, no residue | `SHELL-W9` |
| G.10 | Re-register twice → no duplicate entries | `SHELL-W10` |

`scripts/modern-menu-gate.ps1` follows the Phase 4 pattern: automate everything machine-checkable
(package presence, CLSID registration, artefacts on disk), leave only what needs eyes, and keep
`NOT IMPLEMENTED` distinct from `NOT RUN`.

---

## H. Tests and negative controls

### H.1 New contracts

| ID | Holds | Where |
|---|---|---|
| **SHELL-063** | the adapter holds no menu logic — no format name, capability term or action name in `src/NOBA.Archive.ShellAdapter/**` | Architecture (source rule) |
| **SHELL-064** | **adapter and classic surface produce identical menus for identical selections** — the bridge is driven with the §14.2 matrix and compared cell for cell against `ShellMenuBuilder` | Presentation |
| **SHELL-065** | package identity, CLSID and verb id derive from `ShellRegistration`; the generated `AppxManifest.xml` matches | Architecture + Presentation |
| **SHELL-082** *(new id, after 078b)* | the bridge's JSON round trip preserves order, Unicode, spaces and the directory flag | Presentation |
| **SHELL-083** *(new id)* | a bridge failure yields no launch — malformed JSON, absent entry, null plan each start nothing | Presentation |

**`SHELL-064` is the load-bearing one.** It is testable on Linux because both sides of the comparison
are managed: the bridge's entry point takes JSON and returns JSON, so the test drives
`ShellBridgeExports` directly and compares against `ShellMenuBuilder`. Only the C++ marshalling is
untestable here, and `SHELL-082` narrows what that marshalling can get wrong.

### H.2 Negative controls

| Mutation | Must turn red |
|---|---|
| a format name appears in the C++ adapter | `SHELL-063` |
| the bridge filters the selection before building the menu | `SHELL-064` |
| the bridge returns entries in a different order | `SHELL-064` |
| the CLSID is typed into `AppxManifest.xml` as a literal | `SHELL-065` |
| the JSON drops `isDirectory` | `SHELL-082`, and `SHELL-064` on the folder rows |
| a null plan starts the executable anyway | `SHELL-083` |
| `GetState` returns `ECS_HIDDEN` where the model says disabled | `SHELL-064` |

All existing tests keep running unchanged. **Nothing from `SHELL-001`…`SHELL-078b` is deleted or
weakened** — surface B still exists and is still tested.

---

## I. Risks and non-goals

### I.1 Risks

| | Risk | Handling |
|---|---|---|
| **R1** | **CLR cold start exceeds the context-menu budget.** The largest risk, and it invalidates B1 | `E_PENDING` on `fOkToBeSlow == FALSE`; **G.1 measures it before the adapter is written** |
| R2 | Sparse package will not register — signature, `Publisher` mismatch, developer mode | Register script compares subject to manifest before packing |
| R3 | A crash in the adapter is a crash in `dllhost.exe` | Surrogate keeps it out of Explorer; every COM entry point wrapped, failures return `HRESULT` |
| R4 | Both surfaces registered → NOBA twice (OPEN-5) | G.8; registration must know which surface is live |
| R5 | **I cannot compile or test C++ or COM here** | The bridge is managed and testable; C++ is thin; `SHELL-064` compares the new surface to a proven model |
| R6 | `hostfxr` cannot find a runtime on a machine without .NET | Detected at load; adapter reports unavailable rather than half-working |
| R7 | A disabled entry may not be able to explain itself | G.5; decision deferred, not guessed |

### I.2 Non-goals for Phase 5

Production certificate (Phase 6) · WiX MSI (Phase 9) · classic-surface changes of any kind ·
single-instance activation (`SHELL-L3`) · icons beyond what registration requires · DROP 09 ·
any amendment to ADR-0006.

**Explicitly:** the classic surface is not modified, `%*` is not changed to `%1`, no collector is
built, N-processes is not accepted, and Option A is not taken. `SHELL-L8` stands as a measured
limitation of surface B.

---

## J. Files proposed for modification

### J.1 New

```
src/NOBA.Archive.ShellBridge/**                        managed bridge (5 files)
src/NOBA.Archive.ShellAdapter/**                       C++ COM adapter (11 files)
packaging/sparse/AppxManifest.xml                      generated, not hand-written
packaging/sparse/Assets/**                             package logos
scripts/dev-certificate.ps1
scripts/modern-menu-register.ps1
scripts/modern-menu-gate.ps1
tests/NOBA.Archive.Presentation.Tests/ShellModernAdapterTests.cs
tests/NOBA.Archive.Architecture.Tests/ShellAdapterSourceTests.cs
```

### J.2 Modified

| File | Change | Why |
|---|---|---|
| `Core/Actions/ShellRegistration.cs` | **add** package identity, CLSID and verb id constants | `SHELL-065` — identity from one place |
| `Cli/Commands/CliApplication.cs` | **add** an `appx-manifest` verb that generates `AppxManifest.xml` | same reason `shell-manifest` exists |
| `Cli/BuildStamp.cs` | bump | standing rule |
| `NOBA.Archive.App.sln` | add the two new projects | build |
| `docs/SHELL_INTEGRATION.md` | surface A section; `SHELL-L2`/`L9` marked closing | record |
| `docs/PRODUCT_PLAN.md` | Phase 5 status | record |
| `docs/DROP08_REAUDIT.md` | Phase 5 results | record |

**No change to** `ShellMenuBuilder`, `ShellInvocations`, `ShellLauncher`, `ArchiveActionRequest`,
`ShellSelection`, `FormatCapabilityReport`, `ShellCommandLine`, `ShellRequestSeed`,
`shell-register.ps1`, `shell-verbs.json`, or any existing test.

That list is the plan's own claim about itself: **if Phase 5 needs to touch the action model, the
adapter is doing something it should not be**, and that is a signal to stop rather than to edit.

---

## L. Installation, elevation, and the security boundary

**Added 2026-08-28 by explicit requirement. This section is architecture, not implementation — no
installer, no manifest and no certificate script has been written.**

*(Placed before §K deliberately: §K is the plan's closing ask and stays last. The lettering is out of
order because this section was added after the plan was written, not because a section is missing.)*

The rule being recorded:

> **INSTALL / UNINSTALL = Administrator. NORMAL RUNTIME = Standard User.**

### L.0 First: does this contradict ADR-0006?

**No, and ADR-0006 is therefore not amended.** ADR-0006 already chose *"unpackaged EXE installed by a
WiX MSI … per-machine install"*, recording the install directory in `HKLM\SOFTWARE\NOBA\Archive\InstallPath`
and putting the directory on `PATH`. Every one of those is an elevated operation. The requirement
names a boundary the accepted packaging decision already implies; it does not move it.

**One thing worth stating plainly, because it is easy to assume the opposite:** MSIX does *not*
inherently require elevation. The documented per-user registration —
`Add-AppxPackage -Path <pkg> -ExternalLocation <dir>` — runs as a standard user with no UAC prompt at
all. Elevation here is a **consequence of choosing a per-machine install**, which ADR-0006 did for
reasons that had nothing to do with the shell. So the requirement is satisfiable, and it is satisfied
by the path already chosen — but it is a choice, not a platform constraint, and the plan should not
pretend otherwise.

### L.1 Which installation operations require elevation

| Operation | Elevated? | Why |
|---|---|---|
| MSI writes the install directory (Program Files) | **yes** | per-machine location |
| MSI writes `HKLM\SOFTWARE\NOBA\Archive\InstallPath` | **yes** | `HKLM` |
| MSI adds the install directory to the machine `PATH` | **yes** | machine environment |
| `Add-AppxPackage -Stage <pkg> -ExternalLocation <dir>` | **yes** | staging for all users |
| `Add-AppxProvisionedPackage -Online -PackagePath <pkg>` | **yes** | provisioning for all users |
| dev certificate → `Cert:\LocalMachine\TrustedPeople` | **yes** | machine certificate store — **Phase 5 gating only**, see L.6 |
| *(alternative)* `Add-AppxPackage` per-user, cert → `Cert:\CurrentUser\TrustedPeople` | **no** | per-user; used for gating, not for the product install |

**Nothing else at install time needs elevation, and nothing at runtime does.**

### L.2 What the elevated installer registers — and the trap that makes this non-obvious

The elevated installer performs, in order:

1. lay down `NOBA.Archive.App.exe`, `noba-ar.exe`, `native\x64\7z.dll` and
   `NOBA.Archive.ShellAdapter.dll` in the install directory;
2. write `HKLM\SOFTWARE\NOBA\Archive\InstallPath`; add to `PATH` (opt-out per ADR-0006);
3. **stage** the sparse package with `-ExternalLocation` pointing at the install directory;
4. **provision** it online, so every user of the machine gets the registration at next sign-in.

**The trap.** An elevated installer must **not** simply call plain `Add-AppxPackage`. That registers
the package **for the principal running the installer** — the administrator, or `SYSTEM` — and the
target user's Explorer then shows nothing at all, with no error anywhere to explain it. The
documented per-machine pair is *stage then provision* precisely because a single-step registration
cannot cross that boundary. This is the single most likely way to produce an installer that "works"
on the developer's machine and silently fails on a real one.

**The same trap applies to the Windows 10 classic fallback, and it touches existing code.**
`scripts/shell-register.ps1` writes `HKCU\Software\Classes` throughout, and says so in its own
header: *"per-user keys under HKCU, no package, no signature, no elevation."* That is correct for
what the script is — a developer and opt-in tool. But it means **the installer must not implement the
classic fallback by running `shell-register.ps1` elevated**, because the keys would land in the
administrator's hive. The installer's classic path must write `HKLM\Software\Classes`, or defer to a
per-user bootstrap that runs unelevated in the target user's session.

**`shell-register.ps1` is not modified by this section.** The requirement is recorded against the
installer, which does not exist yet.

### L.3 Which runtime process runs as the normal user

All of them. The chain, with the token each link carries:

```
Windows Explorer                      standard user, the user's own token
  ↓  COM activation
dllhost.exe  (surrogate)              SAME user, SAME session — no elevation, no UAC
  ↓  hostfxr
NOBA managed bridge                   standard user
  ↓  ShellMenuBuilder → ShellLauncher → ShellLaunchPlan
  ↓  CreateProcessW  (inherits the caller's token)
noba-ar.exe / NOBA.Archive.App.exe    standard user
  ↓
existing NOBA engine, ResourceGuard, Operation Queue   standard user
```

**No link in that chain elevates, and none may be made to.** `ResourceGuard` and the Operation Queue
are unchanged by this section; they already run under whatever token the process has, and that token
is now explicitly the user's.

### L.4 How the activation preserves the boundary — enforceable rules, not intentions

A surrogate inherits its identity from the activating client, so the boundary holds **by default**
and is broken only by explicitly asking for elevation. The rules below name every way of asking, so
that "we did not intend to elevate" is not the only thing standing between the design and a UAC
prompt on every right-click.

| Must not appear | Where it would break the boundary |
|---|---|
| `RunAs` value under the `AppID` (`Interactive User`, a named account, `NT AUTHORITY\SYSTEM`) | changes the surrogate's identity |
| the elevation moniker `Elevation:Administrator!new:` | elevates the COM object |
| `CoCreateInstanceAsAdmin` | same, via the shell helper |
| `ShellExecuteEx` with the `runas` verb in `ShellLauncher` | elevates the launched process |
| `requestedExecutionLevel` other than `asInvoker` in any NOBA executable manifest | elevates unconditionally |

**`SHELL-085`** *(new id, after `SHELL-084`)* — a source rule over `src/NOBA.Archive.ShellAdapter/`,
the managed bridge, and the generated `AppxManifest.xml`: none of the five strings above occurs, and
the generated manifest contains no `RunAs` attribute. The build fails if one appears, in the same way
`SHELL-063` fails on a format name.

**A finding from reading the current tree, recorded rather than fixed.**
`src/NOBA.Archive.App/app.manifest` declares `dpiAwareness`, `longPathAware` and `supportedOS` but
**no `requestedExecutionLevel` at all**, and `NOBA.Archive.Cli` has **no manifest**. An absent
declaration leaves the process subject to Windows' installer-detection heuristics rather than an
explicit `asInvoker`. Under this requirement both should state `asInvoker` explicitly. **Not changed
here** — it is implementation, and this section is a plan update.

`ShellLaunchPlan` is already the right shape for this: it carries an executable and an argument
array, and **no elevation flag**. The requirement is that it never gains one. That is a property of
the existing contract, which is why §J's "no change to `ShellLauncher`" survives this section intact.

### L.5 Uninstall

**Uninstall is elevated, and it has two halves that are easy to mistake for one.**

| Step | Elevated? | Removes |
|---|---|---|
| `Remove-AppxProvisionedPackage -Online` | yes | the provisioning — **but not** registrations users have already received |
| `Remove-AppxPackage` per registered user | per user | the remaining per-user registrations |
| delete classic `Software\Classes` NOBA keys | yes for `HKLM`, per-user for `HKCU` | the Windows 10 fallback, both hives |
| remove `HKLM\SOFTWARE\NOBA\Archive` | yes | install record |
| remove the `PATH` entry | yes | ADR-0009's CLI reachability |
| remove install directory and payload | yes | binaries |
| untrust the dev certificate | yes | **Phase 5 gating machines only** — must never exist on a user's machine |

**Deprovisioning alone is not uninstalling.** A user who has already signed in once keeps a working
registration after the provisioned package is gone, and the menu survives an "uninstall" that only
did the first row. ADR-0006 already requires *"a packaging test verifies a clean machine state after
uninstall"*; this section says what clean has to mean.

**`SHELL-086`** *(new id)* — uninstall completeness across **both** surfaces: after uninstall, no
NOBA package is provisioned, none is registered for the invoking user, no NOBA key survives under
`HKLM\Software\Classes` or `HKCU\Software\Classes`, `HKLM\SOFTWARE\NOBA` is gone, and `PATH` no
longer contains the install directory. This extends `SHELL-W9`, which today checks `HKCU` only
because `HKCU` is all that exists today.

### L.6 The development certificate during Phase 5 gating

> **CORRECTED 2026-08-28 BY MEASUREMENT. The original recommendation in this section was wrong.**
> It said to gate with `Cert:\CurrentUser\TrustedPeople` and no elevation. `Add-AppxPackage` refused
> the package:
>
> ```
> 0x800B0109 — A certificate chain processed, but terminated in a root certificate which is not
>              trusted by the trust provider.
> ```
>
> **MSIX deployment consults the MACHINE TrustedPeople store, not the user's.** Microsoft's signing
> guidance says so directly: import the certificate *"into the Local Machine Trusted People store
> using `Import-PfxCertificate` from an admin PowerShell session"*. The per-user store is not an
> option for a self-signed package, at gating time or any other time.

**Corrected recommendation: trusting a self-signed certificate is an elevated step, always. Nothing
else about the gate is.**

| | Gating (Phase 5) | Product install (Phase 9) |
|---|---|---|
| certificate store | `Cert:\LocalMachine\TrustedPeople` | — production CA certificate, **no trust step at all** |
| who trusts it | **Administrator**, once, then removed | nobody: the chain is already trusted |
| registration | `Add-AppxPackage -ExternalLocation` — **standard user** | stage + provision — Administrator |
| Explorer / surrogate | **standard user** | standard user |
| blast radius | the machine, until removed | none |

**The correction strengthens §L rather than undermining it.** The elevated step is *install-time
trust*, and everything after it — registration, Explorer, the surrogate, the archive engine — remains
the standard user. `run-e2.ps1` demonstrates exactly that boundary: it refuses to run elevated, and
elevates twice through a helper process, once to trust the certificate and once to remove it.
Criterion 3 still means what it says, because the process that activates the handler was never
elevated.

**And it is a development cost, not a product one.** A CA-issued certificate needs no trust step, so
the extra elevation disappears at Phase 9. The per-machine install is elevated anyway, for the
reasons in §L.1.

**§F.2 was right and this section was wrong.** §F.2 named `LocalMachine\TrustedPeople`; L.6's first
version "refined" it to `CurrentUser` on reasoning rather than evidence. The refinement is withdrawn.

Unchanged from §F.2, and reinforced: the subject is literally `CN=NOBA-DEV-ONLY`; every run prints
`DEV-ONLY — NOT A SHIPPING CERTIFICATE`; removal is verified rather than attempted, and the manual
command is printed if anything survives. **A DEV-ONLY certificate trusted machine-wide is exactly the
residue `SHELL-W9` exists to catch**, and it must never appear in a shipping artefact. The packaging
gate must fail if a `CN=NOBA-DEV-ONLY` certificate is trusted on a machine claiming production
readiness. Phase 6 still owns procurement, and nothing in Phase 5 may claim production signing
readiness.

### L.7 No runtime UAC — confirmed, with the one exception the requirement allows

**Confirmed.** With L.3 and L.4 in force, none of these prompts:

right-click → NOBA Archive · Compress · Create archive · Open · Test · Extract · every other normal
archive operation.

**The one exception, stated exactly as the requirement does:** an individual archive operation may
fail because the *user's own filesystem permissions* do not allow it — writing into a protected
directory, reading a file whose ACL excludes them. That is an ordinary permission failure and the
engine already reports it as one.

**It is not a trigger to elevate.** Three prohibitions, which are the point of this paragraph:

- the archive engine is **never** silently elevated;
- NOBA operations do **not** run as Administrator as a class;
- the shell integration is **not** launched elevated merely because the installation was.

An operation the user lacks permission for produces an error the user can act on. It does not produce
a consent dialog, and it does not produce a second, elevated copy of the engine.

### L.8 Elevation is not a `SHELL-L8` workaround

Recorded because it would otherwise look like an option once an elevated installer exists.

The classic surface's measured limits are unchanged by any privilege:

| measured | result |
|---|---|
| `%*` | `argc 0` — no arguments arrive |
| `"%1"` + `MultiSelectModel=Player` | three processes, one path each |

**Administrator privileges do not change Windows Shell argument expansion**, because the limitation is
in how the shell invokes verbs, not in what the invoked process is allowed to do. Elevation would
produce three *elevated* processes and three archives.

Therefore, unchanged and restated: do not modify the classic registry implementation to solve this ·
do not change `%*` to `%1` as a multi-selection fix · do not introduce a collector or debounce · do
not accept N processes → N archives. **The multi-selection solution remains `IExplorerCommand` +
`IShellItemArray`.**

### L.9 Files that would eventually implement this boundary

**None of these exist. None is authorised by this section.**

| File | New? | Role |
|---|---|---|
| `installer/NOBA.Archive.Installer.wixproj` | new | WiX MSI (ADR-0006, WixToolset.Sdk pinned at Phase 9) |
| `installer/Product.wxs` | new | per-machine layout, `HKLM` install record, `PATH`, the elevated custom actions |
| `installer/RegisterPackage.ps1` | new | stage + provision; **never plain `Add-AppxPackage`** (L.2) |
| `installer/UnregisterPackage.ps1` | new | deprovision **and** per-user removal (L.5) |
| `packaging/AppxManifest.xml` | new, **generated** | sparse manifest; generated from `ShellRegistration` constants per `SHELL-065`; carries no `RunAs` |
| `scripts/dev-certificate.ps1` | new | DEV-ONLY cert; `CurrentUser` for gating, `LocalMachine` for per-machine (L.6) |
| `scripts/modern-menu-register.ps1` | new | pack → sign → register, gating path |
| `scripts/modern-menu-gate.ps1` | new | the Phase 5 Windows gate (§G) |
| `src/NOBA.Archive.App/app.manifest` | **modify** | add explicit `asInvoker` |
| `src/NOBA.Archive.Cli/app.manifest` | new | `asInvoker` |
| `src/NOBA.Archive.ShellAdapter/NOBA.Archive.ShellAdapter.rc` | new | adapter resources; no elevation request |
| `tests/NOBA.Archive.Architecture.Tests/ElevationBoundaryTests.cs` | new | **`SHELL-085`** |
| `tests/NOBA.Archive.Architecture.Tests/InstallerTests.cs` | new | **`SHELL-086`** |

**Still not touched by any of it:** `ShellMenuBuilder` · `ShellInvocations` · `ShellLauncher` ·
`ArchiveActionRequest` · `ShellSelection` · `FormatCapabilityReport` · `ShellCommandLine` ·
`ShellRequestSeed` · `shell-register.ps1` · `shell-verbs.json` · ADR-0006.

### L.13 Sideloading policy — a deployment constraint on ADR-0006, found by measurement

**Added 2026-08-29 after E2 attempt 4 failed with `0x80073CFF`.**

A signed sparse package **will not register on a machine where sideloading is disabled**, regardless
of who signed it. The gate machine has `HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx`
`AllowAllTrustedApps = 0`, and deployment was refused with *"you need either a Windows developer
license or a sideloading-enabled system"*.

**This is not only a harness problem.** ADR-0006 ships the modern context menu through a sparse
package. If an enterprise disables sideloading by policy — a normal, defensible thing for an
enterprise to do — **the modern menu cannot be registered on those machines at all**.

Requirements this places on Phase 9:

| | Requirement |
|---|---|
| 1 | The installer **detects** the sideloading policy before attempting to register the sparse package. **It reads BOTH keys** — see the correction below; checking Developer Mode alone gives the wrong answer |
| 2 | When it is disabled, the installer says so **in those words** — not an HRESULT — and names both keys |
| 3 | The rest of the install still succeeds: the app, the CLI, `PATH`, file associations. Only the modern menu is unavailable |
| 4 | On such a machine the **Windows 10 classic fallback** (surface B, ADR-0006) becomes the only context-menu surface. `SHELL-L8`'s multi-selection limit applies there and must be stated, not hidden |
| 5 | The installer **never** changes the policy. It is a machine security setting and may belong to an organisation |

**CORRECTION 2026-08-29, from measurement — "Developer Mode is on" is not a sufficient test.**

The development machine has the two keys **disagreeing**, and the disagreement resolves against the
setting:

```
…\Policies\Microsoft\Windows\Appx\AllowAllTrustedApps                = 0   deny
…\CurrentVersion\AppModelUnlock\AllowAllTrustedApps                    = 1   allow
…\CurrentVersion\AppModelUnlock\AllowDevelopmentWithoutDevLicense      = 1   Developer Mode ON
```

Sideloading is enabled at the setting level, Developer Mode is on, and registration is still refused
with `0x80073CFF`. Reproduced three times, byte-identical transcripts. **The Group Policy value
wins.**

So an installer that checks Developer Mode, or `AppModelUnlock` alone, will report *"sideloading is
available"* on precisely the machines where the modern menu cannot be registered — the enterprise
machines this requirement exists for. **Requirement 1 above is therefore: read both keys, and let the
policy value override the setting.** Evidence: `DROP08_PHASE5_E2_EVIDENCE.md` §3.1.

**Two keys, and only one is documented.** `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock`
is the documented setting (`AllowAllTrustedApps`, `AllowDevelopmentWithoutDevLicense`). The
`…\Policies\Microsoft\Windows\Appx` override that this machine actually has is **not** described in
Microsoft's Developer Mode documentation; its effect here is observed, not documented, and is recorded
AMBIGUOUS in `DROP08_PHASE5_E2_AUDIT.md` §G-6.

**ADR-0006 is not amended.** This does not change the packaging decision — it adds a precondition the
installer must handle, which is exactly what §L is for.

### L.10 Sources for the two documented claims in L.1 and L.2

- [Grant package identity by packaging with external location — Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps) — the per-user (`Add-AppxPackage -ExternalLocation`, unelevated) and per-machine (`-Stage` + `Add-AppxProvisionedPackage -Online`, elevated) registration commands, and the `TrustedPeople` trust step for self-signed certificates.
- [Add-AppxProvisionedPackage (DISM) — Microsoft Learn](https://learn.microsoft.com/en-us/powershell/module/dism/add-appxprovisionedpackage) — provisioning semantics: applies to users at next sign-in, which is why deprovisioning alone does not uninstall.

---

### L.11 The five lifecycle stages, named and kept apart

**Added 2026-08-28 on approval of §L.** The five stages below are distinct operations with distinct
principals, and collapsing any two of them is the defect this section exists to prevent. L.2 and L.5
describe the traps; this table is the vocabulary.

| # | Stage | Who performs it | Principal | Reversed by |
|---|---|---|---|---|
| 1 | **Package staging** | elevated installer — `Add-AppxPackage -Stage <pkg> -ExternalLocation <dir>` | Administrator | removal of the staged package |
| 2 | **Package provisioning** | elevated installer — `Add-AppxProvisionedPackage -Online -PackagePath <pkg>` | Administrator | **stage 4** |
| 3 | **User-visible registration** | **Windows**, for each user, at their next sign-in | **the user**, unelevated | **stage 5**, per user |
| 4 | **Deprovisioning** | elevated uninstaller — `Remove-AppxProvisionedPackage -Online` | Administrator | — |
| 5 | **Uninstall** | elevated uninstaller **and** per-user removal | Administrator + each user | — |

Four consequences, each of which is a way of getting this wrong:

- **Stage 3 is not performed by the installer.** It is performed by Windows on the user's behalf, and
  it is the stage at which the menu actually appears. An installer that "succeeds" through stages 1
  and 2 has not yet produced a visible menu for anyone, and that is correct rather than broken —
  the user has not signed in again yet.
- **Stage 4 does not undo stage 3.** Deprovisioning stops *future* users from receiving the
  registration; every user who already reached stage 3 keeps a working menu. This is the whole reason
  `SHELL-086` exists.
- **Plain `Add-AppxPackage` is stage 3, not stages 1–2**, and running it from an elevated installer
  performs stage 3 *for the administrator* — which is why L.2 forbids it as a substitute. It is not a
  shortcut through the documented model; it is a different stage aimed at the wrong principal.
- **Only stage 3 is unelevated**, and it is the only stage the ordinary user ever participates in.
  That is the install/runtime boundary restated in lifecycle terms.

### L.12 Status of §L's proposed rules

| | Rule | Status |
|---|---|---|
| `SHELL-085` | no `RunAs`, no elevation moniker, no `CoCreateInstanceAsAdmin`, no `runas` verb, no `requestedExecutionLevel` other than `asInvoker` — source rule plus generated-manifest check | **APPROVED** 2026-08-28. Not implemented. |
| `SHELL-086` | uninstall completeness across the modern and classic surfaces, per L.5 and L.11 | **APPROVED** 2026-08-28. Not implemented. |
| explicit `asInvoker` in `src/NOBA.Archive.App/app.manifest` and a new CLI manifest | **RECORDED AS A REQUIREMENT, DEFERRED.** These files must **not** be modified yet: the change is implementation and stays behind the feasibility gates unless the gate harness itself requires it. | pending |
| certificate policy (L.6) | **APPROVED.** `CN=NOBA-DEV-ONLY` for the Phase 5 gate; local development trust acceptable; production readiness must fail while the DEV-ONLY certificate is in use; procurement remains Phase 6. | in force for gating |
| classic fallback | **UNCHANGED.** `shell-register.ps1` is not modified and the classic fallback is not redesigned. Surface B remains the Windows 10 fallback per ADR-0006, and `SHELL-L8` stays documented rather than disguised. | in force |

---

## K. What I need before implementation

1. **Approve or redirect the bridge (§0.1).** B1 recommended, B3 the measured fallback. This is the
   decision everything else rests on.
2. **Approve G.1 first** — the latency spike before the adapter, so B1 is validated or discarded on
   evidence rather than after the work is done.
3. **Confirm `SHELL-082` and `SHELL-083`** as new ids after `SHELL-078b`.
4. **Confirm the disabled-entry question (§E.2)** is deferred to G.5 rather than decided now.

**STOPPING HERE.** No product code. ADR-0006 unamended. DROP 09 not started.
