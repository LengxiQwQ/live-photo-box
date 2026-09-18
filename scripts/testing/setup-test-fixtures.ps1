[CmdletBinding()]
param(
    [string]$TargetDirectory,
    [string]$SourceDirectory,
    [string]$ArchivePath,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..\..'))
$manifestPath = Join-Path $projectRoot 'tests\fixtures\realsamples-manifest.json'

function Stop-FixtureSetup([string]$Message) {
    throw "[Fixtures] $Message"
}

function Get-AbsolutePath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-RequiredSamples($Manifest) {
    return @($Manifest.samples | Where-Object { $_.required -eq $true })
}

function Test-RequiredCorpus([string]$Root, $Samples, [string]$Label) {
    $missing = [System.Collections.Generic.List[string]]::new()
    $sizeMismatch = [System.Collections.Generic.List[string]]::new()
    $hashMismatch = [System.Collections.Generic.List[string]]::new()

    foreach ($sample in $Samples) {
        $path = Join-Path $Root $sample.filename
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $missing.Add($sample.filename)
            continue
        }

        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne [int64]$sample.byteSize) {
            $sizeMismatch.Add("$($sample.filename) (expected $($sample.byteSize), got $($item.Length))")
            continue
        }

        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, [string]$sample.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            $hashMismatch.Add("$($sample.filename) (expected $($sample.sha256), got $actualHash)")
        }
    }

    if ($missing.Count -ne 0 -or $sizeMismatch.Count -ne 0 -or $hashMismatch.Count -ne 0) {
        $parts = [System.Collections.Generic.List[string]]::new()
        if ($missing.Count -ne 0) { $parts.Add("missing: $($missing -join ', ')") }
        if ($sizeMismatch.Count -ne 0) { $parts.Add("size mismatch: $($sizeMismatch -join '; ')") }
        if ($hashMismatch.Count -ne 0) { $parts.Add("SHA-256 mismatch: $($hashMismatch -join '; ')") }
        Stop-FixtureSetup "$Label is not the required verified corpus; $($parts -join ' | ')"
    }
}

