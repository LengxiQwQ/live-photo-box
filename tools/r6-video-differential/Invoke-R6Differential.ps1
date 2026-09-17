[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CandidateExe,

    [string]$InputRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.ai-tmp\workspace\P4-R6\extracted-videos'),

    [string]$OutputDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.ai-tmp\workspace\P4-R6\differential-evidence'),

    [string]$MatrixPath = (Join-Path $PSScriptRoot 'matrix.json'),

    [switch]$NoBuild
)

# Research/evidence only. ffmpeg/ffprobe are never production dependencies.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$cliProject = Join-Path $projectRoot 'LivePhotoBox.Cli\LivePhotoBox.Cli.csproj'
$ffprobe = Get-Command ffprobe -ErrorAction Stop
$ffmpeg = Get-Command ffmpeg -ErrorAction Stop

if (-not (Test-Path -LiteralPath $InputRoot -PathType Container)) {
    throw "Input root does not exist: $InputRoot. Extract real samples through the project-owned path first."
}
if (-not (Test-Path -LiteralPath $MatrixPath -PathType Leaf)) {
    throw "Differential matrix does not exist: $MatrixPath"
}
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Output directory already exists: $OutputDirectory. Use a new evidence directory; this harness never overwrites prior evidence."
}

$matrix = Get-Content -LiteralPath $MatrixPath -Raw | ConvertFrom-Json
if ($matrix.schemaVersion -ne 1 -or $null -eq $matrix.cases -or $matrix.cases.Count -eq 0) {
    throw "Unsupported or empty R6 differential matrix: $MatrixPath"
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

function Invoke-ExternalCapture {
    param([string]$Executable, [string[]]$Arguments, [string]$LogPath)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $Executable @Arguments *> $LogPath
    $exitCode = $LASTEXITCODE
    $timer.Stop()
    return [pscustomobject]@{ ExitCode = $exitCode; ElapsedMilliseconds = [math]::Round($timer.Elapsed.TotalMilliseconds, 3); LogPath = $LogPath }
}

function Get-ProbeFacts {
    param([string]$VideoPath, [string]$ReportPath)
    $arguments = @('-v', 'error', '-show_entries', 'stream=index,codec_type,codec_name,profile,pix_fmt,width,height,color_space,color_transfer,color_primaries,avg_frame_rate,r_frame_rate,sample_rate,channels:stream_tags=rotate:format=duration,size,bit_rate', '-of', 'json', $VideoPath)
    $result = Invoke-ExternalCapture $ffprobe.Source $arguments $ReportPath
    if ($result.ExitCode -ne 0) { throw "ffprobe failed for '$VideoPath' (exit $($result.ExitCode)); see $ReportPath" }
    return Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
}

function Get-QualityMetrics {
    param([string]$ReferencePath, [string]$OutputPath, [object]$OutputFacts, [string]$LogPath)
    $primaryVideo = @($OutputFacts.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1)
    if ($primaryVideo.Count -ne 1 -or $primaryVideo[0].width -le 0 -or $primaryVideo[0].height -le 0) {
        throw "ffprobe did not provide a primary video size for '$OutputPath'."
    }
    # Compare undecorated stored frames.  The reference is scaled to the
    # candidate's encoded dimensions, so a codec alignment resize is explicit
    # rather than silently making the metric inapplicable.  Rotation and HDR
    # metadata are independently reported by ffprobe; PSNR/SSIM never imply
    # their preservation.
    $width = $primaryVideo[0].width
    $height = $primaryVideo[0].height
    $filter = "[0:v:0]settb=AVTB,setpts=PTS-STARTPTS,scale=$width`:$height,format=yuv420p,split=2[refp][refs];" +
        '[1:v:0]settb=AVTB,setpts=PTS-STARTPTS,format=yuv420p,split=2[distp][dists];' +
        '[refp][distp]psnr=shortest=1;[refs][dists]ssim=shortest=1'
    $result = Invoke-ExternalCapture $ffmpeg.Source @('-hide_banner', '-nostdin', '-noautorotate', '-i', $ReferencePath, '-noautorotate', '-i', $OutputPath, '-filter_complex', $filter, '-f', 'null', '-') $LogPath
    $log = Get-Content -LiteralPath $LogPath -Raw
    $psnr = [regex]::Match($log, 'PSNR .*average:([0-9.]+|inf)')
    $ssim = [regex]::Match($log, 'SSIM .*All:([0-9.]+)')
    return [ordered]@{ ExitCode = $result.ExitCode; ElapsedMilliseconds = $result.ElapsedMilliseconds; PsnrAverage = if ($psnr.Success) { $psnr.Groups[1].Value } else { $null }; SsimAll = if ($ssim.Success) { $ssim.Groups[1].Value } else { $null }; LogPath = $LogPath }
}

$summary = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('O')
    matrixPath = (Resolve-Path -LiteralPath $MatrixPath).Path
    inputRoot = (Resolve-Path -LiteralPath $InputRoot).Path
    productionBackend = 'LivePhotoBox project-owned ISO-BMFF + Windows Media Foundation SoftwareForced'
    candidateBackend = 'research libav* candidate (not a production-size claim)'
    cases = @()
}

