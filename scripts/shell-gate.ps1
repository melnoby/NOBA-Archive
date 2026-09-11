#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 Phase 4 — the Windows classic runtime gate, built around SHELL-W3…W10.
.DESCRIPTION
    This is the gate for **surface B**: the classic ContextMenuHandlers registration, which on
    Windows 11 appears under "Show more options". It is NOT the gate for surface A, the modern menu
    above "Show more options" — that needs ADR-0006's signed sparse package and IExplorerCommand
    server, neither of which exists. SHELL-W1 and SHELL-W2 are therefore out of scope here and are
    reported as NOT IMPLEMENTED rather than skipped, because a skip reads like something that could
    have passed.

    THE PREVIOUS VERSION OF THIS GATE ASKED 34 QUESTIONS AND WAS NEVER COMPLETED. That is the defect
    this rewrite addresses. Most of what it asked a person to eyeball is not actually a visual fact:

      "no Compress to RAR"        is which keys exist under SystemFileAssociations\.rar
      "BZIP2 and XZ nowhere"      is a registry scan
      "GZIP not offered for many" is a refusal with an exit code
      "uninstall leaves nothing"  is a registry scan
      "reinstall does not double" is a key count

    So those now run by themselves and record evidence. What is left for a person is only what a
    person can actually see: whether Explorer draws the menu at all, where it draws it, and what the
    submenu reads. Nine questions, not thirty-four.

    WHAT THIS GATE DOES AND DOES NOT PROVE.

    It executes the command string **the registry actually contains**, substituting %1 and %* the way
    the shell documents, and then checks the artefact on disk. That proves the registered command is
    correct and that the action model behind it works.

    It does NOT prove that Explorer performs the same substitution. Whether %* arrives as separate
    arguments under MultiSelectModel=Player is SHELL-L8 and is unobserved; step M7 is the only thing
    that can settle it, and it is a manual step for exactly that reason. A green automated W4 with a
    NOT RUN on M7 means "the contract is right and nobody has watched Windows honour it".

.PARAMETER SkipBuild
    Use what is already built.

.PARAMETER SkipRegister
    Do not touch the registry. Use when the menu is already registered.

.PARAMETER Unattended
    Run only the automated checks. Every manual step is recorded NOT RUN, and the verdict can never
    be PASS. Use this to get the machine-checkable half quickly; it is not a way to finish the gate.

.EXAMPLE
    .\scripts\shell-gate.ps1
.EXAMPLE
    .\scripts\shell-gate.ps1 -Unattended
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipRegister,
    [switch]$Unattended,
    [string]$ExpectedBuildStamp = '2026-08-27-drop-08-phase-3-directory-identity'
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

$sep = [System.IO.Path]::DirectorySeparatorChar
function Local {
    param([string]$Path)
    return $Path.Replace('\', $sep).Replace('/', $sep)
}

$sandbox = Join-Path $root (Local "artifacts\shell-gate-$stamp")
$log = Join-Path $root (Local "artifacts\shell-gate-$stamp.txt")
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$script:Results = New-Object System.Collections.ArrayList

function Write-Log {
    param([string]$Text = '')
    Write-Host $Text
    Add-Content -LiteralPath $log -Value $Text -Encoding UTF8
}

<#
.SYNOPSIS
    Records one criterion's outcome. Verdict is PASS, FAIL, NOT RUN or NOT IMPLEMENTED.
.DESCRIPTION
    NOT IMPLEMENTED is distinct from NOT RUN on purpose. "Not run" is work somebody still owes;
    "not implemented" is a surface that does not exist yet, and calling it a skip would suggest it
    might have passed if only somebody had looked.
#>
function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Verdict,
        [Parameter(Mandatory)][string]$What,
        [string]$Evidence = ''
    )

    [void]$script:Results.Add([pscustomobject]@{ Id = $Id; Verdict = $Verdict; What = $What })

    Write-Log ("  {0,-15} {1,-9} {2}" -f $Verdict, $Id, $What)
    if ($Evidence) {
        foreach ($line in ($Evidence -split "`n")) { Write-Log ("                                  {0}" -f $line.TrimEnd()) }
    }
}

<#
.SYNOPSIS
    An automated check: run a scriptblock that returns $true/$false and a message.
#>
function Check {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$What,
        [Parameter(Mandatory)][scriptblock]$Test
    )

    try {
        $emitted = @(& $Test)
    }
    catch {
        Add-Result -Id $Id -Verdict 'FAIL' -What $What -Evidence "the check itself threw: $($_.Exception.Message)"
        return
    }

    # A check that emitted more than its verdict is a check that has been edited carelessly; taking
    # the last object quietly would let a stray Write-Output decide the outcome.
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
    A manual step. Only for things a person can see and a script cannot.
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

