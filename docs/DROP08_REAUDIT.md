# DROP 08 — Re-audit and test contract

**Status: OPEN / RE-AUDIT.** Nothing closed. No implementation performed. No product source modified.
ADR-0006 not modified.

Read against the tree on disk at build stamp `2026-08-27-drop-08-windows-shell-integration`.
Revision 3 — decisions OPEN-1…7 recorded as authoritative, the matrix resolved with no OPEN cells
left, and **Phase 1 executed: five contract tests are red for the correct reasons** (§12).

> **The reference screenshots have still not arrived.** The uploads folder holds only files staged
> from this repository. Every statement about Windows 11 menu *placement* is derived from ADR-0006
> and from the code. Rows that need the images are marked **NEEDS IMAGES**.

---

## 1. Confirmed defects — none downgraded

| # | Defect | Severity | Evidence |
|---|---|---|---|
| **D1** | Unresolved `Compress to {0}` reaches the user | **Ships a broken label** | `shell-verbs.json` line `"label": "Compress to {0}"`, written verbatim to `MUIVerb` |
| **D2** | Registered `Compress to` is functionally invalid — loses the format and input contract | **Action does nothing** | Registers `noba-ar create "%1"`; CLI requires `create <archive> <inputs...>` → `UsageError` |
| **D3** | Registered `Extract here` loses destination semantics | **Writes to the wrong place** | Registers `extract "%1"` with no `-o`; output lands in the process working directory |
| **D4** | Classic `%1` registration cannot represent multi-selection; launches N processes | **Wrong result, silently** | A `%1` verb is invoked once per selected item; three files → three archives |
| **D5** | Classic registration is capability-blind and does not consume the action/capability model | **Whole model bypassed** | `Get-ScopeVerbs` filters on `archiveOnly` only; all six verbs register on `*` |
| **D6** | Windows 11 Modern Context Menu requirement not implemented | **Primary requirement unmet** | No `IExplorerCommand`, no MSIX, no signing. Reclassified from limitation to unmet requirement |
| **D7** | *(new)* GZIP is offered for a single **folder**, which the engine refuses | **Menu promises what the engine denies** | `PermitsInputCount(selection.Count)` counts *selected items*, not files reaching the archive. One folder = count 1 = permitted. `format-gate.ps1` gate 14 `gzip-refuses-many` proves the engine refuses a folder |

**D7 explained.** The single-member rule asks "how many things did you select?" when the question is
"how many members will the archive hold?" A folder is one selection and many members. The rule is
right; its input is wrong. Same family as D5 — the capability model is being asked the wrong
question, or not asked at all.

**The common root of D1–D5 and D7:** the registration composes its own semantics instead of
consuming `ShellLauncher.PlanFor` and `ShellMenuBuilder`.

```powershell
# shell-register.ps1 — the whole problem in one line
return '"{0}" {1} "%1"' -f $exe, $Verb.verb
```

---

## 2. ID conflict report — **STOP AND CONFIRM**

Per the instruction not to renumber silently, here is the conflict.

### No conflict with shipped tests

Shipped IDs are **SHELL-001…037**, contiguous, no gaps. `SHELL-054…059` collide with nothing that
exists. ✅

### Conflict with the *proposed* IDs from revision 1

Revision 1 of this audit proposed `SHELL-038…053` and `SHELL-W1…W3`. **None were written.** Your new
numbering overlaps them:

| Your ID | Subsumes my revision-1 proposals | Action taken |
|---|---|---|
| SHELL-054 | SHELL-038 | Withdrawn in favour of yours |
| SHELL-055 | SHELL-044, 045, 046, 047 | Withdrawn |
| SHELL-056 | SHELL-041 (+ part of 039, 040) | Withdrawn |
| SHELL-057 | SHELL-043 | Withdrawn |
| SHELL-058 | SHELL-042 | Withdrawn |
| SHELL-059 | SHELL-039, 040 | Withdrawn |

**Direct collision on the W series.** My revision-1 W-IDs meant different things:

| ID | Revision 1 meaning (mine) | Your meaning | Resolution |
|---|---|---|---|
| SHELL-W1 | NOBA above "Show more options" | NOBA above "Show more options" | **Identical** — no conflict |
| SHELL-W2 | Every verb runs and produces the right artefact | Modern menu is capability-aware | **CONFLICT** — mine withdrawn; your meaning stands. My meaning moves to your W3 |
| SHELL-W3 | Uninstall removes from both menus | Every action reaches the same model and result | **CONFLICT** — mine withdrawn; your meaning stands. My meaning moves to your W9 |

**Resolution applied:** your numbering is authoritative throughout. My revision-1 IDs are withdrawn
and recorded here only so the mapping is auditable. Nothing was silently renumbered.

### Two gaps your list does not cover — **SHELL-060…065 APPROVED 2026-08-27**

| Proposed ID | Covers |
|---|---|
| SHELL-060 | The selection matrix (§5) asserted as a table, every cell, rather than case by case |
| SHELL-061 | Multiple archives offer no single-archive action — a stated decision, not an accident |
| SHELL-062 | The member-count question is asked correctly: a folder counts as "many", closing D7 |
| SHELL-063 | The modern adapter holds no menu logic — it calls `ShellMenuBuilder` and nothing else |
| SHELL-064 | Adapter and classic handler produce **identical** menus for identical selections |
| SHELL-065 | Package identity and registration metadata derive from `ShellRegistration`, not literals |

### One existing test is satisfied *by* a defect — must be strengthened, not merely supplemented

**SHELL-031** asserted `ShellVerb.InterimLabel == LocalizedStrings[TextKey]`. Both sides were
`"Compress to {0}"`, so it **passed because of D1** — it certified the defect as correct.

**Amended in Phase 1.** Equality is still required, and the label must now also be a finished string.
The assertion was strengthened, never weakened: deleting `{0}` from the string table would leave
"Compress to" naming no format, and SHELL-056 is what makes the placeholder unnecessary.

---

## 3. Test contracts — SHELL-054…059

Documented, not implemented.

### SHELL-054 — Registration → Action Model parity

**Asserts:** for every verb the registration exposes, the *action*, *inputs*, *format*, *destination*
and *argument list* it will actually invoke are equal to what `ShellLauncher.PlanFor` returns for the
corresponding `ArchiveActionRequest`.

**Explicitly not:** comparing labels, or comparing command strings textually. The comparison is over
the parsed structure — executable plus ordered argument list — so a registration that reaches the
same place by a different spelling still passes, and one that reaches a different place fails even if
the string looks plausible.

**Closes:** D2, D3, and the root cause of D1.
**Would have caught:** all three, on the day they were written.

### SHELL-055 — Registration is capability-aware

**Asserts:** the surface the registration exposes for a given selection equals the surface
`ShellMenuBuilder` computes for the same selection, under the same `FormatCapabilityReport`.

Specifically: archive-only actions are absent for ordinary files; no action appears that the
capability model would withhold; no action the model offers is missing.

**Closes:** D5.
**Note:** this is a *set equality*, not a subset check. A registration that shows less than the model
is as wrong as one that shows more.

### SHELL-056 — Format expansion

**Asserts:** `CompressTo` produces **one resolved entry per creatable format**. No generic,
format-less `CompressTo` entry may reach the registration surface, and no entry may carry an
unresolved placeholder.

**Closes:** D1 structurally — the entry cannot exist without a format, so the label cannot lack one.

### SHELL-057 — Multi-selection contract

**Asserts:** a selection of N inputs produces exactly **one** logical request containing all N. It
must never produce N requests, N invocations, or N processes.

**Closes:** D4.
**Platform note:** on the classic surface this requires `MultiSelectModel` and `%*` rather than `%1`.
The test asserts the contract; the mechanism is an implementation choice.

### SHELL-058 — Destination semantics

**Asserts:**
- `Extract here` resolves to the **containing directory of the selected archive**, carried explicitly.
- `Extract to…` preserves explicit-destination semantics — no destination is baked in.
- Neither depends on the process working directory, and the registration layer may not rely on
  Explorer's CWD for any action.

**Closes:** D3.

### SHELL-059 — Resolved label contract

**Asserts:** every label reaching the registration surface is fully resolved, in **both** English and
Arabic. No `{0}`, `{1}` or any `{n}` may appear in a registered label.

**Closes:** D1 at the boundary, as a backstop to SHELL-056's structural fix.
**Also requires:** SHELL-031 amended (§2), or the pair remains satisfiable by the defect.

---

## 4. Windows 11 acceptance criteria — SHELL-W1…W10

**A and B are separate surfaces and are never equivalent.**

| | Surface | Mechanism | Status |
|---|---|---|---|
| **A** | Windows 11 **Modern** context menu, **above** "Show more options" | `IExplorerCommand` + sparse MSIX + signing (ADR-0006) | **Not implemented** |
| **B** | Windows 10 / classic fallback, **under** "Show more options" | `ContextMenuHandlers` registry | Present, defective (D1–D5, D7) |

**B is not an implementation of A.** The shipping requirement is A. B satisfies ADR-0006's
Windows 10 fallback clause and nothing more.

| ID | Acceptance criterion | Surface | Evidence required |
|---|---|---|---|
| **SHELL-W1** | NOBA Archive appears in the Windows 11 modern context menu **before** "Show more options" | A | Screenshot showing NOBA and "Show more options" in one frame, NOBA above |
| **SHELL-W2** | The modern menu exposes the correct **capability-aware** actions for the current selection | A | Screenshot per selection class from §5 |
| **SHELL-W3** | Every displayed action invokes the same action model / workspace path and produces the correct result | A + B | Artefact produced per action, verified on disk |
| **SHELL-W4** | Multi-selection produces **one** logical request | A + B | One archive containing all selected inputs; process count observed |
| **SHELL-W5** | RAR offers read / test / extract and **never** "Create RAR" | A + B | Screenshot of a selected `.rar` submenu |
| **SHELL-W6** | GZIP is not offered for selections violating its single-member rule | A + B | Screenshots: 1 file (offered), 2 files (not), **1 folder (not — see D7)** |
| **SHELL-W7** | BZIP2 / XZ are not advertised anywhere | A + B | Screenshot of the full submenu |
| **SHELL-W8** | Arabic and Unicode paths survive shell → action → operation intact | A + B | Archive built from `مستندات\تقرير سنوي.txt`, contents listed |
| **SHELL-W9** | Uninstall removes **both** the modern and the classic registration, leaving no NOBA residue | A + B | Registry scan + package query, both empty; screenshot of a clean menu |
| **SHELL-W10** | Reinstall is idempotent — no duplicate menu entries | A + B | Screenshot after two installs; entry count unchanged |

**W1 and W2 cannot be satisfied without the packaging track (§7).** W3–W10 are meaningful on B today
and must be re-run on A once it exists.

---

## 5. Selection matrix — the acceptance contract

**This is the contract the implementation must satisfy.** Every cell is decided; nothing is OPEN.

Legend: ✅ shown and enabled · ⛔ not shown · ⚠️ shown, disabled, with a stated reason

| Selection | Open | Test | Extract here | Extract to… | → 7Z | → ZIP | → TAR | → GZIP | Create archive… |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| single normal file | ⛔ | ⛔ | ⛔ | ⛔ | ✅ | ✅ | ✅ | ✅ | ✅ |
| multiple normal files | ⛔ | ⛔ | ⛔ | ⛔ | ✅ | ✅ | ✅ | ⚠️ | ✅ |
| single folder | ⛔ | ⛔ | ⛔ | ⛔ | ✅ | ✅ | ✅ | **⚠️** | ✅ |
| multiple folders | ⛔ | ⛔ | ⛔ | ⛔ | ✅ | ✅ | ✅ | ⚠️ | ✅ |
| file + folder | ⛔ | ⛔ | ⛔ | ⛔ | ✅ | ✅ | ✅ | ⚠️ | ✅ |
| single 7Z | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| single ZIP | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| single TAR | ✅ | ✅ *(structural only)* | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| single GZIP | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| single RAR | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| multiple archives | ⛔ | ⛔ | ⛔ | ⛔ | ✅ | ✅ | ✅ | ⚠️ | ✅ |
| archive + normal files | ⛔ | ⛔ | ⛔ | ⛔ | ✅ | ✅ | ✅ | ⚠️ | ✅ |
| mixed archive formats | ⛔ | ⛔ | ⛔ | ⛔ | ✅ | ✅ | ✅ | ⚠️ | ✅ |

**Rules the matrix encodes, each traceable to a decision:**

- **Archive actions require exactly one archive** (OPEN-1, OPEN-3). Multiple archives, and an archive
  mixed with plain files, are ordinary multi-selections — the model withholds single-archive actions
  because there is one archive or there is not. The shell does no filtering of its own.
