# ADR-0001 — UI stack: WinUI 3 on .NET 10

**Status:** Accepted (Phase 0, 2026-08-19)

## Context
NOBA Archive is a Windows-native file utility. It needs Explorer integration, drag & drop, per-monitor
DPI, Fluent visuals, screen-reader support, fast startup, and low memory. It must also expose a UI-free
engine for NobaOS.

## Options
| Option | Verdict |
|---|---|
| **WinUI 3 / Windows App SDK** | Native Fluent controls, UI Automation for free, Mica/backdrop, first-class shell interop, Microsoft re-committed at Build 2026 |
| WPF | Mature and stable, but dated visuals, no Windows 11 design language without heavy theming, weaker modern shell story |
| Electron / WebView | Rejected by the brief and on merit: startup time, RAM, and a web renderer in a security-sensitive file tool |
| MAUI | Cross-platform abstraction we do not need; weaker Windows-specific integration |
| Flutter | Would require proving WinUI 3 cannot satisfy a requirement. It cannot be shown, so it is out |

.NET runtime: .NET 8 and .NET 9 both reach end of support on 2026-11-10. .NET 10 is the current LTS and
the only defensible target for a product starting today.

## Decision
WinUI 3 (Windows App SDK 2.x stable, pinned in Phase 1) on .NET 10 LTS, MVVM with CommunityToolkit.Mvvm.

## Consequences
- The project can only be built and tested on Windows. CI must run `windows-latest`.
- Windows App SDK stable releases carry roughly 12 months of servicing, so budget a yearly SDK bump.
- Native AOT and full trimming are unavailable for the app; the CLI may adopt them later.
- The engine layers must stay free of the SDK so NobaOS can consume them.
