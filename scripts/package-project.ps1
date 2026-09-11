#Requires -Version 5.1
<#
.SYNOPSIS
    将 Live Photo Box 项目源代码、AI 配置、测试工程和非 designs 样本打包为完整 ZIP。

.DESCRIPTION
    默认打包项目中可复用的完整开发上下文：源代码、文档、脚本、测试工程、普通资源、
    项目内 AI 配置（.ai/.agents/.claude/.codex、提示词、skills、agents）以及 tests/fixtures/realsamples 原始样本媒体。

    明确排除：.git、.ai-tmp、designs、bin/obj/.vs/AppPackages、构建缓存、测试结果、
    cli-integration-test/output、artifacts 等生成物。designs 只在没有任何项目外样本时作为最后回退；
    当前项目已有项目外样本，因此默认不会读取或打包 designs。

.PARAMETER OutputPath
    ZIP 输出路径。默认在 publish/live-photo-box-source.zip（每次自动覆盖）。

.PARAMETER OnlyIfChanged
    若指定，只有源文件比现有 ZIP 更新时才重新打包。

.PARAMETER ExcludeTests
    排除测试工程、测试夹具和测试样本。

.PARAMETER OpenFolder
    完成后是否在资源管理器中定位生成的 ZIP 文件。

.EXAMPLE
    .\scripts\package-project.ps1

.EXAMPLE
    .\scripts\package-project.ps1 -OnlyIfChanged
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$OutputPath = "",

    [Parameter()]
    [switch]$OnlyIfChanged,

    [Parameter()]
    [switch]$ExcludeTests,

    [Parameter()]
    [switch]$OpenFolder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
Push-Location $repoRoot

