#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - VM TOOLCHAIN VERIFICATION. Reports only. Starts no gate.

.DESCRIPTION
    Answers one question: is this VM's toolchain the one the passing Gate 1 / E1 runs used?

    Every check below is derived from what the harness ACTUALLY INVOKES, read out of the scripts,
    not from a general idea of what a C++/.NET machine needs:

      dotnet            run-g1.ps1:150, run-e1.ps1:86  - Get-Command dotnet
      net10.0           bridge csproj, Directory.Build.props:18
      AspNetCore.App    Directory.Build.targets - NobaUsesExtensions + NobaOfflineBuild take a
                        FrameworkReference to Microsoft.AspNetCore.App, so a runtime-only or
                        NETCore-only install is NOT enough
      vswhere           run-g1.ps1:234 - exact path under ProgramFiles(x86)
      -requires         run-g1.ps1:243 - Microsoft.VisualStudio.Component.VC.Tools.x86.x64
      vcvars64.bat      run-g1.ps1:251
      cl.exe            run-g1.ps1:277, run-e1.ps1:129/137, run-e2.ps1:330/333
      link libs         ole32.lib oleaut32.lib uuid.lib (E1), + shlwapi.lib (E2)
      makeappx/signtool run-e2.ps1:431-436 - Windows Kits\10\bin\10.*\x64

    WHAT THIS SCRIPT DOES NOT DO
      - starts no gate: no run-g1.ps1, no run-e1.ps1, no run-e2.ps1
      - reads no policy: no Appx, no AppModelUnlock, no Developer Mode
      - creates no certificate, no package, no registration
      - writes nothing into the repository. The compile probe lives in %TEMP% and is deleted.

.PARAMETER SkipCompileProbe
    Skip the contained cl.exe compile+link in %TEMP%. The probe is the only step that produces a
    file anywhere, and it is the only step that proves the toolchain end to end rather than proving
    that four executables exist.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\vm\verify-toolchain.ps1
#>
[CmdletBinding()]
param([switch]$SkipCompileProbe)

$ErrorActionPreference = 'Continue'

# The versions the passing Gate 1 run used (DROP08_PHASE5_G1_REPORT.md section 6.2). A different
# version is reported as a DIFFERENCE, never silently accepted: steps 4 and 5 of the VM checklist
# re-establish Gate 1 and E1 on this machine, and a toolchain delta would be a confound in those
# numbers rather than a detail.
$ExpectedSdk       = '10.0.400'
$ExpectedNetCore   = '10.0.11'
$ExpectedMsvc      = '14.44.35207'
$ExpectedWinSdk    = '10.0.26100'
$MinOsBuild        = 22000

$pass = 0; $fail = 0; $diff = 0
function Ok   ([string]$w, [string]$d) { Write-Host ("  [ok]   {0}" -f $w) -ForegroundColor Green;  if ($d) { Write-Host "         $d" }; $script:pass++ }
function Bad  ([string]$w, [string]$d) { Write-Host ("  [FAIL] {0}" -f $w) -ForegroundColor Red;    if ($d) { Write-Host "         $d" }; $script:fail++ }
function Note ([string]$w, [string]$d) { Write-Host ("  [diff] {0}" -f $w) -ForegroundColor Yellow; if ($d) { Write-Host "         $d" }; $script:diff++ }
function Section([string]$t) { Write-Host ''; Write-Host ('=' * 78); Write-Host $t; Write-Host ('=' * 78) }

Write-Host ''
Write-Host ('=' * 78)
Write-Host 'DROP 08 Phase 5 - VM TOOLCHAIN VERIFICATION'
Write-Host '  reports only. no gate is started. no registry, policy or certificate is touched.'
Write-Host ('=' * 78)

# =================================================================================================
Section '1. HOST'
# =================================================================================================
$os = $null
try { $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop } catch { }
if ($null -eq $os) {
    Bad 'Windows' 'Win32_OperatingSystem could not be read - this is not a Windows host'
} else {
    $build = 0
    if ($os.BuildNumber -match '^\d+$') { $build = [int]$os.BuildNumber }
    if ($build -ge $MinOsBuild) { Ok "OS build $build (>= $MinOsBuild)" "$($os.Caption), $($os.OSArchitecture)" }
    else { Bad "OS build $build is below $MinOsBuild" 'the modern context menu does not exist before Windows 11 22000' }
}

