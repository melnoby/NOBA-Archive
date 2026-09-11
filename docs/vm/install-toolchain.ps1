#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - VM TOOLCHAIN INSTALL. Installs exactly four things and nothing else.

.DESCRIPTION
    Installs, on THIS machine, only:

      1. .NET SDK 10.0.400            (carries runtime Microsoft.NETCore.App 10.0.11 and the
                                       Microsoft.AspNetCore.App shared framework the offline build
                                       FrameworkReferences)
      2. Visual Studio 2022 Build Tools, with exactly three component IDs:
           Microsoft.VisualStudio.Workload.VCTools
           Microsoft.VisualStudio.Component.VC.Tools.x86.x64
           Microsoft.VisualStudio.Component.Windows11SDK.26100

    --includeRecommended is deliberately NOT passed. Those three IDs are what the harness invokes;
    a smaller install is a smaller set of variables in the Gate 1 numbers this VM will produce.

    NOT INSTALLED, deliberately: Visual Studio IDE, Windows App SDK, Microsoft.Windows.SDK.BuildTools,
    PowerShell 7, and every other workload. Justification per item is in
    docs/DROP08_PHASE5_VM_TOOLCHAIN.md section 1.

    WHAT THIS SCRIPT DOES NOT DO
      - starts no gate: no run-g1.ps1, no run-e1.ps1, no run-e2.ps1
      - does not run the verification either. Run it yourself afterwards; keeping install and
        verification separate is the point of having a verification.
      - touches no registry policy, no Developer Mode, no AppModelUnlock, no certificate
      - changes nothing in the repository. Downloads live in %TEMP%.

    Requires elevation. Installers need it; the gates must NOT have it.

.PARAMETER DownloadOnly
    Download both installers and verify their Authenticode signatures. Install nothing.
    Use this to stage a VM with no network at install time, or to inspect before committing.

.PARAMETER KeepInstallers
    Leave the downloaded installers in %TEMP% instead of deleting them.

.EXAMPLE
    # elevated
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\vm\install-toolchain.ps1

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\vm\install-toolchain.ps1 -DownloadOnly
#>
[CmdletBinding()]
param([switch]$DownloadOnly, [switch]$KeepInstallers)

$ErrorActionPreference = 'Stop'

$SdkVersion = '10.0.400'
$SdkUrl     = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe'
$SdkChecksums = 'https://builds.dotnet.microsoft.com/dotnet/checksums/10.0.11-sha.txt'
$VsUrl      = 'https://aka.ms/vs/17/release/vs_BuildTools.exe'
$VsComponents = @(
    'Microsoft.VisualStudio.Workload.VCTools',
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
    'Microsoft.VisualStudio.Component.Windows11SDK.26100'
)
$MinFreeGb = 15

function Say ([string]$m) { Write-Host $m }
function Ok  ([string]$m) { Write-Host ("  [ok]   {0}" -f $m) -ForegroundColor Green }
function Bad ([string]$m) { Write-Host ("  [FAIL] {0}" -f $m) -ForegroundColor Red }
function Warn([string]$m) { Write-Host ("  [warn] {0}" -f $m) -ForegroundColor Yellow }
function Section([string]$t) { Write-Host ''; Write-Host ('=' * 78); Write-Host $t; Write-Host ('=' * 78) }

Write-Host ''
Write-Host ('=' * 78)
Write-Host 'DROP 08 Phase 5 - VM TOOLCHAIN INSTALL'
Write-Host '  installs .NET SDK 10.0.400 and VS 2022 Build Tools with three components.'
Write-Host '  no gate is started. no registry policy, no certificate, no repository change.'
Write-Host ('=' * 78)

# =================================================================================================
Section '0. PRECONDITIONS'
# =================================================================================================
$id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
            [System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $DownloadOnly) {
    Bad 'this shell is not elevated'
    Say '  Installers require Administrator. Open an elevated Windows PowerShell and re-run.'
    Say '  Note: the GATES must NOT be run elevated. This script is not a gate.'
    exit 1
}
if ($isAdmin) { Ok 'elevated' } else { Ok 'not elevated (-DownloadOnly needs no elevation)' }

$sysDrive = $env:SystemDrive
$free = $null
try {
    $d = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$sysDrive'" -ErrorAction Stop
    $free = [math]::Round($d.FreeSpace / 1GB, 1)
} catch { }
if ($null -eq $free) { Warn "free space on $sysDrive could not be read" }
elseif ($free -lt $MinFreeGb) { Bad "$free GB free on $sysDrive - at least $MinFreeGb GB is needed"; exit 1 }
else { Ok "$free GB free on $sysDrive" }

# Large downloads on Windows PowerShell 5.1 are drastically slower with the progress bar on, and
# some builds still default to a protocol list that excludes TLS 1.2.
$ProgressPreference = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$stage = Join-Path $env:TEMP 'noba-phase5-toolchain'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Ok "staging directory: $stage"

# =================================================================================================
Section '1. DOWNLOAD'
# =================================================================================================
function Get-Installer {
    param([string]$Url, [string]$OutFile, [string]$Label)
    if (Test-Path $OutFile) {
        Ok "$Label already downloaded ($([math]::Round((Get-Item $OutFile).Length / 1MB, 1)) MB)"
        return
    }
    Say "  downloading $Label ..."
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    Ok "$Label ($([math]::Round((Get-Item $OutFile).Length / 1MB, 1)) MB)"
}

