<#
.SYNOPSIS
    Runs the repository's repeatable verification gates without packaging release artifacts.

.DESCRIPTION
    Fast    restores, builds Native + GUI + CLI, and runs Core/CLI tests.
    Full    adds a complete solution build through Visual Studio MSBuild.
    Release adds the real-sample CLI integration workflow after the Full gate.

    This script never calls build-release.ps1 and never deletes publish/. The CLI
    integration test owns and cleans only its ignored cli-integration-test/ workspace.
#>
[CmdletBinding()]
param(
    [ValidateSet('Fast', 'Full', 'Release')]
    [string]$Scope = 'Fast',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solutionPath = Join-Path $projectRoot 'Live Photo Box.sln'
$nativeBuildScript = Join-Path $projectRoot 'scripts\native\build-native.ps1'
$coreTests = Join-Path $projectRoot 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj'
$cliTests = Join-Path $projectRoot 'tests\LivePhotoBox.CLI.Tests\LivePhotoBox.CLI.Tests.csproj'
$cliIntegrationScript = Join-Path $projectRoot 'scripts\testing\run-cli-integration-test.py'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$coreTestFilter = if ($env:CI -eq 'true') { 'Category!=RealSamples' } else { $null }
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

    Invoke-VerificationStep -Name 'Restore tracked projects' -Action {
        if ($env:CI -eq 'true') {
            # The local solution intentionally includes private stress/benchmark projects that
            # are Git-ignored. GitHub must restore only projects present in a clean checkout.
            foreach ($project in $ciTrackedProjects) {
                & dotnet restore $project --nologo
                if ($LASTEXITCODE -ne 0) { return }
            }
        }
        else {
            & dotnet restore $solutionPath --nologo
        }
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

    Invoke-VerificationStep -Name "Run Core tests ($Configuration x64)" -Action {
        $arguments = @(
            'test', $coreTests,
            '-c', $Configuration,
            '-p:Platform=x64',
            '-p:SkipNativeBuild=true',
            '--no-restore',
            '--nologo'
        )
        if ($coreTestFilter) {
            Write-Host 'CI excludes [Category=RealSamples]; the local Release gate still runs them.' -ForegroundColor DarkGray
            $arguments += @('--filter', $coreTestFilter)
        }
        & dotnet @arguments
    }

    Invoke-VerificationStep -Name "Run CLI tests ($Configuration x64)" -Action {
        & dotnet test $cliTests `
            -c $Configuration `
            -p:Platform=x64 `
            -p:SkipNativeBuild=true `
            --no-restore `
            --nologo
    }

    if ($Scope -in @('Full', 'Release')) {
        if (-not (Test-Path -LiteralPath $vswhere)) {
            throw 'Visual Studio Installer (vswhere.exe) is required for the Full gate.'
        }

        $visualStudioPath = (& $vswhere -latest -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath | Select-Object -First 1).Trim()
        $desktopMsbuild = Join-Path $visualStudioPath 'MSBuild\Current\Bin\MSBuild.exe'
        if ([string]::IsNullOrWhiteSpace($visualStudioPath) -or -not (Test-Path -LiteralPath $desktopMsbuild)) {
            throw 'Visual Studio MSBuild with the C++ workload is required for the Full gate.'
        }

        # dotnet test cannot import the solution's .vcxproj. Core and CLI tests
        # have already run above. A clean CI checkout excludes local-only stress projects,
        # so it verifies the tracked desktop product projects individually instead.
        if ($env:CI -eq 'true') {
            foreach ($project in @('LivePhotoBox\LivePhotoBox.csproj', 'LivePhotoBox.CLI\LivePhotoBox.CLI.csproj')) {
                Invoke-VerificationStep -Name "Build tracked project with Visual Studio MSBuild: $project ($Configuration x64)" -Action {
                    & $desktopMsbuild $project `
                        /nologo `
                        /m `
                        /t:Build `
                        "/p:Configuration=$Configuration" `
                        /p:Platform=x64 `
                        /p:SkipNativeBuild=true `
                        /v:minimal
                }
            }
        }
        else {
            Invoke-VerificationStep -Name "Build complete solution with Visual Studio MSBuild ($Configuration x64)" -Action {
                & $desktopMsbuild $solutionPath `
                    /nologo `
                    /m `
                    /t:Build `
                    "/p:Configuration=$Configuration" `
                    /p:Platform=x64 `
                    /p:SkipNativeBuild=true `
                    /v:minimal
            }
        }
    }

    if ($Scope -eq 'Release') {
        Write-Host 'The CLI integration test uses only the ignored cli-integration-test/ workspace.' -ForegroundColor DarkGray
        Invoke-VerificationStep -Name 'Run CLI real-sample integration tests' -Action {
            & python $cliIntegrationScript
        }
    }

    Write-Host "`nVerification gate passed: $Scope ($Configuration x64)." -ForegroundColor Green
}
finally {
    Pop-Location
}