$psv = $PSVersionTable.PSVersion.ToString()
if ($PSVersionTable.PSEdition -eq 'Desktop' -and $PSVersionTable.PSVersion.Major -eq 5) {
    Ok "Windows PowerShell $psv" 'the harness targets 5.1; pwsh 7 is not required and is not installed by default'
} else {
    Note "running under PowerShell $psv ($($PSVersionTable.PSEdition))" 'the harness runs under Windows PowerShell 5.1 on the VM; re-run this under powershell.exe'
}

# The ANSI codepage, because it is the variable that decides how Windows PowerShell 5.1 decodes a
# BOM-less script (DELTA section 13). Under 5.1, [Text.Encoding]::Default IS the ANSI codepage; under
# PowerShell 7 it is always UTF-8 and says nothing about the machine. Reported, not asserted: the
# harness scripts are pure ASCII, so no codepage can change how they decode. This line exists so a
# future parse failure can be interpreted instead of guessed at.
$acp = $null
try { $acp = [System.Text.Encoding]::Default.CodePage } catch { }
if ($PSVersionTable.PSEdition -eq 'Desktop' -and $acp) {
    Write-Host "  [info] ANSI codepage: $acp" -ForegroundColor DarkGray
    if ($acp -eq 65001) { Write-Host '         65001 (UTF-8) - a BOM-less UTF-8 script would decode correctly here even if it were not ASCII' }
} else {
    Write-Host "  [info] ANSI codepage not reported (PowerShell $($PSVersionTable.PSEdition))" -ForegroundColor DarkGray
}

$elevated = $false
try {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
                 [System.Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }
if ($elevated) { Note 'this shell is ELEVATED' 'run-e2.ps1 refuses to run elevated. Installing needs elevation; the gates do not.' }
else           { Ok 'this shell is not elevated' 'which is what run-e2.ps1 requires' }

# =================================================================================================
Section '2. .NET SDK'
# =================================================================================================
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Bad 'dotnet is not on PATH' 'run-g1.ps1:150 and run-e1.ps1:86 stop here'
} else {
    Ok 'dotnet on PATH' $dotnet.Source

    $sdkVersion = (& dotnet --version 2>$null | Select-Object -First 1)
    if ($sdkVersion -eq $ExpectedSdk) { Ok "SDK $sdkVersion" 'matches the Gate 1 run' }
    elseif ($sdkVersion) { Note "SDK $sdkVersion" "Gate 1 ran on $ExpectedSdk" }
    else { Bad 'dotnet --version produced nothing' '' }

    $sdks = @(& dotnet --list-sdks 2>$null)
    $has10 = @($sdks | Where-Object { $_ -match '^10\.' })
    if ($has10.Count -gt 0) { Ok "a 10.x SDK is installed" (($has10 | ForEach-Object { ($_ -split ' ')[0] }) -join ', ') }
    else { Bad 'no 10.x SDK found' 'every project targets net10.0 (Directory.Build.props:18)' }

    $runtimes = @(& dotnet --list-runtimes 2>$null)

    $netcore = @($runtimes | Where-Object { $_ -like 'Microsoft.NETCore.App 10.*' })
    if ($netcore.Count -gt 0) {
        $v = ($netcore[-1] -split ' ')[1]
        if ($v -eq $ExpectedNetCore) { Ok "Microsoft.NETCore.App $v" 'matches the Gate 1 run' }
        else { Note "Microsoft.NETCore.App $v" "Gate 1 ran on $ExpectedNetCore" }
    } else { Bad 'no Microsoft.NETCore.App 10.x runtime' '' }

    # NOT optional. Directory.Build.targets gives every NobaUsesExtensions project a FrameworkReference
    # to Microsoft.AspNetCore.App when NobaOfflineBuild=true, which is how the build avoids nuget.org.
    # The bridge sets NobaUsesExtensions, and so does the whole Engine chain it references.
    $aspnet = @($runtimes | Where-Object { $_ -like 'Microsoft.AspNetCore.App 10.*' })
    if ($aspnet.Count -gt 0) {
        Ok "Microsoft.AspNetCore.App $((($aspnet[-1]) -split ' ')[1])" 'required: the offline build takes a FrameworkReference to it'
    } else {
        Bad 'no Microsoft.AspNetCore.App 10.x shared framework' `
            'the offline build (NobaOfflineBuild=true) FrameworkReferences it - install the SDK, not a runtime-only package'
    }
}

# =================================================================================================
Section '3. MSVC - located exactly as the harness locates it'
# =================================================================================================
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath  = $null
if (-not (Test-Path $vswhere)) {
    Bad 'vswhere.exe not found' "$vswhere - install Visual Studio Build Tools 2022"
} else {
    Ok 'vswhere.exe' $vswhere
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    if ($vsPath) { $vsPath = @($vsPath)[0] }
    if (-not $vsPath) {
        Bad 'no install satisfies -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64' `
            'this is the exact query run-g1.ps1:243 makes. The C++ workload is missing.'
    } else {
        Ok 'VC.Tools.x86.x64 present' $vsPath
        $prod = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property catalog_productDisplayVersion 2>$null
        if ($prod) { Write-Host "         VS version: $(@($prod)[0])" }
    }
}

