#Requires -Version 5.1
<#
.SYNOPSIS
    R13 — the shipping-configuration validation run on Windows. Captures everything to one log.
.DESCRIPTION
    This is the run that closes R13. It uses the SHIPPING dependency set: real NuGet packages, real
    xUnit v3. It never passes -Offline, and it deliberately has no switch to make it do so.

    It does not stop at the first failure, and it does not try to make anything pass. Every phase runs,
    every exit code is recorded, and the full output goes to one transcript file so the whole run can be
    read and classified afterwards rather than reconstructed from a scrollback buffer.

    Send back the file it prints at the end. That single file is the evidence.
.EXAMPLE
    .\scripts\validate.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $root 'artifacts'
$log = Join-Path $logDir "r13-windows-validation-$stamp.txt"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$phases = New-Object System.Collections.Specialized.OrderedDictionary

<#
    Test totals, added up by the script rather than by whoever reads it.

    Every suite prints its own "Total: N, Errors: E, Failed: F, Skipped: S" line and nothing prints the
    sum, so the sum has been arrived at by hand — and got published wrong twice. A number nobody
    computed is not evidence, and this is a machine that never mis-adds.
#>
$testTotals = [ordered]@{ Total = 0; Errors = 0; Failed = 0; Skipped = 0; Suites = 0 }

function Write-Log {
    param([string]$Text)
    Write-Host $Text
    Add-Content -LiteralPath $log -Value $Text -Encoding UTF8
}

function Invoke-Phase {
    param([string]$Name, [string]$Exe, [string[]]$PhaseArgs)

    Write-Log ''
    Write-Log ('=' * 78)
    Write-Log "PHASE: $Name"
    Write-Log "COMMAND: $Exe $($PhaseArgs -join ' ')"
    Write-Log ('=' * 78)

    $output = & $Exe @PhaseArgs 2>&1 | Out-String
    $code = $LASTEXITCODE

    Write-Log $output.TrimEnd()
    Write-Log "EXIT CODE: $code"

    foreach ($line in [regex]::Matches(
        $output,
        'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+)')) {
        $testTotals.Suites  += 1
        $testTotals.Total   += [int]$line.Groups[1].Value
        $testTotals.Errors  += [int]$line.Groups[2].Value
        $testTotals.Failed  += [int]$line.Groups[3].Value
        $testTotals.Skipped += [int]$line.Groups[4].Value
    }

    $phases[$Name] = $code
    return $code
}

