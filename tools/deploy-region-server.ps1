<#
.SYNOPSIS
    Repeatable .NET 10 region-server binary cutover into an OpenSim program home.

.DESCRIPTION
    Copies ONLY the managed/unmanaged binary surface of a self-contained publish
    bundle (staging) into an existing production program home, after taking a
    verified backup. Everything that is configuration, region state or data is
    owned by the operator and is never touched.

    ############################################################################
    # HARD SAFETY RULE - THE NEVER-TOUCH LIST                                  #
    #                                                                           #
    # This script must NEVER create, delete, overwrite, truncate or touch:      #
    #   regions\            (per-region OpenSim.ini + region state)              #
    #   data\               (Serilog logs, per-region data dirs)                 #
    #   database\ databases\ (SQLite files)                                     #
    #   assets\             (cached assets / OARs)                               #
    #   SSL\                (server + client certificates)                      #
    #   config-include\     (included ini fragments)                             #
    #   robust-include\                                                                #
    #   bin\                (any nested tool binaries)                           #
    #   Robust.ini, Robust.HG.ini.example, Robust.ini.*                          #
    #   OpenSim.ini, OpenSim.ini.example, any *.ini, any *.ini.*                 #
    #   OpenSimDefaults.ini                                                        #
    #   ossl*.ini, osslEnable.ini, osslDefaultEnable.ini                          #
    #   log.config                                     (logging stays as-is)    #
    #   any *.log / *.stat / *.json under data\ or regions\                      #
    #                                                                           #
    # This is enforced by construction: the copy set is built from an explicit  #
    # ALLOW-LIST of file extensions and an explicit ALLOW-LIST of directories.  #
    # Anything not on the allow-list is skipped and reported. Deny rules are    #
    # applied AFTER the allow-list as a second, independent barrier.            #
    ############################################################################

    Other required properties:
      * DRY RUN BY DEFAULT. Nothing is written unless -Force is passed.
      * Refuses to run while any OpenSim.exe is running, unless -AllowRunning.
      * Timestamped backup of every file that will be overwritten, into
        -BackupRoot, with file-count and total-byte verification BEFORE any
        write/delete happens. If verification fails, nothing is touched.
      * Post-copy verification of critical files, including that the retired
        InWorldz.Phlox.dll is ABSENT (consolidated into Phlox.ScriptEngine.dll).

.PARAMETER BundlePath
    Staging publish bundle root, e.g. H:\grid\staging\tranquillity-net10-v3.
    Must contain OpenSim.exe.

.PARAMETER TargetHome
    Production program home that receives the binaries, e.g.
    H:\grid_final\opensim-base. Must already exist.

.PARAMETER BackupRoot
    Parent directory under which a timestamped backup folder is created.
    Default: H:\grid\backups

.PARAMETER Force
    Actually write. Without this switch the script is a pure dry run.

.PARAMETER AllowRunning
    Proceed even if OpenSim.exe processes are detected (they will hold file
    locks; only useful for a controlled hot-swap).