$vcvars = $null
if ($vsPath) {
    $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
    if (Test-Path $vcvars) { Ok 'vcvars64.bat' $vcvars }
    else { Bad 'vcvars64.bat not found' $vcvars; $vcvars = $null }
}

$clPath = $null; $vcToolsVersion = $null; $winSdkFromVcvars = $null
if ($vcvars) {
    # Imported into THIS process exactly as run-g1.ps1:256 does, so what is verified is what the
    # harness will get - not what a Developer Command Prompt would have given.
    $envDump = & cmd.exe /c "`"$vcvars`" >nul 2>&1 && set"
    if ($LASTEXITCODE -ne 0) {
        Bad "vcvars64.bat failed (exit $LASTEXITCODE)" ''
    } else {
        foreach ($line in $envDump) {
            $i = $line.IndexOf('=')
            if ($i -gt 0) { Set-Item -Path ("Env:" + $line.Substring(0, $i)) -Value $line.Substring($i + 1) -ErrorAction SilentlyContinue }
        }
        $vcToolsVersion   = $env:VCToolsVersion
        $winSdkFromVcvars = $env:WindowsSDKVersion
        $cl = Get-Command cl.exe -ErrorAction SilentlyContinue
        if ($cl) {
            $clPath = $cl.Source
            Ok 'cl.exe on PATH after vcvars64.bat' $clPath
            if ($vcToolsVersion -eq $ExpectedMsvc) { Ok "MSVC toolset $vcToolsVersion" 'matches the Gate 1 run' }
            elseif ($vcToolsVersion) { Note "MSVC toolset $vcToolsVersion" "Gate 1 ran on $ExpectedMsvc" }
            $banner = (& cmd.exe /c "cl.exe 2>&1 | findstr /C:Version") 2>$null
            if ($banner) { Write-Host "         $(@($banner)[0].Trim())" }
        } else {
            Bad 'cl.exe still not on PATH after vcvars64.bat' 'the C++ toolset is not actually installed'
        }
    }
}

# =================================================================================================
Section '4. WINDOWS SDK - located exactly as run-e2.ps1 locates it'
# =================================================================================================
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$sdkBin  = $null
if (-not (Test-Path $sdkRoot)) {
    Bad 'Windows Kits\10\bin not found' $sdkRoot
} else {
    $sdkBin = Get-ChildItem $sdkRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64' } |
        Where-Object { (Test-Path (Join-Path $_ 'makeappx.exe')) -and (Test-Path (Join-Path $_ 'signtool.exe')) } |
        Select-Object -First 1

    if (-not $sdkBin) {
        Bad 'no SDK bin directory carries BOTH makeappx.exe and signtool.exe' `
            'run-e2.ps1:436 requires both in the same directory. Install the Windows 11 SDK component.'
    } else {
        Ok 'makeappx.exe + signtool.exe' $sdkBin
        $chosen = Split-Path -Leaf (Split-Path -Parent $sdkBin)
        if ($chosen -like "$ExpectedWinSdk*") { Ok "Windows SDK $chosen" 'matches the Gate 1 run' }
        else { Note "Windows SDK $chosen" "Gate 1 ran on $ExpectedWinSdk.0" }
        if ($winSdkFromVcvars) { Write-Host "         vcvars64 selected: $($winSdkFromVcvars.TrimEnd('\'))" }
    }
}

