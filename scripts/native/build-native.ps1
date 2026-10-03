param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64')][string]$Architecture = 'x64',
    [switch]$RunTests,
    [switch]$Clean,
    [switch]$TestHarness,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$VideoSidecarBuildId = 'r5-main10-v1',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$ExecutionRecordId = 'r5-native-main10-v1'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$env:VSLANG = '1033'
$script:NativeLoggedExitCode = -1
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
function Invoke-NativeCommandLogged([string]$Executable, [string[]]$Arguments, [string]$LogPath) {
    & $Executable @Arguments 2>&1 | ForEach-Object {
        $line = [string]$_
        [System.IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine, $utf8NoBom)
        [Console]::Out.WriteLine($line)
    }
    $script:NativeLoggedExitCode = $LASTEXITCODE
}
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

$workspaceRoot = Join-Path $projectRoot '.ai-tmp\workspace\P5-R5'
$buildDir = Join-Path $workspaceRoot 'cmake-build'
$artifactRoot = Join-Path $projectRoot 'artifacts\native'
$defaultExecutionRecordLog = Join-Path $workspaceRoot "$ExecutionRecordId-native-$Configuration-build.log"
if (-not $PSBoundParameters.ContainsKey('ExecutionRecordId') -and
    (Test-Path -LiteralPath $defaultExecutionRecordLog -PathType Leaf) -and
    $ExecutionRecordId -match '^(?<prefix>.+)-v(?<version>[0-9]+)$') {
    $recordPrefix = $Matches.prefix
    $recordVersion = [int]$Matches.version
    do {
        $recordVersion++
        $candidateRecordId = "$recordPrefix-v$recordVersion"
        $candidateLog = Join-Path $workspaceRoot "$candidateRecordId-native-$Configuration-build.log"
    } while (Test-Path -LiteralPath $candidateLog -PathType Leaf)
    $ExecutionRecordId = $candidateRecordId
}
$sidecarBuildEnabled = $Configuration -eq 'Release' -and -not $TestHarness
$ffmpegPrefix = ''
$codecPrefix = ''
$dumpbinPath = ''
if ($sidecarBuildEnabled) {
    $prerequisiteScript = Join-Path $PSScriptRoot 'build-p5-r5-video-sidecar-prerequisites.ps1'
    & $prerequisiteScript -BuildId $VideoSidecarBuildId
    if (-not $?) { throw 'Pinned P5-R5 video sidecar prerequisite build failed.' }
    $prerequisiteRecord = Join-Path $workspaceRoot "$VideoSidecarBuildId-libav-prerequisites.json"
    if (-not (Test-Path -LiteralPath $prerequisiteRecord -PathType Leaf)) {
        throw "Pinned P5-R5 prerequisite record was not written: $prerequisiteRecord"
    }
    $prerequisite = Get-Content -LiteralPath $prerequisiteRecord -Raw | ConvertFrom-Json
    $ffmpegPrefix = [string]$prerequisite.ffmpegPrefix
    $codecPrefix = [string]$prerequisite.codecPrefix
    $msvcRoot = Join-Path $vsPath 'VC\Tools\MSVC'
    $msvcVersion = Get-ChildItem -LiteralPath $msvcRoot -Directory |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $msvcVersion) { throw "The installed MSVC x64 toolset was not found: $msvcRoot" }
    $dumpbinPath = Join-Path $msvcVersion.FullName 'bin\Hostx64\x64\dumpbin.exe'
    if (-not (Test-Path -LiteralPath $dumpbinPath -PathType Leaf)) {
        throw "The exact MSVC PE import inspector was not found: $dumpbinPath"
    }
}
New-Item -ItemType Directory -Force -Path $workspaceRoot | Out-Null
$buildLog = Join-Path $workspaceRoot "$ExecutionRecordId-native-$Configuration-build.log"
if (Test-Path -LiteralPath $buildLog) { throw "Create-only native build log already exists: $buildLog" }
$configureArgs = @(
    '-S', $projectRoot, '-B', $buildDir, '-G', $generator, '-A', 'x64',
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
    '-DVCPKG_TARGET_TRIPLET=x64-windows-static',
    "-DLPB_NATIVE_OUTPUT_ROOT=$artifactRoot",
    '-DLPB_BUILD_TEST_HARNESS=ON',
    "-DLPB_BUILD_VIDEO_SIDECAR=$($sidecarBuildEnabled.ToString().ToUpperInvariant())",
    "-DLPB_P5_R5_FFMPEG_PREFIX=$ffmpegPrefix",
    "-DLPB_P5_R5_CODEC_PREFIX=$codecPrefix",
    "-DLPB_P5_R5_DUMPBIN_EXE=$dumpbinPath")
[System.IO.File]::WriteAllText($buildLog, "ARGV: $cmake $($configureArgs -join ' ')`r`n", $utf8NoBom)
$priorErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
Invoke-NativeCommandLogged $cmake $configureArgs $buildLog
$configureExitCode = $script:NativeLoggedExitCode
$ErrorActionPreference = $priorErrorActionPreference
if ($configureExitCode -ne 0) { throw "CMake/vcpkg configure failed ($configureExitCode); see $buildLog" }

$target = if ($TestHarness) { 'LivePhotoBoxNativeTestHarness' } else { 'LivePhotoBoxNative' }
$buildArgs = @('--build', $buildDir, '--config', $Configuration, '--target', $target)
if ($Clean) { $buildArgs += '--clean-first' }
$buildArgs += @('--', '/m:1', '/p:CL_MPCount=1', '/p:UseMultiToolTask=false', '/v:minimal')
[System.IO.File]::AppendAllText($buildLog, "ARGV: $cmake $($buildArgs -join ' ')`r`n", $utf8NoBom)
$priorErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
Invoke-NativeCommandLogged $cmake $buildArgs $buildLog
$buildExitCode = $script:NativeLoggedExitCode
$ErrorActionPreference = $priorErrorActionPreference
if ($buildExitCode -ne 0) { throw "CMake $target build failed ($buildExitCode); see $buildLog" }

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
if ($sidecarBuildEnabled) {
    $sidecarDll = Join-Path $artifactDir 'LivePhotoBox.Video.Libav.dll'
    if (-not (Test-Path -LiteralPath $sidecarDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath ([System.IO.Path]::ChangeExtension($sidecarDll, '.pdb')) -PathType Leaf)) {
        throw "CMake build lacks the expected pinned sidecar DLL/PDB: $sidecarDll"
    }
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
