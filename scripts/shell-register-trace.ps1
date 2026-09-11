#Requires -Version 5.1
<#
.SYNOPSIS
    DROP 08 diagnostic — runs the registration steps one at a time and names the one that stops.
.DESCRIPTION
    This exists because shell-register.ps1 stopped producing output after its banner, and the cause
    could not be reproduced on the build machine. It performs the SAME operations in the SAME order,
    but prints a line before and a line after every single one, with an elapsed time, flushed
    immediately. Whatever the last "-> " line without a matching "   ok" is, that is the statement
    that blocked.

    It writes to the same HKCU keys the real script does, so it is not a simulation. Run it, let it
    hang, then press Ctrl+C and send the output: the last line is the answer.

    It changes nothing about the product and is not part of the install path.

.PARAMETER InstallPath
    Where the executables are. Defaults to the built application, same as shell-register.ps1.

.EXAMPLE
    .\scripts\shell-register-trace.ps1
#>
[CmdletBinding()]
param([string]$InstallPath)

# Continue, not Stop: a failing step must be reported and stepped over, so the trace reaches the end
# instead of stopping at the first problem and telling us nothing about the rest.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot

$sep = [System.IO.Path]::DirectorySeparatorChar
function Local { param([string]$Path) return $Path.Replace('\', $sep).Replace('/', $sep) }

$script:Step = 0

function Step {
    param([string]$What, [scriptblock]$Do)

    $script:Step++
    $label = "{0,3}. {1}" -f $script:Step, $What
    Write-Host "-> $label" -NoNewline
    [Console]::Out.Flush()

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Do | Out-Null
        Write-Host ("  ok  ({0} ms)" -f $sw.ElapsedMilliseconds)
    }
    catch {
        Write-Host ("  FAILED ({0} ms)" -f $sw.ElapsedMilliseconds)
        Write-Host ("     {0}" -f $_.Exception.GetType().FullName)
        Write-Host ("     {0}" -f $_.Exception.Message)
    }
    [Console]::Out.Flush()
}

Write-Host ('=' * 72)
Write-Host 'NOBA Archive - registration TRACE'
Write-Host 'The last "->" line with no "ok" after it is the statement that blocked.'
Write-Host ('=' * 72)
Write-Host ''

# ---------------------------------------------------------------------------
# What the environment looks like before anything is touched.
# ---------------------------------------------------------------------------
Write-Host "PowerShell : $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
Write-Host "Repo       : $root"
Write-Host ''

$manifest = Join-Path $root (Local 'scripts\shell-verbs.json')
Write-Host "Manifest   : $manifest"
Write-Host "  exists   : $(Test-Path -LiteralPath $manifest)"

if (Test-Path -LiteralPath $manifest) {
    $bytes = [System.IO.File]::ReadAllBytes($manifest)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    Write-Host "  bytes    : $($bytes.Length)"
    Write-Host "  UTF-8 BOM: $bom   (PowerShell 5.1's ConvertFrom-Json rejects a leading BOM)"
}

Write-Host ''
Write-Host '--- reading the manifest ---'
$spec = $null
Step 'Get-Content + ConvertFrom-Json' {
    $script:spec = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
}
if ($null -eq $spec) {
    Write-Host ''
    Write-Host 'The manifest did not parse. Nothing below would have run either. Stopping here.'
    exit 1
}
Write-Host "  menuId   : $($spec.menuId)"
Write-Host "  verbs    : $(@($spec.verbs).Count)"

if (-not $InstallPath) {
    $InstallPath = Join-Path $root (Local 'src\NOBA.Archive.App\bin\x64\Debug\net10.0-windows10.0.19041.0')
}
$appExe = Join-Path $InstallPath $spec.applicationExecutable
$cliExe = Join-Path $InstallPath $spec.commandLineExecutable
Write-Host "  app exe  : $appExe  (exists: $(Test-Path -LiteralPath $appExe))"
Write-Host "  cli exe  : $cliExe  (exists: $(Test-Path -LiteralPath $cliExe))"

# ---------------------------------------------------------------------------
# How big the hive is. If a wildcard path is being enumerated, this is the cost.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '--- the hive being written to ---'
Step 'count subkeys under HKCU:\Software\Classes' {
    $classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes')
    if ($classes) {
        $names = $classes.GetSubKeyNames()
        Write-Host ''
        Write-Host "     subkeys under HKCU\Software\Classes : $($names.Count)"
        Write-Host "     a literal '*' key exists            : $($names -contains '*')"
        $classes.Dispose()
    }
}

# ---------------------------------------------------------------------------
# The operations themselves, one per line, in the order the real script runs them.
#
# Both spellings are traced: the PowerShell provider (which is what the failing script used, and
# whose -Path parameter treats '*' as a wildcard) and the .NET API (which treats every character
# literally). If the provider blocks and the .NET call does not, that is the answer.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '--- publisher key ---'
Step "provider: New-Item HKCU:\Software\NOBA\Archive" {
    New-Item -Path 'HKCU:\Software\NOBA\Archive' -Force
}
Step "provider: Set-ItemProperty $($spec.installPathValue)" {
    Set-ItemProperty -Path 'HKCU:\Software\NOBA\Archive' -Name $spec.installPathValue -Value $InstallPath
}

foreach ($scope in @('*', 'Directory')) {
    Write-Host ''
    Write-Host "--- scope '$scope' ---"

    $menuProvider = "HKCU:\Software\Classes\$scope\shell\$($spec.menuId)"
    $menuLiteral = "Software\Classes\$scope\shell\$($spec.menuId)"

    # THE SUSPECT. New-Item has no -LiteralPath parameter - only -Path, which globs - so for the
    # '*' scope this asks the registry provider to resolve a path whose second-to-last segment is
    # a wildcard. This is the first statement after the banner in the failing script.
    Step "provider: New-Item -Path (contains '$scope')  <-- SUSPECT" {
        New-Item -Path $menuProvider -Force
    }

    Step "  .NET  : CreateSubKey (literal, no globbing)" {
        $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($menuLiteral)
        if ($k) { $k.Dispose() }
    }

    Step "provider: Set-ItemProperty MUIVerb" {
        Set-ItemProperty -Path $menuProvider -Name 'MUIVerb' -Value $spec.menuTitle
    }
    Step "provider: Set-ItemProperty Icon" {
        Set-ItemProperty -Path $menuProvider -Name 'Icon' -Value $appExe
    }
    Step "provider: Set-ItemProperty subcommands" {
        Set-ItemProperty -Path $menuProvider -Name 'subcommands' -Value ''
    }

    foreach ($verb in $spec.verbs) {
        if ($scope -eq 'Directory' -and $verb.archiveOnly) { continue }

        $exe = if ($verb.host -eq 'Application') { $appExe } else { $cliExe }
        $itemProvider = "$menuProvider\shell\$($verb.id)"
        $itemLiteral = "$menuLiteral\shell\$($verb.id)"
        $command = '"{0}" {1} "%1"' -f $exe, $verb.verb

        Step "provider: New-Item $($verb.id)\command" {
            New-Item -Path "$itemProvider\command" -Force
        }
        Step "provider: Set-ItemProperty $($verb.id) MUIVerb" {
            Set-ItemProperty -Path $itemProvider -Name 'MUIVerb' -Value $verb.label
        }
        Step "provider: Set-ItemProperty $($verb.id) command" {
            Set-ItemProperty -Path "$itemProvider\command" -Name '(default)' -Value $command
        }
        Step "  .NET  : CreateSubKey + SetValue $($verb.id)" {
            $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$itemLiteral\command")
            if ($k) { $k.SetValue('', $command, [Microsoft.Win32.RegistryValueKind]::String); $k.Dispose() }
        }
    }
}

# ---------------------------------------------------------------------------
# What actually landed, read back literally so a wildcard cannot flatter the result.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '--- reading back, literally ---'

foreach ($scope in @('*', 'Directory')) {
    $literal = "Software\Classes\$scope\shell\$($spec.menuId)"
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($literal)
    if ($key) {
        $shell = $key.OpenSubKey('shell')
        $count = if ($shell) { $shell.GetSubKeyNames().Count } else { 0 }
        Write-Host ("  scope '{0}': key present, {1} verb key(s)" -f $scope, $count)
        if ($shell) { $shell.Dispose() }
        $key.Dispose()
    }
    else {
        Write-Host ("  scope '{0}': NOT PRESENT" -f $scope)
    }
}

# Did a wildcard write scatter the menu across other classes? Counted, not assumed.
$classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes')
if ($classes) {
    $scattered = @()
    foreach ($name in $classes.GetSubKeyNames()) {
        if ($name -eq '*' -or $name -eq 'Directory') { continue }
        $probe = $classes.OpenSubKey("$name\shell\$($spec.menuId)")
        if ($probe) { $scattered += $name; $probe.Dispose() }
    }

    Write-Host ''
    if ($scattered.Count -gt 0) {
        Write-Host "  *** the menu was written under $($scattered.Count) OTHER class key(s):"
        $scattered | Select-Object -First 20 | ForEach-Object { Write-Host "        $_" }
        Write-Host '  *** that is a wildcard fan-out, and uninstall would not find any of them.'
    }
    else {
        Write-Host '  no fan-out: the menu exists only under the scopes it should.'
    }
    $classes.Dispose()
}

Write-Host ''
Write-Host 'Trace complete. Send this whole output.'
Write-Host 'To undo everything it wrote:  .\scripts\shell-register.ps1 -Remove'
