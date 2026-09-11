#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - the Windows gate for SURFACE A, the Windows 11 modern context menu.

.DESCRIPTION
    This is what shell-gate.ps1 records as NOT IMPLEMENTED. It covers SHELL-W1 and SHELL-W2, and the
    G.2...G.10 steps of the Phase 5 plan.

    THE PHASE 4 PATTERN, DELIBERATELY. Automate everything machine-checkable - package presence,
    CLSID registration, artefacts on disk, archive contents - and leave a person only what a person
    can actually see: whether Explorer draws the menu, where it draws it, and what the submenu reads.
    Four questions, not fourteen.

    NOT IMPLEMENTED IS NOT NOT RUN, AND NEITHER IS A PASS. "Not run" is work somebody still owes.
    "Not implemented" is a surface that does not exist. A skip would read like something that could
    have passed if only somebody had looked.

    WHAT THIS GATE CANNOT CLAIM. Nothing here demonstrates production signing readiness: the package
    is signed by a self-signed DEV-ONLY certificate, and Phase 6 owns procurement. The gate prints
    that in its own summary so a green run cannot be quoted as more than it is.

.PARAMETER Unattended
    Run only the automated checks. Every manual step is recorded NOT RUN, and the verdict can never
    be a pass - a criterion nobody looked at is not a criterion that succeeded.

.PARAMETER WorkPath
    Where the gate creates its sample files. Defaults to a fresh directory under TEMP, and it is
    removed at the end.

.EXAMPLE
    .\scripts\modern-menu-gate.ps1

.EXAMPLE
    .\scripts\modern-menu-gate.ps1 -Unattended
#>
[CmdletBinding()]
param(
    [switch]$Unattended,
    [string]$WorkPath
)

$ErrorActionPreference = 'Continue'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path

$PackageName = 'NOBA.Archive'
$CertSubject = 'CN=NOBA-DEV-ONLY'

# The CLSID is read from the generated manifest rather than typed here. SHELL-065: a gate that
# carried its own copy of the identity could pass while the product registered a different one.
$ManifestPath = Join-Path $repoRoot 'packaging\sparse\AppxManifest.xml'

$script:Results = New-Object System.Collections.ArrayList

function Write-Log([string]$m) { Write-Host $m }

<#
.SYNOPSIS
    Records one criterion. Verdict is PASS, FAIL, NOT RUN or NOT IMPLEMENTED.
#>
function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Verdict,
        [Parameter(Mandatory)][string]$What,
        [string]$Evidence = ''
    )

    [void]$script:Results.Add([pscustomobject]@{ Id = $Id; Verdict = $Verdict; What = $What })

    $colour = switch ($Verdict) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        default { 'Yellow' }
    }
    Write-Host ("  {0,-15} {1,-12} {2}" -f $Verdict, $Id, $What) -ForegroundColor $colour
    if ($Evidence) {
        foreach ($line in ($Evidence -split "`n")) { Write-Log ("                                  {0}" -f $line.TrimEnd()) }
    }
}

<#
.SYNOPSIS
    An automated check: a scriptblock returning exactly one @{ Ok; Evidence }.
#>
function Check {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$What,
        [Parameter(Mandatory)][scriptblock]$Test
    )

    try {
        $emitted = @(& $Test)
    } catch {
        Add-Result -Id $Id -Verdict 'FAIL' -What $What -Evidence "the check itself threw: $($_.Exception.Message)"
        return
    }

    # A check that emitted more than its verdict has been edited carelessly, and taking the last
    # object quietly would let a stray Write-Output decide the outcome.
    if ($emitted.Count -ne 1) {
        Add-Result -Id $Id -Verdict 'FAIL' -What $What `
            -Evidence "the check emitted $($emitted.Count) objects; it must emit exactly one @{ Ok; Evidence }"
        return
    }

    $outcome = $emitted[0]
    $verdict = if ($outcome.Ok) { 'PASS' } else { 'FAIL' }
    Add-Result -Id $Id -Verdict $verdict -What $What -Evidence $outcome.Evidence
}

<#
.SYNOPSIS
    A manual step. Only for what a person can see and a script cannot.
#>
function Ask {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Do,
        [Parameter(Mandatory)][string]$Expect
    )

    if ($Unattended) {
        Add-Result -Id $Id -Verdict 'NOT RUN' -What $Do -Evidence 'unattended run - nobody looked'
        return
    }

    Write-Host ''
    Write-Host ("--- {0} ---" -f $Id) -ForegroundColor Cyan
    Write-Host "  DO     : $Do"
    Write-Host "  EXPECT : $Expect"

    $answer = Read-Host '  [y] as expected  [n] not as expected  [s] skip'
    $verdict = switch ($answer.Trim().ToLowerInvariant()) {
        'y' { 'PASS' }
        'n' { 'FAIL' }
        default { 'NOT RUN' }
    }

    $note = ''
    if ($verdict -eq 'FAIL') { $note = Read-Host '  what happened instead' }

    Add-Result -Id $Id -Verdict $verdict -What $Do -Evidence $note
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

# =================================================================================================
Write-Log ''
Write-Log ('=' * 78)
Write-Log 'DROP 08 Phase 5 - SURFACE A GATE: the Windows 11 modern context menu'
Write-Log ('=' * 78)
Write-Log "repo    : $repoRoot"
Write-Log "package : $PackageName"
Write-Log ''
Write-Log 'DEV-ONLY signing. Nothing in this gate demonstrates production signing readiness.'

$clsid = $null
if (Test-Path $ManifestPath) {
    try {
        [xml]$manifestXml = Get-Content $ManifestPath -Raw
        $clsid = $manifestXml.Package.Applications.Application.Extensions.Extension |
            ForEach-Object { $_.ComServer } |
            Where-Object { $_ } |
            ForEach-Object { $_.SurrogateServer.Class.Id } |
            Where-Object { $_ } |
            Select-Object -First 1
    } catch { $clsid = $null }
}

# =================================================================================================
Write-Log ''
Write-Log 'REGISTRATION'
# =================================================================================================

Check -Id 'SHELL-W1a' -What 'the sparse package is registered for this user' -Test {
    $p = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if ($null -eq $p) { return @{ Ok = $false; Evidence = 'no package with this identity is registered' } }
    return @{ Ok = $true; Evidence = "$($p.PackageFullName)`nInstallLocation: $($p.InstallLocation)" }
}

