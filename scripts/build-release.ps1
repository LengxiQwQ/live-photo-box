# LivePhotoBox Release Build — 编译 + 打包 3 个产物: 便携版 zip + CLI zip + 安装包
# 用法: powershell -ExecutionPolicy Bypass -File build-release.ps1
#       powershell -ExecutionPolicy Bypass -File build-release.ps1 -CI  (GitHub Actions)

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

$manifest = Get-Content 'LivePhotoBox\Package.appxmanifest' -Raw -Encoding UTF8
$versionFull = if ($manifest -match 'Identity.*Version\s*=\s*"([^"]+)"') { $Matches[1] } else { '0.0.0.0' }
$version = $versionFull -replace '\.0$', ''

Write-Host '============================================' -ForegroundColor Cyan
Write-Host "  Live Photo Box Release Build v$version" -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host ''

if (Test-Path publish) { Remove-Item -Recurse -Force publish }
New-Item -ItemType Directory publish | Out-Null

# ═══════════════════════════════════════════════════════════════
# [1/7] Build GUI x64 (SelfContained)
# ═══════════════════════════════════════════════════════════════
Write-Host '[1/7] Building GUI x64 (SelfContained)...' -ForegroundColor Yellow

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
    Write-Host 'BUILD FAILED - GUI exe not found in output' -ForegroundColor Red
    if (-not $CI) { pause }
    exit 1
}
Write-Host '       GUI build OK' -ForegroundColor Green

# ═══════════════════════════════════════════════════════════════
# [2/7] Build CLI（多文件）→ merge into portable dir
# ═══════════════════════════════════════════════════════════════
Write-Host ''
Write-Host '[2/7] Building CLI x64 (SelfContained, multi-file)...' -ForegroundColor Yellow

# 多文件 CLI：别名 exe 为 apphost 副本（几百 KB），依赖同目录 DLL 运行 → merge 时 GUI 已有 DLL 被跳过，增量仅 2~3MB
dotnet publish 'LivePhotoBox.CLI\LivePhotoBox.CLI.csproj' -c Release -r win-x64 --self-contained true -p:Platform=x64 -o publish\cli_multi

if ($LASTEXITCODE -ne 0) {
    Write-Host "       CLI publish warning: exit code $LASTEXITCODE" -ForegroundColor DarkYellow
}

if (Test-Path 'publish\cli_multi\livephotobox-boot.exe') {
    # 只复制 GUI 目录中不存在的文件，避免 CLI 的 SDK 投影 DLL 覆盖 GUI 版本
    Get-ChildItem 'publish\cli_multi' -Recurse | ForEach-Object {
        $target = Join-Path $outDir $_.FullName.Substring((Get-Item 'publish\cli_multi').FullName.Length + 1)
        if ($_.PSIsContainer) {
            if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
        } else {
            if (-not (Test-Path $target)) { Copy-Item $_.FullName $target }
        }
    }
    Write-Host '       CLI merged into portable' -ForegroundColor Green
    Remove-Item -Recurse -Force 'publish\cli_multi' -ErrorAction SilentlyContinue
} else {
    Write-Host '       CLI build FAILED - skipping merge' -ForegroundColor DarkYellow
    Remove-Item -Recurse -Force 'publish\cli_multi' -ErrorAction SilentlyContinue
}

# ═══════════════════════════════════════════════════════════════
# [3/7] CLI single-file + Go shims → standalone zip（winget 用）
# ═══════════════════════════════════════════════════════════════
Write-Host ''
Write-Host '[3/7] Building CLI single-file & packaging standalone...' -ForegroundColor Yellow

# 单文件 CLI：托管核心打进 boot exe，原生库（Magick.Native / heif）外置 → 启动零解压
dotnet publish 'LivePhotoBox.CLI\LivePhotoBox.CLI.csproj' -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=true -o publish\cli_single

