#Requires -Version 5.1
<#
.SYNOPSIS
    Measures NOBA Archive against 7-Zip, and measures the known solid-archive cost (R17, R18).

.DESCRIPTION
    Two open risks in the project are stated as opinions rather than numbers:

      R17  "competes with 7-Zip" - with not one measurement behind it.
      R18  solid 7z extraction is O(n^2), because each entry is a separate Extract call and a solid
           block decodes as a unit. Believed, argued from the design, never timed.

    This turns both into data. It builds archives of increasing entry count in two shapes - solid and
    non-solid - and times list, test and extract for NOBA and for 7-Zip on each. If R18 is real, the
    solid column grows quadratically while the non-solid column grows linearly, and the ratio between
    them is the size of the problem.

    Nothing here is destructive: everything is built and extracted under a temporary folder.

.EXAMPLE
    .\scripts\benchmark.ps1
    .\scripts\benchmark.ps1 -EntryCounts 100,400,1600 -BudgetSeconds 300
#>
[CmdletBinding()]
param(
    [string]   $Exe,
    [string]   $SevenZip,
    # A string, not int[], and that is deliberate. Under "powershell -File", every argument arrives as
    # a string: "50,200" bound to an [int[]] parameter is converted as a single number using culture
    # rules, and the comma is read as a thousands separator. The first run of this script built 50,200
    # files instead of two sets of 50 and 200, then died with "argument list too long". Parsing it
    # here means the same command line does the same thing however the script is invoked.
    # The default must reach the range where the effect is visible. At 50-800 entries the fixed
    # per-call overhead still dominates and the curve looks linear; the quadratic term only takes over
    # further out. An earlier default of 50,200,800 produced a confident "tracks linear growth" on
    # Windows while the identical code on Linux, measured out to 3200, was 257x slower than 7-Zip.
    # A benchmark whose defaults produce a false all-clear is worse than no benchmark.
    [string]   $EntryCounts = '200,800,3200',
    [int]      $EntryBytes = 4096,

    # A single measurement that exceeds this stops the run from growing further. Without it, an
    # O(n^2) path with a large n does not fail - it simply never finishes, which is worse.
    [int]      $BudgetSeconds = 120,
    [string]   $WorkRoot
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$repo = Resolve-Path "$PSScriptRoot\.."

$counts = @(
    $EntryCounts -split '[,;\s]+' |
        Where-Object { $_ } |
        ForEach-Object { [int]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) } |
        Sort-Object
)
if ($counts.Count -eq 0) {
    Write-Host "-EntryCounts did not contain any numbers." -ForegroundColor Red
    exit 1
}

function Invoke-Native {
    param([Parameter(Mandatory)][string] $FilePath, [string[]] $ArgumentList = @())
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @ArgumentList 2>$null
        return [pscustomobject]@{ Output = @($output); ExitCode = $LASTEXITCODE }
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Measure-Command2 {
    param([Parameter(Mandatory)][string] $FilePath, [string[]] $ArgumentList = @())
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-Native -FilePath $FilePath -ArgumentList $ArgumentList
    $watch.Stop()
    return [pscustomobject]@{
        Seconds  = [math]::Round($watch.Elapsed.TotalSeconds, 3)
        ExitCode = $result.ExitCode
    }
}

# --- tools -------------------------------------------------------------------
if (-not $Exe) {
    $hits = @(
        (Join-Path $repo 'artifacts\alpha\noba-ar.exe'),
        'C:\artifacts\alpha\noba-ar.exe'
    ) | Where-Object { Test-Path $_ }
    $hits = @($hits)
    if ($hits.Count -eq 0) {
        Write-Host "noba-ar.exe not found. Build it: .\scripts\alpha.ps1 -SkipTests" -ForegroundColor Yellow
        exit 1
    }
    $Exe = (Resolve-Path $hits[0]).Path
} elseif (-not (Test-Path $Exe)) {
    Write-Host "-Exe '$Exe' does not exist." -ForegroundColor Red
    exit 1
}

if (-not $SevenZip) {
    foreach ($p in @('C:\Program Files\7-Zip\7z.exe', 'C:\Program Files (x86)\7-Zip\7z.exe', '/usr/bin/7z')) {
        if (Test-Path $p) { $SevenZip = $p; break }
    }
}
if (-not $SevenZip) {
    Write-Host "7-Zip was not found; it is needed to build the fixtures." -ForegroundColor Red
    exit 1
}

if (-not $WorkRoot) { $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) "noba-bench" }
if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force }
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
$WorkRoot = (Resolve-Path $WorkRoot).Path

