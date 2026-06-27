$ErrorActionPreference = "Stop"
Set-Location "D:\Projects\live-photo-box"

[Console]::OutputEncoding = [Text.Encoding]::UTF8
chcp 65001 > $null

$manifest = Get-Content "LivePhotoBox\Package.appxmanifest" -Raw -Encoding UTF8
$version = if ($manifest -match 'Identity.*Version\s*=\s*"([^"]+)"') { $Matches[1] } else { "0.0.0.0" }

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  LivePhotoBox Release Build v$version" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

if (Test-Path publish) { Remove-Item -Recurse -Force publish }
New-Item -ItemType Directory publish | Out-Null

Write-Host "[1/3] Building Release x64 (SelfContained)..." -ForegroundColor Yellow

dotnet publish LivePhotoBox\LivePhotoBox.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:WindowsAppSDKSelfContained=true `
    -o publish\portable_x64

if (-not (Test-Path "publish\portable_x64\Live Photo Box.exe")) {
    Write-Host "BUILD FAILED - exe not found" -ForegroundColor Red
    pause
    exit 1
}

Write-Host ""
Write-Host "[2/3] Creating portable zip..." -ForegroundColor Yellow

$zipName = "LivePhotoBox_v$($version)_portable_x64.zip"
$zipPath = "publish\$zipName"
Compress-Archive -Path "publish\portable_x64\*" -DestinationPath $zipPath -Force
$zipSize = "{0:N1} MB" -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "       $zipName  ($zipSize)" -ForegroundColor Green

Write-Host ""
Write-Host "[3/3] Creating installer..." -ForegroundColor Yellow

$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $iscc) {
    & $iscc /Qp /dVERSION=$version "scripts\setup.iss"
    if ($LASTEXITCODE -eq 0) {
        $setupName = "LivePhotoBox_Setup_v$($version)_x64.exe"
        $setupPath = "publish\$setupName"
        $setupSize = "{0:N1} MB" -f ((Get-Item $setupPath).Length / 1MB)
        Write-Host "       $setupName  ($setupSize)" -ForegroundColor Green
    }
    else {
        Write-Host "       Inno Setup failed" -ForegroundColor Red
    }
}
else {
    Write-Host "       Inno Setup not installed, skipping" -ForegroundColor DarkYellow
}

Remove-Item -Recurse -Force publish\portable_x64 -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Build Complete!" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Get-ChildItem publish | ForEach-Object {
    $s = "{0:N1} MB" -f ($_.Length / 1MB)
    Write-Host "  $($_.Name)  ($s)" -ForegroundColor White
}
Write-Host ""
Write-Host "Upload to: https://github.com/LengxiQwQ/live-photo-box/releases" -ForegroundColor White
Write-Host ""
pause
