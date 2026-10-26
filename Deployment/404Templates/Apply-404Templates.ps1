param(
    [string]$RobustRoot = 'H:\grid\robust-net8',
    [string]$RegionsRoot = 'H:\grid_final\opensim-base\Regions',
    [string]$BackupRoot = 'H:\grid\backups'
)

$ErrorActionPreference = 'Stop'
$robustTemplate = Join-Path $PSScriptRoot 'robust.html'
$regionTemplate = Join-Path $PSScriptRoot 'region.html'
foreach ($path in @($robustTemplate, $regionTemplate, $RobustRoot, $RegionsRoot)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required path does not exist: $path" }
}

$backup = Join-Path $BackupRoot ('404-template-deploy-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Install-Page([string]$Path, [string]$Content, [string]$BackupName) {
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path -LiteralPath $Path) {
        $backupPath = Join-Path $script:backup $BackupName
        New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
        Copy-Item -LiteralPath $Path -Destination $backupPath -Force
    }
    [System.IO.File]::WriteAllText($Path, $Content, $script:utf8)
}

$robustHtml = [System.IO.File]::ReadAllText($robustTemplate)
Install-Page (Join-Path $RobustRoot 'http_404.html') $robustHtml 'robust/http_404.html'
Install-Page (Join-Path $RobustRoot '404/robust.html') $robustHtml 'robust/404/robust.html'

$count = 0
foreach ($region in Get-ChildItem -LiteralPath $RegionsRoot -Directory) {
    $html = [System.IO.File]::ReadAllText($regionTemplate).Replace('{{REGION}}', [System.Net.WebUtility]::HtmlEncode($region.Name))
    Install-Page (Join-Path $region.FullName 'http_404.html') $html ("regions/{0}/http_404.html" -f $region.Name)
    Install-Page (Join-Path $region.FullName ("404/{0}.html" -f $region.Name)) $html ("regions/{0}/404/{0}.html" -f $region.Name)
    $count++
}

Write-Output "Installed Robust fallback/configured page and $count region pages."
Write-Output "Previous pages (if any) backed up to: $backup"
