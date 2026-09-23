#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 5 - the DEVELOPMENT signing certificate. NOT A SHIPPING CERTIFICATE.

.DESCRIPTION
    Creates a self-signed code-signing certificate subject CN=NOBA-DEV-ONLY and trusts it in
    LocalMachine\TrustedPeople so a locally-built sparse package can be sideloaded.

    TWO STORES, TWO DIFFERENT STATEMENTS. They are not interchangeable and neither one is a
    preference:

        LocalMachine\TrustedPeople   PUBLIC certificate. "this publisher may be sideloaded here."
                                     Machine-wide, and what MSIX deployment reads. A certificate
                                     trusted only in the user's store is refused with 0x800B0109 -
                                     a chain error naming neither the store nor the certificate.
                                     Plan section L.6 recommended the user store for this; that
                                     recommendation was wrong and cost a Windows run (DELTA 10).

        CurrentUser\My               PRIVATE KEY. "this user can sign." signtool without /sm
                                     searches THIS store, so this is where the key has to be.

    THE SECOND ONE WAS ALSO PAID FOR. The key was created in LocalMachine\My, and
    modern-menu-register.ps1 checked LocalMachine\My and reported "signable" - then signtool, which
    had never been told to look there, failed with "No certificates were found that met all the
    given criteria". Two places named the same thing and neither could see the other. The E2 harness
    that passed 3/3 had it right: key in CurrentUser\My, public certificate in the machine store.

    A machine key would not have been enough anyway: its private key is readable only by
    Administrators, so signing would have required elevation - and the package is registered for a
    user, not for an administrator.

    WHY X509Store AND NEVER Remove-Item Cert:. The Cert: provider failed to remove a LocalMachine
    certificate twice - once inside a script and once by hand from an elevated prompt - leaving a
    DEV-ONLY certificate trusted machine-wide. Every store operation here goes through the .NET API,
    and a store that cannot be READ is reported UNKNOWN rather than counted clean.

    THIS IS DEVELOPMENT ONLY. Phase 6 owns procurement of anything that could sign a release. The
    subject is literally CN=NOBA-DEV-ONLY so it cannot be mistaken for one in a certificate store,
    and the banner prints on every run including -Remove.

    TWO ACCOUNTS, TWO RUNS - AND THE SCRIPT ENFORCES WHICH IS WHICH. Creating the key and trusting
    the certificate are not just two stores, they are two IDENTITIES:

        create   the signing user, UNELEVATED.  CurrentUser\My needs no administrator, and the key
                 must belong to whoever will run modern-menu-register.ps1 - which itself refuses to
                 run elevated (plan L.4).
        -Trust   an administrator, ELEVATED.    LocalMachine\TrustedPeople is machine-wide, so it
                 does not matter WHICH administrator does it.

    THIS WAS ALSO PAID FOR. A single elevated run created the key under nob11\Administrator while
    the user who signs is NobaTester. Everything printed ok - the key existed, the trust existed -
    and signtool would still have found nothing, because it searches the CALLING USER's store. A
    script that elevates the whole job silently moves the key to whoever answered the UAC prompt.
    So this one refuses to create a key while elevated, and prints the owning identity either way.

.PARAMETER Trust
    ELEVATED. Import the exported PUBLIC certificate into LocalMachine\TrustedPeople. Creates no key
    and touches no user store.

.PARAMETER Remove
    Untrust and delete the certificate from every store this script writes to. Elevation is needed
    for the machine stores; the user store cleaned is the CALLER'S. Exits 0 when nothing is left;
    non-zero when something could not be removed OR could not be read.

.PARAMETER Report
    Print what is in the stores and change nothing. Needs no elevation.

.PARAMETER CertificatePath
    Where the public .cer is written by the create step and read by -Trust.

.PARAMETER Force
    CREATE step only. Proceed even though a DEV-ONLY key already exists in CurrentUser\My. Without
    it, a second run refuses rather than leaving two signing keys with the same subject and no way to
    tell signtool which one is meant - this is the duplicate-key state running the create step twice
    on a fresh account has produced before. Remove the old key first instead, when possible.

