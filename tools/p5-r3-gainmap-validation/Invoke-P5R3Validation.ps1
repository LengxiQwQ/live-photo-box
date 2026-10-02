[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path $RepositoryRoot).Path
$matrixPath = Join-Path $repo "tools\p5-r3-gainmap-validation\matrix-v1.json"
$policyPath = Join-Path $repo "tools\p5-r3-gainmap-validation\reference-policy-v1.json"
$validatorPath = Join-Path $repo "tools\p5-r3-gainmap-validation\validate_p5_r3.py"
$sampleRoot = Join-Path $repo ".ai-tmp\cache\samples\P5-R3-v1"
$workspaceRoot = Join-Path $repo ".ai-tmp\workspace\P5-R3"
$artifactRoot = Join-Path $workspaceRoot "formal-artifacts"
$resultRoot = Join-Path $workspaceRoot "formal-test-results"
$testLog = Join-Path $workspaceRoot "iteration-4-formal-real-samples.log"
$independentLog = Join-Path $workspaceRoot "iteration-4-formal-independent-validation.log"
$independentWork = Join-Path $workspaceRoot "formal-independent-validation"
$runnerReport = Join-Path $workspaceRoot "iteration-4-formal-runner.json"
$evidenceRoot = Join-Path $repo ".ai-tmp\evidence\P5-R3"
$expected = @{
    "荣耀.jpg" = "970658F835ADD139247694A67803E21A0CA0421290A3A2D269D52D970CABF759"
    "vivo.jpg" = "631CF8DAC983A9F58FC1D8CB63AAF8CC0569C45B80F046F1B995936D608320B9"
    "苹果双文件.HEIC" = "868F29D1408D090193D04EBE5C71FEA139705381B9C1B8673C40C62E1A456998"
    "三星.heic" = "DBFB8AD846A16291B0B09599FCF588A3E6C20D58B96782E357D5652E3EC544C4"
}

foreach ($path in @($matrixPath, $policyPath, $validatorPath, $sampleRoot, $workspaceRoot)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required R3 validation input is missing: $path" }
}
foreach ($path in @($artifactRoot, $resultRoot, $testLog, $independentLog, $independentWork, $runnerReport, $evidenceRoot)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite existing P5-R3 validation output: $path" }
}

$sourceHashes = @{}
foreach ($name in $expected.Keys) {
    $path = Join-Path $sampleRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required canonical source sample is missing: $path" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($hash -ne $expected[$name]) { throw "Canonical source hash mismatch for ${name}: $hash" }
    $sourceHashes[$name] = $hash
}

$appleCompanion = Join-Path $sampleRoot "苹果双文件.MOV"
if (-not (Test-Path -LiteralPath $appleCompanion -PathType Leaf)) {
    throw "The Apple neutral-pipeline fixture must also be cached at the fixed P5-R3-v1 path: $appleCompanion"
}
$appleCompanionHashBefore = (Get-FileHash -LiteralPath $appleCompanion -Algorithm SHA256).Hash.ToUpperInvariant()
if ($appleCompanionHashBefore -ne "D47C0E8FC052393E91A6ECB0AA941AFB20758CD244220943E3C06C52BC2DF381") {
    throw "Cached Apple companion sample hash mismatch: $appleCompanionHashBefore"
}

