#Requires -Version 5.1
<#
.SYNOPSIS
    Records exactly what Explorer passes to a context-menu command. Diagnostic only — registers
    nothing NOBA-branded and touches no product key.
.DESCRIPTION
    SHELL-L8, settled by measurement instead of argument.

    The Phase 4 gate proved the ACTION MODEL is right: SHELL-W4 took the command string out of the
    registry, expanded %* itself into three arguments, ran it, and got one ZIP containing three
    files. Then step M7 did the same thing through Explorer and crashed, and step M8 — which is the
    other %*-carrying entry — opened the application with nothing loaded.

    Both point at the same seam: what Windows actually builds when it substitutes %*. Nobody has
    ever seen it. Every statement about it in this repository is inference from documentation.

    So this stops inferring. It registers three probe verbs whose only job is to write down what
    they were handed:

        NOBA.Probe.Star        command ends in   %*      MultiSelectModel = Player
        NOBA.Probe.StarQuoted  command ends in  "%*"     MultiSelectModel = Player
        NOBA.Probe.One         command ends in  "%1"     MultiSelectModel = Player

    Three variants because the failure could be any of three things and they look identical from
    outside: %* expanding to nothing, %* arriving as one joined string, or Player silently behaving
    like Document and launching one process per selected file. The log distinguishes all three - a
    single click that produces three log entries is the third case, and no amount of reading the
    documentation would have told you.

    THE PROBE IS VISIBLE AND SELF-EVIDENCING, AND THE FIRST VERSION WAS NOT.

    The first version ran hidden and wrote only to a log. When the log came back empty there was no
    way to tell "nobody clicked" from "the click never reached the recorder" - which are completely
    different findings, one of them trivial and one of them large. So now:

      * each probe opens a WINDOW showing what it captured, and holds it open;
      * the recorder writes to two logs, the repository one and one under %TEMP%, so a permissions
        problem in either place cannot destroy the evidence;
      * -SelfTest runs the recorder exactly as the registry would, before any clicking, and proves
        the plumbing works - so a later empty log is a fact about Explorer, not about the probe.

    WHAT IT RECORDS, AND WHY THAT FORM.

    [Environment]::CommandLine is the raw, unparsed line the operating system constructed. That is
    the measurement that matters: argv has already been through a parser by the time anything can
    read it, so a fault in the substitution and a fault in the parsing are indistinguishable from
    $args alone. Both are recorded, and the raw line is the authority.

    THIS IS NOT PART OF THE PRODUCT. It registers under HKCU\Software\Classes\*\shell\NOBA.Probe.*,
    which the product never writes and its uninstall never touches, so it cannot be confused with a
    real registration or left behind by one. -Remove takes it away.

.PARAMETER SelfTest
    Run the recorder the way the registry command would, and report whether the log grew. Proves the
    plumbing before you click anything. Does not register.

.PARAMETER Auto
    Invoke the probe verbs through the shell itself, with no clicking, using Shell.Application. This
    answers the single-file half of the question unattended: whether %* expands to the selected path
    or to nothing. The multi-file half still needs a human, because a multi-item context-menu
    invocation is not reachable from PowerShell - so the default mode now waits for you rather than
    returning to the prompt and letting the run be lost.

.PARAMETER Remove
    Unregister the probes and stop.

.PARAMETER Report
    Print both logs and stop. Does not register or unregister.

