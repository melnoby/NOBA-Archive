#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the NOBA Archive test suites and prints the phase-gate summary.
.DESCRIPTION
    Never edit this script to report PASS for a suite that did not run: the phase gate exists precisely
    to stop that (rule 51).

    Runs on Windows PowerShell 5.1 (what ships with Windows) as well as PowerShell 7+.
.EXAMPLE
    .\scripts\test.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Offline,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$props = @()
if ($Offline) { $props += '-p:NobaOfflineBuild=true' }

$suites = New-Object System.Collections.Specialized.OrderedDictionary
$suites.Add('UNIT TESTS (Core)',           'tests\NOBA.Archive.Core.Tests')
$suites.Add('UNIT TESTS (Security)',       'tests\NOBA.Archive.Security.Tests')
$suites.Add('UNIT TESTS (Application)',    'tests\NOBA.Archive.Application.Tests')
$suites.Add('UNIT TESTS (Infrastructure)', 'tests\NOBA.Archive.Infrastructure.Tests')
$suites.Add('UNIT TESTS (Formats)',        'tests\NOBA.Archive.Formats.Tests')
$suites.Add('HOSTILE ARCHIVE + ROUND TRIP', 'tests\NOBA.Archive.Integration.Tests')
$suites.Add('ARCHITECTURE TESTS',          'tests\NOBA.Archive.Architecture.Tests')
$suites.Add('CLI CONTRACT TESTS',          'tests\NOBA.Archive.Cli.Tests')
$suites.Add('PARITY FOUNDATION',           'tests\NOBA.Archive.Parity.Tests')

$results = New-Object System.Collections.Specialized.OrderedDictionary

Push-Location $root
try {
    if (-not $SkipBuild) {
        dotnet build NOBA.Archive.sln -c $Configuration --nologo $props
        if ($LASTEXITCODE -ne 0) {
            Write-Host ''
            Write-Host 'BUILD: FAIL - not running any suite.' -ForegroundColor Red
            exit 1
        }
    }

    foreach ($name in @($suites.Keys)) {
        $project = $suites[$name]
        Write-Host ''
        Write-Host "--- $name ---" -ForegroundColor Cyan

        # Both xUnit v3 and the offline harness produce self-executing test projects, so one command
        # covers both modes. 'dotnet test' also works for the xUnit path (see DEVELOPMENT.md).
        dotnet run --project $project -c $Configuration --no-build $props

        if ($LASTEXITCODE -eq 0) { $results[$name] = 'PASS' } else { $results[$name] = 'FAIL' }
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host ('=' * 56)
foreach ($name in @($results.Keys)) {
    $colour = if ($results[$name] -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host ('{0,-30} {1}' -f $name, $results[$name]) -ForegroundColor $colour
}

if (@($results.Values) -contains 'FAIL') { exit 1 }