# ---------------------------------------------------------------------------
# Registry access, literal. See shell-register.ps1 for why the provider is not used: one of the
# scope keys is literally named "*" and every provider cmdlet reads that as a wildcard.
# ---------------------------------------------------------------------------
function Get-NobaValue {
    param([Parameter(Mandatory)][string]$SubKey, [Parameter(Mandatory)][AllowEmptyString()][string]$Name)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey)
    if ($null -eq $key) { return $null }
    try { return $key.GetValue($Name) } finally { $key.Dispose() }
}

function Get-NobaSubKeyNames {
    param([Parameter(Mandatory)][string]$SubKey)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey)
    if ($null -eq $key) { return @() }
    try { return @($key.GetSubKeyNames()) } finally { $key.Dispose() }
}

function Test-NobaKey {
    param([Parameter(Mandatory)][string]$SubKey)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey)
    if ($null -eq $key) { return $false }
    $key.Dispose()
    return $true
}

<#
.SYNOPSIS
    Splits a registry command string into an executable and an argument array, substituting the
    selection the way the shell documents.
.DESCRIPTION
    This is the heart of the gate, and its limits are the gate's limits. It reproduces the
    substitution Windows is documented to perform:

        "%1"  the invoked item, one argument
        %*    every selected item, one argument each

    Reproducing it is not the same as observing it. If Explorer does something else with %* - passes
    one joined string, passes nothing - this function will not notice, because it is this script
    doing the expanding. SHELL-L8, and step M7 is what settles it.

    Tokenising is quote-aware so that an install path containing a space stays one argument. There is
    no cmd.exe anywhere in this: the result is an argv array handed to the call operator.
#>
function ConvertTo-NobaInvocation {
    param(
        [Parameter(Mandatory)][string]$CommandString,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Selection
    )

    $tokens = New-Object System.Collections.ArrayList
    $current = New-Object System.Text.StringBuilder
    $inQuotes = $false
    $started = $false

    foreach ($ch in $CommandString.ToCharArray()) {
        if ($ch -eq '"') { $inQuotes = -not $inQuotes; $started = $true; continue }

        if (-not $inQuotes -and [char]::IsWhiteSpace($ch)) {
            if ($started) { [void]$tokens.Add($current.ToString()); [void]$current.Clear(); $started = $false }
            continue
        }

        [void]$current.Append($ch)
        $started = $true
    }
    if ($started) { [void]$tokens.Add($current.ToString()) }

    if ($tokens.Count -eq 0) { return $null }

    $exe = $tokens[0]
    $argv = New-Object System.Collections.ArrayList

    for ($i = 1; $i -lt $tokens.Count; $i++) {
        switch -regex ($tokens[$i]) {
            '^%\*$'   { foreach ($p in $Selection) { [void]$argv.Add($p) }; break }
            '^%[1L]$' { if ($Selection.Count -gt 0) { [void]$argv.Add($Selection[0]) }; break }
            default   { [void]$argv.Add($tokens[$i]) }
        }
    }

    return [pscustomobject]@{ Exe = $exe; Args = @($argv) }
}

<#
.SYNOPSIS
    Runs one registered entry exactly as its registry key says, against a selection.
.DESCRIPTION
    The command string is READ FROM THE REGISTRY, not rebuilt from the manifest. That is deliberate:
    a gate that reconstructs what it expects to find would agree with itself and prove nothing. If
    registration wrote something other than what the manifest says, this runs the wrong thing and the
    artefact check fails - which is the point.
#>
function Invoke-NobaEntry {
    param(
        [Parameter(Mandatory)][string]$Scope,
        [Parameter(Mandatory)][string]$EntryId,
        [Parameter(Mandatory)][string[]]$Selection
    )

    $key = "Software\Classes\$Scope\shell\$($script:MenuId)\shell\$EntryId\command"
    $command = Get-NobaValue -SubKey $key -Name ''

    if ([string]::IsNullOrWhiteSpace($command)) {
        return [pscustomobject]@{ Ran = $false; ExitCode = $null; Command = $null
                                  Reason = "no command registered at HKCU\$key" }
    }

    $invocation = ConvertTo-NobaInvocation -CommandString $command -Selection $Selection
    if ($null -eq $invocation) {
        return [pscustomobject]@{ Ran = $false; ExitCode = $null; Command = $command
                                  Reason = 'the registered command string is empty' }
    }

    $stdout = & $invocation.Exe @($invocation.Args) 2>&1 | Out-String
    $code = $LASTEXITCODE

    return [pscustomobject]@{
        Ran = $true; ExitCode = $code; Command = $command
        Output = $stdout.TrimEnd(); Reason = ''
        Rendered = ("{0} {1}" -f $invocation.Exe, ($invocation.Args -join ' '))
    }
}

