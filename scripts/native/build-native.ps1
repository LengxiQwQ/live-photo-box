param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64')][string]$Architecture = 'x64',
    [switch]$RunTests,
    [switch]$Clean,
    [switch]$TestHarness
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer (vswhere.exe) was not found.' }
$vsPath = [string](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
$vsVersion = [string](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion | Select-Object -First 1)
if (-not $vsPath -or -not $vsVersion) { throw 'Visual Studio MSVC x64 build tools are required.' }
$cmake = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path -LiteralPath $cmake)) { throw "Visual Studio CMake was not found: $cmake" }
$generator = switch ($vsVersion.Split('.')[0]) {
    '18' { 'Visual Studio 18 2026' }
    '17' { 'Visual Studio 17 2022' }
    default { throw "Unsupported Visual Studio generator version: $vsVersion" }
}
$vcpkgRoot = if ($env:VCPKG_ROOT) { $env:VCPKG_ROOT } else { Join-Path $vsPath 'VC\vcpkg' }
$toolchain = Join-Path $vcpkgRoot 'scripts\buildsystems\vcpkg.cmake'
if (-not (Test-Path -LiteralPath $toolchain)) { throw "vcpkg toolchain was not found: $toolchain" }

$buildDir = Join-Path $projectRoot '.ai-tmp\workspace\P4-R3\cmake-build'
$artifactRoot = Join-Path $projectRoot 'artifacts\native'
& $cmake -S $projectRoot -B $buildDir -G $generator -A x64 `
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
    '-DVCPKG_TARGET_TRIPLET=x64-windows-static' `
    "-DLPB_NATIVE_OUTPUT_ROOT=$artifactRoot" `
    '-DLPB_BUILD_TEST_HARNESS=ON'
if ($LASTEXITCODE -ne 0) { throw "CMake/vcpkg configure failed ($LASTEXITCODE)." }

$target = if ($TestHarness) { 'LivePhotoBoxNativeTestHarness' } else { 'LivePhotoBoxNative' }
$buildArgs = @('--build', $buildDir, '--config', $Configuration, '--target', $target)
if ($Clean) { $buildArgs += '--clean-first' }
$buildArgs += @('--', '/m:1', '/p:CL_MPCount=1', '/p:UseMultiToolTask=false', '/v:minimal')
& $cmake @buildArgs
if ($LASTEXITCODE -ne 0) { throw "CMake $target build failed ($LASTEXITCODE)." }

$artifactDir = if ($TestHarness) {
    Join-Path $artifactRoot "TestHarness\$Configuration\win-x64"
} else {
    Join-Path $artifactRoot "$Configuration\win-x64"
}
$dllName = if ($TestHarness) { 'LivePhotoBox.Native.TestHarness.dll' } else { 'LivePhotoBox.Native.dll' }
$dll = Join-Path $artifactDir $dllName
$pdb = [System.IO.Path]::ChangeExtension($dll, '.pdb')
if (-not (Test-Path -LiteralPath $dll) -or -not (Test-Path -LiteralPath $pdb)) {
    throw "CMake build lacks the expected DLL/PDB: $dll"
}

if ($RunTests -and -not $TestHarness) {
    $portableArgs = @('--build', $buildDir, '--config', $Configuration, '--target', 'lpb_portable_io_smoke', '--', '/m:1', '/p:CL_MPCount=1', '/p:UseMultiToolTask=false', '/v:minimal')
    & $cmake @portableArgs
    if ($LASTEXITCODE -ne 0) { throw 'Portable-core smoke target build failed.' }
    $ctest = Join-Path (Split-Path -Parent $cmake) 'ctest.exe'
    & $ctest --test-dir $buildDir -C $Configuration --output-on-failure
    if ($LASTEXITCODE -ne 0) { throw 'Portable-core smoke test failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-native-test-harness.ps1') `
        -Configuration $Configuration -Architecture x64
    if ($LASTEXITCODE -ne 0) { throw 'CMake test harness build failed.' }
    & dotnet test (Join-Path $projectRoot 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj') `
        -c $Configuration -p:Platform=x64 -p:SkipNativeBuild=true `
        --filter 'FullyQualifiedName~NativeRuntimeTests' -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native ABI/runtime smoke tests failed.' }
}
Write-Host "[Native CMake] Ready: $dll" -ForegroundColor Green
