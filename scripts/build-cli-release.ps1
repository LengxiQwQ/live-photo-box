# LivePhotoBox CLI-Only Release Build — 编译 + 打包独立 CLI zip（不含 GUI）
# 用法: powershell -ExecutionPolicy Bypass -File build-cli-release.ps1
#       powershell -ExecutionPolicy Bypass -File build-cli-release.ps1 -CI  (GitHub Actions)
#
# 产物: publish\Live-Photo-Box-v{version}-x64-cli.zip
# 内容: livephotobox.exe + 别名 + Tools\ (外部工具链)

param([switch]$CI)

$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

[Console]::OutputEncoding = [Text.Encoding]::UTF8
chcp 65001 > $null

$manifest = Get-Content 'LivePhotoBox\Package.appxmanifest' -Raw -Encoding UTF8
$versionFull = if ($manifest -match 'Identity.*Version\s*=\s*"([^"]+)"') { $Matches[1] } else { '0.0.0.0' }
$version = $versionFull -replace '\.0$', ''

Write-Host '============================================' -ForegroundColor Cyan
Write-Host "  Live Photo Box CLI-Only Release v$version" -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host ''

# 不清除 publish\，保留 GUI build 已有的产物
if (-not (Test-Path publish)) { New-Item -ItemType Directory publish | Out-Null }

$outDir = 'publish\cli_standalone'
if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ── 1. Publish CLI ─────────────────────────────────────────────
Write-Host '[1/4] Publishing CLI x64 (SelfContained)...' -ForegroundColor Yellow

dotnet publish 'LivePhotoBox.CLI\LivePhotoBox.CLI.csproj' -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "       dotnet publish exited with code $LASTEXITCODE" -ForegroundColor DarkYellow
}

if (-not (Test-Path "$outDir\livephotobox.exe")) {
    Write-Host 'BUILD FAILED - livephotobox.exe not found in output' -ForegroundColor Red
    if (-not $CI) { pause }
    exit 1
}
Write-Host '       CLI publish OK' -ForegroundColor Green

# ── 2. 复制外部工具 + 本地化 ──────────────────────────────────
Write-Host ''
Write-Host '[2/4] Copying external tools...' -ForegroundColor Yellow

# Copy Tools (skip jpegtran.exe — only GUI Repair uses it)
$toolsSrc = Join-Path $projectRoot 'LivePhotoBox\Tools'
if (Test-Path $toolsSrc) {
    New-Item -ItemType Directory -Path "$outDir\Tools" -Force | Out-Null
    Get-ChildItem $toolsSrc | ForEach-Object {
        if ($_.Name -ne 'jpegtran.exe') {
            Copy-Item $_.FullName "$outDir\Tools\" -Recurse -Force
        }
    }
    Write-Host '       Tools\ copied (jpegtran.exe excluded)' -ForegroundColor Green
}
else {
    Write-Host '       WARNING: Tools\ not found, CLI may not work' -ForegroundColor DarkYellow
}

# ── 3. Clean unnecessary files ─────────────────────────────────
Write-Host ''
Write-Host '[3/4] Cleaning unnecessary files...' -ForegroundColor Yellow

# 1. Strip all locale satellite dirs (CLI English-only, resw embedded in Core.dll)
$count = 0
foreach ($dir in (Get-ChildItem $outDir -Directory -ErrorAction SilentlyContinue)) {
    if ($dir.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)?$') {
        Remove-Item -Recurse -Force $dir.FullName -ErrorAction SilentlyContinue
        $count++
    }
}
Write-Host "       Removed $count locale folders" -ForegroundColor Gray

# 2. 删除调试符号 + XML 文档 + 开发机残留
Remove-Item -Force "$outDir\*.pdb" -ErrorAction SilentlyContinue
Remove-Item -Force "$outDir\*.xml" -ErrorAction SilentlyContinue
Remove-Item -Force "$outDir\appsettings.json" -ErrorAction SilentlyContinue

$kb = (Get-ChildItem $outDir -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1KB
Write-Host "       Final size: $('{0:N0}' -f $kb) KB" -ForegroundColor Green

# ── 4. 打包 zip ───────────────────────────────────────────────
Write-Host ''
Write-Host '[4/4] Creating CLI zip...' -ForegroundColor Yellow

$zipName = "Live-Photo-Box-v$version-x64-cli.zip"
$zipPath = "publish\$zipName"
Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath -Force
$zipSize = '{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "       $zipName  ($zipSize)" -ForegroundColor Green

Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '============================================' -ForegroundColor Cyan
Write-Host '  CLI-Only Build Complete!' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host "  $zipName  ($zipSize)" -ForegroundColor White
Write-Host ''

if (-not $CI) { pause }