.EXAMPLE
    .\deploy-region-server.ps1 -BundlePath H:\grid\staging\tranquillity-net10-v3 `
                               -TargetHome H:\grid_final\opensim-base

.EXAMPLE
    .\deploy-region-server.ps1 -BundlePath H:\grid\staging\tranquillity-net10-v3 `
                               -TargetHome H:\grid_final\opensim-base `
                               -BackupRoot H:\grid\backups -Force
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BundlePath,

    [Parameter(Mandatory = $true)]
    [string]$TargetHome,

    [string]$BackupRoot = 'H:\grid\backups',

    [switch]$Force,

    [switch]$AllowRunning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Allow-list definitions (the ONLY things this script may ever copy)
# ---------------------------------------------------------------------------

# Root-level files: allowed extensions. Deliberately excludes .ini and .log.
$AllowedRootExtensions = @(
    '.dll', '.exe', '.pdb', '.json', '.config',
    '.xml', '.dat', '.html', '.htm', '.txt', '.sh', '.png'
)

# Directories this script owns inside the bundle. Nothing else is ever recursed.
$AllowedBundleDirs = @(
    'lib64', 'runtimes',
    'cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr',
    'zh-Hans', 'zh-Hant'
)

# Extensions permitted inside the native/localization directories above.
$AllowedNativeExtensions = @('.dll', '.so', '.dylib')
$AllowedLocalePattern = '*.resources.dll'

# Extensions permitted inside the localization resource directories.
$LocaleDirs = @('cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr', 'zh-Hans', 'zh-Hant')
$NativeDirs = @('lib64', 'runtimes')

# Exact filenames never copied, regardless of extension.
$DenyExactNames = @('log.config')

# Deny regexes applied to any PATH SEGMENT (directory name).
# These are the never-touch directories, matched case-insensitively.
$DenyDirPatterns = @(
    '^(regions|data|databases?|assets|SSL|bin|config-include|robust-include)$'
)

# Deny regexes applied to the FILE NAME only (not the directory chain).
# Note the assembly Microsoft.Extensions.Configuration.Ini.dll is a legitimate
# managed assembly, so the *.ini rule explicitly exempts managed assemblies.
$DenyFilePatterns = @(
    '\.ini($|\.)',            # any *.ini and any *.ini.*   (e.g. OpenSim.ini.example)
    '^log\.config$',
    '^ossl[^.]*\.ini',        # osslEnable.ini, osslDefaultEnable.ini
    '\.log$',                 # stray logs
    '\.stat$'
)

# Never a managed assembly, whatever its name looks like.
$AssemblyExtension = '.dll'

# Post-copy verification contract.
$RequiredFiles = @(
    'OpenSim.exe',
    'lib64\ubode.dll',
    'lib64\BulletSim.dll',
    'TasiaAddons.Quic.dll',
    'Microsoft.Data.Sqlite.dll',
    'Microsoft.Extensions.Caching.Memory.dll'
)
$ForbiddenFiles = @('InWorldz.Phlox.dll')

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host "  $Title"
    Write-Host ('=' * 78)
}

function Test-Denied {
    param([string]$RelativePath)

    $leaf = Split-Path -Leaf $RelativePath

    if ($DenyExactNames -contains $leaf) { return $true }

    # Managed assemblies are never denied by name - only by directory.
    if ($leaf.ToLowerInvariant().EndsWith($AssemblyExtension)) {
        $isDenied = $false
    }
    else {
        $isDenied = $false
        foreach ($p in $DenyFilePatterns) {
            if ($leaf -match $p) { $isDenied = $true; break }
        }
    }
    if ($isDenied) { return $true }

    $segments = @(($RelativePath -split '[\\/]') | Select-Object -SkipLast 1)
    foreach ($seg in $segments) {
        foreach ($p in $DenyDirPatterns) {
            if ($seg -match $p) { return $true }
        }
    }
    return $false
}

function Get-RelativePath {
    param([string]$Base, [string]$Full)
    $b = $Base.TrimEnd('\') + '\'
    if ($Full.StartsWith($b, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Full.Substring($b.Length)
    }
    return $Full
}

# ---------------------------------------------------------------------------
# 0. Preconditions
# ---------------------------------------------------------------------------

Write-Section 'PRECONDITIONS'

$BundlePath = (Resolve-Path -LiteralPath $BundlePath).Path
if (-not (Test-Path -LiteralPath $TargetHome -PathType Container)) {
    Write-Host "FATAL: TargetHome does not exist: $TargetHome" -ForegroundColor Red
    exit 2
}
$TargetHome = (Resolve-Path -LiteralPath $TargetHome).Path

if (-not (Test-Path -LiteralPath (Join-Path $BundlePath 'OpenSim.exe') -PathType Leaf)) {
    Write-Host "FATAL: BundlePath has no OpenSim.exe - is this a RegionServer publish bundle?" -ForegroundColor Red
    exit 2
}

Write-Host "Bundle    : $BundlePath"
Write-Host "Target    : $TargetHome"
Write-Host "BackupRoot: $BackupRoot"
Write-Host "Mode      : $(if ($Force) { 'APPLY (-Force)' } else { 'DRY RUN (no writes will occur)' })" `
    -ForegroundColor $(if ($Force) { 'Yellow' } else { 'Cyan' })

$mode = if ($Force) { 'APPLY' } else { 'DRY-RUN' }

# ---------------------------------------------------------------------------
# 1. Running-process guard
# ---------------------------------------------------------------------------

Write-Section 'RUNNING PROCESS GUARD'

