<#
    DROP 08 Phase 5 - GATE 2 / E2 PRE-FLIGHT. HARNESS ONLY.

    Every assertion here is derived from what the harness ACTUALLY DOES, not from recollection of the
    schema. The rule that produced this module: the script deploys with -ExternalLocation, therefore
    the pre-flight must prove the manifest enables external content. Three Windows runs were lost to
    checks that covered what I had already thought about instead of what the operation required.

    It is a module rather than inline script so that every check can be executed on any platform, with
    negative controls, before it is ever run on Windows. test-e2-preflight.ps1 does exactly that.

    SOURCES for each requirement are recorded in docs/DROP08_PHASE5_E2_AUDIT.md, matrix A.
#>

Set-StrictMode -Version Latest

$script:NS = @{
    f       = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
    uap     = 'http://schemas.microsoft.com/appx/manifest/uap/windows10'
    uap10   = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
    rescap  = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities'
    com     = 'http://schemas.microsoft.com/appx/manifest/com/windows10'
    desktop4= 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/4'
    desktop5= 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/5'
}

function New-Finding {
    param([string]$Id, [string]$Requirement, [ValidateSet('PASS','FAIL','WARN')][string]$Status, [string]$Detail)
    [pscustomobject]@{ Id = $Id; Requirement = $Requirement; Status = $Status; Detail = $Detail }
}

<#
.SYNOPSIS
    Validates the E2 sparse-package manifest against every requirement the harness's own operations
    impose. Returns one finding per requirement; the caller decides what to do with them.
.PARAMETER Path
    The AppxManifest.xml to check.
.PARAMETER ExpectedPublisher
    The certificate subject the package will be signed with. Publisher must match it exactly.
.PARAMETER ExpectedClsid
    The CLSID the stub DLL implements, without braces. The COM class and every verb must use it.
.PARAMETER ExternalLocation
    Where the payload lives. Files the manifest references by name must exist there, because
    -ExternalLocation is how they are resolved.
