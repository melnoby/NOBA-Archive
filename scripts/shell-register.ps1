#Requires -Version 5.1
<#
.SYNOPSIS
    Registers (or removes) NOBA Archive's Explorer entry — the interim seam, not the shipping installer.
.DESCRIPTION
    ADR-0006 is the plan of record: a WiX MSI installs an unpackaged NOBA Archive, and a signed sparse
    MSIX package registers an IExplorerCommand handler so the entry appears in the Windows 11 menu
    proper. None of that exists in this repository yet, and a sparse package will not register without
    a trusted signature, so this script is what stands in until it does.

    What it does instead is the classic ContextMenuHandlers path, which ADR-0006 already names as the
    Windows 10 fallback: per-user keys under HKCU, no package, no signature, no elevation. On Windows
    11 the entry appears under "Show more options" rather than in the top-level menu. That is a real
    difference from the shipping design and is not presented as equivalent to it.

    THIS SCRIPT DECIDES NOTHING. It is an adapter, and the thing it adapts is scripts\shell-verbs.json,
    generated from NOBA.Archive.Core by `noba-ar shell-manifest`. Which entries exist, what each is
    called, where each may appear, what each runs and in what order - all of that arrives in the
    manifest. This translates it into the classic registry's spelling and reads back what it wrote.

    That division is the correction for D2 and D3. The previous version of this script built its own
    command line - executable, the verb name, and "%1" - which is how "Compress to" registered as
    `noba-ar create "%1"` with no inputs and no format, and how "Extract here" lost its destination.
    Both looked plausible in the registry and were wrong, and nothing compared them to what the product
    actually does, because there was nothing to compare them to. Now there is: the manifest publishes
    each entry's argument list as tokens, and Expand-NobaArguments below maps a token to the shell's
    substitution and refuses any token it does not know.

    Two argument kinds cannot be expressed as a substitution at all - a containing folder and a
    suggested archive name are computed from the selection, and the registry can only substitute the
    selection itself. Those entries carry directlyRegistrable = false and are registered as a call to
    the resolver verb, which does the computing with the same ShellMenuBuilder and ShellLauncher the
    test suite exercises.

    REGISTRY ACCESS IS THE .NET API, NOT THE POWERSHELL PROVIDER, AND THAT IS MEASURED, NOT TASTE.
    One of the scopes this registers under is the class key literally named "*" - the shell's name for
    "any file" - and every PowerShell provider cmdlet that takes -Path treats that character as a
    wildcard. The earlier provider-based version of this script did not fail; it resolved that
    wildcard against all 801 subkeys of HKCU\Software\Classes, once per path segment. Timed on
    Windows 11 / PowerShell 5.1.26100 by scripts\shell-register-trace.ps1:

        Set-ItemProperty, no "*" in the path .............      35 ms
        Set-ItemProperty, "*" at depth 3 .................  24,693 ms
        Set-ItemProperty, "*" at depth 5 ................. 840,771 ms   (14 minutes)
        Set-ItemProperty, "*" at depth 6 ................. did not return

    Six verbs across two scopes, at that curve, is not a wait anybody sits through - it reads as a
    hang. Microsoft.Win32.RegistryKey.CreateSubKey and SetValue take literal names: they resolve
    nothing, enumerate nothing, and return when the key exists. They are also synchronous, which is
    the property that matters when the alternative stopped producing output.

    New-Item was never the culprit - it measured 75 ms, because with -Force on a missing leaf it takes
    the literal creation path. So no provider cmdlet touches the registry here, not just the one that
    looked guilty.

.PARAMETER InstallPath
    Where NOBA.Archive.App.exe and noba-ar.exe live. Defaults to the built application.

.PARAMETER Remove
    Unregister instead of register. Removes every key this script can create, then the publisher key.

.EXAMPLE
    .\scripts\shell-register.ps1
.EXAMPLE
    .\scripts\shell-register.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$InstallPath,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$sep = [System.IO.Path]::DirectorySeparatorChar
