# UTF-8 BOM
[CmdletBinding()]
param(
    [string]$EvidenceRoot,
    [Parameter(Mandatory)]
    [string]$ValidatorJson
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$workspaceRoot = Join-Path $projectRoot '.ai-tmp\workspace'
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = Join-Path $workspaceRoot 'p2-real-device-validation' }

$resolvedWorkspaceRoot = [IO.Path]::GetFullPath($workspaceRoot).TrimEnd('\') + '\'
$resolvedEvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (-not $resolvedEvidenceRoot.StartsWith($resolvedWorkspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "EvidenceRoot must remain under $workspaceRoot; received $resolvedEvidenceRoot."
}

New-Item -ItemType Directory -Force -Path $resolvedEvidenceRoot | Out-Null
$samplesDir = Join-Path $projectRoot 'designs\各个机型测试'
$testsProject = Join-Path $projectRoot 'tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj'
$testResultsDir = Join-Path $resolvedEvidenceRoot 'test-results'
$listLog = Join-Path $resolvedEvidenceRoot 'discovery.log'
$testLog = Join-Path $resolvedEvidenceRoot 'execution.log'
$beforePath = Join-Path $resolvedEvidenceRoot 'samples-before.json'
$afterPath = Join-Path $resolvedEvidenceRoot 'samples-after.json'
$summaryPath = Join-Path $resolvedEvidenceRoot 'gate-summary.json'
$reportPath = Join-Path $resolvedEvidenceRoot 'gate-report.md'
$filter = 'FullyQualifiedName~ExtractorAuthorityTests|FullyQualifiedName~ExtractorW2RealSampleTests|FullyQualifiedName~HeifCompoundGainMapTests|FullyQualifiedName~ExtractorW4AdversarialTests|FullyQualifiedName~ExtractorCleanupSourceTransactionTests|FullyQualifiedName~ExtractorRealSampleTests|FullyQualifiedName~CleanerArtifactIdentityTests'
$requiredSamples = @(
    'oppo.jpg', 'vivo.jpg', '一加.jpg', '一加-改了封面照片.jpg', '三星.jpg', '三星.heic',
    '华为-Mate80.jpg', '华为Mate80.heic', '小米.jpg', '红米老款-GV1.JPG', '荣耀.jpg',
    'vivo双文件.jpg', 'vivo双文件.mp4', '苹果-双文件.JPG', '苹果-双文件.MOV',
    '苹果双文件.HEIC', '苹果双文件.MOV'
)

function Get-SourceInventory {
    param([string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw "Required real-sample directory is missing: $Directory" }
    $files = @(Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name)
    if ($files.Count -eq 0) { throw "Required real-sample directory is empty: $Directory" }
    return @($files | ForEach-Object {
        [PSCustomObject]@{ Name = $_.Name; Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}

function Get-TrxCounters {
    param([string]$TrxPath)
    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) { throw "Required TRX evidence is missing: $TrxPath" }
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $results = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    if ($results.Count -eq 0) { throw "TRX contains no UnitTestResult entries: $TrxPath" }
    $outcomes = @($results | ForEach-Object { $_.outcome })
    return [PSCustomObject]@{
        Executed = $results.Count
        Passed = @($outcomes | Where-Object { $_ -eq 'Passed' }).Count
        Failed = @($outcomes | Where-Object { $_ -in @('Failed', 'Error', 'Timeout', 'Aborted', 'Inconclusive') }).Count
        Skipped = @($outcomes | Where-Object { $_ -in @('NotExecuted', 'Warning', 'Disconnected') }).Count
        Other = @($outcomes | Where-Object { $_ -notin @('Passed', 'Failed', 'Error', 'Timeout', 'Aborted', 'Inconclusive', 'NotExecuted', 'Warning', 'Disconnected') }).Count
    }
}

$before = Get-SourceInventory $samplesDir
$actualNames = @($before | ForEach-Object Name)
$missingSampleNames = @($requiredSamples | Where-Object { $_ -notin $actualNames })
if ($missingSampleNames.Count -ne 0) { throw "Required P2 real samples are missing: $($missingSampleNames -join ', ')" }
$validatorPath = [IO.Path]::GetFullPath($ValidatorJson)
if (-not (Test-Path -LiteralPath $validatorPath -PathType Leaf)) { throw "Required independent validator JSON is missing: $validatorPath" }
try { $validator = Get-Content -LiteralPath $validatorPath -Raw | ConvertFrom-Json } catch { throw "Required independent validator JSON is malformed: $validatorPath" }
if ($validator.Passed -ne $true) { throw "Required independent validator did not pass: $validatorPath" }
$before | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $beforePath -Encoding utf8

Write-Host '[P2 gate] Discovering targeted tests...' -ForegroundColor Cyan
$discoveryOutput = & dotnet test $testsProject -c Release -p:Platform=x64 --no-build --filter $filter --list-tests 2>&1
$discoveryExit = $LASTEXITCODE
$discoveryOutput | Set-Content -LiteralPath $listLog -Encoding utf8
if ($discoveryExit -ne 0) { throw "P2 targeted test discovery failed with exit code $discoveryExit. See $listLog" }
$discovered = @($discoveryOutput | Where-Object { $_ -match '^\s*LivePhotoBox\.Core\.Tests\.' }).Count
if ($discovered -eq 0) { throw "P2 targeted test discovery produced zero tests. Filter: $filter" }

New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
$trxName = 'p2-targeted.trx'
Write-Host "[P2 gate] Executing $discovered targeted tests..." -ForegroundColor Cyan
$executionOutput = & dotnet test $testsProject -c Release -p:Platform=x64 --no-build --filter $filter --logger "trx;LogFileName=$trxName" --logger 'console;verbosity=normal' --results-directory $testResultsDir 2>&1
$executionExit = $LASTEXITCODE
$executionOutput | Set-Content -LiteralPath $testLog -Encoding utf8

$trxPath = Join-Path $testResultsDir $trxName
$counters = Get-TrxCounters $trxPath
$after = Get-SourceInventory $samplesDir
$after | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $afterPath -Encoding utf8
$beforeKeys = @($before | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.Sha256)" })
$afterKeys = @($after | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.Sha256)" })
$sampleDiff = @(Compare-Object -ReferenceObject $beforeKeys -DifferenceObject $afterKeys)
$sourcesUnchanged = $sampleDiff.Count -eq 0
$missing = $missingSampleNames.Count
$passed = $executionExit -eq 0 -and $counters.Executed -eq $discovered -and $counters.Passed -eq $discovered -and $counters.Failed -eq 0 -and $counters.Skipped -eq 0 -and $counters.Other -eq 0 -and $missing -eq 0 -and $sourcesUnchanged

$summary = [ordered]@{
    Schema = 'LivePhotoBox.P2.TargetedGate.v1'; TimestampUtc = [DateTime]::UtcNow.ToString('o'); Filter = $filter; EvidenceRoot = $resolvedEvidenceRoot
    Samples = [ordered]@{ Directory = $samplesDir; BeforeCount = $before.Count; AfterCount = $after.Count; Missing = $missing; MissingNames = $missingSampleNames; Unchanged = $sourcesUnchanged; Differences = @($sampleDiff | ForEach-Object { $_.InputObject }) }
    IndependentValidator = [ordered]@{ Path = $validatorPath; Passed = $validator.Passed }
    Commands = [ordered]@{ DiscoveryExitCode = $discoveryExit; ExecutionExitCode = $executionExit }
    Tests = [ordered]@{ Discovered = $discovered; Executed = $counters.Executed; Passed = $counters.Passed; Failed = $counters.Failed; Skipped = $counters.Skipped; Other = $counters.Other; TrxPath = $trxPath }
    Passed = $passed
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$status = if ($passed) { 'PASS' } else { 'FAIL' }
@(
    '# Live Photo Box · P2 Targeted Real-Sample Gate', '', "Status: **$status**", '', '| Metric | Value |', '|---|---:|',
    "| Samples before / after | $($before.Count) / $($after.Count) |", "| Samples unchanged | $sourcesUnchanged |",
    "| Tests discovered / executed | $discovered / $($counters.Executed) |", "| Passed / failed / skipped / other | $($counters.Passed) / $($counters.Failed) / $($counters.Skipped) / $($counters.Other) |",
    "| Discovery / execution exit code | $discoveryExit / $executionExit |", '', "- Filter: ``$filter``", "- TRX: ``$trxPath``", "- Source inventories: ``$beforePath`` and ``$afterPath``", "- Machine-readable summary: ``$summaryPath``"
) | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Host "[P2 gate] $status — discovered=$discovered executed=$($counters.Executed) passed=$($counters.Passed) failed=$($counters.Failed) skipped=$($counters.Skipped) missing=$missing unchanged=$sourcesUnchanged" -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
if (-not $passed) { exit 1 }