# The libraries cl.exe is actually told to link. Checking these is the difference between "an SDK is
# installed" and "the SDK this build links against is installed".
$libDir = $null
if ($env:LIB) {
    $libDir = @($env:LIB -split ';' | Where-Object { $_ -and (Test-Path $_) })
}
if ($libDir -and $libDir.Count -gt 0) {
    $needed = @('ole32.lib', 'oleaut32.lib', 'uuid.lib', 'shlwapi.lib')
    $missing = @()
    foreach ($n in $needed) {
        $found = $false
        foreach ($d in $libDir) { if (Test-Path (Join-Path $d $n)) { $found = $true; break } }
        if (-not $found) { $missing += $n }
    }
    if ($missing.Count -eq 0) { Ok 'link libraries present' ($needed -join ', ') }
    else { Bad 'link libraries missing' ($missing -join ', ') }
} elseif ($vcvars) {
    # vcvars imported but LIB is empty: that IS a defect, and a distinct one.
    Bad 'LIB is not set after vcvars64.bat imported' 'the link step would fail'
} else {
    # MSVC is absent, which section 3 already reported. Counting the unreachable LIB check as a
    # separate difference would report one defect twice and make the totals mean less than they say.
    Write-Host '  [skip] link libraries not checked' -ForegroundColor DarkGray
    Write-Host '         MSVC is absent (section 3) - this is the same failure, not another one'
}

# =================================================================================================
Section '5. COMPILE PROBE - contained, in %TEMP%, deleted afterwards'
# =================================================================================================
if ($SkipCompileProbe) {
    Note 'compile probe skipped (-SkipCompileProbe)' 'four executables existing is not the same as a toolchain that builds'
} elseif (-not $clPath) {
    Bad 'compile probe not attempted' 'cl.exe is not available'
} else {
    # The same shape as the E2 stub: a COM DLL with an exported DllGetClassObject, linked against the
    # same libraries. It proves headers, libs, linker and the DEF path all work. It is NOT the stub,
    # it is NOT built in the repository, and it registers nothing.
    $probeDir = Join-Path $env:TEMP ("noba-toolchain-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $probeDir -Force | Out-Null
    try {
        $cpp = @'
#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <objbase.h>
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID, REFIID, void** ppv)
{
    if (ppv) { *ppv = nullptr; }
    return CLASS_E_CLASSNOTAVAILABLE;
}
extern "C" HRESULT __stdcall DllCanUnloadNow(void) { return S_OK; }
'@
        $def = @'
LIBRARY noba_toolchain_probe
EXPORTS
    DllGetClassObject   PRIVATE
    DllCanUnloadNow     PRIVATE
'@
        Set-Content -Path (Join-Path $probeDir 'probe.cpp') -Value $cpp -Encoding ASCII
        Set-Content -Path (Join-Path $probeDir 'probe.def') -Value $def -Encoding ASCII

        Push-Location $probeDir
        try {
            $out = & cl.exe /nologo /std:c++17 /EHsc /O2 /W3 /LD probe.cpp /Fe:probe.dll `
                            /link /DEF:probe.def ole32.lib oleaut32.lib uuid.lib shlwapi.lib 2>&1
            $rc = $LASTEXITCODE
        } finally { Pop-Location }

        if ($rc -eq 0 -and (Test-Path (Join-Path $probeDir 'probe.dll'))) {
            Ok 'cl.exe compiled and linked a COM DLL' '/std:c++17 /EHsc /O2 /W3 /LD + ole32 oleaut32 uuid shlwapi, DEF exports'
        } else {
            Bad "compile probe failed (exit $rc)" (($out | Select-Object -Last 6) -join ' | ')
        }
    } finally {
        Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path $probeDir)) { Ok 'probe directory removed' 'nothing left behind' }
    else { Note 'probe directory could not be removed' $probeDir }
}

# =================================================================================================
Section 'RESULT'
# =================================================================================================
Write-Host ''
if ($fail -eq 0 -and $diff -eq 0) {
    Write-Host "TOOLCHAIN VERIFIED: $pass checks, 0 failures, 0 differences" -ForegroundColor Green
} elseif ($fail -eq 0) {
    Write-Host "TOOLCHAIN USABLE: $pass checks, 0 failures, $diff difference(s) from the Gate 1 run" -ForegroundColor Yellow
    Write-Host '  A difference is not a failure. It is a variable in the Gate 1 / E1 numbers this VM'
    Write-Host '  will produce at checklist steps 4 and 5. Record it; do not ignore it.'
} else {
    Write-Host "TOOLCHAIN INCOMPLETE: $pass ok, $fail failure(s), $diff difference(s)" -ForegroundColor Red
}
Write-Host ''
Write-Host 'No gate was started. No registry value, policy, or certificate was read or written.'
Write-Host ('=' * 78)
exit $(if ($fail -eq 0) { 0 } else { 1 })
