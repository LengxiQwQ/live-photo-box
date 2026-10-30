<#
.SYNOPSIS
    Runs the repository's repeatable verification gates without packaging release artifacts.

.DESCRIPTION
    Build   restores and builds the public solution with Visual Studio MSBuild (Native + GUI + CLI + test assemblies; no test execution).
    Fast    is a historical broad-check name: builds Native + GUI + CLI and runs Core/CLI tests.
    Full    currently runs the same checks as Fast; neither is a targeted development check or whole-product acceptance.
    Release adds the existing split-only real-sample CLI integration workflow after those checks; it does not prove P9 conversion/device acceptance.
    Build is the safe default. Select test scopes explicitly; use targeted build/CLI commands for a completed development unit.

    This script never calls build-release.ps1 and never deletes publish/. The CLI
    integration test owns and cleans only its ignored .ai-tmp/workspace/CLI/integration/ workspace.
#>
[CmdletBinding()]
param(
    [ValidateSet('Build', 'Fast', 'Full', 'Release')]
    [string]$Scope = 'Build',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

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
$nativeBuildScript = Join-Path $projectRoot 'scripts\native\build-native.ps1'
$coreTests = Join-Path $projectRoot 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj'
$cliTests = Join-Path $projectRoot 'tests\LivePhotoBox.CLI.Tests\LivePhotoBox.CLI.Tests.csproj'
$cliIntegrationScript = Join-Path $projectRoot 'scripts\testing\run-cli-integration-test.py'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$realSampleTestFilter = if ($env:CI -eq 'true') { 'Category!=RealSamples' } else { $null }
$ciTrackedProjects = @(
    'LivePhotoBox\LivePhotoBox.csproj',
    'LivePhotoBox.CLI\LivePhotoBox.CLI.csproj',
    'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj',
    'tests\LivePhotoBox.CLI.Tests\LivePhotoBox.CLI.Tests.csproj',
    'tests\LivePhotoBox.UITests\LivePhotoBox.UITests.csproj'
)

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
    Invoke-VerificationStep -Name "Build Native ($Configuration x64) and run ABI smoke tests" -Action {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeBuildScript `
            -Configuration $Configuration `
            -Architecture x64 `
            -RunTests
    }

    Invoke-VerificationStep -Name "Build GUI ($Configuration x64)" -Action {
        & dotnet build 'LivePhotoBox\LivePhotoBox.csproj' `
            -c $Configuration `
            -p:Platform=x64 `
            -p:SkipNativeBuild=true `
            -p:EnableMsixTooling=false `
            --no-restore `
            --nologo
    }

    Invoke-VerificationStep -Name "Build CLI ($Configuration x64)" -Action {
        & dotnet build 'LivePhotoBox.CLI\LivePhotoBox.CLI.csproj' `
            -c $Configuration `
            -p:Platform=x64 `
            -p:SkipNativeBuild=true `
            --no-restore `
            --nologo
    }

    if ($Scope -eq 'Build') {
        Write-Host "`nVerification gate passed: $Scope ($Configuration x64)." -ForegroundColor Green
        return
    }

    Invoke-VerificationStep -Name "Run Core tests ($Configuration x64)" -Action {
        $arguments = @(
            'test', $coreTests,
            '-c', $Configuration,
            '-p:Platform=x64',
            '-p:SkipNativeBuild=true',
            '--no-restore',
            '--nologo'
        )
        if ($realSampleTestFilter) {
            Write-Host 'CI excludes [Category=RealSamples]; the local Release gate still runs them.' -ForegroundColor DarkGray
            $arguments += @('--filter', $realSampleTestFilter)
        }
        & dotnet @arguments
    }

    Invoke-VerificationStep -Name "Run CLI tests ($Configuration x64)" -Action {
        $arguments = @(
            'test', $cliTests,
            '-c', $Configuration,
            '-p:Platform=x64',
            '-p:SkipNativeBuild=true',
            '--no-restore',
            '--nologo'
        )
        if ($realSampleTestFilter) {
            $arguments += @('--filter', $realSampleTestFilter)
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