.EXAMPLE
    .\scripts\shell-argv-probe.ps1 -SelfTest
    .\scripts\shell-argv-probe.ps1
    .\scripts\shell-argv-probe.ps1 -Report
    .\scripts\shell-argv-probe.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [switch]$Auto,
    [switch]$Remove,
    [switch]$Report
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$sep = [System.IO.Path]::DirectorySeparatorChar
function Local {
    param([string]$Path)
    return $Path.Replace('\', $sep).Replace('/', $sep)
}

$artifacts = Join-Path $root 'artifacts'
$log = Join-Path $artifacts 'shell-argv-probe.log'
$fallback = Join-Path $env:TEMP 'noba-argv-probe.log'
$recorder = Join-Path $root (Local 'scripts\shell-argv-record.ps1')

# MEASURED 2026-08-28, single item, via Shell.Application:
#   %*    -> argc 0        the parameter expanded to nothing at all
#   "%*"  -> argc 1, <>    it expanded to an empty string
#   "%1"  -> argc 1, path  correct
#
# So %* is not the way a shell verb receives a selection, and the two variants that use it are kept
# only as the control that proves it. The open question is now narrow: with MultiSelectModel and
# "%1", does a three-file selection arrive as ONE invocation carrying three paths, or as THREE
# invocations carrying one each? Player and Document are both registered so the difference is
# visible in one sitting.
$variants = @(
    @{ Key = 'NOBA.Probe.Star';       Label = 'NOBA probe 1 (raw %*)';         Substitution = '%*';   Model = 'Player' }
    @{ Key = 'NOBA.Probe.StarQuoted'; Label = 'NOBA probe 2 (quoted %*)';      Substitution = '"%*"'; Model = 'Player' }
    @{ Key = 'NOBA.Probe.One';        Label = 'NOBA probe 3 (%1, Player)';     Substitution = '"%1"'; Model = 'Player' }
    @{ Key = 'NOBA.Probe.OneDoc';     Label = 'NOBA probe 4 (%1, Document)';   Substitution = '"%1"'; Model = 'Document' }
)

# ---------------------------------------------------------------------------
# Registry access, literal. The scope key is named "*" and every PowerShell provider cmdlet reads
# that as a wildcard - see shell-register.ps1 for the measurements.
# ---------------------------------------------------------------------------
function Set-ProbeValue {
    param(
        [Parameter(Mandatory)][string]$SubKey,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKey)
    if ($null -eq $key) { throw "Could not create HKCU\$SubKey." }
    try { $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}

function Remove-ProbeKey {
    param([Parameter(Mandatory)][string]$SubKey)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey)
    if ($null -eq $key) { return $false }
    $key.Dispose()

    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($SubKey, $false)
    return $true
}

function Write-Recorder {
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

    $body = @'
# Written by shell-argv-probe.ps1. Records what Explorer handed this process, and nothing else.
param([string]$Variant = '?')

$lines = New-Object System.Collections.ArrayList
[void]$lines.Add('')
[void]$lines.Add(('-' * 74))
[void]$lines.Add(("variant     : {0}" -f $Variant))
[void]$lines.Add(("at          : {0:o}" -f (Get-Date)))
[void]$lines.Add(("pid         : {0}" -f $PID))
[void]$lines.Add(("parent cwd  : {0}" -f (Get-Location).Path))

# The raw line the operating system built. This is the measurement; $args has already been parsed.
[void]$lines.Add(("raw command : {0}" -f [Environment]::CommandLine))

[void]$lines.Add(("argc        : {0}" -f $args.Count))
for ($i = 0; $i -lt $args.Count; $i++) {
    [void]$lines.Add(("  arg[{0}]    <{1}>" -f $i, $args[$i]))
}

# Two destinations. If the repository copy cannot be written - permissions, a moved folder, a
# redirected drive - the evidence still survives under %TEMP%, and an empty repository log then
# means something other than "nothing ran".
$targets = @(
    (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\shell-argv-probe.log')
    (Join-Path $env:TEMP 'noba-argv-probe.log')
)

foreach ($target in $targets) {
    try {
        $dir = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $stream = [System.IO.File]::AppendText($target)
        try { foreach ($line in $lines) { $stream.WriteLine($line) } } finally { $stream.Dispose() }
    }
    catch {
        [void]$lines.Add(("  (could not write {0}: {1})" -f $target, $_.Exception.Message))
    }
}

# Shown, not hidden. A probe you cannot see is a probe whose silence means nothing.
Write-Host ''
foreach ($line in $lines) { Write-Host $line }
Write-Host ''
Write-Host 'Recorded. This window closes in 6 seconds.' -ForegroundColor Green
Start-Sleep -Seconds 6
'@

    Set-Content -LiteralPath $recorder -Value $body -Encoding UTF8
}

<#
.SYNOPSIS
    Prints a log and returns the CLICKS it records - not the lines, and not the self-tests.
.DESCRIPTION
    The first version counted lines matching "variant" across both logs and announced "2
    invocation(s) recorded" when the only thing present was a single self-test written to two files.
    A report that reassures you about the very thing it exists to detect is worse than no report.

    So a click is now identified by (variant, pid), self-tests are excluded by name, and the same
    invocation appearing in both logs counts once.
#>
function Get-ProbeClicks {
    # $Title is NOT mandatory: the wait loop polls with no title, and a Mandatory [string] rejects
    # the empty string, which is how the poll threw on its first pass instead of watching for clicks.
    param([Parameter(Mandatory)][string]$Path, [string]$Title = '', [switch]$Quiet)

    if (-not $Quiet) {
        Write-Host ''
        Write-Host "--- $Title"
        Write-Host "    $Path"
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        if (-not $Quiet) { Write-Host '    (does not exist)' }
        return @()
    }

    $content = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    if ($content.Count -eq 0) {
        if (-not $Quiet) { Write-Host '    (exists but is empty)' }
        return @()
    }

    if (-not $Quiet) {
        Write-Host ''
        $content | ForEach-Object { Write-Host $_ }
    }

    # One record is a variant line followed by its pid line. Keyed on both so the same invocation
    # written to two logs is one click, and so two clicks of the same verb are two.
    $clicks = New-Object System.Collections.ArrayList
    $variant = $null
    foreach ($line in $content) {
        if ($line -match '^variant\s*:\s*(.+?)\s*$') { $variant = $Matches[1]; continue }
        if ($variant -and $line -match '^pid\s*:\s*(\d+)') {
            if ($variant -ne 'SELFTEST') { [void]$clicks.Add("$variant|$($Matches[1])") }
            $variant = $null
        }
    }

    return @($clicks)
}

Write-Host ('=' * 74)
Write-Host 'NOBA Archive - Explorer argv probe (diagnostic, not part of the product)'
Write-Host ('=' * 74)

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $powershell)) { $powershell = 'powershell.exe' }

