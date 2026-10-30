param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64')][string]$Architecture = 'x64',
    [switch]$RunTests,
    [switch]$Clean,
    [switch]$TestHarness,
    [switch]$ValidateOnly,
    [switch]$CleanOnly,
    [string]$ArtifactDirectory = '',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,23}$')][string]$VideoSidecarBuildId = 'r4-hevc-v2',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$ExecutionRecordId = 'r4-native-hevc-v1'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-environment.ps1')
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

$variant = if ($TestHarness) { 'harness' } else { 'production' }
$workspaceRoot = Join-Path $projectRoot "artifacts\build\native\$Configuration-$variant"
$buildDir = Join-Path $workspaceRoot 'cmake'
$artifactRoot = Join-Path $projectRoot 'artifacts\native'
$artifactDir = if ($TestHarness) { Join-Path $artifactRoot "TestHarness\$Configuration\win-x64" } else { Join-Path $artifactRoot "$Configuration\win-x64" }
if ($ArtifactDirectory) {
    if (-not $ValidateOnly) { throw 'ArtifactDirectory is only supported for validating an explicitly prepared build.' }
    $artifactDir = [IO.Path]::GetFullPath($ArtifactDirectory)
}
. (Join-Path $PSScriptRoot 'native-build-state.ps1')
$toolchainIdentity = "$vsPath|$vsVersion|$toolchain|$((Get-FileHash -LiteralPath $cmake).Hash)"
New-Item -ItemType Directory -Force -Path $workspaceRoot | Out-Null
# One shared output/dependency graph per checkout; process death releases the lock.
$lockPath = Join-Path $artifactRoot 'native-build.lock'
New-Item -ItemType Directory -Force $artifactRoot | Out-Null
$lockStream = $null
$deadline = [DateTime]::UtcNow.AddMinutes(30)
while (-not $lockStream) {
    try { $lockStream = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch [IO.IOException] { if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for another Native build.' }; Start-Sleep -Milliseconds 250 }
}
try {
$fingerprint = Get-NativeBuildFingerprint $projectRoot $Configuration $variant $toolchainIdentity
if ($ValidateOnly) {
    if (-not (Test-NativeBuildReceipt $artifactDir $fingerprint $Configuration $variant)) { throw "Native build is missing, stale, or modified: $artifactDir. Build without SkipNativeBuild first." }
    Write-Host "[Native CMake] Verified current build: $artifactDir"
    return
}
$cachePath = Join-Path $buildDir 'CMakeCache.txt'
if (Test-Path -LiteralPath $cachePath) {
    $cacheText = Get-Content -LiteralPath $cachePath -Raw
    $expectedRoot = $projectRoot.Replace('\','/')
    $expectedBuild = $buildDir.Replace('\','/')
    if ($cacheText -notmatch ('(?im)^CMAKE_HOME_DIRECTORY:INTERNAL=' + [regex]::Escape($expectedRoot) + '\r?$') -or
        $cacheText -notmatch ('(?im)^CMAKE_CACHEFILE_DIR:INTERNAL=' + [regex]::Escape($expectedBuild) + '\r?$') -or
        $cacheText -notmatch ('(?im)^CMAKE_GENERATOR:INTERNAL=' + [regex]::Escape($generator) + '\r?$')) {
        $resolvedBuild = [IO.Path]::GetFullPath($buildDir)
        if (-not $resolvedBuild.StartsWith([IO.Path]::GetFullPath($workspaceRoot) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stale-cache relocation target.' }
        $stale = "$buildDir-stale-1"; $counter = 1
        while (Test-Path -LiteralPath $stale) { $counter++; $stale = "$buildDir-stale-$counter" }
        Move-Item -LiteralPath $resolvedBuild -Destination $stale
    }
}
if ($CleanOnly) {
    if (Test-Path -LiteralPath (Join-Path $buildDir 'CMakeCache.txt')) {
        & $cmake --build $buildDir --config $Configuration --target clean
        if ($LASTEXITCODE -ne 0) { throw 'Native clean failed.' }
    }
    $receiptPath = Join-Path $artifactDir 'native-build.json'
    if (Test-Path -LiteralPath $receiptPath) { Remove-Item -LiteralPath $receiptPath }
    return
}
# Reuse only an exact source/toolchain/configuration and artifact identity.
$alreadyCurrent = -not $Clean -and (Test-NativeBuildReceipt $artifactDir $fingerprint $Configuration $variant)
if (-not $alreadyCurrent) {
$sidecarBuildEnabled = -not $TestHarness
$ffmpegPrefix = ''
$codecPrefix = ''
$dumpbinPath = ''
if ($sidecarBuildEnabled) {
    $prerequisiteScript = Join-Path $PSScriptRoot 'restore-video-dependencies.ps1'
    & $prerequisiteScript -BuildId $VideoSidecarBuildId
    if (-not $?) { throw 'Pinned P5-R5 video sidecar prerequisite build failed.' }
    $prerequisiteRecord = Join-Path $projectRoot "artifacts\deps\video\$VideoSidecarBuildId-libav-prerequisites.json"
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
$attempt = 1
while (Test-Path -LiteralPath $buildLog) { $buildLog = Join-Path $workspaceRoot "$ExecutionRecordId-native-$Configuration-attempt-$attempt.log"; $attempt++ }
$configureArgs = @(
    '-S', $projectRoot, '-B', $buildDir, '-G', $generator, '-A', 'x64',
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
    '-DVCPKG_TARGET_TRIPLET=x64-windows-static',
    "-DLPB_NATIVE_OUTPUT_ROOT=$artifactRoot",
    "-DLPB_BUILD_TEST_HARNESS=$($TestHarness.ToString().ToUpperInvariant())",
    '-DBUILD_TESTING=OFF',
    "-DVCPKG_INSTALLED_DIR=$(Join-Path $projectRoot 'artifacts\deps\images')",
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

$required = @($dllName, [IO.Path]::GetFileName($pdb))
if ((Get-NativeBuildFingerprint $projectRoot $Configuration $variant $toolchainIdentity) -ne $fingerprint) {
    throw 'Build inputs changed while compiling; no current-build receipt will be issued. Build again after edits stop.'
}
if ($sidecarBuildEnabled) { $required += @('LivePhotoBox.Video.Libav.dll','LivePhotoBox.Video.Libav.pdb') }
$record = [ordered]@{ schemaVersion=1; fingerprint=$fingerprint; configuration=$Configuration; variant=$variant;
    toolchain=$toolchainIdentity; files=@($required | ForEach-Object { @{name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $artifactDir $_)).Hash} }) }
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactDir 'native-build.json') -Encoding UTF8
} else { Write-Host "[Native CMake] Up to date: $artifactDir" }
} finally { $lockStream.Dispose() }
if ($RunTests -and -not $TestHarness) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-native.ps1') -Configuration $Configuration -TestHarness
    if ($LASTEXITCODE -ne 0) { throw 'Native test harness build failed.' }
    & dotnet test (Join-Path $projectRoot 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj') -c $Configuration -p:Platform=x64 -p:SkipNativeBuild=true --filter 'FullyQualifiedName~NativeRuntimeTests' -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native ABI/runtime smoke tests failed.' }
}
Write-Host "[Native CMake] Ready: $artifactDir" -ForegroundColor Green