- **GZIP is ⚠️ wherever the selection yields more than one member** (OPEN-2). That includes a single
  folder — which is the D7 correction, and the only row that changes from today's behaviour.
- **GZIP is ✅ for a single archive** (OPEN-2): one archive is one member. No special case either way.
- **`Compress to X` on a `.rar` means re-pack that rar inside X.** It is never "Create RAR", which
  never appears because `CanCreate` is false.
- **TAR Test is offered, and must not claim content verification** (OPEN-4). TAR has no
  `ContentIntegrityCheck`; the verdict means "headers are sound". The DROP 05 caveat must reach this
  surface, not only the app.
- **A folder named `x.zip` is a folder**, never an archive.
- **`Create archive…` is always available** when any format is creatable — it is the route to the
  options the quick actions deliberately do not ask.

## 6. Decisions — authoritative, approved 2026-08-27

| # | Decision | Consequence for the contract |
|---|---|---|
| **OPEN-1** | Multiple archives expose **no** Open / Test / Extract. Never launch N operations silently. Compression offered only per the shared capability model. | Matrix row `multiple archives`: ⛔ for all four archive actions. SHELL-061 states it as a decision |
| **OPEN-2** | *Compress to GZIP* on a single archive is governed by the shared capability model. **No archive-specific special case.** | GZIP on one archive follows the same member-count rule as anything else. SHELL-062 fixes the rule's input, not its scope |
| **OPEN-3** | Archive + normal files is a **mixed selection**. The shell passes the complete selection to the shared contract. **No shell-specific filtering.** | Archive actions are withheld because the model withholds them for a non-single-archive selection — not because the shell filtered |
| **OPEN-4** | TAR Test stays available (`CanTest=true`). Verification language must stay honest: structural validation is **not** `ContentIntegrityCheck`. | Matrix `single TAR` / Test = ✅. The DROP 05 caveat (L22) must reach the shell surface too |
| **OPEN-5** | Modern and classic may coexist as compatibility layers, but Windows 11 must not show NOBA **twice**. Modern is shipping; classic is fallback. | SHELL-W10 gains a duplicate-surface clause. Registration must know which surface is live |
| **OPEN-6** | **NOBA Archive is a SUBMENU**, in the Windows 11 modern menu, **above** "Show more options". "Under Show more options" is not equivalent. | SHELL-W1 is placement *and* shape. Settled by the screenshots |
| **OPEN-7** | **No** automatic collision-avoiding rename in DROP 08. Existing naming/collision contract preserved. | `SuggestedArchivePath` unchanged; the engine refuses an occupied target and the overwrite contract decides |

**Nothing in the matrix is now marked OPEN.** Every cell is decided.

## 7. Coverage classification

### Existing DROP 08 tests — what each actually proves

**Model only — never touches registry, manifest-to-registry, or Windows (25 tests)**

| ID | Proves |
|---|---|
| SHELL-001 | One file → one Create request naming it |
| SHELL-002 | Many files → **one** request with all paths, ordered |
| SHELL-003 | A folder is one input; `Backups.zip` as a folder is not an archive |
| SHELL-004 | Files and folders together, order preserved |
| SHELL-005 | Arabic path byte-identical through request and launch plan |
| SHELL-006 | Japanese and Greek likewise |
| SHELL-007 | RAR filtered out of creatable candidates by capability, not name |
| SHELL-008 | Selected RAR yields Open/Test/ExtractHere/ExtractTo, no create-RAR |
| SHELL-009 | One file → GZIP enabled |
| SHELL-010 | Two files → GZIP disabled with `SingleMemberFormat`; request refused |
| SHELL-011 | BZIP2/XZ absent; reports unavailable |
| SHELL-012 | Archive gets extract actions; plain file does not |
| SHELL-013 | Create archive… seeds `CreateViewModel`, section switches, nothing starts |
| SHELL-014 | ExtractHere destination = archive's containing folder, CLI host |
| SHELL-015 | ExtractTo carries null destination, App host |
| SHELL-016 | No `--conflict` argument; `ExtractPlanner.Default` is `Ask` |
| SHELL-017 | No overwrite/force argument; name derived as `report.7z` |
| SHELL-018 | Empty and whitespace selections → empty menu, null request, no throw |
| SHELL-019 | A missing file still produces a well-formed request |
| SHELL-020 | Building a menu and forming a request queue nothing |
| SHELL-021 | Probe called once per selected item, never more — no enumeration |
| SHELL-025 | Menu and Create screen agree for counts 1, 2, 3 |
| SHELL-026 | Containing folder derived from string, roots and UNC included |
| SHELL-028 | Repeated path in a selection seeds one input |
| ARC-060/061 | `Core/Actions` names no engine type and makes no filesystem call |

**App-side seam (3):** SHELL-029 (launcher→parser round trip), SHELL-030 (non-shell command line
refused), SHELL-013.

**Manifest internal consistency (3):** SHELL-022 (ids unique, keys under one root, labels are keys),
SHELL-023 (install/uninstall key sets equal, no ancestor before descendant), SHELL-031 (**satisfied
by D1 — see §2**).

**Localisation (2):** SHELL-027 (both languages carry every label, Arabic is Arabic), SHELL-031.

**Script shape — text inspection, never executed (6):** SHELL-032 (no globbing registry path),
SHELL-033 (self-check), SHELL-034 (reads back, exits non-zero), SHELL-035 (both directions read the
manifest), SHELL-036 (idempotent by construction), SHELL-037 (no interpreter).

**The gap in one sentence:** no test reads the *registered command line* and compares it to what
`ShellLauncher` says that verb should run. That single missing assertion is why D1, D2 and D3 shipped.

### Classification

| Requirement | Status | Note |
|---|---|---|
| Action model | **Covered** | Model proven end to end |
| File / folder / mixed selection | **Covered** | SHELL-001…004 |
| Multiple selection | **Partial** | Model ✅; registration D4 |
| Archive selection | **Partial** | Model ✅; registration D5 |
| 7Z / ZIP / TAR | **Partial** | Quick compress D2 |
| GZIP single-member | **Partial** | Model ✅; D5 + **D7** |
| RAR read-only | **Partial** | Model ✅; registration D5 |
| BZIP2/XZ refusal | **Covered** | Absent from both surfaces |
| Create archive… | **Covered** | SHELL-013, 028, 029 |
| Quick compress | **Missing** | D2 — action does nothing |
| Extract here | **Missing** | D3 — wrong destination |
| Extract to… | **Covered** (model) / **Partial** (Windows) | No Windows run |
| Open archive | **Partial** | No Windows run |
| Test archive | **Partial** | Registered path untested; OPEN-4 |
| Generated archive naming | **Covered** (model) | Unreachable via menu (D2) |
| `{0}` placeholder | **Missing** | D1; SHELL-031 certifies it |
| Arabic / Unicode | **Covered** (model) / **Partial** (Windows) | |
| Argument safety | **Partial** | Model safe; registry uses a command string |
| Install / reinstall / uninstall | **Partial** | Structural only; gate never completed |
| Classic fallback | **Partial** | Registers, but D1–D5, D7 make it wrong |
| **Windows 11 modern menu** | **Blocked** | Packaging + signing |
| **Top-level before "Show more options"** | **Blocked** | Packaging + signing |
| Visual UX | **Missing** | **NEEDS IMAGES** |
| Windows validation | **Missing** | 34-step gate never completed |

**Tally: 8 Covered · 12 Partial · 6 Missing · 2 Blocked.**

---

## 8. The one action contract

The end state, with no second interpretation anywhere:

```
Explorer  (classic registry)          Explorer  (Windows 11 modern)
        │                                      │
        └──────────────┬───────────────────────┘
                       ↓
              platform selection
                       ↓
                ShellSelection
                       ↓
              ShellMenuBuilder          ← the only place a menu is decided
                       ↓
             ArchiveActionRequest       ← the only action contract
                       ↓
                ShellLauncher           ← the only place arguments are formed
                       ↓
                NOBA CLI / App
                       ↓
                 OperationQueue
                       ↓
               handler / provider
```

**The rule:** an adapter may translate the platform's representation of a selection into
`ShellSelection`, and translate a `ShellLaunchPlan` into whatever the platform needs to invoke. It
may **not** decide which entries appear, what they are called, or what arguments they carry. Those
three decisions belong to `ShellMenuBuilder`, `LocalizedStrings` and `ShellLauncher` respectively.

SHELL-054 and SHELL-064 are what hold this. Today the classic registration violates all three.

---

## 9. Packaging track — separate, and not weakened

Kept out of DROP 08, and ADR-0006 stands unamended:

- `IExplorerCommand` COM server
- sparse MSIX with `AllowExternalContent`
- code-signing certificate (ADR-0006: procurement starts at Phase 6)
- WiX MSI
- production install / uninstall, `HKLM\SOFTWARE\NOBA\Archive\InstallPath`, PATH entry, `7z.dll` layout

**A sparse MSIX will not register unsigned.** That is a hard gate, not an estimate.

**And the blocker is not an excuse.** D1–D5 and D7 are defects in the contract that exists today, on
a surface that ships today. They are fixable without any packaging work, and DROP 08 must fix them.

---

## 10. Proposed DROP 08 test plan

Ordered so that each phase's failures are attributable.

**Phase 1 — make the contract testable (no behaviour change)**
1. Amend **SHELL-031** so it cannot be satisfied by an unresolved placeholder.
2. Add **SHELL-059** — resolved labels, both languages.
3. Add **SHELL-054** — registration ↔ `ShellLauncher.PlanFor` parity.

*Expected: SHELL-054 and SHELL-059 fail immediately, and SHELL-031 fails once amended. Those three
red tests are D1, D2 and D3 stated as assertions.*

**Phase 2 — close the structural defects**
4. **SHELL-056** — format expansion (closes D1 structurally).
5. **SHELL-058** — destination semantics (closes D3).
6. **SHELL-057** — multi-selection contract (closes D4).
7. **SHELL-055** — capability-aware registration (closes D5).
8. **SHELL-062** — member-count asked correctly (closes D7). *Needs OPEN-2 decided.*

**Phase 3 — make the matrix a contract**
9. **SHELL-060** — the §5 table, every cell. *Blocked on OPEN-1…OPEN-4.*
10. **SHELL-061** — multiple archives, stated decision. *Blocked on OPEN-1.*

**Phase 4 — Windows gate on surface B**
11. Rewrite `shell-gate.ps1` around SHELL-W3…W10 on the classic surface.
12. Run it to completion. It never has.

**Phase 5 — modern menu (packaging track, not DROP 08)**
13. **SHELL-063, 064, 065** — adapter holds no logic; both surfaces produce identical menus;
    identity derives from `ShellRegistration`.
14. **SHELL-W1, W2** — placement and capability-awareness on surface A.

**Negative controls required before any phase is accepted:** reinstate the composed command string
(SHELL-054 must fail), remove the format dimension (SHELL-056 must fail), drop `-o` (SHELL-058 must
fail), revert to `%1` (SHELL-057 must fail), register all verbs on `*` (SHELL-055 must fail).

---

## 12. Phase 1 results — executed 2026-08-27

**Five tests red. No product code modified.** `git`-equivalent verification: the only files changed
are two test files and this document.

### The failures, verbatim

| Test | Maps to | Failure |
|---|---|---|
| `The_interim_registry_labels_match_the_localized_text` (SHELL-031, amended) | **D1** | `'noba.compress' registers the label "Compress to {0}", which still carries {0}` |
| `Every_registered_label_is_resolved_in_both_languages` (SHELL-059) | **D1** | `en: 'noba.compress' would register "Compress to {0}"` · `ar: 'noba.compress' would register "ضغط إلى {0}"` |
| `Every_registered_verb_matches_the_action_model` (SHELL-054) | **D1 D2 D3** | All six verbs: *the manifest declares no argument contract* |
| `The_registration_script_composes_no_command_semantics_of_its_own` (SHELL-054b) | **D2 D3** | `shell-register.ps1 builds its own command line from the verb name and "%1"` |
| `A_single_folder_is_not_offered_a_single_member_format` (SHELL-062) | **D7** | `GZIP is offered for a single folder` |

### What SHELL-054 exposed, per verb

The parity check prints what the action model says each verb should run. Set against what the
registration actually writes (`"<exe>" <verb> "%1"`), the divergence is explicit:

| Verb | Registration writes | `ShellLauncher` says | Divergence |
|---|---|---|---|
| `noba.open` | `--open "%1"` | `--open <archive>` | none in substance |
| `noba.test` | `test "%1"` | `test <archive>` | none in substance |
| `noba.extracthere` | `extract "%1"` | `extract <archive> -o <containingFolder>` | **destination lost (D3)** |
| `noba.extractto` | `--extract "%1"` | `--extract <archive>` | none in substance |
| `noba.compress` | `create "%1"` | `create <archivePath> <inputs...> --format <id>` | **target, inputs and format lost (D2)** |
| `noba.create` | `--create "%1"` | `--create <inputs...>` | single-input only (**D4**) |

