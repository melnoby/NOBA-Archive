#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - GATE 2 / E2: does the PACKAGED com:SurrogateServer activation path keep the
    bridge out of explorer.exe?
.DESCRIPTION
    E1 answered the hosting model using the classic HKCU surrogate. E2 answers the question ADR-0006
    rides on: whether **Explorer**, activating a handler registered through a **sparse package**,
    loads the DLL and its CLR into **dllhost.exe** rather than into explorer.exe, as the standard user.

    E1 PASS DOES NOT IMPLY E2 PASS. Gate 2 is FAIL unless both pass.

    THE THREE PASS CRITERIA, fixed in DROP08_PHASE5_DELTA.md section 2 before any code existed:
      1. the stub verb appears in Explorer's MODERN menu
      2. explorer.exe does NOT load coreclr.dll / hostfxr.dll / the stub DLL
      3. a dllhost.exe DOES, and it runs as the STANDARD USER - no elevation, no UAC prompt

    STRUCTURE. Everything that can fail for free fails first, and nothing is created before every
    prerequisite has been proven:

        preconditions -> aborted-run recovery -> build -> stage -> PRE-FLIGHT -> pack
        -> certificate -> trust (elevated) -> verify trust -> sign -> deploy -> observe -> CLEANUP

    Pre-flight lives in E2Preflight.psm1 and is exercised by test-e2-preflight.ps1 with a negative
    control for every requirement, on any platform, before this script is ever run.

    ELEVATION. This script REFUSES to run elevated, and elevates exactly twice through a helper
    process: to trust the DEV-ONLY certificate and to remove it. Packing, signing, registration,
    Explorer and the surrogate all stay standard-user, which is what makes criterion 3 mean anything.
    Production uses a CA-issued certificate and has no trust step, so the elevation is a development
    cost, not a product one (plan section L.6).

    THE CERTIFICATE IS DEV-ONLY. Cleanup is a guarded sequence ordered by consequence: the machine
    store is cleared first, every step is independent, and an unreadable store is reported UNKNOWN and
    never counted as clean.

.PARAMETER SkipBuild
    Reuse the binaries already in package\.

.PARAMETER KeepInstalled
    Leave the package registered and the certificate trusted, and print how to remove both.

.PARAMETER CleanOnly
    Run only residue detection and cleanup, then exit. Use after an aborted run.

.PARAMETER ObserveSeconds
    Upper bound on section 9's observation window. The window normally ends when the surrogate is
    captured or when the operator confirms the right-click - the clock is only a backstop, and an
    expired window is reported as UNMEASURED rather than as a failed criterion.

.PARAMETER GraceSeconds
    How long section 9 keeps sampling after the operator confirms the right-click. Activation and
    process start are not instantaneous. Default 20.

.PARAMETER PreconditionsOnly
    Print the precondition report and exit. Reads the machine; creates nothing, changes nothing,
    touches no certificate store and no registry. Exit 0 if E2 can run here, 2 if it is BLOCKED BY
    ENVIRONMENT. Run this first on any new machine.

.EXAMPLE
    .\harness\phase5-e\run-e2.ps1
.EXAMPLE
    .\harness\phase5-e\run-e2.ps1 -CleanOnly
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$KeepInstalled,
    [switch]$CleanOnly,
    [switch]$PreconditionsOnly,
    [ValidateRange(10, 900)][int]$ObserveSeconds = 180,
    [ValidateRange(1, 120)][int]$GraceSeconds = 20
)

$ErrorActionPreference = 'Stop'

$harness   = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = (Resolve-Path (Join-Path $harness '..\..')).Path
$bridgeOut = Join-Path $repoRoot 'harness\phase5-g1\bridge\bin\Debug\net10.0'
$pkgDir    = Join-Path $harness 'package'
$stageDir  = Join-Path $harness 'stage'
$resultDir = Join-Path $harness 'results'

$CLSID       = '17019B66-1E78-459B-92AE-63A440E7C5EA'
$PkgName     = 'NOBA.Archive.Phase5.E2Probe'
$CertSubject = 'CN=NOBA-DEV-ONLY'
$msix        = Join-Path $harness 'noba-e2-probe.msix'
$cerPath     = Join-Path $harness 'noba-dev-only.cer'
$stubLog     = Join-Path $bridgeOut 'noba_e2_stub.log'

Import-Module (Join-Path $harness 'E2Preflight.psm1') -Force

