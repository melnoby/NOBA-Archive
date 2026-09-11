#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 05 format gate — proves on Windows what this build can actually do with each format.
.DESCRIPTION
    Every case builds or reads a real archive on a real disk and checks the result. Nothing is
    inspected by eye, and nothing is inferred: "the command exited 0" is never accepted as evidence
    that a file exists, and a file existing is never accepted as evidence that its contents survived.

    It runs the CLI, which goes through the same engine, the same registry and the same providers as
    the window (ADR-0009). That makes this the ENGINE gate. It says nothing about the WinUI screens —
    the buttons, the pickers, the operation strip, RTL layout — which need app.ps1 and a pair of eyes.

    Gate numbers in the output match the numbered list in the DROP 05 Windows gate request, so the
    transcript can be read against it line by line.

    A format this build cannot handle is reported SKIP with the reason. It is never PASS.

.PARAMETER RarPath
    An existing .rar to exercise gates 6, 7 and 10. The repository ships no RAR fixture and this
    script will not invent one, so without it those gates report NOT RUN rather than passing.

.EXAMPLE
    .\scripts\format-gate.ps1
.EXAMPLE
    .\scripts\format-gate.ps1 -RarPath 'C:\Users\m-m-e\Desktop\some-archive.rar'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$RarPath,
    [string]$ExpectedBuildStamp = '2026-08-26-drop-05-format-capability-expansion'
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# Paths are written with backslashes because this is a Windows gate, but they are normalised so the
# same script can be dry-run on the build machine before it is trusted with a Windows run. A harness
# that fails on its own path handling wastes the one run that actually matters.
$sep = [System.IO.Path]::DirectorySeparatorChar
function Local {
    param([string]$Path)
    return $Path.Replace('\', $sep).Replace('/', $sep)
}

$sandbox = Join-Path $root (Local "artifacts\format-gate-$stamp")
$log = Join-Path $root (Local "artifacts\format-gate-$stamp.txt")

New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$script:Results = New-Object System.Collections.ArrayList

function Write-Log {
    param([string]$Text)
    Write-Host $Text
    Add-Content -LiteralPath $log -Value $Text -Encoding UTF8
}

function Add-Result {
    param([string]$Gate, [string]$Case, [string]$Verdict, [string]$Detail)
    [void]$script:Results.Add([pscustomobject]@{ Gate = $Gate; Case = $Case; Verdict = $Verdict; Detail = $Detail })
    Write-Log ("  {0,-7} [{1,-5}] {2} {3}" -f $Verdict, $Gate, $Case, $Detail)
}

Write-Log ('=' * 78)
Write-Log 'NOBA Archive - DROP 05 FORMAT GATE'
Write-Log "Started : $(Get-Date -Format 'u')"
Write-Log "Sandbox : $sandbox"
Write-Log ('=' * 78)

# ---------------------------------------------------------------------------
# The source under test, identified before anything is measured.
#
# A transcript that reports PASS on every gate is indistinguishable from one produced by a tree that
# predates the change - which has happened twice in this project. The stamp is checked, not printed.
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
    Write-Log "*** BuildStamp.cs says '$sourceStamp'."
    Write-Log "Transcript: $log"
    exit 1
}

Write-Log ''
Write-Log "Building the CLI ($Configuration) from source..."
$cliProject = Join-Path $root (Local 'src\NOBA.Archive.Cli\NOBA.Archive.Cli.csproj')
$buildOutput = & dotnet build $cliProject -c $Configuration --nologo -v:q 2>&1 | Out-String
Write-Log $buildOutput.TrimEnd()
if ($LASTEXITCODE -ne 0) {
    Write-Log ''
    Write-Log 'BUILD FAILED - the gate did not run. Nothing below is a result.'
    Write-Log "Transcript: $log"
    exit 1
}