$running = @(Get-Process -Name 'OpenSim' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "OpenSim.exe processes detected: $($running.Count)" -ForegroundColor Yellow
    if (-not $AllowRunning) {
        Write-Host ''
        Write-Host 'ABORT: refusing to run while regions are live.' -ForegroundColor Red
        Write-Host 'Stop the regions first, or re-run with -AllowRunning for a controlled hot-swap.' -ForegroundColor Red
        exit 3
    }
    Write-Host '-AllowRunning supplied: continuing despite live processes.' -ForegroundColor Yellow
} else {
    Write-Host 'No OpenSim.exe processes running. OK.'
}

# ---------------------------------------------------------------------------
# 2. Build the allow-listed copy plan
# ---------------------------------------------------------------------------

Write-Section 'BUILDING COPY PLAN (ALLOW-LIST ONLY)'

$plan = New-Object System.Collections.Generic.List[object]
$skippedDirs = New-Object System.Collections.Generic.List[string]

# --- root-level files -------------------------------------------------------
Get-ChildItem -LiteralPath $BundlePath -File | ForEach-Object {
    $ext = $_.Extension.ToLowerInvariant()
    if ($AllowedRootExtensions -notcontains $ext) {
        return
    }
    $rel = $_.Name
    if (Test-Denied -RelativePath $rel) {
        Write-Host ("  SKIP (deny-rule)  {0}" -f $rel) -ForegroundColor DarkGray
        return
    }
    $plan.Add([pscustomobject]@{
        Relative = $rel
        Source   = $_.FullName
        Size     = $_.Length
    })
}

# --- allow-listed directories ----------------------------------------------
foreach ($dir in $AllowedBundleDirs) {
    $srcDir = Join-Path $BundlePath $dir
    if (-not (Test-Path -LiteralPath $srcDir -PathType Container)) { continue }

    Get-ChildItem -LiteralPath $srcDir -Recurse -File | ForEach-Object {
        $rel = Get-RelativePath -Base $BundlePath -Full $_.FullName
        $ext = $_.Extension.ToLowerInvariant()
        $leaf = $_.Name

        if ($NativeDirs -contains $dir) {
            # lib64 / runtimes: natives plus extensionless marker files
            if (-not (($AllowedNativeExtensions -contains $ext) -or $ext -eq '')) {
                Write-Host ("  SKIP (not a native) {0}" -f $rel) -ForegroundColor DarkGray
                return
            }
        }
        elseif ($LocaleDirs -contains $dir) {
            if ($leaf -notlike $AllowedLocalePattern) {
                Write-Host ("  SKIP (not a satellite) {0}" -f $rel) -ForegroundColor DarkGray
                return
            }
        }

        if (Test-Denied -RelativePath $rel) {
            Write-Host ("  SKIP (deny-rule)  {0}" -f $rel) -ForegroundColor DarkGray
            return
        }

        $plan.Add([pscustomobject]@{
            Relative = $rel
            Source   = $_.FullName
            Size     = $_.Length
        })
    }
}

# Report every bundle directory that was deliberately NOT owned by this script.
Get-ChildItem -LiteralPath $BundlePath -Directory | Where-Object {
    $AllowedBundleDirs -notcontains $_.Name
} | ForEach-Object {
    $skippedDirs.Add($_.Name)
}

$plan = @($plan | Sort-Object Relative)

Write-Host ""
Write-Host "Planned copies      : $($plan.Count)"
Write-Host ("Planned bytes       : {0:N0}" -f (($plan | Measure-Object -Property Size -Sum).Sum))

if ($skippedDirs.Count -gt 0) {
    Write-Host ""
    Write-Host 'Bundle directories NOT owned by this script (never recursed):' -ForegroundColor DarkGray
    Write-Host ('  ' + (($skippedDirs | Sort-Object) -join ', ')) -ForegroundColor DarkGray
}

# Classify: Add / Update / Unchanged
$adds = New-Object System.Collections.Generic.List[object]
$updates = New-Object System.Collections.Generic.List[object]
$unchanged = New-Object System.Collections.Generic.List[object]

foreach ($p in $plan) {
    $dst = Join-Path $TargetHome $p.Relative
    if (-not (Test-Path -LiteralPath $dst -PathType Leaf)) {
        $adds.Add($p)
    }
    elseif ((Get-Item -LiteralPath $dst).Length -ne $p.Size) {
        $updates.Add($p)
    }
    else {
        $unchanged.Add($p)
    }
}