Two verbs agree by accident, not by contract — which is precisely why the test compares structure
rather than trusting that they look right.

### Tests not trivially red

`SHELL-059b` was added alongside, and **passes**: it drives the placeholder detector directly and
asserts it flags `Compress to {0}`, `{0} of {1}` and `ضغط إلى {0}`, while accepting `Compress to 7Z`,
`ضغط إلى 7Z`, `C:\Docs\{report}.txt` and `{}`. A red test that could never go green is an assertion
nobody can satisfy; this shows the redness is evidence about the product.

### Negative controls re-confirmed

Each was reinstated, observed to fire, and restored — both files verified byte-identical afterwards
by sha256.

| Control | Guard that fired |
|---|---|
| Capability lookup removed from `ShellMenuBuilder` | `RAR_is_never_offered_for_creation`, `Unsupported_formats_are_never_offered` |
| Filesystem walk added to menu construction | `The_shell_action_model_does_not_walk_the_filesystem` (ARC-061) **and** `Does_not_touch_the_filesystem_directly(NOBA.Archive.Core)` |
| Duplicate path protection removed | 11 failures across `CreateWorkflowTests`, `DragDropTests`, `ShellIntegrationTests` |

### Suite totals

| | Before Phase 1 | After Phase 1 |
|---|--:|--:|
| Presentation tests | 221 | **226** |
| Passing | 221 | **221** |
| Failing | 0 | **5 (intended)** |

---

## 13. Phase 2 results — executed 2026-08-27

**Every Phase 1 failure is green, and none of them by touching an assertion.** The only assertion
changes in this phase are *additions*: SHELL-055, 056, 057, 057b, 058 as named tests, plus six
resolver tests in `NOBA.Archive.Cli.Tests`.

### Build stamp

`2026-08-27-drop-08-phase-2-shell-action-contract`

### The structural change

One table of command shapes — `Core/Actions/ShellInvocation.cs` — and three consumers that may only
read it:

```
ShellInvocations.For(action)  ──▶  ShellLauncher.PlanFor      fills the slots for a real selection
                              ├─▶  noba-ar shell-manifest     publishes the slots as tokens
                              └─▶  shell-register.ps1         translates tokens to registry syntax
```

`ShellRegistration` no longer describes a verb's command; it names the entry and reads the template.
`shell-register.ps1` no longer builds a command; `Convert-NobaToken` maps one token to one
substitution and **throws on a token it does not know**, so a new argument kind cannot silently
produce a command with an argument missing.

### The two shapes the registry cannot spell

`ContainingFolder` and `SuggestedTarget` are computed from the selection, and a registry command can
only substitute the selection. `ShellInvocations.IsDirectlyRegistrable` marks them, and those entries
register as `noba-ar shell <entry-id> <selection>` — a new CLI verb that rebuilds the selection, asks
`ShellMenuBuilder` the same question the menu asked, and dispatches through `ShellLauncher.PlanFor`
in-process as an argument list. No string is assembled anywhere on the path.

This is what let D2 and D3 close without hardcoding a larger set of registry command strings.

### Defects closed

| Defect | Closed by | Evidence |
|---|---|---|
| **D1** — `{0}` reaches the registry | `ShellRegistration.Entries` expands CompressTo per creatable format | SHELL-031 (amended), SHELL-056, SHELL-059 green; manifest shows `noba.compress.7z` … `noba.compress.zip` |
| **D2** — `Compress to` registers `create "%1"` | resolver verb + the one command table | SHELL-054 green; registration writes `noba-ar shell noba.compress.7z %*` |
| **D3** — `Extract here` loses `-o` | `ContainingFolder` slot, carried through the resolver | SHELL-054, SHELL-058 green; `Extract_here_puts_the_contents_beside_the_archive` runs it end to end |
| **D4** — one process per selected item | `MultiSelectModel` from the command shape; `%*` for whole-selection entries | SHELL-057, SHELL-057b green |
| **D5** — every verb registered on `*` | `ShellScope` decided in Core from `FormatCapabilityReport` | SHELL-055 green; archive verbs scoped to 7 openable extensions |
| **D7** — member count counted clicks | `ShellSelection.MemberCount`, `PermitsMemberCount` | SHELL-062 green |

**D7 needed no filesystem access.** `MemberCount` derives from `ShellItem.IsDirectory`, which Explorer
already supplies — so ARC-061 holds. A folder is `Many` because the true count needs the disk, and
withholding a single-member format from a folder that happened to hold one file is the safe direction.

### Manifest, as generated

| Entry | Scope | Direct | Multi-select | Registry command |
|---|---|---|---|---|
| `noba.open` | Archives | yes | Single | `"<app>" --open "%1"` |
| `noba.test` | Archives | yes | Single | `"<cli>" test "%1"` |
| `noba.extracthere` | Archives | **no** | Single | `"<cli>" shell noba.extracthere "%1"` |
| `noba.extractto` | Archives | yes | Single | `"<app>" --extract "%1"` |
| `noba.compress.7z` | Files, Folders | **no** | Player | `"<cli>" shell noba.compress.7z %*` |
| `noba.compress.gzip` | **Files only** | **no** | Player | `"<cli>" shell noba.compress.gzip %*` |
| `noba.compress.tar` | Files, Folders | **no** | Player | `"<cli>" shell noba.compress.tar %*` |
| `noba.compress.zip` | Files, Folders | **no** | Player | `"<cli>" shell noba.compress.zip %*` |
| `noba.create` | Files, Folders | yes | Player | `"<app>" --create %*` |

`Archives` expands to `SystemFileAssociations\.7z .gz .r00 .rar .tar .zip .zipx` — the extensions this
build reports `CanOpen` for.

### Negative controls — each broken, observed red, restored, sha256-verified

| Control | Test that fired | Restored |
|---|---|---|
| Reinstate the composed command string | `The_registration_script_composes_no_command_semantics_of_its_own` (SHELL-054b) | byte-identical |
| Remove the format dimension | SHELL-031, SHELL-054, SHELL-056, SHELL-057, SHELL-059 — 6 failures | byte-identical |
| Drop `-o` from `ExtractHere` | SHELL-054, SHELL-054c, SHELL-058, `Extract_here_targets_the_archives_own_folder`, and the CLI end-to-end test | byte-identical |
| Revert `%*` to `%1` and `Player` to `Document` | `The_registration_script_maps_a_whole_selection_to_a_whole_selection` (SHELL-057b) | byte-identical |
| Register every verb on `*` and `Directory` | SHELL-055 — 8 offences named, per selection and per entry | byte-identical |
| Count clicks instead of members | SHELL-062 **and** SHELL-055 | byte-identical |

**One control found a real gap.** Reverting `%*` to `%1` initially broke nothing: SHELL-057 held the
model and the manifest but nothing held the script's translation of them. `SHELL-057b` was added for
exactly that, and the control then fired. The gap was found by breaking the code, not by reading it.

### Suite totals

| Suite | Phase 1 | Phase 2 |
|---|--:|--:|
| Core | 95 | 95 |
| Security | 41 | 41 |
| Application | 35 | 35 |
| Infrastructure | 19 | 19 |
| Formats | 18 | 18 |
| Integration | 161 (+6 skipped) | 161 (+6 skipped) |
| Architecture | 67 | 67 |
| CLI | 26 | **32** |
| Parity | 23 | 23 |
| Presentation | 226 (5 red) | **232** |
| **Total passing** | 706 | **723** |
| **Failing** | 5 (intended) | **0** |

Six Integration skips are `MarkOfTheWebTests` — NTFS alternate data streams, which do not exist on
this filesystem, declared as skips rather than silently passing.

### Unnumbered tests — decision needed

Two additions carry no id from the approved SHELL-054…065 range, and are reported rather than
renumbered into somebody else's range:

- **`ShellResolverTests`** (six tests, `NOBA.Archive.Cli.Tests`) — the resolver verb end to end. The
  resolver is the mechanism Phase 2 needed; no id was allocated for it.
- **`SHELL-057b`** — the script's translation of the multi-selection contract. Named with the `b`
  suffix convention already used for SHELL-054b / SHELL-054c.

### What was NOT done

- ADR-0006 untouched.
- No MSIX, no `IExplorerCommand`, no COM server, no WiX. SHELL-063/064/065 remain Phase 5.
- `shell-gate.ps1` not rewritten around SHELL-W3…W10 — that is Phase 4.
- **Nothing here ran on Windows.** Every result above is from the Linux build machine. `%*` and
  `MultiSelectModel` are the documented mechanism and are recorded as unverified (SHELL-L8).

### One latent issue, unchanged and reported

`CreateViewModel.Blocker` still calls `Options.PermitsInputCount(_inputs.Count)`. The Create screen
therefore has the same D7 shape: a folder added there counts as one member. `_inputs` is a
`List<string>` with no directory knowledge, so correcting it means changing what DROP 06 stores.
Left alone rather than silently widening this drop's scope.

---

## 14. Contract reconciliation — executed 2026-08-27, after Phase 2 acceptance

No Phase 3 work. Three test additions, **one product-code change of any kind: none.** The single
behavioural question raised (item 5) turned out to be already satisfied and is now asserted rather
than altered.

### 14.1 Test inventory — the unnumbered tests are numbered

New ids allocated **after SHELL-065**. Nothing in SHELL-054…065 was renumbered or reused.

| ID | Test | Project | Holds |
|---|---|---|---|
| **SHELL-066** | `Extract_here_puts_the_contents_beside_the_archive` | CLI | D3 end to end: the payload lands beside the archive, not in the process CWD |
| **SHELL-067** | `Compressing_several_files_to_a_single_member_format_is_refused_with_its_reason` | CLI | the member rule at the boundary the registry cannot express; refusal names the rule, nothing written |
| **SHELL-068** | `Compressing_several_files_to_a_multi_member_format_produces_one_archive` | CLI | the control for 067 — and D4: **one** archive from N inputs |
| **SHELL-069** | `An_archive_verb_on_a_plain_file_is_refused` | CLI | an archive verb reached on a non-archive refuses rather than tries |
| **SHELL-070** | `An_unknown_entry_id_is_a_usage_error` | CLI | an unregistered entry id is an error, not a silent no-op |
| **SHELL-071** | `The_resolver_requires_an_entry_and_a_selection` | CLI | neither the entry nor the selection is inferred |
| **SHELL-072** | `The_registration_script_maps_a_whole_selection_to_a_whole_selection` | Presentation | the script's *translation* of the multi-selection contract (was the working name SHELL-057b) |
| **SHELL-073** | `A_selection_that_did_not_survive_substitution_writes_nothing` | CLI | **new in this reconciliation** — a malformed `%*` writes nothing |

`SHELL-054b` and `SHELL-054c` keep their Phase-1 working names: they were approved under those names
and renaming them now would break the Phase 1 record for no gain. Only SHELL-057b was renumbered,
because it was created in Phase 2 and never carried an approved id.

### 14.2 D7 reconciled — the complete selection matrix

**Measured, not reasoned.** Every row below was produced by driving the real `ShellMenuBuilder` with
the real provider registry of this build. Capabilities as reported by the build:

| Format | Open | Test | Extract | Create | AddEntries |
|---|:--:|:--:|:--:|:--:|:--:|
| 7Z | ✅ | ✅ | ✅ | ✅ | ✅ |
| ZIP | ✅ | ✅ | ✅ | ✅ | ✅ |
| TAR | ✅ | ✅ | ✅ | ✅ | ✅ |
| GZIP | ✅ | ✅ | ✅ | ✅ | ❌ |
| RAR | ✅ | ✅ | ✅ | ❌ | ❌ |

Cells: **✅** offered · **⚪** shown greyed with `SingleMemberFormat` · **⛔** absent from the menu
entirely.

