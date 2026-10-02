[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Formal')][string] $Mode = 'Preflight',
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$repo = [System.IO.Path]::GetFullPath($RepositoryRoot)
$toolRoot = Join-Path $repo 'tools\p5-r3-gainmap-validation'
$matrixPath = Join-Path $toolRoot 'matrix-v2.json'
$policyPath = Join-Path $toolRoot 'reference-policy-v1.json'
$structuralPolicyPath = Join-Path $toolRoot 'source-structure-policy-v2.json'
$validatorPath = Join-Path $toolRoot 'validate_p5_r3_v2.py'
$sampleRoot = Join-Path $repo '.ai-tmp\cache\samples\P5-R3-v1'
$evidenceRoot = Join-Path $repo '.ai-tmp\evidence\P5-R3'

if ($Mode -eq 'Preflight') {
    $runRoot = Join-Path $repo '.ai-tmp\workspace\P5-R3\iteration-5-preflight-v2'
    $independentReports = Join-Path $runRoot 'independent-results'
}
else {
    $runRoot = Join-Path $repo '.ai-tmp\workspace\P5-R3\iteration-5-formal-v2'
    $independentReports = $evidenceRoot
    if (Test-Path -LiteralPath $evidenceRoot) {
        throw "Refusing to overwrite existing formal R3 evidence: $evidenceRoot"
    }
}

$artifactRoot = Join-Path $runRoot 'artifacts'
$resultRoot = Join-Path $runRoot 'test-results'
$validatorWork = Join-Path $runRoot 'independent-work'
$testLog = Join-Path $runRoot 'canonical-real-samples.log'
$validatorLog = Join-Path $runRoot 'independent-validator.json'
$runnerReport = Join-Path $runRoot 'runner-summary.json'
$v1Archive = Join-Path $repo '.ai-tmp\evidence\archive\2026-10-02-P5-R3-v1-failed'
if (Test-Path -LiteralPath $runRoot) {
    throw "Refusing to overwrite existing P5-R3 $Mode workspace: $runRoot"
}
$v1RunReportPath = Join-Path $v1Archive 'run-report.json'
if (-not (Test-Path -LiteralPath $validatorPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $matrixPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $policyPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $structuralPolicyPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sampleRoot -PathType Container) -or
    -not (Test-Path -LiteralPath $v1RunReportPath -PathType Leaf)) {
    throw 'A required P5-R3 v2 validator, profile, sample cache, or archived v1 report is missing.'
}

$matrix = [System.IO.File]::ReadAllText($matrixPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$referencePolicy = [System.IO.File]::ReadAllText($policyPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$structuralPolicy = [System.IO.File]::ReadAllText($structuralPolicyPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$expectedReferencePolicyHash = [string]$structuralPolicy.referencePolicySha256
$actualReferencePolicyHash = (Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($matrix.schemaVersion -ne 2 -or $matrix.suiteId -ne 'P5-R3-v2' -or
    $referencePolicy.schemaVersion -ne 1 -or $structuralPolicy.schemaVersion -ne 2 -or
    $actualReferencePolicyHash -ne $expectedReferencePolicyHash) {
    throw 'The v2 matrix, unchanged frozen reference policy, and source-structure policy do not match their locked profile.'
}
$forensicPath = Join-Path $repo $structuralPolicy.forensicReport.path
if ((Get-FileHash -LiteralPath $forensicPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne
    ([string]$structuralPolicy.forensicReport.sha256).ToUpperInvariant()) {
    throw 'The Vivo forensic report differs from the frozen v2 structural-policy source.'
}
if ((Get-FileHash -LiteralPath $v1RunReportPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne
    ([string]$structuralPolicy.forensicReport.v1RunReportSha256).ToUpperInvariant()) {
    throw 'The archived failed v1 report differs from the frozen v2 structural-policy source.'
}

$sourceHashesBefore = @{}
foreach ($route in $matrix.canonicalRoutes) {
    $samplePath = Join-Path $sampleRoot $route.sourceFile
    if (-not (Test-Path -LiteralPath $samplePath -PathType Leaf)) { throw "Canonical sample missing: $samplePath" }
    $hash = (Get-FileHash -LiteralPath $samplePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($hash -ne ([string]$route.sourceSha256).ToUpperInvariant()) { throw "Canonical sample hash mismatch: $($route.sourceFile)" }
    $sourceHashesBefore[$route.sourceFile] = $hash
}
foreach ($supportingInput in $matrix.supportingInputs) {
    $samplePath = Join-Path $sampleRoot $supportingInput.file
    if (-not (Test-Path -LiteralPath $samplePath -PathType Leaf)) { throw "Supporting input missing: $samplePath" }
    $hash = (Get-FileHash -LiteralPath $samplePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($hash -ne ([string]$supportingInput.sha256).ToUpperInvariant()) { throw "Supporting input hash mismatch: $($supportingInput.file)" }
    $sourceHashesBefore[$supportingInput.file] = $hash
}

New-Item -ItemType Directory -Path $runRoot -Force:$false | Out-Null
New-Item -ItemType Directory -Path $artifactRoot -Force:$false | Out-Null
$testProject = Join-Path $repo 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj'
$filter = 'FullyQualifiedName~NeutralPipeline_HeicGainMapToJpeg|FullyQualifiedName~NeutralPipeline_JpegGainMapToHeic'
$testArgs = @(
    'test', $testProject,
    '--configuration', 'Debug',
    '--no-restore',
    '--results-directory', $resultRoot,
    '--logger', 'trx;LogFileName=P5-R3-V2-CanonicalRealSamples.trx',
    '--filter', $filter,
    '-p:Platform=x64',
    '-p:SkipNativeBuild=true'
)
$previousSamples = $env:LIVEPHOTOBOX_TEST_SAMPLES_DIR
$previousOutput = $env:LPB_P5_R3_OUTPUT_DIR
$env:LIVEPHOTOBOX_TEST_SAMPLES_DIR = $sampleRoot
$env:LPB_P5_R3_OUTPUT_DIR = $artifactRoot
$testStartedAt = Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK'
$testExit = 1
try {
    Push-Location $repo
    try {
        & dotnet @testArgs *> $testLog
        $testExit = $LASTEXITCODE
    }
    finally { Pop-Location }
}
finally {
    $env:LIVEPHOTOBOX_TEST_SAMPLES_DIR = $previousSamples
    $env:LPB_P5_R3_OUTPUT_DIR = $previousOutput
}

$trxPath = Join-Path $resultRoot 'P5-R3-V2-CanonicalRealSamples.trx'
$counters = $null
if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
    [xml]$trx = [System.IO.File]::ReadAllText($trxPath, [System.Text.Encoding]::UTF8)
    $counterNode = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -ne $counterNode) {
        $counters = @{}
        foreach ($attribute in $counterNode.Attributes) { $counters[$attribute.Name] = [int]$attribute.Value }
    }
}
$expectedArtifacts = @($matrix.canonicalRoutes | ForEach-Object { $_.targetFile } | Sort-Object)
$artifactNames = @(Get-ChildItem -LiteralPath $artifactRoot -File | Select-Object -ExpandProperty Name | Sort-Object)
$testPass = $testExit -eq 0 -and $null -ne $counters -and $counters.total -eq 4 -and
    $counters.passed -eq 4 -and $counters.failed -eq 0 -and $counters.notExecuted -eq 0 -and
    $artifactNames.Count -eq 4 -and (@(Compare-Object $expectedArtifacts $artifactNames).Count -eq 0)
$sourceHashesAfter = @{}
foreach ($name in $sourceHashesBefore.Keys) {
    $sourceHashesAfter[$name] = (Get-FileHash -LiteralPath (Join-Path $sampleRoot $name) -Algorithm SHA256).Hash.ToUpperInvariant()
}
$inputsUnchanged = @($sourceHashesBefore.Keys | Where-Object { $sourceHashesBefore[$_] -ne $sourceHashesAfter[$_] }).Count -eq 0
$testPass = $testPass -and $inputsUnchanged

$summary = [ordered]@{
    schemaVersion = 2
    suiteId = $matrix.suiteId
    mode = $Mode
    startedAt = $testStartedAt
    testCommand = "dotnet $($testArgs -join ' ')"
    testExitCode = $testExit
    trx = $trxPath
    trxCounters = $counters
    expectedRoutes = 4
    exportedArtifacts = $artifactNames
    sourceHashesBefore = $sourceHashesBefore
    sourceHashesAfter = $sourceHashesAfter
    sourceInputsUnchanged = $inputsUnchanged
    zeroSkip = ($null -ne $counters -and $counters.notExecuted -eq 0)
    matrixSha256 = (Get-FileHash -LiteralPath $matrixPath -Algorithm SHA256).Hash.ToUpperInvariant()
    referencePolicySha256 = $actualReferencePolicyHash
    structuralPolicySha256 = (Get-FileHash -LiteralPath $structuralPolicyPath -Algorithm SHA256).Hash.ToUpperInvariant()
    validatorSha256 = (Get-FileHash -LiteralPath $validatorPath -Algorithm SHA256).Hash.ToUpperInvariant()
    pass = $testPass
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $runnerReport -Encoding utf8

$validatorExit = 1
$independentReport = $null
if ($testPass) {
    $validatorArgs = @(
        $validatorPath,
        '--matrix', $matrixPath,
        '--policy', $policyPath,
        '--structural-policy', $structuralPolicyPath,
        '--sample-dir', $sampleRoot,
        '--artifact-dir', $artifactRoot,
        '--work-dir', $validatorWork,
        '--evidence-dir', $independentReports
    )
    Push-Location $repo
    try {
        & python -B @validatorArgs *> $validatorLog
        $validatorExit = $LASTEXITCODE
    }
    finally { Pop-Location }
    if (Test-Path -LiteralPath (Join-Path $independentReports 'run-report.json') -PathType Leaf) {
        $independentReport = [System.IO.File]::ReadAllText((Join-Path $independentReports 'run-report.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    }
}

if ($Mode -eq 'Formal') {
    if (-not (Test-Path -LiteralPath $evidenceRoot)) { New-Item -ItemType Directory -Path $evidenceRoot | Out-Null }
    $formalReportPath = Join-Path $evidenceRoot 'run-report.json'
    if (-not (Test-Path -LiteralPath $formalReportPath)) {
        @{ schemaVersion = 2; suiteId = $matrix.suiteId; status = 'FAIL'; failure = 'The independent v2 validator did not produce a run report.'; test = $summary; validatorExitCode = $validatorExit } |
            ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $formalReportPath -Encoding utf8
    }
    if (-not (Test-Path -LiteralPath $validatorLog -PathType Leaf)) {
        'Independent validator was not run because the canonical RealSample zero-skip gate failed.' |
            Set-Content -LiteralPath $validatorLog -Encoding utf8
    }
    if (-not (Test-Path -LiteralPath $testLog -PathType Leaf)) {
        'Canonical RealSample command did not produce a log.' | Set-Content -LiteralPath $testLog -Encoding utf8
    }
    $executionRoot = Join-Path $evidenceRoot 'execution'
    New-Item -ItemType Directory -Path $executionRoot | Out-Null
    foreach ($sourcePath in @($matrixPath, $policyPath, $structuralPolicyPath, $validatorPath, $PSCommandPath, $testLog, $validatorLog, $runnerReport)) {
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $executionRoot ([System.IO.Path]::GetFileName($sourcePath)))
    }
    $forensicEvidenceCopy = Join-Path $executionRoot 'vivo-mpf-forensic-v2.json'
    Copy-Item -LiteralPath $forensicPath -Destination $forensicEvidenceCopy
    if ((Get-FileHash -LiteralPath $forensicEvidenceCopy -Algorithm SHA256).Hash.ToUpperInvariant() -ne
        ([string]$structuralPolicy.forensicReport.sha256).ToUpperInvariant()) {
        throw 'Copied forensic report failed its versioned structural-policy hash check.'
    }
    if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
        Copy-Item -LiteralPath $trxPath -Destination (Join-Path $executionRoot 'real-samples.trx')
    }
    @{ path = '.ai-tmp/evidence/archive/2026-10-02-P5-R3-v1-failed'; status = 'FAIL'; runReportSha256 = $structuralPolicy.forensicReport.v1RunReportSha256 } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $executionRoot 'v1-failed-archive-reference.json') -Encoding utf8
    foreach ($artifact in Get-ChildItem -LiteralPath $artifactRoot -File) {
        $route = $matrix.canonicalRoutes | Where-Object { $_.targetFile -eq $artifact.Name }
        if ($null -eq $route) { throw "Unexpected R3 v2 artifact: $($artifact.Name)" }
        $routeEvidence = Join-Path $evidenceRoot $route.id
        if (-not (Test-Path -LiteralPath $routeEvidence)) { New-Item -ItemType Directory -Path $routeEvidence | Out-Null }
        Copy-Item -LiteralPath $artifact.FullName -Destination (Join-Path $routeEvidence ("converted-output" + $artifact.Extension))
    }
}

$routesPass = $null -ne $independentReport -and $independentReport.status -eq 'PASS' -and
    $independentReport.routesExpected -eq 4 -and $independentReport.routesExecuted -eq 4 -and
    $independentReport.skipped -eq 0 -and @($independentReport.routes | Where-Object { $_.status -ne 'PASS' }).Count -eq 0
$finalPass = $testPass -and $validatorExit -eq 0 -and $routesPass
Write-Host "P5-R3 v2 ${Mode}: $(if ($finalPass) { 'PASS' } else { 'FAIL' })"
Write-Host "Workspace: $runRoot"
if ($Mode -eq 'Formal') { Write-Host "Evidence: $evidenceRoot" }
Write-Host "RealSample tests: $testLog (exit $testExit; total=$($counters.total); skipped=$($counters.notExecuted))"
Write-Host "Independent validator exit: $validatorExit; four routes passed=$routesPass"
if (-not $finalPass) { exit 1 }