Write-Host ""
Write-Host "  ADD      (not present in target) : $($adds.Count)"
Write-Host "  UPDATE   (size differs)          : $($updates.Count)"
Write-Host "  UNCHANGED(same size)             : $($unchanged.Count)"

if ($adds.Count -gt 0) {
    Write-Host ''
    Write-Host 'Files that would be ADDED:' -ForegroundColor Yellow
    $adds | Select-Object -First 40 | ForEach-Object { Write-Host ("    + {0}" -f $_.Relative) -ForegroundColor DarkYellow }
    if ($adds.Count -gt 40) { Write-Host ("    ... and {0} more" -f ($adds.Count - 40)) -ForegroundColor DarkYellow }
}

if ($updates.Count -gt 0) {
    Write-Host ''
    Write-Host 'Files that would be OVERWRITTEN (these get backed up):' -ForegroundColor Yellow
    $updates | ForEach-Object {
        $dst = Get-Item -LiteralPath (Join-Path $TargetHome $_.Relative)
        Write-Host ("    ~ {0}  {1} -> {2} bytes" -f $_.Relative, $dst.Length, $_.Size) -ForegroundColor DarkYellow
    }
}

# Explicitly reassure about the never-touch list.
Write-Host ''
Write-Host 'Never-touch surface confirmed untouched by this plan:' -ForegroundColor Green
# $plan is allow-list output, so by definition nothing forbidden is present.
# This is an independent re-check of the built plan, not a restatement of intent.
$guardChecks = @(
    @{ Label = 'regions\*.ini'; Rx = '^regions\\.*\.ini($|\.)'; ExcludeDll = $false },
    @{ Label = 'regions\ (any)'; Rx = '^regions\\'; ExcludeDll = $false },
    @{ Label = 'data\ (any)'; Rx = '^data\\'; ExcludeDll = $false },
    @{ Label = 'databases?\ (any)'; Rx = '^databases?\\'; ExcludeDll = $false },
    @{ Label = 'assets\ (any)'; Rx = '^assets\\'; ExcludeDll = $false },
    @{ Label = 'SSL\ (any)'; Rx = '^SSL\\'; ExcludeDll = $false },
    @{ Label = 'bin\ (any)'; Rx = '^bin\\'; ExcludeDll = $false },
    @{ Label = 'config-include\ (any)'; Rx = '^config-include\\'; ExcludeDll = $false },
    @{ Label = 'Robust.ini*'; Rx = '^Robust\.ini'; ExcludeDll = $false },
    @{ Label = 'ossl*.ini'; Rx = '^ossl'; ExcludeDll = $false },
    @{ Label = 'OpenSimDefaults.ini'; Rx = '^OpenSimDefaults\.ini$'; ExcludeDll = $false },
    @{ Label = 'log.config'; Rx = '(^|\\)log\.config$'; ExcludeDll = $false },
    @{ Label = 'any *.ini / *.ini.*'; Rx = '\.ini($|\.)'; ExcludeDll = $true }
)
$guardViolations = 0
foreach ($g in $guardChecks) {
    $cand = @($plan)
    if ($g.ExcludeDll) {
        # Managed assemblies may legitimately contain '.ini.' in their name
        # (Microsoft.Extensions.Configuration.Ini.dll); they are not ini files.
        $cand = @($cand | Where-Object { -not $_.Relative.ToLowerInvariant().EndsWith('.dll') })
    }
    $hit = @($cand | Where-Object { $_.Relative -match $g.Rx })
    $count = $hit.Count
    $colour = if ($count -eq 0) { 'Green' } else { 'Red' }
    $note = if ($count -eq 0) { '' } else { '  <-- VIOLATION: ' + (($hit | Select-Object -First 5 | ForEach-Object { $_.Relative }) -join ', ') }
    Write-Host ("    {0,-24} planned copies: {1}{2}" -f $g.Label, $count, $note) -ForegroundColor $colour
    $guardViolations += $count
}
if ($guardViolations -gt 0) {
    Write-Host ''
    Write-Host "ABORT: the built copy plan touches the never-touch surface ($guardViolations entries)." -ForegroundColor Red
    exit 5
}

# ---------------------------------------------------------------------------
# 3. Backup (only meaningful in APPLY mode; dry run reports what it would do)
# ---------------------------------------------------------------------------

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDir = Join-Path $BackupRoot "programhome-bin-$stamp"

