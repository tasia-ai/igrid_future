<#
.SYNOPSIS
    Read-only health check for a live OpenSim .NET 10 region fleet.

.DESCRIPTION
    Walks every region listed in deploy.json and reports, in one table:
      * process up            (OpenSim.exe with --inifile regions\<name>\OpenSim.ini)
      * TCP listening         (on that region's http_port)
      * log file present      (data\<name>\OpenSim.Server.RegionServer<yyyyMMdd>.log)
      * log line count
      * log STILL GROWING     (sampled twice, default 20 s apart)
      * critical pattern hits (FATAL, Unhandled, DllNotFound, FileNotFound,
                               IndexOutOfRange)

    Also reports whether Robust is listening on its public port (default 22000).

    ############################################################################
    # THIS SCRIPT IS STRICTLY READ-ONLY.                                       #
    # It opens files for reading only, creates nothing, deletes nothing,       #
    # writes nothing, starts nothing and stops nothing. There is no -Force and  #
    # no write path of any kind.                                                #
    ############################################################################

    The "still growing" check is the point of this script: a region whose log
    keeps growing across the sample window while its process is up is in a
    crash-restart loop (exactly how the old console crash-loop bug presented).

.PARAMETER ProgramHome
    Production program home, e.g. H:\grid_final\opensim-base.

.PARAMETER DeployJson
    Region list. Default H:\grid_final\generated\deploy.json

.PARAMETER RobustPort
    Robust public TCP port. Default 22000.

.PARAMETER SampleSeconds
    Gap between the two log samples. Default 20.

.EXAMPLE
    .\verify-deployment.ps1 -ProgramHome H:\grid_final\opensim-base
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProgramHome,

    [string]$DeployJson = 'H:\grid_final\generated\deploy.json',

    [int]$RobustPort = 22000,

    [int]$SampleSeconds = 20,

    [string]$RobustHome = 'H:\grid\robust-net8'
)

$ErrorActionPreference = 'Stop'

$CriticalPatterns = @('FATAL', 'Unhandled', 'DllNotFound', 'FileNotFound', 'IndexOutOfRange')

# A log that is growing is NOT by itself a crash: a healthy region serving
# viewers appends DEBUG lines constantly. The unambiguous crash-loop signature
# is a log containing MORE THAN ONE process lifetime, i.e. the startup banner
# appearing repeatedly in the same file, combined with growth during the sample.
# Exactly ONE line is emitted per process lifetime, so counting this single
# canonical banner gives the number of lifetimes directly. Counting several
# banner strings and dividing would over-count, because a single startup emits
# all of them.
$StartupBannerPatterns = @(
    '[STARTUP]: Beginning startup processing'
)

$ProgramHome = (Resolve-Path -LiteralPath $ProgramHome).Path
$DeployJson = (Resolve-Path -LiteralPath $DeployJson).Path
$robustHomeGuess = $RobustHome

$deploy = Get-Content -LiteralPath $DeployJson -Raw | ConvertFrom-Json
$sims = @($deploy.sims)