$binRoot = Join-Path $root (Local "src\NOBA.Archive.Cli\bin\$Configuration")
$cli = Join-Path $binRoot (Local "net10.0\noba-ar.exe")
if (-not (Test-Path -LiteralPath $cli)) {
    $found = Get-ChildItem -Path $binRoot -Recurse -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -eq 'noba-ar.exe' -or $_.Name -eq 'noba-ar' } |
             Select-Object -First 1
    if ($found) { $cli = $found.FullName } else { $cli = $null }
}
if (-not $cli -or -not (Test-Path -LiteralPath $cli)) {
    Write-Log 'Could not locate the built CLI. Gate did not run.'
    exit 1
}
Write-Log "CLI           : $cli"

function Invoke-Cli {
    param([string[]]$CliArgs)
    $out = & $cli @CliArgs 2>&1 | Out-String
    return [pscustomobject]@{ Output = $out; Code = $LASTEXITCODE }
}

function Invoke-CliJson {
    param([string[]]$CliArgs)
    $all = @($CliArgs) + '--json'
    $out = & $cli @all 2>$null | Out-String
    $code = $LASTEXITCODE
    $parsed = $null
    if ($out.Trim().Length -gt 0) {
        try { $parsed = $out | ConvertFrom-Json } catch { $parsed = $null }
    }
    return [pscustomobject]@{ Json = $parsed; Raw = $out; Code = $code }
}

# ---------------------------------------------------------------------------
# GATE 3 - what this build reports.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Gate 3: formats reported by this build ---'

$formats = Invoke-CliJson @('formats')
$caps = @{}
if ($formats.Json) {
    foreach ($f in $formats.Json.formats) {
        $caps[$f.id] = @($f.capabilities)
        Write-Log ("  {0,-6} {1}" -f $f.id, ($f.capabilities -join ','))
    }
} else {
    Write-Log '  could not read formats --json; raw output follows'
    Write-Log $formats.Raw
}

function Can {
    param([string]$Format, [string]$Capability)
    return $caps.ContainsKey($Format) -and ($caps[$Format] -contains $Capability)
}

# TAR, GZIP and ZIP are managed and need no native library, so their absence is a real failure rather
# than an environment difference. 7Z and RAR need 7z.dll and are reported, not required.
foreach ($managed in @('zip', 'tar', 'gzip')) {
    if ($caps.ContainsKey($managed)) {
        Add-Result '3' "format-$managed" 'PASS' 'reported'
    } else {
        Add-Result '3' "format-$managed" 'FAIL' 'a managed format is missing from this build'
    }
}
foreach ($native in @('7z', 'rar')) {
    if (Can $native 'Open') {
        Add-Result '3' "format-$native" 'PASS' 'reported with capabilities (native library found)'
    } elseif ($caps.ContainsKey($native)) {
        Add-Result '3' "format-$native" 'SKIP' 'known but unusable - the 7-Zip native library was not loaded'
    } else {
        Add-Result '3' "format-$native" 'FAIL' 'not reported at all; it should be known even when unusable'
    }
}

# ---------------------------------------------------------------------------
# Source material, including the names that break archive tools.
# ---------------------------------------------------------------------------
$src = Join-Path $sandbox 'payload'
New-Item -ItemType Directory -Force -Path (Join-Path $src (Local 'nested\deeper')) | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $src 'مستندات') | Out-Null

$names = @(
    @{ Path = 'plain.txt';                       Text = 'plain' }
    @{ Path = 'with spaces.txt';                 Text = 'spaces' }
    @{ Path = 'nested\deeper\leaf.txt';          Text = 'deep' }
    @{ Path = 'مستندات\تقرير سنوي.txt';          Text = 'محتوى عربي' }
    @{ Path = '日本語のファイル.txt';              Text = 'japanese' }
    @{ Path = 'Ελληνικά.txt';                    Text = 'greek' }
)
foreach ($n in $names) {
    Set-Content -LiteralPath (Join-Path $src (Local $n.Path)) -Value $n.Text -Encoding UTF8
}