#>
function Test-E2Manifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedPublisher,
        [Parameter(Mandatory)][string]$ExpectedClsid,
        [string]$ExternalLocation,
        [string]$PackageRoot
    )

    $findings = @()

    if (-not (Test-Path $Path)) {
        return ,@(New-Finding 'M00' 'manifest exists' 'FAIL' "not found: $Path")
    }

    try { [xml]$m = Get-Content -Path $Path -Raw -ErrorAction Stop }
    catch { return ,@(New-Finding 'M00' 'manifest is well-formed XML' 'FAIL' $_.Exception.Message) }

    $nsm = New-Object System.Xml.XmlNamespaceManager($m.NameTable)
    foreach ($k in $script:NS.Keys) { $nsm.AddNamespace($k, $script:NS[$k]) }

    # The comma operator matters: returning an XmlNodeList from a PowerShell function ENUMERATES it,
    # so an empty result becomes $null and a single result becomes a bare element - and .Count then
    # throws under StrictMode. Wrapping keeps every result an array, which is what the callers assume.
    function Sel([string]$xpath) { ,@($m.SelectNodes($xpath, $nsm)) }
    function One([string]$xpath) { $m.SelectSingleNode($xpath, $nsm) }

    # --- M01 root -------------------------------------------------------------------------------
    $findings += if ($m.DocumentElement.NamespaceURI -eq $script:NS.f -and $m.DocumentElement.LocalName -eq 'Package') {
        New-Finding 'M01' 'root is foundation Package' 'PASS' $m.DocumentElement.NamespaceURI
    } else {
        New-Finding 'M01' 'root is foundation Package' 'FAIL' "got {$($m.DocumentElement.NamespaceURI)}$($m.DocumentElement.LocalName)"
    }

    # --- M02 declared namespaces vs used prefixes ------------------------------------------------
    # A prefix used but not declared is a parse error; a prefix declared but absent from
    # IgnorableNamespaces makes the whole manifest fail on an OS that does not know it.
    $declared = @{}
    foreach ($a in $m.DocumentElement.Attributes) {
        if ($a.Prefix -eq 'xmlns') { $declared[$a.LocalName] = $a.Value }
    }
    $ignorable = @()
    if ($m.DocumentElement.HasAttribute('IgnorableNamespaces')) {
        $ignorable = @($m.DocumentElement.GetAttribute('IgnorableNamespaces') -split '\s+' | Where-Object { $_ })
    }
    $usedPrefixes = @{}
    foreach ($n in $m.SelectNodes('//*')) { if ($n.Prefix) { $usedPrefixes[$n.Prefix] = $true } }
    foreach ($n in $m.SelectNodes('//@*'))  { if ($n.Prefix -and $n.Prefix -ne 'xmlns') { $usedPrefixes[$n.Prefix] = $true } }

    $undeclared = @($usedPrefixes.Keys | Where-Object { -not $declared.ContainsKey($_) })
    $findings += if ($undeclared.Count -eq 0) { New-Finding 'M02a' 'every used prefix is declared' 'PASS' ($usedPrefixes.Keys -join ' ') }
                 else { New-Finding 'M02a' 'every used prefix is declared' 'FAIL' "undeclared: $($undeclared -join ', ')" }

    $notIgnorable = @($usedPrefixes.Keys | Where-Object { $_ -ne 'xmlns' -and $ignorable -notcontains $_ })
    $findings += if ($notIgnorable.Count -eq 0) { New-Finding 'M02b' 'every used prefix is in IgnorableNamespaces' 'PASS' ($ignorable -join ' ') }
                 else { New-Finding 'M02b' 'every used prefix is in IgnorableNamespaces' 'FAIL' "missing from IgnorableNamespaces: $($notIgnorable -join ', ')" }

    # --- M03 identity ----------------------------------------------------------------------------
    $identity = One('/f:Package/f:Identity')
    if (-not $identity) {
        $findings += New-Finding 'M03' 'Identity present' 'FAIL' 'no Identity element'
    } else {
        $findings += New-Finding 'M03' 'Identity present' 'PASS' "Name=$($identity.GetAttribute('Name')) Version=$($identity.GetAttribute('Version'))"

        $pub = $identity.GetAttribute('Publisher')
        $findings += if ($pub -ceq $ExpectedPublisher) {
            New-Finding 'M04' 'Publisher matches the signing certificate subject exactly' 'PASS' $pub
        } else {
            New-Finding 'M04' 'Publisher matches the signing certificate subject exactly' 'FAIL' "manifest '$pub' vs certificate '$ExpectedPublisher' (deployment fails 0x8007000B)"
        }

        # A sparse identity package carries no binaries, so it is architecture-neutral. The canonical
        # Microsoft example uses neutral; the ContextMenuSample omits the attribute entirely. A
        # concrete architecture here is unjustified and was not taken from any source.
        $arch = if ($identity.HasAttribute('ProcessorArchitecture')) { $identity.GetAttribute('ProcessorArchitecture') } else { '(absent)' }
        $findings += if ($arch -in @('neutral','(absent)')) {
            New-Finding 'M05' 'ProcessorArchitecture is neutral or absent' 'PASS' $arch
        } else {
            New-Finding 'M05' 'ProcessorArchitecture is neutral or absent' 'FAIL' "got '$arch'; the package has no payload binaries"
        }
    }

    # --- M06 AllowExternalContent - the requirement the operation imposes -------------------------
    $allow = One('/f:Package/f:Properties/uap10:AllowExternalContent')
    $findings += if (-not $allow) {
        New-Finding 'M06' 'uap10:AllowExternalContent present (required by -ExternalLocation)' 'FAIL' 'element missing; deployment fails 0x80073D2E'
    } elseif ($allow.InnerText.Trim().ToLowerInvariant() -ne 'true') {
        New-Finding 'M06' 'uap10:AllowExternalContent is true' 'FAIL' "value '$($allow.InnerText)'"
    } else {
        New-Finding 'M06' 'uap10:AllowExternalContent is true' 'PASS' 'true'
    }

    # --- M07 SplashScreen must be absent ----------------------------------------------------------
    # Documented to make registration fail for this package shape.
    $splash = Sel('//*[local-name()="SplashScreen"]')
    $findings += if ($splash.Count -eq 0) { New-Finding 'M07' 'no SplashScreen element' 'PASS' 'absent' }
                 else { New-Finding 'M07' 'no SplashScreen element' 'FAIL' "$($splash.Count) present; registration fails" }

    # --- M08 target device family ------------------------------------------------------------------
    $tdf = One('/f:Package/f:Dependencies/f:TargetDeviceFamily')
    if (-not $tdf) {
        $findings += New-Finding 'M08' 'TargetDeviceFamily present' 'FAIL' 'missing'
    } else {
        $minv = $tdf.GetAttribute('MinVersion')
        $ok = $false
        $parsed = $null
        if ([version]::TryParse($minv, [ref]$parsed)) { $ok = $parsed -ge [version]'10.0.19041.0' }
        $findings += if ($tdf.GetAttribute('Name') -eq 'Windows.Desktop' -and $ok) {
            New-Finding 'M08' 'Windows.Desktop, MinVersion >= 10.0.19041.0 (AllowExternalContent)' 'PASS' "$($tdf.GetAttribute('Name')) $minv"
        } else {
            New-Finding 'M08' 'Windows.Desktop, MinVersion >= 10.0.19041.0 (AllowExternalContent)' 'FAIL' "$($tdf.GetAttribute('Name')) $minv"
        }
    }

    # --- M09 capabilities ---------------------------------------------------------------------------
    $caps = @(Sel('/f:Package/f:Capabilities/rescap:Capability') | ForEach-Object { $_.GetAttribute('Name') })
    foreach ($need in @('runFullTrust','unvirtualizedResources')) {
        $findings += if ($caps -contains $need) { New-Finding "M09-$need" "rescap:Capability $need" 'PASS' 'declared' }
                     else { New-Finding "M09-$need" "rescap:Capability $need" 'FAIL' "missing; declared: $($caps -join ', ')" }
    }

    # --- M10 application ------------------------------------------------------------------------------
    $app = One('/f:Package/f:Applications/f:Application')
    if (-not $app) {
        $findings += New-Finding 'M10' 'Application present' 'FAIL' 'missing'
    } else {
        $exe = $app.GetAttribute('Executable')
        $findings += if ($exe) { New-Finding 'M10' 'Application/@Executable set' 'PASS' $exe }
                     else { New-Finding 'M10' 'Application/@Executable set' 'FAIL' 'empty' }

        foreach ($pair in @(@{A='TrustLevel'; V='mediumIL'}, @{A='RuntimeBehavior'; V='win32App'})) {
            $got = $app.GetAttribute($pair.A, $script:NS.uap10)
            $findings += if ($got -eq $pair.V) { New-Finding "M11-$($pair.A)" "uap10:$($pair.A) = $($pair.V)" 'PASS' $got }
                         else { New-Finding "M11-$($pair.A)" "uap10:$($pair.A) = $($pair.V)" 'FAIL' "got '$got'" }
        }

        $ve = One('/f:Package/f:Applications/f:Application/uap:VisualElements')
        $findings += if ($ve -and $ve.GetAttribute('AppListEntry') -eq 'none') {
            New-Finding 'M12' 'VisualElements AppListEntry=none' 'PASS' 'none'
        } else {
            New-Finding 'M12' 'VisualElements AppListEntry=none' 'FAIL' "got '$(if($ve){$ve.GetAttribute('AppListEntry')}else{'no VisualElements'})'"
        }
    }

    # --- M13 COM server -------------------------------------------------------------------------------
    # Confirmed by the Microsoft ContextMenuSample: com:Extension lives under Application/Extensions,
    # not Package/Extensions.
    $comExt = One('/f:Package/f:Applications/f:Application/f:Extensions/com:Extension[@Category="windows.comServer"]')
    $findings += if ($comExt) { New-Finding 'M13' 'com:Extension windows.comServer under Application/Extensions' 'PASS' 'present' }
                 else { New-Finding 'M13' 'com:Extension windows.comServer under Application/Extensions' 'FAIL' 'missing or in the wrong parent' }

    $surrogate = One('//com:ComServer/com:SurrogateServer')
    $findings += if ($surrogate) { New-Finding 'M14' 'com:SurrogateServer declared (out-of-process host)' 'PASS' $surrogate.GetAttribute('DisplayName') }
                 else { New-Finding 'M14' 'com:SurrogateServer declared (out-of-process host)' 'FAIL' 'missing - an in-proc server would load into explorer.exe' }

    $comClass = One('//com:SurrogateServer/com:Class')
    if (-not $comClass) {
        $findings += New-Finding 'M15' 'com:Class under SurrogateServer' 'FAIL' 'missing'
    } else {
        $id = $comClass.GetAttribute('Id').Trim('{','}')
        $findings += if ($id -ieq $ExpectedClsid) { New-Finding 'M15' 'com:Class Id matches the stub CLSID' 'PASS' $id }
                     else { New-Finding 'M15' 'com:Class Id matches the stub CLSID' 'FAIL' "manifest $id vs stub $ExpectedClsid" }

        $tm = $comClass.GetAttribute('ThreadingModel')
        $findings += if ($tm) { New-Finding 'M16' 'com:Class ThreadingModel set' 'PASS' $tm }
                     else { New-Finding 'M16' 'com:Class ThreadingModel set' 'FAIL' 'empty' }
    }

    # --- M17 context menu extension --------------------------------------------------------------------
    # Mixed prefixes, and the schema is strict: Extension and FileExplorerContextMenus are desktop4,
    # ItemType and Verb are desktop5.
    $d4ext = One('/f:Package/f:Applications/f:Application/f:Extensions/desktop4:Extension[@Category="windows.fileExplorerContextMenus"]')
    $findings += if ($d4ext) { New-Finding 'M17' 'desktop4:Extension windows.fileExplorerContextMenus' 'PASS' 'present' }
                 else { New-Finding 'M17' 'desktop4:Extension windows.fileExplorerContextMenus' 'FAIL' 'missing or wrong prefix/parent' }

    $findings += if (One('//desktop4:FileExplorerContextMenus')) { New-Finding 'M18' 'desktop4:FileExplorerContextMenus' 'PASS' 'present' }
                 else { New-Finding 'M18' 'desktop4:FileExplorerContextMenus' 'FAIL' 'missing or wrong prefix' }

    $itemTypes = Sel('//desktop4:FileExplorerContextMenus/desktop5:ItemType')
    $findings += if ($itemTypes.Count -gt 0) { New-Finding 'M19' 'desktop5:ItemType present' 'PASS' "$($itemTypes.Count): $(@($itemTypes | ForEach-Object { $_.GetAttribute('Type') }) -join ', ')" }
                 else { New-Finding 'M19' 'desktop5:ItemType present' 'FAIL' 'none' }

    $verbs = @(Sel('//desktop5:ItemType/desktop5:Verb'))
    if ($verbs.Count -eq 0) {
        $findings += New-Finding 'M20' 'desktop5:Verb present' 'FAIL' 'none'
    } else {
        $bad = @($verbs | Where-Object { $_.GetAttribute('Clsid').Trim('{','}') -ine $ExpectedClsid })
        $findings += if ($bad.Count -eq 0) { New-Finding 'M20' 'every desktop5:Verb Clsid matches the stub CLSID' 'PASS' "$($verbs.Count) verb(s)" }
                     else { New-Finding 'M20' 'every desktop5:Verb Clsid matches the stub CLSID' 'FAIL' "$($bad.Count) verb(s) with a different Clsid" }
    }

    # --- M21 wrong-prefix combinations that the schema rejects --------------------------------------------
    foreach ($bad in @(
        @{ X = '//desktop5:Extension';                W = 'desktop5:Extension (must be desktop4)' },
        @{ X = '//desktop5:FileExplorerContextMenus'; W = 'desktop5:FileExplorerContextMenus (must be desktop4)' },
        @{ X = '//desktop4:ItemType';                 W = 'desktop4:ItemType (this harness uses desktop5)' },
        @{ X = '//desktop4:Verb';                     W = 'desktop4:Verb (this harness uses desktop5)' })) {
        $hit = Sel($bad.X)
        $findings += if ($hit.Count -eq 0) { New-Finding "M21" "no $($bad.W)" 'PASS' 'absent' }
                     else { New-Finding "M21" "no $($bad.W)" 'FAIL' "$($hit.Count) present - makeappx rejects this (C00CE014)" }
    }

    # --- M22 elevation boundary (plan section L.4) -----------------------------------------------------------------
    # Checked against the parsed XML: a text search matches the manifest's own comments.
    $runas = Sel('//@*[local-name()="RunAs"]')
    $findings += if ($runas.Count -eq 0) { New-Finding 'M22' 'no RunAs attribute (plan section L.4 elevation boundary)' 'PASS' 'absent' }
                 else { New-Finding 'M22' 'no RunAs attribute (plan section L.4 elevation boundary)' 'FAIL' "$($runas.Count) present" }

    # --- M23 referenced files exist ---------------------------------------------------------------------------
    # Assets are IN the package, so they resolve against the package root. Executable and the COM
    # class Path are NOT in the package - they resolve against the external location, which is the
    # entire point of -ExternalLocation.
    if ($PackageRoot) {
        $assetRefs = @()
        $logo = One('/f:Package/f:Properties/f:Logo'); if ($logo) { $assetRefs += $logo.InnerText }
        $ve2 = One('/f:Package/f:Applications/f:Application/uap:VisualElements')
        if ($ve2) { foreach ($a in @('Square150x150Logo','Square44x44Logo')) { if ($ve2.HasAttribute($a)) { $assetRefs += $ve2.GetAttribute($a) } } }
        $missingAssets = @()
        foreach ($r in $assetRefs) {
            $p = Join-Path $PackageRoot ($r -replace '\\', [IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path $p)) { $missingAssets += $r }
        }
        $findings += if ($missingAssets.Count -eq 0) { New-Finding 'M23' 'package assets exist in the package root' 'PASS' "$($assetRefs.Count) reference(s)" }
                     else { New-Finding 'M23' 'package assets exist in the package root' 'FAIL' "missing: $($missingAssets -join ', ')" }
    }

    if ($ExternalLocation) {
        $extRefs = @()
        if ($app -and $app.GetAttribute('Executable')) { $extRefs += $app.GetAttribute('Executable') }
        if ($comClass -and $comClass.GetAttribute('Path')) { $extRefs += $comClass.GetAttribute('Path') }
        $missingExt = @()
        foreach ($r in $extRefs) {
            $p = Join-Path $ExternalLocation ($r -replace '\\', [IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path $p)) { $missingExt += $r }
        }
        $findings += if ($missingExt.Count -eq 0) { New-Finding 'M24' 'external-location payload exists' 'PASS' ($extRefs -join ', ') }
                     else { New-Finding 'M24' 'external-location payload exists' 'FAIL' "missing from $ExternalLocation : $($missingExt -join ', ')" }
    }

    return ,$findings
}

