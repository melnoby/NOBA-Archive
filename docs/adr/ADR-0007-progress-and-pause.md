# ADR-0007 — Honest progress and honest pause

**Status:** Accepted (Phase 0, 2026-08-19)

## Context
Rules 12 and 26 forbid fake progress. Streaming formats and solid archives genuinely cannot always report
a total; some backends cannot pause mid-item.

## Decision
- `OperationProgress.ItemsTotal`, `BytesTotal`, `Percent`, and `Eta` are **nullable**. `null` means
  unknown, and the UI renders an indeterminate state. No estimate is ever invented to fill a bar.
- `IArchiveSession.SupportsPause` is a real capability. Where it is false, the UI shows
  *Pause is not available for this format* instead of a button that silently does nothing.
- Progress flows through a bounded channel with drop-oldest semantics and is coalesced to 10 updates per
  second on the UI thread.
- Cancellation propagates from the UI command into the provider's innermost copy loop; a cancelled
  operation leaves no partial file under a final name.

## Consequences
- ViewModels must handle `null` totals as a first-class state, which shows up in UI tests.
- A million-entry operation does not flood the dispatcher.
- Users can trust the numbers we do show, which is worth more than a bar that always moves.
