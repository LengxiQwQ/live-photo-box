param(
    [Parameter(Mandatory = $true)][string]$SidecarPath,
    [Parameter(Mandatory = $true)][string]$DumpbinPath,
    [Parameter(Mandatory = $true)][string]$HeaderPath
)

$ErrorActionPreference = 'Stop'
$sidecar = (Resolve-Path -LiteralPath $SidecarPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $DumpbinPath -PathType Leaf)) {
    throw "The pinned MSVC PE import inspector is missing: $DumpbinPath"
}

$dumpbinOutput = @(& $DumpbinPath /nologo /imports $sidecar 2>&1)
$dumpbinExitCode = $LASTEXITCODE
if ($dumpbinExitCode -ne 0) {
    throw "dumpbin import inspection failed ($dumpbinExitCode) for $sidecar"
}

$parsedImports = [System.Collections.Generic.List[string]]::new()
foreach ($line in $dumpbinOutput) {
    $lineText = [string]$line
    if ($lineText -match '^\s+(?<module>[A-Za-z0-9_.-]+\.dll)\s*$') {
        $parsedImports.Add($Matches.module.ToLowerInvariant())
    }
}
if ($parsedImports.Count -eq 0) {
    throw "No PE import descriptors were found in the pinned video sidecar: $sidecar"
}

$distinctImports = @($parsedImports | Sort-Object -Unique)
if ($distinctImports.Count -ne $parsedImports.Count) {
    throw "The pinned video sidecar contains a duplicate PE import descriptor: $sidecar"
}

$allowedSystemImports = @('advapi32.dll', 'kernel32.dll', 'ncrypt.dll')
$forbiddenCodecImport = $distinctImports | Where-Object {
    $_ -match '^(avcodec|avformat|avutil|swscale|x264|x265).*\.dll$'
}
if ($forbiddenCodecImport) {
    throw "The video sidecar unexpectedly imports a shared FFmpeg/codec library: $($forbiddenCodecImport -join ', ')"
}
$unexpectedImports = $distinctImports | Where-Object { $_ -notin $allowedSystemImports }
if ($unexpectedImports) {
    throw "The video sidecar imports a dependency outside the frozen Windows system import set: $($unexpectedImports -join ', ')"
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
$sidecarStream = [System.IO.File]::OpenRead($sidecar)
try {
    $hash = [System.BitConverter]::ToString($sha256.ComputeHash($sidecarStream)).Replace('-', '').ToLowerInvariant()
}
finally {
    $sidecarStream.Dispose()
    $sha256.Dispose()
}
$importEntries = ($distinctImports | ForEach-Object { '    "' + $_ + '"' }) -join ",`r`n"
$header = @"
#pragma once

#include <array>
#include <string_view>

namespace lpb::media::build_identity
{
inline constexpr char sidecar_sha256[] = "$hash";
inline constexpr std::array<std::string_view, $($distinctImports.Count)> sidecar_imports{{
$importEntries
}};
}
"@

$headerDirectory = Split-Path -Parent $HeaderPath
New-Item -ItemType Directory -Force -Path $headerDirectory | Out-Null
[System.IO.File]::WriteAllText($HeaderPath, $header, [System.Text.UTF8Encoding]::new($false))
Write-Output "SIDECAR_SHA256=$hash"
Write-Output "SIDECAR_IMPORTS=$($distinctImports -join ',')"
Write-Output "IDENTITY_HEADER=$HeaderPath"
