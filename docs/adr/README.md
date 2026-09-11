# Architecture Decision Records

Each record states the context, the options considered, the decision, and the consequences.
A settled decision is not revisited without new evidence (rule 52).

| ID | Decision | Status |
|---|---|---|
| [0001](ADR-0001-ui-stack.md) | WinUI 3 + .NET 10 for the desktop UI | Accepted (Phase 0) |
| [0002](ADR-0002-archive-backends.md) | Hybrid backend: managed for ZIP/TAR, native 7z.dll for 7Z/RAR | Accepted (Phase 0) |
| [0003](ADR-0003-sevenzip-interop.md) | How to talk to 7z.dll | Proposed — spike in Phase 3 |
| [0004](ADR-0004-rar-scope.md) | RAR is read-only, permanently | Accepted (Phase 0) |
| [0005](ADR-0005-extraction-writer.md) | Providers decode; only our writer touches the filesystem | Accepted (Phase 0) |
| [0006](ADR-0006-packaging.md) | Unpackaged EXE + MSI, with a sparse package for the shell menu | Accepted (Phase 0) |
| [0007](ADR-0007-progress-and-pause.md) | Nullable progress totals and honest pause capability | Accepted (Phase 0) |
| [0008](ADR-0008-nobaos-surface.md) | Engine library + CLI as the NobaOS integration surface | Accepted (Phase 0) |
| [0009](ADR-0009-cli-distribution.md) | `noba-ar.exe` ships in the standard installer, over the same engine | Accepted (Phase 0) — resolves D6 |
| [0010](ADR-0010-minimum-os.md) | Minimum Windows 10 1809; tested baseline 22H2 / Windows 11 | Accepted (Phase 0) — resolves D7 |
| [ADR-0011](ADR-0011-design-foundation.md) | Design foundation: tokens, bidirectionality, typography | Accepted | 2026-08-20 |