Write-Log ('=' * 78)
Write-Log 'NOBA Archive - DROP 08 PHASE 4: WINDOWS CLASSIC RUNTIME GATE (surface B)'
Write-Log "Started : $(Get-Date -Format 'u')"
Write-Log "Sandbox : $sandbox"
Write-Log ('=' * 78)
Write-Log ''
Write-Log 'This gate covers SHELL-W3...W10 on the CLASSIC surface only.'
Write-Log 'SHELL-W1 and SHELL-W2 are the Windows 11 modern menu and are NOT IMPLEMENTED.'

# ---------------------------------------------------------------------------
# The tree under test.
# ---------------------------------------------------------------------------
$stampFile = Join-Path $root (Local 'src\NOBA.Archive.Cli\BuildStamp.cs')
$sourceStamp = $null
if (Test-Path -LiteralPath $stampFile) {
    $match = Select-String -LiteralPath $stampFile -Pattern 'Value\s*=\s*"([^"]+)"'
    if ($match) { $sourceStamp = $match.Matches[0].Groups[1].Value }
}

Write-Log ''
Write-Log "Source drop   : $sourceStamp"
Write-Log "Expected drop : $ExpectedBuildStamp"

if ($sourceStamp -ne $ExpectedBuildStamp) {
    Write-Log ''
    Write-Log '*** WRONG SOURCE TREE. The gate did not run, and nothing below is a result.'
    Write-Log '    Pass -ExpectedBuildStamp if you meant to gate a different drop.'
    exit 1
}

# ---------------------------------------------------------------------------
# Build.
# ---------------------------------------------------------------------------
$appDir = Join-Path $root (Local 'src\NOBA.Archive.App\bin\x64\Debug\net10.0-windows10.0.19041.0')
$appExe = Join-Path $appDir 'NOBA.Archive.App.exe'
$cliOut = Join-Path $root (Local 'src\NOBA.Archive.Cli\bin\Release\net10.0')

if (-not $SkipBuild) {
    Write-Log ''
    Write-Log 'Building the engine solution...'
    $engine = & dotnet build (Join-Path $root 'NOBA.Archive.sln') -c Release --nologo -v:q 2>&1 | Out-String
    Write-Log $engine.TrimEnd()
    if ($LASTEXITCODE -ne 0) { Write-Log 'ENGINE BUILD FAILED - the gate did not run.'; exit 1 }

    Write-Log ''
    Write-Log 'Building the WinUI shell...'
    $shell = & dotnet build (Join-Path $root 'NOBA.Archive.App.sln') -c Debug -p:Platform=x64 --nologo -v:q 2>&1 | Out-String
    Write-Log $shell.TrimEnd()
    if ($LASTEXITCODE -ne 0) { Write-Log 'WINUI BUILD FAILED - the gate did not run. Report the error and stop.'; exit 1 }
}

if (-not (Test-Path -LiteralPath $appExe)) {
    Write-Log "The application is not at $appExe. The gate did not run."
    exit 1
}

$cliExe = Join-Path $appDir 'noba-ar.exe'
Write-Log ''
Write-Log 'Placing noba-ar.exe beside the application...'
foreach ($file in Get-ChildItem -LiteralPath $cliOut -File -ErrorAction SilentlyContinue) {
    Copy-Item -LiteralPath $file.FullName -Destination $appDir -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path -LiteralPath $cliExe)) {
    Write-Log "  could not place noba-ar.exe at $cliExe - the headless verbs will not work."
    exit 1
}
Write-Log "  $cliExe"

# ---------------------------------------------------------------------------
# The manifest, generated from code.
# ---------------------------------------------------------------------------
$manifestPath = Join-Path $root (Local 'scripts\shell-verbs.json')
Write-Log ''
Write-Log 'Generating the registration manifest from NOBA.Archive.Core...'
$manifestJson = & $cliExe shell-manifest | Out-String
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

$spec = $manifestJson | ConvertFrom-Json
$script:MenuId = $spec.menuId
Write-Log ("  {0} entries, menu id {1}" -f $spec.entries.Count, $script:MenuId)

$registerScript = Join-Path $root (Local 'scripts\shell-register.ps1')

function Invoke-Registration {
    param([switch]$Remove)

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $registerScript)
    if ($Remove) { $arguments += '-Remove' } else { $arguments += @('-InstallPath', $appDir) }

    & powershell @arguments 2>&1 | ForEach-Object { Write-Log ("  {0}" -f $_) }
    return $LASTEXITCODE
}

if (-not $SkipRegister) {
    Write-Log ''
    Write-Log 'Registering the Explorer menu (interim classic handler)...'
    if ((Invoke-Registration) -ne 0) {
        Write-Log ''
        Write-Log 'REGISTRATION FAILED. The gate did not run.'
        Write-Log 'Run .\scripts\shell-register-trace.ps1 and send its output.'
        exit 1
    }

    Write-Log ''
    Write-Log 'Restarting Explorer so the new keys are read...'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
        Start-Sleep -Seconds 2
    }
}

