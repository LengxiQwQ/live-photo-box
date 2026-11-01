<#
.SYNOPSIS
    Runs the repository's repeatable verification gates without packaging release artifacts.

.DESCRIPTION
    Build   restores and builds the public solution with Visual Studio MSBuild (Native + GUI + CLI + test assemblies; no test execution).
    Fast    runs a small, explicit Core/CLI smoke selection without building the GUI.
    Targeted requires TestFilter and selects Core, CLI or Both; zero matching tests is an error.
    Full    builds the solution once and runs Core/CLI regression; requires AllowBroadTests.
    Release adds the existing split-only real-sample CLI integration workflow after those checks; it does not prove P9 conversion/device acceptance.
    Build is the safe default. Select test scopes explicitly; use targeted build/CLI commands for a completed development unit.

    This script never calls build-release.ps1 and never deletes publish/. The CLI
    integration test owns and cleans only its ignored .ai-tmp/workspace/CLI/integration/ workspace.
#>
[CmdletBinding()]
param(
    [ValidateSet('Build', 'Fast', 'Targeted', 'Full', 'Release')]
    [string]$Scope = 'Build',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('Core', 'CLI', 'Both')]
    [string]$TestProject = 'Core',
    [string]$TestFilter,
    [switch]$NoBuild,
    [switch]$AllowBroadTests
)

$ErrorActionPreference = 'Stop'
if ($Scope -eq 'Targeted' -and [string]::IsNullOrWhiteSpace($TestFilter)) {
    throw 'Targeted requires -TestFilter. Select tests related to the change; use Full explicitly for broad regression.'
}
if ($Scope -ne 'Targeted' -and ($PSBoundParameters.ContainsKey('TestFilter') -or $PSBoundParameters.ContainsKey('TestProject'))) {
    throw '-TestFilter and -TestProject apply only to Targeted.'
}
if ($NoBuild -and $Scope -notin @('Fast', 'Targeted')) { throw '-NoBuild applies only to Fast or Targeted.' }
if ($Scope -in @('Full', 'Release') -and -not $AllowBroadTests) {
    throw 'Full/Release are broad regression, not daily checks. Pass -AllowBroadTests only when this scope is intended.'
}

