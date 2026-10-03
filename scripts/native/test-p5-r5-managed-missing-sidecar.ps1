param(
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$RecordId = 'r5-iter7-managed-sidecar'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workspace = Join-Path $projectRoot '.ai-tmp\workspace\P5-R5'
$buildDirectory = Join-Path $workspace 'managed-smoke-build'
$packageDirectory = Join-Path $workspace "$RecordId-missing-sidecar-package"
$outputDirectory = Join-Path $workspace "$RecordId-missing-sidecar-output"
$buildLog = Join-Path $workspace "$RecordId-managed-smoke-build.log"
$buildOutputLog = "$buildLog.output"
$runLog = Join-Path $workspace "$RecordId-managed-missing-sidecar.log"
$runOutputLog = "$runLog.output"
$projectPath = Join-Path $projectRoot 'tools\p5-r5-video-validation\managed-smoke\P5R5.ManagedSmoke.csproj'
$sourceVideo = Join-Path $workspace 'huawei-extraction-pass-c\motion.mp4'
$mainAssemblyName = 'P5R5.ManagedSmoke.dll'
$sidecarName = 'LivePhotoBox.Video.Libav.dll'

function Get-Sha256Hex([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '').ToUpperInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $sourceVideo -PathType Leaf)) {
    throw "The canonical P5-R5 Huawei motion-video sample is required: $sourceVideo"
}
if ((Get-Item -LiteralPath $sourceVideo).Length -ne 6519404 -or
    (Get-Sha256Hex -Path $sourceVideo) -ne '664EF6DBA25D7B04228C79B874A0EDA742B1211E17EE770D0E82E765BD454CE6') {
    throw 'The canonical P5-R5 Huawei motion-video sample identity changed.'
}
if (Test-Path -LiteralPath $buildLog) { throw "Create-only managed-smoke build log already exists: $buildLog" }
if (Test-Path -LiteralPath $runLog) { throw "Create-only managed missing-sidecar log already exists: $runLog" }
New-Item -ItemType Directory -Force -Path $workspace | Out-Null

$publishArgs = @('publish', $projectPath, '--configuration', $Configuration, '--output', $buildDirectory)
"COMMAND: dotnet $($publishArgs -join ' ')" | Set-Content -LiteralPath $buildLog -Encoding utf8
& dotnet @publishArgs *> $buildOutputLog
$buildExitCode = $LASTEXITCODE
Get-Content -LiteralPath $buildOutputLog | Add-Content -LiteralPath $buildLog
Remove-Item -LiteralPath $buildOutputLog
"EXIT_CODE=$buildExitCode" | Add-Content -LiteralPath $buildLog
Get-Content -LiteralPath $buildLog
if ($buildExitCode -ne 0) { throw "Managed smoke build failed ($buildExitCode)." }

$requiredAssembly = Join-Path $buildDirectory $mainAssemblyName
$requiredNative = Join-Path $buildDirectory 'LivePhotoBox.Native.dll'
$requiredSidecar = Join-Path $buildDirectory $sidecarName
foreach ($required in @($requiredAssembly, $requiredNative, $requiredSidecar)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Managed smoke publish is incomplete: $required" }
}
if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $packageDirectory | Out-Null
}
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}
if (Test-Path -LiteralPath (Join-Path $packageDirectory $sidecarName) -PathType Leaf) {
    throw 'The missing-sidecar package unexpectedly already contains the sidecar.'
}
if (Get-ChildItem -LiteralPath $outputDirectory -Force | Select-Object -First 1) {
    throw "The managed missing-sidecar output directory must be empty before execution: $outputDirectory"
}

$sourceFiles = Get-ChildItem -LiteralPath $buildDirectory -File -Recurse |
    Where-Object { $_.Name -ne $sidecarName }
foreach ($file in $sourceFiles) {
    $relative = [System.IO.Path]::GetRelativePath($buildDirectory, $file.FullName)
    $destination = Join-Path $packageDirectory $relative
    $destinationParent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
    }
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
        (Get-Sha256Hex -Path $destination) -ne (Get-Sha256Hex -Path $file.FullName)) {
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}
if (Test-Path -LiteralPath (Join-Path $packageDirectory $sidecarName)) {
    throw 'The no-sidecar runtime package contains the sidecar after preparation.'
}

$appAssembly = Join-Path $packageDirectory $mainAssemblyName
$commandLine = "dotnet `"$appAssembly`" missing-sidecar `"$sourceVideo`" `"$outputDirectory`""
"COMMAND: $commandLine" | Set-Content -LiteralPath $runLog -Encoding utf8
$priorErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& dotnet $appAssembly 'missing-sidecar' $sourceVideo $outputDirectory *> $runOutputLog
$runExitCode = $LASTEXITCODE
$ErrorActionPreference = $priorErrorActionPreference
Get-Content -LiteralPath $runOutputLog | Add-Content -LiteralPath $runLog
Remove-Item -LiteralPath $runOutputLog
"EXIT_CODE=$runExitCode" | Add-Content -LiteralPath $runLog
Get-Content -LiteralPath $runLog
if ($runExitCode -ne 0) { throw "Managed real missing-sidecar validation failed ($runExitCode)." }
Write-Output "Managed real missing-sidecar conversion failed closed with truthful HDR capability diagnostics. Log: $runLog"
