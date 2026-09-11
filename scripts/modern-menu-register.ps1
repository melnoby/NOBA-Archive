#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - packs, signs and registers the sparse package that gives NOBA the Windows 11
    MODERN context menu (surface A).

.DESCRIPTION
    Everything that can fail for free fails first, and nothing is created before every prerequisite
    has been proven:

        preconditions -> sideloading GATE -> certificate -> generate manifest -> stage -> pack
        -> sign -> register

    EVERY ORDERING RULE HERE WAS PAID FOR. Four E2 attempts failed before the packaged activation
    path passed, and each failure moved a check earlier:

      C00CE014    all-desktop5 prefixes            -> the manifest is GENERATED, never typed
      0x800B0109  certificate in the user store    -> LocalMachine\TrustedPeople, verified by reading back
      0x80073D2E  AllowExternalContent missing     -> the generator writes it; a test asserts it
      0x80073CFF  sideloading blocked              -> a GATE before anything is created, not a note

    THE FOURTH IS THE ONE WORTH REPEATING. The harness printed the sideloading state and deployed
    anyway. A fact that is printed but not wired to a decision is decoration. This script REFUSES.

    IT NEVER CHANGES A SECURITY SETTING. Sideloading, Developer Mode, Group Policy and MDM state are
    read and reported, never written. A policy value may belong to an organisation rather than to the
    developer, and a build script is not the place to overrule one.

.PARAMETER Remove
    Unregister the package. The external-location registration has no separate lifetime - it is a
    property of the registered package and goes with it.

.PARAMETER PreconditionsOnly
    Report whether this machine can register the package, and exit. Creates nothing, changes nothing,
    reads two registry values. Exit 0 if it can, 2 if BLOCKED BY ENVIRONMENT.

.PARAMETER SkipBuild
    Use the binaries already in the payload directory.

.PARAMETER PayloadPath
    Where the built product lives - the sparse package's external location. Defaults to the
    unpackaged build output.

.EXAMPLE
    .\scripts\modern-menu-register.ps1 -PreconditionsOnly

.EXAMPLE
    .\scripts\modern-menu-register.ps1