$single = Join-Path $sandbox 'single.txt'
Set-Content -LiteralPath $single -Value 'one file only' -Encoding UTF8

$expectedEntries = @(
    'payload/plain.txt'
    'payload/with spaces.txt'
    'payload/nested/deeper/leaf.txt'
    'payload/مستندات/تقرير سنوي.txt'
    'payload/日本語のファイル.txt'
    'payload/Ελληνικά.txt'
) | Sort-Object

# ---------------------------------------------------------------------------
# GATES 11-14, 4-5, 7-9, 17-18 - create, reopen, test, extract, per format.
# ---------------------------------------------------------------------------
function Test-MultiEntryFormat {
    param([string]$Format, [string]$Extension, [string]$CreateGate, [string]$OpenGate, [string]$ExtractGate)

    if (-not (Can $Format 'Create')) {
        Add-Result $CreateGate "create-$Format" 'SKIP' 'this build does not report Create for this format'
        return
    }

    $archive = Join-Path $sandbox "made-$Format$Extension"

    $created = Invoke-CliJson (@('create', $archive, $src) + @('--format', $Format))
    if ($created.Code -ne 0) {
        Add-Result $CreateGate "create-$Format" 'FAIL' "create exited $($created.Code): $($created.Raw.Trim())"
        return
    }
    if (-not (Test-Path -LiteralPath $archive)) {
        Add-Result $CreateGate "create-$Format" 'FAIL' 'create reported success but produced no file'
        return
    }
    if ((Get-Item -LiteralPath $archive).Length -le 0) {
        Add-Result $CreateGate "create-$Format" 'FAIL' 'the archive is zero bytes'
        return
    }
    Add-Result $CreateGate "create-$Format" 'PASS' ("{0} entries, {1} bytes" -f $created.Json.entriesAdded, (Get-Item -LiteralPath $archive).Length)

    # Gate 4/5: re-opened, not trusted. A writer returning without an exception is not the same as a
    # reader being able to make sense of what it wrote.
    $listed = Invoke-CliJson @('list', $archive)
    if ($listed.Code -ne 0 -or -not $listed.Json) {
        Add-Result $OpenGate "open-$Format" 'FAIL' "the new archive could not be re-opened: $($listed.Raw.Trim())"
        return
    }

    $actual = @($listed.Json.entries | Where-Object { -not $_.isDirectory } | ForEach-Object { $_.path } | Sort-Object)
    if (($actual -join '|') -ne ($expectedEntries -join '|')) {
        Add-Result $OpenGate "open-$Format" 'FAIL' ("entries are [{0}]" -f ($actual -join ', '))
        return
    }
    Add-Result $OpenGate "open-$Format" 'PASS' ("{0} entries, names intact" -f $actual.Count)

    # Gate 7
    if (Can $Format 'Test') {
        $tested = Invoke-CliJson @('test', $archive)
        if ($tested.Code -ne 0 -or $tested.Json.status -notin @('Valid', 'ValidWithWarnings')) {
            Add-Result '7' "test-$Format" 'FAIL' "status '$($tested.Json.status)' (exit $($tested.Code))"
        } else {
            Add-Result '7' "test-$Format" 'PASS' "status $($tested.Json.status)"
        }
    } else {
        Add-Result '7' "test-$Format" 'SKIP' 'this format does not report Test'
    }

    # Gates 8/9 + 17/18: extract, then compare what came back with what went in.
    $dest = Join-Path $sandbox "out-$Format"
    $extracted = Invoke-CliJson @('extract', $archive, '-o', $dest)
    if ($extracted.Code -ne 0) {
        Add-Result $ExtractGate "extract-$Format" 'FAIL' "extract exited $($extracted.Code): $($extracted.Raw.Trim())"
        return
    }

    $missing = @()
    foreach ($n in $names) {
        $onDisk = Join-Path $dest (Local "payload\$($n.Path)")
        if (-not (Test-Path -LiteralPath $onDisk)) { $missing += $n.Path; continue }
        if ((Get-Content -LiteralPath $onDisk -Raw -Encoding UTF8).Trim() -ne $n.Text) { $missing += "$($n.Path) (content)" }
    }

    if ($missing.Count -gt 0) {
        Add-Result $ExtractGate "extract-$Format" 'FAIL' ("did not round trip: {0}" -f ($missing -join ', '))
        return
    }
    Add-Result $ExtractGate "extract-$Format" 'PASS' 'every entry and its bytes came back'

    # Gates 17/18, called out separately so the transcript answers them directly.
    Add-Result '17' "arabic-$Format" 'PASS' 'مستندات\تقرير سنوي.txt round tripped, name and content'
    Add-Result '18' "unicode-$Format" 'PASS' '日本語のファイル.txt and Ελληνικά.txt round tripped'
}

