#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - GATE 1: runs the G.1 feasibility harness on real Windows.
.DESCRIPTION
    G.1 asked whether a native host can activate the managed NOBA shell model fast enough, and safely
    enough, for a Windows Explorer context menu. It was written and executed on Linux first because
    that is where it could be iterated. **Linux numbers are not Windows evidence** - CLR startup,
    assembly loading off NTFS, the file system cache and Defender's on-access scanning are all
    different, and the entire question is about a Windows surface. This script exists so the identical
    measurement runs where it counts.

    WHAT IT REPORTS, in the order the review asked for them:
      1  cold-start measurements        section 3, aggregated over -ColdRuns separate processes
      2  warm-start measurements        section 3, median and p95 over -WarmIterations samples per process
      3  menu construction cost         section 3, isolated - first menu vs steady-state menu
      4  failure behaviour              section 3, per run: managed exception, malformed input, slow call
      5  concurrency behaviour          section 3 warm contention, section 5 cold race
      6  exact build/runtime environment section 1, captured before anything is built

    WHAT IT DOES NOT AND CANNOT ESTABLISH
      Question E - isolation from explorer.exe. That is a property of com:SurrogateServer
      registration, which G.1 is not authorised to build. **A standalone .exe host is not evidence of
      isolation in either direction**, and this script does not claim it is. Gate 2 is separate and is
      not closed by running this.

    NOTHING IS REGISTERED. No COM class, no CLSID, no package, no certificate, no registry write.
    This script builds two binaries, runs one of them several times, and writes a transcript.

.PARAMETER SkipBridgeBuild
    Reuse the managed bridge already under bridge\bin. Use only when it was just built.

.PARAMETER SkipHostBuild
    Reuse host\g1host.exe.

.PARAMETER WarmIterations
    Steady-state samples per process. Default 50, matching the Linux baseline.

.PARAMETER ColdRuns
    How many times to start the process from scratch. Cold start is the figure that decides whether
    GetState needs E_PENDING, and a single sample of it is not a measurement - the first process pays
    for a cold file cache and the ones after it do not. Default 5; the report quotes the range.

.PARAMETER NoTranscript
    Do not write a transcript file. The default writes one, because evidence that has to be
    copy-pasted out of a console tends to arrive incomplete.

.EXAMPLE
    .\harness\phase5-g1\run-g1.ps1
.EXAMPLE
    .\harness\phase5-g1\run-g1.ps1 -ColdRuns 10
#>
[CmdletBinding()]
param(
    [switch]$SkipBridgeBuild,
    [switch]$SkipHostBuild,
    [int]$WarmIterations = 50,
    [int]$ColdRuns = 5,
    [switch]$NoTranscript
)

$ErrorActionPreference = 'Stop'

$harness   = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = (Resolve-Path (Join-Path $harness '..\..')).Path
$bridgeDir = Join-Path $harness 'bridge'
$hostDir   = Join-Path $harness 'host'
$resultDir = Join-Path $harness 'results'

if (-not (Test-Path $resultDir)) { New-Item -ItemType Directory -Path $resultDir -Force | Out-Null }

$runStamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$transcript = Join-Path $resultDir "g1-windows-$runStamp.log"
if (-not $NoTranscript) {
    try { Start-Transcript -Path $transcript -Force | Out-Null } catch { $transcript = $null }
}

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Text
    Write-Host ('=' * 78)
}

function Write-Fail([string]$Text) { Write-Host "FAIL  $Text" -ForegroundColor Red }
function Write-Ok  ([string]$Text) { Write-Host "ok    $Text" -ForegroundColor Green }

function Get-Ms([string[]]$Lines, [string]$Label) {
    foreach ($l in $Lines) {
        if ($l -match [regex]::Escape($Label) + '\s+([0-9]+\.[0-9]+)\s+ms') { return [double]$matches[1] }
    }
    return $null
}

function Show-Range([string]$Label, [double[]]$Values) {
    if (-not $Values -or $Values.Count -eq 0) { Write-Host ("  {0,-44} (no samples)" -f $Label); return }
    $min = ($Values | Measure-Object -Minimum).Minimum
    $max = ($Values | Measure-Object -Maximum).Maximum
    $avg = ($Values | Measure-Object -Average).Average
    Write-Host ("  {0,-44} min {1,8:F2}  avg {2,8:F2}  max {3,8:F2} ms" -f $Label, $min, $avg, $max)
}

Write-Section 'DROP 08 Phase 5 - GATE 1: G.1 harness on Windows'
Write-Host "repo    : $repoRoot"
Write-Host "harness : $harness"
if ($transcript) { Write-Host "log     : $transcript" } else { Write-Host 'log     : (transcript unavailable - console only)' }

