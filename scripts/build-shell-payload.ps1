#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - builds the B1 payload: the external location the sparse package points at.

.DESCRIPTION
    Produces the directory modern-menu-register.ps1 expects, containing at minimum:

        NOBA.Archive.ShellAdapter.dll    native, MSVC, x64 - the IExplorerCommand server
        NOBA.Archive.ShellBridge.dll     managed, net10.0  - the real ShellMenuBuilder behind it

    WHY BOTH, AND WHY BESIDE EACH OTHER. ClrHost.cpp resolves the bridge assembly and its
    runtimeconfig relative to the ADAPTER'S OWN module directory. "Beside" is literal: a payload with
    the adapter and no bridge draws no menu at all, and the failure has nothing in any log.

    THIS SCRIPT BUILDS AND VERIFIES. IT DOES NOT DEPLOY.
      - it does not pack, sign or register anything
      - it does not create, trust or remove a certificate
      - it does not read or write a policy, Developer Mode or any registry value
      - it does not run the gate
      - it needs no elevation, and refuses none

    DETERMINISM, HONESTLY. The native project passes /Brepro so no timestamp reaches the object
    files or the PE header, and the managed build is already Deterministic with
    ContinuousIntegrationBuild set here. Two builds of the same checkout on the same machine should
    therefore produce the same bytes. Byte-for-byte equality with a DIFFERENT machine is NOT claimed:
    absolute source paths still differ, and no source-path map is configured. The script reports
    SHA256 so that is checkable rather than asserted.

    NO NUGET. -p:NobaOfflineBuild=true swaps the Microsoft.Extensions.* packages for the shared
    framework, which is what makes this buildable on a VM with the four verified components and
    nothing else.

.PARAMETER PayloadPath
    Where to put the payload. Defaults to artifacts\app, which is what modern-menu-register.ps1
    defaults to. Changing one without the other is the mistake this default exists to prevent.

.PARAMETER Configuration
    Release or Debug. Release by default.

.PARAMETER Clean
    Delete the payload directory first. Without this, a stale file from an earlier build survives and
    the hashes describe a directory nobody produced in one piece.

.EXAMPLE
    .\scripts\build-shell-payload.ps1

.EXAMPLE
    .\scripts\build-shell-payload.ps1 -Clean