Write-Host "NOBA Archive - benchmark (R17 / R18)" -ForegroundColor Cyan
Write-Host "  noba-ar  : $Exe"
Write-Host "  7-Zip    : $SevenZip"
Write-Host "  entries  : $($counts -join ', ')   payload $EntryBytes bytes each"
Write-Host "  budget   : ${BudgetSeconds}s per measurement"
Write-Host ""

$results = @()
$stopSolid = $false

foreach ($count in $counts) {
    # --- build a source tree of $count files -------------------------------
    $source = Join-Path $WorkRoot "src-$count"
    New-Item -ItemType Directory -Path $source -Force | Out-Null

    # Compressible but not trivially so, and identical across runs.
    $payload = ([char[]](1..$EntryBytes | ForEach-Object { [char](65 + ($_ % 26)) })) -join ''
    for ($i = 0; $i -lt $count; $i++) {
        Set-Content -Path (Join-Path $source "file-$i.txt") -Value $payload -NoNewline -Encoding UTF8
    }

    foreach ($shape in @('nonsolid', 'solid')) {
        if ($shape -eq 'solid' -and $stopSolid) {
            Write-Host ("{0,-9} {1,6}  skipped - a previous solid measurement exceeded the budget" -f $shape, $count) -ForegroundColor DarkGray
            continue
        }

        $archive = Join-Path $WorkRoot "$shape-$count.7z"
        $switch = if ($shape -eq 'solid') { '-ms=on' } else { '-ms=off' }
        $null = Invoke-Native -FilePath $SevenZip -ArgumentList @('a', '-t7z', '-mx=1', $switch, $archive, $source)

        if (-not (Test-Path $archive)) {
            Write-Host "Could not build $archive" -ForegroundColor Red
            continue
        }

        $destination = Join-Path $WorkRoot "out-$shape-$count"

        $nobaList    = Measure-Command2 -FilePath $Exe -ArgumentList @('list', $archive, '--json')
        $nobaTest    = Measure-Command2 -FilePath $Exe -ArgumentList @('test', $archive)
        $nobaExtract = Measure-Command2 -FilePath $Exe -ArgumentList @('extract', $archive, '-o', $destination)

        $sevenList    = Measure-Command2 -FilePath $SevenZip -ArgumentList @('l', '-ba', $archive)
        $sevenTest    = Measure-Command2 -FilePath $SevenZip -ArgumentList @('t', $archive)
        $sevenExtract = Measure-Command2 -FilePath $SevenZip -ArgumentList @('x', '-y', "-o$destination-7z", $archive)

        $ratio = if ($sevenExtract.Seconds -gt 0) {
            [math]::Round($nobaExtract.Seconds / $sevenExtract.Seconds, 1)
        } else { $null }

        $row = [pscustomobject][ordered]@{
            Shape           = $shape
            Entries         = $count
            NobaListSec     = $nobaList.Seconds
            SevenListSec    = $sevenList.Seconds
            NobaTestSec     = $nobaTest.Seconds
            SevenTestSec    = $sevenTest.Seconds
            NobaExtractSec  = $nobaExtract.Seconds
            SevenExtractSec = $sevenExtract.Seconds
            ExtractRatio    = $ratio
            NobaFailed      = ($nobaList.ExitCode -ne 0 -or $nobaExtract.ExitCode -ne 0)
        }
        $results += $row

        $colour = if ($row.NobaFailed) { 'Red' } elseif ($ratio -and $ratio -gt 3) { 'Yellow' } else { 'DarkGray' }
        Write-Host ("{0,-9} {1,6}  list {2,7}s  test {3,7}s  extract {4,7}s   (7-Zip extract {5,7}s, x{6})" -f
            $shape, $count, $row.NobaListSec, $row.NobaTestSec, $row.NobaExtractSec, $row.SevenExtractSec, $ratio) -ForegroundColor $colour

        if ($shape -eq 'solid' -and $nobaExtract.Seconds -gt $BudgetSeconds) {
            Write-Host "  solid extraction exceeded the budget; larger solid sizes will be skipped." -ForegroundColor Yellow
            $stopSolid = $true
        }
    }
}