# ---------------------------------------------------------------------------
# Material to act on.
# ---------------------------------------------------------------------------
$payload = Join-Path $sandbox 'right-click-me'
New-Item -ItemType Directory -Force -Path (Join-Path $payload (Local 'ProjectA\nested')) | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'مستندات') | Out-Null

# Two folders differing only after a dot. This is SHELL-075: they used to propose the same archive
# name, and the second was refused as already existing.
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'Project.Alpha') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'Project.Beta') | Out-Null

foreach ($f in @(
    @{ Path = 'report.pdf';               Text = 'report' }
    @{ Path = 'photo.jpg';                Text = 'photo' }
    @{ Path = 'with spaces.txt';          Text = 'spaces' }
    @{ Path = 'ProjectA\nested\leaf.txt'; Text = 'deep' }
    @{ Path = 'Project.Alpha\a.txt';      Text = 'alpha' }
    @{ Path = 'Project.Beta\b.txt';       Text = 'beta' }
    @{ Path = 'مستندات\تقرير سنوي.txt';   Text = 'محتوى عربي' }
    @{ Path = '日本語のファイル.txt';       Text = 'japanese' }
)) {
    Set-Content -LiteralPath (Join-Path $payload (Local $f.Path)) -Value $f.Text -Encoding UTF8
}

$sample = Join-Path $payload 'sample.7z'
& $cliExe create $sample (Join-Path $payload 'ProjectA') --format 7z | Out-Null
if (-not (Test-Path -LiteralPath $sample)) {
    Write-Log ''
    Write-Log 'Could not build a sample 7Z. The gate did not run.'
    exit 1
}

$before = Get-ChildItem -LiteralPath $payload -Recurse -File |
          Sort-Object FullName |
          ForEach-Object { "{0}|{1}" -f $_.FullName.Substring($payload.Length), $_.Length }

Write-Log ''
Write-Log ("Material laid out in {0} ({1} files)" -f $payload, $before.Count)

# The scope keys, as the manifest assigned them.
$archiveScope = 'SystemFileAssociations\.7z'
$fileScope = '*'
$folderScope = 'Directory'

# ===========================================================================
Write-Log ''
Write-Log ('=' * 78)
Write-Log 'AUTOMATED CHECKS - the registry is read, and the registered command is what runs'
Write-Log ('=' * 78)
Write-Log ''

# --- SHELL-W3 : every action invokes the same model and produces the right result --------------
Check -Id 'SHELL-W3a' -What 'Compress to 7Z on one file produces report.7z beside it' -Test {
    $target = Join-Path $payload 'report.7z'
    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    $r = Invoke-NobaEntry -Scope $fileScope -EntryId 'noba.compress.7z' -Selection @((Join-Path $payload 'report.pdf'))
    $ok = $r.Ran -and $r.ExitCode -eq 0 -and (Test-Path -LiteralPath $target)
    @{ Ok = $ok; Evidence = "registry command: $($r.Command)`nran: $($r.Rendered)`nexit $($r.ExitCode); report.7z exists: $(Test-Path -LiteralPath $target)" }
}

Check -Id 'SHELL-W3b' -What 'Compressing copies; the original is untouched' -Test {
    $original = Join-Path $payload 'report.pdf'
    $ok = (Test-Path -LiteralPath $original) -and ((Get-Item -LiteralPath $original).Length -gt 0)
    @{ Ok = $ok; Evidence = "report.pdf present: $(Test-Path -LiteralPath $original)" }
}

Check -Id 'SHELL-W3c' -What 'Extract here lands beside the archive, not in the working directory' -Test {
    $probe = Join-Path $sandbox 'extract-here'
    New-Item -ItemType Directory -Force -Path $probe | Out-Null
    $movedArchive = Join-Path $probe 'sample.7z'
    Copy-Item -LiteralPath $sample -Destination $movedArchive -Force

    $r = Invoke-NobaEntry -Scope $archiveScope -EntryId 'noba.extracthere' -Selection @($movedArchive)
    $landed = @(Get-ChildItem -LiteralPath $probe -Recurse -File -ErrorAction SilentlyContinue).Count -gt 1
    @{ Ok = ($r.Ran -and $r.ExitCode -eq 0 -and $landed)
       Evidence = "registry command: $($r.Command)`nexit $($r.ExitCode); files under the archive's own folder: $(@(Get-ChildItem -LiteralPath $probe -Recurse -File -ErrorAction SilentlyContinue).Count)" }
}

