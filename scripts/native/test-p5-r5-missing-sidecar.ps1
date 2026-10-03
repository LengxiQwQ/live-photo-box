param(
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$RecordId = 'r5-v3',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$OutputSet = 'iteration-5-bridge-1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$artifactDir = Join-Path $projectRoot "artifacts\native\$Configuration\win-x64"
$workspace = Join-Path $projectRoot '.ai-tmp\workspace\P5-R5'
$negativeDir = Join-Path $workspace "$RecordId-missing-sidecar-package"
$nativeName = 'LivePhotoBox.Native.dll'
$smokeName = 'lpb_video_sidecar_runtime_smoke.exe'
$conversionSmokeName = 'lpb_video_sidecar_conversion_smoke.exe'
$sourceNative = Join-Path $artifactDir $nativeName
$sourceSmoke = Join-Path $artifactDir $smokeName
$sourceConversionSmoke = Join-Path $artifactDir $conversionSmokeName
$sourceSidecar = Join-Path $artifactDir 'LivePhotoBox.Video.Libav.dll'
$logPath = Join-Path $workspace "$RecordId-missing-sidecar-smoke.log"
$sourceVideo = Join-Path $workspace 'huawei-extraction-pass-b\motion.mp4'
$outputDirectory = Join-Path $workspace $OutputSet
$conversionOutput = Join-Path $outputDirectory 'huawei-missing-sidecar.mp4'

function Get-Sha256Hex([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

foreach ($required in @($sourceNative, $sourceSmoke, $sourceConversionSmoke, $sourceSidecar, $sourceVideo)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "The Release sidecar smoke build is incomplete: $required" }
}
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    if (Test-Path -LiteralPath $outputDirectory) { throw "The create-only output-set path is not a directory: $outputDirectory" }
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}
if (Test-Path -LiteralPath $logPath) { throw "Create-only missing-sidecar log already exists: $logPath" }
if (-not (Test-Path -LiteralPath $negativeDir -PathType Container)) {
    New-Item -ItemType Directory -Path $negativeDir | Out-Null
}
if (Test-Path -LiteralPath (Join-Path $negativeDir 'LivePhotoBox.Video.Libav.dll')) {
    throw "The negative runtime package already contains the sidecar and cannot prove its absence: $negativeDir"
}

foreach ($name in @($nativeName, $smokeName, $conversionSmokeName)) {
    $source = Join-Path $artifactDir $name
    $destination = Join-Path $negativeDir $name
    $sourceHash = Get-Sha256Hex -Path $source
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        if ((Get-Sha256Hex -Path $destination) -ne $sourceHash) {
            throw "Existing negative runtime artifact differs from the current Release build: $destination"
        }
    }
    else {
        Copy-Item -LiteralPath $source -Destination $destination
    }
}

$smoke = Join-Path $negativeDir $smokeName
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($logPath, "ARGV: $smoke 0`r`n", $utf8NoBom)
$priorErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $smoke 0 2>&1 | ForEach-Object {
    $line = [string]$_
    [System.IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8NoBom)
    [Console]::Out.WriteLine($line)
}
$exitCode = $LASTEXITCODE
$ErrorActionPreference = $priorErrorActionPreference
[System.IO.File]::AppendAllText($logPath, "EXIT_CODE=$exitCode`r`n", $utf8NoBom)
Write-Output "EXIT_CODE=$exitCode"
if ($exitCode -ne 0) { throw "Missing-sidecar runtime smoke failed ($exitCode); see $logPath" }

if (Test-Path -LiteralPath $conversionOutput) { throw "Create-only missing-sidecar output already exists: $conversionOutput" }
$conversionSmoke = Join-Path $negativeDir $conversionSmokeName
[System.IO.File]::AppendAllText($logPath, "ARGV: $conversionSmoke missing-sidecar $sourceVideo $conversionOutput`r`n", $utf8NoBom)
$ErrorActionPreference = 'Continue'
& $conversionSmoke 'missing-sidecar' $sourceVideo $conversionOutput 2>&1 | ForEach-Object {
    $line = [string]$_
    [System.IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8NoBom)
    [Console]::Out.WriteLine($line)
}
$conversionExitCode = $LASTEXITCODE
$ErrorActionPreference = $priorErrorActionPreference
[System.IO.File]::AppendAllText($logPath, "CONVERSION_EXIT_CODE=$conversionExitCode`r`n", $utf8NoBom)
if ($conversionExitCode -ne 0) { throw "Missing-sidecar conversion path failed ($conversionExitCode); see $logPath" }
Write-Output "CONVERSION_EXIT_CODE=$conversionExitCode"
