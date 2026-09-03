param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('x64')]
    [string]$Architecture = 'x64',

    [switch]$RunTests,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$nativeProject = Join-Path $projectRoot 'LivePhotoBox.Native\LivePhotoBox.Native.vcxproj'
$artifactDirectory = Join-Path $projectRoot "artifacts\native\$Configuration\win-$Architecture"

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

$syncScript = Join-Path $PSScriptRoot 'sync-native-project.ps1'
if (Test-Path $syncScript) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $syncScript -Quiet
}

$target = if ($Clean) { 'Rebuild' } else { 'Build' }

Write-Host "[Native] Building $Configuration $Architecture with Visual C++..." -ForegroundColor Cyan
# MSVC 14.51 intermittently crashes with C1001 in zmmintrin.h when this
# project is compiled through the parallel multi-tool pipeline. Keep the
# project build deterministic; source-level parallelism is still handled
# by MSBuild for other independent verification steps.
$msbuildArgs = @(
    $nativeProject,
    '/nologo',
    '/m:1',
    "/t:$target",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Architecture",
    '/p:CL_MPCount=1',
    '/p:UseMultiToolTask=false',
    '/v:minimal'
)
$buildSucceeded = $false
$buildExitCode = 1
for ($attempt = 1; $attempt -le 2; $attempt++) {
    # If the first incremental build fails, the retry must discard the project's
    # intermediate state. Reusing partially compiled objects could otherwise
    # produce a successful DLL that still contains stale code.
    $attemptArgs = @($msbuildArgs)
    if ($attempt -eq 2 -and $target -eq 'Build') {
        $attemptArgs = @(
            $nativeProject,
            '/nologo',
            '/m:1',
            '/t:Rebuild',
            "/p:Configuration=$Configuration",
            "/p:Platform=$Architecture",
            '/p:CL_MPCount=1',
            '/p:UseMultiToolTask=false',
            '/v:minimal'
        )
    }

    & $msbuild @attemptArgs
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -eq 0) {
        $buildSucceeded = $true
        break
    }
    if ($attempt -lt 2) {
        Write-Warning "Native MSBuild failed with exit code $buildExitCode; retrying once for transient compiler failures."
    }
}
if (-not $buildSucceeded) {
    throw "Native MSBuild failed with exit code $buildExitCode."
}

$nativeDll = Join-Path $artifactDirectory 'LivePhotoBox.Native.dll'
$nativePdb = Join-Path $artifactDirectory 'LivePhotoBox.Native.pdb'
if (-not (Test-Path -LiteralPath $nativeDll)) {
    throw "Native build completed without the expected DLL: $nativeDll"
}
if (-not (Test-Path -LiteralPath $nativePdb)) {
    throw "Native build completed without the expected PDB: $nativePdb"
}

if ($RunTests) {
    Write-Host '[Native] Running managed ABI/runtime smoke tests...' -ForegroundColor Cyan
    & dotnet test (Join-Path $projectRoot 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj') `
        -c $Configuration `
        -p:Platform=x64 `
        -p:SkipNativeBuild=true `
        --filter 'FullyQualifiedName~NativeRuntimeTests' `
        -nologo `
        -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Native runtime tests failed with exit code $LASTEXITCODE."
    }
}

Write-Host "[Native] Ready: $nativeDll" -ForegroundColor Green
