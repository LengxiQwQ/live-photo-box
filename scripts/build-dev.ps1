# LivePhotoBox Dev Build — 只编译
# 用法: powershell -ExecutionPolicy Bypass -File build-dev.ps1

$ErrorActionPreference = "Stop"
Set-Location "D:\Projects\live-photo-box"

[Console]::OutputEncoding = [Text.Encoding]::UTF8
chcp 65001 > $null

$manifest = Get-Content "Live Photo Box\Package.appxmanifest" -Raw -Encoding UTF8
$version = if ($manifest -match 'Identity.*Version\s*=\s*"([^"]+)"') { $Matches[1] } else { "0.0.0.0" }

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  LivePhotoBox Dev Build v$version" -ForegroundColor Cyan
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
    -o publish\portable_x64

# dotnet publish 的 exit code 可能因为 MSIX 符号转换警告而非零
# 实际编译产物不受影响，检查 exe 是否存在
if (-not (Test-Path "publish\portable_x64\Live Photo Box.exe")) {
    Write-Host "BUILD FAILED — exe not found" -ForegroundColor Red
    pause; exit 1
}

Write-Host ""
Write-Host "Output: publish\portable_x64\" -ForegroundColor Green
Write-Host "Run  : publish\portable_x64\Live Photo Box.exe" -ForegroundColor Green
Write-Host ""
pause
