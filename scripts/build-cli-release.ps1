# LivePhotoBox CLI-Only Release Build — 编译 + 打包独立 CLI zip（不含 GUI）
# 用法: powershell -ExecutionPolicy Bypass -File build-cli-release.ps1
#       powershell -ExecutionPolicy Bypass -File build-cli-release.ps1 -CI  (GitHub Actions)
#
# 产物: publish\Live-Photo-Box-v{version}-x64-cli.zip
# 内容: livephotobox-boot.exe + 4 个 Go shim 别名 + Tools\ (外部工具链)
#       别名: livephotobox, livebox, lpb, livephoto（winget symlink 兼容）

param([switch]$CI)

# ── zip 完整性辅助函数 ──────────────────────────────────────────
# 注意：Windows PowerShell 5.1 的 Compress-Archive 对「刚写入 / 仍被杀软扫描」的文件
# 会静默丢弃（不报错、无警告）。打包后校验必需文件都在，缺失则自动重试，仍失败则报错退出。
function Invoke-ReliableZip {
    param(
        [string]$SourceDir,
        [string]$ZipPath,
        [string[]]$RequiredNames,
        [switch]$CI
    )
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $attempts = 0
    do {
        $attempts++
        Compress-Archive -Path "$SourceDir\*" -DestinationPath $ZipPath -Force
        $z = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $ZipPath))
        $names = @($z.Entries | ForEach-Object { $_.Name })
        $z.Dispose()
        $missing = @($RequiredNames | Where-Object { $_ -notin $names })
        if ($missing.Count -eq 0) { return }
        Write-Host "       WARN: zip 缺失必需文件 ($($missing -join ', '))，第 $attempts 次重试..." -ForegroundColor DarkYellow
        Start-Sleep -Milliseconds 500
    } while ($attempts -lt 3)
    Write-Host "BUILD FAILED - 重试 3 次后 zip 仍缺失: $($missing -join ', ')" -ForegroundColor Red
    if (-not $CI) { pause }
    exit 1
}

$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

[Console]::OutputEncoding = [Text.Encoding]::UTF8
chcp 65001 > $null

try {
    & "$PSScriptRoot\native\build-native.ps1" -Configuration Release -Architecture x64 -RunTests
}
catch {
    Write-Host "NATIVE BUILD FAILED - $($_.Exception.Message)" -ForegroundColor Red
    if (-not $CI) { pause }
    exit 1
}

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
Write-Host '[1/4] Publishing CLI x64 (SelfContained, single-file)...' -ForegroundColor Yellow

# 单文件 CLI：托管核心打进 boot exe，原生库（Magick.Native / heif）外置 → 启动零解压
dotnet publish 'LivePhotoBox.CLI\LivePhotoBox.CLI.csproj' -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=true -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "       dotnet publish exited with code $LASTEXITCODE" -ForegroundColor DarkYellow
}

if (-not (Test-Path "$outDir\livephotobox-boot.exe")) {
    Write-Host 'BUILD FAILED - livephotobox-boot.exe not found in output' -ForegroundColor Red
    if (-not $CI) { pause }
    exit 1
}
if (-not (Test-Path "$outDir\LivePhotoBox.Native.dll")) {
    Write-Host 'BUILD FAILED - LivePhotoBox.Native.dll not found in CLI output' -ForegroundColor Red
    if (-not $CI) { pause }
    exit 1
}
Write-Host '       CLI publish OK' -ForegroundColor Green

# ── 1.5 用 Go shim 覆盖别名（winget symlink 兼容，与 build-release.ps1 的 step 3 一致）──
Write-Host ''
Write-Host '[1.5/4] Building Go alias shims (symlink-safe)...' -ForegroundColor Yellow

$goCmd = (Get-Command go -ErrorAction SilentlyContinue).Source
$shimSrc = Join-Path $projectRoot 'scripts\utils\alias-launcher.go'

if ($goCmd -and (Test-Path $shimSrc)) {
    $cliAliases = @('livephotobox', 'livebox', 'lpb', 'livephoto')
    foreach ($alias in $cliAliases) {
        $outExe = Join-Path $outDir "$alias.exe"
        & $goCmd build -ldflags="-s -w" -o $outExe $shimSrc 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0 -and (Test-Path $outExe)) {
            $shimSize = '{0:N1} KB' -f ((Get-Item $outExe).Length / 1KB)
            Write-Host "       $alias.exe  ($shimSize)" -ForegroundColor Green
            # Remove apphost-copy leftovers (Go shim doesn't need these)
            Remove-Item -Force "$outDir\$alias.runtimeconfig.json" -ErrorAction SilentlyContinue
            Remove-Item -Force "$outDir\$alias.deps.json" -ErrorAction SilentlyContinue
            Remove-Item -Force "$outDir\$alias.pdb" -ErrorAction SilentlyContinue
        } else {
            Write-Host "       $alias.exe  BUILD FAILED - keeping apphost copy" -ForegroundColor DarkYellow
        }
    }
} else {
    if (-not $goCmd) { Write-Host '       Go not found - keeping apphost copies (winget symlinks will NOT work!)' -ForegroundColor DarkYellow }
    if (-not (Test-Path $shimSrc)) { Write-Host "       $shimSrc not found" -ForegroundColor DarkYellow }
}

# ── 2. Clean unnecessary files ─────────────────────────────────
Write-Host ''
Write-Host '[2/3] Cleaning unnecessary files...' -ForegroundColor Yellow

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

# 3.5 拷贝 PATH 辅助脚本（用户可一键把本目录加入用户 PATH）
Copy-Item 'scripts\install\add-to-path.cmd'      "$outDir\add-to-path.cmd"      -Force
Copy-Item 'scripts\install\remove-from-path.cmd' "$outDir\remove-from-path.cmd" -Force
Write-Host '       PATH helper scripts (add/remove-to-path.cmd) copied' -ForegroundColor Green

# ── 3. 打包 zip ───────────────────────────────────────────────
Write-Host ''
Write-Host '[3/3] Creating CLI zip...' -ForegroundColor Yellow

$zipName = "Live-Photo-Box-v$version-x64-cli.zip"
$zipPath = "publish\$zipName"
Invoke-ReliableZip -SourceDir $outDir -ZipPath $zipPath -RequiredNames @('LivePhotoBox.Native.dll','add-to-path.cmd','remove-from-path.cmd') -CI:$CI
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
