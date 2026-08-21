# update-project-overview.ps1
#
# Auto-update the numeric parts of docs/项目总览.md (project overview):
#   1. Recompute the "项目统计" (project stats) numbers from real file counts.
#   2. Update the "最后更新" (last updated) date at the bottom.
#
# Usage (run after creating/moving/deleting files):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/update-project-overview.ps1
#
# NOTE: The directory trees in section 4 are hand-maintained (they carry
# Chinese annotations and logical ordering). After structural changes, an
# agent must ALSO manually update the tree sections, not only run this script.
#
# IMPORTANT: Keep this file ASCII-only (no Chinese outside the strings that
# must match the overview) so Windows PowerShell 5.1 parses it correctly
# regardless of console code page. The overview file itself is UTF-8.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$overview = Join-Path $repoRoot "docs\项目总览.md"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path $overview)) { throw "Overview not found: $overview" }

# ---- 1. Compute stats -------------------------------------------------------
function Count-Cs([string]$dir) {
    if (-not (Test-Path $dir)) { return 0 }
    (Get-ChildItem $dir -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }).Count
}
function Count-Xaml([string]$dir) {
    if (-not (Test-Path $dir)) { return 0 }
    (Get-ChildItem $dir -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }).Count
}
function Count-Files([string]$dir) {
    if (-not (Test-Path $dir)) { return 0 }
    (Get-ChildItem $dir -Filter *.cs).Count
}

$coreCs     = Count-Cs (Join-Path $repoRoot "LivePhotoBox.Core")
$guiCs      = Count-Cs (Join-Path $repoRoot "LivePhotoBox")
$cliCs      = Count-Cs (Join-Path $repoRoot "LivePhotoBox.CLI")
$totalCs    = $coreCs + $guiCs + $cliCs
$xaml       = Count-Xaml (Join-Path $repoRoot "LivePhotoBox")
$viewModels = Count-Files (Join-Path $repoRoot "LivePhotoBox\ViewModels")
$views      = Count-Files (Join-Path $repoRoot "LivePhotoBox\Views")
$controls   = (Count-Files (Join-Path $repoRoot "LivePhotoBox\Controls")) + 1
$converters = Count-Files (Join-Path $repoRoot "LivePhotoBox\Converters")
$helpers    = Count-Files (Join-Path $repoRoot "LivePhotoBox\Helpers")
$guiSvc     = Count-Files (Join-Path $repoRoot "LivePhotoBox\Services")
$guiModels  = Count-Files (Join-Path $repoRoot "LivePhotoBox\Models")
$coreSvc    = Count-Files (Join-Path $repoRoot "LivePhotoBox.Core\Services")
$coreModels = Count-Files (Join-Path $repoRoot "LivePhotoBox.Core\Models")
$protocols  = Count-Files (Join-Path $repoRoot "LivePhotoBox.Core\Services\Protocols")

$today = Get-Date -Format "yyyy-MM-dd"

# ---- 2. Read original text and apply replacements ---------------------------
$text = [System.IO.File]::ReadAllText($overview)
$orig = $text

# 2a. Replace stat numbers in the stats table (section 19).
$stats = @{
    "C# 源文件 \(\.cs\)"        = "~$totalCs（Core $coreCs + GUI $guiCs + CLI $cliCs）"
    "XAML 页面/控件 \(\.xaml\)" = "$xaml"
    "ViewModels"                = "$viewModels"
    "Views \(页面\)"            = "$views"
    "Controls \(自定义控件\)"   = "$controls"
    "Converters"                = "$converters"
    "Helpers"                   = "~$($helpers + 1)（GUI $helpers + Core 1）"
    "Services"                  = "~$($coreSvc + $guiSvc)（Core $coreSvc + GUI $guiSvc）"
    "Models"                    = "~$($guiModels + $coreModels)（GUI $guiModels + Core $coreModels）"
    "Protocols"                 = "$protocols 个 .cs（1 抽象基类 + 7 注册实现 + Apple/HDR 辅助）"
    "RESW 资源文件"             = "2 (zh-Hans + en-US)"
}
foreach ($k in $stats.Keys) {
    $esc = [regex]::Escape($k)
    $text = [regex]::Replace($text, "(?m)^(\|\s*$esc\s*\|\s*)[^|]+(\s*\|)$", "`${1}$($stats[$k])`${2}")
}

# 2b. Update the last-updated date.
$text = [regex]::Replace($text, "(?m)^(> 最后更新: ).*$", "`${1}$today")

if ($text -eq $orig) {
    Write-Host "Overview is already up to date (no changes)."
} else {
    [System.IO.File]::WriteAllText($overview, $text, $utf8NoBom)
    Write-Host "Updated docs/项目总览.md (stats + date)."
}