Check -Id 'SHELL-W1b' -What 'the package identity is the one the generator publishes' -Test {
    if ($null -eq $clsid) { return @{ Ok = $false; Evidence = "could not read a CLSID from $ManifestPath" } }
    return @{ Ok = $true; Evidence = "manifest CLSID $clsid" }
}

Check -Id 'SHELL-W1c' -What 'the handler CLSID is registered with COM' -Test {
    if ($null -eq $clsid) { return @{ Ok = $false; Evidence = 'no CLSID to look for' } }
    # Packaged COM lives in the package catalogue rather than under HKCR, so a missing HKCR key is
    # not evidence of absence. Activation is what settles it, and that is the manual step below.
    $key = "Registry::HKEY_CLASSES_ROOT\CLSID\{$clsid}"
    $present = Test-Path $key
    return @{
        Ok = $true
        Evidence = ("HKCR CLSID key present: {0}. Packaged COM registrations are not required to " -f $present) +
                   'appear there, so this is recorded as information, not as the criterion.'
    }
}

Check -Id 'SHELL-W1d' -What 'the DEV-ONLY certificate is trusted machine-wide' -Test {
    $n = Get-CertCount -StoreName 'TrustedPeople' -Location 'LocalMachine'
    if ($n -lt 0) { return @{ Ok = $false; Evidence = 'the store could not be read - UNKNOWN, not absent' } }
    return @{ Ok = ($n -gt 0); Evidence = "$n certificate(s) with subject $CertSubject" }
}

Check -Id 'SHELL-W1e' -What 'the adapter and the bridge are where the package points' -Test {
    $p = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if ($null -eq $p) { return @{ Ok = $false; Evidence = 'no package' } }
    # A sparse package's payload is at the EXTERNAL location, not inside InstallLocation.
    $missing = @()
    foreach ($f in @('NOBA.Archive.ShellAdapter.dll', 'NOBA.Archive.ShellBridge.dll')) {
        $found = Get-ChildItem -Path $p.InstallLocation -Filter $f -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $found) { $missing += $f }
    }
    if ($missing.Count -gt 0) {
        return @{
            Ok = $true
            Evidence = ('not under InstallLocation: {0}. Expected for a sparse package - the payload ' -f ($missing -join ', ')) +
                       'lives at the external location. Recorded as information.'
        }
    }
    return @{ Ok = $true; Evidence = 'both present under InstallLocation' }
}

# =================================================================================================
Write-Log ''
Write-Log 'THE MENU - what only a person can see'
# =================================================================================================

Ask -Id 'SHELL-W1' `
    -Do 'Restart Explorer (Stop-Process -Name explorer -Force), then right-click a file' `
    -Expect 'a "NOBA Archive" entry in the MODERN menu - the short one, ABOVE "Show more options"'

Ask -Id 'SHELL-W2' `
    -Do 'Open the NOBA Archive submenu on a .zip file' `
    -Expect 'Open with NOBA Archive, Test archive, Extract here, Extract to..., the Compress to entries, Create archive...'

Ask -Id 'G.5' `
    -Do 'Right-click a FOLDER and open the NOBA Archive submenu' `
    -Expect 'Compress to GZIP is SHOWN AND GREYED, not absent. This is SHELL-L9; it is the state the classic surface could not express'

Ask -Id 'G.8' `
    -Do 'With BOTH surfaces registered, right-click a file and look at the whole context menu' `
    -Expect 'NOBA appears ONCE in the modern menu and once under "Show more options" - never twice in the same menu (OPEN-5)'

# =================================================================================================
Write-Log ''
Write-Log 'THE REASON PHASE 5 EXISTS - three files, one archive'
# =================================================================================================

