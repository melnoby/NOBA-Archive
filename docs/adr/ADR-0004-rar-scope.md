# ADR-0004 — RAR is read-only, permanently

**Status:** Accepted (Phase 0, 2026-08-19)

## Context
The unRAR licence, as published by RARLAB and reproduced in 7-Zip's own licence file, permits using the
UnRAR sources to *handle* RAR archives free of charge but forbids using them to *re-create the RAR
compression algorithm*, which is proprietary. Redistribution of modified sources requires a clear
statement that the code may not be used to develop a RAR-compatible archiver.

## Decision
NOBA Archive supports RAR **open, list, extract, test, and decrypt with a user-supplied password**.
It will **never** support RAR creation, update, add, delete, or recovery-record generation.
RAR does not appear in the Create Archive format list at all.

## Consequences
- `NOTICE.txt` ships with the unRAR licence text verbatim and the required statement; the About dialog
  links to it; the interop source file restates it in a header comment.
- The UI disables RAR-mutating commands with an explanatory tooltip rather than failing after a click.
- Users who need RAR creation are told plainly to use WinRAR. That honesty is a feature (rule 8).
- No roadmap item may propose RAR creation.