function Local {
    param([string]$Path)
    return $Path.Replace('\', $sep).Replace('/', $sep)
}

# ---------------------------------------------------------------------------
# Registry access, literal and synchronous.
# ---------------------------------------------------------------------------

<#
.SYNOPSIS
    Creates a key under HKCU and returns it open. The caller disposes it.
.DESCRIPTION
    Intermediate keys are created too, and an existing key is returned rather than replaced - which
    is what makes running this twice a no-op instead of a duplicate.
#>
function New-NobaKey {
    param([Parameter(Mandatory)][string]$SubKey)

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKey)
    if ($null -eq $key) {
        throw "Could not create HKCU\$SubKey."
    }

    return $key
}

<#
.SYNOPSIS
    Writes one string value. An empty name is the key's default value, which is where a shell
    command line lives.
#>
function Set-NobaValue {
    param(
        [Parameter(Mandatory)][string]$SubKey,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value
    )

    $key = New-NobaKey -SubKey $SubKey
    try {
        $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

function Test-NobaKey {
    param([Parameter(Mandatory)][string]$SubKey)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey)
    if ($null -eq $key) {
        return $false
    }

    $key.Dispose()
    return $true
}

function Get-NobaValue {
    param([Parameter(Mandatory)][string]$SubKey, [Parameter(Mandatory)][AllowEmptyString()][string]$Name)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey)
    if ($null -eq $key) {
        return $null
    }

    try {
        return $key.GetValue($Name)
    }
    finally {
        $key.Dispose()
    }
}

<#
.SYNOPSIS
    Removes a key and everything under it. Returns whether there was anything to remove.
.DESCRIPTION
    DeleteSubKeyTree with throwOnMissingSubKey = $false, so removing twice is not an error - an
    uninstaller that fails because the machine was already clean is an uninstaller people learn to
    skip.
#>
function Remove-NobaKey {
    param([Parameter(Mandatory)][string]$SubKey)

    if (-not (Test-NobaKey -SubKey $SubKey)) {
        return $false
    }

    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($SubKey, $false)
    return $true
}

# ---------------------------------------------------------------------------
# The entries, read rather than written down.
# ---------------------------------------------------------------------------
$manifest = Join-Path $root (Local 'scripts\shell-verbs.json')
if (-not (Test-Path -LiteralPath $manifest)) {
    Write-Host "Missing $manifest."
    Write-Host "Generate it first:  dotnet run --project src\NOBA.Archive.Cli -- shell-manifest > scripts\shell-verbs.json"
    exit 1
}

# Read as raw bytes and strip any byte-order mark before parsing. PowerShell 5.1 writes a BOM with
# -Encoding UTF8, and its ConvertFrom-Json rejects a leading BOM as an invalid primitive - so a
# manifest this repository generated could fail to load in the shell that generated it.
$manifestBytes = [System.IO.File]::ReadAllBytes($manifest)
if ($manifestBytes.Length -ge 3 -and $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF) {
    $manifestText = [System.Text.Encoding]::UTF8.GetString($manifestBytes, 3, $manifestBytes.Length - 3)
}
else {
    $manifestText = [System.Text.Encoding]::UTF8.GetString($manifestBytes)
}

$spec = $manifestText | ConvertFrom-Json

if (-not $spec.entries) {
    Write-Host 'The manifest declares no entries. Regenerate it with `noba-ar shell-manifest`.'
    exit 1
}

# Every class key any entry asks for, deduplicated and in a stable order. These are key NAMES; the
# "*" among them is the shell's class for "any file" and is never a pattern here.
$scopes = @($spec.entries | ForEach-Object { $_.scopes } | Sort-Object -Unique)

function Get-MenuKey {
    param([Parameter(Mandatory)][string]$Scope)
    return "Software\Classes\$Scope\shell\$($spec.menuId)"
}

function Get-EntryKey {
    param([Parameter(Mandatory)][string]$Scope, [Parameter(Mandatory)][string]$EntryId)
    return "$(Get-MenuKey -Scope $Scope)\shell\$EntryId"
}