.PARAMETER Thumbprint
    -Trust only, and required. The thumbprint the CREATE step printed. Binds trust to that specific
    key rather than to the subject name alone - a self-signed certificate with subject CN=NOBA-DEV-ONLY
    is trivial for anyone to mint, so a Subject match by itself proves nothing about which key is being
    trusted.

.EXAMPLE
    .\scripts\dev-certificate.ps1 -Report

.EXAMPLE
    # as the user who will sign - NOT elevated
    .\scripts\dev-certificate.ps1

.EXAMPLE
    # elevated, any administrator - <thumbprint> is what the create step printed
    .\scripts\dev-certificate.ps1 -Trust -Thumbprint <thumbprint>

.EXAMPLE
    # elevated for the machine stores; run again as the signing user for theirs
    .\scripts\dev-certificate.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Trust,
    [switch]$Remove,
    [switch]$Report,
    [switch]$Force,
    [string]$Thumbprint,
    [string]$CertificatePath
)

$ErrorActionPreference = 'Stop'

$Subject = 'CN=NOBA-DEV-ONLY'
$FriendlyName = 'NOBA Archive DEV-ONLY package signing'

# The public certificate travels between the two runs as a file, because the two runs are two
# different accounts and share nothing else. PUBLIC ONLY - the private key never leaves the store it
# was created in, and never needs to.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Join-Path $repoRoot 'artifacts\dev-certificate\NOBA-DEV-ONLY.cer'
}

function Get-Identity { return [System.Security.Principal.WindowsIdentity]::GetCurrent().Name }

# Where a certificate with this subject may legitimately be, and where cleanup must look. Ordered by
# consequence: the machine store is the one that grants trust to everyone on the machine, so it is
# cleared first and reported first.
#
# LocalMachine\My is still listed although nothing writes there any more: earlier runs of this
# script put keys in it, and a cleanup that stops looking at a store the moment it stops writing to
# it is how residue outlives the tool that created it.
$Stores = @(
    @{ Name = 'TrustedPeople'; Location = 'LocalMachine'; Why = 'grants trust MACHINE-WIDE' },
    @{ Name = 'My';            Location = 'LocalMachine'; Why = 'private key, machine - LEGACY' },
    @{ Name = 'My';            Location = 'CurrentUser';  Why = 'private key - signtool reads this' }
)

function Write-Banner {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host '  DEV-ONLY - NOT A SHIPPING CERTIFICATE' -ForegroundColor Yellow
    Write-Host '  Self-signed, subject CN=NOBA-DEV-ONLY, for sideloading a locally built package.'
    Write-Host '  Phase 6 owns procurement. Nothing here claims production signing readiness.'
    Write-Host ('=' * 78)
}

function Write-Ok  ([string]$m) { Write-Host "ok    $m" -ForegroundColor Green }
function Write-Bad ([string]$m) { Write-Host "FAIL  $m" -ForegroundColor Red }
function Write-Note([string]$m) { Write-Host "warn  $m" -ForegroundColor Yellow }

function Test-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
.SYNOPSIS
    How many certificates with this subject are in a store. -1 means "could not look".
.DESCRIPTION
    The sentinel is the point. An unreadable store is UNKNOWN, and reporting it as zero is how a
    cleanup announces success over a certificate it never saw.
#>
function Get-CertCount {
    param([string]$StoreName, [string]$Location)
    try {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $Location)
        $store.Open('ReadOnly')
        try { return @($store.Certificates | Where-Object { $_.Subject -eq $Subject }).Count }
        finally { try { $store.Close() } catch { } }
    } catch {
        return -1
    }
}

<#
.SYNOPSIS
    Removes every certificate with this subject from one store. Never throws.