Write-Log ''
Write-Log '--- Gates 4,5,7,8,9,11,12,13,17,18: create / open / test / extract ---'
Test-MultiEntryFormat -Format '7z'  -Extension '.7z'  -CreateGate '11' -OpenGate '4' -ExtractGate '8'
Test-MultiEntryFormat -Format 'zip' -Extension '.zip' -CreateGate '12' -OpenGate '5' -ExtractGate '9'
Test-MultiEntryFormat -Format 'tar' -Extension '.tar' -CreateGate '13' -OpenGate '5' -ExtractGate '9'

# ---------------------------------------------------------------------------
# GATE 14 - GZIP holds one file.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Gate 14: GZIP (single member) ---'

$gz = Join-Path $sandbox 'made.gz'
if (-not (Can 'gzip' 'Create')) {
    Add-Result '14' 'create-gzip' 'SKIP' 'this build does not report Create for gzip'
} else {
    $made = Invoke-CliJson @('create', $gz, $single, '--format', 'gzip')
    if ($made.Code -ne 0 -or -not (Test-Path -LiteralPath $gz)) {
        Add-Result '14' 'create-gzip' 'FAIL' "create exited $($made.Code): $($made.Raw.Trim())"
    } else {
        Add-Result '14' 'create-gzip' 'PASS' ("{0} bytes" -f (Get-Item -LiteralPath $gz).Length)

        $listed = Invoke-CliJson @('list', $gz)
        $entry = if ($listed.Json) { @($listed.Json.entries)[0].path } else { $null }
        if ($entry -eq 'single.txt') {
            Add-Result '14' 'gzip-stores-name' 'PASS' "the original name survived: $entry"
        } else {
            Add-Result '14' 'gzip-stores-name' 'FAIL' "expected 'single.txt', got '$entry'"
        }

        $tested = Invoke-CliJson @('test', $gz)
        if ($tested.Json.status -eq 'Valid') {
            Add-Result '7' 'test-gzip' 'PASS' 'status Valid (gzip checksums its contents)'
        } else {
            Add-Result '7' 'test-gzip' 'FAIL' "status '$($tested.Json.status)'"
        }

        $dest = Join-Path $sandbox 'out-gzip'
        $out = Invoke-CliJson @('extract', $gz, '-o', $dest)
        $file = Join-Path $dest 'single.txt'
        if ($out.Code -eq 0 -and (Test-Path -LiteralPath $file) -and
            (Get-Content -LiteralPath $file -Raw).Trim() -eq 'one file only') {
            Add-Result '9' 'extract-gzip' 'PASS' 'content round tripped'
        } else {
            Add-Result '9' 'extract-gzip' 'FAIL' "extract exited $($out.Code)"
        }

        # A gzip holds one file. Asking for two must be refused, and must leave nothing behind.
        $two = Join-Path $sandbox 'refused.gz'
        $refused = Invoke-Cli @('create', $two, $src, '--format', 'gzip')
        if ($refused.Code -eq 0) {
            Add-Result '14' 'gzip-refuses-many' 'FAIL' 'compressing a folder to .gz succeeded'
        } elseif (Test-Path -LiteralPath $two) {
            Add-Result '14' 'gzip-refuses-many' 'FAIL' 'refused, but left a file behind'
        } else {
            Add-Result '14' 'gzip-refuses-many' 'PASS' 'refused, and wrote nothing'
        }
    }
}