# =================================================================================================
# Sideloading policy - a GATE, and a precondition REPORT.
#
# THIS EXISTS BECAUSE THE AUDIT FAILED HERE. Requirement 30 was recorded VERIFIED on the strength of
# "sideloading needs no policy toggle on Windows 10 2004+", and the check was made INFORMATIONAL. The
# machine then printed `AllowAllTrustedApps=0` in the script's own output and it deployed anyway,
# failing 0x80073CFF. The fact was gathered, printed, and never wired to a decision.
#
# TWO KEYS, and only one of them is documented:
#   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock   the SETTING (Settings UI writes it)
#   HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx                   the GROUP POLICY override
#
# Microsoft documents the first. It does NOT document the second's semantics, so the policy branch
# reports the OBSERVED value and says so, rather than presenting an interpretation as a fact.
#
# THE HARNESS NEVER CHANGES EITHER KEY. Sideloading is a machine security setting and a Group Policy
# value may belong to an organisation rather than to the developer at the keyboard.
# =================================================================================================

<#
.SYNOPSIS
    The precondition report: policy source, effective value, whether it is user-configurable, and what
    the harness will do about it. Reads only; changes nothing.
#>
function Resolve-E2SideloadingState {
    <#
    .SYNOPSIS
        The decision, separated from the registry so every branch can be tested.
    .DESCRIPTION
        Split out because the BLOCKED-BY-POLICY branch is the one that matters and the one that can
        never be reached on a machine that does not have the policy. A decision function whose
        important branch has never been executed is not a tested decision.
    #>
    param(
        [AllowNull()][object]$PolicyTrusted,
        [AllowNull()][object]$UnlockTrusted,
        [AllowNull()][object]$UnlockDev
    )

    $fmt = { param($v) if ($null -eq $v) { '(unset)' } else { "$v" } }

    if ($PolicyTrusted -eq 0 -and $null -ne $PolicyTrusted) {
        $state  = 'BLOCKED BY POLICY'
        $source = 'Group Policy: HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx\AllowAllTrustedApps = 0'
        $config = 'NO for the developer alone. A Policies value is set by Group Policy, MDM or an OEM/organisation image. The Settings toggle is typically greyed out while it is present.'
        $action = 'REFUSE. The harness stops before creating a certificate, a package or a registration.'
        $ok     = $false
    }
    elseif (($UnlockTrusted -eq 1) -or ($UnlockDev -eq 1) -or ($PolicyTrusted -eq 1)) {
        $state  = 'PERMITTED'
        $source = if ($PolicyTrusted -eq 1) { 'Group Policy allows it' }
                  elseif ($UnlockDev -eq 1) { 'AppModelUnlock\AllowDevelopmentWithoutDevLicense = 1 (Developer Mode)' }
                  else { 'AppModelUnlock\AllowAllTrustedApps = 1 (sideloading)' }
        $config = 'YES - Settings > System > For developers.'
        $action = 'PROCEED.'
        $ok     = $true
    }
    else {
        $state  = 'NOT ENABLED'
        $source = 'no key permits it; AppModelUnlock values are unset or 0'
        $config = 'YES - Settings > System > For developers > Developer Mode, unless a policy appears.'
        $action = 'REFUSE. Absence of a permission is not permission.'
        $ok     = $false
    }

    return [pscustomobject]@{
        EffectiveState        = $state
        Permitted             = $ok
        Source                = $source
        UserConfigurable      = $config
        HarnessAction         = $action
        PolicyKey             = 'HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx'
        PolicyAllowAllTrusted = & $fmt $PolicyTrusted
        UnlockKey             = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
        UnlockAllowAllTrusted = & $fmt $UnlockTrusted
        UnlockAllowDevMode    = & $fmt $UnlockDev
        FailureCode           = '0x80073CFF'
    }
}