Check -Id 'SHELL-W3d' -What 'Test archive runs headless and reports success' -Test {
    $r = Invoke-NobaEntry -Scope $archiveScope -EntryId 'noba.test' -Selection @($sample)
    @{ Ok = ($r.Ran -and $r.ExitCode -eq 0); Evidence = "exit $($r.ExitCode)`n$($r.Output)" }
}

Check -Id 'SHELL-W3e' -What 'A single folder named Project.Alpha proposes Project.Alpha.zip (SHELL-075)' -Test {
    $r = Invoke-NobaEntry -Scope $folderScope -EntryId 'noba.compress.zip' -Selection @((Join-Path $payload 'Project.Alpha'))
    $alpha = Test-Path -LiteralPath (Join-Path $payload 'Project.Alpha.zip')
    $collided = Test-Path -LiteralPath (Join-Path $payload 'Project.zip')
    @{ Ok = ($r.ExitCode -eq 0 -and $alpha -and -not $collided)
       Evidence = "Project.Alpha.zip: $alpha; the old colliding Project.zip: $collided" }
}

Check -Id 'SHELL-W3f' -What 'Project.Beta does not collide with Project.Alpha' -Test {
    $r = Invoke-NobaEntry -Scope $folderScope -EntryId 'noba.compress.zip' -Selection @((Join-Path $payload 'Project.Beta'))
    $beta = Test-Path -LiteralPath (Join-Path $payload 'Project.Beta.zip')
    @{ Ok = ($r.ExitCode -eq 0 -and $beta)
       Evidence = "Project.Beta.zip: $beta; exit $($r.ExitCode) (a collision would refuse with 'already exists')" }
}

# --- SHELL-W4 : multi-selection produces ONE logical request ------------------------------------
Check -Id 'SHELL-W4' -What 'Three selected files produce ONE archive containing all three' -Test {
    $probe = Join-Path $sandbox 'multi'
    New-Item -ItemType Directory -Force -Path $probe | Out-Null
    $picks = @('a.txt', 'b.txt', 'c.txt') | ForEach-Object {
        $p = Join-Path $probe $_
        Set-Content -LiteralPath $p -Value $_ -Encoding UTF8
        $p
    }

    $r = Invoke-NobaEntry -Scope $fileScope -EntryId 'noba.compress.zip' -Selection $picks
    $produced = @(Get-ChildItem -LiteralPath $probe -Filter '*.zip' -File -ErrorAction SilentlyContinue)

    # Parsed, not pattern-counted: the CLI reports the entry count itself, so this reads the
    # product's own answer instead of guessing from the shape of its JSON.
    $members = 0
    if ($produced.Count -eq 1) {
        $listing = & $cliExe list $produced[0].FullName --json | Out-String
        $members = ($listing | ConvertFrom-Json).count
    }

    @{ Ok = ($r.ExitCode -eq 0 -and $produced.Count -eq 1 -and $members -ge 3)
       Evidence = "archives produced: $($produced.Count) (must be 1, never 3); entries inside: $members`nNOTE: this script expanded %*. Whether EXPLORER expands it the same way is step M7 (SHELL-L8)." }
}

# --- SHELL-W5 : RAR is readable and never creatable ---------------------------------------------
Check -Id 'SHELL-W5' -What 'RAR offers open/test/extract and no Compress to RAR is registered anywhere' -Test {
    $rarScope = 'SystemFileAssociations\.rar'
    $onRar = @(Get-NobaSubKeyNames -SubKey "Software\Classes\$rarScope\shell\$($script:MenuId)\shell")

    $expected = @('noba.open', 'noba.test', 'noba.extracthere', 'noba.extractto')
    $missing = @($expected | Where-Object { $onRar -notcontains $_ })

    # And no Compress to RAR under ANY scope, not merely under .rar.
    $rarCompress = @()
    foreach ($scope in @($fileScope, $folderScope, $rarScope, $archiveScope)) {
        $rarCompress += @(Get-NobaSubKeyNames -SubKey "Software\Classes\$scope\shell\$($script:MenuId)\shell" |
                          Where-Object { $_ -like '*compress*rar*' })
    }

    @{ Ok = ($missing.Count -eq 0 -and $rarCompress.Count -eq 0)
       Evidence = "on .rar: $($onRar -join ', ')`nmissing: $($missing -join ', ')`ncompress-to-RAR entries found: $($rarCompress.Count)" }
}

# --- SHELL-W6 : GZIP's single-member rule ---------------------------------------------------------
Check -Id 'SHELL-W6a' -What 'Compress to GZIP is NOT registered on folders' -Test {
    $onFolders = @(Get-NobaSubKeyNames -SubKey "Software\Classes\$folderScope\shell\$($script:MenuId)\shell")
    $onFiles = @(Get-NobaSubKeyNames -SubKey "Software\Classes\$fileScope\shell\$($script:MenuId)\shell")

    @{ Ok = (($onFolders -notcontains 'noba.compress.gzip') -and ($onFiles -contains 'noba.compress.gzip'))
       Evidence = "on Directory: gzip present = $($onFolders -contains 'noba.compress.gzip') (must be False)`non *: gzip present = $($onFiles -contains 'noba.compress.gzip') (must be True)" }
}

