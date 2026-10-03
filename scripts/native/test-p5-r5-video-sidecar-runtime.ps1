param(
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$BuildId = 'r5-v3',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$RecordId = 'r5-v3-runtime',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$OutputSet = 'iteration-6-runtime-1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workspace = Join-Path $projectRoot '.ai-tmp\workspace\P5-R5'
$buildDir = Join-Path $workspace 'cmake-build'
$artifactDir = Join-Path $projectRoot "artifacts\native\$Configuration\win-x64"
$logPath = Join-Path $workspace "$RecordId-smoke.log"
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = [string](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if (-not $vsPath) { throw 'Visual Studio MSVC x64 build tools are required for the R5 runtime smoke.' }
$cmake = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ctest = Join-Path (Split-Path -Parent $cmake) 'ctest.exe'
foreach ($required in @($cmake, $ctest, (Join-Path $artifactDir 'LivePhotoBox.Native.dll'),
        (Join-Path $artifactDir 'LivePhotoBox.Video.Libav.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Release sidecar smoke prerequisite is missing: $required" }
}
if (Test-Path -LiteralPath $logPath) { throw "Create-only runtime smoke log already exists: $logPath" }
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($logPath, "R5_BUILD_ID=$BuildId`r`n", $utf8NoBom)

function Invoke-NativeLogged([string]$Executable, [string[]]$Arguments) {
    $script:NativeSmokeExitCode = -1
    & $Executable @Arguments 2>&1 | ForEach-Object {
        $line = [string]$_
        [System.IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8NoBom)
        [Console]::Out.WriteLine($line)
    }
    $script:NativeSmokeExitCode = $LASTEXITCODE
}

$smokeBuildArgs = @('--build', $buildDir, '--config', $Configuration, '--target', 'lpb_video_sidecar_runtime_smoke',
    '--', '/m:1', '/p:CL_MPCount=1', '/p:UseMultiToolTask=false', '/v:minimal')
[System.IO.File]::AppendAllText($logPath, "ARGV: $cmake $($smokeBuildArgs -join ' ')`r`n", $utf8NoBom)
$priorErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
Invoke-NativeLogged $cmake $smokeBuildArgs
$buildExitCode = $script:NativeSmokeExitCode
$ErrorActionPreference = $priorErrorActionPreference
[System.IO.File]::AppendAllText($logPath, "SMOKE_BUILD_EXIT_CODE=$buildExitCode`r`n", $utf8NoBom)
if ($buildExitCode -ne 0) { throw "R5 runtime smoke target build failed ($buildExitCode); see $logPath" }

$ctestArgs = @('--test-dir', $buildDir, '-C', $Configuration, '--output-on-failure',
    '-R', '^P5R5_VideoSidecarRuntimeSiblingPositive$')
[System.IO.File]::AppendAllText($logPath, "ARGV: $ctest $($ctestArgs -join ' ')`r`n", $utf8NoBom)
$ErrorActionPreference = 'Continue'
Invoke-NativeLogged $ctest $ctestArgs
$positiveExitCode = $script:NativeSmokeExitCode
$ErrorActionPreference = $priorErrorActionPreference
[System.IO.File]::AppendAllText($logPath, "POSITIVE_CTEST_EXIT_CODE=$positiveExitCode`r`n", $utf8NoBom)
if ($positiveExitCode -ne 0) { throw "Exact-sibling positive runtime smoke failed ($positiveExitCode); see $logPath" }

$negativeScript = Join-Path $PSScriptRoot 'test-p5-r5-missing-sidecar.ps1'
[System.IO.File]::AppendAllText($logPath, "ARGV: powershell.exe -NoProfile -ExecutionPolicy Bypass -File $negativeScript -Configuration $Configuration -RecordId $RecordId -OutputSet $OutputSet`r`n", $utf8NoBom)
$ErrorActionPreference = 'Continue'
& $negativeScript -Configuration $Configuration -RecordId $RecordId -OutputSet $OutputSet 2>&1 | ForEach-Object {
    $line = [string]$_
    [System.IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8NoBom)
    [Console]::Out.WriteLine($line)
}
$negativeExitCode = if ($?) { 0 } else { 1 }
$ErrorActionPreference = $priorErrorActionPreference
[System.IO.File]::AppendAllText($logPath, "NEGATIVE_SCRIPT_EXIT_CODE=$negativeExitCode`r`n", $utf8NoBom)
if ($negativeExitCode -ne 0) { throw "Real missing-sidecar runtime smoke failed ($negativeExitCode); see $logPath" }
Write-Output "R5 runtime sibling positive and missing-sidecar negative smokes passed. Log: $logPath"
