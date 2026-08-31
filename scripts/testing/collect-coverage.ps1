[CmdletBinding()]
param(
    [ValidateSet('Core', 'Cli', 'All')]
    [string]$Scope = 'All',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputDirectory = Join-Path $repositoryRoot "artifacts\coverage\$timestamp"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$projects = @()
if ($Scope -in @('Core', 'All'))
{
    $projects += Join-Path $repositoryRoot 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj'
}
if ($Scope -in @('Cli', 'All'))
{
    $projects += Join-Path $repositoryRoot 'tests\LivePhotoBox.CLI.Tests\LivePhotoBox.CLI.Tests.csproj'
}

foreach ($project in $projects)
{
    & dotnet test $project `
        -c $Configuration `
        '-p:Platform=x64' `
        '--collect:XPlat Code Coverage' `
        '--results-directory' $outputDirectory
    if ($LASTEXITCODE -ne 0)
    {
        throw "Coverage test run failed: $project"
    }
}

$reports = @(Get-ChildItem -Path $outputDirectory -Filter 'coverage.cobertura.xml' -Recurse -File)
if ($reports.Count -eq 0)
{
    throw "No Cobertura reports were produced in $outputDirectory."
}

$reportGenerator = Get-Command reportgenerator -ErrorAction SilentlyContinue
if ($null -ne $reportGenerator)
{
    $htmlDirectory = Join-Path $outputDirectory 'html'
    $reportInputs = $reports.FullName -join ';'
    & $reportGenerator.Source "-reports:$reportInputs" "-targetdir:$htmlDirectory" '-reporttypes:Html;TextSummary'
    if ($LASTEXITCODE -ne 0)
    {
        throw 'ReportGenerator failed.'
    }
    Write-Host "Coverage HTML report: $htmlDirectory\index.html"
    Write-Host "Coverage summary: $htmlDirectory\Summary.txt"
}
else
{
    Write-Warning 'reportgenerator is not installed; Cobertura XML reports were still created.'
}

Write-Host "Coverage artifacts: $outputDirectory"
