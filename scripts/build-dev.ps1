# LivePhotoBox Dev Build — 编译并发布（未打包），含 GUI + CLI
# 用法: powershell -ExecutionPolicy Bypass -File build-dev.ps1
#       powershell -ExecutionPolicy Bypass -File build-dev.ps1 -CI

param([switch]$CI)

$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

[Console]::OutputEncoding = [Text.Encoding]::UTF8
chcp 65001 > $null

$manifest = Get-Content 'LivePhotoBox\Package.appxmanifest' -Raw -Encoding UTF8
$version = if ($manifest -match 'Identity.*Version\s*=\s*"([^"]+)"') { $Matches[1] } else { '0.0.0.0' }

Write-Host '============================================' -ForegroundColor Cyan
Write-Host "  Live Photo Box Dev Build v$version" -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host ''

if (Test-Path publish) { Remove-Item -Recurse -Force publish }
New-Item -ItemType Directory publish | Out-Null

Write-Host 'Building Release x64 (SelfContained)...' -ForegroundColor Yellow

$outDir = 'publish\portable_x64'
$publishArgs = @(
    'publish', 'LivePhotoBox\LivePhotoBox.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:Platform=x64',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:EnableMsixTooling=false',
    '-o', $outDir
)
dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "       dotnet publish exited with code $LASTEXITCODE" -ForegroundColor DarkYellow
}

if (-not (Test-Path "$outDir\Live Photo Box.exe")) {
    Write-Host "BUILD FAILED - exe not found in $outDir" -ForegroundColor Red
    if (-not $CI) { pause }
    exit 1
}

# CLI publish + merge
Write-Host 'Building CLI...' -ForegroundColor Yellow
$cliPublishArgs = @(
    'publish', 'LivePhotoBox.CLI\LivePhotoBox.CLI.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:Platform=x64',
    '-o', 'publish\cli_x64'
)
dotnet @cliPublishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "       CLI publish warning: exit code $LASTEXITCODE" -ForegroundColor DarkYellow
}

if (-not (Test-Path 'publish\cli_x64\livephotobox.exe')) {
    Write-Host '       CLI build FAILED - exe not found, skipping merge' -ForegroundColor DarkYellow
} else {
    # 只复制 GUI 目录中不存在的文件，避免 CLI 的 SDK 投影 DLL 覆盖 GUI 版本
    Get-ChildItem 'publish\cli_x64' -Recurse | ForEach-Object {
        $target = Join-Path $outDir $_.FullName.Substring((Get-Item 'publish\cli_x64').FullName.Length + 1)
        if ($_.PSIsContainer) {
            if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
        } else {
            if (-not (Test-Path $target)) { Copy-Item $_.FullName $target }
        }
    }
    Write-Host '       CLI merged into portable' -ForegroundColor Green

    # CLI standalone (dev, unzipped)
    $cliDir = 'publish\cli_standalone'
    if (Test-Path $cliDir) { Remove-Item -Recurse -Force $cliDir }
    Copy-Item 'publish\cli_x64' $cliDir -Recurse -Force

    # Copy Tools (skip jpegtran.exe — only GUI Repair uses it)
    $toolsSrc = Join-Path $projectRoot 'LivePhotoBox\Tools'
    if (Test-Path $toolsSrc) {
        New-Item -ItemType Directory -Path "$cliDir\Tools" -Force | Out-Null
        Get-ChildItem $toolsSrc | ForEach-Object {
            if ($_.Name -ne 'jpegtran.exe') {
                Copy-Item $_.FullName "$cliDir\Tools\" -Recurse -Force
            }
        }
    }

    # Strip all locale satellite dirs (CLI English-only)
    foreach ($dir in (Get-ChildItem $cliDir -Directory -ErrorAction SilentlyContinue)) {
        if ($dir.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)?$') {
            Remove-Item -Recurse -Force $dir.FullName -ErrorAction SilentlyContinue
        }
    }

    Write-Host '       CLI standalone ready' -ForegroundColor Green
}
Remove-Item -Recurse -Force 'publish\cli_x64' -ErrorAction SilentlyContinue

Write-Host '       Build OK' -ForegroundColor Green

Write-Host 'Cleaning locale folders...' -ForegroundColor Yellow

# 从 csproj 读取要保留的原生语言列表
[xml]$csprojXml = Get-Content 'LivePhotoBox\LivePhotoBox.csproj'
$keepLocales = ($csprojXml.Project.PropertyGroup.AppSupportedNativeLocales | Where-Object { $_ }) -split ';'

$removed = 0
Get-ChildItem -Path $outDir -Recurse -Filter '*.mui' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Directory.Name -notin $keepLocales) {
        Remove-Item -Recurse -Force $_.Directory.FullName -ErrorAction SilentlyContinue
        $removed++
    }
}
Write-Host "       Removed $removed locale folders (kept $($keepLocales -join ', '))" -ForegroundColor Gray

Write-Host ''
Write-Host "Output:" -ForegroundColor Green
Write-Host "  GUI + CLI : $outDir\Live Photo Box.exe" -ForegroundColor Green
Write-Host "  CLI only  : publish\cli_standalone\livephotobox.exe  (aliases: lpb, livebox, lipbox, lpbx, livephoto)" -ForegroundColor Green
Write-Host ''
if (-not $CI) { pause }