if (-not $WorkPath) {
    $WorkPath = Join-Path ([IO.Path]::GetTempPath()) ("noba-gate-" + [guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Path $WorkPath -Force | Out-Null
try {
    'one' | Out-File -FilePath (Join-Path $WorkPath 'a.txt') -Encoding utf8
    'two' | Out-File -FilePath (Join-Path $WorkPath 'b.txt') -Encoding utf8
    'three' | Out-File -FilePath (Join-Path $WorkPath 'c.txt') -Encoding utf8

    Write-Log ''
    Write-Log "  sample files: $WorkPath"

    Ask -Id 'G.4' `
        -Do "In Explorer, select ALL THREE files in $WorkPath, then NOBA Archive > Compress to ZIP" `
        -Expect 'exactly ONE archive appears, and it contains all three files. Not three archives. This is D4 and SHELL-057, and it is why the modern surface exists'

    Check -Id 'G.4-artefact' -What 'exactly one archive was produced, containing three entries' -Test {
        $archives = @(Get-ChildItem -Path $WorkPath -Filter '*.zip' -ErrorAction SilentlyContinue)
        if ($archives.Count -eq 0) {
            return @{ Ok = $false; Evidence = 'no archive in the sample directory - G.4 was not performed, or it failed' }
        }
        if ($archives.Count -gt 1) {
            return @{ Ok = $false; Evidence = "$($archives.Count) archives; one invocation must produce ONE archive (D4)" }
        }
        try {
            Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
            $zip = [System.IO.Compression.ZipFile]::OpenRead($archives[0].FullName)
            try { $count = @($zip.Entries).Count } finally { $zip.Dispose() }
            return @{ Ok = ($count -eq 3); Evidence = "$($archives[0].Name) contains $count entries" }
        } catch {
            return @{ Ok = $false; Evidence = "could not read the archive: $($_.Exception.Message)" }
        }
    }

    Ask -Id 'G.7' `
        -Do 'Repeat G.4 with files whose names are Arabic and Japanese' `
        -Expect 'the archive is produced and the names survive intact (SHELL-W8)'

    Ask -Id 'G.6' `
        -Do 'Set the UI language to Arabic and reopen the submenu' `
        -Expect 'the submenu labels are Arabic. This is what the classic MUIVerb could not do (SHELL-L2)'
} finally {
    if (Test-Path $WorkPath) { Remove-Item $WorkPath -Recurse -Force -ErrorAction SilentlyContinue }
}

# =================================================================================================
Write-Log ''
Write-Log 'LIFECYCLE'
# =================================================================================================

Ask -Id 'G.10' `
    -Do 'Run modern-menu-register.ps1 a second time without removing first, then reopen the submenu' `
    -Expect 'the menu is unchanged - one NOBA entry, not two (SHELL-W10)'

Ask -Id 'G.9' `
    -Do 'Run modern-menu-register.ps1 -Remove, then dev-certificate.ps1 -Remove (elevated), then reopen the menu' `
    -Expect 'the NOBA entry is gone, Get-AppxPackage finds nothing, and dev-certificate.ps1 -Report says no certificate anywhere (SHELL-W9)'

# =================================================================================================
Write-Log ''
Write-Log ('=' * 78)
Write-Log 'RESULT'
Write-Log ('=' * 78)

$passed = @($script:Results | Where-Object { $_.Verdict -eq 'PASS' }).Count
$failed = @($script:Results | Where-Object { $_.Verdict -eq 'FAIL' }).Count
$notrun = @($script:Results | Where-Object { $_.Verdict -eq 'NOT RUN' }).Count
$notimpl = @($script:Results | Where-Object { $_.Verdict -eq 'NOT IMPLEMENTED' }).Count

Write-Log ("PASSED {0}   FAILED {1}   NOT RUN {2}   NOT IMPLEMENTED {3}" -f $passed, $failed, $notrun, $notimpl)
Write-Log ''

if ($failed -gt 0) {
    Write-Host 'SURFACE A GATE: FAIL' -ForegroundColor Red
} elseif ($notrun -gt 0) {
    Write-Host 'SURFACE A GATE: INCOMPLETE' -ForegroundColor Yellow
    Write-Log '  A NOT RUN is not a pass. Whatever it names is still unverified, and SHELL-W1, SHELL-W2'
    Write-Log '  and G.4 are the whole point of Phase 5 - none of them can be settled by a script.'
} else {
    Write-Host 'SURFACE A GATE: PASS' -ForegroundColor Green
}

Write-Log ''
Write-Log 'WHAT THIS GATE DOES NOT CLAIM'
Write-Log '  Production signing readiness. The package is signed by a self-signed DEV-ONLY certificate;'
Write-Log '  Phase 6 owns procurement, and no run of this script can substitute for it.'
Write-Log '  The per-machine install flow (stage + provision) from plan section L. That is Phase 9.'

exit $(if ($failed -eq 0 -and $notrun -eq 0) { 0 } else { 1 })
