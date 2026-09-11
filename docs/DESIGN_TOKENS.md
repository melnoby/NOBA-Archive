# NOBA Archive — design tokens

The complete styling vocabulary. Per ADR-0011, no component names a raw colour, pixel value, or font;
everything below is what components are allowed to reference.

Values are derived from the product mock. They are the **reference definition**, not an implementation:
the WinUI resource dictionary and any future web surface both generate from this file, so the two cannot
drift.

---

## 1. Layers

Three layers, and components only ever touch the third.

1. **Primitives** — raw values with no meaning (`blue.60`, `neutral.10`, `8px`). Never referenced
   directly by a component.
2. **Semantic tokens** — meaning, resolved per theme (`surface.raised`, `text.secondary`). This is the
   component vocabulary.
3. **Component tokens** — only where a component genuinely needs its own knob
   (`fileList.rowHeight`). Kept few on purpose.

A theme is a mapping from semantic tokens to primitives. Dark, light, high contrast and a user accent
are all the same mechanism.

---

## 2. Colour primitives

Brand blue, taken from the mock's identity.

| Token | Value | Notes |
| --- | --- | --- |
| `blue.70` | `#1B4FD8` | brand core |
| `blue.60` | `#2563EB` | accent default, interactive |
| `blue.50` | `#3B82F6` | hover |
| `blue.40` | `#60A5FA` | accent on dark surfaces |
| `blue.20` | `#BFDBFE` | subtle fill |
| `neutral.98` | `#F8FAFC` | lightest |
| `neutral.94` | `#EEF2F7` | |
| `neutral.86` | `#D8DEE8` | |
| `neutral.64` | `#94A3B8` | |
| `neutral.44` | `#64748B` | |
| `neutral.28` | `#334155` | |
| `neutral.18` | `#1E293B` | |
| `neutral.12` | `#141C2B` | dark raised |
| `neutral.08` | `#0B1220` | dark base |
| `red.60` | `#DC2626` | blocked / error |
| `red.40` | `#F87171` | blocked on dark |
| `green.60` | `#16A34A` | allowed / valid |
| `green.40` | `#4ADE80` | allowed on dark |
| `amber.60` | `#D97706` | warning |
| `amber.40` | `#FBBF24` | warning on dark |

Accent is the only primitive a user may change. `blue.60` is the default.

---

## 3. Semantic colour tokens

| Token | Dark | Light | Used for |
| --- | --- | --- | --- |
| `surface.base` | `neutral.08` | `neutral.98` | window background |
| `surface.raised` | `neutral.12` | `#FFFFFF` | panels, dialogs, cards |
| `surface.sunken` | `#070C16` | `neutral.94` | list background, wells |
| `surface.overlay` | `neutral.18` | `#FFFFFF` | flyouts, menus, tooltips |
| `surface.selected` | `blue.70` @ 24% | `blue.20` | selected list row |
| `surface.hover` | `#FFFFFF` @ 6% | `#000000` @ 4% | hover |
| `border.subtle` | `#FFFFFF` @ 8% | `neutral.86` | dividers, column rules |
| `border.strong` | `#FFFFFF` @ 16% | `neutral.64` | inputs, focusable boundaries |
| `border.focus` | `blue.40` | `blue.60` | focus ring — never removed |
| `text.primary` | `neutral.98` | `neutral.18` | body, file names |
| `text.secondary` | `neutral.64` | `neutral.44` | metadata, column headers |
| `text.disabled` | `neutral.44` | `neutral.64` | |
| `text.onAccent` | `#FFFFFF` | `#FFFFFF` | text on accent fills |
| `accent.default` | `blue.60` | `blue.60` | primary action, brand |
| `accent.hover` | `blue.50` | `blue.70` | |
| `accent.subtle` | `blue.70` @ 20% | `blue.20` | progress track fill, chips |
| `state.blocked` | `red.40` | `red.60` | refused entry — **with icon and text** |
| `state.allowed` | `green.40` | `green.60` | permitted entry |
| `state.warning` | `amber.40` | `amber.60` | degraded, unverifiable |
| `state.encrypted` | `blue.40` | `blue.70` | encrypted entry marker |

### Rules

- Every foreground/background pair meets **WCAG AA**: 4.5:1 body text, 3:1 large text and UI boundaries.
  A user accent that fails is adjusted, and the user is told.
- `state.*` is **never the only signal.** Blocked and allowed always carry an icon and a text label.
  Red-green deficiency is the most common colour-vision difference, and a security refusal rendered as
  colour alone is a failed refusal.
- Windows high-contrast mode replaces the whole palette with system colours and overrides user accent.
- Focus is always visible. `border.focus` has no theme in which it is transparent.

---

## 4. Typography

Two independent stacks, because almost no family serves both scripts well.

| Token | Latin | Arabic |
| --- | --- | --- |
| `font.ui` | Segoe UI Variable, Segoe UI, system-ui | Segoe UI, Dubai, Noto Sans Arabic |
| `font.mono` | Cascadia Mono, Consolas | Cascadia Mono *(paths and hashes are LTR)* |

