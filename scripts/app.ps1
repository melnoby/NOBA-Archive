#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and runs the WinUI 3 desktop shell.
.DESCRIPTION
    NOBA.Archive.App is the one project that cannot be built outside Windows, so it lives in its own
    solution and has its own script. The engine solution still builds and tests everywhere; this is the
    part that does not.

    First run after a drop will very likely fail to compile. That is expected and is the honest position:
    this code has never been through a compiler, because there is no compiler for it on the machine it
    was written on. Send the errors back rather than working around them.

.EXAMPLE
    .\scripts\app.ps1              # build and run
    .\scripts\app.ps1 -BuildOnly   # compile only, and report
    .\scripts\app.ps1 -Screenshots # build, run, and explain how to capture the two languages
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$BuildOnly,
    [switch]$Screenshots
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repo 'NOBA.Archive.App.sln'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $repo 'artifacts'
$log = Join-Path $logDir "winui-build-$stamp.txt"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Write-Log {
    param([string]$Text)
    Write-Host $Text
    Add-Content -LiteralPath $log -Value $Text -Encoding UTF8
}

Write-Log "NOBA Archive - WinUI 3 shell"
Write-Log "Started: $(Get-Date -Format 'o')"

$stampFile = Join-Path $repo 'src\NOBA.Archive.Cli\BuildStamp.cs'
$drop = 'MISSING'
if (Test-Path $stampFile) {
    $stampMatch = Select-String -Path $stampFile -Pattern 'Value\s*=\s*"([^"]+)"' | Select-Object -First 1
    if ($stampMatch) { $drop = $stampMatch.Matches[0].Groups[1].Value }
}
Write-Log "Source drop : $drop"
Write-Log ''

# The engine solution is built first and separately. If that is broken, a XAML error is noise.
Write-Log '--- engine solution (this must pass before the UI means anything) ---'
& dotnet build (Join-Path $repo 'NOBA.Archive.sln') -c Release --nologo 2>&1 | Tee-Object -Variable engineOutput | Out-Null
$engineCode = $LASTEXITCODE
Write-Log (($engineOutput | Select-String -Pattern 'error|Build succeeded|Build FAILED') -join [Environment]::NewLine)
Write-Log "EXIT CODE: $engineCode"

if ($engineCode -ne 0) {
    Write-Log ''
    Write-Log 'The engine does not build. Fix that before looking at the UI.'
    exit 1
}

Write-Log ''
Write-Log '--- WinUI 3 shell ---'
Write-Log "COMMAND: dotnet build $solution -c $Configuration -p:Platform=x64"

& dotnet build $solution -c $Configuration -p:Platform=x64 --nologo 2>&1 | Tee-Object -Variable appOutput | Out-Null
$appCode = $LASTEXITCODE

$errors = @($appOutput | Select-String -Pattern ': error ')
Write-Log (($appOutput | Select-String -Pattern ': error |Build succeeded|Build FAILED') -join [Environment]::NewLine)
Write-Log "EXIT CODE: $appCode"

Write-Log ''
if ($appCode -ne 0) {
    Write-Log "BUILD: FAIL - $($errors.Count) error(s)."
    Write-Log ''
    Write-Log "Send back this file: $log"
    Write-Log 'Do not edit the source to make it compile - the errors are the report.'
    exit 1
}

Write-Log 'BUILD: PASS'

if ($BuildOnly) {
    Write-Log "Transcript: $log"
    exit 0
}

$exe = Join-Path $repo "src\NOBA.Archive.App\bin\x64\$Configuration\net10.0-windows10.0.19041.0\NOBA.Archive.App.exe"
if (-not (Test-Path $exe)) {
    Write-Log "The build reported success but produced no executable at:"
    Write-Log "  $exe"
    exit 1
}

Write-Log ''
Write-Log "Launching: $exe"

# "Launching" is not a result. The app has already once compiled, started, and exited before drawing a
# pixel because a service was missing from the container - and this script reported that as success and
# then explained how to take screenshots of a window that did not exist. A launch is only news once the
# process is still there a moment later.
$process = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3
$process.Refresh()

if ($process.HasExited) {
    Write-Log ''
    Write-Log "STARTUP: FAIL - the process exited within 3 seconds of launching."
    Write-Log "  exit code: $($process.ExitCode)"
    Write-Log ''
    Write-Log 'The build is fine; something threw before the window appeared. To see what:'
    Write-Log '  Get-WinEvent -LogName Application -MaxEvents 40 |'
    Write-Log '    Where-Object { $_.Message -match ''NOBA'' } | Format-List TimeCreated, Message'
    Write-Log ''
    Write-Log "Transcript: $log"
    exit 1
}

# A live process is not a visible window. The whole of Feature 02 slice 2 shipped past this gate while
# the constructor was throwing a NullReferenceException: the process lingered, this line said PASS, and
# the user was told to go and test a window that had never been drawn. So the gate now asks the only
# question that matters - did a window actually appear.
$deadline = (Get-Date).AddSeconds(12)
$handle = 0
while ((Get-Date) -lt $deadline) {
    $process.Refresh()
    if ($process.HasExited) { break }
    if ($process.MainWindowHandle -ne 0) { $handle = $process.MainWindowHandle; break }
    Start-Sleep -Milliseconds 250
}

if ($process.HasExited) {
    Write-Log ''
    Write-Log "STARTUP: FAIL - the process exited before showing a window."
    Write-Log "  exit code: $($process.ExitCode)"
    Write-Log ''
    Write-Log 'Something threw before the window appeared. To see what:'
    Write-Log '  Get-WinEvent -LogName Application -MaxEvents 40 |'
    Write-Log '    Where-Object { $_.Message -match ''NOBA'' } | Format-List TimeCreated, Message'
    Write-Log ''
    Write-Log "Transcript: $log"
    exit 1
}

if ($handle -eq 0) {
    Write-Log ''
    Write-Log "STARTUP: FAIL - process $($process.Id) is alive but drew no window within 12 seconds."
    Write-Log ''
    Write-Log 'This is the shape a constructor exception takes: the process survives and nothing appears.'
    Write-Log '  Get-WinEvent -LogName Application -MaxEvents 40 |'
    Write-Log '    Where-Object { $_.Message -match ''NOBA'' } | Format-List TimeCreated, Message'
    Write-Log ''
    Write-Log "Transcript: $log"
    exit 1
}

Write-Log "STARTUP: PASS - process $($process.Id) drew a window (handle $handle)."

if ($Screenshots) {
    Write-Log ''
    Write-Log 'For the screenshots, both languages, both themes:'
    Write-Log '  1. Open one of your real archives (the rar folder has seven).'
    Write-Log '  2. Capture the window: Win+Shift+S, or Alt+PrintScreen for the active window.'
    Write-Log '  3. Click the language button in the sidebar and capture again.'
    Write-Log '  4. Click the theme button and capture both languages again.'
    Write-Log ''
    Write-Log 'What is worth looking at in each shot:'
    Write-Log '  - does the whole layout mirror in Arabic, including the navigation and the scrollbar'
    Write-Log '  - do Arabic file names read correctly, and do paths stay left-to-right inside them'
    Write-Log '  - are the numeric columns still aligned and still left-to-right'
    Write-Log '  - is a blocked entry identifiable without relying on its colour'
}

Write-Log ''
Write-Log "Transcript: $log"
