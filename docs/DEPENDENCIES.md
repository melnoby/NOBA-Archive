# NOBA Archive — Dependency Matrix

> **Status:** Phase 0 proposal. Versions marked *(verify)* must be pinned to the exact current version
> at the start of Phase 1 and recorded in `Directory.Packages.props`.
> **Date:** 2026-08-19

## 1. Runtime and SDK

| Component | Version | Licence | Notes |
|---|---|---|---|
| .NET | **10.0 (LTS)** | MIT | .NET 8 (LTS) and .NET 9 (STS, extended to 24 months) both reach end of support 2026-11-10 — under three months away; starting on either would be a mistake |
| Windows App SDK | **2.x stable** — 2.4.0 published 2026-08-13 *(pin one version at Phase 1)* | **Microsoft Software License Terms** (proprietary; redistribution permitted under those terms). Parts of the *source* are MIT, but the shipped NuGet package and runtime are not | Backward compatible to Windows 10 1809; stable releases carry ~12 months of servicing (2.0: 2026-04-29 → 2027-04-29), so plan a yearly SDK bump. Redistribution terms go into `NOTICE.txt` |
| Windows SDK | 10.0.22621.0 or later | — | Build requirement |
| Visual Studio 2026 / MSBuild + Windows App SDK workload | current | — | WinUI 3 requires Windows to build |

## 2. Product dependencies

| Package | Used by | Licence | Purpose | Risk |
|---|---|---|---|---|
| `Microsoft.WindowsAppSDK` | App, UI | Microsoft Software License Terms | WinUI 3 | Low — Microsoft, re-committed at Build 2026 |
| `Microsoft.Extensions.DependencyInjection` | App, Cli | MIT | Composition root | Low |
| `Microsoft.Extensions.Logging` (+ `Serilog.Extensions.Logging`, file sink) | Infrastructure | MIT / Apache-2.0 | Structured logging | Low |
| `Microsoft.Extensions.Configuration.*` | Infrastructure | MIT | Settings binding | Low |
| `CommunityToolkit.Mvvm` | UI | MIT | Source-generated MVVM | Low |
| `SharpCompress` **0.50.x** *(verify exact)* | Formats.Managed | **MIT** | TAR/GZIP/BZIP2/XZ/LZip/Zstd; ZIP AES + ZipCrypto **read only** (it cannot write encrypted ZIP — that routes to `7z.dll`); RAR read fallback | Medium — pre-1.0 versioning, breaking changes between minors. Pin exactly; wrap behind our provider so a swap is contained |
| `System.IO.Compression` (BCL) | Formats.Managed | MIT | ZIP baseline, Zip64, Deflate | Low |
| **`7z.dll` 26.02** (2026-06-25) | Formats.SevenZipNative | **LGPL-2.1+**, BSD-2/BSD-3 parts, unRAR restriction on the RAR handler | 7Z create/extract, RAR & bonus-format extraction, solid, filename encryption | Medium — native code, own CVE history (see THREAT_MODEL.md §1); must track upstream patches |
| `SharpSevenZip` 2.0.109 *(only if the Phase 3 spike selects option A)* | Formats.SevenZipNative | **LGPL-3.0-or-later** | Managed wrapper over `7z.dll` | Medium — LGPL-3.0 forbids merging/trimming that assembly; must stay a separate replaceable DLL |

## 3. Test / build-only dependencies

| Package | Licence | Purpose |
|---|---|---|
| `xunit.v3`, `xunit.runner.visualstudio` | Apache-2.0 | Test framework |
| `NSubstitute` | BSD-3 | Test doubles |
| `Microsoft.NET.Test.Sdk`, `coverlet.collector` | MIT | Test host, coverage |
| `Appium.WebDriver` + WinAppDriver *(verify current UI-automation story for WinUI 3 on Windows 11)* | Apache-2.0 | UI tests |
| `WixToolset.Sdk` **v6 or v7** *(v7.0.0 is published; pin the exact version at Phase 9 and check the current licensing/FireGiant terms)* | MS-RL | MSI installer |
| Roslyn analyzers, `Microsoft.CodeAnalysis.NetAnalyzers`, `StyleCop.Analyzers` | MIT / Apache-2.0 | Code quality gates |

> **Resolved in Phase 1 (risk R7 closed):** `FluentAssertions` is **not** taken as a dependency. Its
> licensing changed to a commercial model for some uses, and the assertions the suites actually need are
> covered by plain xUnit asserts. One fewer dependency, one fewer licence to track, and no behavioural
> loss — failure messages stayed readable. If a future suite genuinely needs fluent assertions,
> `Shouldly` (BSD) is the substitute to evaluate, and this table is where that decision gets recorded.