#>
[CmdletBinding()]
param(
    [string]$PayloadPath,
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path

# The payload contract. These names are asserted against NOBA.Archive.Core's constants by
# ShellPayloadContractTests, so this list and the register script's cannot drift apart.
$AdapterDll = 'NOBA.Archive.ShellAdapter.dll'
$BridgeDll = 'NOBA.Archive.ShellBridge.dll'
$CommandLineHost = 'noba-ar.exe'

$AdapterProject = Join-Path $repoRoot 'src\NOBA.Archive.ShellAdapter\NOBA.Archive.ShellAdapter.vcxproj'
$BridgeProject = Join-Path $repoRoot 'src\NOBA.Archive.ShellBridge\NOBA.Archive.ShellBridge.csproj'
$CliProject = Join-Path $repoRoot 'src\NOBA.Archive.Cli\NOBA.Archive.Cli.csproj'

# The toolchain this payload was designed against (DROP08_PHASE5_VM_TOOLCHAIN.md). A difference is
# reported, never fatal: a patch bump is not a reason to refuse to build, but it IS a variable in
# anything measured afterwards, and it belongs in the transcript rather than in somebody's memory.
$VerifiedMsvc = '14.44.35207'
$VerifiedSdk = '10.0.26100.0'

function Write-Section([string]$t) { Write-Host ''; Write-Host ('=' * 78); Write-Host $t; Write-Host ('=' * 78) }
function Write-Ok  ([string]$m) { Write-Host "ok    $m" -ForegroundColor Green }
function Write-Bad ([string]$m) { Write-Host "FAIL  $m" -ForegroundColor Red }
function Write-Note([string]$m) { Write-Host "warn  $m" -ForegroundColor Yellow }

function Import-MsvcEnvironment {
    # vswhere, not PATH. A plain PowerShell prompt is not a developer prompt, and cl.exe will not be
    # there. The -requires string is the same component id the toolchain verification asked for.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        Write-Bad 'vswhere.exe not found. Visual Studio Build Tools with the C++ workload are required.'
        return $false
    }

    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($vsPath) { $vsPath = @($vsPath)[0] }
    if (-not $vsPath) {
        Write-Bad 'no install satisfies -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
        return $false
    }

    $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path $vcvars)) {
        Write-Bad "vcvars64.bat not found at $vcvars"
        return $false
    }

    $dump = & cmd.exe /c "`"$vcvars`" >nul 2>&1 && set"
    if ($LASTEXITCODE -ne 0) {
        Write-Bad "vcvars64.bat failed (exit $LASTEXITCODE)"
        return $false
    }
    foreach ($line in $dump) {
        $i = $line.IndexOf('=')
        if ($i -gt 0) { Set-Item -Path ("Env:" + $line.Substring(0, $i)) -Value $line.Substring($i + 1) -ErrorAction SilentlyContinue }
    }

    if (-not (Get-Command msbuild.exe -ErrorAction SilentlyContinue)) {
        Write-Bad 'msbuild.exe is not on PATH after importing vcvars64.bat.'
        return $false
    }
    return $true
}

# =================================================================================================
Write-Section 'DROP 08 Phase 5 - B1 payload build'
# =================================================================================================
Write-Host "repo          : $repoRoot"
Write-Host "configuration : $Configuration"
Write-Host ''
Write-Host 'This script builds and verifies. It registers nothing, signs nothing, creates no'
Write-Host 'certificate, writes no registry value, and does not run the gate.'

if (-not $PayloadPath) { $PayloadPath = Join-Path $repoRoot 'artifacts\app' }
Write-Host "payload       : $PayloadPath"

# =================================================================================================
Write-Section '1. TOOLCHAIN'
# =================================================================================================
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Bad 'dotnet is not on PATH.'; exit 1 }
$sdkVersion = (& dotnet --version | Select-Object -First 1)
Write-Ok ".NET SDK $sdkVersion"

if (-not (Import-MsvcEnvironment)) { exit 1 }

$toolset = $env:VCToolsVersion
$winsdk = if ($env:WindowsSDKVersion) { $env:WindowsSDKVersion.TrimEnd('\') } else { '(unset)' }
Write-Ok "MSVC toolset $toolset"
Write-Ok "Windows SDK  $winsdk"

if ($toolset -ne $VerifiedMsvc) { Write-Note "MSVC differs from the verified toolchain ($VerifiedMsvc). Recorded, not fatal." }
if ($winsdk -ne $VerifiedSdk) { Write-Note "Windows SDK differs from the verified toolchain ($VerifiedSdk). Recorded, not fatal." }

# =================================================================================================
Write-Section '2. PAYLOAD DIRECTORY'
# =================================================================================================
if ($Clean -and (Test-Path $PayloadPath)) {
    Remove-Item $PayloadPath -Recurse -Force
    Write-Ok 'cleaned'
} elseif (Test-Path $PayloadPath) {
    Write-Note 'the payload directory already exists; a stale file from an earlier build will survive.'
    Write-Host '      Use -Clean when the hashes need to describe a directory produced in one piece.'
}
New-Item -ItemType Directory -Path $PayloadPath -Force | Out-Null
Write-Ok $PayloadPath

# =================================================================================================
Write-Section '3. MANAGED - ShellBridge'
# =================================================================================================
# publish, not build: the adapter needs the bridge's whole closure beside it - the referenced
# assemblies, the deps.json and the runtimeconfig.json hostfxr is handed by name.
& dotnet publish $BridgeProject -c $Configuration -o $PayloadPath `
    -p:NobaOfflineBuild=true -p:ContinuousIntegrationBuild=true --nologo
if ($LASTEXITCODE -ne 0) { Write-Bad "publishing the bridge failed ($LASTEXITCODE)"; exit 1 }
Write-Ok "published to $PayloadPath"