| Token | Size | Line height (Latin) | Line height (Arabic) |
| --- | --- | --- | --- |
| `type.caption` | 11 | 16 | 20 |
| `type.body` | 13 | 18 | 22 |
| `type.bodyStrong` | 13 semibold | 18 | 22 |
| `type.subtitle` | 16 semibold | 22 | 26 |
| `type.title` | 20 semibold | 28 | 34 |
| `type.display` | 28 semibold | 36 | 44 |

### Rules

- **Arabic line height is larger at every size.** Diacritics and descenders collide at Latin metrics.
  Line height is a function of script, not a constant.
- Font family and base size are **user settings**. Size scales the whole ramp proportionally; individual
  steps are not independently editable.
- The file list uses **tabular figures** so sizes, ratios and CRC32 values align in their columns,
  whatever font is chosen.
- Paths, hashes and CRC32 use `font.mono` in both languages.
- A user-selected font is checked for glyph coverage; on failure the app falls back visibly and says so
  rather than rendering tofu.

---

## 5. Spacing, radius, elevation

Spacing is a 4px scale. Only these values exist.

| Token | Value | | Token | Value |
| --- | --- | --- | --- | --- |
| `space.0` | 0 | | `space.4` | 16 |
| `space.1` | 4 | | `space.5` | 24 |
| `space.2` | 8 | | `space.6` | 32 |
| `space.3` | 12 | | `space.7` | 48 |

| Token | Value | Used for |
| --- | --- | --- |
| `radius.sm` | 4 | chips, small controls |
| `radius.md` | 8 | buttons, inputs, list rows |
| `radius.lg` | 12 | panels, dialogs, cards |
| `radius.full` | 9999 | pills, avatars |

| Token | Dark | Light | Used for |
| --- | --- | --- | --- |
| `elevation.rest` | none | none | flat surfaces |
| `elevation.raised` | border only | `0 1px 2px rgba(0,0,0,.06)` | cards |
| `elevation.overlay` | `0 8px 24px rgba(0,0,0,.48)` | `0 8px 24px rgba(0,0,0,.12)` | flyouts, dialogs |

Dark themes lean on borders rather than shadows: a shadow on a near-black surface reads as noise.

---

## 6. Bidirectionality

| Token | LTR | RTL |
| --- | --- | --- |
| `flow.start` | left | right |
| `flow.end` | right | left |
| `icon.chevronNext` | `→` | `←` |
| `progress.origin` | left | right |

### Rules

- **Logical properties only.** `margin-start`, never `margin-left`. No physical direction appears in any
  component.
- **Every path, entry name and archive name is direction-isolated** (`FSI`…`PDI`). An LTR path inside an
  RTL paragraph visibly rearranges without it — the most common bug in RTL file managers, and one that
  makes a security dialog actively misleading when the string being judged is a path.
- **Numbers are LTR runs** regardless of UI language: sizes, ratios, CRC32, timestamps. Digit shape
  (Western vs Arabic-Indic) is a separate user preference from direction.
- **Mirror navigation, not content.** Chevrons and progress mirror. Logos, file-type glyphs, checkmarks
  and numeric chart axes do not.
- **Display sorting is culture-aware; security comparison is ordinal.** These never share a code path.
  A culture-sensitive comparison inside a path check is a vulnerability, not a cosmetic bug.

---

## 7. Motion

| Token | Value | Used for |
| --- | --- | --- |
| `motion.instant` | 0ms | state that must feel direct |
| `motion.fast` | 120ms | hover, focus, selection |
| `motion.default` | 200ms | expand, flyout, tab change |
| `motion.slow` | 320ms | dialog and panel entry |
| `motion.easing` | `cubic-bezier(0.2, 0, 0, 1)` | all of the above |

Progress indicators never animate faster than the work they describe — a bar that moves while nothing
happens is a lie about progress, and ADR-0007 already rejects invented percentages.

`prefers-reduced-motion` and the Windows "reduce motion" setting collapse every duration to
`motion.instant`. Accessibility settings override personalisation.

---

## 8. Component tokens

Deliberately few.

| Token | Value | Notes |
| --- | --- | --- |
| `fileList.rowHeight` | 28 | 32 in Arabic — taller line box |
| `fileList.columnGap` | `space.4` | |
| `toolbar.height` | 40 | |
| `sidebar.width` | 208 | collapsible to 48 |
| `statusBar.height` | 28 | |

---

## 9. Validation

This file is a specification until something checks it. When the first UI surface is built:

1. Generate the WinUI resource dictionary from these tokens rather than hand-authoring it.
2. Add a contrast test asserting AA for every semantic pair in every shipped theme, including generated
   user accents.
3. Add an architecture test asserting no UI component references a primitive directly.
4. Render the file list, the extraction-security panel and the create dialog in Arabic with a long
   Windows path in every row — that trio is where isolation, tabular figures and line height all fail
   at once if any of them is wrong.
