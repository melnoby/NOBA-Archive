# Written by shell-argv-probe.ps1. Records what Explorer handed this process, and nothing else.
param([string]$Variant = '?')

$lines = New-Object System.Collections.ArrayList
[void]$lines.Add('')
[void]$lines.Add(('-' * 74))
[void]$lines.Add(("variant     : {0}" -f $Variant))
[void]$lines.Add(("at          : {0:o}" -f (Get-Date)))
[void]$lines.Add(("pid         : {0}" -f $PID))
[void]$lines.Add(("parent cwd  : {0}" -f (Get-Location).Path))

# The raw line the operating system built. This is the measurement; $args has already been parsed.
[void]$lines.Add(("raw command : {0}" -f [Environment]::CommandLine))

[void]$lines.Add(("argc        : {0}" -f $args.Count))
for ($i = 0; $i -lt $args.Count; $i++) {
    [void]$lines.Add(("  arg[{0}]    <{1}>" -f $i, $args[$i]))
}

# Two destinations. If the repository copy cannot be written - permissions, a moved folder, a
# redirected drive - the evidence still survives under %TEMP%, and an empty repository log then
# means something other than "nothing ran".
$targets = @(
    (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\shell-argv-probe.log')
    (Join-Path $env:TEMP 'noba-argv-probe.log')
)

foreach ($target in $targets) {
    try {
        $dir = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $stream = [System.IO.File]::AppendText($target)
        try { foreach ($line in $lines) { $stream.WriteLine($line) } } finally { $stream.Dispose() }
    }
    catch {
        [void]$lines.Add(("  (could not write {0}: {1})" -f $target, $_.Exception.Message))
    }
}

# Shown, not hidden. A probe you cannot see is a probe whose silence means nothing.
Write-Host ''
foreach ($line in $lines) { Write-Host $line }
Write-Host ''
Write-Host 'Recorded. This window closes in 6 seconds.' -ForegroundColor Green
Start-Sleep -Seconds 6