#>
function Remove-Certs {
    param([string]$StoreName, [string]$Location)
    $result = @{ Opened = $false; Removed = 0; Failed = 0; Error = $null }
    try {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $Location)
        $store.Open('ReadWrite')
        $result.Opened = $true
    } catch {
        $result.Error = $_.Exception.Message
        return $result
    }
    try {
        foreach ($c in @($store.Certificates | Where-Object { $_.Subject -eq $Subject })) {
            try { $store.Remove($c); $result.Removed++ }
            catch { $result.Failed++; $result.Error = $_.Exception.Message }
        }
    } finally { try { $store.Close() } catch { } }
    return $result
}

function Show-Stores {
    Write-Host ''
    Write-Host 'CERTIFICATE STORES'
    # The identity is part of the reading, not decoration: "CurrentUser\My" names a DIFFERENT store
    # depending on who ran this, and a transcript that omits the owner cannot be compared with one
    # taken from another account.
    Write-Host ("      running as: {0}{1}" -f (Get-Identity), $(if (Test-Elevated) { ' (ELEVATED)' } else { '' }))
    $unknown = 0
    $present = 0
    foreach ($s in $Stores) {
        $n = Get-CertCount -StoreName $s.Name -Location $s.Location
        $label = "$($s.Location)\$($s.Name)"
        if ($n -lt 0) {
            Write-Note ("{0,-32} UNREADABLE   ({1})" -f $label, $s.Why)
            $unknown++
        } elseif ($n -eq 0) {
            Write-Host ("      {0,-32} none         ({1})" -f $label, $s.Why)
        } else {
            Write-Note ("{0,-32} {1} present  ({2})" -f $label, $n, $s.Why)
            $present += $n
        }
    }
    return @{ Present = $present; Unknown = $unknown }
}

# =================================================================================================
Write-Banner
# =================================================================================================

if ($Report) {
    $state = Show-Stores
    Write-Host ''
    if ($state.Unknown -gt 0) {
        Write-Bad "$($state.Unknown) store(s) could not be read - the state is UNKNOWN, not clean."
        exit 1
    }
    if ($state.Present -gt 0) {
        Write-Note "$($state.Present) DEV-ONLY certificate(s) present. Remove with -Remove (elevated)."
    } else {
        Write-Ok 'no DEV-ONLY certificate in any store this script writes to'
    }
    exit 0
}

if ($Trust -and $Remove) {
    Write-Bad '-Trust and -Remove are opposite operations. Pick one.'
    exit 1
}

# =================================================================================================
if ($Remove) {
# =================================================================================================
    Write-Host ''
    Write-Host 'REMOVING - ordered by consequence, machine-wide trust first.'

    # A guarded sequence: NO STEP MAY PREVENT A LATER ONE. An earlier version of the E2 harness let
    # an unguarded loop throw, escaped its finally, and left a DEV-ONLY certificate trusted
    # machine-wide - the exact residue the cleanup existed to prevent (DELTA section 11.3).
    if (-not (Test-Elevated)) {
        Write-Note 'not elevated: the machine stores can be READ but not written.'
        Write-Host '      A store that is already empty still counts as clean - it can be proven'
        Write-Host '      read-only. One that is not will be reported as residue, not as an error'
        Write-Host '      to work around: rerun elevated.'
    }
    Write-Host ("      user store being cleaned belongs to: {0}" -f (Get-Identity))

    $residue = @()
    foreach ($s in $Stores) {
        $label = "$($s.Location)\$($s.Name)"
        $r = Remove-Certs -StoreName $s.Name -Location $s.Location
        if (-not $r.Opened) {
            # Could not open for WRITING. That is not the same fact as "could not look": fall back to
            # the read-only count, so an already-empty machine store does not fail an unelevated run
            # and a non-empty one is never quietly called clean.
            $stillThere = Get-CertCount -StoreName $s.Name -Location $s.Location
            if ($stillThere -eq 0) {
                Write-Ok "$label : nothing to remove (read-only view)"
                continue
            }
            if ($stillThere -lt 0) {
                Write-Note "$label : could not open or read ($($r.Error)) - state UNKNOWN"
                $residue += "$label UNREADABLE"
                continue
            }
            Write-Note "$label : $stillThere present, could not open to remove ($($r.Error))"
            $residue += "$label $stillThere not removed - rerun elevated"
            continue
        }
        if ($r.Failed -gt 0) {
            Write-Note "$label : removed $($r.Removed), FAILED $($r.Failed) ($($r.Error))"
            $residue += "$label $($r.Failed) not removed"
            continue
        }
        Write-Ok "$label : removed $($r.Removed)"
    }

    Write-Host ''
    $after = Show-Stores
    Write-Host ''
    if ($residue.Count -eq 0 -and $after.Present -eq 0 -and $after.Unknown -eq 0) {
        Write-Ok 'no residue: no DEV-ONLY certificate in any store'
        exit 0
    }
    Write-Bad ('residue or unknown state: ' + (($residue + @("present=$($after.Present)", "unreadable=$($after.Unknown)")) -join '; '))
    exit 1
}