#>
[CmdletBinding()]
param(
    [switch]$Remove,
    [switch]$PreconditionsOnly,
    [switch]$SkipBuild,
    [string]$PayloadPath
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path

$PackageName = 'NOBA.Archive'
$CertSubject = 'CN=NOBA-DEV-ONLY'
$SparseDir = Join-Path $repoRoot 'packaging\sparse'
$StageDir = Join-Path $repoRoot 'artifacts\sparse-stage'
$MsixPath = Join-Path $repoRoot 'artifacts\noba-archive-sparse.msix'

function Write-Section([string]$t) { Write-Host ''; Write-Host ('=' * 78); Write-Host $t; Write-Host ('=' * 78) }
function Write-Ok  ([string]$m) { Write-Host "ok    $m" -ForegroundColor Green }
function Write-Bad ([string]$m) { Write-Host "FAIL  $m" -ForegroundColor Red }
function Write-Note([string]$m) { Write-Host "warn  $m" -ForegroundColor Yellow }

function Test-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Read-Dword {
    param([string]$Key, [string]$Name)
    try {
        $item = Get-ItemProperty -Path $Key -Name $Name -ErrorAction Stop
        return [int]$item.$Name
    } catch {
        return $null
    }
}

<#
.SYNOPSIS
    Whether this machine permits sideloading, and why.
.DESCRIPTION
    The decision is separated from the registry read so the BLOCKED branch is testable: it is the
    branch that matters and it is unreachable on a machine that does not have the policy.

    THE PRECEDENCE WAS MEASURED, NOT ASSUMED. On the development machine, policy
    AllowAllTrustedApps = 0 refused deployment while AppModelUnlock\AllowAllTrustedApps = 1 AND
    Developer Mode were both on. The policy value overrides both, three byte-identical transcripts
    (DELTA section 12).
#>
function Resolve-SideloadingState {
    param(
        [AllowNull()][object]$PolicyTrusted,
        [AllowNull()][object]$UnlockTrusted,
        [AllowNull()][object]$UnlockDev
    )

    if ($null -ne $PolicyTrusted -and $PolicyTrusted -eq 0) {
        return [pscustomobject]@{
            Permitted = $false
            State = 'BLOCKED BY POLICY'
            Source = 'Policies\Microsoft\Windows\Appx\AllowAllTrustedApps = 0'
            Configurable = 'NOT by this script, and not by NOBA. The value may belong to an organisation.'
            FailureCode = '0x80073CFF'
        }
    }

    if ($PolicyTrusted -eq 1) {
        return [pscustomobject]@{
            Permitted = $true; State = 'PERMITTED'
            Source = 'Policies\Microsoft\Windows\Appx\AllowAllTrustedApps = 1'
            Configurable = 'set by policy'; FailureCode = ''
        }
    }

    if ($UnlockTrusted -eq 1) {
        return [pscustomobject]@{
            Permitted = $true; State = 'PERMITTED'
            Source = 'AppModelUnlock\AllowAllTrustedApps = 1'
            Configurable = 'YES - Settings > System > For developers.'; FailureCode = ''
        }
    }

    if ($UnlockDev -eq 1) {
        return [pscustomobject]@{
            Permitted = $true; State = 'PERMITTED'
            Source = 'AppModelUnlock\AllowDevelopmentWithoutDevLicense = 1 (Developer Mode)'
            Configurable = 'YES - Settings > System > For developers.'; FailureCode = ''
        }
    }

    # Absence of a permission is not permission.
    return [pscustomobject]@{
        Permitted = $false
        State = 'NOT ENABLED'
        Source = 'no key permits it'
        Configurable = 'YES - Settings > System > For developers > Developer Mode, unless a policy appears.'
        FailureCode = '0x80073CFF'
    }
}

function Get-SideloadingReport {
    $policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx'
    $unlockKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'

    $policyTrusted = Read-Dword -Key $policyKey -Name 'AllowAllTrustedApps'
    $unlockTrusted = Read-Dword -Key $unlockKey -Name 'AllowAllTrustedApps'
    $unlockDev = Read-Dword -Key $unlockKey -Name 'AllowDevelopmentWithoutDevLicense'

    $state = Resolve-SideloadingState -PolicyTrusted $policyTrusted -UnlockTrusted $unlockTrusted -UnlockDev $unlockDev

    Write-Host ''
    Write-Host 'SIDELOADING'
    Write-Host "      effective state   : $($state.State)"
    Write-Host "      source            : $($state.Source)"
    Write-Host "      user-configurable : $($state.Configurable)"
    Write-Host "      $policyKey"
    Write-Host ("          AllowAllTrustedApps               = {0}" -f $(if ($null -eq $policyTrusted) { '(unset)' } else { $policyTrusted }))
    Write-Host "      $unlockKey"
    Write-Host ("          AllowAllTrustedApps               = {0}" -f $(if ($null -eq $unlockTrusted) { '(unset)' } else { $unlockTrusted }))
    Write-Host ("          AllowDevelopmentWithoutDevLicense = {0}" -f $(if ($null -eq $unlockDev) { '(unset)' } else { $unlockDev }))
    Write-Host '      This script NEVER writes either key. Sideloading is a machine security setting.'
    return $state
}

function Get-CertCount {
    param([string]$StoreName, [string]$Location)
    try {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $Location)
        $store.Open('ReadOnly')
        try { return @($store.Certificates | Where-Object { $_.Subject -eq $CertSubject }).Count }
        finally { try { $store.Close() } catch { } }
    } catch { return -1 }
}

<#
.SYNOPSIS
    The certificate signtool will actually use, or $null.
