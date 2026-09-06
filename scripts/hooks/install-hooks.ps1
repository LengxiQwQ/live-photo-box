#Requires -Version 5.1
<#
.SYNOPSIS
    安装项目的 Git 钩子 (post-commit)，使代码提交后自动触发 vibe-notify 通知与源码打包。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$gitHooksDir = Join-Path $repoRoot ".git\hooks"
$sourceHook = Join-Path $PSScriptRoot "post-commit"

if (-not (Test-Path $gitHooksDir)) {
    throw "未找到 .git/hooks 目录，请确认此目录处于 Git 仓库根中。"
}

$targetHook = Join-Path $gitHooksDir "post-commit"
Copy-Item $sourceHook $targetHook -Force
Write-Host "成功安装 Git post-commit 钩子 -> $targetHook" -ForegroundColor Green