Check -Id 'SHELL-W6b' -What 'GZIP on two files is refused by the model, and writes nothing' -Test {
    $probe = Join-Path $sandbox 'gzip-many'
    New-Item -ItemType Directory -Force -Path $probe | Out-Null
    $picks = @('one.txt', 'two.txt') | ForEach-Object {
        $p = Join-Path $probe $_; Set-Content -LiteralPath $p -Value $_ -Encoding UTF8; $p
    }

    $r = Invoke-NobaEntry -Scope $fileScope -EntryId 'noba.compress.gzip' -Selection $picks
    $written = @(Get-ChildItem -LiteralPath $probe -Filter '*.gz' -File -ErrorAction SilentlyContinue).Count

    # Exit 3 is UnsupportedOperation: the capability model refusing, with its reason.
    @{ Ok = ($r.ExitCode -eq 3 -and $written -eq 0)
       Evidence = "exit $($r.ExitCode) (3 = UnsupportedOperation); .gz files written: $written (must be 0)`n$($r.Output)" }
}

Check -Id 'SHELL-W6c' -What 'GZIP on ONE file still works - the rule, not a blanket ban' -Test {
    $probe = Join-Path $sandbox 'gzip-one'
    New-Item -ItemType Directory -Force -Path $probe | Out-Null
    $one = Join-Path $probe 'solo.txt'
    Set-Content -LiteralPath $one -Value 'solo' -Encoding UTF8

    $r = Invoke-NobaEntry -Scope $fileScope -EntryId 'noba.compress.gzip' -Selection @($one)
    $written = @(Get-ChildItem -LiteralPath $probe -Filter '*.gz' -File -ErrorAction SilentlyContinue).Count
    @{ Ok = ($r.ExitCode -eq 0 -and $written -eq 1); Evidence = "exit $($r.ExitCode); .gz written: $written" }
}

# --- SHELL-W7 : BZIP2 / XZ advertised nowhere -----------------------------------------------------
Check -Id 'SHELL-W7' -What 'No BZIP2 or XZ entry is registered under any scope' -Test {
    $found = @()
    foreach ($scope in @($spec.entries | ForEach-Object { $_.scopes } | Sort-Object -Unique)) {
        $found += @(Get-NobaSubKeyNames -SubKey "Software\Classes\$scope\shell\$($script:MenuId)\shell" |
                    Where-Object { $_ -match 'bzip|xz' })
    }
    @{ Ok = ($found.Count -eq 0); Evidence = "bzip2/xz entries found: $($found.Count) $($found -join ', ')" }
}

# --- SHELL-W8 : Arabic and Unicode survive ---------------------------------------------------------
Check -Id 'SHELL-W8' -What 'An Arabic path survives shell -> action -> archive, name intact' -Test {
    $arabic = Join-Path $payload (Local 'مستندات\تقرير سنوي.txt')
    $r = Invoke-NobaEntry -Scope $fileScope -EntryId 'noba.compress.7z' -Selection @($arabic)

    $produced = @(Get-ChildItem -LiteralPath (Join-Path $payload 'مستندات') -Filter '*.7z' -File -ErrorAction SilentlyContinue)
    $listing = ''
    if ($produced.Count -eq 1) { $listing = & $cliExe list $produced[0].FullName | Out-String }

    @{ Ok = ($r.ExitCode -eq 0 -and $produced.Count -eq 1 -and $listing -match 'تقرير')
       Evidence = "exit $($r.ExitCode); archive: $($produced.Count); the Arabic name is inside: $($listing -match 'تقرير')`n$($listing.Trim())" }
}

Check -Id 'SHELL-W8b' -What 'A Japanese path survives the same route' -Test {
    $japanese = Join-Path $payload '日本語のファイル.txt'
    $r = Invoke-NobaEntry -Scope $fileScope -EntryId 'noba.compress.7z' -Selection @($japanese)
    $target = Join-Path $payload '日本語のファイル.7z'
    $listing = ''
    if (Test-Path -LiteralPath $target) { $listing = & $cliExe list $target | Out-String }
    @{ Ok = ($r.ExitCode -eq 0 -and (Test-Path -LiteralPath $target) -and $listing -match '日本語')
       Evidence = "exit $($r.ExitCode); the Japanese name is inside: $($listing -match '日本語')" }
}

