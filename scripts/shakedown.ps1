#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the NOBA Archive alpha over every ZIP, 7Z and RAR already on this machine and compares it
    against 7-Zip.

.DESCRIPTION
    Priority 5 of the agreed order: real usage feedback. Every fixture in the 349-test suite was written
    by the same person who wrote the code being tested, which means the suite proves the implementation
    matches its author's idea of a ZIP file. Your Downloads folder does not care what that idea was.

    For each archive found this compares NOBA's entry count against 7-Zip's and reports the disagreements.
    A disagreement is the finding — it does not say who is right.

    By default nothing is extracted and nothing is modified: listing only. -Extract adds a second,
    slower pass that extracts each archive with both tools into temporary folders and compares the
    results file by file, by SHA-256. That is the only check that exercises a decoder, and it is what
    caught a RAR whose entries all failed while the extraction reported success.

.EXAMPLE
    .\scripts\shakedown.ps1
    .\scripts\shakedown.ps1 -Path "D:\Archives" -Max 200
    .\scripts\shakedown.ps1 -Path "D:\Archives\rar" -Extract
#>
[CmdletBinding()]
param(
    [string]   $Exe,
    [string[]] $Path,
    [string]   $SevenZip,
    [int]      $Max = 60,
    [switch]   $Extract,
    [string]   $Include = 'zip,7z,rar'
)

$ErrorActionPreference = 'Stop'

<#
    Native commands need their own error handling.

    Under PowerShell 5.1 anything a native program writes to stderr becomes an ErrorRecord, and with
    $ErrorActionPreference = 'Stop' that record terminates the script — even when stderr is redirected
    to $null. noba-ar deliberately writes its human-readable listing to stderr in --json mode so the
    pipe stays clean, so the very first archive killed the run.

    Isolating the preference around the call is the fix; the exit code is what decides success here,
    which is what it should have been keyed on in the first place.
#>
function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string]   $FilePath,
        [string[]] $ArgumentList = @(),
        [switch]   $KeepStderr
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($KeepStderr) {
            $output = & $FilePath @ArgumentList 2>&1
        } else {
            $output = & $FilePath @ArgumentList 2>$null
        }
        return [pscustomobject]@{
            Output   = @($output)
            ExitCode = $LASTEXITCODE
        }
    } catch {
        return [pscustomobject]@{
            Output   = @($_.Exception.Message)
            ExitCode = -1
        }
    } finally {
        $ErrorActionPreference = $previous
    }
}

<#
    Extracts one archive with both tools and compares the trees by content hash.

    Listing agreement says the headers were read the same way. It says nothing about a decoder, and a
    decoder is where the interesting failures live: a real RAR extracted through the batch path once
    reported success having written nothing at all, because a per-entry failure was reported through a
    channel nobody was reading.