Push-Location $root
try {
    Write-Log "NOBA Archive - R13 shipping-configuration validation"
    Write-Log "Started: $(Get-Date -Format 'o')"
    Write-Log ''
    Write-Log '--- environment fingerprint ---'
    Write-Log "PowerShell      : $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
    Write-Log "OS              : $([System.Environment]::OSVersion.VersionString)"
    Write-Log "OS build        : $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"
    Write-Log "Architecture    : $env:PROCESSOR_ARCHITECTURE"
    Write-Log "Repo            : $root"

    <#
        Which source tree is this?

        Every phase below can report PASS against a tree that predates the change being validated, and
        nothing else in this transcript would say so - it has happened twice. The stamp is read from the
        source file rather than from the built binary so that it is printed even when the build fails,
        and so that it describes what is about to be compiled rather than what was compiled last time.
    #>
    $stampFile = Join-Path $root 'src\NOBA.Archive.Cli\BuildStamp.cs'
    $stamp = 'MISSING - this tree predates the build stamp'
    if (Test-Path $stampFile) {
        # Not $match: PowerShell is case-insensitive, so that name is the automatic $Matches.
        $stampMatch = Select-String -Path $stampFile -Pattern 'Value\s*=\s*"([^"]+)"' | Select-Object -First 1
        if ($stampMatch) { $stamp = $stampMatch.Matches[0].Groups[1].Value }
    }
    Write-Log "Source drop     : $stamp"

    <#
        Is the alpha binary the same drop?

        artifacts\alpha\noba-ar.exe is published by alpha.ps1 and by nothing else, so it survives any
        number of source drops. A gate step that reads its banner to confirm which tree it is testing
        will happily confirm a tree from a week ago - which is the exact failure the build stamp was
        introduced to prevent, moved one artifact to the left. Not a phase failure: the alpha is not
        part of this solution's contract. But it must never be silently stale.
    #>
    $alphaExe = Join-Path $root 'artifacts\alpha\noba-ar.exe'
    if (Test-Path $alphaExe) {
        $alphaStamp = 'unreadable'
        $banner = & $alphaExe version 2>&1
        $bannerMatch = $banner | Select-String -Pattern 'Source drop:\s*(\S+)' | Select-Object -First 1
        if ($bannerMatch) { $alphaStamp = $bannerMatch.Matches[0].Groups[1].Value }

        if ($alphaStamp -eq $stamp) {
            Write-Log "Alpha binary    : $alphaStamp (current)"
        }
        else {
            Write-Log "Alpha binary    : $alphaStamp"
            Write-Log "*** STALE ALPHA - artifacts\alpha\noba-ar.exe is NOT this drop."
            Write-Log "*** Its results describe $alphaStamp. Run .\scripts\alpha.ps1 to rebuild it."
        }
    }
    else {
        Write-Log "Alpha binary    : not built (run .\scripts\alpha.ps1)"
    }

    # The toolchain identity matters as much as the result: 'it failed' and 'it failed on SDK 10.0.100'
    # are different reports.
    Invoke-Phase 'dotnet --info' 'dotnet' @('--info') | Out-Null
    Invoke-Phase 'nuget sources' 'dotnet' @('nuget', 'list', 'source') | Out-Null

    # Restore is its own phase because a restore failure and a compile failure are different diagnoses
    # and must not be reported as one number.
    Invoke-Phase 'restore' 'dotnet' @('restore', 'NOBA.Archive.sln', '--nologo') | Out-Null

    Invoke-Phase 'build' 'dotnet' @('build', 'NOBA.Archive.sln', '-c', $Configuration, '--no-restore', '--nologo') | Out-Null

    $suites = New-Object System.Collections.Specialized.OrderedDictionary
    $suites.Add('UNIT TESTS (Core)',           'tests\NOBA.Archive.Core.Tests')
    $suites.Add('UNIT TESTS (Security)',       'tests\NOBA.Archive.Security.Tests')
    $suites.Add('UNIT TESTS (Application)',    'tests\NOBA.Archive.Application.Tests')
    $suites.Add('UNIT TESTS (Infrastructure)', 'tests\NOBA.Archive.Infrastructure.Tests')
    $suites.Add('UNIT TESTS (Formats)',        'tests\NOBA.Archive.Formats.Tests')
    $suites.Add('HOSTILE ARCHIVE + ROUND TRIP','tests\NOBA.Archive.Integration.Tests')
    $suites.Add('ARCHITECTURE TESTS',          'tests\NOBA.Archive.Architecture.Tests')
    $suites.Add('CLI CONTRACT TESTS',          'tests\NOBA.Archive.Cli.Tests')
    $suites.Add('PARITY',                      'tests\NOBA.Archive.Parity.Tests')
    $suites.Add('PRESENTATION (UI logic)',     'tests\NOBA.Archive.Presentation.Tests')

    # Every suite runs even after one fails. Stopping early would hide whether a failure is isolated or
    # systemic, and that distinction is most of the diagnosis.
    foreach ($name in @($suites.Keys)) {
        Invoke-Phase $name 'dotnet' @('run', '--project', $suites[$name], '-c', $Configuration, '--no-build') | Out-Null
    }
}
finally {
    Pop-Location
}

Write-Log ''
Write-Log ('=' * 78)
Write-Log 'SUMMARY'
Write-Log ('=' * 78)
foreach ($name in @($phases.Keys)) {
    $verdict = if ($phases[$name] -eq 0) { 'PASS' } else { "FAIL (exit $($phases[$name]))" }
    Write-Log ('{0,-32} {1}' -f $name, $verdict)
}

$failed = @($phases.Values | Where-Object { $_ -ne 0 }).Count
Write-Log ''
if ($testTotals.Suites -gt 0) {
    Write-Log ("TESTS: {0} across {1} suite(s) - {2} failed, {3} errored, {4} skipped" -f `
        $testTotals.Total, $testTotals.Suites, $testTotals.Failed, $testTotals.Errors, $testTotals.Skipped)
    if ($testTotals.Skipped -gt 0) {
        # A skip is an untested control, never a working one. It belongs on the headline rather than
        # buried in one suite's output.
        Write-Log "       $($testTotals.Skipped) skipped - a skipped test proves nothing."
    }
} else {
    Write-Log "TESTS: no suite reported a total. Do not read the PASS lines as a test result."
}

Write-Log ''
Write-Log "Phases failed: $failed"
Write-Log "Finished: $(Get-Date -Format 'o')"

Write-Host ''
Write-Host "Transcript written to:" -ForegroundColor Cyan
Write-Host "  $log" -ForegroundColor Cyan
Write-Host ''
Write-Host "Send that file back. Do not edit it, and do not re-run trying to get a green result -" -ForegroundColor Yellow
Write-Host "the failures are the point of this run." -ForegroundColor Yellow

if ($failed -gt 0) { exit 1 }