| Selection | Count | Effective members | 7Z | ZIP | TAR | GZIP | RAR | Archive actions |
|---|:--:|---|:--:|:--:|:--:|:--:|:--:|---|
| single file | 1 | `ExactlyOne` | ✅ | ✅ | ✅ | ✅ | ⛔ | none |
| multiple files | 3 | `Many` | ✅ | ✅ | ✅ | ⚪ | ⛔ | none |
| single folder | 1 | **`Many`** | ✅ | ✅ | ✅ | ⚪ | ⛔ | none |
| multiple folders | 2 | `Many` | ✅ | ✅ | ✅ | ⚪ | ⛔ | none |
| file + folder | 2 | `Many` | ✅ | ✅ | ✅ | ⚪ | ⛔ | none |
| single 7Z | 1 | `ExactlyOne` | ✅ | ✅ | ✅ | ✅ | ⛔ | Open, Test, Extract here, Extract to |
| single ZIP | 1 | `ExactlyOne` | ✅ | ✅ | ✅ | ✅ | ⛔ | Open, Test, Extract here, Extract to |
| single TAR | 1 | `ExactlyOne` | ✅ | ✅ | ✅ | ✅ | ⛔ | Open, Test, Extract here, Extract to |
| single GZIP | 1 | `ExactlyOne` | ✅ | ✅ | ✅ | ✅ | ⛔ | Open, Test, Extract here, Extract to |
| single RAR | 1 | `ExactlyOne` | ✅ | ✅ | ✅ | ✅ | ⛔ | Open, Test, Extract here, Extract to |
| multiple archives | 2 | `Many` | ✅ | ✅ | ✅ | ⚪ | ⛔ | **none** |
| archive + normal file | 2 | `Many` | ✅ | ✅ | ✅ | ⚪ | ⛔ | **none** |
| mixed archive formats | 3 | `Many` | ✅ | ✅ | ✅ | ⚪ | ⛔ | **none** |

**Every row is consistent with the decisions already made.**

- **RAR is `⛔` everywhere**, not greyed. A format that can never be created has no reason to appear
  and be explained; that is `CanCreate`, not a selection rule, and it is why the RAR column never
  changes down the table.
- **`single GZIP` offers *Compress to GZIP*.** That is OPEN-2 working as decided: one `.gz` file is
  one member, and re-compressing an archive is a legitimate thing to want. No archive-specific case.
- **`single RAR` offers all four archive actions and every creatable format.** OPEN-3: a RAR is an
  archive for reading and an ordinary file for compressing, decided by capability in both directions.
- **`multiple archives`, `archive + normal file` and `mixed archive formats` offer no archive
  action.** OPEN-1 and OPEN-3: the model withholds them because `SingleArchive` is null, not because
  the shell filtered the selection. The complete selection is passed through untouched.
- **`single folder` is the D7 row.** `Many` from one click. The old rule answered `ExactlyOne` here
  and offered GZIP, which the engine then refused.

**The member count is never determined by touching the filesystem.** `ShellSelection.MemberCount`
reads `ShellItem.IsDirectory` — a fact Explorer supplies with the selection — and nothing else.
`ARC-061` is unchanged and still fails the build on any filesystem call under `Core/Actions`.

**The architectural limitation, stated rather than worked around.** For a folder, the *true* member
count cannot be known without enumerating it, and menu construction may not enumerate. The contract
therefore does not claim to know it: `Many` is the honest answer to "one or more than one?" when the
answer is unavailable, and it is the safe direction. The cost is one false negative — a folder holding
exactly one file is refused GZIP from the context menu — and the person reaches it through *Create
archive…*, where the inputs are visible. No heuristic was invented; nothing samples, guesses from the
name, or probes.

### 14.3 The Create screen's D7 boundary — documented, not crossed

`CreateViewModel.Blocker` still calls `Options.PermitsInputCount(_inputs.Count)`. **Unchanged in this
drop**, deliberately: `_inputs` is a `List<string>` created by DROP 06 with no record of which entries
are folders, so correcting it changes what DROP 06 stores and what its drop-intake produces.

**There is no second capability rule.** `PermitsInputCount` is not a parallel implementation — it is a
thin adapter over the same method:

```csharp
public bool PermitsInputCount(int count) =>
    count > 0
    && PermitsMemberCount(count == 1 ? ArchiveMemberCount.ExactlyOne : ArchiveMemberCount.Many);
```

One rule, `PermitsMemberCount`, with two entry points. The int-taking one carries an **assumption**:
that one input means one member. That assumption is true for files and false for folders — which is
exactly D7, still present on the Create screen and absent from the shell.

**The future contract required**, when DROP 06 is next opened:

1. `CreateViewModel` stores inputs with their directory flag, not as bare strings — the same
   `(path, isDirectory)` pair `ShellItem` already carries.
2. `Blocker` calls `PermitsMemberCount` with a count derived the same way `ShellSelection.MemberCount`
   derives it.
3. `PermitsInputCount` is then deleted, not left as a convenience — while it exists, a caller can ask
   the wrong question and get a plausible answer.
4. `SHELL-025` (menu and Create screen agree for the same selection) is extended to cover a folder,
   which is the case it cannot currently express.

Until then the divergence is: **the shell is correct, the Create screen is not**, and the Create
screen's error is the permissive one. Recorded here so it is a known debt rather than a discovery.

### 14.4 Phase 2 architecture — verified, with evidence

| Claim | How it was checked | Result |
|---|---|---|
| `shell-register.ps1` constructs no action semantics | grep for every verb name, action name, format name and capability term in the script | **no matches** |
| The script reads only registration data | enumerate every `$spec.*` / `$Entry.*` field it touches | `menuId`, `menuTitle`, `entries`, `resolverVerb`, `installPathValue`, the two executables; per entry `id`, `label`, `arguments`, `executable`, `directlyRegistrable`, `multiSelect`, `scopes` — **no capability field, no verb field** |
| One command/action shape only | grep `ShellArgumentKind` / `ShellInvocationTemplate` across `src/` | defined in `ShellInvocation.cs`; `ShellLaunchPlan.cs`'s six references are `case` labels in the slot-filler; `ShellRegistration.cs`'s one reference carries the template. **No second definition.** |
| Registration is an adapter | `Convert-NobaToken` is total and throws on an unknown token; `SHELL-054b` and `SHELL-072` assert composition and faithful translation | holds |
| The resolver returns to the same model | `noba-ar shell` → `ShellSelection` → `ShellMenuBuilder.Build` → `RequestFor` → `ShellLauncher.PlanFor` → `RunAsync(plan.Arguments)` **in the same process** → the same handler the CLI verb uses | holds; `SHELL-066` proves it end to end by finding the extracted file |

One asymmetry worth naming: `ShellCommandLine.Parse` — the receiving end for App-hosted verbs —
matches on `ShellRegistration.Verbs` rather than on a literal, so a renamed verb is followed on both
sides. It defines no argument list of its own.

### 14.5 What `%*` is expected to deliver — and what happens if it does not

**Expected:** for a key carrying `MultiSelectModel = Player`, Explorer invokes the command **once** and
substitutes `%*` with **every selected path, as separate arguments**, each quoted by the shell where it
needs quoting. The resolver therefore expects `argv` to be `shell <entry-id> <path1> <path2> …`.

**This has not been observed on Windows from this environment and is not claimed to work**
(SHELL-L8). It is the documented mechanism; whether Explorer honours it here is what
`scripts/shell-gate.ps1` must measure, and it is the first thing to check on the next Windows run.

**Fail-safe behaviour, measured on the three ways it can go wrong:**

| What arrived | Result | Written |
|---|---|---|
| one argument holding every path run together | `IoFailure` (exit 9) — the engine refuses a path that is not there | nothing |
| no arguments at all | `UsageError` (exit 1) — the resolver refuses before doing anything | nothing |
| a path that does not exist | `IoFailure` (exit 9) | nothing |

**No second existence check was added.** The refusal comes from the engine's own path validation,
which is the single authority on whether an input is usable; the resolver's `Directory.Exists` call
classifies an item as a folder and does not vet it. Adding a check in the resolver would be a second
input-validation rule, which is the class of duplication this drop exists to remove.

`SHELL-073` now asserts all three, and its negative control — a "helpful" fallback to the containing
folder for a path that is not there — makes it fail, because that fallback would compress the whole
folder from a malformed selection.

### 14.6 The shipping target, recorded

**DROP 08 ships when NOBA Archive appears in the Windows 11 MODERN context menu, above "Show more
options", as the "NOBA Archive" submenu** (OPEN-6). That requirement is unchanged and mandatory.

**Phase 2 does not implement it, and nothing here should be read as implementing it.** What Phase 2
produced is the classic `ContextMenuHandlers` registration, which lands **under** "Show more options"
on Windows 11. That is compatibility and fallback only. ADR-0006 remains the plan of record and
remains unchanged: a WiX MSI plus a signed sparse MSIX package hosting an `IExplorerCommand` server.
No MSIX, no `IExplorerCommand`, no COM server and no WiX exist in this repository, and none were
started.

The work that closes it is SHELL-063, SHELL-064, SHELL-065 and SHELL-W1/W2, all of which remain
Phase 5 on the packaging track.

### 14.7 Regression after reconciliation

| Suite | Discovered | Ran | Passed | Failed | Skipped |
|---|--:|--:|--:|--:|--:|
| Core | 95 | 95 | 95 | 0 | 0 |
| Security | 41 | 41 | 41 | 0 | 0 |
| Application | 35 | 35 | 35 | 0 | 0 |
| Infrastructure | 19 | 19 | 19 | 0 | 0 |
| Formats | 18 | 18 | 18 | 0 | 0 |
| **Integration** | 167 | 161 | 161 | 0 | **6** |
| **Architecture** | 67 | 67 | 67 | 0 | 0 |
| **CLI** | 33 | 33 | 33 | 0 | 0 |
| Parity | 23 | 23 | 23 | 0 | 0 |
| **Presentation** | 232 | 232 | 232 | 0 | 0 |
| **Total** | **730** | **724** | **724** | **0** | **6** |

The six skips are `MarkOfTheWebTests` — NTFS alternate data streams, which do not exist on this
filesystem. Declared as skips rather than passing on a platform that cannot exercise them.

`analyzer-preflight`: **PASS** — 12 source files and 10 markup files against 284 project types, no
CS0103/CS0104/CS1061/CA1822/CA1859/shadowing risk. Solution build: **0 warnings, 0 errors**, no
suppressions.

### 14.8 Negative controls — re-run, all fired, all restored

| Control | Test that fired | Restored |
|---|---|---|
| Reinstate the composed command string | `SHELL-054b` | sha256 identical |
| Remove the format dimension | SHELL-031, 054, 056, 057, 059 — 6 failures | sha256 identical |
| Drop `-o` from `ExtractHere` | SHELL-054, 054c, 058, `Extract_here_targets_the_archives_own_folder`, SHELL-066 | sha256 identical |
| Revert `%*` to `%1`, `Player` to `Document` | `SHELL-072` | sha256 identical |
| Register every verb on `*` and `Directory` | `SHELL-055` — 8 offences named | sha256 identical |
| Count clicks instead of members | `SHELL-062` **and** `SHELL-055` | sha256 identical |
| **New:** fall back to the containing folder for a missing path | `SHELL-073` | sha256 identical |

### 14.9 Phase 3 gate

**Phase 3 is READY.**

- Both blockers named in §10 are cleared: OPEN-1…OPEN-4 were decided before Phase 2, and the matrix
  in §14.2 is measured, complete and internally consistent — SHELL-060 has a table to assert and
  SHELL-061 has a stated decision to encode.
- No inconsistency was found between the model, the registration and the manifest.
- The one open divergence (§14.3, the Create screen) is on the DROP 06 side of a boundary Phase 3 does
  not cross, and is not a second capability rule.
- Phase 3 remains **not started**.

Phase 3 would be: SHELL-060 (the §14.2 table as a contract, every cell) and SHELL-061 (multiple
archives offer no single-archive action, as a stated decision rather than an emergent one). Both are
test-only; neither requires a product change on the evidence above.

---

## 15. Phase 3 results — executed 2026-08-27

**Phase 3 is BLOCKED. One semantic mismatch was found, and it is not patched.**

Contract validation only. Six new tests, no product code touched. Five of the six pass; the sixth
found a real defect and is left red, per the stop condition.

### 15.1 Tests added

| ID | Test | Project | What it holds |
|---|---|---|---|
| **SHELL-060** | `The_selection_matrix_holds_in_every_cell` | Presentation | 13 rows × 5 formats + 13 member counts + archive-action column, every cell stated |
| **SHELL-061** | `Several_archives_offer_no_single_archive_action` | Presentation | OPEN-1 as a decision, and OPEN-3's mechanism: withheld by the model, not by filtering |
| **SHELL-074** | `Every_action_accepts_what_it_should_and_refuses_what_it_should_not` | Presentation | per-action contract: 10 actions × the selections each accepts and refuses, both directions |
| **SHELL-075** | `No_action_inherits_its_destination_from_the_working_directory` | Presentation | the three destination shapes, plus: no plan for any action may carry a relative path |
| **SHELL-076** | `Each_compress_format_reaches_execution_as_itself` | Presentation | four formats produce four distinct plans; `--format` is explicit, never inferred from the name |
| **SHELL-077** | `A_format_no_provider_claims_is_offered_nowhere_and_launches_nothing` | Presentation | BZIP2/XZ unadvertised and unlaunchable even when offered to the builder; RAR never creatable |

