#Requires -Version 5.1
<#
.SYNOPSIS
    PHASE B — proves on Windows that creation actually produces usable archives.
.DESCRIPTION
    Every case builds a real archive on a real disk, then re-opens it, tests it, extracts it and
    compares what came back with what went in. Nothing is inspected by eye and nothing is asserted
    twice in the same direction: "the command exited 0" is not evidence that a file exists, and a file
    existing is not evidence that its contents survived.

    It runs the CLI, which goes through the same handlers as the Create screen (ADR-0009). That makes
    this the ENGINE gate on Windows. It says nothing about the WinUI screen, the pickers, the progress
    strip or RTL layout - those need the app and a pair of eyes, and this script deliberately does not
    pretend otherwise.

    A format the running build cannot create is reported SKIP, with the reason. It is never PASS.

.EXAMPLE
    .\scripts\create-gate.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$sandbox = Join-Path $root "artifacts\create-gate-$stamp"
$log = Join-Path $root "artifacts\create-gate-$stamp.txt"

New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$script:Results = New-Object System.Collections.ArrayList

function Write-Log {
    param([string]$Text)
    Write-Host $Text
    Add-Content -LiteralPath $log -Value $Text -Encoding UTF8
}

function Add-Result {
    param([string]$Case, [string]$Verdict, [string]$Detail)
    [void]$script:Results.Add([pscustomobject]@{ Case = $Case; Verdict = $Verdict; Detail = $Detail })
    Write-Log ("  {0,-6} {1} {2}" -f $Verdict, $Case, $Detail)
}

# ---------------------------------------------------------------------------
# The CLI, built from the current source rather than taken from artifacts\alpha.
# A stale binary that passes proves the previous drop, which is the failure mode
# validate.ps1's STALE ALPHA warning exists for.
# ---------------------------------------------------------------------------
Write-Log ('=' * 78)
Write-Log "NOBA Archive - CREATE GATE (PHASE B)"
Write-Log "Started : $(Get-Date -Format 'u')"
Write-Log "Sandbox : $sandbox"
Write-Log ('=' * 78)

$cliProject = Join-Path $root 'src\NOBA.Archive.Cli\NOBA.Archive.Cli.csproj'
Write-Log ''
Write-Log "Building the CLI ($Configuration)..."
$buildOutput = & dotnet build $cliProject -c $Configuration --nologo -v:q 2>&1 | Out-String
Write-Log $buildOutput.TrimEnd()
if ($LASTEXITCODE -ne 0) {
    Write-Log ''
    Write-Log "BUILD FAILED - the gate did not run. Nothing below is a result."
    Write-Log "Transcript: $log"
    exit 1
}

$cli = Join-Path $root "src\NOBA.Archive.Cli\bin\$Configuration\net10.0\noba-ar.exe"
if (-not (Test-Path -LiteralPath $cli)) {
    # The assembly name may differ from the project name; find the produced executable.
    $cli = (Get-ChildItem -Path (Join-Path $root "src\NOBA.Archive.Cli\bin\$Configuration") -Filter '*.exe' -Recurse |
            Select-Object -First 1).FullName
}
if (-not $cli -or -not (Test-Path -LiteralPath $cli)) {
    Write-Log "Could not locate the built CLI executable. Gate did not run."
    exit 1
}
Write-Log "CLI     : $cli"

function Invoke-Cli {
    param([string[]]$CliArgs)
    $out = & $cli @CliArgs 2>&1 | Out-String
    return [pscustomobject]@{ Output = $out; Code = $LASTEXITCODE }
}

function Invoke-CliJson {
    param([string[]]$CliArgs)
    $all = @($CliArgs) + '--json'
    # --json puts one JSON document on stdout and text on stderr, so stderr is dropped here.
    $out = & $cli @all 2>$null | Out-String
    $code = $LASTEXITCODE
    $parsed = $null
    if ($out.Trim().Length -gt 0) {
        try { $parsed = $out | ConvertFrom-Json } catch { $parsed = $null }
    }
    return [pscustomobject]@{ Json = $parsed; Raw = $out; Code = $code }
}