Write-Host ''
Write-Host ('=' * 132)
Write-Host ('  OpenSim .NET 10 deployment verification (READ-ONLY)')
Write-Host ('=' * 132)
Write-Host ("  Program home : $ProgramHome")
Write-Host ("  deploy.json  : $DeployJson")
Write-Host ("  Regions      : {0}" -f $sims.Count)
Write-Host ("  Run at       : {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Host ''

# --- snapshot: running processes ------------------------------------------
Write-Host 'Collecting process state...' -ForegroundColor DarkGray
$procs = @(Get-CimInstance Win32_Process -Filter "Name='OpenSim.exe'" -ErrorAction SilentlyContinue |
    Select-Object ProcessId, CommandLine, CreationDate)

# --- snapshot: listening TCP ports ----------------------------------------
Write-Host 'Collecting TCP listeners...' -ForegroundColor DarkGray
$listening = New-Object 'System.Collections.Generic.HashSet[int]'
try {
    Get-NetTCPConnection -State Listen -ErrorAction Stop | ForEach-Object {
        [void]$listening.Add([int]$_.LocalPort)
    }
}
catch {
    # Fallback for environments without Get-NetTCPConnection
    netstat -ano -p TCP | ForEach-Object {
        if ($_ -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING') {
            [void]$listening.Add([int]$matches[1])
        }
    }
}

# --- helper: newest region log --------------------------------------------
function Get-RegionLog {
    param([string]$RegionName)
    $dir = Join-Path (Join-Path $ProgramHome 'data') $RegionName
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return $null }
    $logs = @(Get-ChildItem -LiteralPath $dir -Filter 'OpenSim.Server.RegionServer*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($logs.Count -eq 0) { return $null }
    return $logs[0]
}

# Serilog holds the current log file with a non-shared handle, so a plain
# File.OpenText / Get-Content would fail with "used by another process".
# We must open explicitly for READ with full sharing, and we must read the
# byte count from the SAME open handle. Falling back to Get-Item for the size
# while the read failed silently produces bogus growth deltas.
$FileShareAll = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete

function Get-LogStats {
    param([string]$Path)

    $lines = 0
    $hits = @{}
    foreach ($p in $CriticalPatterns) { $hits[$p] = 0 }

    $fs = $null
    $sr = $null
    try {
        $fs = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            $FileShareAll)

        # Byte count must come from the open handle, not a separate stat call.
        $bytes = $fs.Length

        $starts = 0
        $sr = New-Object System.IO.StreamReader($fs)
        while (($line = $sr.ReadLine()) -ne $null) {
            $lines++
            foreach ($p in $CriticalPatterns) {
                if ($line.IndexOf($p, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $hits[$p]++ }
            }
            foreach ($b in $StartupBannerPatterns) {
                if ($line.IndexOf($b, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $starts++; break }
            }
        }

        # Exactly one canonical banner per process lifetime.
        $lifetimes = [Math]::Max(1, $starts)

        return [pscustomobject]@{
            Ok        = $true
            Lines     = $lines
            Bytes     = $bytes
            Hits      = $hits
            Total     = [int](($hits.Values | Measure-Object -Sum).Sum)
            Starts    = $starts
            Lifetimes = $lifetimes
            Error     = ''
        }
    }
    catch {
        return [pscustomobject]@{
            Ok        = $false
            Lines     = 0
            Bytes     = 0
            Hits      = $hits
            Total     = 0
            Starts    = 0
            Lifetimes = 0
            Error     = $_.Exception.Message
        }
    }
    finally {
        if ($sr) { $sr.Dispose() } elseif ($fs) { $fs.Dispose() }
    }
}

# --- pass 1: log sizes / stats --------------------------------------------
Write-Host 'Sample 1: reading region logs...' -ForegroundColor DarkGray

$state = @{}
foreach ($s in $sims) {
    $log = Get-RegionLog -RegionName $s.name
    $st = if ($log) { Get-LogStats -Path $log.FullName } else { $null }
    $state[$s.name] = [pscustomobject]@{
        Sim     = $s
        LogPath = if ($log) { $log.FullName } else { $null }
        S1      = $st
    }
}

Write-Host ("Waiting {0} s for the growth sample (a growing log while the process is up = crash loop)..." -f $SampleSeconds) -ForegroundColor DarkGray
Start-Sleep -Seconds $SampleSeconds

Write-Host 'Sample 2: re-reading region logs...' -ForegroundColor DarkGray
foreach ($s in $sims) {
    $e = $state[$s.name]
    $size2 = $null
    if ($e.LogPath -and (Test-Path -LiteralPath $e.LogPath)) {
        try {
            # Same shared-read open as sample 1, so the byte count is
            # comparable and is not affected by Serilog's exclusive handle.
            $fs2 = [System.IO.File]::Open(
                $e.LogPath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                $FileShareAll)
            try { $size2 = $fs2.Length }
            finally { $fs2.Dispose() }
        }
        catch {
            Write-Host ("    WARN: could not re-stat {0}: {1}" -f $e.LogPath, $_.Exception.Message) -ForegroundColor Yellow
        }
    }
    $e | Add-Member -NotePropertyName S2 -NotePropertyValue $size2 -Force
}

# --- region table ----------------------------------------------------------
$rows = New-Object System.Collections.Generic.List[object]

foreach ($s in $sims) {
    $e = $state[$s.name]

    $p = @($procs | Where-Object { $_.CommandLine -and $_.CommandLine -match ('regions\\' + [regex]::Escape($s.name) + '\\OpenSim\.ini') })
    $procUp = $p.Count -gt 0

    $listeningOk = $listening.Contains([int]$s.http_port)

    $logOk = ($null -ne $e.LogPath) -and $e.S1.Ok
    $lines = if ($logOk) { $e.S1.Lines } else { -1 }

    # Growth is only meaningful when both samples were read successfully.
    # If either sample failed, report UNKNOWN - never "no".
    $delta = 0
    if ($logOk -and $null -ne $e.S2) {
        $delta = $e.S2 - $e.S1.Bytes
    }

    $crit = if ($logOk) { $e.S1.Total } else { -1 }

    # CRASH LOOP = the log grew during the sample AND it contains more than one
    # process lifetime (repeated startup banner). Growth with a single lifetime
    # is ordinary DEBUG request traffic from a healthy, busy region.
    $lifetimes = if ($logOk) { $e.S1.Lifetimes } else { 0 }
    $crashLoop = ($logOk -and $delta -gt 0 -and $lifetimes -gt 1)

    $growTxt = if (-not $logOk) { 'n/a' }
    elseif ($null -eq $e.S2) { 'UNKNOWN' }
    elseif ($crashLoop) { 'YES <<<' }
    elseif ($delta -gt 0) { "yes (+$delta b)" }
    else { 'no' }

    $rows.Add([pscustomobject]@{
        Region    = $s.name
        ProcUp    = $procUp
        PIDs      = (($p | ForEach-Object { $_.ProcessId }) -join ',')
        Started   = $(if ($p.Count -gt 0) { (($p | Sort-Object CreationDate | Select-Object -First 1).CreationDate) } else { $null })
        HttpPort  = [int]$s.http_port
        Listen    = $listeningOk
        LogFile   = $(if ($logOk) { Split-Path -Leaf $e.LogPath } else { '(none)' })
        Lines     = $lines
        Growing   = $growTxt
        Delta     = $delta
        Lives     = $lifetimes
        CrashLoop = $crashLoop
        Critical  = $crit
        Detail   = $(if ($logOk -and $e.S1.Total -gt 0) {
                (($e.S1.Hits.GetEnumerator() | Where-Object { $_.Value -gt 0 } |
                    ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
            } else { '' })
    })
}

Write-Host ''
Write-Host '  REGION TABLE'
Write-Host ('=' * 168)
$fmt = '{0,-31} {1,-5} {2,-7} {3,-6} {4,-8} {5,-7} {6,7} {7,-14} {8,5} {9,5} {10,4}'
Write-Host ("  " + ($fmt -f 'REGION', 'PROC', 'PID', 'HTTP', 'LISTEN', 'LOGDATE', 'LINES', 'GROWTH', 'LIVES', 'CRIT', ''))
Write-Host ('  ' + ('-' * 164))

foreach ($r in $rows) {
    $logDate = if ($r.LogFile -eq '(none)') { '-' } else { ($r.LogFile -replace '^.*RegionServer(\d{8})\.log$', '$1') }
    Write-Host ("  " + ($fmt -f `
            $r.Region,
            $(if ($r.ProcUp) { 'UP' } else { 'DOWN' }),
            $(if ($r.PIDs) { $r.PIDs } else { '-' }),
            $r.HttpPort,
            $(if ($r.Listen) { 'LISTEN' } else { 'NO' }),
            $logDate,
            $(if ($r.Lines -ge 0) { $r.Lines } else { '-' }),
            $r.Growing,
            $(if ($r.Lives -gt 0) { $r.Lives } else { '-' }),
            $(if ($r.Critical -ge 0) { $r.Critical } else { '-' }),
            $r.Detail))
}

Write-Host ''
Write-Host '  Column notes:'
Write-Host '    LINES   lines in the current region log'
Write-Host '    GROWTH  bytes appended during the sampling window. "yes (+N b)" is ordinary'
Write-Host '            DEBUG request traffic from a busy but healthy region.'
Write-Host '    LIVES   process lifetimes in the log (one "[STARTUP]: Beginning startup'
Write-Host '            processing" banner each). >1 means the region restarted into'
Write-Host '            the same file.'
Write-Host '    GROWTH = "YES <<<" is a crash loop: it grew AND contains more than one lifetime.'
Write-Host '    CRIT    hits for FATAL / Unhandled / DllNotFound / FileNotFound / IndexOutOfRange'

# --- Robust ----------------------------------------------------------------
Write-Host ''
Write-Host ('=' * 132)
Write-Host '  ROBUST'
Write-Host ('=' * 132)
$robustListen = $listening.Contains([int]$RobustPort)

# Robust may run as Robust.exe, or as `dotnet.exe Robust.dll` when it is
# framework-dependent. Match both, and require the command line to reference
# Robust.dll/Robust.exe so we do not match an unrelated dotnet host.
$robustProcs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -match '(?i)Robust\.(dll|exe)' })

$robustHome = Join-Path $robustHomeGuess 'Robust.ini'
Write-Host ("  Robust process      : {0}  pid(s): {1}" -f `
    $(if ($robustProcs.Count -gt 0) { 'UP' } else { 'NOT FOUND' }),
    $(if ($robustProcs.Count -gt 0) { ($robustProcs.ProcessId -join ',') } else { '-' }))
foreach ($rp in $robustProcs) {
    $host2 = if ($rp.CommandLine -match '"?([^" ]*Robust\.(?:dll|exe))"?') { $matches[1] } else { '(unknown)' }
    Write-Host ("    pid {0,-6} host {1,-12} started {2}" -f $rp.ProcessId, $rp.Name, $rp.CreationDate)
    Write-Host ("      cmd: {0}" -f $rp.CommandLine.Trim())
}
Write-Host ("  Listening on {0,-6}  : {1}" -f $RobustPort, $(if ($robustListen) { 'YES' } else { 'NO' }))
Write-Host ("  Expected Robust home: {0}  (config exists: {1})" -f $robustHomeGuess, (Test-Path -LiteralPath $robustHome))

# --- roll-up ---------------------------------------------------------------
$upCount = @($rows | Where-Object ProcUp).Count
$listenCount = @($rows | Where-Object Listen).Count
$logCount = @($rows | Where-Object { $_.LogFile -ne '(none)' }).Count
$crashLoopCount = @($rows | Where-Object CrashLoop).Count
$busyCount = @($rows | Where-Object { $_.Delta -gt 0 -and -not $_.CrashLoop }).Count
$unknownCount = @($rows | Where-Object { $_.Growing -eq 'UNKNOWN' -or $_.Growing -eq 'n/a' -and $_.Lines -lt 0 }).Count
$critCount = @($rows | Where-Object { $_.Critical -gt 0 }).Count

# A region whose log could not be sampled is INCONCLUSIVE, not healthy.
# It must never be counted as a pass.
$healthy = ($upCount -eq $sims.Count) -and ($listenCount -eq $sims.Count) -and
($logCount -eq $sims.Count) -and ($crashLoopCount -eq 0) -and
($unknownCount -eq 0) -and ($critCount -eq 0) -and $robustListen

Write-Host ''
Write-Host ('=' * 132)
Write-Host '  ROLL-UP'
Write-Host ('=' * 132)
Write-Host ("  Regions in deploy.json          : {0}" -f $sims.Count)
Write-Host ("  Process UP                      : {0}" -f $upCount)
Write-Host ("  HTTP port LISTENING             : {0}" -f $listenCount)
Write-Host ("  Log file present                : {0}" -f $logCount)
Write-Host ("  CRASH LOOPS (grew + >1 lifetime) : {0}" -f $crashLoopCount)
Write-Host ("  Logs grew (busy, single lifetime): {0}" -f $busyCount)
Write-Host ("  Log growth INCONCLUSIVE         : {0}" -f $unknownCount)
Write-Host ("  Regions with critical patterns  : {0}" -f $critCount)
Write-Host ("  Robust listening on {0,-10} : {1}" -f $RobustPort, $(if ($robustListen) { 'YES' } else { 'NO' }))
Write-Host ''

if (-not $healthy) {
    Write-Host '  PROBLEMS:' -ForegroundColor Red
    $rows | Where-Object { -not $_.ProcUp } | ForEach-Object { Write-Host ("    process DOWN      : {0}" -f $_.Region) -ForegroundColor Red }
    $rows | Where-Object { -not $_.Listen } | ForEach-Object { Write-Host ("    port {0} not listening : {1}" -f $_.HttpPort, $_.Region) -ForegroundColor Red }
    $rows | Where-Object { $_.LogFile -eq '(none)' } | ForEach-Object { Write-Host ("    no log file       : {0}" -f $_.Region) -ForegroundColor Red }
    $rows | Where-Object CrashLoop | ForEach-Object { Write-Host ("    CRASH LOOP       : {0} grew +{1} bytes in {2}s across {3} process lifetimes" -f $_.Region, $_.Delta, $SampleSeconds, $_.Lives) -ForegroundColor Red }
    $rows | Where-Object { $_.Growing -eq 'UNKNOWN' } | ForEach-Object { Write-Host ("    log unreadable (INCONCLUSIVE, not a pass) : {0}" -f $_.Region) -ForegroundColor Red }
    $rows | Where-Object { $_.Critical -gt 0 } | ForEach-Object { Write-Host ("    critical patterns : {0} -> {1}" -f $_.Region, $_.Detail) -ForegroundColor Red }
    if (-not $robustListen) { Write-Host ("    Robust not listening on {0}" -f $RobustPort) -ForegroundColor Red }
    Write-Host ''
}

Write-Host ("  VERDICT: {0}" -f $(if ($healthy) { 'HEALTHY - all regions and Robust OK' } else { 'UNHEALTHY' })) `
    -ForegroundColor $(if ($healthy) { 'Green' } else { 'Red' })
Write-Host ("  (this script performed no writes; nothing was started, stopped or reconfigured)")
Write-Host ''

if (-not $healthy) { exit 1 }
exit 0