# ---------------------------------------------------------------------------
if ($Report) {
    $a = Get-ProbeClicks -Path $log -Title 'repository log'
    $b = Get-ProbeClicks -Path $fallback -Title 'fallback log (%TEMP%)'
    $clicks = @(@($a) + @($b) | Sort-Object -Unique)

    Write-Host ''
    Write-Host ('=' * 74)
    if ($clicks.Count -eq 0) {
        Write-Host 'NOTHING WAS RECORDED.'
        Write-Host ''
        Write-Host 'That means one of two very different things, and they must not be confused:'
        Write-Host ''
        Write-Host '  (a) the probe entries were never clicked; or'
        Write-Host '  (b) they were clicked and the command never ran.'
        Write-Host ''
        Write-Host 'Run  .\scripts\shell-argv-probe.ps1 -SelfTest  first. If the self-test records'
        Write-Host 'and a click does not, the answer is (b) - and that is a finding in itself.'
    }
    else {
        Write-Host ("{0} probe click(s) recorded (self-tests excluded, duplicates folded):" -f $clicks.Count)
        foreach ($c in $clicks) {
            $parts = $c -split '\|'
            Write-Host ("    {0,-24} pid {1}" -f $parts[0], $parts[1])
        }
        Write-Host ''
        Write-Host 'For the three-file click on NOBA.Probe.Star: ONE pid means MultiSelectModel was'
        Write-Host 'honoured and the argc above is the answer. THREE pids means it was not, and'
        Write-Host 'Explorer launched a process per file.'
    }
    Write-Host ('=' * 74)
    exit 0
}

# ---------------------------------------------------------------------------
if ($Remove) {
    Write-Host ''
    foreach ($v in $variants) {
        $key = "Software\Classes\*\shell\$($v.Key)"
        if (Remove-ProbeKey -SubKey $key) { Write-Host "  removed  HKCU\$key" }
        else { Write-Host "  absent   HKCU\$key" }
    }

    Write-Host ''
    Write-Host 'Probes removed. Both logs are left in place - they are the evidence.'
    Write-Host "  $log"
    Write-Host "  $fallback"
    exit 0
}

