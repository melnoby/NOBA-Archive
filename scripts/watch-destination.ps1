<#
.SYNOPSIS
    Watches an extraction destination and reports whether it is still growing.

.DESCRIPTION
    The cancel criterion is not "the badge said Cancelled". It is "the bytes stopped". Those are
    different claims, and the drop that fixed the batch extraction path exists because they had been
    confused for one another once already.

    This samples a folder twice, far enough apart that any writer would have shown itself in between,
    and reports whether the file count and the total size moved. It decides nothing about the archive
    and touches nothing: it counts, waits, counts again.

    An empty folder is reported as EMPTY, never as PASS. A destination that was never written to
    looks exactly like a destination that stopped, and treating the two the same is how an unproven
    criterion gets recorded as proven.

.PARAMETER Path
    The destination folder. Read it off the operation row or the strip in the app - both now name the
    folder the picker returned, which is the only way to be sure this is the folder that was written.

.PARAMETER Seconds
    How long to wait between samples. The gate asks for ten.

.PARAMETER ExpectAtLeast
    The number of files below which the run is not evidence of anything: a cancel that stopped before
    the first entry landed proves nothing about stopping mid-write.

.EXAMPLE
    .\scripts\watch-destination.ps1 -Path 'D:\noba-cancel-out'

.EXAMPLE
    .\scripts\watch-destination.ps1 -Path 'D:\noba-cancel-out' -Seconds 15 -ExpectAtLeast 100
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [int]$Seconds = 10,
    [int]$ExpectAtLeast = 1
)

$ErrorActionPreference = 'Stop'

function Measure-Destination {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        return [pscustomobject]@{ Exists = $false; Files = 0; Bytes = [long]0 }
    }

    $files = Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue
    $bytes = [long]0
    foreach ($file in $files) {
        $bytes += $file.Length
    }

    return [pscustomobject]@{ Exists = $true; Files = @($files).Count; Bytes = $bytes }
}

Write-Host ''
Write-Host 'NOBA - destination watch' -ForegroundColor Cyan
Write-Host "  path   : $Path"
Write-Host "  window : $Seconds s"
Write-Host ''

$first = Measure-Destination -Root $Path

if (-not $first.Exists) {
    Write-Host "NOT FOUND - '$Path' does not exist." -ForegroundColor Red
    Write-Host 'This is not evidence that a cancel worked. Read the destination off the operation row' -ForegroundColor Red
    Write-Host 'in the app - it names the folder the picker actually returned - and watch that one.' -ForegroundColor Red
    exit 2
}

Write-Host ("  sample 1: {0} file(s), {1:N0} bytes" -f $first.Files, $first.Bytes)
Start-Sleep -Seconds $Seconds
$second = Measure-Destination -Root $Path
Write-Host ("  sample 2: {0} file(s), {1:N0} bytes" -f $second.Files, $second.Bytes)
Write-Host ''

if ($first.Files -eq 0 -and $second.Files -eq 0) {
    Write-Host "EMPTY - nothing was ever written to '$Path'." -ForegroundColor Yellow
    Write-Host 'An empty folder is not a stopped extraction. Either this is the wrong folder, or the' -ForegroundColor Yellow
    Write-Host 'cancel landed before the first entry - and neither proves the criterion.' -ForegroundColor Yellow
    exit 2
}

$grewFiles = $second.Files - $first.Files
$grewBytes = $second.Bytes - $first.Bytes

if ($grewFiles -ne 0 -or $grewBytes -ne 0) {
    Write-Host "FAIL - still being written." -ForegroundColor Red
    Write-Host ("  {0:+#;-#;0} file(s), {1:+#;-#;0} byte(s) in {2}s" -f $grewFiles, $grewBytes, $Seconds) -ForegroundColor Red
    Write-Host 'The operation reported Cancelled while the engine kept writing. That is the defect this' -ForegroundColor Red
    Write-Host 'gate exists to catch: report it, do not re-run hoping for a green result.' -ForegroundColor Red
    exit 1
}

if ($second.Files -lt $ExpectAtLeast) {
    Write-Host ("INCONCLUSIVE - only {0} file(s), fewer than the {1} this run needs." -f $second.Files, $ExpectAtLeast) `
        -ForegroundColor Yellow
    Write-Host 'Writing stopped, but too little had been written for that to mean anything. Cancel later' -ForegroundColor Yellow
    Write-Host 'in the extraction - after the progress bar has visibly moved - and watch again.' -ForegroundColor Yellow
    exit 2
}

Write-Host ("PASS - unchanged for {0}s at {1} file(s), {2:N0} bytes." -f $Seconds, $second.Files, $second.Bytes) `
    -ForegroundColor Green
Write-Host 'Writing stopped. Check the row still reads Cancelled and reports no completion figures.' -ForegroundColor Green
exit 0
