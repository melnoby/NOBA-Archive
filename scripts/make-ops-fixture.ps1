<#
.SYNOPSIS
    Builds a large, SAFE archive for exercising the Operations Center on Windows.

.DESCRIPTION
    The Operations gate needs an extraction that runs long enough for a person to watch progress
    move, pause it, resume it and cancel it. That means a lot of extracted bytes across a lot of
    entries - and the first attempt at such a fixture was refused by NOBA's own bomb protection at
    748:1, which was the guard doing its job.

    So the ratio is a design input here, not an obstacle. Every 4 KiB block of every file carries
    64 bytes of cryptographic randomness, which no compressor can shrink. That puts a hard ceiling
    of about 64:1 on the archive - measured at 55.8:1 - against a policy that refuses anything over
    500:1 in total or 1000:1 for a single entry. The fixture is large because it holds a lot of real
    bytes, not because it expands.

    Nothing here touches, relaxes or bypasses ResourceGuardOptions. If a change ever pushed the
    achieved ratio toward the policy, this script fails instead of shipping the fixture.

.PARAMETER Files
    How many files to generate. More files means a longer extraction for the same byte count,
    because per-file cost on NTFS (create, write, Mark-of-the-Web stream, rename) is what dominates.

.PARAMETER FileKilobytes
    Size of each generated file, in KiB. Must be a multiple of 4.

.PARAMETER WorkFolder
    Where the source tree is generated. Deleted and rebuilt on each run.

.PARAMETER OutputArchive
    Path of the archive to write.

.PARAMETER Cli
    Path to noba-ar.exe. The archive is built by NOBA's own create path, so the fixture is one this
    product actually produces rather than one a third-party tool happened to write.

.EXAMPLE
    .\scripts\make-ops-fixture.ps1
    3,800 files x 256 KiB = 950 MB extracted, about 17 MB stored.

.EXAMPLE
    .\scripts\make-ops-fixture.ps1 -Files 12000
    3 GB extracted - use this if the default finishes too quickly to click Pause.
#>
[CmdletBinding()]
param(
    [int]$Files = 3800,
    [int]$FileKilobytes = 256,
    [string]$WorkFolder = "$PSScriptRoot\..\fixtures\noba-ops-fixture",
    [string]$OutputArchive = "$PSScriptRoot\..\fixtures\noba-ops.7z",
    [string]$Cli = "$PSScriptRoot\..\artifacts\alpha\noba-ar.exe"
)

$ErrorActionPreference = 'Stop'

# The policy this fixture is built to respect, mirrored from ResourceGuardOptions. If the product's
# defaults ever change, change them there and update this line to match - never the other way round.
$policyTotalRatio = 500

if (-not (Test-Path -LiteralPath $Cli)) {
    Write-Host "noba-ar.exe not found at: $Cli" -ForegroundColor Red
    Write-Host "Build it first (scripts\build.ps1) or pass -Cli <path>." -ForegroundColor Red
    exit 2
}

$chunk = 4096
$randomBytesPerChunk = 64
$fileBytes = [long]$FileKilobytes * 1024

if ($fileBytes % $chunk -ne 0) {
    Write-Host "-FileKilobytes must be a multiple of 4." -ForegroundColor Red
    exit 2
}

# Folder names are deliberately Arabic as well as Latin, and nested, because the row that names the
# current entry and the file table both have to survive a right-to-left path.
$folders = @(
    'تقارير الأداء',
    'تقارير الأداء\الربع الأول',
    'تقارير الأداء\الربع الثاني',
    'سجلات النظام',
    'سجلات النظام\2026',
    'أرشيف العملاء',
    'أرشيف العملاء\مرفقات',
    'logs',
    'logs\nested',
    'data',
    'data\nested\deep',
    'exports'
)

$extractedBytes = [long]$Files * $fileBytes

Write-Host ''
Write-Host 'NOBA - Operations fixture' -ForegroundColor Cyan
Write-Host "  files          : $Files"
Write-Host "  size each      : $FileKilobytes KiB"
Write-Host "  extracted total: $([math]::Round($extractedBytes / 1MB, 0)) MB"
Write-Host ''

