# UI mock — what it settles, and what it does not

Notes on the product mock dated 2026-08-20. The design foundation it justifies is in ADR-0011 and
`DESIGN_TOKENS.md`. This file records what the mock **implies but does not decide**, so none of it gets
built by accident later on the strength of a picture.

---

## 1. What the mock gets right, and is now foundation

- **The security panel is the product.** "Extract with Security", listing refused entries with reasons
  next to allowed ones and a `2 blocked / 2 allowed` count, is the visible form of
  `ExtractOutcome.Blocked` — which already exists and is covered by SEC-001…021. No competitor surfaces
  this. It gets fixed presentation and dedicated tokens so it can never be restyled into ambiguity.
- **Arabic as a peer language, not a translation.** The `العربية / English` toggle sitting beside the
  theme control is the right framing, and it is the differentiator that is genuinely hard to copy —
  7-Zip and WinRAR both handle Arabic poorly, and retrofitting bidirectionality is expensive.
- **Capability honesty.** "Create ZIP & 7Z", "RAR" only under open/extract, "Preview without extract" —
  the design mostly reflects real capability rather than a wish list, which matches `CapabilityGate`.
- **Queue with pause/resume** matches ADR-0007, including that some backends cannot pause mid-item.

---

## 2. Five conflicts, unresolved

### 2.1 "Create ZIP & 7Z **(RAR if licensed)**" — conflicts with ADR-0004

ADR-0004 locked RAR as **read-only, permanently**: list, extract, test. The unRAR licence forbids using
its source to build a RAR-compatible *compressor*, and that is not a restriction a licence fee removes —
there is no generally available commercial licence that grants RAR creation to a third-party tool. The
parenthetical describes a door that does not exist.

**Recommendation:** delete the parenthetical. Showing it costs nothing to remove now and creates a
support and legal problem the moment a user reads it as a roadmap promise.

### 2.2 ZIP + Zstandard in the benchmark panel

The mock benchmarks `ZIP (ZSTD)` at 298 MB/s beside `ZIP (Deflate)`. Zstandard inside ZIP is method 93 —
supported by 7-Zip and a few others, and **not readable by Windows Explorer** or by most tools a
recipient will have. The 7z spike confirmed 7-Zip on Windows exposes zstd, so this is implementable; the
question is whether it should ever be a default.

**Recommendation:** benchmark it, but never offer it as a ZIP creation default, and warn at the point of
creation that the result is not universally readable. A ZIP the recipient cannot open is a worse
outcome than a slower ZIP. Needs a decision before any UI exposes it.

### 2.3 A single "Repair" button across all formats

Real recovery capability differs enormously: RAR recovery records are a genuine format feature (and
readable through unRAR), 7z offers limited recovery, and ZIP has essentially nothing beyond
reconstructing a central directory from local headers. One uniform button implies a uniform capability
that will not exist.

**Recommendation:** route it through `CapabilityGate` like everything else, so the button is disabled
with a reason per format rather than promising and then disappointing. The mechanism already exists.

### 2.4 "Password Manager (Local & Secure)" — needs a threat-model entry

Storing archive passwords at rest is a new asset and a new attack surface. It is not covered anywhere in
`THREAT_MODEL.md`, and it changes what a compromise of the user's profile yields.

**Open questions:** DPAPI vs Windows Credential Manager vs a keyfile; whether the CLI and NobaOS can
read the store (if yes, any process running as the user can); lock/unlock lifetime; what happens on
uninstall. Note this cuts against an existing rule — passwords are deliberately never accepted via argv
or environment because a same-user process can read both. A store readable by any same-user process
would reintroduce exactly that exposure.

**Recommendation:** own decision, own ADR, own threat-model section. Not a checkbox.

### 2.5 "Preview without extract" — the largest new attack surface in the mock

The mock previews PPTX and PDF inside the app. Rendering untrusted archive content is how preview
handlers have historically become exploit chains, and it undoes some of what ADR-0005 buys: the point of
that decision is that hostile bytes never reach a place where they can act.

**Recommendation:** no in-process rendering of arbitrary formats, ever. Two defensible paths:

1. A small set of formats parsed in-process by code we control, with hard limits — plain text, and
   images through a decoder with size and dimension caps.
2. Everything else extracted to a controlled temp location, marked with Mark of the Web, and handed to
   the OS preview handler **out of process**, so a decoder bug does not run inside the archive manager.

Either way this needs its own ADR and threat-model entry before a line of it is written.

---

## 3. Smaller notes

- The property strip shows `Solid: Yes` twice; one is presumably `Encrypted`.
- `CRC32` as a visible column is right, but the format needs a defined empty state — directories and
  encrypted entries have none, and `00000000` would be a lie.
- `Modified 18/08/2025 10:15` is `dd/MM/yyyy`. Date format must follow the user's locale, not the UI
  language, and must be an LTR run in Arabic per `DESIGN_TOKENS.md` §6.
- `Total 24 items` alongside a preview reading `1 / 24` conflates archive entries with preview pages.
- "Split to Parts & Reassemble": creating multi-volume archives is fine; note that reassembling a
  multi-volume set is a read path that needs its own SEC coverage, since volume names come from the
  archive.

---

## 4. Status

Nothing in this file is scheduled. The current priority order stands: **R13** (Windows shipping
validation), then **R1** (7z interop and the ADR-0005 invariant against the real `7z.dll`), then ZIP
production work, then an internal alpha of open/list/extract, then real usage feedback — and only then
the product surface.

The design foundation exists now so that when the first real surface is built it validates ADR-0011
rather than inventing a second vocabulary. That is the whole reason it was written this early.