## 4. Dependency policy

1. Every addition requires: licence check, maintenance check (last release, open-issue trend),
   transitive-dependency review, **a compatibility check against Windows 10 1809 (build 17763)** —
   not just against current Windows — and a note in this file. No exceptions. A dependency that drops
   1809 is a reason to reconsider the dependency, or to raise the floor deliberately via ADR-0010,
   never to quietly ship something that fails there.
2. Central package management (`Directory.Packages.props`) — no version specified in a `.csproj`.
3. NuGet package signature validation on; `dotnet list package --vulnerable --include-transitive` runs in CI
   and fails the build on any *High* or *Critical* advisory.
4. Copyleft rules: LGPL components stay as separately replaceable dynamic libraries/assemblies.
   **No GPL/AGPL dependency may be introduced at any time.**
5. Native binaries are recorded with version + SHA-256 in `installer/native-manifest.json`.
6. An SBOM (CycloneDX) is generated for every release build.

## 5. Deployment implications of the licence choices

| Constraint | Consequence |
|---|---|
| LGPL dynamic-link requirement | **No single-file publish** that embeds `SharpSevenZip`, and no ILMerge. Framework-dependent or self-contained multi-file publish only. |
| LGPL relinking requirement | `7z.dll` ships as a loose, user-replaceable file with an override setting |
| Full trimming | Not used for LGPL assemblies; also incompatible with WinUI 3's reflection-heavy XAML in 1.0 |
| Native AOT | Out of scope for 1.0 (WinUI 3 does not support it). `NOBA.Archive.Cli` could adopt it later **only if** the Phase 3 spike selects option B — under option A the CLI would reference the LGPL-3.0 `SharpSevenZip`, and AOT would compile it in, breaking the separate-assembly requirement |

## 6. Offline build profile

`NobaOfflineBuild=true` swaps the `Microsoft.Extensions.*` packages for the
`Microsoft.AspNetCore.App` shared framework (which contains the same assemblies) and xUnit v3 for the
dependency-free harness in `NOBA.Archive.TestKit`. It exists so the solution can be built and tested in
an environment that cannot reach nuget.org.

It is **not** the shipping configuration. The default build restores the pinned packages above, and the
Windows build machine must use it. Test sources are byte-identical in both modes — the harness mirrors
the `[Fact]` / `[Theory]` / `[InlineData]` / `Assert` surface the tests use — so switching is a build
property, never an edit.

## 7. Facts verified on 2026-08-19 (and how)

Everything below was checked against a primary source on the date shown. Anything not in this table is
a *proposal* and must be confirmed before it is relied upon.

| Fact | Source |
|---|---|
| .NET 8 (LTS) and .NET 9 (STS, extended to 24 months) both end support 2026-11-10 | Microsoft .NET Blog, "…End of Support on November 10, 2026" |
| Windows App SDK stable 2.4.0 published 2026-08-13; backward compatible to Windows 10 1809; ~12-month servicing per stable release | Microsoft Learn, Windows App SDK release channels; NuGet package page |
| 7-Zip 26.02 released 2026-06-25; "7-Zip Extra" ships the standalone `7z.dll` | 7-zip.org download page |
| 7-Zip licence: LGPL-2.1-or-later with BSD-2/BSD-3 parts and the unRAR restriction | 7-zip.org/license.txt |
| unRAR licence text (may not be used to re-create the RAR compression algorithm) | 7-zip.org/license.txt; Fedora Project licensing wiki |
| SharpCompress is MIT; supports RAR/7z read, ZIP/TAR/GZIP/BZIP2 write; targets net10.0 among others | SharpCompress GitHub README and FORMATS.md |
| SharpSevenZip 2.0.109 published 2026-07-10; LGPL-3.0-or-later; does **not** bundle `7z.dll` | NuGet package page |
| Archiver CVEs cited in the threat model (WinRAR CVE-2025-8088, CVE-2025-6218; 7-Zip CVE-2025-11001/-11002, CVE-2025-55188) | vendor and security-vendor advisories |
| WixToolset.Sdk 7.0.0 is published | NuGet |

Unverified and explicitly marked as such: exact SharpCompress patch version to pin, current
`FluentAssertions` licence terms, the UI-automation story for WinUI 3 on current Windows 11, and the
precise handler list of the shipped `7z.dll` build.