# =================================================================================================
# 1. EXACT BUILD / RUNTIME ENVIRONMENT  (review item 6)
#
# Captured first and unconditionally. Three Windows runs of the Phase 4 gate were wasted against a
# source tree that had never been extracted, and every one of them reported PASS. A transcript that
# does not name what it ran is not evidence, and a timing transcript that does not name the machine
# is not comparable to anything.
# =================================================================================================
Write-Section '1. EXACT BUILD / RUNTIME ENVIRONMENT'

Write-Host "timestamp     : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
Write-Host "powershell    : $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion)"

try {
    $os = Get-CimInstance Win32_OperatingSystem
    Write-Host "os            : $($os.Caption) build $($os.BuildNumber) ($($os.OSArchitecture))"
} catch { Write-Host "os            : $([System.Environment]::OSVersion.VersionString)" }

try {
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    Write-Host "cpu           : $($cpu.Name.Trim())"
    Write-Host "cores/threads : $($cpu.NumberOfCores) / $($cpu.NumberOfLogicalProcessors)"
} catch { Write-Host 'cpu           : (unavailable)' }

try {
    $cs = Get-CimInstance Win32_ComputerSystem
    Write-Host ("ram           : {0:N1} GB" -f ($cs.TotalPhysicalMemory / 1GB))
} catch { Write-Host 'ram           : (unavailable)' }

# Defender's on-access scanning is not a detail here. It runs on every assembly load, which is
# exactly the cost this harness measures, and a machine with it disabled will produce a materially
# faster cold start than the machine a user actually has. Recorded so the numbers can be read.
try {
    $mp = Get-MpComputerStatus -ErrorAction Stop
    Write-Host "defender rtp  : $($mp.RealTimeProtectionEnabled)  (affects assembly-load timing)"
} catch { Write-Host 'defender rtp  : (unknown - Get-MpComputerStatus unavailable)' }