# =================================================================================================
if ($Trust) {
# =================================================================================================
# TRUST - elevated, any administrator, PUBLIC certificate only, no key created or read.
# =================================================================================================
    if (-not (Test-Elevated)) {
        Write-Bad 'this needs Administrator.'
        Write-Host '      LocalMachine\TrustedPeople is a machine-wide trust decision, and Windows will'
        Write-Host '      not let an unelevated process make one.'
        exit 1
    }

    Write-Host ''
    Write-Host 'TRUSTING in LocalMachine\TrustedPeople'
    Write-Host '      MSIX deployment reads this store. A certificate trusted only for the current user'
    Write-Host '      is refused with 0x800B0109, an error naming neither the store nor the certificate.'
    Write-Host "      running as: $(Get-Identity)"
    Write-Host '      Which administrator does this does not matter - the store is machine-wide. The'
    Write-Host '      PRIVATE KEY is a different question and stays with whoever ran the create step.'

    if (-not (Test-Path $CertificatePath)) {
        Write-Bad "no exported certificate at $CertificatePath"
        Write-Host '      Run the create step first, AS THE USER WHO WILL SIGN and NOT elevated:'
        Write-Host '        .\scripts\dev-certificate.ps1'
        exit 1
    }

    # Read it before trusting it. This store is a statement about who may install software on this
    # machine, and a file on disk is not evidence of anything until it has been looked at.
    try {
        $incoming = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    } catch {
        Write-Bad "the file at $CertificatePath is not a readable certificate: $($_.Exception.Message)"
        exit 1
    }

    if ($incoming.Subject -ne $Subject) {
        Write-Bad "refusing: the file is '$($incoming.Subject)', not $Subject."
        exit 1
    }
    if ($incoming.HasPrivateKey) {
        Write-Bad 'refusing: the file carries a PRIVATE KEY. Only the public certificate is trusted here.'
        exit 1
    }
    Write-Ok "read: $($incoming.Subject)  thumbprint $($incoming.Thumbprint)"
    Write-Host "      not after: $($incoming.NotAfter.ToString('yyyy-MM-dd'))"

    # A matching Subject proves nothing: CN=NOBA-DEV-ONLY is a name anyone can self-sign, and this
    # file's path (under artifacts\, inside a checkout) is not a secret. Trust is bound to the
    # THUMBPRINT the create step printed, not to whatever certificate happens to sit at this path.
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        Write-Bad 'refusing: -Thumbprint was not given.'
        Write-Host '      A file at this path is not evidence of which key it is - anyone who can write'
        Write-Host "      to this path can drop a different self-signed $Subject certificate here and"
        Write-Host '      have it trusted machine-wide. Pass the thumbprint the CREATE step printed (or'
        Write-Host '      that -Report on the signing user''s machine shows):'
        Write-Host '        .\scripts\dev-certificate.ps1 -Trust -Thumbprint <thumbprint-from-create-step>'
        exit 1
    }
    $expectedThumbprint = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $incomingThumbprint = ($incoming.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($expectedThumbprint -ne $incomingThumbprint) {
        Write-Bad "refusing: the file's thumbprint is $($incoming.Thumbprint), not the requested $Thumbprint."
        Write-Host '      This is exactly the check that stops a substituted certificate - confirm the'
        Write-Host '      thumbprint with whoever ran the create step before trusting a different key.'
        exit 1
    }
    Write-Ok "thumbprint matches: trusting $($incoming.Thumbprint) and no other key"

    $trusted = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
    $trusted.Open('ReadWrite')
    try { $trusted.Add($incoming) } finally { try { $trusted.Close() } catch { } }

    # Verified by reading it back, and by THUMBPRINT rather than by count: a count of one proves a
    # certificate with this subject is trusted, not that it is the one that will sign.
    $check = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
    $check.Open('ReadOnly')
    try {
        $present = @($check.Certificates | Where-Object { $_.Thumbprint -eq $incoming.Thumbprint })
    } finally { try { $check.Close() } catch { } }

    if ($present.Count -lt 1) {
        Write-Bad "trust NOT verified: $($incoming.Thumbprint) is not in LocalMachine\TrustedPeople."
        exit 1
    }
    Write-Ok "trust verified: $($incoming.Thumbprint) in LocalMachine\TrustedPeople"

    Write-Host ''
    Write-Host 'NEXT - as the signing user, NOT elevated:'
    Write-Host '  .\scripts\dev-certificate.ps1 -Report'
    Write-Host '  .\scripts\modern-menu-register.ps1'
    Write-Host ''
    Write-Host 'WHEN FINISHED, REMOVE IT:'
    Write-Host '  .\scripts\dev-certificate.ps1 -Remove        # elevated, then again as the signing user'
    Write-Host ''
    exit 0
}