Write-Section 'BACKUP'

# Files that already exist in the target and would be overwritten.
$toBackup = @($plan | Where-Object { Test-Path -LiteralPath (Join-Path $TargetHome $_.Relative) -PathType Leaf })

$expectedCount = $toBackup.Count
$expectedBytes = [int64](($toBackup | ForEach-Object { (Get-Item -LiteralPath (Join-Path $TargetHome $_.Relative)).Length } | Measure-Object -Sum).Sum)

Write-Host "Backup folder    : $backupDir"
Write-Host "Files to back up : $expectedCount"
Write-Host ("Bytes to back up : {0:N0}" -f $expectedBytes)

if ($mode -eq 'DRY-RUN') {
    Write-Host ''
    Write-Host 'DRY RUN: no backup was taken, no file was written.' -ForegroundColor Cyan
}
else {
    if ($expectedCount -gt 0) {
        if (-not (Test-Path -LiteralPath $BackupRoot -PathType Container)) {
            New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
        }
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

        foreach ($p in $toBackup) {
            $dst = Join-Path $TargetHome $p.Relative
            $bak = Join-Path $backupDir $p.Relative
            $bakDir = Split-Path -Parent $bak
            if (-not (Test-Path -LiteralPath $bakDir -PathType Container)) {
                New-Item -ItemType Directory -Path $bakDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $dst -Destination $bak -Force
        }

        # ---- VERIFY THE BACKUP BEFORE ANY DESTRUCTIVE WRITE ----
        $actual = @(Get-ChildItem -LiteralPath $backupDir -Recurse -File)
        $actualBytes = [int64](($actual | Measure-Object -Property Length -Sum).Sum)

        $bad = New-Object System.Collections.Generic.List[string]
        if ($actual.Count -ne $expectedCount) {
            $bad.Add("file count mismatch: expected $expectedCount, got $($actual.Count)")
        }
        if ($actualBytes -ne $expectedBytes) {
            $bad.Add("byte total mismatch: expected $expectedBytes, got $actualBytes")
        }
        foreach ($p in $toBackup) {
            $bak = Join-Path $backupDir $p.Relative
            $orig = Get-Item -LiteralPath (Join-Path $TargetHome $p.Relative)
            if (-not (Test-Path -LiteralPath $bak -PathType Leaf)) {
                $bad.Add("missing in backup: $($p.Relative)")
            }
            elseif ((Get-Item -LiteralPath $bak).Length -ne $orig.Length) {
                $bad.Add("size mismatch in backup: $($p.Relative)")
            }
        }

        if ($bad.Count -gt 0) {
            Write-Host ''
            Write-Host 'BACKUP VERIFICATION FAILED - ABORTING WITHOUT TOUCHING THE TARGET.' -ForegroundColor Red
            $bad | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
            exit 4
        }

        Write-Host ''
        Write-Host "Backup VERIFIED: $($actual.Count) files, $('{0:N0}' -f $actualBytes) bytes match the originals." -ForegroundColor Green
    }
    else {
        Write-Host 'Nothing to back up (no planned file already exists in the target).'
    }
}

# ---------------------------------------------------------------------------
# 4. Copy
# ---------------------------------------------------------------------------

Write-Section 'COPY'

if ($mode -eq 'DRY-RUN') {
    Write-Host 'DRY RUN: would copy the planned files listed above. Nothing written.' -ForegroundColor Cyan
}
else {
    $copied = 0
    $locked = New-Object System.Collections.Generic.List[string]
    foreach ($p in $plan) {
        $dst = Join-Path $TargetHome $p.Relative
        $dstDir = Split-Path -Parent $dst
        if (-not (Test-Path -LiteralPath $dstDir -PathType Container)) {
            New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
        }
        try {
            Copy-Item -LiteralPath $p.Source -Destination $dst -Force
            $copied++
        }
        catch {
            $locked.Add("$($p.Relative) :: $($_.Exception.Message)")
        }
    }
    Write-Host "Copied $copied / $($plan.Count) files."
    if ($locked.Count -gt 0) {
        Write-Host ''
        Write-Host "$($locked.Count) file(s) FAILED to copy (locked by a running process?):" -ForegroundColor Red
        $locked | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    }
}

# ---------------------------------------------------------------------------
# 5. Post-copy verification
# ---------------------------------------------------------------------------

Write-Section 'POST-COPY VERIFICATION'

$verifyFail = 0
foreach ($f in $RequiredFiles) {
    $full = Join-Path $TargetHome $f
    if (Test-Path -LiteralPath $full -PathType Leaf) {
        $len = (Get-Item -LiteralPath $full).Length
        Write-Host ("  PRESENT  {0,-46} {1,12:N0} bytes" -f $f, $len) -ForegroundColor Green
    }
    else {
        Write-Host ("  MISSING  {0}" -f $f) -ForegroundColor Red
        $verifyFail++
    }
}
foreach ($f in $ForbiddenFiles) {
    $full = Join-Path $TargetHome $f
    if (Test-Path -LiteralPath $full) {
        Write-Host ("  PRESENT(retired!)  {0} - should have been consolidated into Phlox.ScriptEngine.dll" -f $f) -ForegroundColor Red
        $verifyFail++
    }
    else {
        Write-Host ("  ABSENT   {0,-46} (correct: consolidated into Phlox.ScriptEngine.dll)" -f $f) -ForegroundColor Green
    }
}
$phlox = Join-Path $TargetHome 'Phlox.ScriptEngine.dll'
if (Test-Path -LiteralPath $phlox) {
    Write-Host ("  PRESENT  {0,-46} {1,12:N0} bytes" -f 'Phlox.ScriptEngine.dll', (Get-Item -LiteralPath $phlox).Length) -ForegroundColor Green
}
else {
    Write-Host '  MISSING  Phlox.ScriptEngine.dll' -ForegroundColor Red
    $verifyFail++
}

# Runtime identity, useful evidence that the net10 build actually landed.
try {
    $rc = Join-Path $TargetHome 'OpenSim.runtimeconfig.json'
    if (Test-Path -LiteralPath $rc) {
        $j = Get-Content -LiteralPath $rc -Raw | ConvertFrom-Json
        $ro = $j.runtimeOptions
        # Self-contained publishes use includedFrameworks; framework-dependent use framework.
        $fw = $null
        if ($ro.includedFrameworks) { $fw = "$($ro.includedFrameworks[0].name) $($ro.includedFrameworks[0].version) (self-contained)" }
        elseif ($ro.framework) { $fw = "$($ro.framework.name) $($ro.framework.version) (framework-dependent)" }
        Write-Host ("  runtime  tfm={0}  framework={1}" -f $ro.tfm, $fw)
        Write-Host ("  runtime  hostfxr={0}  coreclr={1}  System.Private.CoreLib={2}" -f `
                (Test-Path -LiteralPath (Join-Path $TargetHome 'hostfxr.dll')),
                (Test-Path -LiteralPath (Join-Path $TargetHome 'coreclr.dll')),
                (Test-Path -LiteralPath (Join-Path $TargetHome 'System.Private.CoreLib.dll')))
    }
}
catch { Write-Host "  (runtimeconfig read failed: $($_.Exception.Message))" -ForegroundColor DarkYellow }

# ---------------------------------------------------------------------------
# 6. Summary
# ---------------------------------------------------------------------------

Write-Section 'SUMMARY'

$verdict = if ($verifyFail -eq 0) { 'PASS' } else { "FAIL ($verifyFail problem(s))" }
Write-Host "Mode                 : $mode"
Write-Host "Bundle               : $BundlePath"
Write-Host "Target program home  : $TargetHome"
Write-Host "Files planned        : $($plan.Count)"
Write-Host "  add / update / same: $($adds.Count) / $($updates.Count) / $($unchanged.Count)"
if ($mode -eq 'APPLY') {
    Write-Host "Backup location      : $backupDir"
    Write-Host "Rollback source      : restore the files above from the backup, or H:\grid\backups\programhome-bin-20261002-194326"
}
else {
    Write-Host "Backup location      : (not created - dry run)"
}
Write-Host "Verification         : $verdict"
Write-Host ''
Write-Host 'NEXT STEPS after a real -Force run:'
Write-Host '  1. Restart regions via FreshMetaverseManager (or your own launcher).'
Write-Host '  2. Run tools\verify-deployment.ps1 -ProgramHome <home> and confirm all regions'
Write-Host '     show process UP, port LISTENING, log NOT GROWING, 0 critical patterns.'
Write-Host ''

if ($verifyFail -gt 0) { exit 1 }
exit 0
