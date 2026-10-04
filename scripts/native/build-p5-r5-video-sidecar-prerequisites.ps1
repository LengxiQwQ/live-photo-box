[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$BuildId = 'r5-v3',
    [ValidateRange(1, 64)][int]$Jobs = [Math]::Min([Environment]::ProcessorCount, 8)
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$env:VSLANG = '1033'

function Get-LocalFileHash {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][ValidateSet('SHA256', 'SHA512')][string]$Algorithm
    )

    $hashAlgorithm = if ($Algorithm -eq 'SHA512') {
        [System.Security.Cryptography.SHA512]::Create()
    }
    else {
        [System.Security.Cryptography.SHA256]::Create()
    }
    $stream = [System.IO.File]::Open(
        $LiteralPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $digest = $hashAlgorithm.ComputeHash($stream)
        return ([System.BitConverter]::ToString($digest)).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $hashAlgorithm.Dispose()
    }
}

function Ensure-ExactVcpkgDownload {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DownloadsRoot,
        [Parameter(Mandatory)][string]$ArchiveName,
        [Parameter(Mandatory)][string]$ExpectedSha512
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "Pinned x264 source archive is missing: $SourcePath"
    }
    $sourceSha512 = Get-LocalFileHash -LiteralPath $SourcePath -Algorithm SHA512
    if ($sourceSha512 -ne $ExpectedSha512) {
        throw "Pinned x264 source archive SHA-512 mismatch: $sourceSha512"
    }

    [System.IO.Directory]::CreateDirectory($DownloadsRoot) | Out-Null
    $destinationPath = Join-Path $DownloadsRoot $ArchiveName
    if (Test-Path -LiteralPath $destinationPath) {
        if (-not (Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
            throw "Existing vcpkg x264 download-cache entry is not a file: $destinationPath"
        }
        $existingSha512 = Get-LocalFileHash -LiteralPath $destinationPath -Algorithm SHA512
        if ($existingSha512 -ne $ExpectedSha512) {
            throw "Existing vcpkg x264 download-cache SHA-512 mismatch: $existingSha512"
        }
        Write-Output "VCPKG_X264_ARCHIVE_SOURCE=$destinationPath; SHA512=$existingSha512; state=verified-existing"
        return
    }

    $sourceStream = [System.IO.File]::Open(
        $SourcePath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $destinationStream = [System.IO.File]::Open(
            $destinationPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $sourceStream.CopyTo($destinationStream)
            $destinationStream.Flush($true)
        }
        finally {
            $destinationStream.Dispose()
        }
    }
    finally {
        $sourceStream.Dispose()
    }

    $seededSha512 = Get-LocalFileHash -LiteralPath $destinationPath -Algorithm SHA512
    if ($seededSha512 -ne $ExpectedSha512) {
        throw "Seeded vcpkg x264 download-cache SHA-512 mismatch: $seededSha512"
    }
    Write-Output "VCPKG_X264_ARCHIVE_SOURCE=$destinationPath; SHA512=$seededSha512; state=seeded-create-only"
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workspaceRoot = Join-Path $projectRoot '.ai-tmp\workspace\P5-R5'
$buildInfoPath = Join-Path $projectRoot 'tools\p5-r5-video-validation\profiles\P5-R5-v1\backend-build.json'
$dependencyManifest = Join-Path $projectRoot 'tools\p5-r5-video-validation\sidecar-dependencies\vcpkg.json'
$sourceArchive = Join-Path $projectRoot 'tools\p5-r5-video-validation\sources\ffmpeg-n9.0.1.tar.gz'
$x264SourceArchive = Join-Path $projectRoot 'tools\p5-r5-video-validation\sources\x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55.tar.gz'
$x264ArchiveName = 'videolan-x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55.tar.gz'
$x264ArchiveSha512 = 'BFAC118DA55DA2FCC4587B17A3184D9ED70D6E03188BC0497F5922DF22B5685FA49FA4325F9C5196C76D03E6E3F56D5906475C540D0DF7E1739DC4182816EEBE'
$downloadsRoot = if ($env:VCPKG_DOWNLOADS) { $env:VCPKG_DOWNLOADS } else { Join-Path $env:LOCALAPPDATA 'vcpkg\downloads' }
$downloadsRoot = [System.IO.Path]::GetFullPath($downloadsRoot)
$env:VCPKG_DOWNLOADS = $downloadsRoot
$buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
$dependencyInfo = Get-Content -LiteralPath $dependencyManifest -Raw | ConvertFrom-Json
$x265Profile = $buildInfo.x265Main10
if ($buildInfo.source.version -ne '9.0.1' -or
    $buildInfo.source.archiveSha256 -ne '195D54BEBE1A27F84D77F4B989D193466F305B355DA92292766A69F16880B18A' -or
    $dependencyInfo.'builtin-baseline' -ne $buildInfo.source.vcpkgBaseline -or
    -not $x265Profile -or
    $x265Profile.sourceRepository -ne 'Multicorewareinc/x265' -or
    $x265Profile.sourceRef -ne '4.3' -or
    $x265Profile.sourceArchiveSha512 -ne '270D0DB180ECEBFC5C2F1FE6451BE66042FAB69273D073428F4AF00420FA0F8B792ECDC71EC1A15EE2860E3B6A325C56295143F117E94FA3616F0CCECB2DED75' -or
    $x265Profile.vcpkgBaselinePortTree -ne '40433737B9204526EDC35AE39D337131B3180251' -or
    $x265Profile.expectedDepth -ne 10 -or
    @($x265Profile.configureOptions).Count -ne 2 -or
    $x265Profile.configureOptions[0] -cne '-DHIGH_BIT_DEPTH=ON' -or
    $x265Profile.configureOptions[1] -cne '-DMAIN12=OFF') {
    throw 'The versioned P5-R5 FFmpeg/x265 Main10 or vcpkg pins do not match the profile.'
}
if (-not (Test-Path -LiteralPath $sourceArchive -PathType Leaf)) {
    throw "Pinned FFmpeg source archive is missing: $sourceArchive"
}
$archiveSha256 = Get-LocalFileHash -LiteralPath $sourceArchive -Algorithm SHA256
$archiveSha512 = Get-LocalFileHash -LiteralPath $sourceArchive -Algorithm SHA512
if ($archiveSha256 -ne $buildInfo.source.archiveSha256 -or
    $archiveSha512 -ne $buildInfo.source.vcpkgPortArchiveSha512) {
    throw "Pinned FFmpeg archive hash mismatch (SHA-256=$archiveSha256; SHA-512=$archiveSha512)."
}

$x265OverlayRoot = Join-Path $projectRoot $x265Profile.overlayPortPath
$x265OverlayFiles = @($x265Profile.overlayFiles)
if (-not (Test-Path -LiteralPath $x265OverlayRoot -PathType Container) -or $x265OverlayFiles.Count -eq 0) {
    throw "Pinned x265 Main10 overlay port is missing or has no file identities: $x265OverlayRoot"
}
$actualOverlayFiles = @(Get-ChildItem -LiteralPath $x265OverlayRoot -File)
if ($actualOverlayFiles.Count -ne $x265OverlayFiles.Count) {
    throw "Pinned x265 Main10 overlay file count differs from backend-build.json: $x265OverlayRoot"
}
foreach ($expectedFile in $x265OverlayFiles) {
    $overlayFile = Join-Path $x265OverlayRoot $expectedFile.path
    if (-not (Test-Path -LiteralPath $overlayFile -PathType Leaf) -or
        (Get-LocalFileHash -LiteralPath $overlayFile -Algorithm SHA256) -ne $expectedFile.sha256) {
        throw "Pinned x265 Main10 overlay file hash mismatch: $overlayFile"
    }
}
$overlayIdentityText = @($x265OverlayFiles | Sort-Object path | ForEach-Object { "$($_.path)=$($_.sha256)" }) -join "`n"
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $x265OverlaySha256 = ([System.BitConverter]::ToString($sha256.ComputeHash([System.Text.UTF8Encoding]::new($false).GetBytes($overlayIdentityText)))).Replace('-', '')
}
finally {
    $sha256.Dispose()
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer (vswhere.exe) was not found.' }
$vsPath = [string](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if (-not $vsPath) { throw 'Visual Studio MSVC x64 build tools are required.' }
$vsDevCmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
$vcpkgRoot = if ($env:VCPKG_ROOT) { $env:VCPKG_ROOT } else { Join-Path $vsPath 'VC\vcpkg' }
$vcpkg = Join-Path $vcpkgRoot 'vcpkg.exe'
if (-not (Test-Path -LiteralPath $vcpkg -PathType Leaf)) { throw "vcpkg.exe was not found under the configured root: $vcpkgRoot" }
if (-not (Test-Path -LiteralPath $vsDevCmd -PathType Leaf)) { throw "VsDevCmd.bat was not found: $vsDevCmd" }

$vcpkgInstallRoot = Join-Path $workspaceRoot 'video-codecs-main10'
$codecIdentityPath = Join-Path $workspaceRoot "$BuildId-codec-identity.json"
$codecPrefixCandidates = @(
    (Join-Path $vcpkgInstallRoot 'x64-windows-static'),
    $vcpkgInstallRoot)
$codecPrefix = $codecPrefixCandidates | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_ 'lib\libx264.lib') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $_ 'lib\x265-static.lib') -PathType Leaf)
} | Select-Object -First 1

