#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - GATE 2 / E1: does an out-of-process COM surrogate really keep the CLR out of
    the calling process?
.DESCRIPTION
    E1 is the half of Question E that can be answered without a package and without a certificate.
    It uses the CLASSIC surrogate route documented by Microsoft: an `AppID` value under the CLSID, an
    `InprocServer32` pointing at the DLL, and a `DllSurrogate` value that is an EMPTY STRING - which
    selects the system-supplied `dllhost.exe`. The client then activates with `CLSCTX_LOCAL_SERVER`.

    Everything it writes is under HKCU\Software\Classes, the same hive shell-register.ps1 uses, and
    it is all removed again in a finally block whether the run succeeds, fails, or is interrupted.

    **E1 PASSING DOES NOT MEAN E2 PASSES.** This is the classic activation path. Explorer uses the
    packaged COM catalogue, and only E2 exercises that. If E1 passes and E2 fails, Gate 2 is FAIL.

    THE FOUR PASS CRITERIA, fixed in DROP08_PHASE5_DELTA.md section 2 before any of this code existed:
      1. the client's module list contains NO coreclr.dll and NO hostfxr.dll
      2. a dllhost.exe exists whose module list contains BOTH, plus the NOBA bridge
      3. faulting the managed side kills the surrogate and the client survives
      4. NobaShellQuery round-trips across the boundary and returns the real menu

    NOT ELEVATED. No package. No certificate. No IExplorerCommand. Nothing under HKLM.

.PARAMETER SkipBuild
    Reuse the binaries already in surrogate\ and client\.

.PARAMETER KeepRegistered
    Leave the registration in place for manual inspection. The script prints exactly how to remove
    it. Use sparingly - a NOBA CLSID left behind is the kind of residue SHELL-W9 exists to catch.

.EXAMPLE
    .\harness\phase5-e\run-e1.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$KeepRegistered
)

$ErrorActionPreference = 'Stop'

$harness   = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = (Resolve-Path (Join-Path $harness '..\..')).Path
$g1        = Join-Path $repoRoot 'harness\phase5-g1'
$bridgeOut = Join-Path $g1 'bridge\bin\Debug\net10.0'
$srcDir    = Join-Path $harness 'surrogate'
$cliDir    = Join-Path $harness 'client'
$resultDir = Join-Path $harness 'results'

$CLSID = '{DD5D1E41-3276-4680-97B4-85B59D8D4986}'