# --- SHELL-W10 : reinstall is idempotent ------------------------------------------------------------
# Run before W9, because W9 uninstalls.
Check -Id 'SHELL-W10' -What 'Registering twice does not double any entry' -Test {
    $scopes = @($spec.entries | ForEach-Object { $_.scopes } | Sort-Object -Unique)

    $countKeys = {
        $total = 0
        foreach ($scope in $scopes) {
            $total += @(Get-NobaSubKeyNames -SubKey "Software\Classes\$scope\shell\$($script:MenuId)\shell").Count
        }
        return $total
    }

    $first = & $countKeys
    Write-Log ''
    Write-Log '  re-registering to test idempotence...'
    $exit = Invoke-Registration
    $second = & $countKeys

    @{ Ok = ($exit -eq 0 -and $first -eq $second -and $first -gt 0)
       Evidence = "entry keys before: $first; after a second registration: $second (must be equal and non-zero)" }
}

Write-Log ''
Write-Log ('=' * 78)
Write-Log 'MANUAL STEPS - only what a script cannot see'
Write-Log ('=' * 78)

Start-Process explorer.exe -ArgumentList $payload | Out-Null

Ask -Id 'M1' -Do 'Right-click report.pdf in the opened folder.' `
    -Expect 'A context menu opens and a "NOBA Archive" entry exists somewhere in it.'

Ask -Id 'M2' -Do 'Note WHERE the NOBA Archive entry is.' `
    -Expect 'On Windows 11 it is UNDER "Show more options". That is expected for this surface - SHELL-L1. Answer [y] if you found it there or in the top-level menu on Windows 10.'

Ask -Id 'M3' -Do 'Open the NOBA Archive submenu on report.pdf and read every line.' `
    -Expect 'Compress to 7Z, ZIP, TAR, GZIP and Create archive... - and NOTHING else. No Open/Test/Extract on a plain file, no BZIP2, no XZ, no RAR.'

Ask -Id 'M4' -Do 'Right-click sample.7z and open the submenu.' `
    -Expect 'Open with NOBA Archive, Test archive, Extract here, Extract to... appear IN ADDITION to the Compress lines.'

Ask -Id 'M5' -Do 'Find or copy in any .rar, right-click it, and read the whole submenu carefully.' `
    -Expect 'Open, Test and both Extracts are present. There is NO "Compress to RAR" anywhere (SHELL-W5).'

Ask -Id 'M6' -Do 'Right-click the ProjectA FOLDER and open the submenu.' `
    -Expect 'Compress to 7Z, TAR and ZIP appear. "Compress to GZIP" is ABSENT - a folder is many members (SHELL-W6, D7).'

Ask -Id 'M7' -Do 'Select report.pdf, photo.jpg and "with spaces.txt" together, then choose Compress to ZIP. Wait, then look in the folder.' `
    -Expect 'EXACTLY ONE .zip appeared, containing all three files. THREE separate archives means Explorer did not honour %* / MultiSelectModel - this is the SHELL-L8 observation and the single most important step in this gate.'

Ask -Id 'M8' -Do 'Choose Create archive... on any selection.' `
    -Expect 'NOBA Archive opens directly on the Create screen with the selection already listed.'

Ask -Id 'M9' -Do 'Choose Open with NOBA Archive on sample.7z.' `
    -Expect 'NOBA Archive opens showing the archive contents on the Browse screen.'

# --- SHELL-W9 : uninstall leaves nothing -------------------------------------------------------
Write-Log ''
Write-Log ('=' * 78)
Write-Log 'UNINSTALL - SHELL-W9'
Write-Log ('=' * 78)
Write-Log ''

Check -Id 'SHELL-W9' -What 'Unregistering removes every NOBA key under HKCU\Software\Classes' -Test {
    Write-Log '  unregistering...'
    $exit = Invoke-Registration -Remove

    $survivors = @()
    $classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes')
    if ($classes) {
        try {
            foreach ($name in $classes.GetSubKeyNames()) {
                $probe = $classes.OpenSubKey("$name\shell\$($script:MenuId)")
                if ($probe) { $survivors += $name; $probe.Dispose() }

                # Nested scopes, e.g. SystemFileAssociations\.zip
                $nested = $classes.OpenSubKey($name)
                if ($nested) {
                    try {
                        foreach ($child in $nested.GetSubKeyNames()) {
                            $deep = $classes.OpenSubKey("$name\$child\shell\$($script:MenuId)")
                            if ($deep) { $survivors += "$name\$child"; $deep.Dispose() }
                        }
                    } catch { } finally { $nested.Dispose() }
                }
            }
        } finally { $classes.Dispose() }
    }

    $publisher = Test-NobaKey -SubKey 'Software\NOBA\Archive'

    @{ Ok = ($exit -eq 0 -and $survivors.Count -eq 0 -and -not $publisher)
       Evidence = "uninstall exit $exit; NOBA menu keys still present: $($survivors.Count) $($survivors -join ', ')`npublisher key still present: $publisher" }
}

