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
$manifestPath = Join-Path $projectRoot 'tests\fixtures\realsamples-manifest.json'

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

# 3. Check if mandatory samples are missing, and download from release if needed
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

$missingFromTarget = @()
foreach ($s in $mandatorySamples) {
    $p = Join-Path $resolvedTarget $s
    if (-not (Test-Path -LiteralPath $p)) {
        $missingFromTarget += $s
    }
}

if ($missingFromTarget.Count -gt 0) {
    Write-Host "[Fixtures] Missing samples in target ($($missingFromTarget.Count)). Attempting to download from release assets..." -ForegroundColor Cyan
    $releaseUrl = "https://github.com/LengxiQwQ/live-photo-box/releases/download/test-fixtures-v1/realsamples-fixtures.zip"
    $expectedZipSha = "d4ed29a1e49c1eb7bc35f0b4e0533c1efa2ad5d6fe825d0bc256eb1a951d1f5b"
    $tempZip = Join-Path ([System.IO.Path]::GetTempPath()) "lpb-realsamples-fixtures.zip"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
        Write-Host "[Fixtures] Downloading $releaseUrl -> $tempZip" -ForegroundColor Gray
        Invoke-WebRequest -Uri $releaseUrl -OutFile $tempZip -UseBasicParsing
        $downloadedSha = (Get-FileHash -LiteralPath $tempZip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($downloadedSha -ne $expectedZipSha) {
            throw "Downloaded fixtures archive hash mismatch: expected $expectedZipSha, got $downloadedSha"
        }
        Write-Host "[Fixtures] Archive verified. Extracting to $resolvedTarget..." -ForegroundColor Green
        Expand-Archive -LiteralPath $tempZip -DestinationPath $resolvedTarget -Force
    }
    catch {
        Write-Warning "[Fixtures] Failed downloading release fixture asset: $_"
    }
    finally {
        if (Test-Path -LiteralPath $tempZip) {
            Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
        }
    }
}

# 4. Verify integrity against manifest if manifest exists
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($prop in $manifest.PSObject.Properties) {
        $fileName = $prop.Name
        $fileEntry = $prop.Value
        $filePath = Join-Path $resolvedTarget $fileName
        if (Test-Path -LiteralPath $filePath) {
            $item = Get-Item -LiteralPath $filePath
            if ($item.Length -ne $fileEntry.size) {
                Write-Warning "[Fixtures] Size mismatch for $fileName (expected $($fileEntry.size), got $($item.Length))"
            }
            $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne $fileEntry.sha256) {
                Write-Warning "[Fixtures] SHA-256 mismatch for $fileName (expected $($fileEntry.sha256), got $hash)"
            }
        }
    }
}

# 5. Final check for mandatory P3 protocol sample coverage
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
