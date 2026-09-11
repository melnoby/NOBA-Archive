#Requires -Version 5.1
<#
.SYNOPSIS
    Local control suite for the E2 pre-flight. Runs on ANY platform, before Windows.
.DESCRIPTION
    Every manifest requirement gets a POSITIVE control (the real manifest must pass it) and a
    NEGATIVE control (a deliberately broken copy must fail it, by name). A check that has never been
    seen to fail is not a check.

    This exists because three Windows runs were spent on defects that a local control would have
    caught in seconds. Run it before ever running run-e2.ps1.

.EXAMPLE
    .\harness\phase5-e\test-e2-preflight.ps1

    Runs under Windows PowerShell 5.1 (which is what ships with Windows) as well as PowerShell 7.
    Nothing here is 7-only: no $IsWindows, no null-coalescing, no ForEach-Object -Parallel.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$here        = Split-Path -Parent $MyInvocation.MyCommand.Path
$harnessRoot = Split-Path -Parent $here

$pass = 0; $fail = 0
function Report([bool]$ok, [string]$what, [string]$detail) {
    if ($ok) { Write-Host ("  [ok]   {0}" -f $what) -ForegroundColor Green; $script:pass++ }
    else     { Write-Host ("  [FAIL] {0}" -f $what) -ForegroundColor Red;   $script:fail++ }
    if ($detail) { Write-Host "         $detail" }
}

Write-Host ''
Write-Host ('=' * 78)
Write-Host 'E2 PRE-FLIGHT - LOCAL CONTROL SUITE'
Write-Host ('=' * 78)

# -------------------------------------------------------------------------------------------------
# 0. BOOTSTRAP. This runs BEFORE Import-Module, and it has to.
#
# The authoritative encoding check lives in E2Preflight.psm1. If that module is itself mis-decoded,
# importing it throws a parse error and the check can never run to explain why - which is exactly
# what happened on the VM. So the byte rule is applied here first, on raw bytes, with no module and
# no parser involved. Section 0 below then runs the authoritative version, including the module.
$bootstrapBad = @()
foreach ($f in @(Get-ChildItem -Path $harnessRoot -Recurse -File -Include '*.ps1', '*.psm1' -ErrorAction SilentlyContinue)) {
    $b = [System.IO.File]::ReadAllBytes($f.FullName)
    $bom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
    if (-not $bom -and @($b | Where-Object { $_ -gt 0x7F }).Count -gt 0) { $bootstrapBad += $f.Name }
}
if ($bootstrapBad.Count -gt 0) {
    Write-Host ''
    Write-Host 'BOOTSTRAP FAILURE - harness scripts are not safe to load under Windows PowerShell 5.1:' -ForegroundColor Red
    foreach ($n in $bootstrapBad) { Write-Host "  $n : UTF-8 without BOM, contains non-ASCII bytes" -ForegroundColor Red }
    Write-Host '  5.1 decodes a BOM-less file as the ANSI codepage. Bytes 0x91-0x94 become PowerShell'
    Write-Host '  quote characters there and silently open strings. Make the file pure ASCII, or add a BOM.'
    exit 1
}

Import-Module (Join-Path $here 'E2Preflight.psm1') -Force