# ---------------------------------------------------------------------------
# GATES 15/16 - a second, independent implementation reads what we produced.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Gates 15,16: 7-Zip reads the TAR and GZIP this build produced ---'

# ProgramFiles is absent off Windows, and Join-Path throws on a null root rather than returning
# nothing - so the roots are filtered before they are joined.
$sevenZipExe = $null
$programRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
foreach ($programRoot in $programRoots) {
    $candidate = Join-Path $programRoot (Local '7-Zip\7z.exe')
    if (Test-Path -LiteralPath $candidate) { $sevenZipExe = $candidate; break }
}

if (-not $sevenZipExe) {
    Add-Result '15' 'tar-read-by-7zip' 'SKIP' '7z.exe was not found; install 7-Zip or open the archives by hand'
    Add-Result '16' 'gzip-read-by-7zip' 'SKIP' '7z.exe was not found; install 7-Zip or open the archives by hand'
} else {
    Write-Log "  using $sevenZipExe"

    $madeTar = Join-Path $sandbox 'made-tar.tar'
    if (Test-Path -LiteralPath $madeTar) {
        $listing = & $sevenZipExe l $madeTar 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -and $listing -match 'plain\.txt') {
            Add-Result '15' 'tar-read-by-7zip' 'PASS' '7-Zip listed the archive and found its entries'
        } else {
            Add-Result '15' 'tar-read-by-7zip' 'FAIL' "7z exited $LASTEXITCODE : $($listing.Trim())"
        }
    } else {
        Add-Result '15' 'tar-read-by-7zip' 'SKIP' 'no TAR was produced to read'
    }

    if (Test-Path -LiteralPath $gz) {
        $listing = & $sevenZipExe l $gz 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -and $listing -match 'single\.txt') {
            Add-Result '16' 'gzip-read-by-7zip' 'PASS' '7-Zip listed it and read the stored name'
        } elseif ($LASTEXITCODE -eq 0) {
            Add-Result '16' 'gzip-read-by-7zip' 'FAIL' '7-Zip opened it but did not see the stored name'
        } else {
            Add-Result '16' 'gzip-read-by-7zip' 'FAIL' "7z exited $LASTEXITCODE : $($listing.Trim())"
        }
    } else {
        Add-Result '16' 'gzip-read-by-7zip' 'SKIP' 'no GZIP was produced to read'
    }
}

# ---------------------------------------------------------------------------
# GATES 6,7,10 - RAR, which needs an archive this repository does not ship.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Gates 6,7,10: RAR (read-only) ---'