# ---------------------------------------------------------------------------
if ($SelfTest) {
    Write-Recorder

    $before = 0
    if (Test-Path -LiteralPath $log) { $before = @(Get-Content -LiteralPath $log -Encoding UTF8).Count }

    Write-Host ''
    Write-Host 'Running the recorder exactly as the registry command would...'
    Write-Host ''

    $probe1 = Join-Path $env:TEMP 'noba-selftest-a.txt'
    $probe2 = Join-Path $env:TEMP 'noba-selftest b.txt'
    Set-Content -LiteralPath $probe1 -Value 'a' -Encoding UTF8
    Set-Content -LiteralPath $probe2 -Value 'b' -Encoding UTF8

    & $powershell -NoProfile -ExecutionPolicy Bypass -File $recorder -Variant SELFTEST $probe1 $probe2

    $after = 0
    if (Test-Path -LiteralPath $log) { $after = @(Get-Content -LiteralPath $log -Encoding UTF8).Count }

    Write-Host ''
    Write-Host ('=' * 74)
    if ($after -gt $before) {
        Write-Host ("SELF-TEST PASS - the log grew by {0} lines." -f ($after - $before)) -ForegroundColor Green
        Write-Host ''
        Write-Host 'The recorder, the log path and the argument shape all work. If a real click now'
        Write-Host 'records nothing, the click is not reaching the command - which is the finding.'
    }
    else {
        Write-Host 'SELF-TEST FAIL - the recorder ran but the log did not grow.' -ForegroundColor Red
        Write-Host 'Check the fallback log and send both paths back; do not bother clicking yet.'
    }
    Write-Host ('=' * 74)
    exit 0
}

# ---------------------------------------------------------------------------
<#
.SYNOPSIS
    Invokes one registered verb on one file through the shell, without a human.
.DESCRIPTION
    Shell.Application asks the shell to run the verb, so the shell performs its own substitution -
    which is the thing under measurement. It is not the same code path as a context-menu click and
    is not offered as if it were, but if %* comes back empty here it comes back empty there too, and
    that is worth knowing without spending another round trip.

    Only single-item invocation is reachable this way. Multi-item needs IContextMenu over several
    pidls, which PowerShell cannot reasonably reach - hence the wait loop below.