# Authenticode rather than a pinned hash. Both installers are executed with Administrator rights, so
# provenance is not optional - and the VS bootstrapper at aka.ms is a rolling build with no stable
# published hash, which a pinned SHA could not cover. A valid Microsoft signature covers both, and
# covers whatever build the bootstrapper happens to be today.
function Assert-MicrosoftSigned {
    param([string]$Path, [string]$Label)
    $sig = Get-AuthenticodeSignature -FilePath $Path
    if ($sig.Status -ne 'Valid') {
        Bad "$Label signature status is '$($sig.Status)' - refusing to run it"
        return $false
    }
    $subject = ''
    if ($sig.SignerCertificate) { $subject = $sig.SignerCertificate.Subject }
    if ($subject -notmatch 'O=Microsoft Corporation') {
        Bad "$Label is signed, but not by Microsoft: $subject"
        return $false
    }
    Ok "$Label signature valid - $subject"
    return $true
}

$sdkExe = Join-Path $stage "dotnet-sdk-$SdkVersion-win-x64.exe"
$vsExe  = Join-Path $stage 'vs_BuildTools.exe'

Get-Installer -Url $SdkUrl -OutFile $sdkExe -Label ".NET SDK $SdkVersion"
Get-Installer -Url $VsUrl  -OutFile $vsExe  -Label 'VS 2022 Build Tools bootstrapper'

$sdkOk = Assert-MicrosoftSigned -Path $sdkExe -Label ".NET SDK $SdkVersion"
$vsOk  = Assert-MicrosoftSigned -Path $vsExe  -Label 'VS Build Tools bootstrapper'
if (-not $sdkOk -or -not $vsOk) {
    Bad 'refusing to install an installer that is not validly signed by Microsoft'
    exit 1
}
Say ''
Say "  To pin the SDK by hash as well, the published checksums are at:"
Say "    $SdkChecksums"

if ($DownloadOnly) {
    Section 'DOWNLOAD ONLY - nothing was installed'
    Say "  installers staged in: $stage"
    Say '  re-run without -DownloadOnly, elevated, to install.'
    exit 0
}

# =================================================================================================
Section '2. INSTALL - .NET SDK 10.0.400'
# =================================================================================================
$p = Start-Process -FilePath $sdkExe -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
$sdkExit = $p.ExitCode
$rebootNeeded = $false
if ($sdkExit -eq 0) { Ok ".NET SDK $SdkVersion installed (exit 0)" }
elseif ($sdkExit -eq 3010) { Ok ".NET SDK $SdkVersion installed (exit 3010 - reboot required)"; $rebootNeeded = $true }
elseif ($sdkExit -eq 1638) { Warn ".NET SDK: exit 1638 - a newer version is already present. Not an error; verification will report which." }
else { Bad ".NET SDK install failed (exit $sdkExit)"; Say '  logs: %TEMP%\dd_*.log'; exit 1 }

# =================================================================================================
Section '3. INSTALL - VS 2022 Build Tools, three components, nothing else'
# =================================================================================================
$vsArgs = @('--quiet', '--wait', '--norestart', '--nocache')
foreach ($c in $VsComponents) { $vsArgs += '--add'; $vsArgs += $c }
Say '  components:'
foreach ($c in $VsComponents) { Say "    $c" }
Say '  --includeRecommended is NOT passed.'
Say '  this takes a while and prints nothing while it works.'

$p = Start-Process -FilePath $vsExe -ArgumentList $vsArgs -Wait -PassThru
$vsExit = $p.ExitCode
if ($vsExit -eq 0) { Ok 'VS Build Tools installed (exit 0)' }
elseif ($vsExit -eq 3010) { Ok 'VS Build Tools installed (exit 3010 - reboot required)'; $rebootNeeded = $true }
elseif ($vsExit -eq 1602) { Bad 'VS Build Tools: exit 1602 - the install was cancelled'; exit 1 }
else {
    Bad "VS Build Tools install failed (exit $vsExit)"
    Say '  logs: %TEMP%\dd_setup_*.log and %TEMP%\dd_bootstrapper_*.log'
    exit 1
}

# =================================================================================================
Section 'RESULT'
# =================================================================================================
if (-not $KeepInstallers) {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $stage)) { Ok 'installers removed from %TEMP%' }
} else {
    Ok "installers kept: $stage"
}

Say ''
if ($rebootNeeded) {
    Write-Host 'REBOOT REQUIRED before verifying.' -ForegroundColor Yellow
    Say '  An installer returned 3010. vcvars64.bat may not resolve until the machine restarts.'
    Say '  Restart, then run:'
} else {
    Write-Host 'TOOLCHAIN INSTALLED. No reboot was requested.' -ForegroundColor Green
    Say '  Next, in a NON-elevated shell, run:'
}
Say ''
Say '    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docs\vm\verify-toolchain.ps1'
Say ''
Say 'No gate was started. No registry policy, no certificate, and nothing in the repository was changed.'
Write-Host ('=' * 78)
exit 0
