[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CandidateExe,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$InputVideos,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string[]]$IndependentOutputs = @()
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

foreach ($input in $InputVideos) {
    if (-not (Test-Path -LiteralPath $input -PathType Leaf)) {
        throw "Input video does not exist: $input"
    }

    $baseName = [IO.Path]::GetFileNameWithoutExtension($input)
    $candidateReport = Join-Path $OutputDirectory "$baseName.libav-probe.json"
    & $CandidateExe probe $input | Out-File -LiteralPath $candidateReport -Encoding utf8NoBOM
    if ($LASTEXITCODE -ne 0) {
        throw "minimal libav* probe failed for '$input' (exit $LASTEXITCODE)."
    }
}

if ($IndependentOutputs.Count -gt 0) {
    $ffprobe = Get-Command ffprobe -ErrorAction Stop
    foreach ($output in $IndependentOutputs) {
        if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
            throw "Independent-validation output does not exist: $output"
        }

        $baseName = [IO.Path]::GetFileNameWithoutExtension($output)
        $validatorReport = Join-Path $OutputDirectory "$baseName.ffprobe.json"
        & $ffprobe.Source -v error `
            -show_entries stream=index,codec_type,codec_name,profile,pix_fmt,width,height,color_space,color_transfer,color_primaries,avg_frame_rate,sample_rate,channels:format=duration `
            -of json $output | Out-File -LiteralPath $validatorReport -Encoding utf8NoBOM
        if ($LASTEXITCODE -ne 0) {
            throw "Independent ffprobe validation failed for '$output' (exit $LASTEXITCODE)."
        }
    }
}

Write-Host "R6 differential evidence written to $OutputDirectory"
