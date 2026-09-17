[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$SourceDirectory,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$ExternalCodecPrefix,

    [Parameter(Mandatory)]
    [string]$BuildRoot,

    [ValidateRange(1, 64)]
    [int]$Jobs = [Math]::Min([Environment]::ProcessorCount, 8)
)

# Research/evidence only.  This intentionally builds libraries, never an
# ffmpeg/ffprobe executable, and writes only underneath the caller's P4-R6
# workspace path.  The caller must supply a pinned, already-audited source tree.
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $BuildRoot) {
    throw "Build root already exists and will not be overwritten: $BuildRoot"
}

$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$codecPrefix = (Resolve-Path -LiteralPath $ExternalCodecPrefix).Path
foreach ($required in @('lib\libx264.lib', 'lib\x265-static.lib', 'lib\pkgconfig\x264.pc', 'lib\pkgconfig\x265.pc')) {
    if (-not (Test-Path -LiteralPath (Join-Path $codecPrefix $required) -PathType Leaf)) {
        throw "External codec prerequisite is missing: $(Join-Path $codecPrefix $required)"
    }
}

$vsDevCmd = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat'
$msysRoot = 'C:\Users\LengxiQwQ\AppData\Local\vcpkg\downloads\tools\msys2'
# vcpkg can garbage-collect one tool component without removing its sibling.
# Discover a complete pair instead of embedding an ephemeral hash directory.
$bash = Get-ChildItem -LiteralPath $msysRoot -Recurse -Filter bash.exe -File -ErrorAction SilentlyContinue |
    Where-Object {
        $installRoot = Split-Path (Split-Path (Split-Path $_.FullName -Parent) -Parent) -Parent
        Test-Path -LiteralPath (Join-Path $installRoot 'usr\share\automake-1.18\ar-lib') -PathType Leaf
    } | Sort-Object FullName | Select-Object -First 1 -ExpandProperty FullName
$pkgConfig = Get-ChildItem -LiteralPath $msysRoot -Recurse -Filter pkg-config.exe -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '[\\/]mingw64[\\/]bin[\\/]' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
$nasm = Get-ChildItem -LiteralPath 'C:\Users\LengxiQwQ\AppData\Local\vcpkg\downloads\tools\nasm' -Recurse -Filter nasm.exe -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
foreach ($tool in @($vsDevCmd, $bash, $pkgConfig, $nasm)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Required local build tool is missing: $tool" }
}

New-Item -ItemType Directory -Path $BuildRoot | Out-Null
$buildRootFull = (Resolve-Path -LiteralPath $BuildRoot).Path
$workSource = Join-Path $buildRootFull 'src'
$prefix = Join-Path $buildRootFull 'prefix'
Copy-Item -LiteralPath $source -Destination $workSource -Recurse

# FFmpeg's configure runs under the vcpkg-provided MSYS bash but compiles with
# the Visual Studio toolchain inherited from this process.
& $env:ComSpec /d /s /c "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set" |
    ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') {
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
        }
    }
if ($LASTEXITCODE -ne 0) { throw "VsDevCmd failed with exit $LASTEXITCODE." }
$msysInstallRoot = Split-Path (Split-Path (Split-Path $bash -Parent) -Parent) -Parent
$msysBin = Join-Path $msysInstallRoot 'usr\bin'
$automakeBin = Join-Path $msysInstallRoot 'usr\share\automake-1.18'
$env:Path = "$env:Path;$msysBin;$automakeBin;$(Split-Path $nasm -Parent)"
$env:PKG_CONFIG_PATH = "$(Join-Path $codecPrefix 'lib\pkgconfig');$(Join-Path $codecPrefix 'share\pkgconfig')"

function ConvertTo-BashPath([string]$path) { return ($path -replace '\\', '/') }
function ConvertTo-MsysPath([string]$path) {
    $normalized = ConvertTo-BashPath $path
    if ($normalized -match '^([A-Za-z]):/(.*)$') {
        return "/$($matches[1].ToLowerInvariant())/$($matches[2])"
    }
    return $normalized
}
function Quote-Bash([string]$value) { return "'" + ($value -replace "'", "'\\''") + "'" }
$clDirectory = Split-Path (Get-Command cl.exe -ErrorAction Stop).Source -Parent
$rcDirectory = Split-Path (Get-Command rc.exe -ErrorAction Stop).Source -Parent
$bashSource = ConvertTo-BashPath $workSource
$bashPrefix = ConvertTo-BashPath $prefix
$bashCodecPrefix = ConvertTo-BashPath $codecPrefix
$bashPkgConfig = ConvertTo-BashPath $pkgConfig
$bashToolPath = @(
    (ConvertTo-MsysPath $clDirectory),
    (ConvertTo-MsysPath $rcDirectory),
    (ConvertTo-MsysPath (Split-Path $nasm -Parent)),
    (ConvertTo-MsysPath (Split-Path $pkgConfig -Parent)),
    (ConvertTo-MsysPath $msysBin),
    (ConvertTo-MsysPath $automakeBin)
) -join ':'

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
    '--arch=x86_64', '--enable-asm', '--enable-x86asm'
)
$command = "PATH=$(Quote-Bash $bashToolPath); export PATH; cd $(Quote-Bash $bashSource) && `$BASH ./configure $($configureArgs -join ' ') && make -j$Jobs && make install"
$command | Set-Content -LiteralPath (Join-Path $buildRootFull 'minimal-libav-command.txt') -Encoding utf8NoBOM
$logPath = Join-Path $buildRootFull 'minimal-libav-build.log'
& $bash -lc $command 2>&1 | Tee-Object -LiteralPath $logPath
if ($LASTEXITCODE -ne 0) { throw "Minimal FFmpeg build failed with exit $LASTEXITCODE; see $logPath" }

$archives = @('avformat.lib', 'avcodec.lib', 'avutil.lib', 'swscale.lib') | ForEach-Object {
    $path = Join-Path $prefix "lib\$_"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Build did not install expected archive: $path" }
    Get-Item -LiteralPath $path | Select-Object Name, Length, FullName
}
[ordered]@{
    schemaVersion = 1
    sourceDirectory = $source
    buildRoot = $buildRootFull
    installPrefix = $prefix
    externalCodecPrefix = $codecPrefix
    configureArgs = $configureArgs
    enabled = @('avutil', 'avcodec', 'avformat', 'swscale', 'mov-demuxer', 'mov/mp4-muxers', 'h264/hevc/aac/pcm_s24le-decoders', 'h264/hevc/aac-parsers', 'libx264/libx265-encoders', 'aac_adtstoasc-bsf', 'file-protocol')
    disabled = @('programs', 'network', 'avdevice', 'avfilter', 'swresample', 'autodetect', 'shared-libraries')
    archives = $archives
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildRootFull 'minimal-libav-build-record.json') -Encoding utf8NoBOM
Write-Host "Minimal libav build completed. Prefix: $prefix"