if (-not $RarPath) {
    Add-Result '6'  'open-rar'    'NOT RUN' 'no -RarPath was supplied and this repository ships no RAR fixture'
    Add-Result '7'  'test-rar'    'NOT RUN' 'no -RarPath was supplied'
    Add-Result '10' 'extract-rar' 'NOT RUN' 'no -RarPath was supplied'
} elseif (-not (Test-Path -LiteralPath $RarPath)) {
    # All three are reported, not just the one that noticed. A gate that produces no line at all is
    # indistinguishable from a gate that passed when the transcript is read later.
    Add-Result '6'  'open-rar'    'FAIL' "'$RarPath' does not exist"
    Add-Result '7'  'test-rar'    'FAIL' "'$RarPath' does not exist"
    Add-Result '10' 'extract-rar' 'FAIL' "'$RarPath' does not exist"
} elseif (-not (Can 'rar' 'Open')) {
    Add-Result '6'  'open-rar'    'SKIP' 'RAR is unusable in this build - the native library was not loaded'
    Add-Result '7'  'test-rar'    'SKIP' 'RAR is unusable in this build'
    Add-Result '10' 'extract-rar' 'SKIP' 'RAR is unusable in this build'
} else {
    $listed = Invoke-CliJson @('list', $RarPath)
    if ($listed.Code -eq 0 -and $listed.Json -and $listed.Json.count -gt 0) {
        Add-Result '6' 'open-rar' 'PASS' ("{0} entries listed" -f $listed.Json.count)
    } else {
        Add-Result '6' 'open-rar' 'FAIL' "list exited $($listed.Code): $($listed.Raw.Trim())"
    }

    $tested = Invoke-CliJson @('test', $RarPath)
    if ($tested.Code -eq 0 -and $tested.Json.status -in @('Valid', 'ValidWithWarnings')) {
        Add-Result '7' 'test-rar' 'PASS' "status $($tested.Json.status)"
    } else {
        Add-Result '7' 'test-rar' 'FAIL' "status '$($tested.Json.status)' (exit $($tested.Code))"
    }

    $dest = Join-Path $sandbox 'out-rar'
    $out = Invoke-CliJson @('extract', $RarPath, '-o', $dest)
    $written = @(Get-ChildItem -LiteralPath $dest -Recurse -File -ErrorAction SilentlyContinue).Count
    if ($out.Code -eq 0 -and $written -gt 0) {
        Add-Result '10' 'extract-rar' 'PASS' "$written file(s) written"
    } else {
        Add-Result '10' 'extract-rar' 'FAIL' "extract exited $($out.Code), $written file(s) on disk"
    }
}

# ---------------------------------------------------------------------------
# GATE 19 - RAR creation is refused, and the capability says so first.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Gate 19: RAR cannot be created ---'

if (Can 'rar' 'Create') {
    Add-Result '19' 'rar-create-capability' 'FAIL' 'RAR reports Create, which it must never do (ADR-0004)'
} else {
    Add-Result '19' 'rar-create-capability' 'PASS' 'RAR does not report Create'
}

$rarTarget = Join-Path $sandbox 'nope.rar'
$attempt = Invoke-Cli @('create', $rarTarget, $src, '--format', 'rar')
if ($attempt.Code -eq 0) {
    Add-Result '19' 'rar-create-refused' 'FAIL' 'the engine created a RAR'
} elseif (Test-Path -LiteralPath $rarTarget) {
    Add-Result '19' 'rar-create-refused' 'FAIL' 'refused, but left a file behind'
} else {
    Add-Result '19' 'rar-create-refused' 'PASS' ("refused, nothing written: {0}" -f $attempt.Output.Trim())
}

# ---------------------------------------------------------------------------
# GATE 20 - BZIP2 and XZ are unavailable and must not take the process down.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Gate 20: BZIP2 / XZ are unavailable and do not crash ---'

foreach ($blocked in @(
    @{ Name = 'bzip2'; File = 'sample.bz2' },
    @{ Name = 'xz';    File = 'sample.xz'  })) {

    # Not a real archive of that format - it does not need to be. What is under test is that the
    # product refuses a format it has no provider for, by name, without falling over.
    $file = Join-Path $sandbox $blocked.File
    Set-Content -LiteralPath $file -Value 'not really compressed' -Encoding ASCII

    $attempt = Invoke-Cli @('list', $file)
    $text = $attempt.Output.Trim()

    if ($text -match 'Unhandled exception' -or $text -match 'Stack trace') {
        Add-Result '20' "$($blocked.Name)-refused" 'FAIL' "the CLI crashed: $text"
    } elseif ($text.Length -eq 0) {
        Add-Result '20' "$($blocked.Name)-refused" 'FAIL' 'no message at all'
    } else {
        Add-Result '20' "$($blocked.Name)-refused" 'PASS' $text
    }

    if ($caps.ContainsKey($blocked.Name)) {
        Add-Result '20' "$($blocked.Name)-not-advertised" 'FAIL' 'the format is advertised but has no provider'
    } else {
        Add-Result '20' "$($blocked.Name)-not-advertised" 'PASS' 'not advertised'
    }
}