if (-not $codecPrefix -or -not (Test-Path -LiteralPath $codecIdentityPath -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path $workspaceRoot | Out-Null
    Ensure-ExactVcpkgDownload -SourcePath $x264SourceArchive -DownloadsRoot $downloadsRoot `
        -ArchiveName $x264ArchiveName -ExpectedSha512 $x264ArchiveSha512
    $installLogBase = Join-Path $workspaceRoot "$BuildId-vcpkg-codecs-install"
    $attempt = 1
    while (Test-Path -LiteralPath "$installLogBase-$attempt.log") { $attempt++ }
    $installLog = "$installLogBase-$attempt.log"
    $manifestRoot = Split-Path -Parent $dependencyManifest
    Write-Output "ARGV: $vcpkg install --triplet=x64-windows-static --host-triplet=x64-windows --x-install-root=$vcpkgInstallRoot --overlay-ports=$x265OverlayRoot; cwd=$manifestRoot"
    Push-Location -LiteralPath $manifestRoot
    try {
        & $vcpkg install '--triplet=x64-windows-static' '--host-triplet=x64-windows' "--x-install-root=$vcpkgInstallRoot" "--overlay-ports=$x265OverlayRoot" 2>&1 |
            Tee-Object -LiteralPath $installLog
        $vcpkgExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    Write-Output "VCPKG_EXIT_CODE=$vcpkgExitCode"
    if ($vcpkgExitCode -ne 0) { throw "Pinned x264/x265 vcpkg install failed ($vcpkgExitCode); see $installLog" }
    $codecPrefix = $codecPrefixCandidates | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_ 'lib\libx264.lib') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_ 'lib\x265-static.lib') -PathType Leaf)
    } | Select-Object -First 1
}
if (-not $codecPrefix) { throw "Pinned x264/x265 archives were not installed below $vcpkgInstallRoot." }

$statusPath = Join-Path $vcpkgInstallRoot 'vcpkg\status'
if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { $statusPath = Join-Path $vcpkgInstallRoot 'status' }
if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
    throw "vcpkg installed-package status is missing under the exact codec prefix: $codecPrefix"
}
$statusText = Get-Content -LiteralPath $statusPath -Raw
foreach ($expectedPackage in @(@{ Name = 'x264'; Version = '0.165.3222' }, @{ Name = 'x265'; Version = '4.3' })) {
    $block = [regex]::Matches($statusText, '(?ms)^Package:\s*([^\r\n]+).*?(?=^Package:|\z)') |
        Where-Object { $_.Groups[1].Value.Trim() -eq $expectedPackage.Name } |
        Select-Object -First 1
    if (-not $block -or $block.Value -notmatch "(?m)^Version:\s*$([regex]::Escape($expectedPackage.Version))(?:#0)?\s*$" -or
        $block.Value -notmatch '(?m)^Architecture:\s*x64-windows-static\s*$') {
        throw "The installed $($expectedPackage.Name) package does not match pinned $($expectedPackage.Version) / x64-windows-static."
    }
}
foreach ($relative in @(
        'lib\pkgconfig\x264.pc', 'lib\pkgconfig\x265.pc',
        'share\x264\copyright', 'share\x265\copyright')) {
    if (-not (Test-Path -LiteralPath (Join-Path $codecPrefix $relative) -PathType Leaf)) {
        throw "Pinned static codec prerequisite is missing: $(Join-Path $codecPrefix $relative)"
    }
}

$codecArchiveRecords = foreach ($archiveName in @('libx264.lib', 'x265-static.lib')) {
    $archivePath = Join-Path $codecPrefix "lib\$archiveName"
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw "Pinned codec archive is missing: $archivePath" }
    $archiveItem = Get-Item -LiteralPath $archivePath
    [ordered]@{ name = $archiveName; bytes = $archiveItem.Length; sha256 = (Get-LocalFileHash -LiteralPath $archivePath -Algorithm SHA256) }
}
$codecIdentity = [ordered]@{
    schemaVersion = 1
    profileId = 'P5-R5-v1'
    vcpkgBaseline = $buildInfo.source.vcpkgBaseline
    manifestSha256 = Get-LocalFileHash -LiteralPath $dependencyManifest -Algorithm SHA256
    targetTriplet = 'x64-windows-static'
    x265SourceRepository = $x265Profile.sourceRepository
    x265SourceRef = $x265Profile.sourceRef
    x265SourceArchiveSha512 = $x265Profile.sourceArchiveSha512
    x265BaselinePortTree = $x265Profile.vcpkgBaselinePortTree
    x265OverlaySha256 = $x265OverlaySha256
    x265BuildOptions = @($x265Profile.configureOptions)
    codecPrefix = $codecPrefix
    archives = @($codecArchiveRecords)
}
$codecIdentityJson = $codecIdentity | ConvertTo-Json -Depth 8
if (Test-Path -LiteralPath $codecIdentityPath -PathType Leaf) {
    $existingCodecIdentity = Get-Content -LiteralPath $codecIdentityPath -Raw | ConvertFrom-Json
    if ($existingCodecIdentity.profileId -ne $codecIdentity.profileId -or
        $existingCodecIdentity.manifestSha256 -ne $codecIdentity.manifestSha256 -or
        $existingCodecIdentity.x265OverlaySha256 -ne $x265OverlaySha256 -or
        $existingCodecIdentity.x265SourceArchiveSha512 -ne $x265Profile.sourceArchiveSha512 -or
        $existingCodecIdentity.codecPrefix -ne $codecPrefix -or
        $existingCodecIdentity.archives[0].sha256 -ne $codecArchiveRecords[0].sha256 -or
        $existingCodecIdentity.archives[1].sha256 -ne $codecArchiveRecords[1].sha256) {
        throw "Existing x265 Main10 codec identity conflicts with installed archives: $codecIdentityPath"
    }
}
else {
    Set-Content -LiteralPath $codecIdentityPath -Value $codecIdentityJson -Encoding UTF8
}
$codecIdentitySha256 = Get-LocalFileHash -LiteralPath $codecIdentityPath -Algorithm SHA256

$sourceRoot = Join-Path $workspaceRoot 'ffmpeg-n9.0.1-source'
$sourceIdentityPath = Join-Path $workspaceRoot 'ffmpeg-n9.0.1-source-identity.json'
if (Test-Path -LiteralPath $sourceRoot) {
    if (-not (Test-Path -LiteralPath $sourceIdentityPath -PathType Leaf)) {
        throw "A partial or unverified FFmpeg source directory already exists: $sourceRoot"
    }
    $sourceIdentity = Get-Content -LiteralPath $sourceIdentityPath -Raw | ConvertFrom-Json
    if ($sourceIdentity.archiveSha256 -ne $archiveSha256 -or
        -not (Test-Path -LiteralPath (Join-Path $sourceRoot 'configure') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $sourceRoot 'COPYING.GPLv3') -PathType Leaf)) {
        throw "The deterministic extracted FFmpeg source identity is invalid: $sourceRoot"
    }
}
else {
    New-Item -ItemType Directory -Path $sourceRoot | Out-Null
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    & $tar -xzf $sourceArchive -C $sourceRoot --strip-components=1
    $extractExitCode = $LASTEXITCODE
    Write-Output "ARGV: $tar -xzf $sourceArchive -C $sourceRoot --strip-components=1"
    Write-Output "TAR_EXIT_CODE=$extractExitCode"
    if ($extractExitCode -ne 0) { throw "Pinned FFmpeg source extraction failed ($extractExitCode)." }
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'configure') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $sourceRoot 'COPYING.GPLv3') -PathType Leaf)) {
        throw 'The pinned FFmpeg archive did not yield the expected source and GPL notice.'
    }
    $archiveRelativePath = $sourceArchive.Substring($projectRoot.TrimEnd('\\').Length).TrimStart('\\')
    [ordered]@{
        schemaVersion = 1
        archive = $archiveRelativePath
        archiveSha256 = $archiveSha256
        archiveSha512 = $archiveSha512
        version = $buildInfo.source.version
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $sourceIdentityPath -Encoding UTF8
}

$noticeRoot = Join-Path $projectRoot 'tools\p5-r5-video-validation\notices'
New-Item -ItemType Directory -Force -Path $noticeRoot | Out-Null
$noticeSources = @(
    @{ Source = (Join-Path $sourceRoot 'COPYING.GPLv3'); Destination = (Join-Path $noticeRoot 'FFmpeg-COPYING.GPLv3') },
    @{ Source = (Join-Path $codecPrefix 'share\x264\copyright'); Destination = (Join-Path $noticeRoot 'x264-copyright.txt') },
    @{ Source = (Join-Path $codecPrefix 'share\x265\copyright'); Destination = (Join-Path $noticeRoot 'x265-copyright.txt') })
foreach ($notice in $noticeSources) {
    $sourceHash = Get-LocalFileHash -LiteralPath $notice.Source -Algorithm SHA256
    if (Test-Path -LiteralPath $notice.Destination -PathType Leaf) {
        if ((Get-LocalFileHash -LiteralPath $notice.Destination -Algorithm SHA256) -ne $sourceHash) {
            throw "Existing package notice differs from its pinned source: $($notice.Destination)"
        }
    }
    else {
        Copy-Item -LiteralPath $notice.Source -Destination $notice.Destination
    }
}

$buildRoot = Join-Path $workspaceRoot "minimal-libav-$BuildId"
$prefix = Join-Path $buildRoot 'prefix'
$buildRecordPath = Join-Path $buildRoot 'minimal-libav-build-record.json'
$recordOutPath = Join-Path $workspaceRoot "$BuildId-libav-prerequisites.json"
$codecManifestHash = Get-LocalFileHash -LiteralPath $dependencyManifest -Algorithm SHA256
$resumeConfiguredBuild = $false
$alreadyBuilt = $false
if (Test-Path -LiteralPath $buildRoot) {
    if (Test-Path -LiteralPath $buildRecordPath -PathType Leaf) {
        $existingRecord = Get-Content -LiteralPath $buildRecordPath -Raw | ConvertFrom-Json
        if ($existingRecord.sourceArchiveSha256 -ne $archiveSha256 -or
            $existingRecord.codecManifestSha256 -ne $codecManifestHash -or
            $existingRecord.codecIdentitySha256 -ne $codecIdentitySha256 -or
            $existingRecord.x265OverlaySha256 -ne $x265OverlaySha256 -or
            $existingRecord.version -ne $buildInfo.source.version) {
            throw "Existing FFmpeg build record does not match the frozen P5-R5 pins: $buildRecordPath"
        }
        foreach ($archiveName in @('avformat.lib', 'avcodec.lib', 'avutil.lib', 'swscale.lib')) {
            $archivePath = Join-Path $prefix "lib\$archiveName"
            if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
                throw "The recorded FFmpeg build is incomplete: $archivePath"
            }
        }
        $alreadyBuilt = $true
    }
    else {
        $buildSource = Join-Path $buildRoot 'src'
        $configMake = Join-Path $buildSource 'ffbuild\config.mak'
        if (-not (Test-Path -LiteralPath $configMake -PathType Leaf)) {
            throw "A partial FFmpeg build root has no completed pinned configure output: $buildRoot"
        }
        $configuredText = Get-Content -LiteralPath $configMake -Raw
        foreach ($requiredSetting in @(
                '^CONFIG_LIBX264=yes$', '^CONFIG_LIBX265=yes$', '^CONFIG_AVFORMAT=yes$',
                '^CONFIG_AVCODEC=yes$', '^CONFIG_AVUTIL=yes$', '^CONFIG_SWSCALE=yes$')) {
            if ($configuredText -notmatch "(?m)$requiredSetting") {
                throw "Existing FFmpeg configure output is not the frozen P5-R5 feature set: $requiredSetting"
            }
        }
        foreach ($forbiddenSetting in @('^CONFIG_SHARED=yes$', '^CONFIG_PROGRAMS=yes$', '^CONFIG_NETWORK=yes$')) {
            if ($configuredText -match "(?m)$forbiddenSetting") {
                throw "Existing FFmpeg configure output enables a prohibited P5-R5 feature: $forbiddenSetting"
            }
        }
        $resumeConfiguredBuild = $true
    }
}
else {
    New-Item -ItemType Directory -Path $buildRoot | Out-Null
    $buildSource = Join-Path $buildRoot 'src'
    Copy-Item -LiteralPath $sourceRoot -Destination $buildSource -Recurse
}

if (-not $alreadyBuilt) {
    & $env:ComSpec /d /s /c "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set" |
        ForEach-Object {
            if ($_ -match '^([^=]+)=(.*)$') {
                [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
            }
        }
    if ($LASTEXITCODE -ne 0) { throw "VsDevCmd failed with exit $LASTEXITCODE." }

    $msysToolsRoot = Join-Path $downloadsRoot 'tools\msys2'
    $bash = Get-ChildItem -LiteralPath $msysToolsRoot -Recurse -Filter bash.exe -File -ErrorAction SilentlyContinue |
        Where-Object {
            $candidateRoot = Split-Path (Split-Path (Split-Path $_.FullName -Parent) -Parent) -Parent
            @(Get-ChildItem -LiteralPath (Join-Path $candidateRoot 'usr\share') -Directory -Filter 'automake-*' -ErrorAction SilentlyContinue |
                    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'ar-lib') -PathType Leaf }).Count -gt 0
        } | Sort-Object FullName | Select-Object -First 1 -ExpandProperty FullName
    $nasmRoot = Join-Path $downloadsRoot 'tools\nasm'
    $nasm = Get-ChildItem -LiteralPath $nasmRoot -Recurse -Filter nasm.exe -File -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -First 1 -ExpandProperty FullName
    if (-not $bash -or -not $nasm) { throw 'The pinned vcpkg MSYS2/NASM build tools are missing.' }
    $msysRoot = Split-Path (Split-Path (Split-Path $bash -Parent) -Parent) -Parent
    $msysBin = Join-Path $msysRoot 'usr\bin'
    $automakeBin = Get-ChildItem -LiteralPath (Join-Path $msysRoot 'usr\share') -Directory -Filter 'automake-*' |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'ar-lib') -PathType Leaf } |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    $pkgConfigCandidates = @(
        (Join-Path $msysRoot 'usr\bin\pkg-config.exe'),
        (Join-Path $msysRoot 'mingw64\bin\pkg-config.exe'))
    $pkgConfig = $pkgConfigCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    $make = Join-Path $msysBin 'make.exe'
    foreach ($tool in @($pkgConfig, $make, (Join-Path $automakeBin 'ar-lib'))) {
        if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "A pinned minimal-libav build tool is missing: $tool" }
    }

    $clDirectory = Split-Path (Get-Command cl.exe -ErrorAction Stop).Source -Parent
    $rcDirectory = Split-Path (Get-Command rc.exe -ErrorAction Stop).Source -Parent
    $env:Path = @($env:Path, $clDirectory, $rcDirectory, (Split-Path $nasm -Parent), (Split-Path $pkgConfig -Parent), $msysBin, $automakeBin) -join ';'

    function ConvertTo-BashPath([string]$PathValue) { return ($PathValue -replace '\\', '/') }
    function ConvertTo-MsysPath([string]$PathValue) {
        $normalized = ConvertTo-BashPath $PathValue
        if ($normalized -match '^([A-Za-z]):/(.*)$') { return "/$($matches[1].ToLowerInvariant())/$($matches[2])" }
        return $normalized
    }
    function Quote-Bash([string]$Value) { return "'" + ($Value -replace "'", "'\\''") + "'" }
    $env:PKG_CONFIG_PATH = @(
        (ConvertTo-MsysPath (Join-Path $codecPrefix 'lib\pkgconfig')),
        (ConvertTo-MsysPath (Join-Path $codecPrefix 'share\pkgconfig'))) -join ':'

    $bashSource = ConvertTo-BashPath $buildSource
    $bashPrefix = ConvertTo-BashPath $prefix
    $bashCodecPrefix = ConvertTo-BashPath $codecPrefix
    $bashPkgConfig = ConvertTo-BashPath $pkgConfig
    $bashToolPath = @(
        (ConvertTo-MsysPath $clDirectory),
        (ConvertTo-MsysPath $rcDirectory),
        (ConvertTo-MsysPath (Split-Path $nasm -Parent)),
        (ConvertTo-MsysPath (Split-Path $pkgConfig -Parent)),
        (ConvertTo-MsysPath $msysBin),
        (ConvertTo-MsysPath $automakeBin)) -join ':'
    $configureArgs = @(
        "--prefix=$(Quote-Bash $bashPrefix)", '--toolchain=msvc', '--enable-pic', '--enable-static', '--disable-shared', '--disable-everything',
        '--disable-programs', '--disable-doc', '--disable-debug', '--disable-autodetect', '--disable-network',
        '--disable-avdevice', '--disable-avfilter', '--disable-swresample',
        '--enable-avutil', '--enable-avcodec', '--enable-avformat', '--enable-swscale', '--enable-gpl',
        '--enable-libx264', '--enable-libx265', '--enable-protocol=file', '--enable-demuxer=mov',
        '--enable-muxer=mov,mp4', '--enable-decoder=h264,hevc,aac,pcm_s24le', '--enable-parser=h264,hevc,aac',
        '--enable-encoder=libx264,libx265', '--enable-bsf=aac_adtstoasc', '--enable-runtime-cpudetect',
        '--enable-w32threads', '--target-os=win32', '--cc=cl.exe', '--host-cc=cl.exe', '--cxx=cl.exe',
        '--windres=rc.exe', '--ld=link.exe', "--ar=$(Quote-Bash 'ar-lib lib.exe')", "--ranlib=$(Quote-Bash ':')",
        "--pkg-config=$(Quote-Bash $bashPkgConfig)", '--pkg-config-flags=--static',
        "--extra-cflags=$(Quote-Bash '-DHAVE_UNISTD_H=0 -MT')", "--extra-cxxflags=$(Quote-Bash '-MT')",
        "--extra-ldflags=$(Quote-Bash "-libpath:$bashCodecPrefix/lib")", '--enable-cross-compile',
        '--arch=x86_64', '--enable-asm', '--enable-x86asm')
    $buildCommand = if ($resumeConfiguredBuild) {
        "PATH=$(Quote-Bash $bashToolPath); export PATH; cd $(Quote-Bash $bashSource) && make -j$Jobs && make install"
    }
    else {
        "PATH=$(Quote-Bash $bashToolPath); export PATH; cd $(Quote-Bash $bashSource) && `$BASH ./configure $($configureArgs -join ' ') && make -j$Jobs && make install"
    }
    $commandSuffix = if ($resumeConfiguredBuild) { 'resume-1' } else { 'initial' }
    $commandPath = Join-Path $buildRoot "minimal-libav-$commandSuffix-command.txt"
    if (Test-Path -LiteralPath $commandPath) {
        if ((Get-Content -LiteralPath $commandPath -Raw).Trim() -ne $buildCommand) {
            throw "Create-only FFmpeg command record conflicts with this invocation: $commandPath"
        }
    }
    else {
        $buildCommand | Set-Content -LiteralPath $commandPath -Encoding UTF8
    }
    $logPath = Join-Path $workspaceRoot "$BuildId-minimal-libav-$commandSuffix.log"
    if (Test-Path -LiteralPath $logPath) { throw "Create-only FFmpeg build log already exists: $logPath" }
    Write-Output "ARGV: $bash -lc <pinned $commandSuffix configure/make command; source=$buildSource; prefix=$prefix; jobs=$Jobs>"
    $priorErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $bash -lc $buildCommand 2>&1 | Tee-Object -LiteralPath $logPath
    $buildExitCode = $LASTEXITCODE
    $ErrorActionPreference = $priorErrorActionPreference
    Write-Output "FFMPEG_BUILD_EXIT_CODE=$buildExitCode"
    if ($buildExitCode -ne 0) { throw "Pinned minimal FFmpeg build failed ($buildExitCode); see $logPath" }

    $archiveRecords = foreach ($archiveName in @('avformat.lib', 'avcodec.lib', 'avutil.lib', 'swscale.lib')) {
        $archivePath = Join-Path $prefix "lib\$archiveName"
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw "Minimal FFmpeg did not install $archivePath" }
        $archiveItem = Get-Item -LiteralPath $archivePath
        [ordered]@{ name = $archiveName; bytes = $archiveItem.Length; sha256 = (Get-LocalFileHash -LiteralPath $archivePath -Algorithm SHA256) }
    }
    [ordered]@{
        schemaVersion = 1
        version = $buildInfo.source.version
        sourceArchiveSha256 = $archiveSha256
        sourceArchiveSha512 = $archiveSha512
        codecManifestSha256 = $codecManifestHash
        codecIdentitySha256 = $codecIdentitySha256
        x265OverlaySha256 = $x265OverlaySha256
        x265SourceArchiveSha512 = $x265Profile.sourceArchiveSha512
        x265BuildOptions = @($x265Profile.configureOptions)
        vcpkgBaseline = $buildInfo.source.vcpkgBaseline
        staticCodecVersions = @($buildInfo.staticCodecs | ForEach-Object { "$($_.name)=$($_.version)#$($_.portVersion)" })
        codecPrefix = $codecPrefix
        buildRoot = $buildRoot
        installPrefix = $prefix
        configureArguments = $configureArgs
        librariesOnly = $true
        disabled = @('programs', 'network', 'avdevice', 'avfilter', 'swresample', 'autodetect', 'shared-libraries')
        archives = @($archiveRecords)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $buildRecordPath -Encoding UTF8
}

