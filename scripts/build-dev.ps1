# LivePhotoBox Dev Build — 只编译
# 用法: powershell -ExecutionPolicy Bypass -File build-dev.ps1

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

[Console]::OutputEncoding = [Text.Encoding]::UTF8
chcp 65001 > $null

$manifest = Get-Content "Live Photo Box\Package.appxmanifest" -Raw -Encoding UTF8
$version = if ($manifest -match 'Identity.*Version\s*=\s*"([^"]+)"') { $Matches[1] } else { "0.0.0.0" }

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Live Photo Box Dev Build v$version" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

if (Test-Path publish) { Remove-Item -Recurse -Force publish }
New-Item -ItemType Directory publish | Out-Null

Write-Host "Building Release x64 (SelfContained)..." -ForegroundColor Yellow

dotnet publish "Live Photo Box\Live Photo Box.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:WindowsAppSDKSelfContained=true `
    -p:EnableMsixTooling=false `
    -o publish\portable_x64

# dotnet publish 的 exit code 可能因为 MSIX 符号转换警告而非零
# 实际编译产物不受影响，检查 exe 是否存在
if (-not (Test-Path "publish\portable_x64\Live Photo Box.exe")) {
    Write-Host "BUILD FAILED — exe not found" -ForegroundColor Red
    pause; exit 1
}

Write-Host "Cleaning unnecessary files..." -ForegroundColor Yellow

$outDir = "publish\portable_x64"
$keepLocales = @('zh-CN','en-us')

# 1. 删除多余语言文件夹
$count = 0
foreach ($dir in (Get-ChildItem $outDir -Directory -ErrorAction SilentlyContinue)) {
    if ($dir.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)+$' -and $dir.Name -notin $keepLocales) {
        Remove-Item -Recurse -Force $dir.FullName -ErrorAction SilentlyContinue
        $count++
    }
}
Write-Host "       Removed $count locale folders" -ForegroundColor Gray

# 2. 删除 AI/ML 无用文件
foreach ($f in @('DirectML.dll','onnxruntime.dll','onnxruntime_providers_shared.dll','Microsoft.ML.OnnxRuntime.dll')) {
    $p = Join-Path $outDir $f
    if (Test-Path $p) { Remove-Item -Force $p }
}
if (Test-Path "$outDir\NpuDetect") { Remove-Item -Recurse -Force "$outDir\NpuDetect" }

# 3. 删除 Microsoft.Windows.AI.*
Get-ChildItem $outDir -Filter 'Microsoft.Windows.AI*' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $outDir -Filter 'Microsoft.Windows.AI*' -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

# 4. 删除 AI 负载配置和杂项
Remove-Item -Force "$outDir\workloads.json" -ErrorAction SilentlyContinue
Remove-Item -Force "$outDir\WindowsAppRuntime.png" -ErrorAction SilentlyContinue

# 5. 删除 XML 文档
Remove-Item -Force "$outDir\*.xml" -ErrorAction SilentlyContinue

Write-Host "       Done" -ForegroundColor Green

Write-Host ""
Write-Host "Output: publish\portable_x64\" -ForegroundColor Green
Write-Host "Run  : publish\portable_x64\Live Photo Box.exe" -ForegroundColor Green
Write-Host ""
pause
