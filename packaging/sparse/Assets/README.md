# Sparse package assets — PLACEHOLDERS

These three PNGs are the ones the E2 harness used: flat colour, correct dimensions, nothing else.
They exist because `makeappx` validates that `Square44x44Logo`, `Square150x150Logo` and `Logo`
resolve to real files, and it will not pack without them.

**They are not branding and must not ship as it.** `AppListEntry="none"` means Windows has no Start
menu tile to draw them into today, so nothing user-visible depends on them yet — which is exactly why
replacing them is easy to forget. Phase 6 owns the real assets.

| File | Required by |
|---|---|
| `Square44x44Logo.png` | `uap:VisualElements/@Square44x44Logo` |
| `Square150x150Logo.png` | `uap:VisualElements/@Square150x150Logo` |
| `StoreLogo.png` | `Properties/Logo` |

The manifest that references them is **generated** — `noba-ar appx-manifest --publisher <subject>`.
Do not hand-write it (SHELL-065).