<#
.SYNOPSIS
    The entries that belong on one scope, as the manifest assigned them.
.DESCRIPTION
    The assignment is a capability decision and was made in Core against FormatCapabilityReport - it
    is why "Compress to GZIP" is absent from folders (a folder is many members and GZIP holds one)
    and why the archive verbs appear only on extensions this build can open. Re-deriving it here
    would be a second capability model, and a second one is how the first one gets contradicted.
#>
function Get-ScopeEntries {
    param([Parameter(Mandatory)][string]$Scope)
    return @($spec.entries | Where-Object { $_.scopes -contains $Scope })
}

$publisherKey = 'Software\NOBA\Archive'

Write-Host ('=' * 72)
Write-Host 'NOBA Archive - Explorer registration (interim, classic handler)'
Write-Host ('=' * 72)
Write-Host ''

# ---------------------------------------------------------------------------
# Removal.
# ---------------------------------------------------------------------------
if ($Remove) {
    $removed = 0

    foreach ($scope in $scopes) {
        $menu = Get-MenuKey -Scope $scope
        if (Remove-NobaKey -SubKey $menu) {
            Write-Host "  removed  HKCU\$menu"
            $removed++
        }
        else {
            Write-Host "  absent   HKCU\$menu"
        }
    }

    # ADR-0006 also records the install directory. Removing it is part of a clean uninstall.
    if (Remove-NobaKey -SubKey $publisherKey) {
        Write-Host "  removed  HKCU\$publisherKey"
        $removed++
    }
    else {
        Write-Host "  absent   HKCU\$publisherKey"
    }

    # Verified, not assumed. An uninstaller that reports success while leaving a menu entry pointing
    # at a deleted executable is the failure this whole manifest exists to prevent.
    $survivors = @()
    foreach ($scope in $scopes) {
        $menu = Get-MenuKey -Scope $scope
        if (Test-NobaKey -SubKey $menu) { $survivors += "HKCU\$menu" }
    }
    if (Test-NobaKey -SubKey $publisherKey) { $survivors += "HKCU\$publisherKey" }

    Write-Host ''
    if ($survivors.Count -gt 0) {
        Write-Host 'REMOVAL INCOMPLETE - these keys are still present:'
        $survivors | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    Write-Host "$removed key tree(s) removed; nothing NOBA-owned remains under HKCU."
    Write-Host 'Restart Explorer to see the change:  taskkill /f /im explorer.exe && start explorer.exe'
    exit 0
}

# ---------------------------------------------------------------------------
# Where the executables are.
# ---------------------------------------------------------------------------
if (-not $InstallPath) {
    $InstallPath = Join-Path $root (Local 'src\NOBA.Archive.App\bin\x64\Debug\net10.0-windows10.0.19041.0')
}

$appExe = Join-Path $InstallPath $spec.applicationExecutable
$cliExe = Join-Path $InstallPath $spec.commandLineExecutable

if (-not (Test-Path -LiteralPath $appExe)) {
    Write-Host "NOBA Archive is not at $appExe."
    Write-Host 'Build it first, or pass -InstallPath. A menu entry pointing at nothing is worse than no entry.'
    exit 1
}

if (-not (Test-Path -LiteralPath $cliExe)) {
    Write-Host "noba-ar.exe is not next to the application ($cliExe)."
    Write-Host 'The headless verbs - Test, Extract here, Compress to - need it. Copy it there and re-run.'
    exit 1
}

Write-Host "Application : $appExe"
Write-Host "CLI         : $cliExe"
Write-Host ''

Set-NobaValue -SubKey $publisherKey -Name $spec.installPathValue -Value $InstallPath
Write-Host "  publisher  HKCU\$publisherKey"

# ---------------------------------------------------------------------------
# Translating one entry into one registry command string.
# ---------------------------------------------------------------------------

<#
.SYNOPSIS
    The shell's substitution for one manifest argument token.
.DESCRIPTION
    A total mapping with no default branch that guesses. A token this does not recognise throws, so an
    argument kind added in Core without a decision about how the classic registry expresses it fails
    the registration loudly instead of registering a command missing an argument - which is exactly
    what D2 and D3 were.

      literal:X      a fixed word, emitted as-is
      ArchivePath    "%1" - the invoked item
      SelectedPaths  %*  - the whole selection, one process, per MultiSelectModel below

    ContainingFolder and SuggestedTarget have no substitution at all, which is why an entry using them
    is marked directlyRegistrable = false and never reaches this function.
#>
function Convert-NobaToken {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Token)

    if ($Token.StartsWith('literal:')) {
        $word = $Token.Substring('literal:'.Length)
        if ($word -match '[\s"]') {
            throw "The manifest literal '$word' contains whitespace or a quote; the registry command string cannot carry it unambiguously."
        }
        return $word
    }

    switch ($Token) {
        'ArchivePath' { return '"%1"' }
        'SelectedPaths' { return '%*' }
        default { throw "The manifest argument token '$Token' has no classic-registry substitution. Decide one, or mark the entry directlyRegistrable = false." }
    }
}