try {
    Write-Host "==================================================" -ForegroundColor Cyan
    Write-Host " Live Photo Box - 完整项目打包工具" -ForegroundColor Cyan
    Write-Host "==================================================" -ForegroundColor Cyan

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $publishDir = Join-Path $repoRoot "publish"
        if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
            New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
        }
        $OutputPath = Join-Path $publishDir "live-photo-box-source.zip"
    } else {
        $OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
        $parentDir = [System.IO.Path]::GetDirectoryName($OutputPath)
        if (-not (Test-Path -LiteralPath $parentDir -PathType Container)) {
            New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
        }
    }

    $repoRootFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd([char[]]@('\', '/'))
    $outputFull = [System.IO.Path]::GetFullPath($OutputPath)
    $outputRelativePath = $null
    if ($outputFull.StartsWith($repoRootFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        $outputRelativePath = $outputFull.Substring($repoRootFull.Length + 1).Replace('\', '/')
    }

    $filesToPack = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $stats = @{
        AiTemp = 0
        Designs = 0
        Generated = 0
        TestOutput = 0
        Artifacts = 0
        Tests = 0
        Missing = 0
        Duplicate = 0
    }

    function Get-NormalizedRelativePath([string]$Path) {
        if ([System.IO.Path]::IsPathRooted($Path)) {
            $full = [System.IO.Path]::GetFullPath($Path)
            $prefix = $repoRootFull + [System.IO.Path]::DirectorySeparatorChar
            if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "路径不在项目根目录内：$Path"
            }
            return $full.Substring($prefix.Length).Replace('\', '/')
        }
        return $Path.Replace('\', '/').TrimStart('/')
    }

    function Test-IsTestDataPath([string]$Path) {
        return $Path -match '^tests/fixtures/realsamples(/|$)'
    }

    function Get-ExclusionReason([string]$Path, [switch]$AllowDesigns) {
        if ($null -ne $outputRelativePath -and $Path -eq $outputRelativePath) {
            return 'Generated'
        }
        if ($Path -match '^\.ai-tmp(/|$)') { return 'AiTemp' }
        if (-not $AllowDesigns -and $Path -match '^designs(/|$)') { return 'Designs' }
        if ($Path -match '^(\.git|\.vs|graphify-out|\.codegraph|publish|LivePhotoBox/AppPackages)(/|$)' -or
            $Path -match '(^|/)(bin|obj|AppPackages|BundleArtifacts)(/|$)') {
            return 'Generated'
        }
        if ($Path -match '^cli-integration-test/output(/|$)') {
            return 'TestOutput'
        }
        if ($Path -match '^artifacts(/|$)') {
            return 'Artifacts'
        }
        if ($Path -match '(^|/)TestResults(/|$)') {
            return 'Generated'
        }
        if ($Path -match '^scripts/testing/__pycache__(/|$)' -or
            [System.IO.Path]::GetExtension($Path).ToLowerInvariant() -in @('.pyc', '.pdb', '.ilk', '.iobj', '.ipdb', '.binlog')) {
            return 'Generated'
        }
        $isTestDataPath = Test-IsTestDataPath $Path
        if ($ExcludeTests -and (($Path -match '^tests(/|$)') -or ($Path -match '^cli-integration-test(/|$)') -or $isTestDataPath)) {
            return 'Tests'
        }
        return $null
    }

    function Add-FileToPack([string]$RelativePath, [string]$FullPath, [switch]$AllowDesigns) {
        $normalized = Get-NormalizedRelativePath $RelativePath
        $reason = Get-ExclusionReason $normalized -AllowDesigns:$AllowDesigns
        if ($null -ne $reason) {
            $stats[$reason] = [int]$stats[$reason] + 1
            return
        }
        if (-not (Test-Path -LiteralPath $FullPath -PathType Leaf)) {
            $stats.Missing++
            return
        }
        if ($filesToPack.ContainsKey($normalized)) {
            $stats.Duplicate++
            return
        }
        $filesToPack[$normalized] = (Resolve-Path -LiteralPath $FullPath).Path
    }

    function Add-DirectoryToPack([string]$RelativeDirectory, [switch]$AllowDesigns) {
        $directoryPath = Join-Path $repoRoot ($RelativeDirectory.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
            $stats.Missing++
            return
        }
        Get-ChildItem -LiteralPath $directoryPath -Force -Recurse -File -ErrorAction Stop |
            ForEach-Object {
                Add-FileToPack (Get-NormalizedRelativePath $_.FullName) $_.FullName -AllowDesigns:$AllowDesigns
            }
    }

    Write-Host "`n[1/4] 正在扫描项目文件..." -ForegroundColor Yellow

    # Git 负责列出正常项目文件；被 .gitignore 忽略的 AI 和样本目录在下面显式加入。
    $gitFiles = @(& git -c core.quotepath=false ls-files -c -o --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $gitFiles.Count -eq 0) {
        throw "未能通过 git ls-files 获取到文件列表，请检查 Git 环境及项目根目录。"
    }
    foreach ($relPath in $gitFiles) {
        if (-not [string]::IsNullOrWhiteSpace($relPath)) {
            $normalized = Get-NormalizedRelativePath $relPath
            $fullPath = Join-Path $repoRoot ($normalized.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            Add-FileToPack $normalized $fullPath
        }
    }

    # 测试工程有一部分按本地验证用途被 .gitignore 忽略；显式扫描整个 tests，
    # 但仍由统一过滤器排除其中的 bin/obj/TestResults 等构建产物。
    Add-DirectoryToPack 'tests'

    # 项目内所有 AI 上下文：提示词、规则、skills、agents、任务状态和工具配置。
    $aiDirectories = @('.ai', '.agents', '.claude', '.codex', '.antigravity', '.gemini', '.cursor', '.windsurf')
    foreach ($directory in $aiDirectories) {
        Add-DirectoryToPack $directory
    }
    $aiFiles = @('AGENTS.md', 'CLAUDE.md', '.mcp.json', '.github/copilot-instructions.md')
    foreach ($file in $aiFiles) {
        $fullPath = Join-Path $repoRoot ($file.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        Add-FileToPack $file $fullPath
    }

    # 只保留 tests/fixtures/realsamples 中的原始测试媒体，避免打包重复副本。
    if (-not $ExcludeTests) {
        $sampleDirectories = @('tests/fixtures/realsamples')
        foreach ($directory in $sampleDirectories) {
            Add-DirectoryToPack $directory
        }
    }

    # 若以后删除了上述项目外样本，才允许使用 designs 作为最后回退；当前不触发。
    $hasExternalSamples = @($filesToPack.Keys | Where-Object { Test-IsTestDataPath $_ }).Count -gt 0
    if (-not $hasExternalSamples -and -not $ExcludeTests) {
        Add-DirectoryToPack 'designs' -AllowDesigns
    }

    if ($OnlyIfChanged -and (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        $zipTime = (Get-Item -LiteralPath $OutputPath).LastWriteTime
        $hasChanges = $false
        foreach ($sourceFile in $filesToPack.Values) {
            if ((Get-Item -LiteralPath $sourceFile).LastWriteTime -gt $zipTime) {
                $hasChanges = $true
                break
            }
        }
        if (-not $hasChanges) {
            Write-Host "项目文件无最新变更，无需重复打包。" -ForegroundColor Green
            return
        }
    }

    if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
        Remove-Item -LiteralPath $OutputPath -Force
    }

    Write-Host "[2/4] 已收集源代码、AI 上下文、测试工程和样本..." -ForegroundColor Yellow
    Write-Host "[3/4] 正在压缩打包 $($filesToPack.Count) 个文件..." -ForegroundColor Yellow

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($OutputPath, [System.IO.Compression.ZipArchiveMode]::Create)
    $totalBytes = 0L
    try {
        foreach ($kvp in $filesToPack.GetEnumerator()) {
            $entryPath = $kvp.Key
            $sourceFile = $kvp.Value
            $fileInfo = Get-Item -LiteralPath $sourceFile
            $totalBytes += $fileInfo.Length
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip,
                $sourceFile,
                $entryPath,
                [System.IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    } finally {
        $zip.Dispose()
    }

    $zipFileInfo = Get-Item -LiteralPath $OutputPath
    $zipSizeMB = [math]::Round($zipFileInfo.Length / 1MB, 2)
    $rawSizeMB = [math]::Round($totalBytes / 1MB, 2)

    Write-Host "`n[4/4] 打包完成！" -ForegroundColor Green
    Write-Host "--------------------------------------------------" -ForegroundColor Gray
    Write-Host "  ZIP 文件位置 : $OutputPath" -ForegroundColor Green
    Write-Host "  打包文件数量 : $($filesToPack.Count) 个"
    Write-Host "  原始文件体积 : $rawSizeMB MB"
    Write-Host "  压缩后体积   : $zipSizeMB MB ($([math]::Round($zipFileInfo.Length / 1KB, 1)) KB)" -ForegroundColor Cyan
    Write-Host "  排除 .ai-tmp : $($stats.AiTemp) 个文件" -ForegroundColor Gray
    Write-Host "  排除 designs  : $($stats.Designs) 个文件" -ForegroundColor Gray
    Write-Host "  排除构建产物 : $($stats.Generated) 个文件" -ForegroundColor Gray
    Write-Host "  排除测试输出 : $($stats.TestOutput) 个文件" -ForegroundColor Gray
    Write-Host "  排除 artifacts: $($stats.Artifacts) 个文件" -ForegroundColor Gray
    if ($ExcludeTests) {
        Write-Host "  排除测试内容 : $($stats.Tests) 个文件" -ForegroundColor Gray
    }
    Write-Host "--------------------------------------------------" -ForegroundColor Gray

    if ($OpenFolder) {
        & explorer.exe /select,$OutputPath
    }
} finally {
    Pop-Location
}