.DESCRIPTION
    CurrentUser\My, AND THAT IS NOT A PREFERENCE. signtool without /sm searches the CURRENT USER's
    store; this function must open the store signtool is going to search, or it proves nothing.

    An earlier version opened LocalMachine\My. It found the certificate, printed "signable", and
    signtool then failed with "No certificates were found that met all the given criteria" - a true
    statement about a store nobody had looked in. The check and the command disagreed about where a
    key lives, and neither could see the other. SigningStoreTests now refuses to let them drift.

    TRUST IS A DIFFERENT STATEMENT AND STAYS MACHINE-WIDE. The PUBLIC certificate lives in
    LocalMachine\TrustedPeople, which is what MSIX deployment reads and what 0x800B0109 is about.
    Moving the private key does not move the trust anchor.
#>
function Get-SigningCertificate {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
    $store.Open('ReadOnly')
    try {
        return @($store.Certificates | Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey }) |
            Sort-Object NotAfter -Descending | Select-Object -First 1
    } finally { try { $store.Close() } catch { } }
}

function Find-SdkTool {
    param([string]$ToolName)
    $root = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $root)) { return $null }
    return Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$ToolName" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

# =================================================================================================
Write-Section 'DROP 08 Phase 5 - modern context menu: sparse package registration'
# =================================================================================================
Write-Host "repo    : $repoRoot"
Write-Host "package : $PackageName"

if (Test-Elevated) {
    Write-Bad 'this script must NOT run elevated.'
    Write-Host '      Registering as administrator registers for the administrator, and the surrogate'
    Write-Host '      would then run as one too - which is the elevation boundary plan section L.4'
    Write-Host '      exists to keep. Only dev-certificate.ps1 needs Administrator.'
    exit 1
}

# =================================================================================================
Write-Section '0. PRECONDITIONS'
# =================================================================================================
$os = Get-CimInstance Win32_OperatingSystem
$build = 0
if ($os.BuildNumber -match '^\d+$') { $build = [int]$os.BuildNumber }
Write-Host "os      : $($os.Caption) build $build"
if ($build -lt 22000) {
    Write-Bad "the modern context menu does not exist before Windows 11 (build 22000); this is $build."
    exit 1
}

$state = Get-SideloadingReport

if ($PreconditionsOnly) {
    Write-Section 'PRECONDITIONS ONLY - nothing created, nothing changed'
    if ($state.Permitted) {
        Write-Ok 'the package can be registered on this machine.'
        exit 0
    }
    Write-Note 'BLOCKED BY ENVIRONMENT.'
    Write-Host '      This is not a product failure and not a signing problem. A clean Windows 11 machine'
    Write-Host '      that is not domain-joined, not MDM-enrolled and has no Appx policy override can'
    Write-Host '      register it. NOBA never modifies enterprise AppX policy (plan section L.13).'
    exit 2
}

# =================================================================================================
if ($Remove) {
# =================================================================================================
    Write-Section 'REMOVING'
    $package = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if ($null -eq $package) {
        Write-Ok 'not registered'
    } else {
        Remove-AppxPackage -Package $package.PackageFullName
        Write-Ok "unregistered $($package.PackageFullName) (external location released with it)"
    }

    foreach ($f in @($MsixPath)) {
        if (Test-Path $f) { Remove-Item $f -Force; Write-Ok "removed $(Split-Path -Leaf $f)" }
    }
    if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force; Write-Ok 'removed the staging directory' }

    Write-Host ''
    Write-Host 'The DEV-ONLY certificate is a separate lifetime and is NOT removed here:'
    Write-Host '  .\scripts\dev-certificate.ps1 -Remove        # elevated'
    exit 0
}

# THE GATE. Not a note (DELTA section 12).
if (-not $state.Permitted) {
    Write-Section 'REFUSING - BLOCKED BY ENVIRONMENT'
    Write-Bad "sideloading is $($state.State); Add-AppxPackage would fail $($state.FailureCode)."
    Write-Host '      Nothing has been built, packed, signed or registered.'
    Write-Host '      This script will not change the setting. Use -PreconditionsOnly for the detail.'
    exit 2
}