`SHELL-074`…`SHELL-077` are newly allocated, after `SHELL-073`. Nothing was renumbered and no existing
test was deleted. Where an existing test already held a requirement it was left alone and is cited in
§15.5 rather than duplicated.

### 15.2 The mismatch — SHELL-075

**Failing contract**

```
a folder name is not a file name with an extension:
  expected 'C:\D\Project.Alpha.zip', got 'C:\D\Project.zip'
```

**Reproduced end to end**, not inferred from reading:

```
$ noba-ar shell noba.compress.zip /tmp/pr/Project.Alpha
Created /tmp/pr/Project.zip: 1 entr(ies), 140 B from 2 B

$ noba-ar shell noba.compress.zip /tmp/pr/Project.Beta
IoFailure: '/tmp/pr/Project.zip' already exists.
```

Two different folders propose the **same** archive name. The second is refused by the overwrite
contract — correctly, and with a message naming a file the person never asked for, about a folder they
never compressed.

**Source of divergence.** `ShellLauncher.SuggestedArchivePath` strips a trailing extension whenever
`Inputs.Count == 1`:

```csharp
var stem = request.Inputs.Count == 1 ? LeafName(request.Inputs[0]) : LeafName(folder);
...
// A single file keeps its base name: report.pdf becomes report.7z, not report.pdf.7z. A
// folder keeps all of its name, because "Project.Alpha" is not "Project" with an extension.
if (request.Inputs.Count == 1)
{
    var dot = stem.LastIndexOf('.');
    if (dot > 0) { stem = stem[..dot]; }
}
```

The comment states the correct rule. The code does not implement it — it cannot, because
`ArchiveActionRequest.Inputs` is `IReadOnlyList<string>` and the request has no way to know whether an
input is a folder. **The `IsDirectory` fact is dropped at the `ShellMenuBuilder.RequestFor`
boundary**, and `SuggestedArchivePath` then substitutes a guess for it: *one input, therefore a file*.

**This is D7 again, one layer further down.** D7 was `PermitsInputCount(count)` guessing members from
clicks. This is `SuggestedArchivePath` guessing file-ness from count. Identical shape, identical
cause: a collection of bare paths that lost the flag Explorer already supplied. It is also the same
root as the Create-screen debt in §14.3.

**Smallest correct architectural fix.** Carry the fact instead of guessing it — no new information is
required and nothing needs the disk:

1. `ArchiveActionRequest` carries its inputs with their directory flag (the `ShellItem` pair
   `ShellSelection` already holds), rather than as bare strings.
2. `SuggestedArchivePath` strips a trailing extension only for an input that is **not** a directory.
3. `ARC-061` is unaffected — the flag comes from Explorer through `ShellSelection`; nothing is probed.
4. The same change is what §14.3 requires for `CreateViewModel`. **One change closes both.**

Rejected alternatives, for the record: *strip nothing ever* breaks `report.pdf → report.7z`, which
SHELL-017 asserts and which is the common case; *probe the disk in `SuggestedArchivePath`* violates
ARC-061 and is unnecessary when the fact is already known upstream.

**Not patched.** This is a change to `ArchiveActionRequest` — the action model — which Phase 3 was
explicitly told not to redesign. SHELL-075 stays red and states the correct contract. Weakening it to
green would encode `Project.Alpha → Project.zip` as intended behaviour.

### 15.3 Registration ↔ action model parity — all nine attributes

| Attribute | Held by | Result |
|---|---|---|
| action identity | SHELL-054 (per entry, against `ShellInvocations`) | ✅ |
| label | SHELL-031 (amended), SHELL-059, SHELL-059b | ✅ |
| format | SHELL-056, SHELL-076 | ✅ |
| scope | SHELL-055 | ✅ |
| input contract | SHELL-074, SHELL-060 | ✅ |
| multi-selection contract | SHELL-057, SHELL-072 | ✅ |
| destination semantics | SHELL-058, **SHELL-075** | ❌ **one cell** |
| argument semantics | SHELL-054, SHELL-054b | ✅ |
| capability requirement | SHELL-055, SHELL-077, SHELL-025 | ✅ |

The resolver's return path — `ShellSelection` → `ShellMenuBuilder` → `ArchiveActionRequest` →
`ShellLauncher` → the common execution path — is held by SHELL-066 end to end and was verified
structurally in §14.4. **One semantic authority**: `ShellInvocations` is the only definition of a
command shape, confirmed by grep across `src/`.

### 15.4 Every action

| Action | Valid selection | Invalid selection | Unsupported capability | Multi-selection | Resolver result | Refusal |
|---|---|---|---|---|---|---|
| Open | one archive | plain file, folder, many files, many archives, empty | absent if `!CanOpen` | hidden (`Single`) | n/a — directly registrable | absent from menu |
| Test | one archive | as above | absent if `!CanTest` | hidden (`Single`) | direct | absent |
| Extract here | one archive | as above | absent if `!CanExtract` | hidden (`Single`) | **resolver**, exit 0 | absent |
| Extract to… | one archive | as above | absent if `!CanExtract` | hidden (`Single`) | direct | absent |
| Compress to 7Z | anything non-empty | empty | absent if `!CanCreate` | one request, all inputs | **resolver**, exit 0 | — |
| Compress to ZIP | anything non-empty | empty | absent if `!CanCreate` | one request, all inputs | **resolver**, exit 0 | — |
| Compress to TAR | anything non-empty | empty | absent if `!CanCreate` | one request, all inputs | **resolver**, exit 0 | — |
| Compress to GZIP | one file, or one archive | folder, many files, many archives, empty | absent if `!CanCreate` | greyed in menu; **resolver refuses, exit 3** | **resolver**, exit 3 with the rule named | "This format holds a single file" |
| Compress to RAR | **never** | every selection | `!CanCreate` — permanent (ADR-0004) | n/a | n/a | absent from menu |
| Create archive… | anything non-empty | empty | absent if nothing creatable | one request, all inputs | direct | — |

Held by SHELL-074 (accept/refuse), SHELL-060 (per selection), SHELL-067/068 (resolver refusal and its
control), SHELL-069 (archive verb on a non-archive), SHELL-076 (format distinctness).

### 15.5 Requirements already held — not duplicated

| Requirement | Existing test |
|---|---|
| RAR offers Open/Test/Extract, never Compress to RAR | SHELL-007, SHELL-008 |
| BZIP2/XZ appear nowhere | SHELL-011 |
| one file → one request; N files → one request with all N | SHELL-001, SHELL-002, SHELL-057 |
| Extract here → containing folder | SHELL-014, SHELL-058, SHELL-066 |
| Extract to… → no baked-in destination | SHELL-015, SHELL-058 |
| single-member rule has one implementation | SHELL-025 |
| the shell adds no conflict policy | SHELL-016, SHELL-017 |
| no `cmd.exe`, no `powershell`, structured arguments only | SHELL-024 |
| menu construction touches no disk | SHELL-020, SHELL-021, ARC-061 |
| duplicate paths fold to one input | SHELL-028 |

### 15.6 Negative controls — all seven fired, all restored

Each mutation was applied, the solution rebuilt, the suites run, the mutation reverted and the file
verified sha256-identical.

| Mutation | Tests that fired |
|---|---|
| `shell-register.ps1` composes a command independently | SHELL-054b |
| `%*` → `%1`, `Player` → `Document` | SHELL-072 |
| format dimension removed | SHELL-031, 054, 056, 057, 059 · **SHELL-067, SHELL-068** (CLI) |
| Extract here loses its destination | SHELL-014, 054, 054c, 058 · **SHELL-066** (CLI) |
| capability filtering bypassed | SHELL-007, SHELL-011, **SHELL-077** |
| GZIP folder treated as a single member | SHELL-062, SHELL-055, **SHELL-060, SHELL-074** |
| duplicate selection protection defeated | SHELL-028, `CreateWorkflowTests`, 4 × `DragDropTests` |

No `#pragma`, no `NoWarn`, no suppression and no timing assertion exists in the product tree; the
mutation for the last control was written to compile without one, and every file is byte-identical to
its pre-mutation state.

### 15.7 Regression

| Suite | Discovered | Ran | Passed | Failed | Skipped |
|---|--:|--:|--:|--:|--:|
| Core | 95 | 95 | 95 | 0 | 0 |
| Security | 41 | 41 | 41 | 0 | 0 |
| Application | 35 | 35 | 35 | 0 | 0 |
| Infrastructure | 19 | 19 | 19 | 0 | 0 |
| Formats | 18 | 18 | 18 | 0 | 0 |
| Integration | 167 | 161 | 161 | 0 | 6 |
| Architecture | 67 | 67 | 67 | 0 | 0 |
| CLI | 33 | 33 | 33 | 0 | 0 |
| Parity | 23 | 23 | 23 | 0 | 0 |
| **Presentation** | 238 | 238 | **237** | **1** | 0 |
| **Total** | **736** | **730** | **729** | **1** | **6** |

Build: 0 warnings, 0 errors. `analyzer-preflight`: PASS. The single failure is SHELL-075 (§15.2).

### 15.8 The four gates, kept apart

| Gate | Status | What it means |
|---|---|---|
| **MODEL VERIFIED** | ⚠️ **ONE FAILURE** | `ShellSelection` → `ShellMenuBuilder` → `ArchiveActionRequest` → `ShellLauncher` is asserted as a table. Every contract passes except the suggested archive name for a single folder (SHELL-075). |
| **REGISTRATION VERIFIED** | ✅ | The manifest and `shell-register.ps1` mean what the model means: argument semantics, scope, labels, multi-selection model, resolver routing. SHELL-054, 054b, 054c, 055, 056, 057, 072. |
| **WINDOWS CLASSIC RUNTIME VERIFIED** | ❌ **NOT VERIFIED** | Nothing in DROP 08 has run on Windows. Explorer has not drawn the menu; `%*` and `MultiSelectModel` have never been observed (SHELL-L8). `scripts/shell-gate.ps1`, 34 steps, has never completed. |
| **WINDOWS 11 MODERN MENU VERIFIED** | ❌ **NOT IMPLEMENTED** | See §15.9. |

These are four gates. Two are unmet, and nothing above should be read as a single PASS.

### 15.9 The shipping requirement, restated

**DROP 08 ships when NOBA Archive appears in the Windows 11 MODERN context menu, as the "NOBA
Archive" submenu, visible ABOVE "Show more options"** (OPEN-6).

**Not implemented, and not implemented by Phase 3.** What exists is the classic
`ContextMenuHandlers` registration, which appears **under** "Show more options" on Windows 11. That is
the compatibility/fallback surface only.

ADR-0006 remains the plan of record and is **unchanged**: a WiX MSI for the unpackaged application
plus a signed sparse MSIX package hosting an `IExplorerCommand` server. No MSIX, no `AppxManifest`,
no COM server and no WiX project exist in this repository, and Phase 3 started none of them. The work
that closes this gate is SHELL-063, SHELL-064, SHELL-065 and SHELL-W1/W2 — Phase 5, packaging track.

### 15.10 Gate

**PHASE 3 = BLOCKED.** One semantic mismatch, reported in §15.2 with its source and its smallest
correct fix, not patched around.

**DROP 08 is NOT ready for Phase 4** until SHELL-075 is resolved. Phase 4 is the Windows classic
runtime gate, and taking a known-wrong archive name onto Windows would spend a manual 34-step run
proving something already known to be broken.

What is needed is a decision, not more analysis: **approve carrying the directory flag on
`ArchiveActionRequest`** (§15.2, the three-step fix) as a scoped change, which also clears the
Create-screen debt in §14.3. It is an action-model change, so it is yours to authorise.

---

## 16. SHELL-075 — authorised fix, executed 2026-08-27

**PHASE 3 = PASS. PHASE 4 = READY.** SHELL-075 is green. No new abstraction, no filesystem probe.

Build stamp: `2026-08-27-drop-08-phase-3-directory-identity`

### 16.1 Files changed

| File | Change |
|---|---|
| `Core/Actions/ArchiveActionRequest.cs` | `Inputs` becomes `IReadOnlyList<ShellItem>`; `PrimaryInput` reads `.Path`; `InputPaths` added for callers that only ever wanted paths |
| `Core/Actions/ShellMenuBuilder.cs` | `RequestFor` hands over `selection.Items` instead of `selection.Paths` |
| `Core/Actions/ShellLaunchPlan.cs` | slot-filling reads `.Path`; `SuggestedArchivePath` strips an extension only when the single input is **not** a directory |
| `Core/Actions/ShellCommandLine.cs` | builds items from `argv`, recording `IsDirectory: false` because a command line carries paths only |
| `Presentation/Interaction/ShellRequestSeed.cs` | seeds `AddInputs(request.InputPaths)` |
| `Cli/BuildStamp.cs` | bumped |
| `tests/…/ShellIntegrationTests.cs`, `ShellRegistrationParityTests.cs`, `ShellActionContractTests.cs` | 12 mechanical `request.Inputs` → `request.InputPaths` updates, no assertion changed |
| `tests/…/ShellActionContractTests.cs` | SHELL-078, SHELL-078b added |