if (-not $Unattended) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe; Start-Sleep -Seconds 2 }
}

Ask -Id 'M10' -Do 'Right-click any file now that the menu has been unregistered.' `
    -Expect 'The NOBA Archive entry is GONE. No leftover entry, no broken entry pointing at nothing (SHELL-W9).'

# --- SHELL-W1 / W2 : not this surface ----------------------------------------------------------
Write-Log ''
Add-Result -Id 'SHELL-W1' -Verdict 'NOT IMPLEMENTED' `
    -What 'NOBA Archive in the Windows 11 MODERN menu, above "Show more options"' `
    -Evidence 'Needs ADR-0006''s signed sparse MSIX + IExplorerCommand server. Not built. This gate cannot test it, and the classic entry under "Show more options" is NOT this.'

Add-Result -Id 'SHELL-W2' -Verdict 'NOT IMPLEMENTED' `
    -What 'The modern menu exposes capability-aware actions per selection' `
    -Evidence 'Same reason as SHELL-W1. Surface A does not exist.'

# ---------------------------------------------------------------------------
# Originals intact.
# ---------------------------------------------------------------------------
Write-Log ''
$originals = $before | Where-Object { $_ -notmatch '\.(7z|zip|tar|gz)\|' }
$after = Get-ChildItem -LiteralPath $payload -Recurse -File -ErrorAction SilentlyContinue |
         Sort-Object FullName |
         ForEach-Object { "{0}|{1}" -f $_.FullName.Substring($payload.Length), $_.Length }
$missing = @($originals | Where-Object { $after -notcontains $_ })

Check -Id 'SHELL-W3g' -What 'Every original file survived every action, same size' -Test {
    @{ Ok = ($missing.Count -eq 0)
       Evidence = "checked $($originals.Count) originals; changed or missing: $($missing.Count)`n$($missing -join "`n")" }
}

# ---------------------------------------------------------------------------
# Verdict.
# ---------------------------------------------------------------------------
$passed  = @($script:Results | Where-Object { $_.Verdict -eq 'PASS'            }).Count
$failed  = @($script:Results | Where-Object { $_.Verdict -eq 'FAIL'            }).Count
$notrun  = @($script:Results | Where-Object { $_.Verdict -eq 'NOT RUN'         }).Count
$notimpl = @($script:Results | Where-Object { $_.Verdict -eq 'NOT IMPLEMENTED' }).Count

Write-Log ''
Write-Log ('=' * 78)
Write-Log 'RESULTS'
Write-Log ('=' * 78)
foreach ($r in $script:Results) { Write-Log ("  {0,-15} {1,-11} {2}" -f $r.Verdict, $r.Id, $r.What) }

Write-Log ''
Write-Log ("PASSED {0}   FAILED {1}   NOT RUN {2}   NOT IMPLEMENTED {3}" -f $passed, $failed, $notrun, $notimpl)

$classicVerdict = if ($failed -eq 0 -and $notrun -eq 0) { 'PASS' } else { 'INCOMPLETE' }

Write-Log ''
Write-Log 'THE FOUR GATES ARE SEPARATE. This script can move exactly one of them:'
Write-Log ''
Write-Log ("  MODEL VERIFIED .......................... see scripts\validate.ps1, not this gate")
Write-Log ("  REGISTRATION VERIFIED ................... see scripts\validate.ps1, not this gate")
Write-Log ("  WINDOWS CLASSIC RUNTIME VERIFIED ........ {0}" -f $classicVerdict)
Write-Log ("  WINDOWS 11 MODERN MENU VERIFIED ......... NOT IMPLEMENTED (SHELL-W1, SHELL-W2)")
Write-Log ''
if ($notrun -gt 0) {
    Write-Log 'A NOT RUN is not a pass. Whatever it names is still unverified, and if M7 is among'
    Write-Log 'them then %* / MultiSelectModel remains unobserved on Windows (SHELL-L8).'
}
Write-Log ''
Write-Log 'Not covered by this gate, and not implemented (docs\SHELL_INTEGRATION.md):'
Write-Log '  SHELL-L1  the Windows 11 modern menu - needs ADR-0006''s signed sparse package'
Write-Log '  SHELL-L2  per-language menu labels - needs the IExplorerCommand handler'
Write-Log '  SHELL-L3  single instance - each action starts a new process'
Write-Log '  SHELL-L4  the production MSI'
Write-Log '  SHELL-L9  a greyed menu entry - the classic registry has no such state'
Write-Log ''
Write-Log "Transcript: $log"
Write-Log "Sandbox   : $sandbox  (left in place on purpose - it is the evidence)"

exit $(if ($failed -gt 0) { 1 } else { 0 })
