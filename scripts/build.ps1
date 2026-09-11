#Requires -Version 5.1
<#
.SYNOPSIS
    Restores and builds the NOBA Archive solution.
.DESCRIPTION
    This is the shipping configuration: it restores the pinned NuGet packages listed in
    Directory.Packages.props. Use -Offline only in an environment with no access to nuget.org; that
    swaps the Microsoft.Extensions.* packages for the shared framework and xUnit for the TestKit
    harness, and its results may NOT be reported as a shipping-configuration PASS.

    Runs on Windows PowerShell 5.1 (what ships with Windows) as well as PowerShell 7+.
.EXAMPLE
    .\scripts\build.ps1
.EXAMPLE
    .\scripts\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Native commands expand an array variable into separate arguments; do not use cmdlet splatting here.
$props = @()
if ($Offline) { $props += '-p:NobaOfflineBuild=true' }

Push-Location $root
try {
    if (-not $Offline) {
        Write-Host 'Restoring...' -ForegroundColor Cyan
        dotnet restore NOBA.Archive.sln
        if ($LASTEXITCODE -ne 0) {
            Write-Host ''
            Write-Host 'RESTORE: FAIL' -ForegroundColor Red
            Write-Host 'If this is NU1102 ("unable to find package ... with version"), a pinned version in'
            Write-Host 'Directory.Packages.props does not exist. Find the real one with, for example:'
            Write-Host '    dotnet package search xunit.v3 --take 5'
            Write-Host 'then correct the pin. Versions are pinned exactly on purpose (DEPENDENCIES.md 4).'
            exit 1
        }
    }

    Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
    dotnet build NOBA.Archive.sln -c $Configuration --nologo $props
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'BUILD: FAIL' -ForegroundColor Red
        exit 1
    }

    Write-Host ''
    Write-Host 'BUILD: PASS' -ForegroundColor Green
}
finally {
    Pop-Location
}