function Test-ArchiveLayout([string]$Path, $Samples) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        $entryNames = @($entries | ForEach-Object { $_.FullName })
        if ($entryNames | Where-Object { $_ -ne [System.IO.Path]::GetFileName($_) }) {
            Stop-FixtureSetup 'Archive layout must be flat-root; nested paths are forbidden.'
        }
        $duplicates = @($entryNames | Group-Object | Where-Object { $_.Count -ne 1 } | Select-Object -ExpandProperty Name)
        if ($duplicates.Count -ne 0) {
            Stop-FixtureSetup "Archive contains duplicate entries: $($duplicates -join ', ')"
        }

        $expected = @($Samples | ForEach-Object { [string]$_.filename } | Sort-Object)
        $actual = @($entryNames | Sort-Object)
        $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)
        if ($difference.Count -ne 0) {
            $rendered = $difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
            Stop-FixtureSetup "Archive entries do not exactly match the manifest allow-list: $($rendered -join '; ')"
        }
    }
    finally {
        $zip.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Stop-FixtureSetup "Canonical manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$samples = Get-RequiredSamples $manifest
if ($samples.Count -eq 0) {
    Stop-FixtureSetup 'Canonical manifest contains no required samples.'
}

if (-not $TargetDirectory) {
    $TargetDirectory = Join-Path $projectRoot 'tests\fixtures\realsamples'
}
$resolvedTarget = Get-AbsolutePath $TargetDirectory

if ($VerifyOnly) {
    if (-not (Test-Path -LiteralPath $resolvedTarget -PathType Container)) {
        Stop-FixtureSetup "Corpus root does not exist: $resolvedTarget"
    }
    Test-RequiredCorpus $resolvedTarget $samples 'Existing corpus'
    Write-Host "Corpus version: $($manifest.corpus.version)" -ForegroundColor Green
    Write-Host "Root: $resolvedTarget"
    Write-Host "Required samples: $($samples.Count)"
    Write-Host "Verified: $($samples.Count)"
    Write-Host 'Missing: 0'
    Write-Host 'Hash mismatch: 0'
    Write-Host 'Size mismatch: 0'
    exit 0
}

$sourceKind = $null
$resolvedSource = $null
$resolvedArchive = $null
if ($PSBoundParameters.ContainsKey('SourceDirectory')) {
    $resolvedSource = Get-AbsolutePath $SourceDirectory
    if (-not (Test-Path -LiteralPath $resolvedSource -PathType Container)) {
        Stop-FixtureSetup "Explicit -SourceDirectory does not exist: $resolvedSource"
    }
    $sourceKind = 'explicit source directory'
}
else {
    if (-not $ArchivePath -and $env:LIVEPHOTOBOX_SAMPLES_ARCHIVE) {
        $ArchivePath = $env:LIVEPHOTOBOX_SAMPLES_ARCHIVE
    }
    if ($ArchivePath) {
        $resolvedArchive = Get-AbsolutePath $ArchivePath
        if (-not (Test-Path -LiteralPath $resolvedArchive -PathType Leaf)) {
            Stop-FixtureSetup "Verifier-supplied archive does not exist: $resolvedArchive"
        }
        $sourceKind = 'verifier-supplied archive'
    }
    elseif (Test-Path -LiteralPath $resolvedTarget -PathType Container) {
        $sourceKind = 'existing verified cache'
        $resolvedSource = $resolvedTarget
    }
    else {
        Stop-FixtureSetup 'No source was supplied. Use -SourceDirectory, -ArchivePath, or LIVEPHOTOBOX_SAMPLES_ARCHIVE. No developer designs directory or automatic download is used.'
    }
}

if ($resolvedArchive) {
    $archiveIdentity = $manifest.corpus.archive
    $archiveItem = Get-Item -LiteralPath $resolvedArchive
    if ($archiveItem.Name -ne [string]$archiveIdentity.filename) {
        Stop-FixtureSetup "Archive filename mismatch (expected $($archiveIdentity.filename), got $($archiveItem.Name))."
    }
    if ($archiveItem.Length -ne [int64]$archiveIdentity.byteSize) {
        Stop-FixtureSetup "Archive size mismatch (expected $($archiveIdentity.byteSize), got $($archiveItem.Length))."
    }
    $archiveHash = (Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash
    if (-not [string]::Equals($archiveHash, [string]$archiveIdentity.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-FixtureSetup "Archive SHA-256 mismatch (expected $($archiveIdentity.sha256), got $archiveHash)."
    }

    $staging = "$resolvedTarget.provisioning-staging"
    if (Test-Path -LiteralPath $staging) {
        Stop-FixtureSetup "Provisioning staging directory already exists: $staging"
    }
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        Test-ArchiveLayout $resolvedArchive $samples
        Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $staging
        Test-RequiredCorpus $staging $samples 'Extracted archive'
        $resolvedSource = $staging
    }
    catch {
        throw
    }
}

try {
    Test-RequiredCorpus $resolvedSource $samples "Source ($sourceKind)"
    if (-not (Test-Path -LiteralPath $resolvedTarget)) {
        New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
    }

    foreach ($sample in $samples) {
        $targetPath = Join-Path $resolvedTarget $sample.filename
        if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
            Test-RequiredCorpus $resolvedTarget @($sample) 'Existing target file'
        }
    }

    foreach ($sample in $samples) {
        $targetPath = Join-Path $resolvedTarget $sample.filename
        if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
            Copy-Item -LiteralPath (Join-Path $resolvedSource $sample.filename) -Destination $targetPath
        }
    }

    Test-RequiredCorpus $resolvedTarget $samples 'Provisioned corpus'
    Write-Host "Corpus version: $($manifest.corpus.version)" -ForegroundColor Green
    Write-Host "Source: $sourceKind"
    Write-Host "Root: $resolvedTarget"
    Write-Host "Required samples: $($samples.Count)"
    Write-Host "Verified: $($samples.Count)"
    Write-Host 'Missing: 0'
    Write-Host 'Hash mismatch: 0'
    Write-Host 'Size mismatch: 0'
    Write-Host "Set LIVEPHOTOBOX_TEST_SAMPLES_DIR to '$resolvedTarget' before running tests when this is not tests/fixtures/realsamples."
}
finally {
    if ($resolvedArchive -and (Test-Path -LiteralPath "$resolvedTarget.provisioning-staging")) {
        Remove-Item -LiteralPath "$resolvedTarget.provisioning-staging" -Recurse -Force
    }
}