if ($LASTEXITCODE -ne 0) {
    Write-Host "       CLI single-file publish warning: exit code $LASTEXITCODE" -ForegroundColor DarkYellow
}

if (Test-Path 'publish\cli_single\livephotobox-boot.exe') {
    # Replace alias copies with Go symlink-safe launchers
    $goCmd = (Get-Command go -ErrorAction SilentlyContinue).Source
    $shimSrc = Join-Path $projectRoot 'scripts\alias-launcher.go'

    if ($goCmd -and (Test-Path $shimSrc)) {
        $cliAliases = @('livephotobox', 'livebox', 'lpb', 'livephoto')
        foreach ($alias in $cliAliases) {
            $outExe = "publish\cli_single\$alias.exe"
            & $goCmd build -ldflags="-s -w" -o $outExe $shimSrc 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0 -and (Test-Path $outExe)) {
                $shimSize = '{0:N1} KB' -f ((Get-Item $outExe).Length / 1KB)
                Write-Host "       $alias.exe  ($shimSize)" -ForegroundColor Green
                # Remove apphost-copy leftovers (Go shim doesn't need these)
                Remove-Item -Force "publish\cli_single\$alias.runtimeconfig.json" -ErrorAction SilentlyContinue
                Remove-Item -Force "publish\cli_single\$alias.deps.json" -ErrorAction SilentlyContinue
                Remove-Item -Force "publish\cli_single\$alias.pdb" -ErrorAction SilentlyContinue
            } else {
                Write-Host "       $alias.exe  BUILD FAILED - keeping apphost copy" -ForegroundColor DarkYellow
            }
        }
    } else {
        if (-not $goCmd) { Write-Host '       Go not found - keeping apphost copies (winget symlinks will NOT work!)' -ForegroundColor DarkYellow }
        if (-not (Test-Path $shimSrc)) { Write-Host "       $shimSrc not found" -ForegroundColor DarkYellow }
    }

    $cliDir = 'publish\cli_standalone'

    # 复制 CLI 发布输出
    Copy-Item 'publish\cli_single' $cliDir -Recurse -Force

    # Copy external tools (jpegtran.exe included — CLI 后续将支持修复功能)
    $toolsSrc = Join-Path $projectRoot 'LivePhotoBox\Tools'
    if (Test-Path $toolsSrc) {
        New-Item -ItemType Directory -Path "$cliDir\Tools" -Force | Out-Null
        Get-ChildItem $toolsSrc | ForEach-Object {
            Copy-Item $_.FullName "$cliDir\Tools\" -Recurse -Force
        }
    }

    # Strip all locale satellite dirs (CLI is English-only, resw embedded in Core.dll)
    foreach ($dir in (Get-ChildItem $cliDir -Directory -ErrorAction SilentlyContinue)) {
        if ($dir.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)?$') {
            Remove-Item -Recurse -Force $dir.FullName -ErrorAction SilentlyContinue
        }
    }

    # 删除调试符号 / XML / 残留
    Remove-Item -Force "$cliDir\*.pdb" -ErrorAction SilentlyContinue
    Remove-Item -Force "$cliDir\*.xml" -ErrorAction SilentlyContinue
    Remove-Item -Force "$cliDir\appsettings.json" -ErrorAction SilentlyContinue

    # 复制文档（README + LICENSE + 使用指南）
    Copy-Item 'README.md' "$cliDir\README.md" -Force
    Copy-Item 'README.zh-CN.md' "$cliDir\README.zh-CN.md" -Force
    Copy-Item 'LICENSE' "$cliDir\LICENSE" -Force
    Copy-Item 'docs\CLI-User-Guide.md' "$cliDir\CLI-User-Guide.md" -Force
    Copy-Item 'docs\CLI-User-Guide.zh-CN.md' "$cliDir\CLI-User-Guide.zh-CN.md" -Force
    Copy-Item 'scripts\add-to-path.cmd'      "$cliDir\add-to-path.cmd"      -Force
    Copy-Item 'scripts\remove-from-path.cmd' "$cliDir\remove-from-path.cmd" -Force
    Write-Host '       Docs + PATH helper scripts copied' -ForegroundColor Green

    # 打包 CLI zip
    $cliZipName = "Live-Photo-Box-v$version-x64-cli.zip"
    $cliZipPath = "publish\$cliZipName"
    Invoke-ReliableZip -SourceDir $cliDir -ZipPath $cliZipPath -RequiredNames @('README.md','README.zh-CN.md','LICENSE','CLI-User-Guide.md','CLI-User-Guide.zh-CN.md','add-to-path.cmd','remove-from-path.cmd') -CI:$CI
    $cliZipSize = '{0:N1} MB' -f ((Get-Item $cliZipPath).Length / 1MB)
    Write-Host "       $cliZipName  ($cliZipSize)" -ForegroundColor Green

    Remove-Item -Recurse -Force $cliDir -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force 'publish\cli_single' -ErrorAction SilentlyContinue
} else {
    Write-Host '       CLI not built, skipping standalone package' -ForegroundColor DarkYellow
    Remove-Item -Recurse -Force 'publish\cli_single' -ErrorAction SilentlyContinue
}