# --- what the numbers say ----------------------------------------------------
Write-Host ""
Write-Host "=========================================================" -ForegroundColor Cyan

$solid = @($results | Where-Object { $_.Shape -eq 'solid' } | Sort-Object Entries)

# The smallest entry count at which the quadratic term is observable above per-call overhead.
# Below this the measurement cannot distinguish linear from quadratic, and must not pretend to.
$ConclusiveEntryCount = 2000

if ($solid.Count -ge 2) {
    $last = $solid[$solid.Count - 1]
    $prev = $solid[$solid.Count - 2]

    # The last step, not first-to-last: asymptotic behaviour shows in the tail, and averaging it
    # against the small-n points where overhead dominates is what hides the problem.
    $entryGrowth = [double]$last.Entries / [double]$prev.Entries
    $timeGrowth  = if ($prev.NobaExtractSec -gt 0) { [double]$last.NobaExtractSec / [double]$prev.NobaExtractSec } else { 0 }

    Write-Host ""
    Write-Host "R18 - solid archive scaling (last step, $($prev.Entries) -> $($last.Entries) entries):" -ForegroundColor Cyan
    Write-Host ("  entries x{0}, NOBA extract time x{1}" -f
        [math]::Round($entryGrowth, 1), [math]::Round($timeGrowth, 1))

    if ($last.Entries -lt $ConclusiveEntryCount) {
        Write-Host "  -> INCONCLUSIVE. The largest archive here has $($last.Entries) entries, and below" -ForegroundColor Yellow
        Write-Host "     about $ConclusiveEntryCount the per-call overhead hides the quadratic term. This says" -ForegroundColor Yellow
        Write-Host "     nothing either way about R18." -ForegroundColor Yellow
        Write-Host "     Re-run: .\scripts\benchmark.ps1 -EntryCounts 800,3200,12800 -BudgetSeconds 600" -ForegroundColor Yellow
    } elseif ($timeGrowth -gt ($entryGrowth * 1.6)) {
        Write-Host "  -> worse than linear. R18 is real and measured, not just argued." -ForegroundColor Yellow
    } else {
        Write-Host "  -> tracks linear growth out to $($last.Entries) entries." -ForegroundColor Green
    }

    $worst = ($solid | Sort-Object ExtractRatio -Descending | Select-Object -First 1)
    if ($worst.ExtractRatio) {
        Write-Host ("  worst ratio against 7-Zip: x{0} at {1} entries" -f $worst.ExtractRatio, $worst.Entries) -ForegroundColor Cyan
    }
}

$failed = @($results | Where-Object { $_.NobaFailed })
if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "$($failed.Count) measurement(s) had NOBA fail outright - the timings there mean nothing:" -ForegroundColor Red
    foreach ($f in $failed) { Write-Host ("  {0} {1} entries" -f $f.Shape, $f.Entries) -ForegroundColor Red }
}

Write-Host ""
Write-Host "Caveats, so these numbers are not read as more than they are:" -ForegroundColor DarkGray
Write-Host "  - one machine, one run each, no warm-up, no repetition. Indicative, not rigorous." -ForegroundColor DarkGray
Write-Host "  - 7-Zip writes files itself; NOBA routes every byte through the hardened extraction" -ForegroundColor DarkGray
Write-Host "    writer (path validation, handle re-verification, guarded stream). Some of the gap is" -ForegroundColor DarkGray
Write-Host "    that work, and it is not a gap worth closing by removing it." -ForegroundColor DarkGray
Write-Host "  - compression level is fixed at -mx=1 so the fixture build does not dominate." -ForegroundColor DarkGray

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $repo 'artifacts'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$csv = Join-Path $outDir "benchmark-$stamp.csv"
$results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8

Write-Host ""
Write-Host "Full results: $csv" -ForegroundColor Cyan
Remove-Item $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