function Get-E2SideloadingReport {
    <#
    .SYNOPSIS
        Reads both keys and resolves the state. Reads only; changes nothing, ever.
    #>
    [CmdletBinding()]
    param()

    function Read-Dword([string]$Key, [string]$Name) {
        try { return [int](Get-ItemProperty -Path $Key -Name $Name -ErrorAction Stop).$Name }
        catch { return $null }
    }

    return Resolve-E2SideloadingState `
        -PolicyTrusted (Read-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx' 'AllowAllTrustedApps') `
        -UnlockTrusted (Read-Dword 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' 'AllowAllTrustedApps') `
        -UnlockDev     (Read-Dword 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' 'AllowDevelopmentWithoutDevLicense')
}

<#
.SYNOPSIS
    The same facts as a pre-flight finding, so the gate and the report can never disagree.
#>
function Test-E2Sideloading {
    [CmdletBinding()]
    param()
    $r = Get-E2SideloadingReport
    $detail = "$($r.EffectiveState); $($r.Source); policy=$($r.PolicyAllowAllTrusted) " +
              "unlock.AllowAllTrustedApps=$($r.UnlockAllowAllTrusted) unlock.DevMode=$($r.UnlockAllowDevMode)"
    if ($r.Permitted) { return ,@(New-Finding 'S01' 'sideloading permitted on this machine' 'PASS' $detail) }
    return ,@(New-Finding 'S01' 'sideloading permitted on this machine' 'FAIL' "$detail - deployment fails $($r.FailureCode)")
}

<#
.SYNOPSIS
    Prints the precondition report. Read-only; used by run-e2.ps1 -PreconditionsOnly.
#>
function Show-E2Preconditions {
    [CmdletBinding()]
    param()
    $r = Get-E2SideloadingReport
    Write-Host ''
    Write-Host 'E2 PRECONDITION REPORT - sideloading'
    Write-Host ('-' * 78)
    Write-Host ("  effective state    : {0}" -f $r.EffectiveState)
    Write-Host ("  policy source      : {0}" -f $r.Source)
    Write-Host ("  user-configurable  : {0}" -f $r.UserConfigurable)
    Write-Host ("  harness action     : {0}" -f $r.HarnessAction)
    Write-Host ''
    Write-Host ("  {0}" -f $r.PolicyKey)
    Write-Host ("      AllowAllTrustedApps                = {0}" -f $r.PolicyAllowAllTrusted)
    Write-Host ("  {0}" -f $r.UnlockKey)
    Write-Host ("      AllowAllTrustedApps                = {0}" -f $r.UnlockAllowAllTrusted)
    Write-Host ("      AllowDevelopmentWithoutDevLicense  = {0}" -f $r.UnlockAllowDevMode)
    Write-Host ''
    Write-Host '  The harness NEVER modifies either key. Sideloading is a machine security setting,'
    Write-Host '  and a Group Policy value may belong to an organisation rather than to the developer.'
    Write-Host ('-' * 78)
    return $r
}

# =================================================================================================
# Certificate lifecycle - X509Store only.
#
# Remove-Item on the Cert: provider failed twice during this gate, once inside the harness and once
# by hand in an elevated prompt. These open the store explicitly and report per-certificate.
# =================================================================================================

<#
.SYNOPSIS
    How many certificates with this subject are in the store. Returns -1 for "could not look",
    which must never be treated as zero.
#>
function Get-E2CertCount {
    param([Parameter(Mandatory)][string]$StoreName,
          [Parameter(Mandatory)][string]$Location,
          [Parameter(Mandatory)][string]$Subject)
    try {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $Location)
        $store.Open('ReadOnly')
        try { return @($store.Certificates | Where-Object { $_.Subject -eq $Subject }).Count }
        finally { try { $store.Close() } catch { } }
    } catch { return -1 }
}

<#
.SYNOPSIS
    Removes every certificate with this subject from the store. Never throws.
#>
function Remove-E2Certs {
    param([Parameter(Mandatory)][string]$StoreName,
          [Parameter(Mandatory)][string]$Location,
          [Parameter(Mandatory)][string]$Subject)
    $result = @{ Removed = 0; Failed = 0; Opened = $false; Error = $null }
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
            try { $store.Remove($c); $result.Removed++ } catch { $result.Failed++; $result.Error = $_.Exception.Message }
        }
    } finally { try { $store.Close() } catch { } }
    return $result
}