#>
function Compare-Extraction {
    param(
        [Parameter(Mandatory)] $Archive,
        [Parameter(Mandatory)] [string] $Exe,
        [string] $SevenZip
    )

    $root = Join-Path ([IO.Path]::GetTempPath()) ("noba-shakedown-" + [Guid]::NewGuid().ToString('N'))
    $ours = Join-Path $root 'noba'
    $theirs = Join-Path $root 'sevenzip'
    New-Item -ItemType Directory -Path $ours -Force | Out-Null
    New-Item -ItemType Directory -Path $theirs -Force | Out-Null

    try {
        $mine = Invoke-Native -FilePath $Exe -ArgumentList @('extract', $Archive.FullName, '-o', $ours) -KeepStderr
        if (-not $SevenZip) {
            return [pscustomobject]@{ Verdict = 'NOBA-OK'; Detail = 'no 7-Zip comparison'; Files = 0 }
        }

        $seven = Invoke-Native -FilePath $SevenZip -ArgumentList @('x', "-o$theirs", '-y', '-p', $Archive.FullName)

        if ($mine.ExitCode -ne 0 -and $seven.ExitCode -ne 0) {
            return [pscustomobject]@{ Verdict = 'AGREE'; Detail = 'both refused to extract'; Files = 0 }
        }
        if ($mine.ExitCode -ne 0) {
            $lines = @($mine.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
            $why = if ($lines.Count -gt 0) { $lines[-1] } else { "exit $($mine.ExitCode)" }
            return [pscustomobject]@{ Verdict = 'EXTRACT-FAILED'; Detail = $why; Files = 0 }
        }
        if ($seven.ExitCode -ne 0) {
            return [pscustomobject]@{ Verdict = 'NOBA-ONLY'; Detail = '7-Zip could not extract it'; Files = 0 }
        }

        # Hash both trees by relative path. Comparing counts alone would pass an extraction that wrote
        # the right number of wrong files.
        $map = @{}
        foreach ($side in @(@{ Dir = $ours; Key = 'A' }, @{ Dir = $theirs; Key = 'B' })) {
            foreach ($file in @(Get-ChildItem -Path $side.Dir -Recurse -File -Force -ErrorAction SilentlyContinue)) {
                $rel = $file.FullName.Substring($side.Dir.Length).TrimStart('\', '/')
                if (-not $map.ContainsKey($rel)) { $map[$rel] = @{} }
                $map[$rel][$side.Key] = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
            }
        }

        $onlyOurs = @($map.Keys | Where-Object { -not $map[$_].ContainsKey('B') })
        $onlyTheirs = @($map.Keys | Where-Object { -not $map[$_].ContainsKey('A') })
        $differing = @($map.Keys | Where-Object { $map[$_].ContainsKey('A') -and $map[$_].ContainsKey('B') -and $map[$_].A -ne $map[$_].B })

        if ($onlyOurs.Count -eq 0 -and $onlyTheirs.Count -eq 0 -and $differing.Count -eq 0) {
            return [pscustomobject]@{ Verdict = 'AGREE'; Detail = ''; Files = $map.Count }
        }

        # NOBA refuses hostile entries by design, so files 7-Zip wrote and NOBA did not are not
        # automatically a defect - but they are always worth a human look, which is what this says.
        $parts = @()
        if ($differing.Count -gt 0)  { $parts += "$($differing.Count) file(s) differ: $($differing[0])" }
        if ($onlyTheirs.Count -gt 0) { $parts += "$($onlyTheirs.Count) only 7-Zip wrote: $($onlyTheirs[0])" }
        if ($onlyOurs.Count -gt 0)   { $parts += "$($onlyOurs.Count) only NOBA wrote: $($onlyOurs[0])" }
        return [pscustomobject]@{ Verdict = 'CONTENT-DIFF'; Detail = ($parts -join '; '); Files = $map.Count }
    } finally {
        Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Arabic entry names are the whole point of this exercise and the PS 5.1 console will mangle them
# without this.
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$repo = Resolve-Path "$PSScriptRoot\.."

if (-not $Exe) {
    $candidates = @(
        (Join-Path $repo 'artifacts\alpha\noba-ar.exe'),
        'C:\artifacts\alpha\noba-ar.exe'
    )
    # @() around the pipeline, not just around the literal: a single-element pipeline result is a
    # string, and indexing a string hands back one character.
    $hits = @($candidates | Where-Object { Test-Path $_ })
    if ($hits.Count -eq 0) {
        Write-Host "noba-ar.exe not found. Build it: .\scripts\alpha.ps1 -SkipTests" -ForegroundColor Yellow
        exit 1
    }
    $Exe = $hits[0]
} elseif (-not (Test-Path $Exe)) {
    # An explicitly named exe that is not there is a typo, not a finding. Say so once rather than
    # reporting NOBA-FAILED against every archive on the machine.
    Write-Host "-Exe '$Exe' does not exist." -ForegroundColor Red
    exit 1
}

if (-not $Path) {
    $Path = @(
        "$env:USERPROFILE\Downloads",
        "$env:USERPROFILE\Desktop",
        "$env:USERPROFILE\Documents"
    )
}

$sevenZip = $SevenZip
if (-not $sevenZip) {
    foreach ($p in @('C:\Program Files\7-Zip\7z.exe', 'C:\Program Files (x86)\7-Zip\7z.exe')) {
        if (Test-Path $p) { $sevenZip = $p; break }
    }
}

Write-Host "NOBA Archive - alpha shakedown" -ForegroundColor Cyan
Write-Host "  noba-ar : $Exe"
Write-Host "  7-Zip   : $(if ($sevenZip) { $sevenZip } else { 'not found - NOBA runs unchecked' })"
Write-Host ""

# Split on the invariant culture, and never bind a list as [string[]] on the command line: under
# -File, "800,3200" arrived as the single number 8003200 because the culture read the comma as a
# thousands separator. One string, split here, is the shape that survives every host.
$extensions = @($Include.Split(',') | ForEach-Object { $_.Trim().TrimStart('.').ToLowerInvariant() } | Where-Object { $_ })

$archives = @()
foreach ($root in $Path) {
    if (-not (Test-Path $root)) { continue }
    foreach ($ext in $extensions) {
        $found = @(Get-ChildItem -Path $root -Filter "*.$ext" -Recurse -File -ErrorAction SilentlyContinue)
        $archives += $found
    }
}
$archives = @($archives | Select-Object -First $Max)

if ($archives.Count -eq 0) {
    Write-Host "No $($extensions -join '/') files found under:" -ForegroundColor Yellow
    $Path | ForEach-Object { Write-Host "  $_" }
    Write-Host "Pass -Path to point somewhere else."
    exit 0
}

Write-Host "$($archives.Count) archive(s) to check." -ForegroundColor DarkGray
Write-Host ""

$results = @()

foreach ($archive in $archives) {
    $row = [ordered]@{
        Name      = $archive.Name
        SizeMB    = [math]::Round($archive.Length / 1MB, 2)
        Compared  = 0
        Noba      = $null
        SevenZip  = $null
        Encoding  = ''
        Verdict   = ''
        Detail    = ''
    }

    # --- NOBA ---------------------------------------------------------------
    # --json puts one machine-readable document on stdout and all human text on stderr, so redirecting
    # stderr away leaves clean JSON.
    $listed = Invoke-Native -FilePath $Exe -ArgumentList @('list', $archive.FullName, '--json')

    if ($listed.ExitCode -eq 0 -and $listed.Output.Count -gt 0) {
        try {
            $parsed = ($listed.Output -join "`n") | ConvertFrom-Json
            $row.Noba = $parsed.count
            if ($parsed.nameEncoding) {
                $row.Encoding = $parsed.nameEncoding
                if ($parsed.nameEncodingIsGuess) { $row.Encoding += ' (guess)' }
            }
        } catch {
            $row.Verdict = 'NOBA-BADJSON'
            $row.Detail  = $_.Exception.Message
        }
    } else {
        $row.Verdict = 'NOBA-FAILED'
        $plain = Invoke-Native -FilePath $Exe -ArgumentList @('list', $archive.FullName) -KeepStderr
        $lines = @($plain.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
        # Last line, not first: the failure is what the tool said last, and in --json mode the
        # earlier lines are the human listing that deliberately goes to stderr.
        $row.Detail = if ($lines.Count -gt 0) { $lines[-1] } else { "exit code $($plain.ExitCode)" }
    }

    # --- 7-Zip --------------------------------------------------------------
    if ($sevenZip) {
        # -tzip pins the handler. Without it 7-Zip silently falls back to whatever handler does match,
        # so a Compound File named .zip listed 10 entries via the Compound handler and the comparison
        # reported NOBA-FAILED against a file that is not a ZIP at all. -ba is the bare listing;
        # -p supplies an empty password so an encrypted archive cannot hang the run on a prompt.
        # The handler is pinned to what the extension claims, per archive. Without it 7-Zip silently
        # falls back to whatever handler does match, so a Compound File named .zip listed 10 entries
        # through the Compound handler and the comparison reported a NOBA defect against a file that
        # is not a ZIP at all.
        # RAR4 and RAR5 are separate handlers behind one extension in 7-Zip exactly as they are in
        # NOBA, and -trar refuses a RAR5 file. Trying both is what the extension actually means.
        $handlers = switch ($archive.Extension.ToLowerInvariant()) {
            '.7z'  { @('-t7z') }
            '.rar' { @('-trar5', '-trar') }
            default { @('-tzip') }
        }

        $seven = $null
        foreach ($handler in $handlers) {
            $seven = Invoke-Native -FilePath $sevenZip -ArgumentList @('l', $handler, '-ba', '-p', $archive.FullName)
            if ($seven.ExitCode -eq 0) { break }
        }
        if ($seven.ExitCode -eq 0) {
            $row.SevenZip = @($seven.Output | Where-Object { $_ -and "$_".Trim() }).Count
        } else {
            $row.SevenZip = -1   # 7-Zip could not open it as that format either.
        }
    }

    # --- verdict ------------------------------------------------------------
    if ($row.Verdict -eq 'NOBA-FAILED' -and $row.SevenZip -eq -1) {
        # Both refused it. That is agreement, not a defect - and reporting it as a defect is
        # exactly the false positive this comparison produced on its first real run.
        $row.Verdict = 'BOTH-REFUSED'
    }

    if (-not $row.Verdict) {
        if ($null -eq $row.SevenZip) {
            $row.Verdict = 'NOBA-OK'
            $row.Detail  = 'no 7-Zip comparison'
        } elseif ($row.SevenZip -eq -1) {
            $row.Verdict = 'NOBA-ONLY'
            $row.Detail  = "noba=$($row.Noba), 7-Zip refused it as $($archive.Extension.TrimStart('.'))"
        } elseif ($row.Noba -eq $row.SevenZip) {
            $row.Verdict = 'AGREE'
        } else {
            $row.Verdict = 'MISMATCH'
            $row.Detail  = "noba=$($row.Noba) 7z=$($row.SevenZip)"
        }
    }

    if ($Extract -and $row.Verdict -in @('AGREE', 'NOBA-OK')) {
        $cmp = Compare-Extraction -Archive $archive -Exe $Exe -SevenZip $sevenZip
        if ($cmp.Verdict -ne 'AGREE') {
            $row.Verdict = $cmp.Verdict
            $row.Detail  = $cmp.Detail
        } elseif ($cmp.Files -gt 0) {
            $row.Compared = $cmp.Files
            $row.Detail = "$($cmp.Files) file(s) identical"
        } else {
            # "0 files identical" reads as agreement when it is usually "neither tool could decode
            # this" - which is the sort of quiet nothing that a shakedown exists to stop reporting
            # as a pass.
            $row.Detail = $cmp.Detail
        }
    }

    $colour = switch ($row.Verdict) {
        'AGREE'        { 'DarkGray' }
        'NOBA-OK'      { 'DarkGray' }
        'BOTH-REFUSED' { 'DarkGray' }
        'MISMATCH'     { 'Yellow' }
        'NOBA-ONLY'    { 'Yellow' }
        default        { 'Red' }
    }
    Write-Host ("{0,-12} {1,-46} {2}" -f $row.Verdict, $row.Name, $row.Detail) -ForegroundColor $colour

    $results += [pscustomobject]$row
}

# --- summary ----------------------------------------------------------------
Write-Host ""
Write-Host "=========================================================" -ForegroundColor Cyan
$byVerdict = $results | Group-Object Verdict | Sort-Object Name
foreach ($g in $byVerdict) {
    Write-Host ("{0,-14} {1}" -f $g.Name, $g.Count)
}

$interesting = @($results | Where-Object { $_.Verdict -notin @('AGREE', 'NOBA-OK', 'BOTH-REFUSED') })
$guessed = @($results | Where-Object { $_.Encoding -like '*guess*' })

Write-Host ""
if ($interesting.Count -eq 0) {
    Write-Host "NOBA agreed with 7-Zip on every archive it was given." -ForegroundColor Green
    if ($Extract) {
        $decoded = @($results | Where-Object { $_.Compared -gt 0 })
        $hashed = 0
        if ($decoded.Count -gt 0) { $hashed = ($decoded | Measure-Object Compared -Sum).Sum }
        Write-Host "Content compared on $($decoded.Count) of $($results.Count) archive(s), $hashed file(s) hashed." -ForegroundColor DarkGray
        if ($decoded.Count -lt $results.Count) {
            # An archive neither tool could decode contributes nothing but looks like a pass in the
            # verdict column. Saying how many were actually decoded is the difference between
            # "they agree" and "nothing was tested".
            Write-Host "The rest neither tool could decode, so they tested nothing." -ForegroundColor DarkGray
        }
    } else {
        Write-Host "That is a real result, but note what it does not cover: entry counts only," -ForegroundColor DarkGray
        Write-Host "no extraction, no content comparison, and only the archives that happen to be here." -ForegroundColor DarkGray
        Write-Host "Add -Extract to compare extracted bytes as well." -ForegroundColor DarkGray
    }
} else {
    Write-Host "$($interesting.Count) archive(s) worth looking at:" -ForegroundColor Yellow
    foreach ($r in $interesting) {
        Write-Host ("  {0,-12} {1}" -f $r.Verdict, $r.Name) -ForegroundColor Yellow
        if ($r.Detail) { Write-Host ("               {0}" -f $r.Detail) -ForegroundColor DarkGray }
    }
}

if ($guessed.Count -gt 0) {
    Write-Host "$($guessed.Count) archive(s) needed a guessed entry-name encoding (R16):" -ForegroundColor Yellow
    foreach ($r in $guessed) {
        Write-Host ("  {0,-46} {1}" -f $r.Name, $r.Encoding) -ForegroundColor Yellow
    }
    Write-Host "If any of those names look wrong, re-run that one with --name-encoding 1256" -ForegroundColor DarkGray
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $repo 'artifacts'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$csv = Join-Path $outDir "shakedown-$stamp.csv"
$results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
Write-Host ""
Write-Host "Full results: $csv" -ForegroundColor Cyan