# Ensure local tool locations (e.g. WinGet Links, Python) are available in PATH
$toolPaths = @(
    "$env:LOCALAPPDATA\Microsoft\WinGet\Links",
    "$env:LOCALAPPDATA\Programs\Python\Python314"
)
foreach ($p in $toolPaths) {
    if ((Test-Path -LiteralPath $p) -and (($env:PATH -split ';') -notcontains $p)) {
        $env:PATH = "$p;$env:PATH"
    }
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solutionPath = Join-Path $projectRoot 'Live Photo Box.sln'
$coreTests = Join-Path $projectRoot 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj'
$cliTests = Join-Path $projectRoot 'tests\LivePhotoBox.CLI.Tests\LivePhotoBox.CLI.Tests.csproj'
$cliIntegrationScript = Join-Path $projectRoot 'scripts\testing\run-cli-integration-test.py'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$ciTestFilter = if ($env:CI -eq 'true') { 'Category!=RealSamples&Category!=DerivedRealSamples&Category!=Extended' } else { $null }

function Invoke-VerificationStep {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    Write-Host "`n==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

Push-Location $projectRoot
try {
    Write-Host 'Live Photo Box verification gate' -ForegroundColor Cyan
    Write-Host "Scope: $Scope | Configuration: $Configuration" -ForegroundColor Gray
    Write-Host "SDK:   $(& dotnet --version)" -ForegroundColor Gray

    if ($Scope -in @('Fast', 'Targeted')) {
        $selection = if ($Scope -eq 'Fast') {
            @(
                @{ Name = 'Core'; Project = $coreTests; Filter = 'FullyQualifiedName~NativeRuntimeTests.NativeStructs_LayoutAgreement_MatchesNativeDefinition|FullyQualifiedName~NativeRuntimeTests.Sha256_StandardVectors_MatchDotNetImplementation|FullyQualifiedName~LivePhotoBox.Core.Tests.Media.P9TargetWriterAbiTests|FullyQualifiedName~LivePhotoBox.Core.Tests.Media.P6NeutralTimingConsumerTests' },
                @{ Name = 'CLI'; Project = $cliTests; Filter = 'FullyQualifiedName~UnknownOptionTests.Split_OptionTypo_SuggestsFormat|FullyQualifiedName~ValidationTests.Parallel_OutOfRange_Rejected|FullyQualifiedName~ValidationTests.SplitParallelZero_Rejected' }
            )
        } else {
            @(
                if ($TestProject -in @('Core', 'Both')) { @{ Name = 'Core'; Project = $coreTests; Filter = $TestFilter } }
                if ($TestProject -in @('CLI', 'Both')) { @{ Name = 'CLI'; Project = $cliTests; Filter = $TestFilter } }
            )
        }
        $resultRoot = Join-Path $projectRoot '.ai-tmp\workspace\diagnostics\test-selection'
        $run = 1
        while (Test-Path -LiteralPath (Join-Path $resultRoot "run-$run")) { $run++ }
        $results = New-Item -ItemType Directory -Path (Join-Path $resultRoot "run-$run") -ErrorAction Stop
        foreach ($item in $selection) {
            $arguments = @('test', $item.Project, '-c', $Configuration, '-p:Platform=x64',
                '--filter', $item.Filter, '--logger', "trx;LogFileName=$($item.Name).trx",
                '--results-directory', $results.FullName, '--nologo')
            if ($NoBuild) { $arguments += @('--no-build', '--no-restore') }
            $arguments | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results.FullName "$($item.Name)-command.json") -Encoding UTF8
            Invoke-VerificationStep -Name "$($item.Name) selected tests: $($item.Filter)" -Action { & dotnet @arguments }
            [xml]$trx = Get-Content -LiteralPath (Join-Path $results.FullName "$($item.Name).trx") -Raw
            if ([int]$trx.TestRun.ResultSummary.Counters.total -le 0) {
                throw "$($item.Name) filter matched zero tests. This is not a passing verification. Results: $($results.FullName)"
            }
        }
        Write-Host "$Scope selection passed. Results: $($results.FullName). This does not certify full media or device compatibility."
        return
    }

    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer (vswhere.exe) is required.' }
    $visualStudioPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1).Trim()
    $desktopMsbuild = Join-Path $visualStudioPath 'MSBuild\Current\Bin\MSBuild.exe'
    Invoke-VerificationStep -Name "Build public solution with Visual Studio MSBuild ($Configuration x64)" -Action {
        & $desktopMsbuild $solutionPath /nologo /m /restore /t:Build "/p:Configuration=$Configuration" /p:Platform=x64 /v:minimal
    }
    if ($Scope -eq 'Build') {
        Write-Host "Verification gate passed: solution Build ($Configuration x64)."
        return
    }
    Invoke-VerificationStep -Name "Run Core tests ($Configuration x64)" -Action {
        $arguments = @(
            'test', $coreTests,
            '-c', $Configuration,
            '-p:Platform=x64',
            '-p:SkipNativeBuild=true',
            '--no-build',
            '--no-restore',
            '--nologo'
        )
        if ($ciTestFilter) {
            Write-Host 'CI excludes private RealSamples/DerivedRealSamples and Extended; this is not full local acceptance.' -ForegroundColor DarkGray
            $arguments += @('--filter', $ciTestFilter)
        }
        & dotnet @arguments
    }

    Invoke-VerificationStep -Name "Run CLI tests ($Configuration x64)" -Action {
        $arguments = @(
            'test', $cliTests,
            '-c', $Configuration,
            '-p:Platform=x64',
            '-p:SkipNativeBuild=true',
            '--no-build',
            '--no-restore',
            '--nologo'
        )
        if ($ciTestFilter) {
            $arguments += @('--filter', $ciTestFilter)
        }
        & dotnet @arguments
    }

    if ($Scope -eq 'Release') {
        Write-Host 'The CLI integration test uses only the ignored .ai-tmp/workspace/CLI/integration/ workspace.' -ForegroundColor DarkGray
        $pythonCmd = if (Get-Command python -ErrorAction SilentlyContinue) {
            'python'
        }
        elseif (Get-Command py -ErrorAction SilentlyContinue) {
            'py'
        }
        else {
            throw "Python executable ('python' or 'py') was not found on PATH."
        }
        Invoke-VerificationStep -Name 'Run CLI real-sample integration tests' -Action {
            & $pythonCmd $cliIntegrationScript --phases split
        }
    }

    Write-Host "`nVerification gate passed: $Scope ($Configuration x64)." -ForegroundColor Green
}
finally {
    Pop-Location
}
