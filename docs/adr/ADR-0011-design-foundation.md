# ADR-0011 — Design foundation: tokens, bidirectionality, typography

**Status:** Accepted (Phase 2, 2026-08-20) — foundation only. No UI is built by this decision.

## Context

A UI mock was produced showing the intended product: tabbed archive browser, preview pane, queue
manager, benchmark, an extraction dialog that lists refused entries, and an Arabic/English toggle with
dark and light modes. The stated competitive angle is control over theme, colours and font.

Two failure modes were available here and both are common. Deferring all of this to the UI phase means
retrofitting bidirectionality and theming onto a codebase that assumed neither — historically the most
expensive mistake in this area. Building the full UI now means investing on top of an engine that has
never run in its shipping configuration (R13) and a security invariant not yet proven against a native
backend (R1).

So: establish the foundation now, validate it on the first real surface, defer the product surface.

## Decision

### 1. Tokens are the only styling vocabulary
No component may name a raw colour, pixel value, or font. Everything resolves through a semantic token
(`surface.raised`, `text.secondary`, `state.blocked`, `space.3`, `radius.md`). Themes are token sets;
"dark mode", "light mode", "high contrast" and any user accent are the same mechanism, not three code
paths. The full token set is specified in `docs/DESIGN_TOKENS.md`.

### 2. The engine knows nothing about presentation
No token, theme, locale, colour, font, or direction may appear in `Core`, `Security`, `Application`,
`Infrastructure`, `Formats`, or `Engine`. The engine returns data and error *codes*; the presentation
layer decides wording, direction, and colour. This is already why `ArchiveErrorCode` is a closed set,
and an architecture test will assert it once a UI assembly exists.

Concretely: the engine never formats a number, a date, or a byte size for display. `ByteSize.ToString()`
exists for logs and diagnostics; the UI formats its own using the user's locale. A "1.48 GB" in an
Arabic UI may need Arabic-Indic digits, and the engine has no business deciding that.

### 3. Bidirectionality is a first-class property, not a mirror flip
Arabic is not "English, reversed". The rules that bind:

- **Direction is per-string, not per-app.** A file list in an Arabic UI routinely contains
  `C:\Users\m-m-e\Desktop` — an LTR string inside an RTL paragraph. Every path, entry name, and archive
  name is rendered with explicit isolation (Unicode `FSI`/`PDI`, or the platform's isolation control),
  because without it a name ending in a bracket or slash visibly rearranges. This is the single most
  common bug in RTL file managers.
- **Logical, never physical.** Start/end, not left/right, in every layout, margin, and icon decision.
- **Direction-sensitive iconography flips; content does not.** Navigation chevrons and progress
  direction mirror. A logo, a file-type glyph, a checkmark, and a chart's numeric axis do not.
- **Numbers stay LTR.** Sizes, ratios, CRC32 values and timestamps are LTR runs regardless of UI
  language. Digit *shape* (Western vs Arabic-Indic) is a separate user preference from direction.
- **Sorting is locale-aware.** Ordinal sorting of Arabic file names is wrong; comparison uses the UI
  culture, while all *security* comparisons stay ordinal. These must never share a code path — a
  culture-sensitive comparison in a path check is a vulnerability, not a cosmetic bug.

### 4. Typography is a real setting, and it carries the Arabic burden
Font family and size are user settings, not a hardcoded stack, because Arabic rendering quality varies
enormously by font in a way Latin does not, and because this is genuinely hard for competitors to
retrofit. Consequences that must hold:

- Separate font stacks per script: the Arabic face and the Latin face are chosen independently, since
  almost no single family does both well.
- Arabic needs more line height than Latin at the same point size — diacritics and descenders collide
  otherwise. Line height is a function of script, not a constant.
- The file list uses tabular figures so sizes and ratios align in a column. Non-negotiable, whatever
  font the user picks.
- Any user-selectable font is validated for the glyph coverage the UI needs, and the app falls back
  visibly rather than rendering tofu.

### 5. Theming is bounded by legibility, not by taste
The user picks a base theme, one accent colour, and typography. They do not hand-author arbitrary
colour pairs.

- Every generated pair is contrast-checked (WCAG AA: 4.5:1 body, 3:1 large text and UI boundaries). An
  accent that fails against its surface is adjusted, and the user is told, rather than silently
  producing unreadable text.
- Windows high-contrast mode and "reduce transparency"/"reduce motion" override user theming entirely.
  Accessibility settings win over personalisation.
- **Status colour is never the only signal.** Blocked/allowed entries carry an icon and text as well as
  colour, because red-green is the most common colour-vision deficiency and because a security refusal
  that reads as decoration is a failed refusal.
- No theme import from a file, for now. A theme file is untrusted input from an untrusted source, and
  this product does not need a second parser of hostile data to defend. Revisit only with a real format
  and a real threat-model entry.

### 6. The security surface is part of the design system
The mock's "Extract with Security" panel — refused entries listed with reasons alongside allowed ones —
is the visible form of `ExtractOutcome.Blocked`, which already exists and is already tested (SEC-001…021).
It gets dedicated tokens (`state.blocked`, `state.allowed`) and a fixed presentation, because it is the
product's main differentiator and the one screen that must never be restyled into ambiguity.

## Consequences

- Retrofitting RTL later is avoided, which is the main cost this ADR buys down.
- The token indirection costs nothing at runtime and some ceremony at authoring time.
- Bounded theming will disappoint anyone expecting a full theme editor. That is the trade for never
  shipping an unreadable UI, and it can be widened later; it cannot be narrowed later.
- Locale-aware display formatting in the UI plus ordinal comparison in security code is a rule that
  needs an architecture test as soon as there is a UI assembly to test.

## Not decided here

The mock also implies decisions that are **not** taken by this ADR and are flagged in
`docs/UI_DESIGN_NOTES.md`: RAR creation (conflicts with ADR-0004 and the unRAR licence), ZIP+Zstandard
output, uniform "Repair" across formats, an at-rest password manager, and in-app preview of untrusted
archive content. Each needs its own decision and, for the last two, its own threat-model entry.
