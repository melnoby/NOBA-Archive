# ADR-0010 — Minimum OS: Windows 10 1809 (17763), tested baseline Windows 10 22H2 / Windows 11

**Status:** Accepted (Phase 0, 2026-08-19) — resolves open decision D7

## Context

The Windows App SDK is backward compatible to Windows 10 version 1809 (build 17763), so 1809 is
technically reachable. The question was whether to claim it, or raise the floor to Windows 11 for
convenience.

Raising the floor for convenience would exclude a real population for no technical reason. Claiming
1809 loosely would be dishonest about what is actually tested and about the state of Microsoft's
own servicing.

## Decision

Three distinct statements, kept separate everywhere they appear:

| Statement | Value |
|---|---|
| **Minimum technical compatibility** | **Windows 10 version 1809 (build 17763), x64** |
| **Primary tested / recommended baseline** | **Windows 10 22H2 and Windows 11** |
| **Development / build machine** | Windows 11 with the current Windows App SDK toolchain |

Rules attached to this decision:

1. **Do not raise the minimum to Windows 11 for convenience.** It moves only if a concrete required
   dependency or feature makes 1809 technically impossible — and that reason gets written down here.
2. **Support 1809 only where our stack genuinely does.** If the pinned Windows App SDK version, .NET 10,
   or a dependency drops 1809, the floor rises to match reality on that day. We do not carry
   compatibility shims to prop up a claim the platform no longer supports.
3. **Never imply that Windows 10 1809 Home/Pro is still receiving normal Microsoft security support.**
   It is not (see §Servicing reality). The floor exists for compatibility, not as an endorsement.
4. **Feature-detect, never version-assume.** Windows 11-only capabilities degrade gracefully rather
   than gating the whole product (see §Feature gating).
5. **Validate the floor where practical.** CI and release validation include a Windows 10 build at or
   near the minimum, in addition to the primary baseline (see §Validation).

## Servicing reality — stated plainly, because the docs must not mislead

| Edition | Status as of 2026-08-19 |
|---|---|
| Windows 10 1809 **Home / Pro** | Out of support since 2020. **Receives no security updates.** |
| Windows 10 **Enterprise LTSC 2019** (built on 1809, build 17763) | In extended support until **2029-01-10**. This is the real reason 1809 remains a defensible floor — managed and appliance fleets. |
| Windows 10 **22H2** consumer | Mainstream support ended **2025-10-14**; consumer ESU runs to **2026-10-13** — roughly two months from now. |
| Windows 11 | Fully supported. The only baseline that is not on a clock. |

Product-facing wording therefore says *"Windows 10 version 1809 or later"* for compatibility, and
*"recommended: Windows 10 22H2 or Windows 11"* for what we test — and nowhere claims that running
an unsupported Windows is safe. If the installer or About dialog ever needs to say more, it says that
security updates for older Windows 10 editions are Microsoft's responsibility and have ended.

## Feature gating (degrade, do not exclude)

| Capability | Windows 11 | Windows 10 1809+ |
|---|---|---|
| Modern context menu (`IExplorerCommand` via sparse package) | ✔ | Classic `ContextMenuHandlers` registry entry, opt-in |
| Mica / backdrop material | ✔ | Solid or acrylic fallback from the theme token layer |
| Rounded window corners, WinUI 11-era visuals | ✔ | WinUI defaults for the OS |
| Everything in the engine — formats, encryption, security controls, CLI | ✔ | ✔ identical |

The engine is version-independent by construction. Nothing in `Core`, `Application`, `Security`,
`Formats`, or `Infrastructure` may branch on OS version; where a Win32 primitive differs,
`NOBA.Archive.Windows` feature-detects the API (`GetProcAddress`, not `Environment.OSVersion`).

## Validation

- **CI (every run):** Windows 11 runner — the development baseline.
- **Nightly / pre-release:** a Windows 10 22H2 image.
- **Release candidate (Phase 10):** a Windows 10 1809-level image (LTSC 2019), running install → smoke →
  extraction of the standard fixture set → `SEC-###` suite → uninstall. `PKG-006` covers this.
- If the minimum-build image cannot be validated for a given release, the release notes say so
  explicitly rather than implying coverage that did not happen (rule 49).

## Consequences

- The app manifest declares `maxversiontested` for the current OS and supported-OS GUIDs down to 1809.
- Any dependency added later must be checked against 1809 (DEPENDENCIES.md §4 gains this to its
  checklist), not just against "current Windows".
- Two extra validation images to maintain. Judged worth it: silently breaking LTSC fleets is the kind of
  defect that surfaces only after release.
- If the floor ever rises, this ADR is amended with the concrete blocking reason — not superseded quietly.