No product file outside `Core/Actions` and the one `ShellRequestSeed` line was touched. `CreateViewModel`
is unchanged.

### 16.2 The type change

```diff
- public required IReadOnlyList<string>    Inputs { get; init; }
+ public required IReadOnlyList<ShellItem> Inputs { get; init; }
+ public IReadOnlyList<string> InputPaths => [.. Inputs.Select(static i => i.Path)];
```

`ShellItem` already existed and already carried `(Path, IsDirectory, Format)`. Nothing new was
introduced — the request now **holds what the selection held** instead of a projection of it, so
`ShellSelection` → `ArchiveActionRequest` → `ShellLauncher` → `SuggestedArchivePath` carries one
identity end to end and nobody re-derives it.

```diff
- if (request.Inputs.Count == 1)          // "one input, therefore a file"
+ if (only is { IsDirectory: false })     // read, not inferred
  {
      var dot = stem.LastIndexOf('.');
      if (dot > 0) { stem = stem[..dot]; }
  }
```

### 16.3 SHELL-075, before and after

| | Before | After |
|---|---|---|
| `Project.Alpha` (folder) → ZIP | `Project.zip` | **`Project.Alpha.zip`** |
| `Project.Beta` (folder) → ZIP | `Project.zip` — **collision**, refused as "already exists" | **`Project.Beta.zip`** |
| `report.pdf` (file) → ZIP | `report.zip` | `report.zip` — unchanged |
| `Sales\a.txt` + `Sales\b.txt` → ZIP | `Sales\Sales.zip` | `Sales\Sales.zip` — unchanged |

Verified through the real resolver, not the unit alone:

```
$ noba-ar shell noba.compress.zip /tmp/pr/Project.Alpha   → Created /tmp/pr/Project.Alpha.zip
$ noba-ar shell noba.compress.zip /tmp/pr/Project.Beta    → Created /tmp/pr/Project.Beta.zip
$ noba-ar shell noba.compress.zip /tmp/pr/report.pdf      → Created /tmp/pr/report.zip
```

### 16.4 New tests

| ID | Test | Covers |
|---|---|---|
| **SHELL-078** | `Directory_identity_travels_from_the_selection_to_the_suggested_name` | A, B, C, D, E, F — and asserts `request.Inputs` **equals** `selection.Items`, so a caller that re-derived the flag from anywhere could not pass by producing the same values |
| **SHELL-078b** | `No_application_hosted_action_consults_the_directory_flag` | the `argv` path records `IsDirectory: false` because it cannot know; safe only while no application-hosted command shape contains a `SuggestedTarget` slot, which this asserts rather than leaves in a comment |

The same folder name presented as a **file** still loses its extension (`Project.Alpha` as a file →
`Project.zip`), which is what proves the rule reads the flag rather than having been switched off.

SHELL-060…074, 076, 077 are unchanged apart from the mechanical `InputPaths` rename. Nothing was
deleted or weakened.

### 16.5 Negative controls — nine, all fired, all restored sha256-identical

| Mutation | Fired |
|---|---|
| **new:** `SuggestedArchivePath` ignores the flag (SHELL-075 reinstated) | SHELL-075, **SHELL-078** |
| **new:** `RequestFor` rebuilds items as files, discarding identity | SHELL-075, **SHELL-078** |
| script composes its own command | SHELL-054b |
| `%*` → `%1`, `Player` → `Document` | SHELL-072 |
| format dimension removed | SHELL-031, 054, 056, 057, 059 · SHELL-067, 068 |
| Extract here loses its destination | SHELL-014, 054, 054c, 058 · SHELL-066 |
| capability filtering bypassed | SHELL-007, 011, 077 |
| GZIP folder as a single member | SHELL-062, 055, 060, 074 |
| duplicate protection defeated | SHELL-028 + 5 others |

No `#pragma`, no `NoWarn`, no suppression, no timing assertion.

### 16.6 Regression

| Suite | Discovered | Ran | Passed | Failed | Skipped |
|---|--:|--:|--:|--:|--:|
| Core | 95 | 95 | 95 | 0 | 0 |
| Security | 41 | 41 | 41 | 0 | 0 |
| Application | 35 | 35 | 35 | 0 | 0 |
| Infrastructure | 19 | 19 | 19 | 0 | 0 |
| Formats | 18 | 18 | 18 | 0 | 0 |
| Integration | 167 | 161 | 161 | 0 | 6 |
| Architecture | 67 | 67 | 67 | 0 | 0 |
| CLI | 33 | 33 | 33 | 0 | 0 |
| Parity | 23 | 23 | 23 | 0 | 0 |
| Presentation | 240 | 240 | 240 | 0 | 0 |
| **Total** | **738** | **732** | **732** | **0** | **6** |

Build: 0 warnings, 0 errors. `analyzer-preflight`: PASS.

### 16.7 ARC-061

**Intact and unweakened.** The fix added no filesystem call anywhere: `ARC-061`
(`The_shell_action_model_does_not_walk_the_filesystem`) and
`Does_not_touch_the_filesystem_directly(NOBA.Archive.Core)` both pass unchanged. The directory flag
travels from Explorer through `ShellSelection`; the only `Directory.Exists` in the whole path remains
the resolver's, which classifies items **after** the person has chosen a line and is outside
`Core/Actions`.

### 16.8 The Create-screen debt, revisited

`CreateViewModel.Blocker` still calls `PermitsInputCount(_inputs.Count)` — **unchanged**, and still on
the DROP 06 side of the boundary. What this fix changes is the cost of clearing it: `ArchiveActionRequest`
now carries `ShellItem`, so `ShellRequestSeed` can hand the Create screen real directory identity the
moment `CreateViewModel` is able to store it. The four-step contract in §14.3 is otherwise unchanged.

### 16.9 The four gates

| Gate | Status |
|---|---|
| **MODEL VERIFIED** | ✅ **PASS** — every contract SHELL-054…078b green, no exceptions |
| **REGISTRATION VERIFIED** | ✅ **PASS** |
| **WINDOWS CLASSIC RUNTIME VERIFIED** | ❌ **NOT VERIFIED** — nothing has run on Windows; `%*` and `MultiSelectModel` never observed (SHELL-L8); the 34-step gate has never completed |
| **WINDOWS 11 MODERN MENU VERIFIED** | ❌ **NOT IMPLEMENTED** — the classic handler lands under "Show more options"; ADR-0006 unchanged; no MSIX, `IExplorerCommand`, COM server or WiX exists |

The shipping requirement is unchanged and unmet: **the modern menu, above "Show more options", as the
NOBA Archive submenu** (OPEN-6). Phase 4 is the classic-runtime gate and does not close it; Phase 5 does.

**Phase 4 is READY and NOT STARTED.**

---

## 17. Phase 4 — the Windows classic runtime gate, prepared 2026-08-27

**The gate is built, statically guarded and dry-run. It has NOT been executed, because I cannot run
anything on Windows.** Phase 4 completes when you run it and send the transcript back.

### 17.1 What changed

| File | Change |
|---|---|
| `scripts/shell-gate.ps1` | **rewritten** around SHELL-W3…W10 |
| `tests/…/ShellScriptTests.cs` | SHELL-079, SHELL-080, SHELL-081 — static guards on the gate itself |

No product code touched. Build stamp unchanged at `2026-08-27-drop-08-phase-3-directory-identity`,
which the gate checks before it will run at all.

### 17.2 Why the old gate never completed

It asked **34 questions**. Most of them were not visual facts at all:

| The old question | What it actually is |
|---|---|
| "there is no Compress to RAR" | which keys exist under `SystemFileAssociations\.rar` |
| "BZIP2 and XZ appear nowhere" | a registry scan |
| "GZIP is greyed for two files" | a refusal with an exit code |
| "the entry is gone after uninstall" | a registry scan |
| "no duplicate entries after reinstall" | a key count |

Those now run by themselves. **Nine manual steps remain**, and each is something a person can see and
a script cannot: whether Explorer draws the menu, where it draws it, and what the submenu reads.

### 17.3 What the gate proves, and what it cannot

It reads the command string **out of the registry key registration wrote**, substitutes `%1` and `%*`
as the shell documents, splits it into an argv array, runs it, and checks the artefact on disk.

That proves the registered command is correct and the action model behind it works. **It does not
prove Explorer performs the same substitution** — this script is the one doing the expanding. That is
SHELL-L8, and step **M7** is the only thing that can settle it. A green automated `SHELL-W4` with a
NOT RUN on M7 means "the contract is right and nobody has watched Windows honour it", and the gate
prints exactly that.

The gate also refuses to rebuild the command from `shell-verbs.json`. A gate that reconstructed what
it expected to find would agree with itself and could never catch a registration that wrote something
else — which is the class of defect D2 and D3 were. `SHELL-080` asserts it reads the key.

### 17.4 Coverage

**Automated — 15 checks, no human:**

| ID | What runs |
|---|---|
| `SHELL-W3a` | Compress to 7Z on one file produces `report.7z` beside it |
| `SHELL-W3b` | the original is untouched — compressing copies |
| `SHELL-W3c` | Extract here lands beside the archive, not in the process CWD |
| `SHELL-W3d` | Test archive runs headless, exit 0 |
| `SHELL-W3e` | a folder named `Project.Alpha` proposes `Project.Alpha.zip` (SHELL-075) |
| `SHELL-W3f` | `Project.Beta` does not collide with it |
| `SHELL-W3g` | every original file survived every action, same size |
| `SHELL-W4` | three files → **one** archive holding three entries, count read from `noba-ar list --json` |
| `SHELL-W5` | `.rar` carries open/test/extract; no compress-to-RAR under **any** scope |
| `SHELL-W6a` | GZIP is not registered on `Directory`, and is registered on `*` |
| `SHELL-W6b` | GZIP on two files exits **3** and writes nothing |
| `SHELL-W6c` | GZIP on one file still works — the rule, not a blanket ban |
| `SHELL-W7` | no BZIP2/XZ entry under any scope |
| `SHELL-W8` / `W8b` | Arabic and Japanese names survive into the archive, verified by listing it |
| `SHELL-W9` | uninstall leaves no NOBA key, nested scopes included, and no publisher key |
| `SHELL-W10` | registering twice does not change the entry count |

**Manual — 9 steps:** M1 menu exists · M2 where it sits · M3 the submenu on a plain file · M4 on an
archive · M5 on a `.rar` · M6 on a folder · **M7 multi-select produces one archive (SHELL-L8)** ·
M8 Create archive… seeds the screen · M9 Open shows the archive · M10 gone after uninstall.

**NOT IMPLEMENTED — SHELL-W1, SHELL-W2.** Recorded with that verdict rather than as skips, because a
skip reads like something that might have passed.

### 17.5 What I verified on Linux

I cannot run the gate. What I could check, I did:

- **Parses clean** under PowerShell 7.4.6.
- **The argv tokenizer dry-run** against the exact command strings `shell-register.ps1` writes, with
  an install path containing a space: quoted paths stay one argument; `%*` expands to N arguments
  preserving spaces and Unicode; `%1` takes the first; an empty selection yields no path arguments.
- **`Check()` plumbing** — pass, fail, a throwing check, and a check that emits a stray object (which
  fails rather than letting the stray decide the verdict).
- **Every exit code and output shape the gate asserts**, measured against the real CLI: GZIP-on-two
  exits `3` and writes nothing; GZIP-on-one exits `0`; `list --json` carries `count`; the Arabic name
  round-trips and appears in `list` output; `Project.Alpha`/`Project.Beta` produce distinct archives.
- **A shadowed automatic variable** (`$args`) found and renamed before it could bite.

### 17.6 New static guards

| ID | Holds |
|---|---|
| **SHELL-079** | the gate reads the registry through the literal `Microsoft.Win32` API — it touches the `*` class key too, so it can reintroduce the 14-minute wildcard defect SHELL-032 forbids for registration |
| **SHELL-080** | the gate runs the command it read from the key, and does not rebuild one from the manifest; no command interpreter on its execution path |
| **SHELL-081** | `NOT IMPLEMENTED` and `NOT RUN` stay distinct, the four gates are reported apart, and a run with unanswered steps cannot report PASS |

All three negative-controlled: a globbing provider path, a manifest-rebuilt command, and a verdict
that ignores `NOT RUN` each turn their guard red. `scripts/shell-gate.ps1` restored sha256-identical.

