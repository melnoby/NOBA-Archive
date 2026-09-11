# DROP 08 Phase 5 — VM TOOLCHAIN

**Exact components, exact versions, exact verification. Nothing here starts a gate.**

Every version below is the one the **passing Gate 1 run actually used**
(`DROP08_PHASE5_G1_REPORT.md` §6.2), and every requirement is derived from what the harness
**actually invokes**, read out of the scripts. Nothing is reconstructed from a general idea of what
a C++/.NET machine needs.

---

## 0. Read this before installing anything

**Checklist step 1 comes first, and it is not optional.**

```powershell
cd <repo on the VM>
.\harness\phase5-e\run-e2.ps1 -PreconditionsOnly
```

It reads two registry values, creates nothing, changes nothing, and needs only Windows 11 and
PowerShell 5.1 — **no toolchain at all**. Exit **2** means the VM carries the sideloading block and
is unsuitable; installing ~11 GB afterwards would buy nothing. The development machine has returned
2 three times, byte-identical, which is the entire reason a separate VM exists.

Install the toolchain only after step 1 exits **0**.

---

## 1. What is required

| | Component | Exact version (Gate 1) | Required by — read from the script |
|---|---|---|---|
| 1 | **.NET SDK** | **10.0.400** (runtime `Microsoft.NETCore.App` **10.0.11**) | `run-g1.ps1:150`, `run-e1.ps1:86` take `Get-Command dotnet`. Every project targets `net10.0` (`Directory.Build.props:18`). |
| 2 | **ASP.NET Core shared framework 10.0.x** | ships **inside** the SDK above | `Directory.Build.targets` gives every `NobaUsesExtensions` project a `FrameworkReference` to `Microsoft.AspNetCore.App` when `NobaOfflineBuild=true`. The bridge sets it, and so does the whole Engine chain. **A runtime-only install is not enough.** |
| 3 | **VS 2022 Build Tools — C++ workload** | MSVC toolset **14.44.35207** | `run-g1.ps1:243` queries `vswhere -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64`; `:251` needs `VC\Auxiliary\Build\vcvars64.bat`; `cl.exe` builds `g1host.exe`, the E1 probe + client, and the E2 stub + host. |
| 4 | **Windows 11 SDK** | **10.0.26100** | `run-e2.ps1:431-436` requires `makeappx.exe` **and** `signtool.exe` in the *same* `Windows Kits\10\bin\10.*\x64` directory. `cl.exe` also links `ole32.lib oleaut32.lib uuid.lib shlwapi.lib` from it. |
| 5 | **Windows PowerShell 5.1** | ships with Windows | every harness script. **`pwsh` 7 is not required and is not installed by default.** |

### Explicitly NOT required

| | Why not |
|---|---|
| **Windows App SDK** / `Microsoft.Windows.SDK.BuildTools` | only `NOBA.Archive.App` references them, and that project is not in `NOBA.Archive.sln` and is not reachable from the bridge. |
| **NuGet network access** | `NobaOfflineBuild=true` swaps the `Microsoft.Extensions.*` packages for the shared framework. Every harness script sets it. The bridge has **no** `PackageReference`. |
| **Visual Studio IDE** | Build Tools is sufficient; `vswhere -products *` matches either. |
| **pwsh 7** | see above. |
| **A CA-issued certificate** | E2 generates its own DEV-ONLY certificate. `0x80073CFF` is about policy, not about who signed. |

Disk: roughly **10 GB** for Build Tools + SDK, **1 GB** for the .NET SDK.

---

## 2. Install — on the VM only, elevated

**One script does exactly this and nothing else:**

```powershell
# elevated
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\vm\install-toolchain.ps1
```

It refuses to run unelevated, checks free space, downloads both installers to `%TEMP%`,
**verifies each one is validly Authenticode-signed by Microsoft before executing it**, installs the
SDK and the three VS components, deletes the installers, and tells you whether a reboot is required.
It does **not** run the verification — keeping install and verification separate is the point of
having a verification. `-DownloadOnly` stages and signature-checks without installing anything.

Authenticode rather than a pinned hash: both installers run with Administrator rights, so provenance
is not optional, and the VS bootstrapper at `aka.ms/vs/17/release` is a rolling build with no stable
published hash that a pinned SHA could cover. To pin the SDK by hash as well, the published
checksums are at `https://builds.dotnet.microsoft.com/dotnet/checksums/10.0.11-sha.txt`.

The two sections below are what the script runs, for the record.

### 2.1 .NET SDK 10.0.400 (pinned)

```powershell
$url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe'
$exe = "$env:TEMP\dotnet-sdk-10.0.400-win-x64.exe"
Invoke-WebRequest -Uri $url -OutFile $exe
Start-Process -FilePath $exe -ArgumentList '/install','/quiet','/norestart' -Wait -PassThru | Select-Object ExitCode
```