# ═══════════════════════════════════════════════════════════════
# [4/7] Clean GUI portable
# ═══════════════════════════════════════════════════════════════
Write-Host ''
Write-Host '[4/7] Cleaning unnecessary files...' -ForegroundColor Yellow

$keepLocales = @('zh-CN','en-us')

# 删除多余语言文件夹
$count = 0
foreach ($dir in (Get-ChildItem $outDir -Directory -ErrorAction SilentlyContinue)) {
    if ($dir.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)+$' -and $dir.Name -notin $keepLocales) {
        Remove-Item -Recurse -Force $dir.FullName -ErrorAction SilentlyContinue
        $count++
    }
}
Write-Host "       Removed $count locale folders (kept zh-CN, en-us)" -ForegroundColor Gray

# 删除运行时生成的配置文件（开发机残留）
$appSettings = Join-Path $outDir 'appsettings.json'
if ($appSettings -and (Test-Path $appSettings)) { Remove-Item -Force $appSettings; Write-Host '       Removed appsettings.json' -ForegroundColor Gray }

# 删除 AI/ML 无用文件
foreach ($f in @('DirectML.dll','onnxruntime.dll','onnxruntime_providers_shared.dll','Microsoft.ML.OnnxRuntime.dll')) {
    $p = Join-Path $outDir $f
    if (Test-Path $p) { Remove-Item -Force $p }
}
if (Test-Path "$outDir\NpuDetect") { Remove-Item -Recurse -Force "$outDir\NpuDetect" }

# 删除 Microsoft.Windows.AI.*
Get-ChildItem $outDir -Filter 'Microsoft.Windows.AI*' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $outDir -Filter 'Microsoft.Windows.AI*' -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

# 删除 AI 负载配置和杂项
Remove-Item -Force "$outDir\workloads.json" -ErrorAction SilentlyContinue
Remove-Item -Force "$outDir\WindowsAppRuntime.png" -ErrorAction SilentlyContinue

# 删除调试符号
Remove-Item -Force "$outDir\Live Photo Box.pdb" -ErrorAction SilentlyContinue
Remove-Item -Force "$outDir\livephotobox-boot.pdb" -ErrorAction SilentlyContinue
foreach ($a in @('livebox','lpb','livephoto')) {
    Remove-Item -Force "$outDir\$a.pdb" -ErrorAction SilentlyContinue
}
Remove-Item -Force "$outDir\LivePhotoBox.Core.pdb" -ErrorAction SilentlyContinue

# 删除 XML 文档
Remove-Item -Force "$outDir\*.xml" -ErrorAction SilentlyContinue