<#
.SYNOPSIS
    The command string for one entry: the executable, then its arguments, and nothing invented.
.DESCRIPTION
    For a directly registrable entry the arguments are the manifest's own token list, translated one
    for one. For an entry the registry cannot express, the command is a call to the manifest's
    resolver verb carrying the entry's id - the id, the verb name and the executable all come from the
    manifest, so this still names no action of its own.

    cmd.exe is never involved either way: this is a direct executable invocation and the path Windows
    substitutes is one argument.
#>
function Get-EntryCommand {
    param([Parameter(Mandatory)]$Entry)

    $exe = if ($Entry.executable -eq $spec.applicationExecutable) { $appExe } else { $cliExe }

    if ($Entry.directlyRegistrable) {
        $parts = @($Entry.arguments | ForEach-Object { Convert-NobaToken -Token $_ })
    }
    else {
        $selection = if ($Entry.multiSelect -eq 'AllItemsAtOnce') { '%*' } else { '"%1"' }
        $parts = @($spec.resolverVerb, $Entry.id, $selection)
    }

    return ('"{0}" {1}' -f $exe, ($parts -join ' '))
}

<#
.SYNOPSIS
    How the shell should behave when several things are selected.
.DESCRIPTION
    D4. The classic default is one process per selected item, so a three-file "Compress to 7Z"
    produced three single-file archives instead of one archive of three. An entry whose argument list
    names the whole selection is registered Player - invoked once, with all of it. An entry with room
    for exactly one is registered Single, so the shell hides it rather than offering a batch the
    action model has no request shape for (OPEN-1).
#>
function Get-EntryMultiSelectModel {
    param([Parameter(Mandatory)]$Entry)

    switch ($Entry.multiSelect) {
        'AllItemsAtOnce' { return 'Player' }
        'SingleItemOnly' { return 'Single' }
        default { throw "The manifest multiSelect value '$($Entry.multiSelect)' is not one this script knows how to register." }
    }
}

# ---------------------------------------------------------------------------
# One submenu per scope, holding the entries the manifest assigned to it.
# ---------------------------------------------------------------------------
foreach ($scope in $scopes) {
    $menu = Get-MenuKey -Scope $scope

    Set-NobaValue -SubKey $menu -Name 'MUIVerb' -Value $spec.menuTitle
    Set-NobaValue -SubKey $menu -Name 'Icon' -Value $appExe

    # An empty "subcommands" value is what turns this key into a cascading submenu whose entries come
    # from its own \shell subkey. The value is deliberately empty; the name is what does the work.
    Set-NobaValue -SubKey $menu -Name 'subcommands' -Value ''

    foreach ($entry in (Get-ScopeEntries -Scope $scope)) {
        $item = Get-EntryKey -Scope $scope -EntryId $entry.id

        Set-NobaValue -SubKey $item -Name 'MUIVerb' -Value $entry.label
        Set-NobaValue -SubKey $item -Name 'MultiSelectModel' -Value (Get-EntryMultiSelectModel -Entry $entry)
        Set-NobaValue -SubKey "$item\command" -Name '' -Value (Get-EntryCommand -Entry $entry)

        Write-Host ("  {0,-28} {1,-22} {2}" -f $scope, $entry.id, $entry.label)
    }
}