### 17.7 Regression

| Suite | Discovered | Ran | Passed | Failed | Skipped |
|---|--:|--:|--:|--:|--:|
| Core | 95 | 95 | 95 | 0 | 0 |
| Security | 41 | 41 | 41 | 0 | 0 |
| Application | 35 | 35 | 35 | 0 | 0 |
| Infrastructure | 19 | 19 | 19 | 0 | 0 |
| Formats | 18 | 18 | 18 | 0 | 0 |
| Integration | 167 | 161 | 161 | 0 | 6 |
| **Architecture** | **70** | **70** | **70** | 0 | 0 |
| CLI | 33 | 33 | 33 | 0 | 0 |
| Parity | 23 | 23 | 23 | 0 | 0 |
| Presentation | 240 | 240 | 240 | 0 | 0 |
| **Total** | **741** | **735** | **735** | **0** | **6** |

Build: 0 warnings, 0 errors. `analyzer-preflight`: PASS.

### 17.8 The four gates

| Gate | Status |
|---|---|
| MODEL VERIFIED | ✅ PASS |
| REGISTRATION VERIFIED | ✅ PASS |
| **WINDOWS CLASSIC RUNTIME VERIFIED** | ⏳ **GATE READY, NOT EXECUTED** — needs a Windows 11 machine |
| WINDOWS 11 MODERN MENU VERIFIED | ❌ NOT IMPLEMENTED (SHELL-W1, W2 — Phase 5) |

### 17.9 What I need from you

```powershell
cd C:\Users\m-m-e\Desktop\NOBA-Archive
.\scripts\shell-gate.ps1
```

It builds both solutions, generates the manifest, registers, runs the 15 automated checks, asks the
9 manual questions, uninstalls, and writes a transcript to `artifacts\shell-gate-<timestamp>.txt`.
Send that file back.

`-Unattended` runs only the automated half if you want the machine-checkable result first; it can
never report PASS, by construction.

**Watch M7 especially.** If three selected files produce three separate archives instead of one, that
is Explorer not honouring `%*` / `MultiSelectModel=Player` — the one thing in this drop that has never
been observed, and the reason SHELL-L8 exists.

---

## 18. Phase 4 — executed on Windows 11, 2026-08-28

**WINDOWS CLASSIC RUNTIME = INCOMPLETE.** 15 of 15 automated checks pass. Two manual steps fail, and
the evidence says they are one root cause, in Explorer's invocation rather than in the action model.

Tree: `2026-08-27-drop-08-phase-3-directory-identity`, confirmed by the gate before it would run.
Transcript: `artifacts\shell-gate-20260828-065655.txt`.

### 18.1 Registration, as Windows actually received it

```
9 submenu(s), 37 entry registration(s), no stray entries
  *                              compress.7z  compress.gzip  compress.tar  compress.zip  create
  Directory                      compress.7z                 compress.tar  compress.zip  create
  SystemFileAssociations\.7z     open  test  extracthere  extractto      (and .gz .r00 .rar .tar .zip .zipx)
```

Every Phase 2/3 correction is visible in that listing, on a real machine: per-format Compress entries
(D1), archive verbs scoped to openable extensions rather than to `*` (D5), and **no `Compress to
GZIP` under `Directory`** (D7).

### 18.2 Automated — 15/15 PASS

| ID | Result | Evidence from the run |
|---|---|---|
| SHELL-W3a | ✅ | registry command `noba-ar.exe shell noba.compress.7z %*` → `report.7z` exists, exit 0 |
| SHELL-W3b | ✅ | `report.pdf` untouched |
| SHELL-W3c | ✅ | Extract here → 2 files under the archive's own folder, exit 0 |
| SHELL-W3d | ✅ | `Valid: 1 entr(ies) tested.` exit 0 |
| **SHELL-W3e** | ✅ | **`Project.Alpha.zip: True; the old colliding Project.zip: False`** |
| **SHELL-W3f** | ✅ | **`Project.Beta.zip: True`** — no collision |
| SHELL-W4 | ✅ | **1 archive, 3 entries inside** |
| SHELL-W5 | ✅ | `.rar` carries open/test/extract; compress-to-RAR found: 0 |
| SHELL-W6a | ✅ | gzip on `Directory` = False, on `*` = True |
| SHELL-W6b | ✅ | exit **3**, `.gz` written: **0**, refusal names the rule |
| SHELL-W6c | ✅ | one file → exit 0, one `.gz` |
| SHELL-W7 | ✅ | bzip2/xz entries: 0 |
| SHELL-W8 / W8b | ✅ | `تقرير سنوي.txt` and the Japanese name inside their archives |
| SHELL-W9 | ✅ | 10 key trees removed, 0 survivors, publisher key gone |
| SHELL-W10 | ✅ | 37 keys before, 37 after a second registration |
| SHELL-W3g | ✅ | 8 originals, 0 changed or missing |

**SHELL-075 is now verified on Windows**, not only on Linux: two folders differing after a dot
produced two distinct archives.

### 18.3 Manual — 8 PASS, 2 FAIL

| ID | Result | |
|---|---|---|
| M1…M6, M9, M10 | ✅ | menu present, in the expected place, correct contents on a file, an archive, a `.rar` and a folder; Open works; nothing left after uninstall |
| **M7** | ❌ | three files → Compress to ZIP → **"crash"** |
| **M8** | ❌ | Create archive… → **"NOBA opened, but it did not open directly on the Create screen and the selected item was not added to the input list"** |

### 18.4 What the two failures have in common

**`noba.compress.zip` and `noba.create` are the only two entries the manual steps reached that carry
`%*`.** Every entry that passed manually uses `"%1"` or is a menu-content check.

The decisive pairing is SHELL-W4 against M7. **Same entry. Same registry command string.** The
difference is who expanded `%*`:

| | Who expanded `%*` | Result |
|---|---|---|
| SHELL-W4 (automated) | the gate, into three separate arguments | one ZIP, three entries, exit 0 |
| M7 (manual) | **Explorer** | crash |

So the action model, the resolver, the CLI and the registered command string are all correct — that
is what W4 proves. **The divergence is in what Explorer hands over**, which is precisely SHELL-L8,
the one thing this drop has never been able to observe.

### 18.5 A hypothesis, labelled as one

If `%*` reaches the process with **no path arguments at all**, both failures follow without either
being a defect in NOBA:

- **M8.** `ShellCommandLine.Parse` requires at least a verb and one path. Given only `--create` it
  returns null, `ShellRequestSeed` seeds nothing, and the application opens on its default screen
  with an empty input list. That is M8's description word for word.
- **M7.** The resolver refuses a selection it cannot reconstruct — `UsageError`, exit 1, nothing
  written (`SHELL-073`). Launched from Explorer with no console attached, that is a window that
  appears and vanishes, which is what a crash looks like from the outside.

**This is not established.** "Crash" is one word and I will not diagnose a cause from it. Competing
explanations that fit M8 equally well include the application's own startup routing, which is WinUI
code that cannot be exercised on the build machine at all.

What the run does establish, and what SHELL-L8 predicted correctly: **nothing wrong was written.**
`SHELL-W3g` confirms all 8 originals survived unchanged, and `SHELL-W6b` confirms a refusal writes
nothing. The failure mode is safe.

### 18.6 The measurement that settles it

`scripts\shell-argv-probe.ps1` — diagnostic, not product. It registers three probe verbs under
`HKCU\Software\Classes\*\shell\NOBA.Probe.*`, a namespace the product never writes, each of which
records what it was handed and does nothing else:

| Probe | Command ends in | Distinguishes |
|---|---|---|
| `NOBA.Probe.Star` | `%*` | the documented behaviour |
| `NOBA.Probe.StarQuoted` | `"%*"` | whether the selection arrives joined into one argument |
| `NOBA.Probe.One` | `"%1"` | whether `MultiSelectModel=Player` is honoured at all |

It records `[Environment]::CommandLine` — the raw line the operating system built — as well as the
parsed `$args`. The raw line is the authority: by the time anything can read `$args` it has been
through a parser, so a substitution fault and a parsing fault are indistinguishable from `$args`
alone.

**Reading it.** For three selected files: one entry with three arguments means `%*` is fine and the
fault is downstream; one entry with one argument means the selection arrived joined; **three entries
means `MultiSelectModel` was ignored and Explorer launched a process per file** — which would explain
M7 and M8 together, and which no amount of reading the documentation would have revealed.

### 18.7 One anomaly in the pasted transcript

The pasted results table shows `FAIL M1`, but M1 was answered `y` and recorded `PASS M1` when it ran,
and the counters (`PASSED 25 FAILED 2`, 29 rows) allow exactly two failures — which M7 and M8 account
for. The pasted text had its line breaks collapsed, so this is very likely a paste artefact rather
than a gate defect. **Worth confirming against `artifacts\shell-gate-20260828-065655.txt`** before it
is treated as either.

### 18.8 The four gates

| Gate | Status |
|---|---|
| MODEL VERIFIED | ✅ PASS |
| REGISTRATION VERIFIED | ✅ PASS — and now corroborated on Windows by 15 automated runtime checks |
| **WINDOWS CLASSIC RUNTIME VERIFIED** | ⚠️ **INCOMPLETE** — M7, M8 |
| WINDOWS 11 MODERN MENU VERIFIED | ❌ NOT IMPLEMENTED (SHELL-W1, W2 — Phase 5) |

Phase 5 not started. DROP 09 not started. ADR-0006 unchanged.

---

## 19. SHELL-L8 measured — `%*` expands to nothing

**2026-08-28. The inference was wrong, and the measurement says so plainly.**

`scripts\shell-argv-probe.ps1 -Auto` registered three verbs differing only in their substitution and
invoked each through the shell on one file. What the shell built:

| Registered command ends in | Raw command line the shell produced | `argc` |
|---|---|--:|
| `%*` | `… -Variant NOBA.Probe.Star` | **0** |
| `"%*"` | `… -Variant NOBA.Probe.StarQuoted ""` | 1, and it is `<>` |
| `"%1"` | `… -Variant NOBA.Probe.One "C:\…\noba-probe-sample.txt"` | **1, the path** |

**`%*` contributes nothing.** `"%*"` contributes an empty string. `"%1"` works. Same harness, same
file, three registrations — so the harness can substitute; it is `%*` specifically that yields
nothing.

### 19.1 Both Phase 4 failures now have a cause

The hypothesis in §18.5 was that `%*` reaches the process with no path arguments. It does.

- **M8** — `noba.create` registers `"<app>" --create %*`. The application therefore receives
  `--create` and nothing else. `ShellCommandLine.Parse` requires a verb and at least one path, so it
  returns null, `ShellRequestSeed` seeds nothing, and the window opens on its default screen with an
  empty input list. That is M8, exactly.
- **M7** — `noba.compress.zip` registers `"<cli>" shell noba.compress.zip %*`. The resolver receives
  an entry id and no selection, refuses with `UsageError` (`SHELL-071`), and exits. Launched from
  Explorer with no console, that is a window that appears and vanishes.

**Nothing in the action model, the manifest, the resolver or the capability model is implicated.**
`SHELL-W4` proved the contract correct by expanding `%*` itself; the fault is one cell in the
adapter's translation table — `Convert-NobaToken` mapping `SelectedPaths` to `%*`.

That the defect is confined there is the Phase 2 architecture doing its job: the registration is an
adapter, its only freedom is how it spells a token, and that is precisely where the error turned out
to be.

### 19.2 What is still unknown, and it is one click

The correct spelling is not yet established. `"%1"` delivers one path; whether
`MultiSelectModel = Player` then causes the shell to append the rest of a multi-selection to the same
command line has **not been observed**, and multi-item invocation is not reachable from
`Shell.Application` — so it needs a human.

The probe now registers a fourth variant so one sitting settles it:

| Probe | Substitution | `MultiSelectModel` |
|---|---|---|
| `NOBA.Probe.One` | `"%1"` | `Player` |
| `NOBA.Probe.OneDoc` | `"%1"` | `Document` |

**Select three files, click probe 3.** One window listing three paths means Player works and the fix
is a one-token change in `Convert-NobaToken`. Three windows with one path each means the classic
surface cannot deliver a multi-selection to a single process at all — a much larger finding, which
the resolver would have to absorb rather than the registration.

**No product change until that is measured.** Swapping `%*` for `"%1"` on the strength of
documentation would be replacing one unverified inference with another, which is how `%*` got there.

### 19.3 Two defects in my own diagnostic, both found by using it

- **The report counted lines, not clicks.** It announced `2 invocation(s) recorded` when the logs held
  one self-test written to two files and no clicks at all — a report reassuring its reader about the
  one thing it existed to detect. A click is now keyed on `(variant, pid)`, self-tests are excluded,
  and the same invocation in two logs counts once.