foreach ($d in @($resultDir, $stageDir)) { if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null } }
$transcript = Join-Path $resultDir ("e2-windows-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
try { Start-Transcript -Path $transcript -Force | Out-Null } catch { $transcript = $null }

function Write-Section([string]$t) { Write-Host ''; Write-Host ('=' * 78); Write-Host $t; Write-Host ('=' * 78) }
function Write-Fail([string]$t) { Write-Host "FAIL  $t" -ForegroundColor Red }
function Write-Ok  ([string]$t) { Write-Host "ok    $t" -ForegroundColor Green }
function Write-Warn([string]$t) { Write-Host "warn  $t" -ForegroundColor Yellow }

$script:pass = 0; $script:fail = 0; $script:unknown = 0
# Set by section 9 so CLEANUP can say whether a surrogate it finds started inside the window.
$script:windowStart = $null; $script:windowEnd = $null; $script:criterion3Capture = $null
function Verdict([bool]$ok, [string]$criterion, [string]$detail) {
    if ($ok) { Write-Host "  [PASS] $criterion" -ForegroundColor Green; $script:pass++ }
    else     { Write-Host "  [FAIL] $criterion" -ForegroundColor Red;   $script:fail++ }
    Write-Host "         $detail"
}

# Three-state. A criterion the harness could not measure is UNKNOWN: not a pass, not a refutation.
# It still stops E2 from being 3/3 - but the transcript must not claim a finding that was not made.
function VerdictEx([string]$status, [string]$criterion, [string]$detail) {
    switch ($status) {
        'PASS'    { Write-Host "  [PASS]    $criterion" -ForegroundColor Green;  $script:pass++ }
        'UNKNOWN' { Write-Host "  [UNKNOWN] $criterion" -ForegroundColor Yellow; $script:fail++; $script:unknown++ }
        default   { Write-Host "  [FAIL]    $criterion" -ForegroundColor Red;    $script:fail++ }
    }
    Write-Host "            $detail"
}

# Modules loaded per process of a given name.
#
# The return is a RECORD, not a list, because three outcomes have to stay distinguishable and the
# previous shape collapsed two of them: no such process at all, processes present but unreadable,
# and processes present and read. "Could not look" is never "not there".
function Get-ProcModules([string]$Name) {
    $procs = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    $rows = @(); $unreadable = 0; $lastErr = $null
    foreach ($p in $procs) {
        $mods = $null
        try { $mods = @($p.Modules | ForEach-Object { $_.ModuleName.ToLowerInvariant() }) }
        catch { $unreadable++; $lastErr = $_.Exception.Message; continue }
        $started = $null
        try { $started = $p.StartTime } catch { }
        $rows += [pscustomobject]@{ Pid = $p.Id; Name = $p.ProcessName; Started = $started; Modules = $mods }
    }
    return [pscustomobject]@{ Found = $procs.Count; Rows = $rows; Unreadable = $unreadable; Error = $lastErr }
}

# A surrogate is identified by having OUR stub loaded, never by process name alone: dllhost.exe is
# shared, and acting on one that is not ours would be someone else's outage.
#
# Returns a record, not a list. The residue scan and the cleanup both need to know the difference
# between "no surrogate holds the stub" and "I could not read the module lists", for the same reason
# the certificate helpers do: an unreadable store is UNKNOWN, never CLEAN.
function Get-E2Surrogates {
    $probe = Get-ProcModules 'dllhost'
    $rows  = @($probe.Rows | Where-Object { @($_.Modules | Where-Object { $_ -like '*noba_e2_stub*' }).Count -gt 0 })
    return [pscustomobject]@{ Rows = $rows; Found = $probe.Found; Unreadable = $probe.Unreadable }
}

# Captures everything criterion 3 asserts AT THE MOMENT OF DETECTION, because a surrogate's lifetime
# is not the harness's to control. Owner lookup can lose the race even here; if it does, that is
# recorded as unknown rather than guessed - the module evidence still stands.
function Find-E2Surrogate {
    $probe = Get-E2Surrogates
    $hit = @($probe.Rows) | Select-Object -First 1
    if ($null -eq $hit) {
        return [pscustomobject]@{ Capture = $null; Found = $probe.Found; Unreadable = $probe.Unreadable }
    }
    $owner = '(could not read - process exited before the owner could be looked up)'
    try {
        $o = Get-CimInstance Win32_Process -Filter "ProcessId = $($hit.Pid)" -ErrorAction Stop |
             Invoke-CimMethod -MethodName GetOwner -ErrorAction Stop
        if ($o -and $o.User) { $owner = "$($o.Domain)\$($o.User)" }
    } catch { }
    $capture = [pscustomobject]@{
        Pid     = $hit.Pid
        Name    = $hit.Name
        Started = $hit.Started
        Owner   = $owner
        HasClr  = (@($hit.Modules | Where-Object { $_ -like '*coreclr*' }).Count -gt 0)
        HasFxr  = (@($hit.Modules | Where-Object { $_ -like '*hostfxr*' }).Count -gt 0)
        HasStub = $true
        Modules = $hit.Modules
    }
    return [pscustomobject]@{ Capture = $capture; Found = $probe.Found; Unreadable = $probe.Unreadable }
}

# A keypress without blocking, so the observation window can watch for the surrogate AND let the
# operator say "done". Under a redirected or non-interactive stdin this throws; then the window
# simply runs to its timeout, which is the safe behaviour.
function Test-KeyPressed {
    try {
        if ([Console]::KeyAvailable) { [void][Console]::ReadKey($true); return $true }
    } catch { }
    return $false
}

# The deployment log, so a failure names the actual prerequisite instead of only an HRESULT.
function Show-DeploymentLog {
    Write-Host ''
    Write-Host 'AppXDeploymentServer/Operational - most recent events:' -ForegroundColor Yellow
    try {
        Get-WinEvent -LogName 'Microsoft-Windows-AppXDeploymentServer/Operational' -MaxEvents 15 -ErrorAction Stop |
            Where-Object { $_.LevelDisplayName -in @('Error','Warning') -or $_.Message -match 'NOBA' } |
            Select-Object -First 6 |
            ForEach-Object { Write-Host ("  [{0}] {1}" -f $_.LevelDisplayName, (($_.Message -split "`n")[0])) }
    } catch {
        Write-Host "  (could not read the deployment log: $($_.Exception.Message))"
        Write-Host '  Event Viewer -> Applications and Services Logs -> Microsoft -> Windows'
        Write-Host '              -> AppxDeploymentServer -> Operational'
    }
}

# =================================================================================================
# CLEANUP - a guarded sequence, ordered by consequence.
#
# Called from finally, from -CleanOnly, and from residue recovery. NO STEP MAY PREVENT A LATER ONE:
# an earlier version's unguarded loop threw and left a DEV-ONLY certificate trusted machine-wide,
# which is the residue SHELL-W9 exists to catch, left behind by the code meant to prevent it.
# =================================================================================================
function Invoke-E2Cleanup {
    $residue = @()

    # 1. E2-owned surrogate processes.
    try {
        $scan = Get-E2Surrogates
        foreach ($s in $scan.Rows) {
            # A surrogate found HERE that criterion 3 did not capture is the most important line in
            # the transcript, not an incidental cleanup note. On 2026-08-31 cleanup stopped pid 13232
            # - holding the stub, found by this same matcher - after criterion 3 had reported that no
            # dllhost ever had it. Printing the start time makes the ordering answerable from the
            # transcript instead of arguable.
            $when = if ($s.Started) { $s.Started } else { '(start time unreadable)' }
            $inWindow = $null
            if ($script:windowStart -and $script:windowEnd -and $s.Started) {
                $inWindow = ($s.Started -ge $script:windowStart -and $s.Started -le $script:windowEnd)
            }
            $note = Resolve-E2LateDetectionNote -SurrogatePid $s.Pid -Capture $script:criterion3Capture `
                                                -StartedInWindow $inWindow
            Write-Host ("      surrogate pid $($s.Pid) holds the stub at cleanup; started $when; " +
                        "inside the observation window: $(if ($null -eq $inWindow) { 'unknown' } elseif ($inWindow) { 'YES' } else { 'NO' })")
            if ($note.Status -eq 'CONFIRMS') { Write-Ok "      $($note.Text)" }
            else                             { Write-Warn "      LATE DETECTION - $($note.Text)" }
            try { Stop-Process -Id $s.Pid -Force -ErrorAction Stop; Write-Host "      stopped surrogate pid $($s.Pid)" }
            catch { Write-Warn "could not stop surrogate pid $($s.Pid): $($_.Exception.Message)" }
        }
        # Could-not-look is residue, not cleanliness: reporting "no surrogate" off an unreadable
        # module list is the same error criterion 3 made.
        if ($scan.Unreadable -gt 0) { $residue += "$($scan.Unreadable) dllhost module list(s) UNREADABLE - cannot confirm no surrogate holds the stub" }
    } catch { Write-Warn "surrogate scan failed: $($_.Exception.Message)"; $residue += 'surrogate scan failed - surrogate state UNKNOWN' }

    # 2. package registration. The external-location registration has no separate lifetime - it is a
    #    property of the registered package and goes with it.
    try {
        $pkg = Get-AppxPackage -Name $PkgName -ErrorAction SilentlyContinue
        if ($pkg) {
            Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
            Write-Ok 'package unregistered (external location released with it)'
        }
    } catch { Write-Fail "package removal failed: $($_.Exception.Message)" }
    try { if (Get-AppxPackage -Name $PkgName -ErrorAction SilentlyContinue) { $residue += 'package registration' } } catch { }

    # 3. MACHINE certificate first among certificate steps - it is the one with security weight.
    $machineCount = Get-E2CertCount -StoreName 'TrustedPeople' -Location 'LocalMachine' -Subject $CertSubject
    if ($machineCount -gt 0) {
        Write-Host 'UAC PROMPT: removing the DEV-ONLY certificate from the machine store.' -ForegroundColor Yellow
        $rmCmd = @"
`$s = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
`$s.Open('ReadWrite')
`$s.Certificates | Where-Object { `$_.Subject -eq '$CertSubject' } | ForEach-Object { `$s.Remove(`$_) }
`$s.Close()
"@
        try {
            $rp = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru `
                                -ArgumentList '-NoProfile', '-NonInteractive', '-Command', $rmCmd
            if ($rp.ExitCode -ne 0) { Write-Fail "elevated certificate removal exited $($rp.ExitCode)" }
        } catch { Write-Fail "could not elevate to remove the machine certificate: $($_.Exception.Message)" }
    } elseif ($machineCount -lt 0) {
        Write-Warn 'machine certificate store could not be read - cannot confirm it is clean'
    }

    # 4. user copies, each independent.
    foreach ($storeName in @('TrustedPeople', 'My')) {
        $r = Remove-E2Certs -StoreName $storeName -Location 'CurrentUser' -Subject $CertSubject
        if (-not $r.Opened) { Write-Warn "CurrentUser\$storeName not writable: $($r.Error)" }
        elseif ($r.Failed -gt 0) { Write-Warn "CurrentUser\$storeName : $($r.Failed) removal(s) failed" }
    }

    # 5. verify every store independently. UNKNOWN is never CLEAN.
    foreach ($loc in @(@{S='TrustedPeople'; L='LocalMachine'}, @{S='TrustedPeople'; L='CurrentUser'}, @{S='My'; L='CurrentUser'})) {
        $n = Get-E2CertCount -StoreName $loc.S -Location $loc.L -Subject $CertSubject
        if ($n -gt 0)      { $residue += "$($loc.L)\$($loc.S) ($n certificate(s))" }
        elseif ($n -lt 0)  { $residue += "$($loc.L)\$($loc.S) (UNKNOWN - could not read)" }
    }

    # 6. generated files and staged payload.
    foreach ($f in @($msix, $cerPath,
                     (Join-Path $bridgeOut 'noba_e2_stub.dll'),
                     (Join-Path $bridgeOut 'noba_e2_host.exe'))) {
        try { if (Test-Path $f) { Remove-Item $f -Force -ErrorAction Stop } }
        catch { Write-Warn "could not delete $f : $($_.Exception.Message)"; $residue += (Split-Path -Leaf $f) }
    }

    # 7. report.
    if ($residue.Count -eq 0) {
        Write-Ok 'no residue: no package, no certificate in any store, no generated files'
    } else {
        Write-Fail "RESIDUE REMAINS: $($residue -join '; ')"
        if (($residue -join ' ') -match 'LocalMachine') {
            Write-Host '      MACHINE-WIDE TRUST. In an ELEVATED PowerShell:' -ForegroundColor Yellow
            Write-Host "      `$s = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')"
            Write-Host "      `$s.Open('ReadWrite'); `$s.Certificates | ? { `$_.Subject -eq '$CertSubject' } | % { `$s.Remove(`$_) }; `$s.Close()"
            Write-Host '      Remove-Item on the Cert: provider does NOT work here, even elevated.'
        }
    }
    return ,$residue
}

Write-Section 'DROP 08 Phase 5 - GATE 2 / E2: packaged com:SurrogateServer activation'
Write-Host "repo   : $repoRoot"
if ($transcript) { Write-Host "log    : $transcript" }
Write-Host "clsid  : {$CLSID}"
Write-Host "package: $PkgName"

# =================================================================================================
# 0. PRECONDITIONS
# =================================================================================================
Write-Section '0. PRECONDITIONS'

$os = Get-CimInstance Win32_OperatingSystem
Write-Host "os      : $($os.Caption) build $($os.BuildNumber)"
if ([int]$os.BuildNumber -lt 22000) {
    Write-Fail 'The modern context menu requires Windows 11 (build 22000+).'
    if ($transcript) { Stop-Transcript | Out-Null }
    exit 1
}

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "elevated: $elevated"
if ($elevated) {
    Write-Fail 'Run this UNELEVATED.'
    Write-Host '      Criterion 3 checks that the surrogate runs as the standard user. Registering from'
    Write-Host '      an elevated prompt registers for the administrator (plan section L.2) and makes the'
    Write-Host '      result meaningless.'
    if ($transcript) { Stop-Transcript | Out-Null }
    exit 1
}

if ($PreconditionsOnly) {
    Write-Section 'PRECONDITIONS ONLY - nothing will be created or changed'
    $r = Show-E2Preconditions
    Write-Host ''
    if ($r.Permitted) {
        Write-Ok 'E2 can run on this machine.'
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 0
    }
    Write-Host 'E2 IS BLOCKED BY ENVIRONMENT ON THIS MACHINE.' -ForegroundColor Yellow
    Write-Host '  This is not an E2 result. Explorer activation is never reached, so the three'
    Write-Host '  acceptance criteria are neither met nor refuted - E2 stays OPEN.'
    if ($transcript) { Stop-Transcript | Out-Null }
    exit 2
}

# Sideloading policy is checked in PRE-FLIGHT as a GATE, not here as a note. An earlier version
# printed it here and deployed anyway; the value was AllowAllTrustedApps=0 and deployment failed
# 0x80073CFF. A fact that is printed but not wired to a decision is decoration.

# =================================================================================================
# 1. ABORTED-RUN RECOVERY
# =================================================================================================
Write-Section '1. RESIDUE FROM PREVIOUS RUNS'

$found = @()
try { if (Get-AppxPackage -Name $PkgName -ErrorAction SilentlyContinue) { $found += 'package still registered' } } catch { }
foreach ($loc in @(@{S='TrustedPeople'; L='LocalMachine'}, @{S='TrustedPeople'; L='CurrentUser'}, @{S='My'; L='CurrentUser'})) {
    $n = Get-E2CertCount -StoreName $loc.S -Location $loc.L -Subject $CertSubject
    if ($n -gt 0)     { $found += "$($loc.L)\$($loc.S): $n certificate(s)" }
    elseif ($n -lt 0) { $found += "$($loc.L)\$($loc.S): UNREADABLE" }
}
$surr = Get-E2Surrogates
if ($surr.Rows.Count -gt 0)   { $found += "$($surr.Rows.Count) surrogate process(es) holding the stub" }
elseif ($surr.Unreadable -gt 0) { $found += "$($surr.Unreadable) dllhost module list(s) UNREADABLE - surrogate state unknown" }
foreach ($f in @($msix, $cerPath, (Join-Path $bridgeOut 'noba_e2_stub.dll'), (Join-Path $bridgeOut 'noba_e2_host.exe'))) {
    if (Test-Path $f) { $found += (Split-Path -Leaf $f) }
}

if ($found.Count -eq 0) {
    Write-Ok 'clean starting state'
} else {
    Write-Warn "residue detected: $($found -join '; ')"
    Write-Host '      Cleaning it before starting, so stale state cannot make this run look successful.'
    Invoke-E2Cleanup | Out-Null
}

if ($CleanOnly) {
    Write-Section 'CLEAN ONLY - done'
    if ($transcript) { Stop-Transcript | Out-Null }
    exit 0
}

# =================================================================================================
# 2. BUILD
# =================================================================================================
Write-Section '2. BUILD THE STUB'

$stubDll = Join-Path $pkgDir 'noba_e2_stub.dll'
$hostExe = Join-Path $pkgDir 'noba_e2_host.exe'

if ($SkipBuild) {
    Write-Host 'skipped by request'
    foreach ($f in @($stubDll, $hostExe)) {
        if (-not (Test-Path $f)) { Write-Fail "-SkipBuild given but $f is missing"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    }
} else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { Write-Fail 'vswhere.exe not found - VS Build Tools with the C++ workload required.'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) { Write-Fail 'no VS installation with the C++ toolset.'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    $envDump = & cmd.exe /c "`"$(Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat')`" >nul 2>&1 && set"
    foreach ($line in $envDump) { if ($line -match '^([^=]+)=(.*)$') { Set-Item -Path ('Env:' + $matches[1]) -Value $matches[2] -ErrorAction SilentlyContinue } }
    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { Write-Fail 'cl.exe not available after vcvars64.bat'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    Write-Host "toolset : $env:VCToolsVersion"

    Push-Location $pkgDir
    try {
        & cl.exe /nologo /std:c++17 /EHsc /O2 /W3 /LD StubCommand.cpp /Fe:noba_e2_stub.dll `
                 /link /DEF:StubCommand.def ole32.lib oleaut32.lib uuid.lib shlwapi.lib
        $rc1 = $LASTEXITCODE
        & cl.exe /nologo /O2 /W3 noba_e2_host.cpp /Fe:noba_e2_host.exe
        $rc2 = $LASTEXITCODE
    } finally { Pop-Location }
    if ($rc1 -ne 0 -or $rc2 -ne 0 -or -not (Test-Path $stubDll) -or -not (Test-Path $hostExe)) {
        Write-Fail "stub build failed (dll=$rc1 exe=$rc2)"
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 1
    }
}
Write-Ok "stub: $stubDll"

# =================================================================================================
# 3. STAGE
# =================================================================================================
Write-Section '3. STAGE'

foreach ($f in @('NOBA.Phase5.G1.Bridge.dll', 'NOBA.Phase5.G1.Bridge.runtimeconfig.json')) {
    if (-not (Test-Path (Join-Path $bridgeOut $f))) {
        Write-Fail "the G.1 bridge is not built: $f  - run .\harness\phase5-g1\run-g1.ps1 first."
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 1
    }
}

$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$fxrPick = Get-ChildItem (Join-Path $dotnetRoot 'host\fxr') -Directory |
    ForEach-Object { $v = $null; if ([version]::TryParse($_.Name, [ref]$v)) { [pscustomobject]@{ V = $v; P = $_.FullName } } } |
    Sort-Object V | Select-Object -Last 1
$hostfxr = Join-Path $fxrPick.P 'hostfxr.dll'

Copy-Item $stubDll (Join-Path $bridgeOut 'noba_e2_stub.dll') -Force
Copy-Item $hostExe (Join-Path $bridgeOut 'noba_e2_host.exe') -Force
Set-Content -Path (Join-Path $bridgeOut 'hostfxr.path') -Value $hostfxr -Encoding ASCII -NoNewline
if (Test-Path $stubLog) { Remove-Item $stubLog -Force }

Remove-Item (Join-Path $stageDir '*') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $pkgDir 'AppxManifest.xml') $stageDir -Force
Copy-Item (Join-Path $pkgDir 'Assets') $stageDir -Recurse -Force
Write-Ok "external location: $bridgeOut"
Write-Ok "package layout   : $stageDir"

# =================================================================================================
# 4. PRE-FLIGHT - every requirement, before anything is created
# =================================================================================================
Write-Section '4. PRE-FLIGHT'

# The harness's own scripts first. This one is here because run-e2.ps1 reached the VM byte-identical
# to the source tree and still would not parse: it was UTF-8 without a BOM, and Windows PowerShell
# 5.1 decodes a BOM-less file as the ANSI codepage, where the third byte of U+2192 is a quote
# character. See the header of E2Preflight.psm1. Folded into one requirement so the transcript stays
# readable; every failure is named in the detail.
$findings = @()
$encFindings = Test-E2ScriptEncoding -Root (Split-Path -Parent $harness) -Codepage 1252, 1251, 932
$encFails    = @($encFindings | Where-Object { $_.Status -eq 'FAIL' })
$findings += if ($encFails.Count -eq 0) {
    New-Finding 'H01' 'every harness script survives Windows PowerShell 5.1' 'PASS' `
                "$($encFindings.Count) checks, 0 failures"
} else {
    New-Finding 'H01' 'every harness script survives Windows PowerShell 5.1' 'FAIL' `
                (($encFails | ForEach-Object { "$($_.Id): $($_.Detail)" }) -join ' | ')
}

# Machine prerequisites next: a policy block makes every manifest check irrelevant.
$findings += Test-E2Sideloading
$findings += Test-E2Manifest -Path (Join-Path $stageDir 'AppxManifest.xml') `
                             -ExpectedPublisher $CertSubject -ExpectedClsid $CLSID `
                             -ExternalLocation $bridgeOut -PackageRoot $stageDir

foreach ($f in $findings) {
    if ($f.Status -eq 'PASS') { Write-Host ("  ok   {0,-8} {1}" -f $f.Id, $f.Requirement) }
    else {
        Write-Host ("  {0} {1,-8} {2}" -f $f.Status, $f.Id, $f.Requirement) -ForegroundColor Red
        Write-Host ("           {0}" -f $f.Detail)
    }
}
$preFails = @($findings | Where-Object { $_.Status -eq 'FAIL' })
if ($preFails.Count -gt 0) {
    Write-Fail "$($preFails.Count) pre-flight failure(s). Nothing was created; there is nothing to clean up."
    # A machine that does not permit sideloading is BLOCKED BY ENVIRONMENT, not an E2 failure. The
    # distinction matters: E2's criteria are about Explorer activation, and activation is never
    # reached, so nothing about the adapter has been shown either way. Exit 2, never 1.
    if (@($preFails | Where-Object { $_.Id -eq 'S01' }).Count -gt 0) {
        Write-Host ''
        Show-E2Preconditions | Out-Null
        Write-Host ''
        Write-Host 'E2 IS BLOCKED BY ENVIRONMENT.' -ForegroundColor Yellow
        Write-Host '  NOT an E2 pass and NOT an E2 fail. Explorer activation was never reached, so the'
        Write-Host '  three acceptance criteria are neither met nor refuted. E2 stays OPEN.'
        Write-Host '  The harness changes no policy. See docs/DROP08_PHASE5_E2_EVIDENCE.md for the'
        Write-Host '  test environment that can close E2 legitimately.'
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 2
    }
    if ($transcript) { Stop-Transcript | Out-Null }
    exit 1
}
Write-Ok "pre-flight: $($findings.Count) requirements, 0 failures"

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$sdkBin = Get-ChildItem $sdkRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^10\.' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object { (Test-Path (Join-Path $_ 'makeappx.exe')) -and (Test-Path (Join-Path $_ 'signtool.exe')) } |
    Select-Object -First 1
if (-not $sdkBin) {
    Write-Fail 'makeappx.exe / signtool.exe not found under the Windows SDK.'
    if ($transcript) { Stop-Transcript | Out-Null }
    exit 1
}
Write-Ok "sdk tools: $sdkBin"

# =================================================================================================
# 5-11. PACK / CERTIFICATE / SIGN / DEPLOY / OBSERVE
# =================================================================================================
try {
    Write-Section '5. PACK'
    # /nv because a sparse package's payload lives outside it; standard validation would reject it.
    & (Join-Path $sdkBin 'makeappx.exe') pack /d $stageDir /p $msix /nv /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }
    Write-Ok "packed: $msix"

    Write-Section '6. CERTIFICATE'
    Write-Host 'DEV-ONLY - NOT A SHIPPING CERTIFICATE' -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type Custom -Subject $CertSubject `
        -KeyUsage DigitalSignature -FriendlyName 'NOBA Phase 5 DEV-ONLY (harness)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}Subject Type:End Entity')
    Write-Ok "created: $($cert.Subject)  thumbprint $($cert.Thumbprint)"

    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

    Write-Host ''
    Write-Host 'UAC PROMPT #1: trusting the DEV-ONLY certificate in the machine store.' -ForegroundColor Yellow
    Write-Host '  MSIX deployment reads LocalMachine\TrustedPeople; a user-store certificate is refused'
    Write-Host '  with 0x800B0109. This is the only step needing Administrator. A second prompt at'
    Write-Host '  cleanup removes it again.'
    Write-Host ''
    $importCmd = "Import-Certificate -FilePath '$cerPath' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null"
    $ip = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru `
                        -ArgumentList '-NoProfile', '-NonInteractive', '-Command', $importCmd
    if ($ip.ExitCode -ne 0) { throw "elevated certificate import failed (exit $($ip.ExitCode)) - was the UAC prompt declined?" }

    $trustedCount = Get-E2CertCount -StoreName 'TrustedPeople' -Location 'LocalMachine' -Subject $CertSubject
    if ($trustedCount -lt 0) { throw 'the machine certificate store could not be read - cannot confirm trust before deploying' }
    if ($trustedCount -eq 0) { throw 'the certificate is not in LocalMachine\TrustedPeople - deployment would fail 0x800B0109' }
    Write-Ok "trust verified in LocalMachine\TrustedPeople ($trustedCount)"

    Write-Section '7. SIGN'
    & (Join-Path $sdkBin 'signtool.exe') sign /fd SHA256 /a /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
    Write-Ok 'signed'

    Write-Section '8. DEPLOY'
    try {
        Add-AppxPackage -Path $msix -ExternalLocation $bridgeOut -ErrorAction Stop
        Write-Ok 'registered for this user (unelevated, -ExternalLocation)'
    } catch {
        Write-Fail "deployment failed: $($_.Exception.Message)"
        Show-DeploymentLog
        throw 'deployment failed'
    }

    Write-Section '9. OBSERVE'
    Write-Host 'Explorer caches context-menu handlers; restarting makes the verb visible and gives a'
    Write-Host 'clean module list to measure.'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe; Start-Sleep -Seconds 3 }
    Write-Ok 'explorer restarted'

    # Criterion 2 is measured later, but WHICH explorer it measures has to be pinned now. A late
    # sample of a DIFFERENT explorer process would be a clean module list that proves nothing about
    # the process that actually drew the menu.
    $explBaseline = @((Get-ProcModules 'explorer').Rows | ForEach-Object { "$($_.Pid)@$($_.Started)" })
    Write-Ok ("explorer baseline: " + $(if ($explBaseline.Count) { $explBaseline -join ', ' } else { '(none readable)' }))

    Write-Host ''
    Write-Host 'NOW, IN FILE EXPLORER:' -ForegroundColor Yellow
    Write-Host '  1. open a folder containing at least one file'
    Write-Host '  2. RIGHT-CLICK a file'
    Write-Host '  3. look for:  NOBA Phase 5 isolation probe (harness)'
    Write-Host '     in the MODERN menu - the short one, above "Show more options"'
    Write-Host '  4. you do NOT need to click it; drawing the menu is enough'
    Write-Host ''
    Write-Host 'PRESS ANY KEY HERE once you have right-clicked. The harness watches until you do.' -ForegroundColor Yellow
    Write-Host "It gives up after $ObserveSeconds seconds, and an expired window is reported as UNMEASURED,"
    Write-Host 'not as a failure - see below.'
    Write-Host ''

    # THE MEASUREMENT, AND WHAT THE 2026-08-31 05:00 RUN TAUGHT.
    #
    # v1 sampled once, after Read-Host, and lost a race with the surrogate's lifetime.
    # v2 (this loop's predecessor) polled for a fixed 180s and then gave up. The window expired, and
    # the harness reported "none ever had the stub loaded" - a refutation. Cleanup then found pid
    # 13232 holding the stub, with THIS SAME matcher. The surrogate was real; it did not exist while
    # the window was open. A fixed timer measures the operator, not the architecture.
    #
    # v3: the window is driven by the operator, not by a clock. It ends when the surrogate is
    # captured, or when the operator says the right-click has happened - and only then is an empty
    # sample allowed to mean anything. Expiry without confirmation measures nothing and says so.
    $watchStart = Get-Date
    $deadline   = $watchStart.AddSeconds($ObserveSeconds)
    $capture    = $null
    $sawHost    = $false
    $unreadable = 0
    $ticks      = 0
    $confirmed  = $false

    $stopAt = $deadline
    while ($true) {
        $now = Get-Date
        if ($now -ge $stopAt) { break }

        $probe = Find-E2Surrogate
        if ($probe.Found -gt 0)      { $sawHost = $true }
        if ($probe.Unreadable -gt 0) { $unreadable += $probe.Unreadable }
        if ($null -ne $probe.Capture) { $capture = $probe.Capture; break }

        # The keypress means "I have right-clicked". It does NOT end the measurement immediately:
        # activation and process start are not instantaneous, so a grace burst keeps sampling.
        # THE 10:40 RUN. A keypress used to REPLACE the deadline with now+GraceSeconds, so an operator
        # who confirmed immediately shrank the window from 180s to 20s - eagerness shortened the
        # measurement. pid 10460 then started 32s after that truncated window closed and was only
        # caught by the post-answer resample; without it the run would have printed a FALSE FAIL.
        # Grace may only EXTEND the window, never end it early.
        if (-not $confirmed -and (Test-KeyPressed)) {
            $confirmed = $true
            $tail = (Get-Date).AddSeconds($GraceSeconds)
            if ($tail -gt $stopAt) { $stopAt = $tail }
            Write-Host ("  right-click confirmed - still watching until {0:HH:mm:ss}" -f $stopAt)
        }

        $ticks++
        if ($ticks % 20 -eq 0) {
            Write-Host ("  watching... {0}s elapsed, dllhost processes seen: {1}, unreadable: {2}, confirmed: {3}" -f `
                        [int]((Get-Date) - $watchStart).TotalSeconds, $(if ($sawHost) { 'yes' } else { 'no' }), `
                        $unreadable, $(if ($confirmed) { 'yes' } else { 'not yet' }))
        }
        Start-Sleep -Milliseconds 250
    }
    $watchEnd = Get-Date

    # An expired window is not a result. Ask, rather than assume, whether anything was attempted -
    # the previous run's FAIL was exactly this assumption.
    if ($null -eq $capture -and -not $confirmed) {
        Write-Host ''
        Write-Warn "the $ObserveSeconds second window expired without a keypress."
        $ans = Read-Host 'Did you right-click a file WHILE the window above was open? [y/n]'
        $confirmed = ($ans -match '^(y|Y)')
        if ($confirmed) {
            Write-Host '  taking one more sample now, in case the surrogate is still alive'
            $late = Find-E2Surrogate
            if ($null -ne $late.Capture) { $capture = $late.Capture }
            if ($late.Unreadable -gt 0)  { $unreadable += $late.Unreadable }
        }
    }

    Write-Host ''
    Write-Host ("window: {0:HH:mm:ss} -> {1:HH:mm:ss} ({2}s), right-click confirmed in window: {3}" -f `
                $watchStart, $watchEnd, [int]($watchEnd - $watchStart).TotalSeconds, $(if ($confirmed) { 'YES' } else { 'NO' }))
    if ($null -ne $capture) {
        Write-Ok ("surrogate captured while alive: pid $($capture.Pid) ($($capture.Name)), started $($capture.Started)")
    } else {
        Write-Warn ("no surrogate captured (host processes seen: $(if ($sawHost) { 'yes' } else { 'no' }), unreadable samples: $unreadable)")
    }

    Write-Section '10. CRITERIA'

    $seen = Read-Host 'Did the entry appear in the MODERN menu (above "Show more options")? [y/n/s=only under Show more options]'
    $c1 = ($seen -match '^(y|Y)')
    Verdict $c1 '1. the stub verb appears in the modern menu' $(
        if ($c1) { 'confirmed by observation' }
        elseif ($seen -match '^(s|S)') { 'appeared ONLY under "Show more options" - that is the classic surface, not the modern one' }
        else { 'not seen - the package registered but Explorer did not draw it' })

    $expl = Get-ProcModules 'explorer'
    if ($expl.Rows.Count -eq 0) {
        VerdictEx 'UNKNOWN' '2. explorer.exe loaded none of coreclr/hostfxr/stub' `
            "no explorer module list could be read (found $($expl.Found), unreadable $($expl.Unreadable)) - NOT evidence of absence"
    } else {
        $now  = @($expl.Rows | ForEach-Object { "$($_.Pid)@$($_.Started)" })
        $same = @($now | Where-Object { $explBaseline -contains $_ })
        $hits = @()
        foreach ($row in $expl.Rows) {
            foreach ($needle in @('coreclr', 'hostfxr', 'noba_e2_stub')) {
                if (@($row.Modules | Where-Object { $_ -like "*$needle*" }).Count -gt 0) { $hits += "pid $($row.Pid): $needle" }
            }
        }
        if ($hits.Count -gt 0) {
            VerdictEx 'FAIL' '2. explorer.exe loaded none of coreclr/hostfxr/stub' "IN-PROCESS: $($hits -join '; ')"
        } elseif ($same.Count -eq 0) {
            # coreclr never unloads from a process, so a clean explorer is meaningful ONLY if it is
            # the same explorer that drew the menu.
            VerdictEx 'UNKNOWN' '2. explorer.exe loaded none of coreclr/hostfxr/stub' `
                "clean, but no explorer from the baseline survived ($($explBaseline -join ', ') -> $($now -join ', ')). A restarted explorer proves nothing about the one that drew the menu."
        } else {
            VerdictEx 'PASS' '2. explorer.exe loaded none of coreclr/hostfxr/stub' `
                "checked $($expl.Rows.Count) explorer process(es), $($same.Count) from the baseline; none of the three modules present"
        }
    }

    # Criterion 1's answer is itself evidence that a right-click just happened. If the window never
    # captured anything, sample once more now rather than throwing that away.
    if ($null -eq $capture -and $c1) {
        $post = Find-E2Surrogate
        if ($null -ne $post.Capture) {
            $capture   = $post.Capture
            $confirmed = $true
            Write-Host "         (captured after the criterion-1 answer: pid $($capture.Pid), started $($capture.Started))"
        }
    }

    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $v3 = Resolve-E2SurrogateVerdict -Capture $capture -SawAnyHostProcess $sawHost `
                                     -UnreadableSamples $unreadable -ExpectedUser $me `
                                     -ActivationConfirmed $confirmed
    VerdictEx $v3.Status '3. a dllhost.exe hosts the stub, as the standard user' $v3.Detail
    $script:windowStart      = $watchStart
    $script:windowEnd        = $watchEnd
    $script:criterion3Capture = $capture

    Write-Section '11. STUB LOG (written from inside the surrogate)'
    if (Test-Path $stubLog) { Get-Content $stubLog | ForEach-Object { Write-Host "  $_" } }
    else { Write-Host '  (no log - the stub was never loaded anywhere)' }
}
catch {
    Write-Host ''
    Write-Fail "E2 could not complete: $($_.Exception.Message)"
    $script:fail++
}
finally {
    Write-Section '12. CLEANUP'
    if ($KeepInstalled) {
        Write-Warn 'LEFT INSTALLED by request. Remove with:'
        Write-Host '  .\harness\phase5-e\run-e2.ps1 -CleanOnly'
    } else {
        Invoke-E2Cleanup | Out-Null
    }
}

Write-Section '13. E2 VERDICT'
$e2ok = ($script:fail -eq 0 -and $script:pass -eq 3)
if ($e2ok) {
    Write-Ok 'E2 PASS - 3/3 criteria met'
} elseif ($script:unknown -gt 0) {
    # An unmeasured criterion is not a refutation, and saying "FAIL" without saying which kind
    # invites the next reader to record a finding that was never made.
    Write-Fail "E2 NOT CLOSED - $($script:pass) passed, $($script:unknown) UNMEASURED, $($script:fail - $script:unknown) refuted."
    Write-Host '  An UNKNOWN criterion means the harness could not look, not that it looked and found'
    Write-Host '  nothing. E2 stays OPEN. Do not record it as evidence either way; fix the measurement'
    Write-Host '  and run again.'
} else {
    Write-Fail "E2 FAIL - $($script:pass) passed, $($script:fail) failed. A partial result is a FAIL."
}

Write-Host ''
Write-Host 'SCOPE' -ForegroundColor Yellow
Write-Host '  Proven here : the PACKAGED activation path. Registration and activation were unelevated;'
Write-Host '                only trusting the DEV-ONLY certificate needed Administrator.'
Write-Host '  NOT proven  : the per-machine install flow (stage + provision) from plan section L. Phase 9.'
Write-Host '  Gate 2 is PASS only if E1 AND E2 both passed.'

if ($transcript) { Write-Host ''; Write-Host "transcript: $transcript"; Stop-Transcript | Out-Null }
exit $(if ($e2ok) { 0 } else { 1 })