# =================================================================================================
# CREATE - the signing user, NOT elevated. Creates a key and exports the public certificate.
# =================================================================================================
if (Test-Elevated) {
    Write-Bad 'this must NOT run elevated.'
    Write-Host '      The key would be created in the store of whoever answered the UAC prompt. That'
    Write-Host "      happened once already: the key landed under an administrator while the user who"
    Write-Host '      signs was somebody else, and signtool - which searches the CALLING user - would'
    Write-Host '      have found nothing. CurrentUser\My needs no administrator.'
    Write-Host ''
    Write-Host '      Run this as the user who will run modern-menu-register.ps1, then trust it:'
    Write-Host '        .\scripts\dev-certificate.ps1                # signing user, not elevated'
    Write-Host '        .\scripts\dev-certificate.ps1 -Trust         # elevated'
    exit 1
}

$before = Show-Stores
if ($before.Unknown -gt 0) {
    Write-Host ''
    Write-Bad 'a store could not be read. Refusing to add a certificate to a state that cannot be described.'
    exit 1
}

# A second key with the same subject in the same store is not a redundant copy - signtool and
# -Trust both search by subject/thumbprint, and a caller who does not pass the right thumbprint can
# end up signing with, or trusting, whichever one the store happens to return first. This is the
# duplicate-key state a repeated create on the same account has produced before.
$existingKeys = Get-CertCount -StoreName 'My' -Location 'CurrentUser'
if ($existingKeys -gt 0 -and -not $Force) {
    Write-Host ''
    Write-Bad "$existingKeys DEV-ONLY key(s) already in CurrentUser\My for $(Get-Identity)."
    Write-Host '      Creating another leaves two signing keys with the same subject and no reliable way'
    Write-Host '      to tell which one signtool or -Trust will pick. Remove the old one first:'
    Write-Host '        .\scripts\dev-certificate.ps1 -Remove        # elevated, then again as this user'
    Write-Host '      or pass -Force to add another key deliberately (not recommended).'
    exit 1
}
if ($existingKeys -gt 0 -and $Force) {
    Write-Note "$existingKeys existing DEV-ONLY key(s) in CurrentUser\My - -Force given, creating another anyway."
}