try {
    $plan = (powercfg /getactivescheme) -join ' '
    Write-Host "power plan    : $plan"
} catch { Write-Host 'power plan    : (unavailable)' }

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) { Write-Fail 'dotnet is not on PATH.'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
$dotnetRoot = Split-Path -Parent $dotnetCmd.Source
Write-Host "dotnet root   : $dotnetRoot"
Write-Host "dotnet sdk    : $(& dotnet --version)"
Write-Host 'installed runtimes:'
& dotnet --list-runtimes | ForEach-Object { Write-Host "  $_" }

$fxrRoot = Join-Path $dotnetRoot 'host\fxr'
if (-not (Test-Path $fxrRoot)) { Write-Fail "no hostfxr under $fxrRoot"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }

# Newest by version, not by name sort: 10.0.9 must not beat 10.0.11.
$fxrPick = Get-ChildItem $fxrRoot -Directory |
    ForEach-Object {
        $v = $null
        if ([version]::TryParse($_.Name, [ref]$v)) { [pscustomobject]@{ Version = $v; Path = $_.FullName } }
    } | Sort-Object Version | Select-Object -Last 1

if (-not $fxrPick) { Write-Fail "no parseable hostfxr version under $fxrRoot"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
$hostfxr = Join-Path $fxrPick.Path 'hostfxr.dll'
if (-not (Test-Path $hostfxr)) { Write-Fail "hostfxr.dll missing at $hostfxr"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
Write-Host "hostfxr       : $hostfxr"
Write-Host "hostfxr ver   : $((Get-Item $hostfxr).VersionInfo.FileVersion)"

$stampFile = Join-Path $repoRoot 'src\NOBA.Archive.Cli\BuildStamp.cs'
$stampHit  = if (Test-Path $stampFile) { Select-String -Path $stampFile -Pattern 'Value\s*=\s*"([^"]+)"' | Select-Object -First 1 } else { $null }
if ($stampHit) {
    Write-Host "build stamp   : $($stampHit.Matches[0].Groups[1].Value)"
} else {
    Write-Host 'build stamp   : (not found - this is a fingerprint line, not a gate)'
}

# The harness sources, by hash. This is what makes the transcript checkable against the tree that
# produced it rather than against a tree that merely has the same file names.
Write-Host 'harness sources (SHA256):'
foreach ($f in @(
    (Join-Path $bridgeDir 'BridgeExports.cs'),
    (Join-Path $bridgeDir 'NOBA.Phase5.G1.Bridge.csproj'),
    (Join-Path $hostDir   'g1host.cpp'))) {
    if (Test-Path $f) {
        Write-Host ("  {0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.Substring(0, 16), (Split-Path -Leaf $f))
    } else {
        Write-Fail "harness source missing: $f"
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 1
    }
}

# =================================================================================================
# 2. BUILD
# =================================================================================================
Write-Section '2. BUILD'

$bridgeProj = Join-Path $bridgeDir 'NOBA.Phase5.G1.Bridge.csproj'
$bridgeOut  = Join-Path $bridgeDir 'bin\Debug\net10.0'

Write-Host '--- managed bridge ---'
if ($SkipBridgeBuild) {
    Write-Host 'skipped by request'
} else {
    $env:NOBA_OFFLINE_BUILD = '1'
    & dotnet build $bridgeProj -c Debug -p:NobaOfflineBuild=true --nologo
    if ($LASTEXITCODE -ne 0) { Write-Fail "bridge build failed (exit $LASTEXITCODE)"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
}

$bridgeDll  = Join-Path $bridgeOut 'NOBA.Phase5.G1.Bridge.dll'
$runtimeCfg = Join-Path $bridgeOut 'NOBA.Phase5.G1.Bridge.runtimeconfig.json'
foreach ($f in @($bridgeDll, $runtimeCfg)) {
    if (-not (Test-Path $f)) { Write-Fail "expected output missing: $f"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
}
Write-Ok "bridge: $bridgeDll"

# MSVC is located through vswhere rather than assumed on PATH, because a plain PowerShell prompt is
# not a developer prompt and cl.exe will not be there. If MSVC cannot be found this stops; it does not
# fall back to another toolchain and report the result as though it were the same measurement.
Write-Host ''
Write-Host '--- native host ---'
$hostExe = Join-Path $hostDir 'g1host.exe'
$hostSrc = Join-Path $hostDir 'g1host.cpp'

if ($SkipHostBuild) {
    if (-not (Test-Path $hostExe)) { Write-Fail "-SkipHostBuild given but $hostExe does not exist"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    Write-Host 'skipped by request'
} else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        Write-Fail 'vswhere.exe not found. Visual Studio Build Tools with the C++ workload are required.'
        Write-Host '      Install "Build Tools for Visual Studio" with the "Desktop development with C++"'
        Write-Host '      workload, then re-run this script.'
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 1
    }

    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) {
        Write-Fail 'no Visual Studio installation with the C++ toolset (VC.Tools.x86.x64) was found.'
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 1
    }
    Write-Host "vs install    : $vsPath"

    $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path $vcvars)) { Write-Fail "vcvars64.bat not found at $vcvars"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }

    # The developer environment is imported into this process rather than used from a child cmd, so
    # compiler diagnostics land in this transcript where a failure can actually be read.
    $envDump = & cmd.exe /c "`"$vcvars`" >nul 2>&1 && set"
    if ($LASTEXITCODE -ne 0) { Write-Fail "vcvars64.bat failed (exit $LASTEXITCODE)"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    foreach ($line in $envDump) {
        if ($line -match '^([^=]+)=(.*)$') {
            Set-Item -Path ('Env:' + $matches[1]) -Value $matches[2] -ErrorAction SilentlyContinue
        }
    }

    $cl = Get-Command cl.exe -ErrorAction SilentlyContinue
    if (-not $cl) { Write-Fail 'cl.exe still not on PATH after importing vcvars64.bat.'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    Write-Host "cl            : $($cl.Source)"
    Write-Host "toolset       : $env:VCToolsVersion"
    Write-Host "windows sdk   : $env:WindowsSDKVersion"

    if (Test-Path $hostExe) { Remove-Item $hostExe -Force }

    Push-Location $hostDir
    try {
        # /EHsc      std::vector / std::string / std::thread need unwinding semantics
        # /O2        this measures time; an unoptimised host measures the harness
        # /std:c++17 matches the g++ invocation that produced the Linux baseline
        & cl.exe /nologo /std:c++17 /EHsc /O2 /W3 /Fe:g1host.exe g1host.cpp
        $clExit = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    if ($clExit -ne 0 -or -not (Test-Path $hostExe)) {
        Write-Fail "cl.exe failed (exit $clExit)"
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 1
    }
}
Write-Ok "host: $hostExe"

# =================================================================================================
# 3. MEASURE  (review items 1-5)
#
# ColdRuns separate processes. Cold start is the figure that decides whether GetState needs
# E_PENDING, so it is quoted as a range across processes rather than as one number: the first process
# pays for a cold file cache and the ones after it do not, and reporting only the fastest would be
# choosing the answer rather than measuring it.
# =================================================================================================
Write-Section "3. MEASURE  ($ColdRuns cold processes, $WarmIterations warm iterations each)"

$coldTotals = @(); $coldFirsts = @(); $warmEngine = @()
$warmMedian = @(); $warmP95    = @(); $concP95    = @()
$failures   = 0
$faultCodes = @(); $survived = @(); $slowMs = @(); $concFail = @()

for ($run = 1; $run -le $ColdRuns; $run++) {
    Write-Host ''
    Write-Host ("--- run $run of $ColdRuns " + ('-' * 44))

    $output = & $hostExe $hostfxr $bridgeOut $runtimeCfg $WarmIterations --split 2>&1 | ForEach-Object { "$_" }
    $exit = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }

    if ($exit -ne 0) { Write-Fail "run $run exited $exit"; $failures++; continue }

    $t  = Get-Ms $output 'TOTAL cold: process start -> first menu'
    $f  = Get-Ms $output 'first NobaShellQuery (engine + first menu)'
    $e  = Get-Ms $output 'NobaShellWarm (DI + provider registry only)'
    $md = Get-Ms $output 'warm median'
    $p9 = Get-Ms $output 'warm p95'
    $cp = Get-Ms $output 'concurrent p95'
    $sl = Get-Ms $output 'slow call (blocks the caller)'

    # All of them are required, not just the two the summary quotes. An earlier version accepted a
    # null for the rest and let the aggregate print "(no samples)" at the bottom, which reads like
    # "this run had nothing to say" rather than "the driver could not find the line it was looking
    # for". One of the labels had in fact drifted, and the run was reported as good.
    $parsed = @{
        'TOTAL cold'          = $t
        'first menu'          = $f
        'engine construction' = $e
        'warm median'         = $md
        'warm p95'            = $p9
        'concurrent p95'      = $cp
        'slow call'           = $sl
    }
    $missing = @($parsed.Keys | Where-Object { $null -eq $parsed[$_] } | Sort-Object)
    if ($missing.Count -gt 0) {
        Write-Fail "run $run - the driver could not parse: $($missing -join ', ')"
        $failures++
        continue
    }

    $coldTotals += $t; $coldFirsts += $f; $warmEngine += $e
    $warmMedian += $md; $warmP95 += $p9; $concP95 += $cp; $slowMs += $sl

    # Failure behaviour, read from the run rather than assumed.
    $faultLine = $output | Where-Object { $_ -match 'return code\s*:\s*(-?\d+)' } | Select-Object -First 1
    if ($faultLine -and $faultLine -match 'return code\s*:\s*(-?\d+)') { $faultCodes += [int]$matches[1] }
    $survived  += [bool](($output | Where-Object { $_ -match 'the host survived and still works' }).Count -gt 0)
    $concFail  += [bool](($output | Where-Object { $_ -match 'CONTENDED CALLS FAILED' }).Count -gt 0)
}

# =================================================================================================
# 4. AGGREGATE
# =================================================================================================
Write-Section '4. AGGREGATE - Windows, this machine'

Write-Host '(1) COLD START'
Show-Range 'cold total (process -> first menu)' $coldTotals

Write-Host ''
Write-Host '(3) MENU CONSTRUCTION COST'
Show-Range 'engine construction (NobaShellWarm)' $warmEngine
Show-Range 'first menu, engine already warm'     $coldFirsts
Show-Range 'steady-state menu (warm median)'     $warmMedian

Write-Host ''
Write-Host '(2) WARM START'
Show-Range 'warm median' $warmMedian
Show-Range 'warm p95'    $warmP95

Write-Host ''
Write-Host '(5) CONCURRENCY - warm, 4 threads x 25 calls'
Show-Range 'concurrent p95' $concP95
$anyConcFail = ($concFail | Where-Object { $_ }).Count
if ($anyConcFail -gt 0) { Write-Fail "$anyConcFail run(s) had a contended call fail" }
else { Write-Ok 'no contended call failed in any run' }

Write-Host ''
Write-Host '(4) FAILURE BEHAVIOUR'
$codes = ($faultCodes | Sort-Object -Unique) -join ', '
Write-Host "  managed exception return code(s)         : $codes  (expected -99)"
$allSurvived = ($survived | Where-Object { -not $_ }).Count -eq 0
if ($allSurvived) { Write-Ok 'the host survived every injected fault and served the next call' }
else { Write-Fail 'at least one run did NOT recover from the injected fault' }
Show-Range 'slow call (2s injected, blocks caller)' $slowMs
Write-Host '  The bridge owns no deadline - by design. The caller does, and on the real surface'
Write-Host "  that caller mechanism is GetState's fOkToBeSlow / E_PENDING."

Write-Host ''
if ($failures -gt 0) {
    Write-Fail "$failures of $ColdRuns runs did not produce a usable measurement."
    Write-Host '      The figures above cover only the runs that succeeded. Do not quote them as the'
    Write-Host '      Windows G.1 result until every run is clean.'
    if ($transcript) { Stop-Transcript | Out-Null }
    exit 1
}
Write-Ok "all $ColdRuns runs completed"

# =================================================================================================
# 5. COLD RACE  (review item 5, the half that warm contention cannot show)
#
# Its own process, because it has to be the first thing that touches the engine. Several threads
# arriving before the engine exists is the only case that can break the double-checked lock in
# EnsureEngine, and that case is unreachable once any warm section has run.
# =================================================================================================
Write-Section '5. COLD RACE - 4 threads against an unbuilt engine (separate process)'

& $hostExe $hostfxr $bridgeOut $runtimeCfg 1 --race 2>&1 | ForEach-Object { Write-Host "$_" }
if ($LASTEXITCODE -ne 0) {
    Write-Fail "the cold race failed (exit $LASTEXITCODE). B1 cannot be recommended on this evidence."
    if ($transcript) { Stop-Transcript | Out-Null }
    exit 1
}
Write-Ok 'cold race clean'

# =================================================================================================
# 6. R2 EVIDENCE - warming that builds a menu, not just the container
#
# R1/R2 were accepted as design requirements. This measures what R2 actually buys on Windows: the
# cost of warming all the way through a menu at startup, and what the first user-visible menu costs
# afterwards. Separate processes for the same reason as the race.
# =================================================================================================
Write-Section "6. R2 - WARM THE REAL MENU PATH ($ColdRuns separate processes)"

$readyMs = @(); $firstVisible = @(); $r2Failures = 0
for ($run = 1; $run -le $ColdRuns; $run++) {
    $out2 = & $hostExe $hostfxr $bridgeOut $runtimeCfg 1 --warm-full 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { Write-Fail "R2 run $run exited $LASTEXITCODE"; $r2Failures++; continue }
    if ($run -eq 1) { $out2 | ForEach-Object { Write-Host $_ } }

    $rdy = Get-Ms $out2 'bridge READY (process start -> warmed)'
    $fst = Get-Ms $out2 'FIRST USER-VISIBLE MENU after full warm-up'
    if ($null -eq $rdy -or $null -eq $fst) { Write-Fail "R2 run $run - unparseable"; $r2Failures++; continue }
    $readyMs += $rdy; $firstVisible += $fst
}

Write-Host ''
Show-Range 'bridge READY (startup, off the menu path)' $readyMs
Show-Range 'FIRST user-visible menu after warm-up'     $firstVisible
if ($r2Failures -gt 0) { Write-Fail "$r2Failures R2 run(s) failed"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
Write-Ok "R2 measured over $ColdRuns processes"

Write-Host ''
Write-Host 'Compare the two numbers under (3): "first menu, engine already warm" is what the first'
Write-Host 'user-visible menu costs when warming stopped at the DI container. The R2 figure above is'
Write-Host 'what it costs when warming went all the way through a menu. The gap is what R2 buys.'

# =================================================================================================
# 7. SCOPE
# =================================================================================================
Write-Section '7. WHAT THIS RUN ESTABLISHES, AND WHAT IT DOES NOT'

Write-Host 'MEASURED HERE, ON WINDOWS, BY THIS SCRIPT'
Write-Host '  A  activation      B  cold start     C  warm start'
Write-Host '  F  managed failure G  timeout        + concurrency, warm and cold-race'
Write-Host ''
Write-Host 'DERIVED, NOT OBSERVED'
Write-Host '  D  whether GetState must return E_PENDING. There is no IExplorerCommand here to watch;'
Write-Host '     it is an inference from the cold and R2 figures above.'
Write-Host ''
Write-Host 'NOT ESTABLISHED BY THIS RUN - GATE 2 IS SEPARATE' -ForegroundColor Yellow
Write-Host '  E  isolation from explorer.exe. A property of com:SurrogateServer registration, which'
Write-Host '     G.1 is not authorised to build. THIS SCRIPT IS A STANDALONE .EXE AND IS THEREFORE'
Write-Host '     NOT EVIDENCE OF ISOLATION IN EITHER DIRECTION. Do not read a green run above as'
Write-Host '     closing Question E.'
Write-Host ''
Write-Host 'Nothing was registered. No COM class, no CLSID, no package, no certificate, no registry'
Write-Host 'write. Nothing was created outside the harness folder.'

if ($transcript) {
    Write-Host ''
    Write-Host "transcript: $transcript"
    Stop-Transcript | Out-Null
}