# =================================================================================================
# Script encoding / Windows PowerShell 5.1 parseability.
#
# WHY THIS EXISTS. run-e2.ps1 reached the VM byte-identical to the source tree (SHA-256 verified)
# and still failed to parse under Windows PowerShell 5.1:
#
#     At line:130 Missing closing '}' in statement block or type definition.
#     At line:122 function Show-DeploymentLog {
#
# The file was UTF-8 WITHOUT a BOM. Windows PowerShell 5.1 decodes a BOM-less file as the machine's
# ANSI codepage, not as UTF-8. The arrow character U+2192 is stored as the bytes E2 86 92, and in
# CP1252 the byte 0x92 is U+2019 RIGHT SINGLE QUOTATION MARK - which PowerShell ACCEPTS as a string
# delimiter. Every mis-decoded arrow therefore opened or closed a string, swallowing the braces
# between them, and the parser reported the imbalance at the nearest enclosing function.
#
# The four bytes that do this are 0x91 0x92 0x93 0x94 (the curly quotes in CP1252 and in most other
# ANSI codepages). Nothing in a PowerShell script needs them.
#
# THE RULE. A harness script must be pure ASCII, or carry a UTF-8 BOM. Pure ASCII decodes to the
# same characters under UTF-8 and under every ANSI codepage, single-byte or DBCS, because no ANSI
# codepage reassigns 0x00-0x7F and no DBCS lead byte is below 0x81. A BOM makes 5.1 decode as UTF-8
# explicitly. Either one removes the failure class; neither is a matter of taste.
#
# THE CHECK. The byte rule above is the guarantee. The parse below is the falsification: the file's
# raw bytes are decoded the way 5.1 would decode them and handed to the PowerShell parser. It is
# deterministic because the codepage is pinned rather than inherited from the host, so it reproduces
# the VM's failure on Linux, on PowerShell 7, and on any machine's regional settings.
# =================================================================================================

