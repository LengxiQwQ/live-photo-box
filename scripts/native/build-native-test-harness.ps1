param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$nativeProject = Join-Path $projectRoot 'LivePhotoBox.Native\LivePhotoBox.Native.vcxproj'
$artifactDirectory = Join-Path $projectRoot 'artifacts\native\TestHarness\win-x64'
$intermediateDirectory = Join-Path $projectRoot 'artifacts\native\obj\TestHarness-x64-Release'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer (vswhere.exe) was not found.'
}

$instances = @(& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json)
if ($instances.Count -eq 0) {
    throw 'Visual Studio with the MSVC x64 build tools is required.'
}

$visualStudioPath = [string]$instances[0].installationPath
$msbuild = Join-Path $visualStudioPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild was not found in Visual Studio: $msbuild"
}

New-Item -ItemType Directory -Force -Path $artifactDirectory, $intermediateDirectory | Out-Null

Write-Host '[Native] Building the test-only raw-facts harness (never the production output)...' -ForegroundColor Cyan
$args = @(
    $nativeProject,
    '/nologo',
    '/m:1',
    '/t:Rebuild',
    "/p:Configuration=$Configuration",
    "/p:Platform=$Architecture",
    '/p:CL_MPCount=1',
    '/p:UseMultiToolTask=false',
    '/p:LPB_NATIVE_EXTRA_DEFINITIONS=LPB_NATIVE_TEST_HARNESS',
    "/p:OutDir=$artifactDirectory\",
    "/p:IntDir=$intermediateDirectory\",
    '/p:TargetName=LivePhotoBox.Native.TestHarness',
    '/v:minimal'
)

& $msbuild @args
if ($LASTEXITCODE -ne 0) {
    throw "Native test harness MSBuild failed with exit code $LASTEXITCODE."
}

$harnessDll = Join-Path $artifactDirectory 'LivePhotoBox.Native.TestHarness.dll'
if (-not (Test-Path -LiteralPath $harnessDll)) {
    throw "Native test harness build completed without the expected DLL: $harnessDll"
}

Write-Host "[Native] Test harness ready: $harnessDll" -ForegroundColor Green