$kb = (Get-ChildItem $outDir -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1KB
Write-Host "       Final size: $('{0:N0}' -f $kb) KB" -ForegroundColor Green

# ═══════════════════════════════════════════════════════════════
# [5/7] Clean MUI locale folders（从 csproj 读取允许的语言列表）
# ═══════════════════════════════════════════════════════════════
[xml]$csprojXml = Get-Content 'LivePhotoBox\LivePhotoBox.csproj'
$keepLocales = ($csprojXml.Project.PropertyGroup.AppSupportedNativeLocales | Where-Object { $_ }) -split ';'

Write-Host '[5/7] Cleaning locale folders...' -ForegroundColor Yellow
$removed = 0
Get-ChildItem -Path $outDir -Recurse -Filter '*.mui' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Directory.Name -notin $keepLocales) {
        Remove-Item -Recurse -Force $_.Directory.FullName -ErrorAction SilentlyContinue
        $removed++
    }
}
Write-Host "       Removed $removed locale folders (kept $($keepLocales -join ', '))" -ForegroundColor Gray

# ═══════════════════════════════════════════════════════════════
# [6/7] Create portable zip
# ═══════════════════════════════════════════════════════════════
Write-Host ''
Write-Host '[6/7] Creating portable zip...' -ForegroundColor Yellow

# 复制文档（README + LICENSE + 使用指南）
Copy-Item 'README.md' "$outDir\README.md" -Force
Copy-Item 'README.zh-CN.md' "$outDir\README.zh-CN.md" -Force
Copy-Item 'LICENSE' "$outDir\LICENSE" -Force
Copy-Item 'docs\CLI-User-Guide.md' "$outDir\CLI-User-Guide.md" -Force
Copy-Item 'docs\CLI-User-Guide.zh-CN.md' "$outDir\CLI-User-Guide.zh-CN.md" -Force
Copy-Item 'scripts\add-to-path.cmd'      "$outDir\add-to-path.cmd"      -Force
Copy-Item 'scripts\remove-from-path.cmd' "$outDir\remove-from-path.cmd" -Force

$zipName = "Live-Photo-Box-v$version-x64-portable.zip"
$zipPath = "publish\$zipName"
Invoke-ReliableZip -SourceDir $outDir -ZipPath $zipPath -RequiredNames @('README.md','README.zh-CN.md','LICENSE','CLI-User-Guide.md','CLI-User-Guide.zh-CN.md','add-to-path.cmd','remove-from-path.cmd') -CI:$CI
$zipSize = '{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "       $zipName  ($zipSize)" -ForegroundColor Green

# ═══════════════════════════════════════════════════════════════
# [7/7] Create installer (Inno Setup)
# ═══════════════════════════════════════════════════════════════
Write-Host ''
Write-Host '[7/7] Creating installer...' -ForegroundColor Yellow

$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if (Test-Path $iscc) {
    & $iscc /Qp "/dVERSION=$versionFull" "/dVERSION_SHORT=$version" 'scripts\setup.iss'
    if ($LASTEXITCODE -eq 0) {
        $setupName = "Live-Photo-Box-v$version-x64-setup.exe"
        $setupPath = "publish\$setupName"
        $setupSize = '{0:N1} MB' -f ((Get-Item $setupPath).Length / 1MB)
        Write-Host "       $setupName  ($setupSize)" -ForegroundColor Green
    }
    else {
        Write-Host '       Inno Setup failed' -ForegroundColor Red
    }
}
else {
    Write-Host '       Inno Setup not installed, skipping' -ForegroundColor DarkYellow
}

Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '============================================' -ForegroundColor Cyan
Write-Host '  Build Complete!' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host ''
Get-ChildItem publish | ForEach-Object {
    $s = '{0:N1} MB' -f ($_.Length / 1MB)
    Write-Host "  $($_.Name)  ($s)" -ForegroundColor White
}
Write-Host ''
Write-Host 'Upload to: https://github.com/LengxiQwQ/live-photo-box/releases' -ForegroundColor White
Write-Host ''

if (-not $CI) { pause }