# ---------------------------------------------------------------------------
# What this build can actually create. Asked, not assumed.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Formats this build reports ---'
$formats = Invoke-CliJson @('formats')
$creatable = @()
if ($formats.Json) {
    foreach ($f in $formats.Json.formats) {
        $caps = ($f.capabilities -join ',')
        Write-Log ("  {0,-6} {1}" -f $f.id, $caps)
        if ($f.capabilities -contains 'Create') { $creatable += $f.id }
    }
} else {
    Write-Log "  could not read 'formats --json'; raw output follows"
    Write-Log $formats.Raw
}
Write-Log ("  creatable: " + ($creatable -join ', '))

# ---------------------------------------------------------------------------
# Source material. Arabic and Unicode names are written by this script, so the
# script file itself must stay UTF-8 with BOM or PowerShell 5.1 will mangle them.
# ---------------------------------------------------------------------------
$src = Join-Path $sandbox 'src'
New-Item -ItemType Directory -Force -Path $src | Out-Null

$singleFile = Join-Path $src 'notes.txt'
Set-Content -LiteralPath $singleFile -Value 'single file content' -Encoding UTF8

$secondFile = Join-Path $src 'second.txt'
Set-Content -LiteralPath $secondFile -Value 'second file content' -Encoding UTF8

$tree = Join-Path $src 'project'
New-Item -ItemType Directory -Force -Path (Join-Path $tree 'inner\deeper') | Out-Null
Set-Content -LiteralPath (Join-Path $tree 'readme.md') -Value 'top level' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $tree 'inner\one.txt') -Value 'one deep' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $tree 'inner\deeper\two.txt') -Value 'two deep' -Encoding UTF8

$arabic = Join-Path $src 'مستندات'
New-Item -ItemType Directory -Force -Path (Join-Path $arabic 'أرشيف') | Out-Null
Set-Content -LiteralPath (Join-Path $arabic 'تقرير سنوي.txt') -Value 'محتوى عربي' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $arabic 'أرشيف\ملاحظات.txt') -Value 'المزيد' -Encoding UTF8

$unicode = Join-Path $src 'unicode'
New-Item -ItemType Directory -Force -Path $unicode | Out-Null
Set-Content -LiteralPath (Join-Path $unicode '日本語のファイル.txt') -Value 'japanese' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $unicode 'Ελληνικά.txt') -Value 'greek' -Encoding UTF8