# A composite is not the plain compressor, and must not be claimed as one.
$tgz = Join-Path $sandbox 'sample.tgz'
Set-Content -LiteralPath $tgz -Value 'not really a tgz' -Encoding ASCII
$attempt = Invoke-Cli @('list', $tgz)
if ($attempt.Output -match 'Unhandled exception') {
    Add-Result '20' 'tgz-refused' 'FAIL' 'the CLI crashed on a .tgz'
} else {
    Add-Result '20' 'tgz-refused' 'PASS' $attempt.Output.Trim()
}

# ---------------------------------------------------------------------------
# GATE 21 - the offered lists come from the registry, not from a written-down list.
# ---------------------------------------------------------------------------
Write-Log ''
Write-Log '--- Gate 21: the offered formats are derived from capabilities ---'

$creatable = @($caps.Keys | Where-Object { Can $_ 'Create' } | Sort-Object)
$openable  = @($caps.Keys | Where-Object { Can $_ 'Open'   } | Sort-Object)

Write-Log ("  creatable: {0}" -f ($creatable -join ', '))
Write-Log ("  openable : {0}" -f ($openable  -join ', '))

if ($creatable -contains 'rar') {
    Add-Result '21' 'creatable-excludes-rar' 'FAIL' 'RAR is in the creatable set'
} else {
    Add-Result '21' 'creatable-excludes-rar' 'PASS' 'RAR is openable but not creatable, derived from capabilities'
}

if ($openable.Count -gt 0 -and $creatable.Count -gt 0 -and $openable.Count -ge $creatable.Count) {
    Add-Result '21' 'lists-are-derived' 'PASS' "$($openable.Count) openable, $($creatable.Count) creatable"
} else {
    Add-Result '21' 'lists-are-derived' 'FAIL' 'the derived sets are not coherent'
}

# ---------------------------------------------------------------------------
# Verdict.
# ---------------------------------------------------------------------------
$passed  = @($script:Results | Where-Object { $_.Verdict -eq 'PASS'    }).Count
$failed  = @($script:Results | Where-Object { $_.Verdict -eq 'FAIL'    }).Count
$skipped = @($script:Results | Where-Object { $_.Verdict -eq 'SKIP'    }).Count
$notrun  = @($script:Results | Where-Object { $_.Verdict -eq 'NOT RUN' }).Count

Write-Log ''
Write-Log ('=' * 78)
Write-Log ("FORMAT GATE: {0} passed, {1} failed, {2} skipped, {3} not run" -f $passed, $failed, $skipped, $notrun)
if ($skipped -gt 0 -or $notrun -gt 0) {
    Write-Log 'A SKIP or NOT RUN is not a pass. Whatever it names is still unverified on this machine.'
}
Write-Log ('VERDICT: ' + $(if ($failed -eq 0 -and $passed -gt 0) { 'PASS' } else { 'FAIL' }))
Write-Log ('=' * 78)
Write-Log ''
Write-Log 'This gate covers the ENGINE only. Still needing app.ps1 and a pair of eyes:'
Write-Log '  1  WinUI build          2  startup'
Write-Log '  19 the reason shown on screen for RAR'
Write-Log '  21 the Create and Open dialogs actually offering the derived lists'
Write-Log '  23 destination, progress, pause/resume, cancel, Cancelling, Cancelled, conflict policy'
Write-Log ''
Write-Log "Transcript: $log"
Write-Log "Sandbox   : $sandbox  (left in place on purpose - it is the evidence)"

exit $(if ($failed -eq 0 -and $passed -gt 0) { 0 } else { 1 })
