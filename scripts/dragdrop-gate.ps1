#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 06 drag-and-drop gate — prepares the material, then walks you through what only eyes can check.
.DESCRIPTION
    Windows decides what a drag carries and when a drop lands. None of that can be asserted from a
    script, so this does the half that can be: it checks the source tree is the right one, builds the
    engine and the shell, lays out a folder of awkward files to drag, launches the app, and then asks
    twenty questions in a fixed order and records the answers.

    Every answer is yours. The script never marks a step PASS on your behalf, and a step you skip is
    recorded as NOT RUN rather than quietly dropped.

    Step 20 is the one that matters most and is easiest to forget: after creating the archive, the
    files you dragged must still be where you dragged them from. A drop that moved them would look
    exactly like a drop that worked.

.PARAMETER SkipBuild
    Use the shell already built. Only sensible when you just built it.

.EXAMPLE
    .\scripts\dragdrop-gate.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$ExpectedBuildStamp = '2026-08-27-drop-06-drag-drop'
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

$sep = [System.IO.Path]::DirectorySeparatorChar
function Local {
    param([string]$Path)
    return $Path.Replace('\', $sep).Replace('/', $sep)
}

$sandbox = Join-Path $root (Local "artifacts\dragdrop-gate-$stamp")
$log = Join-Path $root (Local "artifacts\dragdrop-gate-$stamp.txt")
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$script:Answers = New-Object System.Collections.ArrayList

function Write-Log {
    param([string]$Text)
    Write-Host $Text
    Add-Content -LiteralPath $log -Value $Text -Encoding UTF8
}

Write-Log ('=' * 78)
Write-Log 'NOBA Archive - DROP 06 DRAG & DROP GATE'
Write-Log "Started : $(Get-Date -Format 'u')"
Write-Log "Sandbox : $sandbox"
Write-Log ('=' * 78)

# ---------------------------------------------------------------------------
# The tree under test, before anything is measured.
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
    Write-Log "Transcript: $log"
    exit 1
}

# ---------------------------------------------------------------------------
# Material to drag. Chosen to be inconvenient: spaces, Arabic, Japanese, Greek,
# a nested folder, and a file that will be deleted while the app is open.
# ---------------------------------------------------------------------------
$payload = Join-Path $sandbox 'drag-me'
New-Item -ItemType Directory -Force -Path (Join-Path $payload (Local 'ProjectA\nested')) | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'ProjectB') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'مستندات') | Out-Null

$files = @(
    @{ Path = 'report.pdf';                  Text = 'report' }
    @{ Path = 'with spaces.txt';             Text = 'spaces' }
    @{ Path = 'ProjectA\nested\leaf.txt';    Text = 'deep' }
    @{ Path = 'ProjectB\notes.txt';          Text = 'notes' }
    @{ Path = 'مستندات\تقرير سنوي.txt';      Text = 'محتوى عربي' }
    @{ Path = '日本語のファイル.txt';          Text = 'japanese' }
    @{ Path = 'Ελληνικά.txt';                Text = 'greek' }
)
foreach ($f in $files) {
    Set-Content -LiteralPath (Join-Path $payload (Local $f.Path)) -Value $f.Text -Encoding UTF8
}

# Recorded now so step 20 can be checked against something, not against memory.
$before = Get-ChildItem -LiteralPath $payload -Recurse -File |
          Sort-Object FullName |
          ForEach-Object { "{0}|{1}" -f $_.FullName.Substring($payload.Length), $_.Length }

Write-Log ''
Write-Log "Drag material : $payload"
Write-Log ("  {0} files laid out" -f $before.Count)
Write-Log ''

# ---------------------------------------------------------------------------
# Build.
# ---------------------------------------------------------------------------
$appExe = Join-Path $root (Local 'src\NOBA.Archive.App\bin\x64\Debug\net10.0-windows10.0.19041.0\NOBA.Archive.App.exe')

if (-not $SkipBuild) {
    Write-Log 'Building the engine solution...'
    $engine = & dotnet build (Join-Path $root 'NOBA.Archive.sln') -c Release --nologo -v:q 2>&1 | Out-String
    Write-Log $engine.TrimEnd()
    if ($LASTEXITCODE -ne 0) {
        Write-Log 'ENGINE BUILD FAILED - the gate did not run.'
        exit 1
    }

    Write-Log ''
    Write-Log 'Building the WinUI shell...'
    $shell = & dotnet build (Join-Path $root 'NOBA.Archive.App.sln') -c Debug -p:Platform=x64 --nologo -v:q 2>&1 | Out-String
    Write-Log $shell.TrimEnd()
    if ($LASTEXITCODE -ne 0) {
        Write-Log 'WINUI BUILD FAILED - the gate did not run. Report the error above and stop.'
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $appExe)) {
    Write-Log "Could not find the shell at $appExe. The gate did not run."
    exit 1
}

Write-Log ''
Write-Log "Launching $appExe"
$process = Start-Process -FilePath $appExe -PassThru
Start-Sleep -Seconds 3
Write-Log "  process $($process.Id)"

Start-Process explorer.exe -ArgumentList $payload | Out-Null

