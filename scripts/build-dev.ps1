# LivePhotoBox Dev Build — 编译并发布（未打包）
# 用法: powershell -ExecutionPolicy Bypass -File build-dev.ps1

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
    pause
    exit 1
}

Write-Host '       Build OK' -ForegroundColor Green

Write-Host 'Cleaning locale folders...' -ForegroundColor Yellow

# 从 csproj 读取要保留的原生语言列表（单一真相源）
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
Write-Host "Output: $outDir" -ForegroundColor Green
Write-Host "Run  : $outDir\Live Photo Box.exe" -ForegroundColor Green
Write-Host ''
pause