if (Test-Path -LiteralPath $WorkFolder) {
    Remove-Item -LiteralPath $WorkFolder -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $WorkFolder | Out-Null
$WorkFolder = (Resolve-Path -LiteralPath $WorkFolder).Path

foreach ($folder in $folders) {
    New-Item -ItemType Directory -Force -Path (Join-Path $WorkFolder $folder) | Out-Null
}

# One template of plausible log text, reused for every file, then diluted per block. Reuse is why
# generation is fast; the dilution is why reuse does not turn into a 1000:1 archive.
$line = "2026-08-26 12:00:00.000 [INFO ] noba.archive.extract  entry=00000000 state=ok bytes=$fileBytes checksum=verified`r`n"
$builder = New-Object System.Text.StringBuilder
while ($builder.Length -lt $fileBytes) {
    [void]$builder.Append($line)
}
$template = [System.Text.Encoding]::ASCII.GetBytes($builder.ToString().Substring(0, $fileBytes))

# RandomNumberGenerator.Create() rather than the static GetBytes helper, because this script has to
# run under Windows PowerShell 5.1 as well as PowerShell 7.
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$noise = New-Object byte[] $randomBytesPerChunk
$buffer = New-Object byte[] $fileBytes

$started = Get-Date
for ($i = 0; $i -lt $Files; $i++) {
    $folder = $folders[$i % $folders.Count]
    $isLatin = $folder -match '^[\x20-\x7E]'
    if ($isLatin) { $name = 'log-{0:D5}.txt' -f $i } else { $name = 'تقرير-{0:D5}.log' -f $i }

    [System.Array]::Copy($template, $buffer, $fileBytes)
    for ($offset = 0; $offset -lt $fileBytes; $offset += $chunk) {
        $rng.GetBytes($noise)
        [System.Array]::Copy($noise, 0, $buffer, $offset, $randomBytesPerChunk)
    }

    [System.IO.File]::WriteAllBytes((Join-Path (Join-Path $WorkFolder $folder) $name), $buffer)

    if (($i + 1) % 500 -eq 0) {
        Write-Host "  generated $($i + 1) / $Files"
    }
}
$rng.Dispose()
Write-Host "  generated $Files files in $([math]::Round(((Get-Date) - $started).TotalSeconds, 1))s"

$archiveFolder = Split-Path -Parent $OutputArchive
if ($archiveFolder -and -not (Test-Path -LiteralPath $archiveFolder)) {
    New-Item -ItemType Directory -Force -Path $archiveFolder | Out-Null
}
if (Test-Path -LiteralPath $OutputArchive) {
    Remove-Item -LiteralPath $OutputArchive -Force
}

Write-Host ''
Write-Host 'Packing with noba-ar create ...' -ForegroundColor Cyan

# The work folder is passed as a single ASCII path rather than its Arabic children, so that no
# non-ASCII argument has to survive the console code page on Windows PowerShell 5.1. The Arabic
# names are still inside the archive, which is the part being tested.
$arguments = @('create', $OutputArchive, $WorkFolder, '--profile', 'fast')
& $Cli @arguments
$created = $LASTEXITCODE

if ($created -ne 0) {
    Write-Host "create failed with exit code $created" -ForegroundColor Red
    exit $created
}

$archiveBytes = (Get-Item -LiteralPath $OutputArchive).Length
$ratio = [math]::Round($extractedBytes / $archiveBytes, 1)

Write-Host ''
Write-Host "  archive        : $OutputArchive"
Write-Host "  stored         : $([math]::Round($archiveBytes / 1MB, 1)) MB"
Write-Host "  extracted      : $([math]::Round($extractedBytes / 1MB, 0)) MB"
Write-Host "  expansion ratio: ${ratio}:1  (policy refuses above ${policyTotalRatio}:1)"
Write-Host ''

# A fixture that drifts toward the policy is a fixture that will be refused on some other machine.
# Fail here rather than let that surface as a mysterious Failed row in the middle of the gate.
if ($ratio -gt ($policyTotalRatio * 0.5)) {
    Write-Host "FIXTURE REJECTED: ${ratio}:1 is too close to the ${policyTotalRatio}:1 policy." -ForegroundColor Red
    Write-Host 'Raise the randomness per block; do not raise the policy.' -ForegroundColor Red
    exit 1
}

Write-Host 'FIXTURE READY - safely below the expansion policy.' -ForegroundColor Green
exit 0