$script:E2QuoteCapableBytes = @(0x91, 0x92, 0x93, 0x94)

<#
.SYNOPSIS
    Decodes raw file bytes the way Windows PowerShell 5.1 would: honour a BOM if present, otherwise
    use the given ANSI codepage.
#>
function ConvertFrom-E2ScriptBytes {
    param([Parameter(Mandatory)][byte[]]$Bytes, [Parameter(Mandatory)][int]$Codepage)
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        return [System.Text.Encoding]::UTF8.GetString($Bytes, 3, $Bytes.Length - 3)
    }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) {
        return [System.Text.Encoding]::Unicode.GetString($Bytes, 2, $Bytes.Length - 2)
    }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF) {
        return [System.Text.Encoding]::BigEndianUnicode.GetString($Bytes, 2, $Bytes.Length - 2)
    }
    # .NET on Linux/macOS does not carry the ANSI codepages unless the provider is registered.
    # Registering twice is harmless; failing to register must not be silent.
    try { [void][System.Text.Encoding]::GetEncoding($Codepage) } catch {
        try { [System.Text.Encoding]::RegisterProvider([System.Text.CodePagesEncodingProvider]::Instance) } catch { }
    }
    return [System.Text.Encoding]::GetEncoding($Codepage).GetString($Bytes)
}

<#
.SYNOPSIS
    Reports what a script's bytes are, independently of any host's decoding.
#>
function Get-E2ScriptEncodingFacts {
    param([Parameter(Mandatory)][string]$Path)
    $bytes  = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $high   = @($bytes | Where-Object { $_ -gt 0x7F })
    $quote  = @($bytes | Where-Object { $script:E2QuoteCapableBytes -contains $_ })
    # The line of the first offending byte, counted in the raw bytes, so it matches what an editor
    # shows regardless of how the file is decoded.
    $line = 0
    if ($high.Count -gt 0) {
        $line = 1
        for ($i = 0; $i -lt $bytes.Length; $i++) {
            if ($bytes[$i] -gt 0x7F) { break }
            if ($bytes[$i] -eq 0x0A) { $line++ }
        }
    }
    [pscustomobject]@{
        Path              = $Path
        Name              = [System.IO.Path]::GetFileName($Path)
        Bytes             = $bytes.Length
        HasBom            = $hasBom
        IsAscii           = ($high.Count -eq 0)
        NonAsciiBytes     = $high.Count
        QuoteCapableBytes = $quote.Count
        FirstNonAsciiLine = $line
    }
}

<#
.SYNOPSIS
    Asserts that every harness script survives Windows PowerShell 5.1. Returns findings.
.DESCRIPTION
    Two independent requirements per file:

      H01  the byte rule    - pure ASCII, or a UTF-8 BOM. This is what makes the decode
                              codepage-invariant, and it is the requirement that actually holds
                              the line.
      H02  the parse        - the raw bytes, decoded as 5.1 would decode them under each pinned
                              codepage, are handed to the PowerShell parser. This is evidence,
                              not belief: it is the exact experiment that reproduced the VM's
                              two errors at lines 130 and 122.

    A file can pass H02 by luck - an even number of mis-decoded quote bytes, or all of them inside
    a block comment - which is why H01 is not derived from H02 and neither is skipped.
.PARAMETER Root
    Directory searched recursively for *.ps1 and *.psm1.
.PARAMETER Path
    Explicit files, instead of a directory.
.PARAMETER Codepage
    ANSI codepages to emulate. 1252 is pinned by default because it is the codepage that produced
    the observed failure; passing more costs nothing.
