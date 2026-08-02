# LivePhotoBox Release Build — 编译 + 打包 zip + Inno Setup 安装包
# 用法: powershell -ExecutionPolicy Bypass -File build-release.ps1

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

Write-Host '[1/4] Building Release x64 (SelfContained)...' -ForegroundColor Yellow

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

Write-Host ""
Write-Host "[2/3] Cleaning unnecessary files..." -ForegroundColor Yellow

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
Write-Host "       Removed $count locale folders (kept zh-CN, en-us)" -ForegroundColor Gray

# 2. 删除运行时生成的配置文件（开发机残留）
$appSettings = Join-Path $outDir 'appsettings.json'
if (Test-Path $appSettings) { Remove-Item -Force $appSettings; Write-Host '       Removed appsettings.json' -ForegroundColor Gray }

# 3. 删除 AI/ML 无用文件
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

# 5. 删除调试符号 (发布版不需要)
Remove-Item -Force "$outDir\Live Photo Box.pdb" -ErrorAction SilentlyContinue

# 6. 删除 XML 文档
Remove-Item -Force "$outDir\*.xml" -ErrorAction SilentlyContinue

$kb = (Get-ChildItem $outDir -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1KB
Write-Host "       Final size: $('{0:N0}' -f $kb) KB" -ForegroundColor Green

Write-Host '       Build OK' -ForegroundColor Green

# 从 csproj 读取要保留的原生语言列表（单一真相源）
[xml]$csprojXml = Get-Content 'LivePhotoBox\LivePhotoBox.csproj'
$keepLocales = ($csprojXml.Project.PropertyGroup.AppSupportedNativeLocales | Where-Object { $_ }) -split ';'

Write-Host '[2/4] Cleaning locale folders...' -ForegroundColor Yellow
$removed = 0
Get-ChildItem -Path $outDir -Recurse -Filter '*.mui' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Directory.Name -notin $keepLocales) {
        Remove-Item -Recurse -Force $_.Directory.FullName -ErrorAction SilentlyContinue
        $removed++
    }
}
Write-Host "       Removed $removed locale folders (kept $($keepLocales -join ', '))" -ForegroundColor Gray

Write-Host ''
Write-Host '[3/4] Creating portable zip...' -ForegroundColor Yellow

$zipName = "Live-Photo-Box-v$version-x64-portable.zip"
$zipPath = "publish\$zipName"
Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath -Force
$zipSize = '{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "       $zipName  ($zipSize)" -ForegroundColor Green

Write-Host ''
Write-Host '[4/4] Creating installer...' -ForegroundColor Yellow

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
pause