#>
function Invoke-ProbeVerb {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)

    $shell = New-Object -ComObject Shell.Application
    try {
        $folder = $shell.Namespace((Split-Path -Parent $Path))
        if ($null -eq $folder) { return "could not open the containing folder" }

        $item = $folder.ParseName((Split-Path -Leaf $Path))
        if ($null -eq $item) { return "could not find the item" }

        # Matched on the displayed name with its accelerator ampersands removed; the canonical name
        # is not exposed here.
        foreach ($verb in $item.Verbs()) {
            if ($verb.Name.Replace('&', '') -eq $Label) {
                $verb.DoIt()
                return $null
            }
        }

        return "the verb is not on this item's menu (found: " +
               (($item.Verbs() | ForEach-Object { $_.Name.Replace('&', '') }) -join ', ') + ")"
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

<#
.SYNOPSIS
    Blocks until a probe click is recorded, showing a live count.
.DESCRIPTION
    Three runs of this diagnostic have now ended with an empty log because the shell returned to the
    prompt and the clicking never happened. A script that cannot be accidentally skipped is worth
    more than one that explains itself well, so this one does not finish until it has something.
#>
function Wait-ForClicks {
    param([int]$Seconds = 300)

    $deadline = (Get-Date).AddSeconds($Seconds)
    $seen = 0

    # The Enter that launched this script is still in the buffer, and KeyAvailable sees it on the
    # first pass - which is why the previous run reported "nothing was recorded" before anyone
    # could reach the mouse.
    try { $Host.UI.RawUI.FlushInputBuffer() } catch { }

    Write-Host ''
    Write-Host 'Waiting for you to click. Press ESC to stop waiting.' -ForegroundColor Yellow
    Write-Host ''

    while ((Get-Date) -lt $deadline) {
        if ($Host.UI.RawUI.KeyAvailable) {
            $pressed = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
            if ($pressed.VirtualKeyCode -eq 27) { break }   # ESC only, so a stray key cannot end it
        }

        $clicks = @(@(Get-ProbeClicks -Path $log -Title '' -Quiet) +
                    @(Get-ProbeClicks -Path $fallback -Title '' -Quiet) | Sort-Object -Unique)

        if ($clicks.Count -ne $seen) {
            $seen = $clicks.Count
            Write-Host ("  {0} click(s) recorded so far: {1}" -f $seen, (($clicks | ForEach-Object { ($_ -split '\|')[0] }) -join ', '))
        }

        Start-Sleep -Milliseconds 1500
    }

    Write-Host ''
    if ($seen -eq 0) {
        Write-Host 'Nothing was recorded.' -ForegroundColor Red
        Write-Host 'If you clicked and no window appeared, say so - the command is not running at all.'
    }
    else {
        Write-Host ("{0} click(s) captured. Run -Report to read them." -f $seen) -ForegroundColor Green
    }
}

if ($Auto) {
    Write-Recorder

    $sample = Join-Path $env:TEMP 'noba-probe-sample.txt'
    Set-Content -LiteralPath $sample -Value 'sample' -Encoding UTF8

    Write-Host ''
    Write-Host 'Registering, then invoking each probe on ONE file through the shell...'
    Write-Host ''

    foreach ($v in $variants) {
        $key = "Software\Classes\*\shell\$($v.Key)"
        $command = '"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" -Variant {2} {3}' -f
            $powershell, $recorder, $v.Key, $v.Substitution

        Set-ProbeValue -SubKey $key -Name 'MUIVerb' -Value $v.Label
        Set-ProbeValue -SubKey $key -Name 'MultiSelectModel' -Value $v.Model
        Set-ProbeValue -SubKey "$key\command" -Name '' -Value $command
    }

    Start-Sleep -Seconds 2

    foreach ($v in $variants) {
        $failure = Invoke-ProbeVerb -Path $sample -Label $v.Label
        if ($failure) { Write-Host ("  {0,-22} NOT INVOKED - {1}" -f $v.Key, $failure) }
        else { Write-Host ("  {0,-22} invoked" -f $v.Key) }
        Start-Sleep -Seconds 2
    }

    Write-Host ''
    Write-Host 'Run -Report to read what they captured, then -Remove.'
    Write-Host 'The three-file case still needs a human: run this script with no switches.'
    exit 0
}

Write-Recorder
Write-Host ''
Write-Host "Recorder : $recorder"
Write-Host "Logs     : $log"
Write-Host "           $fallback"
Write-Host ''

foreach ($v in $variants) {
    $key = "Software\Classes\*\shell\$($v.Key)"

    # Deliberately NOT hidden. See the header: a probe you cannot see is a probe whose silence
    # means nothing.
    $command = '"{0}" -NoProfile -ExecutionPolicy Bypass -NoExit -File "{1}" -Variant {2} {3}' -f
        $powershell, $recorder, $v.Key, $v.Substitution

    Set-ProbeValue -SubKey $key -Name 'MUIVerb' -Value $v.Label
    Set-ProbeValue -SubKey $key -Name 'MultiSelectModel' -Value $v.Model
    Set-ProbeValue -SubKey "$key\command" -Name '' -Value $command

    Write-Host ("  {0,-22} {1}" -f $v.Key, $v.Substitution)
}

Write-Host ''
Write-Host 'Restarting Explorer so the probe keys are read...'
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
    Start-Process explorer.exe
    Start-Sleep -Seconds 2
}

Write-Host ''
Write-Host ('=' * 74)
Write-Host 'NOW CLICK. Four clicks, in this order.'
Write-Host ('=' * 74)
Write-Host ''
Write-Host '  Select THREE files, then click:  NOBA probe 3 (%1, Player)'
Write-Host ''
Write-Host '  That single click is the whole remaining question. If you have a moment for a'
Write-Host '  second, repeat with  NOBA probe 4 (%1, Document)  as the control.'
Write-Host ''
Write-Host 'On Windows 11 these are under "Show more options".'
Write-Host ''
Write-Host 'A BLUE POWERSHELL WINDOW SHOULD APPEAR each time, showing what it captured, and stay'
Write-Host 'open. If no window appears, say so - that is itself the answer, and it means the'
Write-Host 'command is not running at all rather than running and getting nothing.'
Write-Host ''
Write-Host 'Then:'
Write-Host '  .\scripts\shell-argv-probe.ps1 -Report'
Write-Host '  .\scripts\shell-argv-probe.ps1 -Remove'
Write-Host ''
Write-Host 'HOW TO READ IT. ONE window listing three paths means Player works and the fix is to'
Write-Host 'register "%1" instead of %*. THREE windows, one path each, means the classic surface'
Write-Host 'cannot deliver a multi-selection to one process at all - a much bigger finding, and one'
Write-Host 'the resolver would have to absorb rather than the registration.'

Wait-ForClicks