$Manifest  = Join-Path $here 'package/AppxManifest.xml'
$PkgRoot   = Join-Path $here 'package'
$Publisher = 'CN=NOBA-DEV-ONLY'
$Clsid     = '17019B66-1E78-459B-92AE-63A440E7C5EA'

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("e2ctl-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

# A broken copy of the manifest, produced by a text edit, checked in the same way.
function New-Broken([string]$Name, [scriptblock]$Mutate) {
    $text = Get-Content $Manifest -Raw
    $text = & $Mutate $text
    $path = Join-Path $tmp "$Name.xml"
    Set-Content -Path $path -Value $text -Encoding UTF8
    return $path
}

function Check([string]$Path, [string]$OnlyId) {
    # PackageRoot is passed so asset checks resolve; ExternalLocation deliberately omitted, because
    # the payload is built on Windows and does not exist here. M24 is exercised separately below.
    return Test-E2Manifest -Path $Path -ExpectedPublisher $Publisher -ExpectedClsid $Clsid -PackageRoot $PkgRoot
}

# -------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '0. WINDOWS POWERSHELL 5.1 PARSEABILITY - every harness script, before anything else'

$enc = Test-E2ScriptEncoding -Root $harnessRoot -Codepage 1252, 1251, 932
$encFails = @($enc | Where-Object { $_.Status -eq 'FAIL' })
Report ($encFails.Count -eq 0) "harness scripts: $($enc.Count) checks, $($encFails.Count) failures" `
       $(if ($encFails.Count) { ($encFails | ForEach-Object { "$($_.Id): $($_.Detail)" }) -join ' | ' }
         else { ($enc | Where-Object { $_.Id -eq 'H00' }).Detail })

# CONTROLS. A check that has never been seen to fail is not a check - and this one in particular was
# added after a defect that every parse check I had run said was absent, because they all ran on
# PowerShell 7, which decodes a BOM-less file as UTF-8.
$encTmp = Join-Path ([IO.Path]::GetTempPath()) ("e2enc-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $encTmp -Force | Out-Null

# The historical construct, rebuilt: Show-DeploymentLog's catch block, arrows inside single-quoted
# strings. U+2192 is E2 86 92; under CP1252 the 0x92 becomes U+2019, which PowerShell takes as a
# string delimiter, so the closing braces end up inside a string. The arrow is built from its code
# point and never typed, so this file stays pure ASCII itself.
$arrow  = [string][char]0x2192
$hazard = "function Show-DeploymentLog {`n" +
          "    try { Write-Host 'ok' } catch {`n" +
          "        Write-Host '  Event Viewer $arrow Applications $arrow Microsoft $arrow Windows'`n" +
          "        Write-Host '              $arrow AppxDeploymentServer $arrow Operational'`n" +
          "    }`n}`n"
# The same hazard with an EVEN number of arrows parses cleanly - the delimiters pair up. It is still
# a defect waiting for one more arrow, which is why the byte rule, not the parse, is the guarantee.
$lucky  = "function Show-Thing {`n    Write-Host 'a $arrow b'`n    Write-Host 'c $arrow d'`n}`n"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$utf8Bom   = [System.Text.UTF8Encoding]::new($true)

$noBom    = Join-Path $encTmp 'hazard-nobom.ps1'
$withBom  = Join-Path $encTmp 'hazard-bom.ps1'
$clean    = Join-Path $encTmp 'clean.ps1'
$luckyPs1 = Join-Path $encTmp 'lucky-nobom.ps1'
[System.IO.File]::WriteAllText($noBom,    $hazard, $utf8NoBom)
[System.IO.File]::WriteAllText($withBom,  $hazard, $utf8Bom)
[System.IO.File]::WriteAllText($clean,    $hazard.Replace($arrow, '->'), $utf8NoBom)
[System.IO.File]::WriteAllText($luckyPs1, $lucky,  $utf8NoBom)

$cNoBom = Test-E2ScriptEncoding -Path $noBom -Codepage 1252
Report (@($cNoBom | Where-Object { $_.Id -like 'H01-*' -and $_.Status -eq 'FAIL' }).Count -gt 0) `
       'control: UTF-8 no BOM with non-ASCII is REJECTED by the byte rule' `
       (@($cNoBom | Where-Object { $_.Id -like 'H01-*' })[0].Detail)
Report (@($cNoBom | Where-Object { $_.Id -like 'H02-*' -and $_.Status -eq 'FAIL' }).Count -gt 0) `
       'control: the same file FAILS the 5.1 parse (this is the VM defect, reproduced)' `
       (@($cNoBom | Where-Object { $_.Id -like 'H02-*' })[0].Detail)

$cBom = Test-E2ScriptEncoding -Path $withBom -Codepage 1252
Report (@($cBom | Where-Object { $_.Id -like 'H0*' -and $_.Status -eq 'FAIL' }).Count -eq 0) `
       'control: identical content WITH a BOM passes both rules' 'a BOM makes 5.1 decode as UTF-8'

$cClean = Test-E2ScriptEncoding -Path $clean -Codepage 1252
Report (@($cClean | Where-Object { $_.Id -like 'H0*' -and $_.Status -eq 'FAIL' }).Count -eq 0) `
       'control: the ASCII form of the same script passes both rules' 'arrow written as ->'

# THE REASON H01 EXISTS SEPARATELY. An even number of mis-decoded quotes pairs up and the file
# parses - so a parse-only guard would have called this file healthy.
$cLucky = Test-E2ScriptEncoding -Path $luckyPs1 -Codepage 1252
Report (@($cLucky | Where-Object { $_.Id -like 'H02-*' -and $_.Status -eq 'FAIL' }).Count -eq 0) `
       'control: an even number of arrows parses by luck (parse alone is not enough)' `
       (@($cLucky | Where-Object { $_.Id -like 'H02-*' })[0].Detail)
Report (@($cLucky | Where-Object { $_.Id -like 'H01-*' -and $_.Status -eq 'FAIL' }).Count -gt 0) `
       'control: the byte rule rejects it anyway' `
       (@($cLucky | Where-Object { $_.Id -like 'H01-*' })[0].Detail)

# The decoder must honour a BOM rather than blindly applying the codepage, or the H02 result for a
# BOM'd file would be meaningless.
$rt = ConvertFrom-E2ScriptBytes -Bytes ([System.IO.File]::ReadAllBytes($withBom)) -Codepage 1252
Report ($rt.IndexOf($arrow) -ge 0) 'control: the decoder honours a UTF-8 BOM instead of the codepage' `
       'arrow survives the round trip'

Remove-Item $encTmp -Recurse -Force -ErrorAction SilentlyContinue

# -------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '1. POSITIVE CONTROL - the real manifest must pass every check'
$real = Check $Manifest
$realFails = @($real | Where-Object { $_.Status -eq 'FAIL' })
Report ($realFails.Count -eq 0) "real manifest: $($real.Count) checks, $($realFails.Count) failures" `
       $(if ($realFails.Count) { ($realFails | ForEach-Object { "$($_.Id) $($_.Requirement): $($_.Detail)" }) -join ' | ' } else { '' })

# -------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '2. NEGATIVE CONTROLS - each break must be caught, by name'

$controls = @(
    @{ Name = 'no-allow-external'; Expect = 'M06'
       Why  = 'AllowExternalContent removed (the attempt-3 defect)'
       Mut  = { param($t) $t -replace '<uap10:AllowExternalContent>true</uap10:AllowExternalContent>', '' } }

    @{ Name = 'allow-external-false'; Expect = 'M06'
       Why  = 'AllowExternalContent set to false'
       Mut  = { param($t) $t -replace '<uap10:AllowExternalContent>true</uap10:AllowExternalContent>', '<uap10:AllowExternalContent>false</uap10:AllowExternalContent>' } }

    @{ Name = 'desktop5-extension'; Expect = 'M17'
       Why  = 'all-desktop5 prefixes (the attempt-1 defect)'
       Mut  = { param($t) $t -replace 'desktop4:Extension', 'desktop5:Extension' -replace 'desktop4:FileExplorerContextMenus', 'desktop5:FileExplorerContextMenus' } }

    @{ Name = 'publisher-mismatch'; Expect = 'M04'
       Why  = 'Publisher does not match the certificate subject'
       Mut  = { param($t) $t -replace 'Publisher="CN=NOBA-DEV-ONLY"', 'Publisher="CN=SOMEONE-ELSE"' } }

    @{ Name = 'arch-x64'; Expect = 'M05'
       Why  = 'ProcessorArchitecture set to a concrete architecture'
       Mut  = { param($t) $t -replace 'ProcessorArchitecture="neutral"', 'ProcessorArchitecture="x64"' } }

    @{ Name = 'no-unvirtualized'; Expect = 'M09-unvirtualizedResources'
       Why  = 'unvirtualizedResources capability removed'
       Mut  = { param($t) $t -replace '<rescap:Capability Name="unvirtualizedResources" />', '' } }

    @{ Name = 'no-runfulltrust'; Expect = 'M09-runFullTrust'
       Why  = 'runFullTrust capability removed'
       Mut  = { param($t) $t -replace '<rescap:Capability Name="runFullTrust" />', '' } }

    @{ Name = 'no-surrogate'; Expect = 'M14'
       Why  = 'SurrogateServer removed - an in-proc server would load into explorer.exe'
       Mut  = { param($t) $t -replace '<com:SurrogateServer[^>]*>', '<com:ServerX>' -replace '</com:SurrogateServer>', '</com:ServerX>' } }

    @{ Name = 'clsid-mismatch'; Expect = 'M15'
       Why  = 'com:Class Id differs from the CLSID the stub implements'
       Mut  = { param($t) $t -replace 'Id="17019B66-1E78-459B-92AE-63A440E7C5EA"', 'Id="00000000-0000-0000-0000-000000000000"' } }

    @{ Name = 'verb-clsid-mismatch'; Expect = 'M20'
       Why  = 'a Verb points at a different CLSID'
       Mut  = { param($t) $t -replace 'Clsid="17019B66-1E78-459B-92AE-63A440E7C5EA"', 'Clsid="00000000-0000-0000-0000-000000000000"' } }

    @{ Name = 'runas'; Expect = 'M22'
       Why  = 'RunAs attribute added - breaks the section L.4 elevation boundary'
       Mut  = { param($t) $t -replace 'ThreadingModel="STA"', 'ThreadingModel="STA" RunAs="Interactive User"' } }

    @{ Name = 'splashscreen'; Expect = 'M07'
       Why  = 'SplashScreen added - documented to break registration'
       Mut  = { param($t) $t -replace '(<uap:VisualElements[^>]*)/>', '$1><uap:SplashScreen Image="Assets\StoreLogo.png" /></uap:VisualElements>' } }

    @{ Name = 'min-version-low'; Expect = 'M08'
       Why  = 'MinVersion below the AllowExternalContent floor'
       Mut  = { param($t) $t -replace 'MinVersion="10.0.19041.0"', 'MinVersion="10.0.17763.0"' } }

    @{ Name = 'trustlevel-wrong'; Expect = 'M11-TrustLevel'
       Why  = 'uap10:TrustLevel not mediumIL'
       Mut  = { param($t) $t -replace 'uap10:TrustLevel="mediumIL"', 'uap10:TrustLevel="appContainer"' } }

    @{ Name = 'runtimebehavior-wrong'; Expect = 'M11-RuntimeBehavior'
       Why  = 'uap10:RuntimeBehavior not win32App'
       Mut  = { param($t) $t -replace 'uap10:RuntimeBehavior="win32App"', 'uap10:RuntimeBehavior="packagedClassicApp"' } }

    @{ Name = 'applistentry'; Expect = 'M12'
       Why  = 'AppListEntry not none - the harness would appear in the Start menu'
       Mut  = { param($t) $t -replace 'AppListEntry="none"', 'AppListEntry="default"' } }

    @{ Name = 'not-ignorable'; Expect = 'M02b'
       Why  = 'a used prefix dropped from IgnorableNamespaces'
       Mut  = { param($t) $t -replace 'IgnorableNamespaces="uap uap10 rescap com desktop4 desktop5"', 'IgnorableNamespaces="uap uap10 rescap com desktop5"' } }

    @{ Name = 'missing-asset'; Expect = 'M23'
       Why  = 'a referenced logo that does not exist'
       Mut  = { param($t) $t -replace 'Square44x44Logo="Assets\\Square44x44Logo.png"', 'Square44x44Logo="Assets\Nope.png"' } }
)

foreach ($c in $controls) {
    $path = New-Broken $c.Name $c.Mut
    $findings = Check $path
    $fails = @($findings | Where-Object { $_.Status -eq 'FAIL' })
    $caught = @($fails | Where-Object { $_.Id -eq $c.Expect })
    Report ($caught.Count -gt 0) "$($c.Why)  ->  expected $($c.Expect)" `
           $(if ($caught.Count) { $caught[0].Detail } else { "NOT CAUGHT. failures were: $(@($fails | ForEach-Object { $_.Id }) -join ', ')" })
}

# -------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '3. EXTERNAL-LOCATION CHECK (M24)'
$fakeExt = Join-Path $tmp 'ext'
New-Item -ItemType Directory -Path $fakeExt -Force | Out-Null
$missing = Test-E2Manifest -Path $Manifest -ExpectedPublisher $Publisher -ExpectedClsid $Clsid -ExternalLocation $fakeExt
$m24miss = @($missing | Where-Object { $_.Id -eq 'M24' -and $_.Status -eq 'FAIL' })
Report ($m24miss.Count -gt 0) 'empty external location is caught' $(if ($m24miss.Count) { $m24miss[0].Detail } else { 'NOT CAUGHT' })

Set-Content -Path (Join-Path $fakeExt 'noba_e2_host.exe') -Value 'x'
Set-Content -Path (Join-Path $fakeExt 'noba_e2_stub.dll') -Value 'x'
$present = Test-E2Manifest -Path $Manifest -ExpectedPublisher $Publisher -ExpectedClsid $Clsid -ExternalLocation $fakeExt
$m24ok = @($present | Where-Object { $_.Id -eq 'M24' -and $_.Status -eq 'PASS' })
Report ($m24ok.Count -gt 0) 'populated external location passes' $(if ($m24ok.Count) { $m24ok[0].Detail } else { 'DID NOT PASS' })

# -------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '4. SIDELOADING GATE - must return a finding, never throw, on any platform'
$side = Test-E2Sideloading
Report ($side.Count -eq 1 -and $side[0].Id -eq 'S01') 'sideloading check returns exactly one S01 finding' `
       "$($side[0].Status): $($side[0].Detail)"
# On a machine with no such registry (or no registry at all), every value reads as unset, which must
# be reported FAIL - never PASS. Absence of a permission is not permission.
$onWin = ($PSVersionTable.PSEdition -eq 'Desktop') -or
         ($PSVersionTable.PSObject.Properties.Name -contains 'Platform' -and $PSVersionTable.Platform -eq 'Win32NT')
if (-not $onWin) {
    Report ($side[0].Status -eq 'FAIL') 'no registry -> FAIL, not PASS (absence is not permission)' $side[0].Detail
}

# EVERY BRANCH OF THE DECISION, including the one that can only occur on a machine that has the
# policy. This is why the decision was split from the registry read: the BLOCKED branch is the one
# that matters and is unreachable here otherwise.
$branches = @(
    @{ Why='policy 0 blocks, even with Developer Mode on'; P=0;    U=1;    D=1;    State='BLOCKED BY POLICY'; Ok=$false }
    @{ Why='policy 0 blocks, everything else unset';       P=0;    U=$null;D=$null;State='BLOCKED BY POLICY'; Ok=$false }
    @{ Why='sideloading allowed by AppModelUnlock';        P=$null;U=1;    D=$null;State='PERMITTED';         Ok=$true  }
    @{ Why='Developer Mode allows it';                     P=$null;U=$null;D=1;    State='PERMITTED';         Ok=$true  }
    @{ Why='policy 1 allows it explicitly';                P=1;    U=$null;D=$null;State='PERMITTED';         Ok=$true  }
    @{ Why='everything unset';                             P=$null;U=$null;D=$null;State='NOT ENABLED';       Ok=$false }
    @{ Why='unlock values present but 0';                  P=$null;U=0;    D=0;    State='NOT ENABLED';       Ok=$false }
)
foreach ($b in $branches) {
    $r = Resolve-E2SideloadingState -PolicyTrusted $b.P -UnlockTrusted $b.U -UnlockDev $b.D
    Report (($r.EffectiveState -eq $b.State) -and ($r.Permitted -eq $b.Ok)) `
           "branch: $($b.Why)  ->  $($b.State)" `
           "got '$($r.EffectiveState)' Permitted=$($r.Permitted); action: $($r.HarnessAction)"
}

# -------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '5. CERTIFICATE HELPERS - unknown must never read as clean'
$n = Get-E2CertCount -StoreName 'TrustedPeople' -Location 'LocalMachine' -Subject $Publisher
# $IsWindows does not exist in Windows PowerShell 5.1, so it is not consulted. PSEdition and the
# platform property do exist in both editions.
$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or
             ($PSVersionTable.PSObject.Properties.Name -contains 'Platform' -and $PSVersionTable.Platform -eq 'Win32NT')
if ($onWindows) {
    Report ($n -ge 0) 'machine store readable on Windows' "count=$n"
} else {
    Report ($n -eq -1) 'unreadable store returns -1 (unknown), not 0' "count=$n"
}
$r = Remove-E2Certs -StoreName 'TrustedPeople' -Location 'LocalMachine' -Subject $Publisher
Report ($null -ne $r) 'Remove-E2Certs returns instead of throwing' "Opened=$($r.Opened) Removed=$($r.Removed) Failed=$($r.Failed)"

# -------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '6. CRITERION 3 - UNMEASURED must never read as REFUTED'

# The 2026-08-31 run reported criterion 3 as "no dllhost.exe has the stub loaded - either never
# activated, or it has already exited" when the truth was that the surrogate had exited before a
# single post-hoc sample, and when an unreadable module list would have produced the same sentence.
# Every branch below was unreachable on Windows without staging a process, which is exactly why the
# decision was split out of the measurement.
$me = 'VM\tester'
$cap = { param($clr, $fxr, $stub, $owner)
         [pscustomobject]@{ Pid=4242; Name='dllhost'; Owner=$owner; HasClr=$clr; HasFxr=$fxr; HasStub=$stub } }

# A is whether the operator confirmed the right-click happened inside the window. An empty sample
# from a window nobody acted in measures nothing - that is the 05:00 run's defect, in one column.
$surrogateCases = @(
    @{ Why='captured, CLR + hostfxr, right owner';        C=(& $cap $true  $true  $true  $me);          H=$true;  U=0; A=$true;  Expect='PASS' }
    @{ Why='captured, but running as someone else';       C=(& $cap $true  $true  $true  'VM\someone'); H=$true;  U=0; A=$true;  Expect='FAIL' }
    @{ Why='captured, but no CLR in it';                  C=(& $cap $false $true  $true  $me);          H=$true;  U=0; A=$true;  Expect='FAIL' }
    @{ Why='captured, but no hostfxr in it';              C=(& $cap $true  $false $true  $me);          H=$true;  U=0; A=$true;  Expect='FAIL' }
    @{ Why='a capture stands even if nobody confirmed';   C=(& $cap $true  $true  $true  $me);          H=$true;  U=0; A=$false; Expect='PASS' }
    @{ Why='NOT captured, module lists unreadable';       C=$null; H=$true;  U=3; A=$true;  Expect='UNKNOWN' }
    @{ Why='NOT captured, CONFIRMED, hosts readable';     C=$null; H=$true;  U=0; A=$true;  Expect='FAIL' }
    @{ Why='NOT captured, CONFIRMED, no host ever';       C=$null; H=$false; U=0; A=$true;  Expect='FAIL' }
    @{ Why='unreadable wins over "hosts were present"';   C=$null; H=$true;  U=1; A=$true;  Expect='UNKNOWN' }
    @{ Why='THE 05:00 RUN: window expired unconfirmed';   C=$null; H=$true;  U=0; A=$false; Expect='UNKNOWN' }
    @{ Why='unconfirmed outranks "no host ever existed"'; C=$null; H=$false; U=0; A=$false; Expect='UNKNOWN' }
)
foreach ($s in $surrogateCases) {
    $v = Resolve-E2SurrogateVerdict -Capture $s.C -SawAnyHostProcess $s.H -UnreadableSamples $s.U `
                                    -ExpectedUser $me -ActivationConfirmed $s.A
    Report ($v.Status -eq $s.Expect) "criterion 3: $($s.Why)  ->  $($s.Expect)" "got '$($v.Status)': $($v.Detail)"
}

# The specific regression: an unreadable sample must not produce the sentence that claims a finding.
$u = Resolve-E2SurrogateVerdict -Capture $null -SawAnyHostProcess $true -UnreadableSamples 2 -ExpectedUser $me -ActivationConfirmed $true
Report ($u.Detail -notmatch 'never|exited') 'UNKNOWN does not claim "never activated" or "already exited"' $u.Detail
Report ($u.Detail -match 'NOT evidence') 'UNKNOWN says plainly that absence was not established' $u.Detail

# THE 2026-08-31 05:00 REGRESSION, asserted on the text itself. The old code produced
# "candidate host processes were present and readable throughout the window, and none ever had the
# stub loaded" for an expired, unconfirmed window - a refutation the harness had not earned.
$w = Resolve-E2SurrogateVerdict -Capture $null -SawAnyHostProcess $true -UnreadableSamples 0 -ExpectedUser $me -ActivationConfirmed $false
Report ($w.Status -eq 'UNKNOWN') 'expired unconfirmed window is UNMEASURED, not refuted' $w.Detail
Report ($w.Detail -notmatch 'none ever had') 'it does not say "none ever had the stub loaded"' $w.Detail
# ... and the same inputs WITH confirmation must still be a real FAIL. The fix must not make
# criterion 3 unfailable.
$w2 = Resolve-E2SurrogateVerdict -Capture $null -SawAnyHostProcess $true -UnreadableSamples 0 -ExpectedUser $me -ActivationConfirmed $true
Report ($w2.Status -eq 'FAIL') 'criterion 3 can still FAIL once activation is confirmed' $w2.Detail

# THE 10:40 RUN'S TRANSCRIPT DEFECT. It PASSED 3/3 and its cleanup section printed
# "Criterion 3 stays UNMEASURED, not refuted" about the very process criterion 3 had captured. The
# note asserted a conclusion it had never checked, inside the document that IS the evidence.
$capOf = { param($p) [pscustomobject]@{ Pid = $p } }
$lateCases = @(
    @{ Why='cleanup finds the process criterion 3 captured'; P=10460; C=(& $capOf 10460); W=$false; Expect='CONFIRMS' }
    @{ Why='cleanup finds a DIFFERENT surrogate';            P=777;   C=(& $capOf 10460); W=$true;  Expect='OTHER'    }
    @{ Why='nothing captured, started OUTSIDE the window';   P=10460; C=$null;            W=$false; Expect='MISTIMED' }
    @{ Why='nothing captured, started INSIDE the window';    P=10460; C=$null;            W=$true;  Expect='MISSED'   }
    @{ Why='nothing captured, start time unreadable';        P=10460; C=$null;            W=$null;  Expect='UNKNOWN'  }
)
foreach ($l in $lateCases) {
    $n = Resolve-E2LateDetectionNote -SurrogatePid $l.P -Capture $l.C -StartedInWindow $l.W
    Report ($n.Status -eq $l.Expect) "late detection: $($l.Why)  ->  $($l.Expect)" "got '$($n.Status)': $($n.Text)"
}
# The regression, asserted on the text: a run whose criterion 3 captured this PID must never be told
# its criterion 3 was unmeasured.
$nc = Resolve-E2LateDetectionNote -SurrogatePid 10460 -Capture (& $capOf 10460) -StartedInWindow $false
Report ($nc.Text -notmatch 'UNMEASURED|not refuted') 'a confirming late detection never says UNMEASURED' $nc.Text
# ... and a genuinely missed process must be named as a HARNESS defect, not excused as mistiming.
$nm = Resolve-E2LateDetectionNote -SurrogatePid 10460 -Capture $null -StartedInWindow $true
Report ($nm.Text -match 'harness defect') 'a process that started inside the window is a harness defect, not mistiming' $nm.Text

# -------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '7. CALL SITES - every command a harness script calls must exist'

# WHY. Criterion 3's rewrite renamed Get-E2Surrogates to Find-E2Surrogate and left two call sites
# behind - one in the residue scan, one in the cleanup. This suite passed 50/50 anyway, because it
# exercises the MODULE and never loads run-e2.ps1. The run then died at section 1 with
# "The term 'Get-E2Surrogates' is not recognized", after a certificate and a package had already
# been created on previous runs - the most expensive place to discover a typo.
#
# So: parse each script and resolve every command it calls against the functions it defines, the
# functions E2Preflight exports, and what is actually installed. Anything left over is a call to
# something that does not exist.
#
# The allowlist below is the harness's Windows-only surface. It is deliberately explicit: a new entry
# has to be added by hand, which is a review point rather than a silent pass.
$WindowsOnly = @(
    'Add-AppxPackage', 'Export-Certificate', 'Get-AppxPackage', 'Get-CimInstance',
    'Get-MpComputerStatus', 'Get-WinEvent', 'Invoke-CimMethod', 'New-SelfSignedCertificate',
    'Remove-AppxPackage',
    'cl.exe', 'cmd.exe', 'powercfg'
)

function Get-UnresolvedCommands {
    param([Parameter(Mandatory)][string]$Path, [string[]]$Known = @(), [string[]]$Allow = @())
    $tok = $null; $er = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tok, [ref]$er)
    if (@($er).Count -gt 0) { return @("<parse error: $(@($er)[0].Message)>") }
    $defined = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) |
                 ForEach-Object { $_.Name })
    $calls = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) |
               ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Sort-Object -Unique)
    $out = @()
    foreach ($c in $calls) {
        if ($defined -contains $c) { continue }
        if ($Known   -contains $c) { continue }
        if ($Allow   -contains $c) { continue }
        if (Get-Command $c -ErrorAction SilentlyContinue) { continue }
        $out += $c
    }
    return ,$out
}

$exported = @(Get-Command -Module E2Preflight -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
Report ($exported.Count -gt 0) 'E2Preflight exports are enumerable' "$($exported.Count) function(s)"

foreach ($script in @(Get-ChildItem -Path $harnessRoot -Recurse -File -Include '*.ps1', '*.psm1' -ErrorAction SilentlyContinue |
                      Sort-Object FullName)) {
    $bad = Get-UnresolvedCommands -Path $script.FullName -Known $exported -Allow $WindowsOnly
    Report ($bad.Count -eq 0) "call sites resolve: $($script.Name)" $(
        if ($bad.Count) { "UNRESOLVED: $($bad -join ', ')" } else { 'every command resolves' })
}

# The control. A check that has never been seen to fail is not a check - and this one was written
# because the previous suite could not fail on exactly this defect.
$callTmp = Join-Path ([IO.Path]::GetTempPath()) ("e2call-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $callTmp -Force | Out-Null
$brokenPs1 = Join-Path $callTmp 'renamed-callsite.ps1'
Set-Content -Path $brokenPs1 -Encoding ASCII -Value @'
function Find-E2Surrogate { 'the new name' }
$surr = Get-E2Surrogates      # the old name, left behind by a rename
Write-Host $surr
'@
$caught = Get-UnresolvedCommands -Path $brokenPs1 -Known $exported -Allow $WindowsOnly
Report (($caught -contains 'Get-E2Surrogates')) `
       'control: a call site left behind by a rename is caught, by name' "unresolved: $($caught -join ', ')"

$fixedPs1 = Join-Path $callTmp 'fixed-callsite.ps1'
Set-Content -Path $fixedPs1 -Encoding ASCII -Value @'
function Find-E2Surrogate { 'the new name' }
$surr = Find-E2Surrogate
Write-Host $surr
'@
$clean = Get-UnresolvedCommands -Path $fixedPs1 -Known $exported -Allow $WindowsOnly
Report ($clean.Count -eq 0) 'control: the corrected version passes' "unresolved: $($clean.Count)"
Remove-Item $callTmp -Recurse -Force -ErrorAction SilentlyContinue

# -------------------------------------------------------------------------------------------------
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ('=' * 78)
# The total is platform-dependent by one: the "no registry -> FAIL, not PASS" control in section 4
# can only run where there is no registry to read. Printing which platform was counted stops a
# correct Windows run from looking like an incomplete transfer.
$plat = if ($onWin) { 'windows' } else { 'non-windows' }
if ($fail -eq 0) {
    Write-Host "LOCAL CONTROL SUITE: PASS - $pass checks, 0 failures ($plat)" -ForegroundColor Green
} else {
    Write-Host "LOCAL CONTROL SUITE: FAIL - $pass passed, $fail failed ($plat)" -ForegroundColor Red
}
Write-Host ('=' * 78)
exit $(if ($fail -eq 0) { 0 } else { 1 })