Pin the version rather than taking `winget install Microsoft.DotNet.SDK.10`. Checklist steps 4 and 5
re-measure Gate 1 and re-establish E1 on this VM, and an SDK delta becomes a variable in numbers
that are meant to be compared against the 26200 run.

### 2.2 VS 2022 Build Tools — the minimal component set

```powershell
$url = 'https://aka.ms/vs/17/release/vs_BuildTools.exe'
$exe = "$env:TEMP\vs_BuildTools.exe"
Invoke-WebRequest -Uri $url -OutFile $exe
Start-Process -FilePath $exe -Wait -PassThru -ArgumentList @(
    '--quiet','--wait','--norestart','--nocache',
    '--add','Microsoft.VisualStudio.Workload.VCTools',
    '--add','Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
    '--add','Microsoft.VisualStudio.Component.Windows11SDK.26100'
) | Select-Object ExitCode
```

| ID | What it is |
|---|---|
| `Microsoft.VisualStudio.Workload.VCTools` | Desktop development with C++ (Build Tools) |
| `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` | MSVC v143 x64/x86 build tools — **the exact string `vswhere -requires` asks for** |
| `Microsoft.VisualStudio.Component.Windows11SDK.26100` | Windows 11 SDK 10.0.26100 — `makeappx`, `signtool`, and the link libraries |

`--includeRecommended` is deliberately **omitted**: the three IDs above are what the harness uses,
and a smaller install is a smaller set of variables.

**Exit codes:** `0` success, **`3010` success but a reboot is required** — reboot before verifying,
or `vcvars64.bat` may not resolve. Anything else is a failure; the log is under
`%TEMP%\dd_setup_*.log`.

---

## 3. Verify — this is the deliverable

```powershell
cd <repo on the VM>
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\vm\verify-toolchain.ps1
```

Unelevated is fine and preferred. The script:

| § | Checks |
|---|---|
| 1 | Windows 11 build ≥ 22000, PowerShell edition/version, whether the shell is elevated |
| 2 | `dotnet` on PATH, `--version`, `--list-sdks`, `--list-runtimes` — **including `Microsoft.AspNetCore.App`** |
| 3 | `vswhere.exe` at the exact path the harness uses, the exact `-requires` query, `vcvars64.bat`, `cl.exe` **after importing vcvars into this process**, `VCToolsVersion` |
| 4 | `makeappx.exe` **and** `signtool.exe` in one SDK bin, the selected SDK version, and `ole32/oleaut32/uuid/shlwapi.lib` on `LIB` |
| 5 | a contained **compile probe**: `cl.exe` builds and links a COM DLL with DEF exports in `%TEMP%`, then deletes it |

§5 exists because four executables existing is not the same thing as a toolchain that builds. It is
the smallest test that can falsify "this VM can build the harness", it touches nothing in the
repository, and it registers nothing. `-SkipCompileProbe` turns it off.

### What the script does not do

- **starts no gate** — no `run-g1.ps1`, no `run-e1.ps1`, no `run-e2.ps1`
- **reads no policy** — no Appx key, no `AppModelUnlock`, no Developer Mode
- **creates no certificate**, no package, no registration
- **writes nothing into the repository**

### Result lines

| Line | Meaning |
|---|---|
| `TOOLCHAIN VERIFIED: N checks, 0 failures, 0 differences` | exactly the Gate 1 toolchain. Exit 0. |
| `TOOLCHAIN USABLE: ... N difference(s)` | everything works, but a version differs from the Gate 1 run. Exit 0. **A difference is not a failure — it is a variable in the numbers steps 4 and 5 will produce. Record it.** |
| `TOOLCHAIN INCOMPLETE: ...` | something the harness invokes is missing, named. Exit 1. |

---

## 4. Then stop

The toolchain being ready is **not** a gate result. After verification the next actions are
checklist steps 2 → 3 → 4 → 5 → 6 → 7, in that order, and each is a gate that stops at the first
failure.

**Not to be done on the VM at any point:** no Developer Mode change, no Group Policy change, no
`AppModelUnlock` change, no MDM enrolment, no certificate installed by hand, no elevated
`run-e2.ps1`. If the VM needs a policy changed to get past step 1, it is the wrong VM.

---

## 5. Sources

Versions: `DROP08_PHASE5_G1_REPORT.md` §6.2 (the passing run's recorded environment).
Requirements: `harness/phase5-g1/run-g1.ps1`, `harness/phase5-e/run-e1.ps1`,
`harness/phase5-e/run-e2.ps1`, `Directory.Build.props`, `Directory.Build.targets`,
`harness/phase5-g1/bridge/NOBA.Phase5.G1.Bridge.csproj`.
Component IDs and the SDK/runtime pairing were confirmed against Microsoft's published tables, not
recalled — see the links in the delivery note.