# =================================================================================================
Write-Section '1. CERTIFICATE'
# =================================================================================================
$trusted = Get-CertCount -StoreName 'TrustedPeople' -Location 'LocalMachine'
if ($trusted -lt 0) {
    Write-Bad 'LocalMachine\TrustedPeople could not be read - the trust state is UNKNOWN, not absent.'
    exit 1
}
if ($trusted -eq 0) {
    Write-Bad "no $CertSubject certificate is trusted in LocalMachine\TrustedPeople."
    Write-Host '      Add-AppxPackage would fail 0x800B0109. Create and trust it first:'
    Write-Host '        .\scripts\dev-certificate.ps1                # signing user, NOT elevated'
    Write-Host '        .\scripts\dev-certificate.ps1 -Trust         # elevated window'
    exit 1
}

# Two different statements, deliberately checked separately and against different stores:
#   TRUST     LocalMachine\TrustedPeople  - machine-wide, read by MSIX deployment (0x800B0109)
#   SIGNING   CurrentUser\My              - the store signtool searches without /sm
# Reporting one and needing the other is how section 5 came to fail after section 1 said "ok".
$cert = Get-SigningCertificate
if ($null -eq $cert) {
    Write-Bad "no $CertSubject certificate WITH A PRIVATE KEY in CurrentUser\My - nothing can sign."
    Write-Host '      This is the store signtool searches, and it is per-USER. A key in another'
    Write-Host '      account - including an administrator who answered a UAC prompt - is invisible'
    Write-Host '      here, and so is a key in LocalMachine\My.'
    Write-Host "      Create it as this user ($env:USERNAME), unelevated, then trust it elevated:"
    Write-Host '        .\scripts\dev-certificate.ps1                # here, NOT elevated'
    Write-Host '        .\scripts\dev-certificate.ps1 -Trust         # elevated window'
    exit 1
}
# The trusted certificate must be THIS certificate, not merely one with the same subject. A count of
# one proves a publisher is trusted; it does not prove the signature about to be produced will chain
# to it. Two DEV-ONLY certificates - an old trusted one and a new signing one - deploy as 0x800B0109
# after a signature that succeeded, which reads as a packaging fault and is not one.
$trustStore = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
$trustStore.Open('ReadOnly')
try {
    $sameCert = @($trustStore.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })
} finally { try { $trustStore.Close() } catch { } }

if ($sameCert.Count -lt 1) {
    Write-Bad "the certificate that would sign is NOT the one that is trusted."
    Write-Host "      signing : $($cert.Thumbprint)  (CurrentUser\My, $env:USERNAME)"
    Write-Host "      trusted : $trusted certificate(s) with this subject, none with that thumbprint"
    Write-Host '      Add-AppxPackage would fail 0x800B0109 AFTER signtool succeeded. Re-trust:'
    Write-Host '        .\scripts\dev-certificate.ps1 -Trust         # elevated window'
    exit 1
}

Write-Ok "trusted ($trusted) and signable: thumbprint $($cert.Thumbprint)"
Write-Host '      DEV-ONLY - NOT A SHIPPING CERTIFICATE. Phase 6 owns procurement.'

# =================================================================================================
Write-Section '2. MANIFEST - generated, never typed'
# =================================================================================================
# SHELL-065. The publisher is read FROM THE CERTIFICATE and handed to the generator, so the two
# cannot disagree. Add-AppxPackage compares them character for character and fails with a signature
# error that names neither when they differ.
$cli = Join-Path $repoRoot 'src\NOBA.Archive.Cli'
Write-Host "      publisher (from the certificate): $($cert.Subject)"

if (-not $SkipBuild) {
    & dotnet build $cli -c Release --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Bad "building the CLI failed ($LASTEXITCODE)"; exit 1 }
}

$manifestPath = Join-Path $SparseDir 'AppxManifest.xml'
& dotnet run --project $cli -c Release --no-build -- appx-manifest --publisher $cert.Subject |
    Out-File -FilePath $manifestPath -Encoding utf8
if ($LASTEXITCODE -ne 0) { Write-Bad "generating the manifest failed ($LASTEXITCODE)"; exit 1 }