Write-Host ''
Write-Host 'CREATING'

# CodeSigningCert with a 3-year life. Longer than a release cycle is not a convenience here: a
# DEV-ONLY certificate that outlives the developer's memory of installing it is the thing that ends
# up trusted on a machine nobody is watching.
#
# CurrentUser\My: the store signtool searches without /sm. See the two-store note in .DESCRIPTION.
# Elevation does not move this - an elevated process of the same account has the same SID and the
# same user store - but running the script as a DIFFERENT administrator does, which is why the
# owning identity is printed and read back below rather than assumed.
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -FriendlyName $FriendlyName `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyUsage DigitalSignature `
    -KeyLength 2048 `
    -NotAfter (Get-Date).AddYears(3)

Write-Ok "created: $($cert.Subject)  thumbprint $($cert.Thumbprint)"
Write-Host "      not after: $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "      key owner: $([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)"
Write-Host '      Sign as THIS user. A key in another account''s store is invisible to signtool.'

# Read back from the store signtool will search, through the same API it will use. "New-Self-
# SignedCertificate returned an object" is not the fact that matters; "there is a usable key in
# CurrentUser\My" is, and the previous version of this script never established it.
$signable = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
$signable.Open('ReadOnly')
try {
    $found = @($signable.Certificates | Where-Object {
        $_.Thumbprint -eq $cert.Thumbprint -and $_.HasPrivateKey })
} finally { try { $signable.Close() } catch { } }

if ($found.Count -ne 1) {
    Write-Bad "the certificate is NOT readable with a private key from CurrentUser\My (found $($found.Count))."
    Write-Host '      signtool would fail with "No certificates were found that met all the given'
    Write-Host '      criteria". Remove with -Remove and investigate before trusting anything.'
    exit 1
}
Write-Ok 'private key verified in CurrentUser\My - the store signtool searches'

Write-Host ''
Write-Host 'EXPORTING the public certificate'
Write-Host '      The trust step runs as an administrator, which is a different account with a'
Write-Host '      different user store. A file is the only thing the two runs share.'

# PUBLIC ONLY - Export(Cert) writes the certificate without the private key, and the private key has
# no reason to leave the store it was born in. A .pfx here would be a signing key on disk, which is
# exactly what a DEV-ONLY certificate should never leave behind.
$public = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)
$bytes = $public.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
New-Item -ItemType Directory -Path (Split-Path -Parent $CertificatePath) -Force | Out-Null
[System.IO.File]::WriteAllBytes($CertificatePath, $bytes)

# Read back, and check the thumbprint rather than the file's existence: a file that exists and
# describes a different certificate is worse than no file at all.
$exported = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
if ($exported.Thumbprint -ne $cert.Thumbprint) {
    Write-Bad "the exported file is $($exported.Thumbprint), not $($cert.Thumbprint)."
    exit 1
}
if ($exported.HasPrivateKey) {
    Write-Bad 'the exported file carries a PRIVATE KEY. Delete it and investigate before trusting it.'
    exit 1
}
Write-Ok "exported (public only): $CertificatePath"

Write-Host ''
Write-Host 'NOT TRUSTED YET - and that is a separate decision, made by an administrator.'
Write-Host '      Nothing on this machine trusts this certificate until -Trust runs. Add-AppxPackage'
Write-Host '      would fail 0x800B0109.'
Write-Host ''
Write-Host 'NEXT - in an ELEVATED window (any administrator):'
Write-Host "  .\scripts\dev-certificate.ps1 -Trust -Thumbprint $($cert.Thumbprint)"
Write-Host ''
Write-Host "  then back here, as $(Get-Identity), NOT elevated:"
Write-Host '  .\scripts\modern-menu-register.ps1'
Write-Host ''
Write-Host 'WHEN FINISHED, REMOVE IT:'
Write-Host '  .\scripts\dev-certificate.ps1 -Remove        # elevated, then again as this user'
Write-Host ''
exit 0
