# Windows shell integration — product contract

DROP 08. What NOBA Archive offers when you right-click, where each action goes, and what is not
built yet.

> **Status.** The action model, the launch contract and the registration manifest are implemented and
> tested. The **shipping registration mechanism from ADR-0006 — a WiX MSI plus a signed sparse MSIX
> package hosting an `IExplorerCommand` server — is not built.** An interim classic-handler script
> stands in. See [Installation](#installation) for exactly what that costs.

## Supported actions

| Action | Appears when | Runs in | Destination |
|---|---|---|---|
| Open with NOBA Archive | one archive, `CanOpen` | application | — |
| Test archive | one archive, `CanTest` | CLI | — |
| Extract here | one archive, `CanExtract` | CLI | the archive's own folder |
| Extract to… | one archive, `CanExtract` | application | chosen in the existing dialog |
| Compress to *FORMAT* | always, one line per creatable format | CLI | beside the first selected item |
| Create archive… | anything is creatable | application | chosen on the Create screen |

The submenu is deliberately six lines plus one per writable format. It is not a list of the product's
operations.

## Selection behaviour

One file, many files, one folder, many folders, and any mixture are all accepted, and reach the
request in the order Explorer listed them.

Open, Test, Extract here and Extract to require **exactly one** archive. A multi-archive selection
does not offer them: the engine has no batch-extract request shape, and a menu entry that silently
handled only the first file would be worse than its absence.

A directory is never treated as an archive however it is named — a folder called `Backups.zip` is a
folder.

## Capability rules

Every line comes from `FormatCapabilityReport`. There is no second matrix and no extension test
anywhere in the decision:

- **RAR** reports `CanOpen`, `CanTest`, `CanExtract` and **not** `CanCreate` (ADR-0004). So Open,
  Test and both Extracts appear, and **Compress to RAR** cannot — the same fact that removes it from
  the Create screen.
- **BZIP2 / XZ** have no provider, so their report is unavailable and they appear nowhere.
- **GZIP** reports `Create` without `AddEntries`. One selected **file** offers *Compress to GZIP*; two
  or more, **or one folder**, show it **disabled** with "This format holds a single file".

That last rule is `FormatCapabilityReport.PermitsMemberCount`, and it has exactly one implementation.
The shell menu, the Create screen's blocker and the CLI all call it — `SHELL-025` asserts the menu and
the screen give the same answer for the same selection, so the menu can never offer what the screen
would refuse.

**The parameter is members, not clicks**, and that distinction is D7. The rule used to be asked as
`PermitsInputCount(selection.Count)` — "how many things did you click?" — so a single selected folder
answered *one*, GZIP was offered for it, and the engine then refused (`format-gate.ps1` gate 14 proves
that refusal). `ShellSelection.MemberCount` answers the right question instead, and answers it from
`ShellItem.IsDirectory`, which Explorer already supplied: **a folder is `Many`**, without anyone
looking inside it.

That is deliberate rather than approximate. The true count needs the disk, and menu construction may
not touch the disk (`ARC-061`). Treating the unknown as `Many` errs toward withholding — the worst
case is that a single-member format is absent from a folder that happened to hold one file, and the
person reaches it through *Create archive…* The opposite error offers an action that fails.

An extension is used for one thing only: picking which `ArchiveFormat` a name suggests, via
`ArchiveFormat.GuessFromExtension`, which is documented as a hint. It never decides a capability. A
mislabelled archive gets the menu its name earned, and the truth arrives when the action runs and the
content is sniffed.

## The command contract

```
Explorer selection
  → ShellSelection            paths + isDirectory, supplied by the caller
  → ShellMenuBuilder.Build    capability report in, menu out — no disk
  → ShellMenuBuilder.RequestFor
  → ArchiveActionRequest      { Action, Inputs (items, with IsDirectory), Destination, Format }
  → ShellLauncher.PlanFor     { Executable, Arguments[] }
  ────────────────────────────────────────────────────
  → noba-ar.exe               headless verbs
    or NOBA.Archive.App.exe   → ShellCommandLine.Parse → ShellRequestSeed.Apply
  → IArchiveWorkspace → OperationQueue → handler → provider
```

There is one engine. The shell layer produces a request and never performs work; `ARC-060` fails the
build if anything under `Core/Actions` so much as names `CreateArchiveHandler`, `IArchiveProvider`,
`OperationQueue`, `IArchiveWorkspace` or `ResourceGuard`.

### The selection's own facts travel with it

`ArchiveActionRequest.Inputs` is a list of `ShellItem` — path **and** directory flag — not a list of
paths. That is SHELL-075: it used to be strings, so the one fact Explorer had already supplied was
thrown away here, and `SuggestedArchivePath` guessed it back from the count. "One input, therefore a
file" made a folder named `Project.Alpha` propose `Project.zip`, and `Project.Alpha` and
`Project.Beta` collided on one archive name.

The naming rule reads the flag rather than inferring anything:

| Selection | Suggested archive |
|---|---|
| one file `report.pdf` | `report.zip` — the extension is replaced |
| one folder `Project.Alpha` | `Project.Alpha.zip` — a folder name is not a file name |
| several items in `Sales\` | `Sales\Sales.zip` — named for the folder they sit in |

Nothing is probed. The flag comes from Explorer through `ShellSelection`, so `ARC-061` holds and menu
construction still touches no disk. This is the same correction as D7 — a caller re-deriving what it
was already told — one layer further down.

### One table of command shapes

`ShellInvocations.For(action)` is the single definition of what an action runs — an executable and an
ordered list of arguments, each either a fixed word or a slot:

| Slot | Filled with |
|---|---|
| `ArchivePath` | the one selected archive |
| `SelectedPaths` | every selected path, expanding to as many arguments as were selected |
| `ContainingFolder` | the folder holding the selected archive |
| `SuggestedTarget` | where a quick compress writes its archive |
| `FormatId` | the chosen format's id |

`ShellLauncher.PlanFor` fills the slots. `noba-ar shell-manifest` publishes the same list as tokens so
an installer can register a command that means the same thing. A future `IExplorerCommand` adapter
reads the same table. **None of them may compose a command of their own**, and `SHELL-054` compares
what the manifest declares against what the table says, per entry, executable included.

This is the correction for D2 and D3. `shell-register.ps1` used to build `"<exe>" <verb> "%1"` from
the verb name alone, so *Compress to* registered as `noba-ar create "%1"` — the selected file taken as
the archive to create, with no inputs and no format — and *Extract here* registered as
`noba-ar extract "%1"`, whose destination then came from whatever working directory Explorer happened
to hand the process. Both looked plausible in the registry. Nothing compared them to the model,
because there was nothing to compare them to.

### What the classic registry cannot say, and what happens instead

A registry command substitutes the selection and nothing else. `ContainingFolder` and
`SuggestedTarget` are *computed* from the selection, so no substitution expresses them.
`ShellInvocations.IsDirectlyRegistrable` marks those entries, and they are registered as a call to the
resolver verb instead of a command string that would be wrong:

```
"<install>\noba-ar.exe" shell noba.extracthere "%1"
"<install>\noba-ar.exe" shell noba.compress.7z %*
```

`noba-ar shell <entry-id> <paths…>` rebuilds the selection, asks `ShellMenuBuilder` the same question
the menu asked, and dispatches through `ShellLauncher.PlanFor` — in process, as an argument list, with
no string ever assembled. `Directory.Exists` is consulted there and only there: the person has already
chosen the line, so it is no longer inside a held right-click.

The resolver is also where the mechanism's second limitation is handled honestly. A registry key is
present or absent and has no greyed state, so an entry Windows offers may still be one the capability
model withholds for this particular selection — three files chosen for GZIP, say. The resolver refuses
with the model's own reason and exit code `3`, rather than starting an operation that fails.

**Create archive…** seeds `CreateViewModel.AddInputs` — the same method the file picker and the DROP
06 drop zone call — so duplicate folding and ordering come free rather than being reimplemented
(`SHELL-028`).

**Extract here** and **Extract to…** carry no conflict policy. The request has nowhere to put one,
which is the design: whatever `ExtractPlanner.Default` is (today `Ask`) is what a shell extraction
gets, and it cannot drift because there is no second value.

## Installation

The manifest is generated from code, never hand-written:

```powershell
dotnet run --project src\NOBA.Archive.Cli -- shell-manifest > scripts\shell-verbs.json
.\scripts\shell-register.ps1
```

`shell-register.ps1` reads that JSON. So does removal — which is the point. An install list and an
uninstall list maintained separately drift, and what drifts is the removal: a machine left with a
NOBA entry pointing at a deleted executable, and nothing that still knows the string. `SHELL-023`
asserts every key install creates is a key uninstall removes, deepest first.

**The script decides nothing.** Which entries exist, what each is called, where each may appear, what
each runs and in what order all arrive in the manifest; the script translates them into the registry's
spelling and reads back what it wrote. `Convert-NobaToken` is that translation and it is total — a
token it does not recognise throws, so an argument kind added in `Core` without a decision about how
the classic registry expresses it fails the registration loudly instead of registering a command with
an argument missing. `SHELL-054b` asserts the script composes nothing of its own; `SHELL-072` asserts
its translation is faithful, because a script that read `AllItemsAtOnce` and wrote `"%1"` anyway would
leave D4 in place with every other assertion still green.

### Where each entry is registered

Each entry carries a `ShellScope` — `Archives`, `Files`, `Folders` — decided in `Core` against the
same `FormatCapabilityReport` the menu uses, and the installer spells it out:

| Scope | Registry class keys |
|---|---|
| `Archives` | `SystemFileAssociations\<ext>` for every extension this build can **open** |
| `Files` | `*` |
| `Folders` | `Directory` |

This is D5. Every verb used to be registered under `*` and `Directory`, so *Test archive* and *Extract
here* appeared on a `.txt` — actions the capability model would never have offered. *Compress to GZIP*
is registered on `*` and **not** on `Directory`, for the member-count reason above.

`MultiSelectModel` is set per entry from the command shape: an entry naming `SelectedPaths` registers
as `Player` and is invoked **once with the whole selection**; an entry with room for exactly one
registers as `Single`, so the shell hides it for a multi-selection rather than launching N processes.
That is D4 — the classic default is one process per selected item, which turned a three-file *Compress
to 7Z* into three separate single-file archives.

### Why registration uses the .NET registry API

The first version of `shell-register.ps1` used PowerShell's registry provider — `New-Item`,
`Set-ItemProperty` — and appeared to hang. It was not hanging. `scripts\shell-register-trace.ps1`
ran the same operations one at a time on Windows 11 / PowerShell 5.1.26100, against a
`HKCU\Software\Classes` holding **801 subkeys**:

| Operation | Path | Elapsed |
|---|---|--:|
| `New-Item` | `HKCU:\Software\NOBA\Archive` | 30 ms |
| `Set-ItemProperty` | `HKCU:\Software\NOBA\Archive` | 35 ms |
| `New-Item` | contains `*`, depth 3 | 75 ms |
| **`Set-ItemProperty`** | **contains `*`, depth 3** | **24,693 ms** |
| **`Set-ItemProperty`** | **contains `*`, depth 5** | **840,771 ms** |
| **`Set-ItemProperty`** | **contains `*`, depth 6** | **did not return** |

One of the two scopes is the class key literally named `*`, and every provider cmdlet taking `-Path`
reads that as a wildcard. The wildcard is resolved against all 801 class keys, and the cost compounds
with each segment beneath it — 35 ms without it, 24 seconds with it, 14 minutes two levels deeper.
Six verbs across two scopes at that curve is not a wait anyone sits through.

`New-Item` was never the culprit: 75 ms, because `-Force` on a missing leaf takes the literal
creation path. The fix is therefore not to patch one call but to keep the provider away from the
registry entirely. `Microsoft.Win32.RegistryKey.CreateSubKey` / `SetValue` / `OpenSubKey` take
literal names — no resolution, no enumeration, synchronous.

The second failure mode is the quiet one. A path resolved as a pattern can write keys somewhere the
uninstall list does not name, leaving a machine with a menu entry pointing at a deleted executable.
So registration now reads back every key and value it wrote, counts NOBA entries under any class
other than the two scopes, and exits non-zero naming whatever is wrong. It cannot report success on
a partial write, and it cannot stop silently.

`SHELL-032`…`SHELL-037` hold the shape on the build machine; the timings above are the Windows
evidence, and they are recorded in `shell-register.ps1` itself so the next reader does not re-derive
them.

### Uninstallation

```powershell
.\scripts\shell-register.ps1 -Remove
```

Removes both submenus and the publisher key. No manual registry editing, at any point, in either
direction.

### What the interim mechanism costs

ADR-0006 chose an unpackaged EXE installed by a WiX MSI, plus a **signed sparse MSIX package** that
registers an `IExplorerCommand` COM server — which is what Windows 11 requires for the top-level
context menu. That decision stands and is not being replaced. It is simply not built:

- there is no WiX project, no `AppxManifest.xml`, and no shell-extension project in this repository
- a sparse package will not register without a certificate the machine trusts, and none has been
  procured (ADR-0006 puts procurement at Phase 6)
- a COM in-process handler cannot be compiled or exercised on the Linux build machine at all

So `shell-register.ps1` writes the **classic `ContextMenuHandlers` entries** instead — which ADR-0006
already names as the Windows 10 opt-in fallback. Per-user, `HKCU`, no package, no signature, no
elevation. **On Windows 11 the NOBA entry appears under "Show more options", not in the top-level
menu.** That is a real difference from the shipping design and is recorded here as such, not glossed.

## Security

The shell integration is an attack surface whose inputs are filenames a hostile party may have
chosen. Two rules carry it:

**There is no command string, so there is nothing to escape.** `ShellLaunchPlan` is an executable
plus a `string[]`. A file legally named `holiday" & del /q *.*.zip` is one argument containing those
characters; through a concatenated command line it would be a command. `SHELL-024` asserts every
action routes to a NOBA executable and that no argument ever names `cmd.exe` or `powershell`.

**A request is intent, not permission.** Nothing in `Core/Actions` validates a path, and it must not
start: every request still passes the path policy, the capability gate and ResourceGuard when the
operation runs, exactly as a button press does.

Explorer stability follows from the same shape. Menu construction cannot fail on a file, because it
never opens one. A selected file deleted between the right-click and the click produces a
well-formed request and the engine's ordinary not-found error (`SHELL-019`) — not an exception in
Explorer's process.

## Performance

Building the menu touches nothing. No archive is opened, no folder walked, no size computed, no
existence probed. The only fact used beyond the paths is `isDirectory`, which Explorer already knows
and which the caller supplies.

This is enforced, not intended: `ARC-061` fails the build if anything under `Core/Actions` calls
`Directory.EnumerateFiles`, `File.ReadAllBytes`, `Path.Exists`, `FileInfo` or their neighbours, and
`SHELL-021` counts the probe calls to prove the builder asks nothing the caller did not already
answer. A folder of thirty thousand files costs what a folder of three costs.

Work begins after a line is chosen. Never before one is drawn.

## RTL and paths

Menu labels are localized; the technical values inside them are not transformed. A path reaching the
engine is byte-for-byte what Explorer gave — not reversed, not reshaped, not normalized, or the file
will not be found (`SHELL-005`, `SHELL-006`). Inside the application, paths are direction-isolated by
the same `BidiText` rules as everywhere else.

The interim registry handler shows **English labels only** (SHELL-L2).

## Future: the AI seam

No AI is implemented in this drop and none should be. What exists is the shape:

```
"Compress this folder"  →  ArchiveActionRequest { CompressTo, [folder], format }
                        →  IArchiveWorkspace → OperationQueue → handler → provider
```

`ArchiveActionRequest` names no shell, window, command line or caller. A future assistant producing
one gets the same capability gate, path policy and ResourceGuard as the menu — which is the point of
the request being a value rather than a method call. It must never gain an execution path of its own.

## Future: the NOBA OS seam

`ArchiveActionRequest` and `ShellLaunchPlan` live in `Core` and depend on nothing. ADR-0008 and
ADR-0009 already put `noba-ar.exe` in the installer as NobaOS's entry point over the same engine;
these types are the structured form of what it invokes. Nothing is named for WinUI.

## Known limitations

- **SHELL-L1 — the Windows 11 top-level menu is not implemented.** ADR-0006's sparse package and
  `IExplorerCommand` server are unbuilt; the interim classic handler lands under "Show more options"
  on Windows 11. This is the largest gap in the drop.
- **SHELL-L2 — registry labels are English only.** A `MUIVerb` holds one string. Per-language labels
  arrive with the runtime handler, which supplies them from `LocalizedStrings`.
- **SHELL-L3 — no single instance.** Each shell action starts a new process; there is no activation
  redirection into a running window. `ShellCommandLine.Parse` is the seam a future redirection would
  feed, so adding it changes the lifecycle and not this path.
- **SHELL-L4 — no production installer.** `shell-register.ps1` registers a *built* application for
  the current user. It is a development seam, not the MSI, and it does not lay down `7z.dll`, add the
  install directory to `PATH`, or write `HKLM`.
- **SHELL-L5 — batch archive actions are absent.** Open, Test and both Extracts require exactly one
  archive; selecting five and choosing Extract here is not offered.
- **SHELL-L6 — the suggested archive name does not avoid collisions.** `Compress to 7Z` on
  `report.pdf` proposes `report.7z` and the engine refuses an occupied target through the existing
  overwrite contract. Auto-renaming would be a second, quieter overwrite policy in the one place a
  person reaches in a single click.
- **SHELL-L10 — CLOSED 2026-08-27.** A single selected folder used to lose everything after the
  first dot in its name (`Project.Alpha` → `Project.zip`), so two folders could collide on one
  archive. `ArchiveActionRequest.Inputs` now carries `ShellItem`, so `SuggestedArchivePath` reads the
  directory flag Explorer supplied instead of guessing it from the input count. `SHELL-075` and
  `SHELL-078` hold it. See `docs/DROP08_REAUDIT.md` §16.
- **SHELL-L7 — nothing in this drop has run on Windows.** The menu logic is proven on Linux; whether
  Explorer draws it, and what it does when it does, is `scripts/shell-gate.ps1` and a pair of eyes.
- **SHELL-L8 — the classic surface cannot deliver a multi-selection to one process. FULLY MEASURED
  2026-08-28.** `%*` expands to nothing (`argc 0`); `"%*"` to an empty string; `"%1"` with
  `MultiSelectModel = Player` produces **one process per selected file** — three files, three pids,
  230 ms apart. There is no third spelling. So `SelectedPaths` has no correct registration on this
  surface at all: `%*` gives no archive, and `"%1"` would give three archives instead of one, which
  violates D4/`SHELL-057`. This is why the obvious one-token fix was not applied.
  `IExplorerCommand` receives an `IShellItemArray` and does not have this limit, so ADR-0006's
  mechanism turns out to be **required** for correct multi-selection rather than merely preferred.
  Options and their costs: `docs/DROP08_REAUDIT.md` §20.
- **SHELL-L9 — a greyed entry cannot be expressed by the classic registry.** A key is present or
  absent. Where the capability model would show a line disabled with a reason, the registration either
  omits it (a folder gets no *Compress to GZIP*) or offers it and lets the resolver refuse (three
  files chosen for GZIP). The person sees a refusal after clicking rather than a greyed line before
  it. `IExplorerCommand` is what closes this, per ADR-0006.