[xml]$manifest = Get-Content $manifestPath -Raw
$declared = $manifest.Package.Identity.Publisher
if ($declared -ne $cert.Subject) {
    Write-Bad "the manifest says Publisher='$declared' but the certificate is '$($cert.Subject)'."
    exit 1
}
Write-Ok "manifest generated and publisher matches exactly"

# =================================================================================================
Write-Section '3. STAGE'
# =================================================================================================
if (-not $PayloadPath) {
    $PayloadPath = Join-Path $repoRoot 'artifacts\app'
}
if (-not (Test-Path $PayloadPath)) {
    Write-Bad "the payload directory does not exist: $PayloadPath"
    Write-Host '      This is the sparse package EXTERNAL LOCATION - where the installed product lives.'
    Write-Host '      Build the product first, or pass -PayloadPath.'
    exit 1
}

foreach ($required in @('NOBA.Archive.ShellAdapter.dll', 'NOBA.Archive.ShellBridge.dll')) {
    if (-not (Test-Path (Join-Path $PayloadPath $required))) {
        Write-Bad "the payload is missing $required"
        Write-Host '      The package registers a COM class whose Path points at the adapter. Registering'
        Write-Host '      against a payload that does not contain it produces a menu entry that activates'
        Write-Host '      nothing.'
        exit 1
    }
}
Write-Ok "external location: $PayloadPath"

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null
Copy-Item $manifestPath (Join-Path $StageDir 'AppxManifest.xml')
Copy-Item (Join-Path $SparseDir 'Assets') $StageDir -Recurse
Write-Ok "package layout   : $StageDir"

# =================================================================================================
Write-Section '4. PACK'
# =================================================================================================
$makeappx = Find-SdkTool -ToolName 'makeappx.exe'
$signtool = Find-SdkTool -ToolName 'signtool.exe'
if ($null -eq $makeappx -or $null -eq $signtool) {
    Write-Bad 'makeappx.exe / signtool.exe not found under the Windows SDK.'
    exit 1
}
Write-Ok "sdk tools: $(Split-Path -Parent $makeappx)"

New-Item -ItemType Directory -Path (Split-Path -Parent $MsixPath) -Force | Out-Null
& $makeappx pack /d $StageDir /p $MsixPath /nv /o
if ($LASTEXITCODE -ne 0) { Write-Bad "makeappx failed ($LASTEXITCODE)"; exit 1 }
Write-Ok "packed: $MsixPath"

# =================================================================================================
Write-Section '5. SIGN'
# =================================================================================================
# NO /sm, ON PURPOSE. Without it signtool searches CurrentUser\My - which is the store
# Get-SigningCertificate opened, and the store dev-certificate.ps1 creates the key in. All three
# name the same place or none of them work; SigningStoreTests asserts they do.
#
# /sm would search LocalMachine\My instead, and the private key of a machine certificate is
# readable only by Administrators - so it would also make signing require elevation, and section 7
# registers the package for a user, not for an administrator.
& $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $MsixPath
if ($LASTEXITCODE -ne 0) {
    Write-Bad "signtool failed ($LASTEXITCODE)"
    Write-Host '      "No certificates were found that met all the given criteria" means signtool'
    Write-Host "      did not find $($cert.Thumbprint) in CurrentUser\My for user $env:USERNAME."
    Write-Host '      Check with:  .\scripts\dev-certificate.ps1 -Report'
    exit 1
}
Write-Ok 'signed'

# =================================================================================================
Write-Section '6. REGISTER'
# =================================================================================================
Add-AppxPackage -Path $MsixPath -ExternalLocation $PayloadPath
Write-Ok 'registered for this user (unelevated, -ExternalLocation)'

Write-Section 'NEXT'
Write-Host '  Explorer caches context-menu handlers. Restart it, then right-click a file:'
Write-Host '    Stop-Process -Name explorer -Force'
Write-Host ''
Write-Host '  Then run the gate:'
Write-Host '    .\scripts\modern-menu-gate.ps1'
Write-Host ''
Write-Host '  To remove:'
Write-Host '    .\scripts\modern-menu-register.ps1 -Remove'
Write-Host '    .\scripts\dev-certificate.ps1 -Remove        # elevated'
exit 0