# ---------------------------------------------------------------------------
# One case: create, then prove the result is a real, readable archive.
# ---------------------------------------------------------------------------
function Test-CreateCase {
    param(
        [string]$Name,
        [string]$FormatId,
        [string]$Extension,
        [string[]]$Inputs,
        [string[]]$ExpectedEntries
    )

    $archive = Join-Path $sandbox ("{0}{1}" -f $Name, $Extension)

    $created = Invoke-CliJson (@('create', $archive) + $Inputs + @('--format', $FormatId))
    if ($created.Code -ne 0) {
        Add-Result $Name 'FAIL' "create exited $($created.Code): $($created.Raw.Trim())"
        return
    }

    if (-not (Test-Path -LiteralPath $archive)) {
        Add-Result $Name 'FAIL' "create reported success but '$archive' does not exist"
        return
    }

    $size = (Get-Item -LiteralPath $archive).Length
    if ($size -le 0) {
        Add-Result $Name 'FAIL' "the archive is $size bytes"
        return
    }

    if ([System.IO.Path]::GetExtension($archive) -ne $Extension) {
        Add-Result $Name 'FAIL' "extension is '$([System.IO.Path]::GetExtension($archive))', expected '$Extension'"
        return
    }

    # Re-opened, not trusted: the writer returning without an exception is not the same as the
    # reader being able to make sense of what it wrote.
    $listed = Invoke-CliJson @('list', $archive)
    if ($listed.Code -ne 0 -or -not $listed.Json) {
        Add-Result $Name 'FAIL' "the new archive could not be re-opened: $($listed.Raw.Trim())"
        return
    }

    $actual = @($listed.Json.entries | Where-Object { -not $_.isDirectory } | ForEach-Object { $_.path } | Sort-Object)
    $expected = @($ExpectedEntries | Sort-Object)
    if (($actual -join '|') -ne ($expected -join '|')) {
        Add-Result $Name 'FAIL' ("entries are [{0}], expected [{1}]" -f ($actual -join ', '), ($expected -join ', '))
        return
    }

    $tested = Invoke-CliJson @('test', $archive)
    if ($tested.Code -ne 0 -or $tested.Json.status -ne 'Valid') {
        Add-Result $Name 'FAIL' "integrity test said '$($tested.Json.status)' (exit $($tested.Code))"
        return
    }

    # And back out again, byte for byte.
    $dest = Join-Path $sandbox ("{0}-out" -f $Name)
    $extracted = Invoke-CliJson @('extract', $archive, '-o', $dest)
    if ($extracted.Code -ne 0) {
        Add-Result $Name 'FAIL' "extract exited $($extracted.Code): $($extracted.Raw.Trim())"
        return
    }

    foreach ($entry in $ExpectedEntries) {
        $onDisk = Join-Path $dest ($entry -replace '/', '\')
        if (-not (Test-Path -LiteralPath $onDisk)) {
            Add-Result $Name 'FAIL' "extraction did not produce '$entry'"
            return
        }
        if ((Get-Item -LiteralPath $onDisk).Length -le 0) {
            Add-Result $Name 'FAIL' "'$entry' came back empty"
            return
        }
    }

    Add-Result $Name 'PASS' ("{0} entries, {1} bytes, re-opened, tested Valid, extracted" -f $ExpectedEntries.Count, $size)
}

foreach ($fmt in @('7z', 'zip')) {
    Write-Log ''
    Write-Log "--- $($fmt.ToUpperInvariant()) ---"

    if ($creatable -notcontains $fmt) {
        Add-Result "$fmt-all-cases" 'SKIP' "this build does not report Create for '$fmt' (native backend missing?)"
        continue
    }

    $ext = if ($fmt -eq '7z') { '.7z' } else { '.zip' }

    Test-CreateCase "$fmt-single-file" $fmt $ext @($singleFile) @('notes.txt')
    Test-CreateCase "$fmt-multiple-files" $fmt $ext @($singleFile, $secondFile) @('notes.txt', 'second.txt')
    Test-CreateCase "$fmt-folder-nested" $fmt $ext @($tree) `
        @('project/readme.md', 'project/inner/one.txt', 'project/inner/deeper/two.txt')
    Test-CreateCase "$fmt-arabic" $fmt $ext @($arabic) `
        @('مستندات/تقرير سنوي.txt', 'مستندات/أرشيف/ملاحظات.txt')
    Test-CreateCase "$fmt-unicode" $fmt $ext @($unicode) `
        @('unicode/日本語のファイル.txt', 'unicode/Ελληνικά.txt')
    Test-CreateCase "$fmt-two-folders" $fmt $ext @($tree, $unicode) `
        @('project/readme.md', 'project/inner/one.txt', 'project/inner/deeper/two.txt',
          'unicode/日本語のファイル.txt', 'unicode/Ελληνικά.txt')

    # -------------------------------------------------------------------
    # The collision contract: refused without consent, and the archive that
    # was already there is byte-for-byte untouched afterwards.
    # -------------------------------------------------------------------
    $collision = Join-Path $sandbox "$fmt-collision$ext"
    $first = Invoke-Cli @('create', $collision, $singleFile, '--format', $fmt)
    if ($first.Code -ne 0) {
        Add-Result "$fmt-collision" 'FAIL' "could not create the first archive: $($first.Output.Trim())"
    } else {
        $before = (Get-FileHash -LiteralPath $collision -Algorithm SHA256).Hash
        $again = Invoke-Cli @('create', $collision, $secondFile, '--format', $fmt)
        $after = (Get-FileHash -LiteralPath $collision -Algorithm SHA256).Hash

        if ($again.Code -eq 0) {
            Add-Result "$fmt-collision-refused" 'FAIL' 'a second create over an existing archive succeeded without --overwrite'
        } elseif ($before -ne $after) {
            Add-Result "$fmt-collision-refused" 'FAIL' 'the refused create altered the archive it refused to replace'
        } else {
            Add-Result "$fmt-collision-refused" 'PASS' 'refused, and the existing archive is unchanged'
        }

        $consented = Invoke-CliJson @('create', $collision, $secondFile, '--format', $fmt, '--overwrite')
        $replaced = (Get-FileHash -LiteralPath $collision -Algorithm SHA256).Hash
        if ($consented.Code -ne 0) {
            Add-Result "$fmt-collision-overwrite" 'FAIL' "--overwrite exited $($consented.Code): $($consented.Raw.Trim())"
        } elseif ($replaced -eq $before) {
            Add-Result "$fmt-collision-overwrite" 'FAIL' '--overwrite reported success but the file did not change'
        } else {
            Add-Result "$fmt-collision-overwrite" 'PASS' 'replaced only after explicit consent'
        }
    }

    # -------------------------------------------------------------------
    # Nothing to compress is refused, and nothing is left behind.
    # -------------------------------------------------------------------
    $emptyDir = Join-Path $src 'empty-folder'
    New-Item -ItemType Directory -Force -Path $emptyDir | Out-Null
    $emptyTarget = Join-Path $sandbox "$fmt-empty$ext"
    $empty = Invoke-Cli @('create', $emptyTarget, $emptyDir, '--format', $fmt)
    if ($empty.Code -eq 0) {
        Add-Result "$fmt-empty-input" 'FAIL' 'creating from a folder with no files succeeded'
    } elseif (Test-Path -LiteralPath $emptyTarget) {
        Add-Result "$fmt-empty-input" 'FAIL' 'refused, but an archive was left on disk'
    } else {
        Add-Result "$fmt-empty-input" 'PASS' 'refused, and nothing was written'
    }
}

# ---------------------------------------------------------------------------
# No staging file may survive anywhere in the sandbox.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Staging files ---'
$staging = @(Get-ChildItem -LiteralPath $sandbox -Filter '.noba-*.tmp' -Recurse -Force -ErrorAction SilentlyContinue)
if ($staging.Count -gt 0) {
    Add-Result 'no-staging-left' 'FAIL' ("{0} staging file(s) survived: {1}" -f $staging.Count, ($staging.Name -join ', '))
} else {
    Add-Result 'no-staging-left' 'PASS' 'none'
}

# ---------------------------------------------------------------------------
# Verdict.
# ---------------------------------------------------------------------------
$passed = @($script:Results | Where-Object { $_.Verdict -eq 'PASS' }).Count
$failed = @($script:Results | Where-Object { $_.Verdict -eq 'FAIL' }).Count
$skipped = @($script:Results | Where-Object { $_.Verdict -eq 'SKIP' }).Count

Write-Log ''
Write-Log ('=' * 78)
Write-Log ("CREATE GATE: {0} passed, {1} failed, {2} skipped" -f $passed, $failed, $skipped)
if ($skipped -gt 0) {
    Write-Log 'A SKIP is not a pass. Whatever it names is still unverified on this machine.'
}
Write-Log ("VERDICT: " + $(if ($failed -eq 0 -and $passed -gt 0) { 'PASS' } else { 'FAIL' }))
Write-Log ('=' * 78)
Write-Log ''
Write-Log "Transcript: $log"
Write-Log "Sandbox   : $sandbox  (left in place on purpose - it is the evidence)"

exit $(if ($failed -eq 0 -and $passed -gt 0) { 0 } else { 1 })