#>
function Test-E2ScriptEncoding {
    [CmdletBinding(DefaultParameterSetName = 'Root')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Root')][string]$Root,
        [Parameter(Mandatory, ParameterSetName = 'Path')][string[]]$Path,
        [int[]]$Codepage = @(1252)
    )

    if ($PSCmdlet.ParameterSetName -eq 'Root') {
        $files = @(Get-ChildItem -Path $Root -Recurse -File -Include '*.ps1', '*.psm1' -ErrorAction SilentlyContinue |
                   Sort-Object FullName | ForEach-Object { $_.FullName })
    } else {
        $files = @($Path)
    }

    $findings = @()
    if ($files.Count -eq 0) {
        $findings += New-Finding 'H00' 'harness scripts found' 'FAIL' "none under $Root - the check would pass vacuously"
        return ,$findings
    }
    $findings += New-Finding 'H00' 'harness scripts found' 'PASS' "$($files.Count) file(s)"

    foreach ($f in $files) {
        $facts = Get-E2ScriptEncodingFacts -Path $f
        $id    = $facts.Name

        if ($facts.IsAscii) {
            $findings += New-Finding "H01-$id" 'pure ASCII or UTF-8 BOM (codepage-invariant)' 'PASS' "$($facts.Bytes) bytes, ASCII only"
        } elseif ($facts.HasBom) {
            $findings += New-Finding "H01-$id" 'pure ASCII or UTF-8 BOM (codepage-invariant)' 'PASS' `
                         "$($facts.NonAsciiBytes) non-ASCII byte(s), but a UTF-8 BOM is present"
        } else {
            $q = if ($facts.QuoteCapableBytes -gt 0) {
                     "$($facts.QuoteCapableBytes) of them decode to a PowerShell quote character under an ANSI codepage"
                 } else { 'none of them is quote-capable today, which is luck, not a guarantee' }
            $findings += New-Finding "H01-$id" 'pure ASCII or UTF-8 BOM (codepage-invariant)' 'FAIL' `
                         ("UTF-8 without BOM, $($facts.NonAsciiBytes) non-ASCII byte(s), first at line $($facts.FirstNonAsciiLine); $q")
        }

        $bytes = [System.IO.File]::ReadAllBytes($f)
        foreach ($cp in $Codepage) {
            $text = $null
            try { $text = ConvertFrom-E2ScriptBytes -Bytes $bytes -Codepage $cp }
            catch {
                $findings += New-Finding "H02-$id-$cp" "parses as Windows PowerShell 5.1 decodes it (cp$cp)" 'FAIL' `
                             "could not decode: $($_.Exception.Message)"
                continue
            }
            $tokens = $null; $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
            $errs = @($errors)
            if ($errs.Count -eq 0) {
                $findings += New-Finding "H02-$id-$cp" "parses as Windows PowerShell 5.1 decodes it (cp$cp)" 'PASS' 'no parse errors'
            } else {
                $first = $errs[0]
                $findings += New-Finding "H02-$id-$cp" "parses as Windows PowerShell 5.1 decodes it (cp$cp)" 'FAIL' `
                             ("$($errs.Count) parse error(s); first at line $($first.Extent.StartLineNumber): $($first.Message)")
            }
        }
    }
    return ,$findings
}

# =================================================================================================
# E2 criterion 3 - the surrogate verdict.
#
# WHY THIS IS A SEPARATE FUNCTION. The 2026-08-31 run returned:
#
#     [FAIL] 3. a dllhost.exe hosts the stub, as the standard user
#            no dllhost.exe has the stub loaded - either never activated, or it has already exited
#
# while the stub's own log proved the stub HAD loaded into two non-explorer processes with the CLR
# running in both. The criterion had sampled live processes once, after a human pressed ENTER, by
# which time both surrogates had gone. Worse, the enumeration collapsed "I could not read the module
# list" into "there was nothing there" - the same mistake the certificate rule exists to prevent,
# made three lines away from the explorer check that gets it right.
#
# So the decision is split from the measurement, exactly as Resolve-E2SideloadingState was: the
# branch that matters (UNKNOWN) is unreachable on a machine where everything is readable, and an
# untestable branch is where the next wrong answer hides.
#
# THE THREE-STATE RULE. A criterion that could not be measured is UNKNOWN. UNKNOWN is not a pass and
# is not a refutation: it does not count towards E2's 3/3, and it must never be reported as though
# the harness had looked and found nothing.
# =================================================================================================

<#
.SYNOPSIS
    Decides E2 criterion 3 from what the observation window actually captured.
.PARAMETER Capture
    The record taken WHILE a surrogate was alive, or $null if none was captured. Expected shape:
    Pid, Name, Owner, HasClr, HasFxr, HasStub.
.PARAMETER SawAnyHostProcess
    Whether any candidate host process existed at any point during the window.
.PARAMETER UnreadableSamples
    How many times a candidate host process was found but its module list could not be read.
    Any non-zero value means the harness could not look, which is never evidence of absence.
.PARAMETER ExpectedUser
    DOMAIN\user the surrogate must run as - the identity running the harness.
.PARAMETER ActivationConfirmed
    Whether the operator confirmed that the right-click actually happened while the window was open.

    THIS PARAMETER EXISTS BECAUSE OF THE 2026-08-31 05:00 RUN. The window expired after 180 seconds
    having seen readable dllhost processes and no stub, and the harness reported:

        [FAIL] candidate host processes were present and readable throughout the window,
               and none ever had the stub loaded

    Cleanup then found pid 13232 with the stub loaded, using the SAME matcher. So the surrogate was
    real; it simply did not exist while the window was open. "Nobody activated it in time" and
    "activation does not land in a surrogate" produce the same empty sample, and only the operator
    can tell them apart. Without this input the harness cannot distinguish a refutation from a
    window that closed too early - and it printed the refutation.