foreach ($case in $matrix.cases) {
    $inputPath = Join-Path $InputRoot $case.input
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Matrix input missing: $inputPath" }
    $caseDirectory = Join-Path $OutputDirectory $case.id
    New-Item -ItemType Directory -Path $caseDirectory | Out-Null
    $extension = ".$($case.targetContainer)"
    $mfOutput = Join-Path $caseDirectory "media-foundation$extension"
    $libavOutput = Join-Path $caseDirectory "minimal-libav$extension"

    $cliArguments = @('run', '--project', $cliProject, '-c', 'Release')
    if ($NoBuild) { $cliArguments += '--no-build' }
    $cliArguments += @('--', 'convert', $inputPath, '-o', $mfOutput, '--codec', $case.targetCodec)
    $mfRun = Invoke-ExternalCapture 'dotnet' $cliArguments (Join-Path $caseDirectory 'media-foundation.log')
    if ($mfRun.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $mfOutput -PathType Leaf)) { throw "Media Foundation case '$($case.id)' failed; see $($mfRun.LogPath)" }

    $libavRun = Invoke-ExternalCapture $CandidateExe @('transcode', $inputPath, $libavOutput, $case.targetCodec) (Join-Path $caseDirectory 'minimal-libav.log')
    if ($libavRun.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $libavOutput -PathType Leaf)) { throw "research libav* case '$($case.id)' failed; see $($libavRun.LogPath)" }

    $mfFacts = Get-ProbeFacts $mfOutput (Join-Path $caseDirectory 'media-foundation.ffprobe.json')
    $libavFacts = Get-ProbeFacts $libavOutput (Join-Path $caseDirectory 'minimal-libav.ffprobe.json')
    $mfQuality = Get-QualityMetrics $inputPath $mfOutput $mfFacts (Join-Path $caseDirectory 'media-foundation.quality.log')
    $libavQuality = Get-QualityMetrics $inputPath $libavOutput $libavFacts (Join-Path $caseDirectory 'minimal-libav.quality.log')
    if ($mfQuality.ExitCode -ne 0 -or $libavQuality.ExitCode -ne 0) {
        throw "Quality metric collection failed for '$($case.id)'; see the per-candidate quality logs."
    }

    $summary.cases += [ordered]@{
        id = $case.id; source = $inputPath; targetContainer = $case.targetContainer; targetCodec = $case.targetCodec; semanticFocus = $case.semanticFocus
        mediaFoundation = [ordered]@{ elapsedMilliseconds = $mfRun.ElapsedMilliseconds; outputBytes = (Get-Item -LiteralPath $mfOutput).Length; output = $mfOutput; ffprobe = $mfFacts; quality = $mfQuality }
        minimalLibav = [ordered]@{ elapsedMilliseconds = $libavRun.ElapsedMilliseconds; outputBytes = (Get-Item -LiteralPath $libavOutput).Length; output = $libavOutput; ffprobe = $libavFacts; quality = $libavQuality }
    }
}

$summaryPath = Join-Path $OutputDirectory 'r6-differential-summary.json'
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $summaryPath -Encoding utf8NoBOM
Write-Host "R6 differential completed. Summary: $summaryPath"