# =================================================================================================
Write-Section '4. MANAGED - command-line host'
# =================================================================================================
# Not part of the two required DLLs, and built anyway. NobaSubCommand::Invoke resolves the launch
# plan's executable relative to the adapter's directory, so a payload without noba-ar.exe draws a
# correct menu whose entries then start nothing - which looks like a broken handler and is not.
& dotnet publish $CliProject -c $Configuration -o $PayloadPath `
    -p:NobaOfflineBuild=true -p:ContinuousIntegrationBuild=true --nologo
if ($LASTEXITCODE -ne 0) { Write-Bad "publishing the command-line host failed ($LASTEXITCODE)"; exit 1 }
Write-Ok 'published'

# =================================================================================================
Write-Section '5. NATIVE - ShellAdapter'
# =================================================================================================
& msbuild.exe $AdapterProject /p:Configuration=$Configuration /p:Platform=x64 /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { Write-Bad "building the adapter failed ($LASTEXITCODE)"; exit 1 }

$adapterBuilt = Join-Path (Split-Path -Parent $AdapterProject) "x64\$Configuration\$AdapterDll"
if (-not (Test-Path $adapterBuilt)) {
    Write-Bad "msbuild reported success but $AdapterDll is not at $adapterBuilt"
    exit 1
}
Copy-Item $adapterBuilt (Join-Path $PayloadPath $AdapterDll) -Force
Write-Ok "built and copied: $AdapterDll"

# =================================================================================================
Write-Section '6. VERIFY THE PAYLOAD'
# =================================================================================================
# The two DLLs are the contract. Everything else is reported; these are asserted.
$required = @($AdapterDll, $BridgeDll)
$missing = @()
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $PayloadPath $f))) { $missing += $f }
}
if ($missing.Count -gt 0) {
    Write-Bad ("the payload is missing: " + ($missing -join ', '))
    Write-Host '      modern-menu-register.ps1 refuses a payload without these, and it is right to:'
    Write-Host '      the package registers a COM class whose Path points at the adapter, and the'
    Write-Host '      adapter loads the bridge from its own directory. Either one absent is a menu'
    Write-Host '      entry that activates nothing.'
    exit 1
}
Write-Ok "both required DLLs present"

# The runtimeconfig is what hostfxr is actually handed. Its absence is the same failure as a missing
# bridge, arriving one step later.
$runtimeConfig = Join-Path $PayloadPath 'NOBA.Archive.ShellBridge.runtimeconfig.json'
if (-not (Test-Path $runtimeConfig)) {
    Write-Bad 'NOBA.Archive.ShellBridge.runtimeconfig.json is missing - hostfxr is initialised with it by name.'
    exit 1
}
Write-Ok 'runtimeconfig.json present'

if (Test-Path (Join-Path $PayloadPath $CommandLineHost)) {
    Write-Ok "$CommandLineHost present - the headless verbs can start"
} else {
    Write-Note "$CommandLineHost is NOT in the payload. The menu will draw and its entries will start nothing."
}

# =================================================================================================
Write-Section '7. HASHES'
# =================================================================================================
Write-Host 'SHA256 of the contract files. Compare across builds to see whether the payload changed;'
Write-Host 'they are not claimed to match a build on a different machine (see -DESCRIPTION).'
Write-Host ''
foreach ($f in ($required + @($CommandLineHost))) {
    $path = Join-Path $PayloadPath $f
    if (Test-Path $path) {
        $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash
        Write-Host ("  {0}  {1}" -f $hash, $f)
    } else {
        Write-Host ("  {0}  {1}" -f ('-' * 64), $f)
    }
}

$fileCount = @(Get-ChildItem -Path $PayloadPath -File -Recurse).Count
Write-Host ''
Write-Host ("payload: {0} file(s) in {1}" -f $fileCount, $PayloadPath)

# =================================================================================================
Write-Section 'NEXT'
# =================================================================================================
Write-Host '  Nothing has been registered, signed or trusted. The next step reads two registry values'
Write-Host '  and creates nothing:'
Write-Host ''
Write-Host '    .\scripts\modern-menu-register.ps1 -PreconditionsOnly'
Write-Host ''
Write-Host '  Exit 0 means the package can be registered on this machine; exit 2 means BLOCKED BY'
Write-Host '  ENVIRONMENT, which is not a product failure and not a signing problem.'
exit 0