if (-not (Test-Path -LiteralPath $recordOutPath -PathType Leaf)) {
    [ordered]@{
        schemaVersion = 1
        buildId = $BuildId
        ffmpegPrefix = $prefix
        codecPrefix = $codecPrefix
        sourceArchiveSha256 = $archiveSha256
        codecManifestSha256 = $codecManifestHash
        codecIdentitySha256 = $codecIdentitySha256
        x265OverlaySha256 = $x265OverlaySha256
        buildRecord = $buildRecordPath
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordOutPath -Encoding UTF8
}
$outputRecord = Get-Content -LiteralPath $recordOutPath -Raw | ConvertFrom-Json
if ($outputRecord.buildId -ne $BuildId -or
    $outputRecord.sourceArchiveSha256 -ne $archiveSha256 -or
    $outputRecord.codecManifestSha256 -ne $codecManifestHash -or
    $outputRecord.codecIdentitySha256 -ne $codecIdentitySha256 -or
    $outputRecord.x265OverlaySha256 -ne $x265OverlaySha256 -or
    $outputRecord.ffmpegPrefix -ne $prefix -or
    $outputRecord.codecPrefix -ne $codecPrefix) {
    throw "The P5-R5 prerequisite record does not match this build invocation: $recordOutPath"
}
Write-Output "FFMPEG_PREFIX=$prefix"
Write-Output "CODEC_PREFIX=$codecPrefix"
Write-Output "CODEC_IDENTITY=$codecIdentityPath"
Write-Output "X265_OVERLAY_SHA256=$x265OverlaySha256"
Write-Output "BUILD_RECORD=$buildRecordPath"