# ---------------------------------------------------------------------------
# The twenty steps. One question each, answered by the person watching.
# ---------------------------------------------------------------------------
function Ask {
    param([int]$Number, [string]$Do, [string]$Expect)

    Write-Host ''
    Write-Host ("--- Step {0} ---" -f $Number) -ForegroundColor Cyan
    Write-Host "  DO     : $Do"
    Write-Host "  EXPECT : $Expect"

    $answer = Read-Host '  [y] as expected  [n] not as expected  [s] skip'
    $verdict = switch ($answer.Trim().ToLowerInvariant()) {
        'y' { 'PASS' }
        'n' { 'FAIL' }
        default { 'NOT RUN' }
    }

    $note = ''
    if ($verdict -eq 'FAIL') {
        $note = Read-Host '  what happened instead'
    }

    [void]$script:Answers.Add([pscustomobject]@{ Step = $Number; Verdict = $verdict; Do = $Do; Note = $note })
    Write-Log ("  {0,-7} step {1}: {2}{3}" -f $verdict, $Number, $Do, $(if ($note) { " -- $note" } else { '' }))
}

Write-Log ''
Write-Log '--- manual steps ---'

Ask 1  'NOBA Archive is open.' 'The window drew, and nothing crashed on startup.'
Ask 2  'Open the Create screen.' 'The input panel says "Drag files or folders here" with a folder glyph.'
Ask 3  'Explorer is open on the drag-me folder.' 'You can see report.pdf, ProjectA, ProjectB and the Arabic folder.'
Ask 4  'Drag report.pdf over the input panel and hold it there.' 'The border highlights and the text changes to "Release to add".'
Ask 5  'Release it.' 'One row appears with the full path. No archive was created.'
Ask 6  'Drag "with spaces.txt", the Japanese file and the Greek file together.' 'Three more rows, all names intact, none mangled.'
Ask 7  'Drag ProjectA onto the panel.' 'ONE row for the folder itself - not its contents, and no pause while it counted them.'
Ask 8  'Drag ProjectB and the Arabic folder together.' 'Two more rows. The Arabic path reads correctly and is not reversed.'
Ask 9  'Drag report.pdf onto the panel a second time.' 'The list does not change. There is still exactly one report.pdf row.'
Ask 10 'Select any row.' 'The Remove button becomes available.'
Ask 11 'Press Remove.' 'That row goes, and only that row.'
Ask 12 'Press Clear.' 'The list empties and the drag invitation comes back.'
Ask 13 'Switch the interface to Arabic.' 'The panel reads "اسحب الملفات أو المجلدات هنا" and the screen mirrors.'
Ask 14 'Drag the Arabic folder in, while still in Arabic.' 'It is added. The path itself still reads left to right.'
Ask 15 'Switch back to English. Choose GZIP as the format.' 'GZIP is offered - it is creatable in this build.'
Ask 16 'Make sure two or more items are in the list.' 'Create archive is DISABLED, and the line beneath explains that this format holds a single file.'
Ask 17 'Remove items until one remains.' 'Create archive becomes available again.'
Ask 18 'Switch the format to 7Z and drag the whole drag-me folder in.' 'Create archive is available.'
Ask 19 'Choose a destination, name it, and press Create archive.' 'It completes, and the operations strip names the archive you just made.'
Ask 20 'Open the produced archive in 7-Zip or Explorer.' 'The Arabic, Japanese and Greek names are all correct inside it.'

# ---------------------------------------------------------------------------
# The check the script CAN make: dragging must not have moved anything.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- what the script can verify by itself ---'

$after = Get-ChildItem -LiteralPath $payload -Recurse -File -ErrorAction SilentlyContinue |
         Sort-Object FullName |
         ForEach-Object { "{0}|{1}" -f $_.FullName.Substring($payload.Length), $_.Length }

if (($before -join "`n") -eq ($after -join "`n")) {
    Write-Log ("  PASS    every dragged file is still in place, same size ({0} files)" -f $after.Count)
    $intact = $true
} else {
    Write-Log '  FAIL    the drag material changed. Dropping must copy nothing and move nothing.'
    Write-Log ("          before {0} files, after {1}" -f $before.Count, $after.Count)
    foreach ($line in (Compare-Object $before $after)) {
        Write-Log ("          {0} {1}" -f $line.SideIndicator, $line.InputObject)
    }
    $intact = $false
}

# ---------------------------------------------------------------------------
# Verdict.
# ---------------------------------------------------------------------------
$passed = @($script:Answers | Where-Object { $_.Verdict -eq 'PASS'    }).Count
$failed = @($script:Answers | Where-Object { $_.Verdict -eq 'FAIL'    }).Count
$notrun = @($script:Answers | Where-Object { $_.Verdict -eq 'NOT RUN' }).Count

Write-Log ''
Write-Log ('=' * 78)
Write-Log ("DRAG & DROP GATE: {0} passed, {1} failed, {2} not run (of 20 manual steps)" -f $passed, $failed, $notrun)
if ($notrun -gt 0) {
    Write-Log 'A NOT RUN is not a pass. Whatever it names is still unverified.'
}
Write-Log ('VERDICT: ' + $(if ($failed -eq 0 -and $notrun -eq 0 -and $intact) { 'PASS' } else { 'INCOMPLETE' }))
Write-Log ('=' * 78)
Write-Log ''
Write-Log "Transcript: $log"
Write-Log "Sandbox   : $sandbox  (left in place on purpose - it is the evidence)"
Write-Log ''
Write-Log 'The app is still open. Close it when you are done.'

exit $(if ($failed -eq 0 -and $notrun -eq 0 -and $intact) { 0 } else { 1 })
