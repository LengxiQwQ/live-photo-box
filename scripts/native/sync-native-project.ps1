# sync-native-project.ps1
#
# Automatically synchronizes disk files in LivePhotoBox.Native (include/ and src/)
# with:
#   1. LivePhotoBox.Native/LivePhotoBox.Native.vcxproj (.h and .cpp items)
#   2. LivePhotoBox.Native/LivePhotoBox.Native.vcxproj.filters (hierarchical filters matching disk)
#   3. LivePhotoBox.Core/LivePhotoBox.Core.csproj (BuildLivePhotoBoxNative target Inputs)
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native/sync-native-project.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native/sync-native-project.ps1 -Check

param(
    [switch]$Check,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$nativeProjDir  = Join-Path $repoRoot 'LivePhotoBox.Native'
$nativeVcxproj  = Join-Path $nativeProjDir 'LivePhotoBox.Native.vcxproj'
$nativeFilters  = Join-Path $nativeProjDir 'LivePhotoBox.Native.vcxproj.filters'
$coreCsprojPath = Join-Path $repoRoot 'LivePhotoBox.Core\LivePhotoBox.Core.csproj'

if (-not (Test-Path $nativeVcxproj))  { throw "vcxproj not found: $nativeVcxproj" }
if (-not (Test-Path $coreCsprojPath)) { throw "csproj not found: $coreCsprojPath" }

# ---- 1. Discover all native files from disk ---------------------------------
$includeDir = Join-Path $nativeProjDir 'include'
$srcDir     = Join-Path $nativeProjDir 'src'

$headerExts = @('.h', '.hpp', '.hxx', '.inl')
$sourceExts = @('.cpp', '.c', '.cc', '.cxx')

$allFiles = @()
if (Test-Path $includeDir) {
    $allFiles += Get-ChildItem -Path $includeDir -Recurse -File | Where-Object { $_.Extension -in $headerExts -or $_.Extension -in $sourceExts }
}
if (Test-Path $srcDir) {
    $allFiles += Get-ChildItem -Path $srcDir -Recurse -File | Where-Object { $_.Extension -in $headerExts -or $_.Extension -in $sourceExts }
}

# Normalize relative paths (e.g. include\livephotobox_native.h, src\binary\binary_io.h)
$headers = @()
$sources = @()

foreach ($file in $allFiles) {
    $rel = $file.FullName.Substring($nativeProjDir.Length).TrimStart('\', '/')
    $rel = $rel.Replace('/', '\')
    if ($file.Extension -in $headerExts) {
        $headers += $rel
    } else {
        $sources += $rel
    }
}

$headers = @($headers | Sort-Object -CaseSensitive:$false)
$sources = @($sources | Sort-Object -CaseSensitive:$false)

if (-not $Quiet) {
    Write-Host "[Sync-Native] Found $($headers.Count) header(s) and $($sources.Count) source file(s) on disk." -ForegroundColor Cyan
}

# ---- 2. Compute directory filters and deterministic GUIDs -------------------
function Get-DeterministicGuid([string]$name) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($name.ToLowerInvariant()))
    $guid = [System.Guid]::new($hash)
    return "{$($guid.ToString().ToUpperInvariant())}"
}

$allRelPaths = $headers + $sources
$filterSet = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($rel in $allRelPaths) {
    $dir = [System.IO.Path]::GetDirectoryName($rel)
    while (-not [string]::IsNullOrEmpty($dir)) {
        [void]$filterSet.Add($dir)
        $dir = [System.IO.Path]::GetDirectoryName($dir)
    }
}

# ---- 3. Build new .vcxproj.filters content ----------------------------------
$filtersXml = [System.Text.StringBuilder]::new()
[void]$filtersXml.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$filtersXml.AppendLine('<Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">')
[void]$filtersXml.AppendLine('  <ItemGroup>')

foreach ($filter in $filterSet) {
    $guid = Get-DeterministicGuid $filter
    [void]$filtersXml.AppendLine("    <Filter Include=""$filter"">")
    [void]$filtersXml.AppendLine("      <UniqueIdentifier>$guid</UniqueIdentifier>")
    [void]$filtersXml.AppendLine('    </Filter>')
}

[void]$filtersXml.AppendLine('  </ItemGroup>')
[void]$filtersXml.AppendLine('  <ItemGroup>')

foreach ($h in $headers) {
    $dir = [System.IO.Path]::GetDirectoryName($h)
    [void]$filtersXml.AppendLine("    <ClInclude Include=""$h"">")
    if (-not [string]::IsNullOrEmpty($dir)) {
        [void]$filtersXml.AppendLine("      <Filter>$dir</Filter>")
    }
    [void]$filtersXml.AppendLine('    </ClInclude>')
}

[void]$filtersXml.AppendLine('  </ItemGroup>')
[void]$filtersXml.AppendLine('  <ItemGroup>')

foreach ($s in $sources) {
    $dir = [System.IO.Path]::GetDirectoryName($s)
    [void]$filtersXml.AppendLine("    <ClCompile Include=""$s"">")
    if (-not [string]::IsNullOrEmpty($dir)) {
        [void]$filtersXml.AppendLine("      <Filter>$dir</Filter>")
    }
    [void]$filtersXml.AppendLine('    </ClCompile>')
}

[void]$filtersXml.AppendLine('  </ItemGroup>')
[void]$filtersXml.AppendLine('</Project>')
$newFiltersContent = $filtersXml.ToString()

# ---- 4. Update .vcxproj items -----------------------------------------------
$vcxprojText = [System.IO.File]::ReadAllText($nativeVcxproj, [System.Text.Encoding]::UTF8)

$headerItemsXml = [System.Text.StringBuilder]::new()
[void]$headerItemsXml.AppendLine('  <ItemGroup>')
foreach ($h in $headers) {
    [void]$headerItemsXml.AppendLine("    <ClInclude Include=""$h"" />")
}
[void]$headerItemsXml.Append('  </ItemGroup>')

$sourceItemsXml = [System.Text.StringBuilder]::new()
[void]$sourceItemsXml.AppendLine('  <ItemGroup>')
foreach ($s in $sources) {
    [void]$sourceItemsXml.AppendLine("    <ClCompile Include=""$s"" />")
}
[void]$sourceItemsXml.Append('  </ItemGroup>')

# Replace ItemGroups for ClInclude and ClCompile
$vcxprojPattern = '(?s)  <ItemGroup>\s*<ClInclude Include="[^"]+" />.*?  </ItemGroup>\s*<ItemGroup>\s*<ClCompile Include="[^"]+" />.*?  </ItemGroup>'
$replacement = "$($headerItemsXml.ToString())`r`n$($sourceItemsXml.ToString())"
$newVcxprojText = [System.Text.RegularExpressions.Regex]::Replace($vcxprojText, $vcxprojPattern, $replacement)

# ---- 5. Update Core.csproj BuildLivePhotoBoxNative Inputs --------------------
$coreCsprojText = [System.IO.File]::ReadAllText($coreCsprojPath, [System.Text.Encoding]::UTF8)

$nativeInputs = @(
    '$(MSBuildThisFileDirectory)..\LivePhotoBox.Native\LivePhotoBox.Native.vcxproj',
    '$(MSBuildThisFileDirectory)..\LivePhotoBox.Native\LivePhotoBox.Native.vcxproj.filters'
)
foreach ($h in $headers) {
    $nativeInputs += "`$(MSBuildThisFileDirectory)..\LivePhotoBox.Native\$h"
}
foreach ($s in $sources) {
    $nativeInputs += "`$(MSBuildThisFileDirectory)..\LivePhotoBox.Native\$s"
}
$nativeInputs += '$(MSBuildThisFileDirectory)..\LivePhotoBox\Package.appxmanifest'
$nativeInputs += '$(MSBuildThisFileDirectory)..\scripts\native\build-native.ps1'

$joinedInputs = $nativeInputs -join ';'
$corePattern = '(?<=Inputs=")[^"]*(?=")'
$newCoreCsprojText = [System.Text.RegularExpressions.Regex]::Replace($coreCsprojText, $corePattern, $joinedInputs)

# ---- 6. Check / Write results -----------------------------------------------
$filtersChanged = $true
if (Test-Path $nativeFilters) {
    $oldFiltersText = [System.IO.File]::ReadAllText($nativeFilters, [System.Text.Encoding]::UTF8)
    if ($oldFiltersText.Trim() -eq $newFiltersContent.Trim()) {
        $filtersChanged = $false
    }
}

$vcxprojChanged = ($vcxprojText.Trim() -ne $newVcxprojText.Trim())
$coreChanged    = ($coreCsprojText.Trim() -ne $newCoreCsprojText.Trim())

if ($Check) {
    if ($filtersChanged -or $vcxprojChanged -or $coreChanged) {
        Write-Error "[Sync-Native] Project files are out of sync with disk! (vcxproj: $vcxprojChanged, filters: $filtersChanged, core csproj: $coreChanged)"
        exit 1
    } else {
        if (-not $Quiet) {
            Write-Host "[Sync-Native] All project files and filters are 100% in sync with disk." -ForegroundColor Green
        }
        exit 0
    }
}

$updatedAny = $false
if ($filtersChanged) {
    [System.IO.File]::WriteAllText($nativeFilters, $newFiltersContent, [System.Text.Encoding]::UTF8)
    Write-Host "[Sync-Native] Updated LivePhotoBox.Native.vcxproj.filters" -ForegroundColor Green
    $updatedAny = $true
}
if ($vcxprojChanged) {
    [System.IO.File]::WriteAllText($nativeVcxproj, $newVcxprojText, [System.Text.Encoding]::UTF8)
    Write-Host "[Sync-Native] Updated LivePhotoBox.Native.vcxproj" -ForegroundColor Green
    $updatedAny = $true
}
if ($coreChanged) {
    [System.IO.File]::WriteAllText($coreCsprojPath, $newCoreCsprojText, [System.Text.Encoding]::UTF8)
    Write-Host "[Sync-Native] Updated LivePhotoBox.Core.csproj (Native Inputs)" -ForegroundColor Green
    $updatedAny = $true
}

if (-not $updatedAny -and -not $Quiet) {
    Write-Host "[Sync-Native] Everything is already up to date." -ForegroundColor Gray
}