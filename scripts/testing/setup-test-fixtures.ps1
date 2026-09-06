[CmdletBinding()]
param(
    [string]$TargetDirectory,
    [string]$SourceDirectory
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..\..'))

if (-not $TargetDirectory) {
    $TargetDirectory = Join-Path $projectRoot 'tests\fixtures\realsamples'
}
if (-not $SourceDirectory) {
    $SourceDirectory = Join-Path $projectRoot 'designs\各个机型测试'
}

$resolvedTarget = [System.IO.Path]::GetFullPath($TargetDirectory)
$resolvedSource = [System.IO.Path]::GetFullPath($SourceDirectory)

if (-not (Test-Path -LiteralPath $resolvedTarget)) {
    New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
}

# 1. If local private designs directory exists, populate fixtures from it
if (Test-Path -LiteralPath $resolvedSource) {
    Write-Host "[Fixtures] Syncing samples from local designs directory: $resolvedSource -> $resolvedTarget" -ForegroundColor Gray
    Get-ChildItem -LiteralPath $resolvedSource -File | ForEach-Object {
        $dest = Join-Path $resolvedTarget $_.Name
        if (-not (Test-Path -LiteralPath $dest)) {
            Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        }
    }
}

# 2. If an archive or environment override was provided
if ($env:LIVEPHOTOBOX_SAMPLES_ARCHIVE -and (Test-Path -LiteralPath $env:LIVEPHOTOBOX_SAMPLES_ARCHIVE)) {
    Write-Host "[Fixtures] Extracting samples archive: $env:LIVEPHOTOBOX_SAMPLES_ARCHIVE -> $resolvedTarget" -ForegroundColor Gray
    Expand-Archive -LiteralPath $env:LIVEPHOTOBOX_SAMPLES_ARCHIVE -DestinationPath $resolvedTarget -Force
}

# 3. Check for mandatory P3 protocol sample coverage
$mandatorySamples = @(
    '苹果双文件.HEIC',
    '苹果双文件.MOV',
    '红米老款-GV1.JPG',
    '小米.jpg',
    'oppo.jpg',
    'vivo.jpg',
    'vivo双文件.jpg',
    'vivo双文件.mp4',
    '三星.jpg',
    '三星.heic',
    '华为-Mate80.jpg',
    '华为Mate80.heic',
    '荣耀.jpg'
)

$missing = @()
foreach ($s in $mandatorySamples) {
    $p = Join-Path $resolvedTarget $s
    $localP = if (Test-Path -LiteralPath $resolvedSource) { Join-Path $resolvedSource $s } else { $null }
    if (-not (Test-Path -LiteralPath $p) -and (-not $localP -or -not (Test-Path -LiteralPath $localP))) {
        $missing += $s
    }
}

if ($missing.Count -gt 0) {
    Write-Warning "[Fixtures] Missing mandatory real sample files: $($missing -join ', ')"
    if ($env:CI -eq 'true') {
        throw "CI Verification Gate Blocker: Mandatory real samples missing: $($missing -join ', '). RealSamples cannot be silently skipped in P3 verification."
    }
} else {
    Write-Host "[Fixtures] All mandatory P3 real samples verified available in test environment." -ForegroundColor Green
}