- **The wait loop exited instantly.** `KeyAvailable` saw the Enter that launched the script still
  sitting in the input buffer. The buffer is flushed first now, and only ESC ends the wait.

### 19.4 Status

| Gate | Status |
|---|---|
| MODEL VERIFIED | ✅ PASS |
| REGISTRATION VERIFIED | ⚠️ **one cell wrong** — `SelectedPaths` → `%*` does not deliver a selection |
| WINDOWS CLASSIC RUNTIME VERIFIED | ⚠️ INCOMPLETE — M7, M8, both now explained, neither yet fixed |
| WINDOWS 11 MODERN MENU VERIFIED | ❌ NOT IMPLEMENTED |

SHELL-L8 is no longer "unobserved". It is observed, and it is a defect with a known location.

---

## 20. SHELL-L8 fully measured — the classic surface cannot deliver a multi-selection

**2026-08-28. Neither substitution works, and this is a limit of surface B rather than a defect in
the registration.**

A real Explorer click on `NOBA.Probe.One` (`"%1"`, `MultiSelectModel = Player`) with three files
selected produced **three processes**:

| pid | at | `argc` | path |
|---|---|--:|---|
| 64100 | 12:07:15.534 | 1 | `…\Screenshots\Screenshot 2026-08-28 063226.png` |
| 63580 | 12:07:15.727 | 1 | `…\Screenshots\Screenshot 2026-08-28 064417.png` |
| 68408 | 12:07:15.764 | 1 | `…\right-click-me\ProjectA\nested\leaf.txt` |

230 milliseconds apart, `parent cwd : C:\Windows\system32` — Explorer's own launch context, so these
are real context-menu invocations and not the `Shell.Application` path.

**`MultiSelectModel = Player` was not honoured. Explorer behaved as `Document`: one process per
selected file.** The probes are registered as plain verbs directly under
`HKCU\Software\Classes\*\shell\…`, not inside a cascading submenu, so the submenu is not the cause.

### 20.1 The complete measurement

| Registration | Result |
|---|---|
| `%*` | expands to **nothing** — `argc 0` |
| `"%*"` | expands to an **empty string** — `argc 1`, `<>` |
| `"%1"` + `MultiSelectModel = Player` | **N processes, one path each** |

There is no third option. **The classic `ContextMenuHandlers` surface, registered as a command
string, cannot hand a multi-selection to a single process.**

### 20.2 What this changes, and what it does not

It does **not** change the diagnosis of M7 and M8: both still fail because `%*` delivers nothing.

It **does** change the fix. §19.2 expected a one-token change in `Convert-NobaToken` — `%*` for
`"%1"`. That would swap "no archive at all" for **three archives instead of one**, which violates D4
and `SHELL-057`, the contract that says N inputs make one request. It would be a worse defect than
the one it replaced, and it would have looked like a fix.

That is why §19.2 refused to make the change on documentation alone.

### 20.3 Four ways forward, none of them mine to choose

| | Approach | Cost |
|---|---|---|
| **A** | `MultiSelectModel = Single` on the `%*`-carrying entries, so *Compress to* and *Create archive…* appear only for a single selection | Honest and immediate, and it is the same principle OPEN-1 already applies to Open/Test/Extract. Multi-select compression disappears from the classic menu |
| **B** | Register `"%1"` and let N processes run, each producing its own archive | **Rejected.** Violates D4/SHELL-057. Three files silently become three archives |
| **C** | A collector: each of the N invocations appends to a short-lived shared list, a debounce elapses, one process does the work | Introduces cross-process state and a timing window into the one path that must never be racy. Contradicts the standing rule against timing-dependent behaviour |
| **D** | `IExplorerCommand` — **ADR-0006, already the plan of record** | Receives the selection as an `IShellItemArray`: one invocation, the whole selection, by design. Needs the signed sparse MSIX package. This is Phase 5 |

**The measurement is an argument for D.** ADR-0006 chose `IExplorerCommand` for the Windows 11 menu;
it turns out to be required for correct multi-selection on *any* surface, which strengthens the
existing decision rather than revising it.

**A is the honest interim.** It makes surface B do less, correctly, instead of appearing to do more
and producing the wrong number of archives.

### 20.4 What I have not done

No product code changed. `Convert-NobaToken` still maps `SelectedPaths` to `%*`, which is measurably
wrong — but every available correction is an architectural decision, not a smallest fix, and the stop
condition says report rather than choose.

`ADR-0006` untouched. Phase 5 not started. DROP 09 not started.

### 20.5 Status

| Gate | Status |
|---|---|
| MODEL VERIFIED | ✅ PASS |
| REGISTRATION VERIFIED | ⚠️ `SelectedPaths` has **no correct spelling** on this surface |
| WINDOWS CLASSIC RUNTIME VERIFIED | ⚠️ INCOMPLETE — M7 and M8 explained, and the explanation is a surface limit |
| WINDOWS 11 MODERN MENU VERIFIED | ❌ NOT IMPLEMENTED — and now demonstrably **required**, not merely preferred |

---

## 21. Decision — Phase 5 is the modern surface, not a classic patch

**Recorded 2026-08-28, on the owner's instruction.**

> Phase 5 does not begin by fixing the classic registry. It begins from the ADR-0006 decision and
> implements the modern Windows 11 surface, which receives `IShellItemArray`.

### 21.1 What this settles

Of the four options in §20.3:

| | | Status |
|---|---|---|
| **A** | `MultiSelectModel = Single` on the classic surface | **Not taken.** Surface B is left as it is, with its limitation documented, rather than reshaped into something that hides it |
| **B** | Accept N processes → N archives | Rejected in §20.3 — violates D4/`SHELL-057` |
| **C** | Cross-process collector with a debounce | Rejected in §20.3 — timing-dependent |
| **D** | `IExplorerCommand` per ADR-0006 | **CHOSEN** |

The classic handler stays exactly what ADR-0006 always called it: the Windows 10 fallback. It is not
patched to look capable of something it cannot do. `SHELL-L8` stands as a measured limitation of that
surface.

### 21.2 Why the measurement makes this the right call rather than merely the preferred one

`IExplorerCommand::Invoke` receives an `IShellItemArray` — the whole selection, once, as objects. Not
a substituted string. That removes the two defects Phase 4 found at their root rather than working
around them:

- **The selection arrives intact.** No `%*`, no `%1`, no substitution to get wrong. D4 and
  `SHELL-057` become structurally satisfiable instead of unachievable.
- **`SFGAO_FOLDER` comes with each item**, so `ShellItem.IsDirectory` is supplied by the shell rather
  than assumed — the same fact SHELL-075 and D7 both turned on, delivered by the platform.
- **`GetState` can return `ECS_DISABLED`**, which finally makes a greyed entry expressible and closes
  `SHELL-L9`. On the classic surface a key is present or absent and there is no third state.
- **`GetTitle` is called at runtime**, so labels come from `LocalizedStrings` in the user's language,
  closing `SHELL-L2` and retiring `ShellVerb.InterimLabel` and `InterimTextFor` — the stopgap that
  `SHELL-031` exists to keep honest.

Four known limitations close because of the mechanism, not because of extra work. That is the sign
the mechanism was the right one all along.

### 21.3 What the existing architecture already contributes

The adapter is a **third consumer of the same table**, which is what Phase 2 was built for:

```
ShellSelection ─┐
                ├─▶ ShellMenuBuilder ─▶ ArchiveActionRequest ─▶ ShellLauncher
IShellItemArray ┘         (unchanged)          (unchanged)         (unchanged)
```

`ShellInvocations`, `ShellMenuBuilder`, `ShellRegistration.Entries` and the capability model are all
surface-agnostic already. **`SHELL-063`, `SHELL-064` and `SHELL-065` were written for exactly this
moment** and remain the contracts: the adapter holds no menu logic, both surfaces produce identical
menus for identical selections, and package identity derives from `ShellRegistration`.

### 21.4 The one hard blocker, stated before any work starts

**A sparse MSIX package will not register unsigned.** That is a platform requirement, not a
preference, and ADR-0006 puts certificate procurement at Phase 6.

There is a development route that does not wait for procurement: a self-signed certificate created
with `New-SelfSignedCertificate` and trusted locally lets the package register on **one machine** for
development and gating. It is not a shipping answer and must never be presented as one — Phase 6
still owns the real certificate.

So Phase 5 is buildable now, on the owner's machine, provided that dev certificate is acceptable as a
gating mechanism.

### 21.5 What I cannot do, said plainly

**A COM in-process shell extension cannot be compiled or exercised on this build machine at all.**
Not the server, not the manifest registration, not the menu. Every previous drop had a Linux-testable
core with a Windows-only edge; this one is Windows-only end to end.

That changes the working method for Phase 5, and it should be agreed before it starts rather than
discovered halfway:

- the action model behind the adapter stays fully testable here, as it is today;
- the adapter itself — the COM class, the `AppxManifest`, the packaging — is **built blind and
  verified only on Windows**;
- `SHELL-064` (both surfaces produce identical menus) is the contract that makes that tolerable,
  because it compares the new adapter against the model that is already proven.

### 21.6 Phase 5 shape, for approval — NOT STARTED

1. **ADR-0006 re-read and confirmed**, not amended. Phase 4's measurement is added as evidence
   supporting it.
2. **`IExplorerCommand` server.** Language and hosting to be decided — a .NET NativeAOT COM server or
   C++; a WinUI-hosted in-process COM extension is the option most likely to fail for reasons that
   have nothing to do with this product.
3. **Sparse `AppxManifest.xml`** — `com.exe` `ComServer` plus `desktop5:Extension`
   `FileExplorerContextMenus`, with identity derived from `ShellRegistration` (`SHELL-065`).
4. **The adapter**, calling `ShellMenuBuilder` and nothing else (`SHELL-063`).
5. **`SHELL-064`** — surface A and surface B produce identical menus for identical selections.
6. **`SHELL-W1`, `SHELL-W2`** — the placement and capability screenshots that only surface A can
   satisfy.
7. **Dev certificate + registration + a Windows gate** modelled on `shell-gate.ps1`.

**Not started.** ADR-0006 unchanged. DROP 09 not started. No product code touched since Phase 3.

### 21.7 Two questions before Phase 5 begins

1. **Is a self-signed development certificate acceptable** for Phase 5 gating, with real procurement
   left to Phase 6?
2. **Which host for the COM server** — .NET NativeAOT or C++? It is the one decision that shapes
   everything after it, and I would be choosing it without being able to compile either.

---

## 11. What I need from you before implementation

**Phase 1 is complete and red.** Nothing else proceeds without your review of §12.

1. **Review the five failures** and confirm each is red for the reason you expect.
2. **Approve Phase 2** — SHELL-056, 058, 057, 055, 062. These are the fixes; every one turns a
   currently-red test green by changing the registration, never by changing an assertion.
3. **Re-attach the screenshots** if the modern-menu shape needs anything beyond OPEN-6's submenu
   decision. They still have not arrived.

**DROP 08 remains OPEN. No implementation until you review the failures.**

---

## 22. Phase 5 G.1 — feasibility gate executed, 2026-08-28

Full results: **[`DROP08_PHASE5_G1_REPORT.md`](DROP08_PHASE5_G1_REPORT.md)**.

One line of it: **PROCEED B1**, conditional on two plan revisions and on one risk G.1 was forbidden
to test.

| | |
|---|---|
| Files changed | four, all under `harness/phase5-g1/` — nothing in `src/`, `tests/`, `scripts/` |
| Build | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| Tests | 735 passed, 6 skipped, 0 failed — unchanged, as it must be |
| Cold (Linux) | 147.72 – 162.79 ms, of which ~95 ms is movable to surrogate startup |
| Warm (Linux) | median 0.02 ms, p95 ≤ 0.08 ms, 0 failures under 4-way contention |
| Failure | managed exception caught, `-99` across the ABI, host survives, next call succeeds |
| **Not tested** | **E — isolation from `explorer.exe`.** A property of `com:SurrogateServer`, which G.1 was not authorised to build. Not claimed either way. |

**Every number above is from the Linux container.** `harness/phase5-g1/run-g1.ps1` runs the identical
measurement on Windows; until its transcript exists, none of this is a Windows result.

Revisions the measurement forces on §0–J: the ABI gains `NobaShellWarm` (R1); that warm-up must build
a menu rather than only the DI container, because the container is the smaller half of the cost (R2);
§E.2's `E_PENDING` row should say "bridge not yet warm" rather than "CLR not yet warm" (R3); and
§G.1's own instruction to measure "a trivial managed function" was wrong and was deliberately not
followed (R4). **ADR-0006 is unamended.**
