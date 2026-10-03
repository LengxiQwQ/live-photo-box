[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$RecordId,

    [string]$FFmpeg,
    [string]$FFprobe,

    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$validatorPath = Join-Path $PSScriptRoot 'validate.py'
$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) {
    $python = Get-Command python3 -ErrorAction SilentlyContinue
}
if ($null -eq $python) {
    throw 'Python 3 is required for the independent P5-R5 matrix validator.'
}

$arguments = @(
    $validatorPath,
    '--record-id', $RecordId
)
if (-not [string]::IsNullOrWhiteSpace($FFmpeg)) {
    $arguments += @('--ffmpeg', $FFmpeg)
}
if (-not [string]::IsNullOrWhiteSpace($FFprobe)) {
    $arguments += @('--ffprobe', $FFprobe)
}
if ($PreflightOnly) {
    $arguments += '--preflight-only'
}

Push-Location -LiteralPath $repositoryRoot
try {
    & $python.Source @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