if (-not (Test-Path $resultDir)) { New-Item -ItemType Directory -Path $resultDir -Force | Out-Null }
$transcript = Join-Path $resultDir ("e1-windows-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
try { Start-Transcript -Path $transcript -Force | Out-Null } catch { $transcript = $null }

function Write-Section([string]$Text) {
    Write-Host ''; Write-Host ('=' * 78); Write-Host $Text; Write-Host ('=' * 78)
}
function Write-Fail([string]$t) { Write-Host "FAIL  $t" -ForegroundColor Red }
function Write-Ok  ([string]$t) { Write-Host "ok    $t" -ForegroundColor Green }

Write-Section 'DROP 08 Phase 5 - GATE 2 / E1: classic COM surrogate isolation'
Write-Host "repo   : $repoRoot"
if ($transcript) { Write-Host "log    : $transcript" }
Write-Host "clsid  : $CLSID"

# -------------------------------------------------------------------------------------------------
# 0. Preconditions
# -------------------------------------------------------------------------------------------------
Write-Section '0. PRECONDITIONS'

$bridgeDll  = Join-Path $bridgeOut 'NOBA.Phase5.G1.Bridge.dll'
$runtimeCfg = Join-Path $bridgeOut 'NOBA.Phase5.G1.Bridge.runtimeconfig.json'
foreach ($f in @($bridgeDll, $runtimeCfg)) {
    if (-not (Test-Path $f)) {
        Write-Fail "the G.1 bridge is not built: $f"
        Write-Host '      Run .\harness\phase5-g1\run-g1.ps1 first (or at least let it build).'
        if ($transcript) { Stop-Transcript | Out-Null }
        exit 1
    }
}
Write-Ok "bridge present: $bridgeOut"

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) { Write-Fail 'dotnet is not on PATH.'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
$dotnetRoot = Split-Path -Parent $dotnetCmd.Source
$fxrPick = Get-ChildItem (Join-Path $dotnetRoot 'host\fxr') -Directory |
    ForEach-Object { $v = $null; if ([version]::TryParse($_.Name, [ref]$v)) { [pscustomobject]@{ V = $v; P = $_.FullName } } } |
    Sort-Object V | Select-Object -Last 1
if (-not $fxrPick) { Write-Fail 'no hostfxr found'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
$hostfxr = Join-Path $fxrPick.P 'hostfxr.dll'
Write-Ok "hostfxr: $hostfxr"

Write-Host "os     : $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"
foreach ($f in @((Join-Path $srcDir 'noba_e_probe.cpp'), (Join-Path $cliDir 'e_client.cpp'))) {
    Write-Host ("source : {0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.Substring(0,16), (Split-Path -Leaf $f))
}

# -------------------------------------------------------------------------------------------------
# 1. Build
# -------------------------------------------------------------------------------------------------
Write-Section '1. BUILD'

$probeDll = Join-Path $srcDir 'noba_e_probe.dll'
$clientEx = Join-Path $cliDir 'e_client.exe'

if ($SkipBuild) {
    Write-Host 'skipped by request'
    foreach ($f in @($probeDll, $clientEx)) {
        if (-not (Test-Path $f)) { Write-Fail "-SkipBuild given but $f is missing"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    }
} else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { Write-Fail 'vswhere.exe not found - VS Build Tools with the C++ workload are required.'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) { Write-Fail 'no VS installation with the C++ toolset.'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
    $envDump = & cmd.exe /c "`"$vcvars`" >nul 2>&1 && set"
    foreach ($line in $envDump) { if ($line -match '^([^=]+)=(.*)$') { Set-Item -Path ('Env:' + $matches[1]) -Value $matches[2] -ErrorAction SilentlyContinue } }
    $cl = Get-Command cl.exe -ErrorAction SilentlyContinue
    if (-not $cl) { Write-Fail 'cl.exe not on PATH after vcvars64.bat'; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
    Write-Host "cl      : $($cl.Source)"
    Write-Host "toolset : $env:VCToolsVersion"

    Push-Location $srcDir
    try {
        & cl.exe /nologo /std:c++17 /EHsc /O2 /W3 /LD noba_e_probe.cpp /Fe:noba_e_probe.dll `
                 /link /DEF:noba_e_probe.def ole32.lib oleaut32.lib uuid.lib
        $rc1 = $LASTEXITCODE
    } finally { Pop-Location }
    if ($rc1 -ne 0 -or -not (Test-Path $probeDll)) { Write-Fail "probe DLL build failed (exit $rc1)"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }

    Push-Location $cliDir
    try {
        & cl.exe /nologo /std:c++17 /EHsc /O2 /W3 e_client.cpp /Fe:e_client.exe `
                 /link ole32.lib oleaut32.lib uuid.lib
        $rc2 = $LASTEXITCODE
    } finally { Pop-Location }
    if ($rc2 -ne 0 -or -not (Test-Path $clientEx)) { Write-Fail "client build failed (exit $rc2)"; if ($transcript) { Stop-Transcript | Out-Null }; exit 1 }
}
Write-Ok "probe : $probeDll"
Write-Ok "client: $clientEx"

# -------------------------------------------------------------------------------------------------
# 2. Stage
#
# The probe is copied NEXT TO the bridge and derives every path from its own module location. A
# surrogate that has to be told where things are is a surrogate with a configuration failure mode;
# this one has only the one file it is handed, and says so loudly if that is missing.
# -------------------------------------------------------------------------------------------------
Write-Section '2. STAGE'

$stagedDll = Join-Path $bridgeOut 'noba_e_probe.dll'
Copy-Item $probeDll $stagedDll -Force
Set-Content -Path (Join-Path $bridgeOut 'hostfxr.path') -Value $hostfxr -Encoding ASCII -NoNewline
$probeLog = Join-Path $bridgeOut 'noba_e_probe.log'
if (Test-Path $probeLog) { Remove-Item $probeLog -Force }
Write-Ok "staged: $stagedDll"

# -------------------------------------------------------------------------------------------------
# 3. Register / run / unregister
#
# Microsoft.Win32.Registry literal API, not Set-ItemProperty: the PowerShell registry provider
# resolves a literal '*' path segment against every subkey of Software\Classes, which took a Phase 4
# registration from 35 ms to 840 seconds. That defect is documented in shell-register.ps1 and the
# lesson carries here.
# -------------------------------------------------------------------------------------------------
Write-Section '3. REGISTER -> RUN -> UNREGISTER'

$clsidKey = "Software\Classes\CLSID\$CLSID"
$appidKey = "Software\Classes\AppID\$CLSID"
$registered = $false
$clientExit = 99

try {
    $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($clsidKey)
    $k.SetValue('', 'NOBA Phase 5 E1 surrogate probe (HARNESS - safe to delete)')
    $k.SetValue('AppID', $CLSID)
    $k.Dispose()

    $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$clsidKey\InprocServer32")
    $k.SetValue('', $stagedDll)
    $k.SetValue('ThreadingModel', 'Both')
    $k.Dispose()

    # The empty string is the documented way to select the system-supplied surrogate, dllhost.exe.
    # A missing value is not the same thing: without DllSurrogate present, COM ignores
    # CLSCTX_LOCAL_SERVER for an inproc server and the DLL would load in-process.
    $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($appidKey)
    $k.SetValue('DllSurrogate', '')
    $k.Dispose()

    $registered = $true
    Write-Ok "registered HKCU\$clsidKey  (DllSurrogate = '' -> dllhost.exe)"

    Write-Host ''
    & $clientEx 2>&1 | ForEach-Object { Write-Host $_ }
    $clientExit = $LASTEXITCODE
}
finally {
    if ($registered -and -not $KeepRegistered) {
        foreach ($key in @($clsidKey, $appidKey)) {
            try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($key, $false) } catch { }
        }
        $survivors = @()
        foreach ($key in @($clsidKey, $appidKey)) {
            $probeKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
            if ($probeKey) { $survivors += "HKCU\$key"; $probeKey.Dispose() }
        }
        Write-Host ''
        if ($survivors.Count -eq 0) { Write-Ok 'unregistered - nothing NOBA-owned survives under HKCU' }
        else { Write-Fail "residue left behind: $($survivors -join ', ')" }
    }
    elseif ($registered) {
        Write-Host ''
        Write-Host "STILL REGISTERED by request. Remove with:" -ForegroundColor Yellow
        Write-Host "  [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('$clsidKey', `$false)"
        Write-Host "  [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('$appidKey', `$false)"
    }
}

# -------------------------------------------------------------------------------------------------
# 4. The surrogate's own log
# -------------------------------------------------------------------------------------------------
Write-Section '4. PROBE LOG (written from inside the surrogate)'
if (Test-Path $probeLog) { Get-Content $probeLog | ForEach-Object { Write-Host "  $_" } }
else { Write-Host '  (no log - the DLL was never loaded anywhere)' }

# -------------------------------------------------------------------------------------------------
# 5. Verdict
# -------------------------------------------------------------------------------------------------
Write-Section '5. E1 VERDICT'

if ($clientExit -eq 0) {
    Write-Ok 'E1 PASS - all four criteria met'
} else {
    Write-Fail "E1 FAIL - client exit $clientExit. See the criteria above; a partial result is a FAIL."
}

Write-Host ''
Write-Host 'E1 PASS DOES NOT IMPLY E2 PASS.' -ForegroundColor Yellow
Write-Host '  This exercised the CLASSIC HKCU activation path. Explorer activates packaged context'
Write-Host '  menu handlers through the packaged COM catalogue, which is a different path. Only E2'
Write-Host '  tests that one, and Gate 2 is FAIL unless BOTH pass.'
Write-Host ''
Write-Host 'Nothing elevated. No package, no certificate, no HKLM, no IExplorerCommand.'

if ($transcript) { Write-Host ''; Write-Host "transcript: $transcript"; Stop-Transcript | Out-Null }
exit $clientExit
