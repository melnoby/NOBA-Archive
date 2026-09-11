#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the NOBA Archive internal alpha: a single self-contained noba-ar.exe.

.DESCRIPTION
    Priority 4 of the agreed order — open / list / extract ZIP on real archives, in the hands of a real
    user, before any more surface gets built.

    Self-contained on purpose. The alpha has to run on a machine that has never had .NET installed,
    because "it failed to start" is feedback about the runtime, not about the archiver.

.EXAMPLE
    .\scripts\alpha.ps1
    .\scripts\alpha.ps1 -Output D:\noba-alpha
#>
[CmdletBinding()]
param(
    [string] $Output,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\.."

# Not a param default. $PSScriptRoot is empty while parameter defaults are bound under PowerShell 5.1,
# so "$PSScriptRoot\..\artifacts\alpha" quietly became "\..\artifacts\alpha" - which resolves against
# the current drive root and dropped the first alpha build in C:\artifacts\alpha.
if (-not $Output) {
    $Output = Join-Path $repo 'artifacts\alpha'
}

Write-Host "NOBA Archive - internal alpha build" -ForegroundColor Cyan
Write-Host ""

if (-not $SkipTests) {
    # Shipping an alpha off an unvalidated tree is how R13 happened in the first place.
    Write-Host "Validating first (use -SkipTests to bypass, at your own risk)..." -ForegroundColor Yellow
    & "$PSScriptRoot\validate.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Validation failed. Not building an alpha from a red tree." -ForegroundColor Red
        exit 1
    }
    Write-Host ""
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$Output = (Resolve-Path $Output).Path

$publishArgs = @(
    'publish', "$repo\src\NOBA.Archive.Cli",
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true',
    '-o', $Output,
    '--nologo'
)

Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    exit 1
}

$exe = Join-Path $Output 'noba-ar.exe'
if (-not (Test-Path $exe)) {
    Write-Host "Expected $exe but it is not there." -ForegroundColor Red
    exit 1
}

# Smoke test. A published binary that cannot answer 'version' is not an alpha.
Write-Host ""
Write-Host "Smoke test:" -ForegroundColor Cyan
& $exe version
if ($LASTEXITCODE -ne 0) {
    Write-Host "The published binary failed its own version command." -ForegroundColor Red
    exit 1
}
& $exe formats

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Alpha ready: $exe ($size MB, self-contained)" -ForegroundColor Green
Write-Host ""
Write-Host "Try it on real archives:" -ForegroundColor Cyan
Write-Host "  .\noba-ar.exe list <archive.zip>"
Write-Host "  .\noba-ar.exe info <archive.zip>"
Write-Host "  .\noba-ar.exe test <archive.zip>"
Write-Host "  .\noba-ar.exe extract <archive.zip> -o <folder>"
Write-Host "  .\noba-ar.exe list <archive.7z>"
Write-Host "  .\noba-ar.exe list <archive.rar>"
Write-Host "  .\noba-ar.exe list <old-arabic.zip> --name-encoding 1256"
Write-Host "  <password> | .\noba-ar.exe list <encrypted.7z> --password-stdin"
Write-Host ""
Write-Host "What is worth reporting back:" -ForegroundColor Yellow
Write-Host "  - any archive it refuses to open that 7-Zip opens"
Write-Host "  - any entry it blocks that you believe is legitimate"
Write-Host "  - names that come out wrong, and whether --name-encoding fixes them"
Write-Host "  - how slow it feels on a large archive (there is no benchmark yet - R17)"
Write-Host "  - a SOLID .7z with many entries (R18 is fixed; solid and non-solid should now feel the same)
  - a .rar of any age: listing, names, and whether extraction produces the same bytes as 7-Zip"