#>
function Resolve-E2SurrogateVerdict {
    param(
        [AllowNull()][object]$Capture,
        [Parameter(Mandatory)][bool]$SawAnyHostProcess,
        [Parameter(Mandatory)][int]$UnreadableSamples,
        [Parameter(Mandatory)][string]$ExpectedUser,
        [bool]$ActivationConfirmed = $false
    )

    if ($null -ne $Capture) {
        $why = @()
        if (-not $Capture.HasClr)  { $why += 'coreclr not loaded' }
        if (-not $Capture.HasFxr)  { $why += 'hostfxr not loaded' }
        if (-not $Capture.HasStub) { $why += 'the stub is not loaded in this process' }
        $ownerOk = ($Capture.Owner -eq $ExpectedUser)
        if (-not $ownerOk) { $why += "owner is '$($Capture.Owner)', expected '$ExpectedUser'" }

        $detail = "captured live: pid $($Capture.Pid) ($($Capture.Name)); coreclr=$($Capture.HasClr) hostfxr=$($Capture.HasFxr) stub=$($Capture.HasStub) owner=$($Capture.Owner)"
        if ($why.Count -eq 0) { return [pscustomobject]@{ Status = 'PASS'; Detail = $detail } }
        return [pscustomobject]@{ Status = 'FAIL'; Detail = "$detail - $($why -join '; ')" }
    }

    if (-not $ActivationConfirmed) {
        $extra = if ($UnreadableSamples -gt 0) { " ($UnreadableSamples module list(s) were also unreadable.)" } else { '' }
        return [pscustomobject]@{
            Status = 'UNKNOWN'
            Detail = 'the window closed without the operator confirming that the right-click happened while it was open. ' +
                     'An empty sample from a window nobody acted in measures nothing, and is NOT a refutation of ' +
                     "criterion 3.$extra"
        }
    }

    if ($UnreadableSamples -gt 0) {
        return [pscustomobject]@{
            Status = 'UNKNOWN'
            Detail = "$UnreadableSamples host process module list(s) could not be read during the window. " +
                     'The harness could not look. That is NOT evidence that no surrogate hosted the stub, ' +
                     'and it is not a refutation of criterion 3.'
        }
    }

    if ($SawAnyHostProcess) {
        return [pscustomobject]@{
            Status = 'FAIL'
            Detail = 'activation was CONFIRMED during the window, candidate host processes were present and readable ' +
                     'throughout, and none ever had the stub loaded'
        }
    }

    return [pscustomobject]@{
        Status = 'FAIL'
        Detail = 'activation was CONFIRMED during the window and no candidate host process existed at any point - ' +
                 'the surrogate was never started'
    }
}

<#
.SYNOPSIS
    Decides what a surrogate found at CLEANUP time means, given what criterion 3 captured.
.DESCRIPTION
    WHY THIS EXISTS. The 2026-08-31 10:40 run PASSED 3/3, and its cleanup section printed:

        LATE DETECTION: surrogate pid 10460 ... started inside the observation window: NO
        Criterion 3 stays UNMEASURED, not refuted.

    Criterion 3 had just captured pid 10460 and passed on it. The note asserted a conclusion it had
    never checked, inside the transcript that IS the evidence - so the official record of a passing
    run contradicts itself.

    Same defect as every previous one in this gate: text stating more than was measured. The way out
    is the same too - make it a decision with inputs, and control every branch.
.PARAMETER SurrogatePid
    The surrogate process cleanup found.
.PARAMETER Capture
    What criterion 3 captured, or $null if it captured nothing.
.PARAMETER StartedInWindow
    Whether that process started inside the observation window. $null if its start time was unreadable.
#>
function Resolve-E2LateDetectionNote {
    param(
        [Parameter(Mandatory)][int]$SurrogatePid,
        [AllowNull()][object]$Capture,
        [AllowNull()][object]$StartedInWindow
    )

    if ($null -ne $Capture -and $Capture.Pid -eq $SurrogatePid) {
        return [pscustomobject]@{
            Status = 'CONFIRMS'
            Text   = "this is the process criterion 3 captured and passed on; finding it here corroborates that result"
        }
    }
    if ($null -ne $Capture) {
        return [pscustomobject]@{
            Status = 'OTHER'
            Text   = "a second surrogate; criterion 3 captured pid $($Capture.Pid). Both held the stub"
        }
    }
    if ($StartedInWindow -eq $true) {
        return [pscustomobject]@{
            Status = 'MISSED'
            Text   = 'it started INSIDE the observation window and the watcher did not see it. ' +
                     'That is a harness defect, not a mistimed window - the polling missed a live process'
        }
    }
    if ($StartedInWindow -eq $false) {
        return [pscustomobject]@{
            Status = 'MISTIMED'
            Text   = 'it started OUTSIDE the observation window. The window was mistimed; activation did ' +
                     'not fail. Criterion 3 is UNMEASURED, not refuted'
        }
    }
    return [pscustomobject]@{
        Status = 'UNKNOWN'
        Text   = 'its start time could not be read, so whether the window was mistimed cannot be said'
    }
}

Export-ModuleMember -Function Test-E2Manifest, Test-E2Sideloading, Resolve-E2SideloadingState, Get-E2SideloadingReport, Show-E2Preconditions, Get-E2CertCount, Remove-E2Certs, New-Finding, Test-E2ScriptEncoding, Get-E2ScriptEncodingFacts, ConvertFrom-E2ScriptBytes, Resolve-E2SurrogateVerdict, Resolve-E2LateDetectionNote