New-Item -ItemType Directory -Path $artifactRoot | Out-Null
$testProject = Join-Path $repo "tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj"
$filter = "FullyQualifiedName~NeutralPipeline_HeicGainMapToJpeg|FullyQualifiedName~NeutralPipeline_JpegGainMapToHeic"
$testArgs = @(
    "test", $testProject,
    "--configuration", "Debug",
    "--no-restore",
    "--results-directory", $resultRoot,
    "--logger", "trx;LogFileName=P5-R3-CanonicalRealSamples.trx",
    "--filter", $filter,
    "-p:Platform=x64",
    "-p:SkipNativeBuild=true"
)
$oldSampleRoot = $env:LIVEPHOTOBOX_TEST_SAMPLES_DIR
$oldArtifactRoot = $env:LPB_P5_R3_OUTPUT_DIR
$env:LIVEPHOTOBOX_TEST_SAMPLES_DIR = $sampleRoot
$env:LPB_P5_R3_OUTPUT_DIR = $artifactRoot
$testStart = Get-Date -Format "yyyy-MM-ddTHH:mm:ssK"
$testExit = 1
try {
    Push-Location $repo
    try {
        & dotnet @testArgs *> $testLog
        $testExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:LIVEPHOTOBOX_TEST_SAMPLES_DIR = $oldSampleRoot
    $env:LPB_P5_R3_OUTPUT_DIR = $oldArtifactRoot
}

$trxPath = Join-Path $resultRoot "P5-R3-CanonicalRealSamples.trx"
$trxCounters = $null
if (Test-Path -LiteralPath $trxPath) {
    [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -ne $counters) {
        $trxCounters = @{}
        foreach ($attribute in $counters.Attributes) { $trxCounters[$attribute.Name] = [int]$attribute.Value }
    }
}
$expectedArtifacts = @(
    "honor-jpeg-to-heic.heic",
    "vivo-jpeg-to-heic.heic",
    "apple-heic-to-jpeg.jpg",
    "samsung-heic-to-jpeg.jpg"
)
$artifactNames = @(Get-ChildItem -LiteralPath $artifactRoot -File | Select-Object -ExpandProperty Name | Sort-Object)
$testPass = $testExit -eq 0 -and
    $null -ne $trxCounters -and
    $trxCounters["total"] -eq 4 -and
    $trxCounters["passed"] -eq 4 -and
    $trxCounters["failed"] -eq 0 -and
    $trxCounters["notExecuted"] -eq 0 -and
    (@($artifactNames | Where-Object { $_ -in $expectedArtifacts }).Count -eq 4) -and
    $artifactNames.Count -eq 4

$sourceHashesAfter = @{}
foreach ($name in $expected.Keys) {
    $sourceHashesAfter[$name] = (Get-FileHash -LiteralPath (Join-Path $sampleRoot $name) -Algorithm SHA256).Hash.ToUpperInvariant()
}
$appleCompanionHashAfter = (Get-FileHash -LiteralPath $appleCompanion -Algorithm SHA256).Hash.ToUpperInvariant()
if ($appleCompanionHashAfter -ne $appleCompanionHashBefore) { $testPass = $false }
foreach ($name in $expected.Keys) {
    if ($sourceHashesAfter[$name] -ne $sourceHashes[$name]) { $testPass = $false }
}

$summary = [ordered]@{
    schemaVersion = 1
    suiteId = "P5-R3-v1"
    startedAt = $testStart
    testCommand = "dotnet $($testArgs -join ' ')"
    testExitCode = $testExit
    trx = $trxPath
    trxCounters = $trxCounters
    expectedRoutes = 4
    exportedArtifacts = $artifactNames
    sourceHashesBefore = $sourceHashes
    sourceHashesAfter = $sourceHashesAfter
    appleCompanionSha256Before = $appleCompanionHashBefore
    appleCompanionSha256After = $appleCompanionHashAfter
    sourceInputsUnchanged = ($appleCompanionHashAfter -eq $appleCompanionHashBefore -and
        (@($expected.Keys | Where-Object { $sourceHashes[$_] -ne $sourceHashesAfter[$_] }).Count -eq 0))
    zeroSkip = ($null -ne $trxCounters -and $trxCounters["notExecuted"] -eq 0)
    pass = $testPass
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runnerReport -Encoding utf8

$validatorArgs = @(
    $validatorPath,
    "--matrix", $matrixPath,
    "--policy", $policyPath,
    "--sample-dir", $sampleRoot,
    "--artifact-dir", $artifactRoot,
    "--work-dir", $independentWork,
    "--evidence-dir", $evidenceRoot
)
$validatorExit = 1
if ($testPass) {
    Push-Location $repo
    try {
        & python @validatorArgs *> $independentLog
        $validatorExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
elseif (-not (Test-Path -LiteralPath $evidenceRoot)) {
    New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
    @{ schemaVersion = 1; suiteId = "P5-R3-v1"; status = "FAIL"; failure = "Canonical RealSample conversion tests did not pass zero-skip evidence gate."; test = $summary } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot "run-report.json") -Encoding utf8
}

if (-not (Test-Path -LiteralPath $evidenceRoot)) {
    New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
}
$executionRoot = Join-Path $evidenceRoot "execution"
New-Item -ItemType Directory -Path $executionRoot | Out-Null
Copy-Item -LiteralPath $matrixPath -Destination (Join-Path $executionRoot "matrix-v1.json")
Copy-Item -LiteralPath $policyPath -Destination (Join-Path $executionRoot "reference-policy-v1.json")
Copy-Item -LiteralPath $validatorPath -Destination (Join-Path $executionRoot "validate_p5_r3.py")
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $executionRoot "Invoke-P5R3Validation.ps1")
Copy-Item -LiteralPath $testLog -Destination (Join-Path $executionRoot "dotnet-test.log")
if (Test-Path -LiteralPath $independentLog) { Copy-Item -LiteralPath $independentLog -Destination (Join-Path $executionRoot "independent-validator.log") }
Copy-Item -LiteralPath $runnerReport -Destination (Join-Path $executionRoot "runner-summary.json")
if (Test-Path -LiteralPath $trxPath) { Copy-Item -LiteralPath $trxPath -Destination (Join-Path $executionRoot "real-samples.trx") }
foreach ($artifact in Get-ChildItem -LiteralPath $artifactRoot -File) {
    $route = switch ($artifact.Name) {
        "honor-jpeg-to-heic.heic" { "honor-jpeg-to-heic" }
        "vivo-jpeg-to-heic.heic" { "vivo-jpeg-to-heic" }
        "apple-heic-to-jpeg.jpg" { "apple-heic-to-jpeg" }
        "samsung-heic-to-jpeg.jpg" { "samsung-heic-to-jpeg" }
        default { throw "Unexpected R3 artifact was exported: $($artifact.Name)" }
    }
    $routeDirectory = Join-Path $evidenceRoot $route
    if (-not (Test-Path -LiteralPath $routeDirectory)) { New-Item -ItemType Directory -Path $routeDirectory | Out-Null }
    Copy-Item -LiteralPath $artifact.FullName -Destination (Join-Path $routeDirectory ("converted-output" + $artifact.Extension))
}

$evidenceStatus = $false
$runReportPath = Join-Path $evidenceRoot "run-report.json"
if (Test-Path -LiteralPath $runReportPath) {
    $evidenceReport = Get-Content -LiteralPath $runReportPath -Raw | ConvertFrom-Json
    $evidenceStatus = $evidenceReport.status -eq "PASS"
}
$finalPass = $testPass -and $validatorExit -eq 0 -and $evidenceStatus
Write-Host "P5-R3 independent validation: $(if ($finalPass) { 'PASS' } else { 'FAIL' })"
Write-Host "Evidence: $evidenceRoot"
Write-Host "RealSample tests: $testLog (exit $testExit, zero skips required)"
Write-Host "Independent validator exit: $validatorExit"
if (-not $finalPass) { exit 1 }
