#Requires -Version 5.1
<#
.SYNOPSIS
    将 Live Photo Box 项目的纯源代码、核心配置、协议规范与设计文档打包为轻量 ZIP 压缩包。

.DESCRIPTION
    本脚本用于项目纯源码与文档的快速打包：
    - 默认打包在 publish 文件夹下的 live-photo-box-source.zip，每次运行自动覆盖上一次生成的压缩包。
    - 自动排除 git 历史、所有 bin/obj 编译缓存、本地临时文件、测试机样本（超大媒体）以及 gitignore 内容。
    - 支持 -OnlyIfChanged 参数，仅在项目源码文件发生实际更改后才触发重新打包。
    - 仅在本项目内生效，不污染全局任何环境。

.PARAMETER OutputPath
    ZIP 输出路径。默认在 publish/live-photo-box-source.zip（每次自动覆盖）。

.PARAMETER OnlyIfChanged
    若指定，脚本会检查项目代码是否有比现有 ZIP 更晚修改的文件；若无变动则直接跳过打包。

.PARAMETER IncludeAssets
    是否包含图标、Banner、教程长图及截图等二进制图片（默认不包含，以保持纯净和最小体积）。

.PARAMETER ExcludeTests
    是否排除单元测试工程源码（默认包含测试代码）。

.PARAMETER OpenFolder
    完成后是否在资源管理器中定位生成的 ZIP 文件。

.EXAMPLE
    .\scripts\package-project.ps1
    默认打包纯代码与核心文档，输出并覆盖 publish/live-photo-box-source.zip。

