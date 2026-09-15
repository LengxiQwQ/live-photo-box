param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64')][string]$Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-native.ps1') `
    -Configuration $Configuration -Architecture $Architecture -TestHarness
if ($LASTEXITCODE -ne 0) { throw "CMake test harness build failed ($LASTEXITCODE)." }
