# Shared build identity. No AI/task workspace or private sample participates.
function Get-NativeBuildFingerprint([string]$Root, [string]$Configuration, [string]$Variant, [string]$Toolchain) {
    $files = @((Join-Path $Root 'CMakeLists.txt'), (Join-Path $Root 'vcpkg.json'),
        (Join-Path $Root 'LivePhotoBox\Package.appxmanifest'))
    $files += @(Get-ChildItem (Join-Path $Root 'LivePhotoBox.Native\src'),(Join-Path $Root 'LivePhotoBox.Native\include'),
        (Join-Path $Root 'build\native') -Recurse -File |
        Where-Object { $_.Extension -in '.cpp','.c','.h','.hpp','.in','.cmake','.json','.ps1','.py','.patch','.diff' } |
        ForEach-Object FullName)
    $files += @('build-native.ps1','native-build-state.ps1','build-environment.ps1',
        'restore-source-archives.ps1','restore-video-base.ps1','restore-video-dependencies.ps1',
        'build-hevc-codecs.py','build-support.py','generate-p5-r5-video-sidecar-identity.ps1') |
        ForEach-Object { Join-Path $Root "scripts\native\$_" }
    if ($Variant -eq 'harness') { $files += @(Get-ChildItem (Join-Path $Root 'tests\native') -Recurse -File | ForEach-Object FullName) }
    $lines = @("root=$Root", "configuration=$Configuration", "variant=$Variant", "toolchain=$Toolchain")
    # PS5/.NET Framework and PS7/.NET use different culture sort tables. Cache
    # identity must use one ordinal order regardless of the invoking shell.
    $orderedFiles = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $files) { [void]$orderedFiles.Add([string]$file) }
    foreach ($file in $orderedFiles) {
        $relative = $file.Substring($Root.Length).TrimStart('\','/').Replace('\','/')
        $lines += "$relative=$((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash)"
    }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))).Replace('-','') }
    finally { $algorithm.Dispose() }
}

function Test-NativeBuildReceipt([string]$ArtifactDir, [string]$Fingerprint, [string]$Configuration, [string]$Variant) {
    $receipt = Join-Path $ArtifactDir 'native-build.json'
    if (-not (Test-Path -LiteralPath $receipt -PathType Leaf)) { return $false }
    $record = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($record.schemaVersion -ne 1 -or $record.fingerprint -ne $Fingerprint -or $record.configuration -ne $Configuration -or $record.variant -ne $Variant) { return $false }
    $name = if ($Variant -eq 'harness') { 'LivePhotoBox.Native.TestHarness' } else { 'LivePhotoBox.Native' }
    $expected = @("$name.dll", "$name.pdb")
    if ($Variant -eq 'production') { $expected += @('LivePhotoBox.Video.Libav.dll','LivePhotoBox.Video.Libav.pdb') }
    if (@($record.files).Count -ne $expected.Count -or
        (@($record.files.name | Sort-Object) -join '|') -ne (@($expected | Sort-Object) -join '|')) { return $false }
    foreach ($entry in $record.files) {
        $path = Join-Path $ArtifactDir $entry.name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path).Hash -ne $entry.sha256) { return $false }
    }
    return $true
}