.EXAMPLE
    .\scripts\package-project.ps1 -OnlyIfChanged
    仅在项目源码有实际修改时才执行打包。
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$OutputPath = "",

    [Parameter()]
    [switch]$OnlyIfChanged,

    [Parameter()]
    [switch]$IncludeAssets,

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
    Write-Host " Live Photo Box - 项目源码打包工具" -ForegroundColor Cyan
    Write-Host "==================================================" -ForegroundColor Cyan

    # 1. 确定输出路径（默认 publish 目录下的固定文件名，每次直接覆盖）
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $publishDir = Join-Path $repoRoot "publish"
        if (-not (Test-Path $publishDir)) {
            New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
        }
        $OutputPath = Join-Path $publishDir "live-photo-box-source.zip"
    } else {
        $OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
        $parentDir = [System.IO.Path]::GetDirectoryName($OutputPath)
        if (-not (Test-Path $parentDir)) {
            New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
        }
    }

    Write-Host "`n[1/4] 正在扫描项目文件..." -ForegroundColor Yellow

    # 2. 获取基于 Git 跟踪与非忽略的文件清单
    $gitCmd = "git -c core.quotepath=false ls-files -c -o --exclude-standard"
    $gitFiles = Invoke-Expression $gitCmd
    if (-not $gitFiles -or $gitFiles.Count -eq 0) {
        throw "未能通过 git ls-files 获取到文件列表，请检查 Git 环境及项目根目录。"
    }

    # 3. 定义过滤规则
    $binaryExts = @(
        '.png', '.jpg', '.jpeg', '.ico', '.gif', '.bmp', '.webp', '.tiff', '.tif',
        '.exe', '.dll', '.pdb', '.lib', '.obj', '.exp', '.ilk',
        '.zip', '.7z', '.tar', '.gz', '.rar',
        '.mp4', '.mov', '.heic', '.heif'
    )

    $filesToPack = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $skippedBinaries = 0
    $skippedTests = 0
    $skippedInsights = 0
    $skippedScreenshots = 0

    foreach ($relPath in $gitFiles) {
        if ([string]::IsNullOrWhiteSpace($relPath)) { continue }

        # 统一路径分隔符
        $normPath = $relPath.Replace('\', '/')

        # 过滤 GitHub 流量统计历史
        if ($normPath -match '^insights/') {
            $skippedInsights++
            continue
        }

        # 过滤 screenshots 截图目录（纯代码包不需要）
        if (-not $IncludeAssets -and ($normPath -match '^screenshots/')) {
            $skippedScreenshots++
            continue
        }

        # 过滤测试工程（如果指定）
        if ($ExcludeTests -and ($normPath -match '^tests/')) {
            $skippedTests++
            continue
        }

        # 过滤二进制文件
        $ext = [System.IO.Path]::GetExtension($normPath).ToLowerInvariant()
        if (-not $IncludeAssets -and ($binaryExts -contains $ext)) {
            $skippedBinaries++
            continue
        }

        $fullPath = Join-Path $repoRoot ($normPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (Test-Path $fullPath -PathType Leaf) {
            $filesToPack[$normPath] = $fullPath
        }
    }

    # 4. 补充核心设计/架构/规约与协议分析文档
    $specialDocs = @(
        'docs/实况照片协议完整分析报告.md',
        'AGENTS.md',
        '.ai/project-context.md'
    )

    foreach ($sDoc in $specialDocs) {
        $fullDocPath = Join-Path $repoRoot ($sDoc.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (Test-Path $fullDocPath -PathType Leaf) {
            $filesToPack[$sDoc] = $fullDocPath
        }
    }

    # 补充 .agents/rules/ 规范
    $agentRulesDir = Join-Path $repoRoot ".agents\rules"
    if (Test-Path $agentRulesDir) {
        Get-ChildItem $agentRulesDir -Filter *.md -File -Recurse | ForEach-Object {
            $rel = $_.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
            $filesToPack[$rel] = $_.FullName
        }
    }

    # 检查 -OnlyIfChanged 变动状态
    if ($OnlyIfChanged -and (Test-Path $OutputPath)) {
        $zipTime = (Get-Item $OutputPath).LastWriteTime
        $hasChanges = $false
        foreach ($sourceFile in $filesToPack.Values) {
            if ((Get-Item $sourceFile).LastWriteTime -gt $zipTime) {
                $hasChanges = $true
                break
            }
        }
        if (-not $hasChanges) {
            Write-Host "项目代码无最新变更，无需重复打包。" -ForegroundColor Green
            return
        }
    }

    # 如果存在上一次生成的压缩包，准备覆盖
    if (Test-Path $OutputPath) {
        Remove-Item $OutputPath -Force
    }

    Write-Host "[2/4] 已收集项目设计、架构与协议分析上下文..." -ForegroundColor Yellow
    Write-Host "[3/4] 正在压缩打包 $($filesToPack.Count) 个文件..." -ForegroundColor Yellow

    # 5. 使用 .NET 原生压缩生成 ZIP
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $zip = [System.IO.Compression.ZipFile]::Open($OutputPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $totalBytes = 0
        foreach ($kvp in $filesToPack.GetEnumerator()) {
            $entryPath = $kvp.Key
            $sourceFile = $kvp.Value
            $fileInfo = Get-Item $sourceFile
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

    $zipFileInfo = Get-Item $OutputPath
    $zipSizeMB = [math]::Round($zipFileInfo.Length / 1MB, 2)
    $rawSizeMB = [math]::Round($totalBytes / 1MB, 2)

    Write-Host "`n[4/4] 打包完成！" -ForegroundColor Green
    Write-Host "--------------------------------------------------" -ForegroundColor Gray
    Write-Host "  ZIP 文件位置 : $OutputPath" -ForegroundColor Green
    Write-Host "  打包文件数量 : $($filesToPack.Count) 个"
    Write-Host "  原始纯代码量 : $rawSizeMB MB"
    Write-Host "  压缩后体积   : $zipSizeMB MB ($([math]::Round($zipFileInfo.Length / 1KB, 1)) KB)" -ForegroundColor Cyan
    Write-Host "  过滤媒体资源 : $($skippedBinaries + $skippedScreenshots) 个文件 (可用 -IncludeAssets 包含)" -ForegroundColor Gray
    if ($ExcludeTests) {
        Write-Host "  跳过测试工程 : $skippedTests 个测试文件" -ForegroundColor Gray
    }
    Write-Host "--------------------------------------------------" -ForegroundColor Gray

    if ($OpenFolder) {
        & explorer.exe /select,$OutputPath
    }

} finally {
    Pop-Location
}