# ---------------------------------------------------------------------------
# Read back what was written. Nothing here trusts that a write succeeded because it did not throw.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Verifying...'

$missing = @()
foreach ($scope in $scopes) {
    $menu = Get-MenuKey -Scope $scope

    if (-not (Test-NobaKey -SubKey $menu)) {
        $missing += "HKCU\$menu"
        continue
    }

    if ($null -eq (Get-NobaValue -SubKey $menu -Name 'subcommands')) {
        $missing += "HKCU\$menu :: subcommands"
    }

    foreach ($entry in (Get-ScopeEntries -Scope $scope)) {
        $item = Get-EntryKey -Scope $scope -EntryId $entry.id

        $expected = Get-EntryCommand -Entry $entry
        $actual = Get-NobaValue -SubKey "$item\command" -Name ''
        if ($actual -ne $expected) {
            $missing += "HKCU\$item\command :: expected '$expected', found '$actual'"
        }

        $expectedModel = Get-EntryMultiSelectModel -Entry $entry
        $actualModel = Get-NobaValue -SubKey $item -Name 'MultiSelectModel'
        if ($actualModel -ne $expectedModel) {
            $missing += "HKCU\$item :: MultiSelectModel expected '$expectedModel', found '$actualModel'"
        }
    }
}

if ($null -eq (Get-NobaValue -SubKey $publisherKey -Name $spec.installPathValue)) {
    $missing += "HKCU\$publisherKey :: $($spec.installPathValue)"
}

# A menu written under any class other than the ones the manifest asked for means a path was resolved
# as a pattern. Counted rather than assumed, because that failure is invisible until uninstall cannot
# find it. Comparison is on the first path segment, since a scope may be nested
# (SystemFileAssociations\.zip).
$scopeRoots = @($scopes | ForEach-Object { $_.Split('\')[0] } | Sort-Object -Unique)
$classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes')
$scattered = @()
if ($classes) {
    try {
        foreach ($name in $classes.GetSubKeyNames()) {
            if ($scopeRoots -contains $name) { continue }
            $probe = $classes.OpenSubKey("$name\shell\$($spec.menuId)")
            if ($probe) { $scattered += $name; $probe.Dispose() }
        }
    }
    finally {
        $classes.Dispose()
    }
}

if ($missing.Count -gt 0 -or $scattered.Count -gt 0) {
    Write-Host ''
    Write-Host 'REGISTRATION INCOMPLETE.'
    $missing | ForEach-Object { Write-Host "  missing: $_" }
    if ($scattered.Count -gt 0) {
        Write-Host "  the menu was also written under $($scattered.Count) unrelated class key(s):"
        $scattered | Select-Object -First 10 | ForEach-Object { Write-Host "      $_" }
        Write-Host '  run -Remove, then report this - a path was treated as a pattern.'
    }
    exit 1
}

$entryCount = 0
foreach ($scope in $scopes) { $entryCount += (Get-ScopeEntries -Scope $scope).Count }
Write-Host "  every key read back: $($scopes.Count) submenu(s), $entryCount entry registration(s), no stray entries."

Write-Host ''
Write-Host 'Registered for the current user.'
Write-Host 'Restart Explorer to see it:  taskkill /f /im explorer.exe && start explorer.exe'
Write-Host ''
Write-Host 'NOTE: this is the classic handler. On Windows 11 the entry lives under "Show more options".'
Write-Host '      The top-level Windows 11 menu needs the signed sparse package from ADR-0006, which'
Write-Host '      this repository does not build yet. See docs\SHELL_INTEGRATION.md.'
